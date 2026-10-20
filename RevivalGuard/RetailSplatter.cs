using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BLOOD SPLATTER SIZED BY THE HIT, AS RETAIL (owner 2026-10-20 "Size by hit"; audit effects-sound.md #5;
    /// docs/findings/splatter-intensity-2026-10-20.md). ACE sends every Splatter* PlayEffect (0xF755) at speed 1.0
    /// (GameMessageScript's default). Retail's speed, fitted on the 31 January-2017 captures by pairing each splatter with
    /// the damage event of the same hit:
    ///     speed = min(1, damage / maxHealth + damage / healthAfterTheHit)
    /// Own hits taken (602 paired): exactly 2 x damage/max at full health, rising as health falls, and the health it
    /// implies drops by each next hit's damage (150 rapid pairs, median error -0.4 HP). Hits dealt (224 with a fresh
    /// UpdateHealth): 79% within 3%. A blow that leaves no health is 1.0.
    ///
    /// How: a prefix on the two TakeDamage methods that precede ACE's splatters (Creature's, for a player or pet hitting
    /// a creature; Player's, for a creature hitting a player, which sends the splatter inside it) notes the target's
    /// health before; in the three splatter sites (Player.DamageTarget, Player.TakeDamage, Creature.EmitSplatter) the
    /// PlayEffect is built by Make, which for a Splatter script at 1.0 for that guid within 0.1 s reads the health now
    /// and sets the speed. Anything unmatched keeps ACE's 1.0.
    ///   @modifybool retail_splatter_size false   ACE's 1.0 for every splatter
    /// </summary>
    static class RetailSplatter
    {
        const string P_ON = "retail_splatter_size";
        static int s_Registered;
        static bool On => PropertyManager.GetBool(P_ON, true).Item;

        [ThreadStatic] static Dictionary<uint, (Creature c, int before, uint max, long tick)> t_Before;

        static void Note(Creature c)
        {
            try
            {
                if (c?.Health == null || !On) return;
                if (t_Before != null && t_Before.Count > 256) t_Before.Clear();   // hits with no splatter (spells, DoTs) never get read
                (t_Before ??= new Dictionary<uint, (Creature, int, uint, long)>())[c.Guid.Full] = (c, (int)c.Health.Current, c.Health.MaxValue, Environment.TickCount64);
            }
            catch { }
        }

        /// <summary>Retail's splatter speed for a hit of <paramref name="damage"/> leaving <paramref name="after"/> of <paramref name="max"/>.</summary>
        internal static float Speed(int damage, int after, uint max)
        {
            if (after <= 0 || max == 0) return 1f;
            return (float)Math.Min(1.0, (double)damage / max + (double)damage / after);
        }

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) != 0) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new ACE.Server.Managers.Property<bool>(true, "RevivalGuard RetailSplatter: blood splatter sized by the hit, as retail (damage/max + damage/health left); off = ACE's 1.0");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailSplatter: {P_ON} not registered with @modifybool ({e.Message}); it reads its default (on)"); }
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(bool) })]
        static class CreatureHit
        {
            static bool Prepare() { Register(); return true; }
            static void Prefix(Creature __instance) => Note(__instance);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(BodyPart), typeof(bool), typeof(AttackConditions) })]
        static class PlayerHit
        {
            static void Prefix(Player __instance) => Note(__instance);
        }

        /// <summary>Every "new GameMessageScript(guid, script, speed)" in ACE's three splatter sites becomes Make(guid,
        /// script, speed). A prefix on the constructor itself had no effect on the box (likely inlined at the call sites).</summary>
        [HarmonyPatch]
        static class Size
        {
            static readonly ConstructorInfo CTOR = AccessTools.Constructor(typeof(GameMessageScript), new[] { typeof(ObjectGuid), typeof(PlayScript), typeof(float) });

            static IEnumerable<MethodBase> TargetMethods()
            {
                var sites = new[]
                {
                    AccessTools.DeclaredMethod(typeof(Player), "DamageTarget", new[] { typeof(Creature), typeof(WorldObject) }),
                    AccessTools.DeclaredMethod(typeof(Player), nameof(Player.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(BodyPart), typeof(bool), typeof(AttackConditions) }),
                    AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.EmitSplatter), new[] { typeof(Creature), typeof(float) }),
                };
                foreach (var m in sites)
                    if (m != null) yield return m;
                    else Mod.Log.Error("[RevivalGuard] RetailSplatter: a splatter site was not found; its splatters keep ACE's 1.0");
            }

            static bool Prepare(MethodBase original)
            {
                if (original == null) Mod.Log.Info($"[RevivalGuard] RetailSplatter: splatter sized by the hit ({P_ON}, default on)");
                return CTOR != null;
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                var code = instructions.ToList();
                int n = 0;
                var make = AccessTools.Method(typeof(RetailSplatter), nameof(Make));
                foreach (var c in code)
                    if (c.opcode == System.Reflection.Emit.OpCodes.Newobj && Equals(c.operand, CTOR)) { c.opcode = System.Reflection.Emit.OpCodes.Call; c.operand = make; n++; }
                if (n == 0) Mod.Log.Error($"[RevivalGuard] RetailSplatter: no GameMessageScript in {original?.DeclaringType?.Name}.{original?.Name}; its splatters keep 1.0");
                return code;
            }
        }

        /// <summary>The PlayEffect, with retail's speed when it is a splatter for a hit just noted.</summary>
        public static GameMessageScript Make(ObjectGuid guid, PlayScript scriptId, float speed)
        {
            try
            {
                if (scriptId >= PlayScript.SplatterLowLeftBack && scriptId <= PlayScript.SplatterUpRightFront && speed == 1f
                    && t_Before != null && t_Before.TryGetValue(guid.Full, out var b))
                {
                    t_Before.Remove(guid.Full);
                    if (Environment.TickCount64 - b.tick <= 100 && On && b.c?.Health != null)
                    {
                        int after = (int)b.c.Health.Current;
                        int damage = b.before - after;
                        if (damage > 0) speed = Speed(damage, after, b.max);
                    }
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailSplatter: {e.Message}; this splatter keeps 1.0"); }
            return new GameMessageScript(guid, scriptId, speed);
        }
    }
}
