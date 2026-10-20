using System.Numerics;
using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// JOKE SPELLS (owner 2026-10-20 "unlock existing jokes"; ours, docs/DIVERGENCES.md). Retail's joke spells as scrolls
    /// (tools/gpubox-ace/gen_joke_spells.py); nobody sells them (Tumble removed, owner 2026-10-20) and the festival
    /// delivery waits for his approval (JokeScrollRewards, off). They keep their ORIGINAL ids, which every
    /// client already has in its DAT, so this rule makes them jokes only when a PLAYER casts them:
    ///   Cow 4267, Present 4269, Table 4270 (the Jester's gag bolts, 100-200) and Bovine Intervention 2366 (a cow from
    ///     the sky, 100-200): damage x SCALE (16-32, a Bolt I). The Jester and any monster keep theirs.
    ///   Fireworks 2815-2926, 4375-4390: harmless (a player's firework bursts on contact and does nothing else); cast at
    ///     no target they fly at the spell's launch angle (ACE reads DefaultLaunchAngle nowhere; UP = 90, OUT = 45)
    ///     instead of level, and burst after FUSE_S in the air (an untargeted streak otherwise flies on and never bursts).
    ///     Anyone's firework bursts in its own colour (ACE's data made every one grey; see Colour).
    ///   Castable by anyone (coordinator 2026-10-20 "fun for anyone"): a player casts every joke id (the above and
    ///     Old School Fireworks 2927; NOT Egg 4078: the Egg Launcher is a holiday item, owner 2026-10-20) at difficulty POWER instead of the DAT's Power (Cow 300), trained or not, and
    ///     needs and burns no components, for at most MANA mana. The client shows the same (Assets/Streaming/AcJokeSpells.cs; keep the ids equal).
    ///   @modifybool rg_joke_spells false   ACE's own behaviour for all of the above
    /// </summary>
    static class JokeSpells
    {
        internal const string P_ON = "rg_joke_spells";
        const float SCALE = 0.16f;
        const double FUSE_S = 1.6;
        internal const int POWER = 1;   // AcJokeSpells.POWER on the client
        internal const uint MANA = 10;  // at most, a Bolt I's (Cow is 35 in the DAT); AcJokeSpells.MANA
        static readonly HashSet<uint> s_Scaled = new() { 4267, 4269, 4270, 2366 };
        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;

        internal static bool IsFirework(uint id) => (id >= 2815 && id <= 2926) || (id >= 4375 && id <= 4390);
        internal static bool IsJoke(uint id) => s_Scaled.Contains(id) || id == 2927 || IsFirework(id);
        static bool PlayerJoke(Spell spell, WorldObject caster) => spell != null && caster is Player && IsJoke(spell.Id) && On;
        static bool PlayerFirework(Spell spell, WorldObject source) => spell != null && source is Player && IsFirework(spell.Id) && On;

        static bool s_Registered;
        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard JokeSpells: player-cast Cow/Present/Table/Bovine Intervention do x0.16 damage; player fireworks are harmless, aim up and burst");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: switch not registered with @modifybool ({e.Message}); still read at its default"); }
            Mod.Log.Info($"[RevivalGuard] JokeSpells: player joke bolts x{SCALE}, harmless fireworks ({P_ON})");
        }

        static MethodBase Find(Type t, string name, Type[] args)
        {
            var m = AccessTools.DeclaredMethod(t, name, args);
            if (m == null) Mod.Log.Error($"[RevivalGuard] JokeSpells: {t.Name}.{name} not found; that part is off");
            return m;
        }

        [HarmonyPatch]
        static class Damage
        {
            static MethodBase TargetMethod() => Find(typeof(SpellProjectile), nameof(SpellProjectile.CalculateDamage),
                new[] { typeof(WorldObject), typeof(Creature), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType() });
            static bool Prepare(MethodBase original) { if (original == null) Register(); return original != null || TargetMethod() != null; }
            static void Postfix(SpellProjectile __instance, WorldObject source, ref float? __result)
            {
                try
                {
                    if (!__result.HasValue || !(source is Player) || __instance.Spell == null) return;
                    if (s_Scaled.Contains(__instance.Spell.Id) && On) __result = __result.Value * SCALE;
                    else if (IsFirework(__instance.Spell.Id) && On) __result = 0f;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: damage: {e.Message}"); }
            }
        }

        // ACE's roll (GetCastingPreCheckStatus) at difficulty POWER; the war/void lockout is skipped for a joke.
        [HarmonyPatch]
        static class EasyCast
        {
            static MethodBase TargetMethod() => Find(typeof(Player), nameof(Player.GetCastingPreCheckStatus), new[] { typeof(Spell), typeof(uint), typeof(bool) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static bool Prefix(Player __instance, Spell spell, uint magicSkill, ref Player.CastingPreCheckStatus __result)
            {
                try
                {
                    if (!PlayerJoke(spell, __instance)) return true;
                    double chance = magicSkill > 0 ? SkillCheck.GetMagicSkillChance((int)magicSkill, POWER) : 0.0;
                    __result = chance > ThreadSafeRandom.Next(0.0f, 1.0f) ? Player.CastingPreCheckStatus.Success : Player.CastingPreCheckStatus.CastFailed;
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: cast: {e.Message}"); return true; }
            }
        }

        // ACE writes a scroll's LongDesc from the DAT ("Inscribed spell: Cow\nShoots a Cow ... 100-200"); a joke scroll
        // tells the player's numbers instead. Same words as the client's AcJokeSpells.
        static readonly Dictionary<uint, string> s_Desc = new()
        {
            { 4267, "Shoots a cow at your target. In a student's hands the cow does 16-32 points of bludgeoning damage, most of it to the target's pride." },
            { 4269, "Shoots a wrapped present at your target. In a student's hands it does 16-32 points of slashing damage. It is the thought that counts." },
            { 4270, "Shoots a table at your target. In a student's hands it does 16-32 points of piercing damage. Mind the corners." },
            { 2366, "When Cow gods intervene: a cow falls out of the sky onto your target and does 16-32 points of bludgeoning damage." },
            { 2927, "The original anniversary fireworks burst around you, and the cheer of it restores 8-15 points of health." },
        };

        [HarmonyPatch]
        static class ScrollText
        {
            static MethodBase TargetMethod() => Find(typeof(Scroll), "SetEphemeralValues", Type.EmptyTypes);
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(Scroll __instance)
            {
                try
                {
                    var sp = __instance.Spell;
                    if (sp == null || !IsJoke(sp.Id) || !On) return;
                    string d = s_Desc.TryGetValue(sp.Id, out var t) ? t : IsFirework(sp.Id) ? sp.Description?.Trim() + " It harms no one." : null;
                    if (d != null) __instance.LongDesc = $"Inscribed spell: {sp.Name}\n{d}";
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: scroll text: {e.Message}"); }
            }
        }

        [HarmonyPatch]
        static class CheapMana
        {
            static MethodBase TargetMethod() => Find(typeof(Creature), nameof(Creature.CalculateManaUsage), new[] { typeof(Creature), typeof(Spell), typeof(WorldObject) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(Creature caster, Spell spell, ref uint __result)
            {
                try { if (PlayerJoke(spell, caster) && __result > MANA) __result = MANA; }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: mana: {e.Message}"); }
            }
        }

        [HarmonyPatch]
        static class NoComponents
        {
            static MethodBase TargetMethod() => Find(typeof(Player), nameof(Player.HasComponentsForSpell), new[] { typeof(Spell) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static bool Prefix(Player __instance, Spell spell, ref bool __result)
            {
                try { if (!PlayerJoke(spell, __instance)) return true; __result = true; return false; }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: components: {e.Message}"); return true; }
            }
        }

        [HarmonyPatch]
        static class NoBurn
        {
            static MethodBase TargetMethod() => Find(typeof(Player), nameof(Player.TryBurnComponents), new[] { typeof(Spell) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static bool Prefix(Player __instance, Spell spell)
            {
                try { return !PlayerJoke(spell, __instance); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: burn: {e.Message}"); return true; }
            }
        }

        [HarmonyPatch]
        static class Harmless
        {
            static MethodBase TargetMethod() => Find(typeof(SpellProjectile), nameof(SpellProjectile.OnCollideObject), new[] { typeof(WorldObject) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static bool Prefix(SpellProjectile __instance)
            {
                try
                {
                    if (!PlayerFirework(__instance.Spell, __instance.ProjectileSource)) return true;
                    __instance.ProjectileImpact();   // the burst, and nothing else: no damage, no aggro, no PK check
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: collide: {e.Message}"); return true; }
            }
        }

        [HarmonyPatch]
        static class Aim
        {
            static MethodBase TargetMethod() => Find(typeof(WorldObject), nameof(WorldObject.CalculateProjectileVelocity),
                new[] { typeof(Spell), typeof(WorldObject), typeof(ProjectileSpellType), typeof(Vector3) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(WorldObject __instance, Spell spell, WorldObject target, ref Vector3 __result)
            {
                try
                {
                    if (target != null || !PlayerFirework(spell, __instance)) return;
                    float deg = spell.DefaultLaunchAngle;
                    if (deg <= 0f || __result == Vector3.Zero) return;
                    float speed = __result.Length();
                    var flat = new Vector3(__result.X, __result.Y, 0f);
                    flat = flat == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(flat);
                    double a = Math.Min(deg, 89.5f) * Math.PI / 180.0;   // 90 exactly gives set_vector_heading no heading
                    __result = (flat * (float)Math.Cos(a) + Vector3.UnitZ * (float)Math.Sin(a)) * speed;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: aim: {e.Message}"); }
            }
        }

        // ACE's world data gives every firework spell a grey burst: 120 of the 128 point at Force Bolt (7264) and the
        // firework weenies (22259-22314) all carry Force Bolt's effect table 0x34000009 (grey sparks, 0x06004049). The DAT
        // keeps retail's coloured missile tables in a row, each burst's particle art that colour (scratch DAT dump,
        // report 261020-0034): red 0x3400000C, orange 0D, yellow 0E, green 0F, blue 10, purple 11, black 13, white 14.
        // A firework projectile (anyone's) takes its colour's table, and the firework weenies' setup 0x0200040D.
        static readonly (string word, uint table)[] s_Colour =
        {
            (" Red ", 0x3400000C), (" Orange ", 0x3400000D), (" Yellow ", 0x3400000E), (" Green ", 0x3400000F),
            (" Blue ", 0x34000010), (" Purple ", 0x34000011), (" Black ", 0x34000013), (" White ", 0x34000014),
        };
        const uint FIREWORK_SETUP = 0x0200040D, FORCE_BOLT_WCID = 7264;

        [HarmonyPatch]
        static class Colour
        {
            static MethodBase TargetMethod() => Find(typeof(SpellProjectile), nameof(SpellProjectile.Setup), new[] { typeof(Spell), typeof(ProjectileSpellType) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Prefix(SpellProjectile __instance, Spell spell)   // before Setup's InitPhysicsObj reads the setup
            {
                try
                {
                    if (spell == null || !IsFirework(spell.Id) || !On || spell.Name == null) return;
                    foreach (var (word, table) in s_Colour)
                        if (spell.Name.Contains(word)) { __instance.PhysicsTableId = table; break; }
                    if (__instance.WeenieClassId == FORCE_BOLT_WCID) { __instance.SetupTableId = FIREWORK_SETUP; __instance.Name = "Fireworks"; }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: colour: {e.Message}"); }
            }
        }

        [HarmonyPatch]
        static class Fuse
        {
            static MethodBase TargetMethod() => Find(typeof(WorldObject), nameof(WorldObject.LaunchSpellProjectiles),
                new[] { typeof(Spell), typeof(WorldObject), typeof(ProjectileSpellType), typeof(WorldObject), typeof(bool), typeof(bool),
                        typeof(List<Vector3>), typeof(Vector3), typeof(uint) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(WorldObject __instance, Spell spell, WorldObject target, List<SpellProjectile> __result)
            {
                try
                {
                    if (target != null || __result == null || !PlayerFirework(spell, __instance)) return;
                    foreach (var sp in __result)
                    {
                        var chain = new ActionChain();
                        chain.AddDelaySeconds(FUSE_S);
                        chain.AddAction(sp, () =>
                        {
                            // still flying (ProjectileImpact clears ReportCollisions and destroys it 5 s later)
                            if (!sp.IsDestroyed && sp.PhysicsObj != null && sp.ReportCollisions == true) sp.ProjectileImpact();
                        });
                        chain.EnqueueChain();
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] JokeSpells: fuse: {e.Message}"); }
            }
        }
    }
}
