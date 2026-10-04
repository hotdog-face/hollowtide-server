using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// OLD SNAPJAW, the Blackmire alligator (weenie 900400; docs/ALLIGATOR-BOSS.md). Ours, not retail.
    /// Owner's brief: "hides at the waters edge. does a roll attack and a bite. lots of teeth".
    ///
    /// Everything ordinary is ACE's own: he sleeps at home (the client draws that as the sunk lurk,
    /// only the eyes above the water), wakes at VisualAwarenessRange, chases, bites through his combat
    /// table (the Reedshark's AttackHigh1/Med1/Low1), goes home past HomeRadius, and MonsterAi heals
    /// him when he gets there. This class adds the one thing ACE has no data for, THE DEATH ROLL:
    ///
    ///   A prefix on Creature.MeleeAttack (Monster_Melee.cs, declared on Creature). When his roll is
    ///   ready it replaces that one swing: he broadcasts SpecialAttack1 (the client's DeathRoll clip),
    ///   clamps on at 0.25 s with an ordinary DamageEvent (dodgeable, armour applies), and if the grab
    ///   lands the victim is HELD for the roll: snapjaw_roll_ticks more hits of that same mitigated
    ///   damage times snapjaw_roll_tick_mult, and Leaden Feet (snapjaw_roll_spell) so the escape is
    ///   slow. Every other swing returns true and ACE bites as normal.
    ///   Enraged under half health: the roll comes round snapjaw_roll_enraged_s instead of
    ///   snapjaw_roll_cooldown_s, said once in the chat as an emote.
    ///
    /// Also his trophy (a postfix on Creature.GenerateTreasure, the TrophyMounts shape: a player's
    /// kill puts Mounted Snapjaw Head, 900401, in the corpse inscribed "Slain by ...") and
    /// @snapjaw [status|reset|go] for the admin panel: reset re-runs his generator (a fresh, full
    /// Snapjaw at the lair, whatever state the old one was in), go takes the admin to the south bank.
    ///
    /// Registers itself from Prepare() (PatchAll calls it), so Mod.cs needs no line. Every hook
    /// catches its own exceptions; a throw never reaches ACE's tick.
    /// </summary>
    static class AlligatorBoss
    {
        internal const uint WCID = 900400, TROPHY = 900401, GENERATOR = 900402;
        internal const uint LAIR_BLOCK = 0xCB60, GEN_GUID = 0x7CB60C00;
        // the south bank, 28 m from his home (102, 146) and outside the ~18 m at which he wakes, facing north
        static readonly Position Bank = new Position(0xCB600025, 102.0f, 118.0f, 6.0f, 0f, 0f, 0f, 1f);
        const float ROLL_SECONDS = 1.6f;   // the DeathRoll clip: 48 frames at 30 fps

        static readonly (string key, double def, string desc)[] s_Doubles =
        {
            ("snapjaw_roll_cooldown_s", 16.0, "Old Snapjaw (RevivalGuard AlligatorBoss): seconds between death rolls"),
            ("snapjaw_roll_enraged_s", 9.0, "Old Snapjaw: seconds between death rolls under half health"),
            ("snapjaw_roll_ticks", 2.0, "Old Snapjaw: extra hits while a grabbed victim is rolled"),
            ("snapjaw_roll_tick_mult", 0.9, "Old Snapjaw: each roll hit, as a fraction of the grab's damage"),
            ("snapjaw_roll_spell", 1005.0, "Old Snapjaw: spell cast on a rolled victim (1005 Leaden Feet Other VI; 0 = none)"),
        };

        sealed class St { public double NextRoll = -1; public bool Enraged; }
        static readonly ConditionalWeakTable<Creature, St> s_State = new ConditionalWeakTable<Creature, St>();
        static long s_Rolls, s_Grabs, s_Trophies;
        static bool s_Registered;

        // ------------------------------------------------------------------ the roll

        static double D(int i) => PropertyManager.GetDouble(s_Doubles[i].key, s_Doubles[i].def).Item;

        /// <summary>False when this swing was the roll (ACE's MeleeAttack skipped, __result its length).</summary>
        internal static bool BeforeMelee(Creature c, ref float __result)
        {
            if (c.WeenieClassId != WCID) return true;
            var target = c.AttackTarget as Creature;
            if (target == null || target.IsDead || c.IsDead) return true;
            var st = s_State.GetValue(c, _ => new St());
            double rt = Timers.RunningTime;
            if (st.NextRoll < 0) { st.NextRoll = rt + 6.0; return true; }   // a few bites first

            float hp = c.Health.MaxValue > 0 ? (float)c.Health.Current / c.Health.MaxValue : 1f;
            if (hp >= 0.99f) st.Enraged = false;   // healed at home (MonsterAi) or reset: calm again
            if (!st.Enraged && hp < 0.5f)
            {
                st.Enraged = true;
                c.EnqueueBroadcast(new GameMessageEmoteText(c.Guid.Full, c.Name, "thrashes, churning the swamp to foam!"), 40f);
            }
            if (rt < st.NextRoll) return true;

            st.NextRoll = rt + (st.Enraged ? D(1) : D(0));
            __result = Roll(c, target);
            return false;
        }

        static float Roll(Creature c, Creature target)
        {
            s_Rolls++;
            if (c.CurrentMotionState?.Stance == MotionStance.NonCombat) c.DoAttackStance();
            c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.SpecialAttack1));
            if (!c.AiImmobile) c.PhysicsObj.stick_to_object(target.PhysicsObj.ID);

            int ticks = Math.Clamp((int)Math.Round(D(2)), 0, 6);
            float mult = (float)Math.Clamp(D(3), 0.0, 5.0);
            uint spellId = (uint)Math.Max(0, D(4));
            var player = target as Player;

            DamageEvent grab = null;
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.25);
            chain.AddAction(c, () =>
            {
                if (c.IsDead || target.IsDead) return;
                grab = DamageEvent.CalculateDamage(c, target, null, MotionCommand.AttackLow1);
                if (!grab.HasDamage) { target.OnEvade(c, CombatType.Melee); grab = null; return; }
                s_Grabs++;
                if (player != null)
                {
                    player.TakeDamage(c, grab);
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat($"{c.Name} seizes you in his jaws and rolls!", ChatMessageType.CombatEnemy));
                }
                else target.TakeDamage(c, grab.DamageType, grab.Damage);
                c.EnqueueBroadcast(new GameMessageEmoteText(c.Guid.Full, c.Name, $"clamps onto {target.Name} and rolls!"), 40f);
                if (spellId != 0 && !target.IsDead)
                {
                    try { c.TryCastSpell(new Spell(spellId), target, c); }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AlligatorBoss: roll spell {spellId}: {e.Message}"); }
                }
            });
            for (int i = 0; i < ticks; i++)
            {
                chain.AddDelaySeconds(0.4);
                chain.AddAction(c, () =>
                {
                    if (grab == null || c.IsDead || target.IsDead || c.GetDistance(target) > 4f) return;
                    float amount = grab.Damage * mult;
                    if (player != null) player.TakeDamage(c, grab.DamageType, amount, grab.BodyPart, grab.IsCritical);
                    else target.TakeDamage(c, grab.DamageType, amount);
                });
            }
            chain.EnqueueChain();

            double now = Timers.RunningTime;
            c.PrevAttackTime = now;
            c.NextMoveTime = now + ROLL_SECONDS + 0.5;
            c.NextAttackTime = now + ROLL_SECONDS + 0.3;
            return ROLL_SECONDS;
        }

        // ------------------------------------------------------------------ the trophy

        internal static void AfterTreasure(Creature c, DamageHistoryInfo killer, Corpse corpse, List<WorldObject> result)
        {
            if (c == null || c.WeenieClassId != WCID || killer == null) return;
            if (!(killer.TryGetPetOwnerOrAttacker() is Player player)) return;
            var trophy = WorldObjectFactory.CreateNewWorldObject(TROPHY);
            if (trophy == null) { Mod.Log.Warn($"[RevivalGuard] AlligatorBoss: weenie {TROPHY} missing; no trophy"); return; }
            trophy.SetProperty(PropertyString.LongDesc, TrophyMounts.Inscription(c.Name, player.Name, DateTime.UtcNow));
            if (corpse != null) { if (!corpse.TryAddToInventory(trophy)) { trophy.Destroy(); return; } }
            else result?.Add(trophy);
            s_Trophies++;
            Mod.Log.Info($"[RevivalGuard] AlligatorBoss: {c.Name} slain by {player.Name}; {trophy.Name} 0x{trophy.Guid.Full:X8} in {(corpse != null ? "the corpse" : "the loot")}");
        }

        // ------------------------------------------------------------------ @snapjaw

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Mod.Log.Info(text);
        }

        static void Handle(Session session, params string[] p)
        {
            string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "status";
            var lb = LandblockManager.GetLandblock(new LandblockId(LAIR_BLOCK << 16 | 0xFFFF), false);
            var gen = lb?.GetObject(GEN_GUID);
            var snap = lb?.GetAllWorldObjectsForDiagnostics().OfType<Creature>().FirstOrDefault(o => o.WeenieClassId == WCID && !o.IsDead);
            if (verb == "go")
            {
                var pl = session?.Player;
                if (pl == null) { Say(session, "[Snapjaw] needs a player"); return; }
                pl.Teleport(new Position(Bank));
                Say(session, "[Snapjaw] to the south bank of his channel; walk north to the water (ours, not retail)");
                return;
            }
            if (verb == "reset")
            {
                if (gen == null) { Say(session, $"[Snapjaw] generator 0x{GEN_GUID:X8} not found in landblock {LAIR_BLOCK:X4}: is alligator-boss.sql applied?"); return; }
                gen.ResetGenerator();                    // what ACE's own @regen does, without a selection
                gen.GeneratorEnteredWorld = false;
                gen.GeneratorRegeneration(Time.GetUnixTime());
                Say(session, "[Snapjaw] reset: a fresh Old Snapjaw is back in his water (ours, not retail)");
                Mod.Log.Info($"[RevivalGuard] AlligatorBoss: reset by {session?.Player?.Name ?? "console"}");
                return;
            }
            string where = snap == null ? "not spawned" :
                $"{snap.Health.Current}/{snap.Health.MaxValue} health, {(snap.IsAwake ? "awake" : "asleep")}, {snap.MonsterState}, at {snap.Location.ToLOCString()}";
            Say(session, $"[Snapjaw] (ours, not retail) landblock {LAIR_BLOCK:X4} {(lb != null ? "loaded" : "not loaded")}, generator {(gen != null ? "present" : "MISSING")}; Old Snapjaw {where}. Rolls {s_Rolls}, grabs {s_Grabs}, trophies {s_Trophies}. @snapjaw reset | go");
        }

        // ------------------------------------------------------------------ registration

        static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                foreach (var (key, def, desc) in s_Doubles) if (!d.ContainsKey(key)) d[key] = new Property<double>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AlligatorBoss: tuning not registered with @modifydouble ({e.Message}); defaults apply"); }
            CommandManager.TryAddCommand(Handle, "snapjaw", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Old Snapjaw, the Blackmire alligator boss (RevivalGuard): status, reset (respawn him fresh), go (to his lair).",
                "[status | reset | go]");
            Mod.Log.Info("[RevivalGuard] AlligatorBoss: Old Snapjaw (900400) death roll, trophy, @snapjaw");
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }

        // ------------------------------------------------------------------ the patches

        [HarmonyPatch(typeof(Creature), nameof(Creature.MeleeAttack))]
        static class Melee
        {
            static int s_Errors;
            static bool Prepare() { Register(); return true; }
            static bool Prefix(Creature __instance, ref float __result)
            {
                try { return BeforeMelee(__instance, ref __result); }
                catch (Exception e)
                {
                    if (++s_Errors <= 5) Mod.Log.Error($"[RevivalGuard] AlligatorBoss roll threw for {__instance?.Name}; ACE's swing runs", e);
                    return true;
                }
            }
        }

        /// <summary>BACK IN HIS WATER, FACING THE BANK. ACE's return home keeps whatever heading the
        /// walk back left (away from the bank, since he came from it), so he lurked tail-first. Sleep
        /// is where a creature settles at home; turn him to his home heading there.</summary>
        [HarmonyPatch(typeof(Creature), nameof(Creature.Sleep))]
        static class Settle
        {
            static void Postfix(Creature __instance)
            {
                try
                {
                    if (__instance?.WeenieClassId != WCID || __instance.IsDead) return;
                    var home = __instance.GetPosition(PositionType.Home);
                    if (home == null || __instance.Location == null || home.DistanceTo(__instance.Location) > 4f) return;
                    __instance.TurnTo(home);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AlligatorBoss settle: {e.Message}"); }
            }
        }

        [HarmonyPatch]
        static class Treasure
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
            static void Postfix(Creature __instance, DamageHistoryInfo killer, Corpse corpse, List<WorldObject> __result)
            {
                try { AfterTreasure(__instance, killer, corpse, __result); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AlligatorBoss trophy: {e.GetType().Name}: {e.Message}"); }
            }
        }
    }
}
