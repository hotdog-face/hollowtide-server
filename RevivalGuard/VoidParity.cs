using System.Collections.Concurrent;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// VOID MAGIC PARITY WITH WAR MAGIC (owner 2026-11-27; the why and the numbers: VoidParityRule.cs and
    /// docs/findings/void-parity-2026-11-27.md). Ours, not retail. Every part OFF by default.
    ///
    ///   rg_void_curse_vuln (bool, off)        while a player's void damage curse (Corrosion, Corruption, Destructive
    ///                                          Curse) is on a monster, nether damage players deal it (bolts and curse
    ///                                          ticks) x the curse level's factor; players as targets never.
    ///   rg_void_curse_vuln_levels (string)    "level:mult,...", default VoidParityRule.DEFAULT_LEVELS.
    ///   rg_void_bench (bool, off)             test shards only: the @voidparity bench and setup below.
    ///
    /// The multiplier sits on ACE's Creature.GetResistanceMod(DamageType, ...), the one both a spell projectile's
    /// CalculateDamage (through the ResistanceType overload; attacker = the projectile) and a DoT's ApplyDamageTick
    /// (attacker = the caster, weapon null) read, so it stacks the way a life vulnerability does (protection x vuln).
    ///
    ///   @voidparity                                 switches, the level table
    ///   @voidparity setup &lt;skill&gt;                   War, Void and Life Magic specialised at that base (bench on)
    ///   @voidparity bench war|void &lt;level&gt; &lt;seconds&gt; [full|bolts] [fire|bludgeon] [cycle s]
    ///       on the last appraised creature: casts through ACE's own TryCastSpell (real projectiles, resist rolls, crits,
    ///       DoT ticks, vulns) every cast cycle (the spell's windup + cast gestures from the motion table, or the given
    ///       cycle), then reports damage, casts, DPS, damage per mana and time to kill. "full" = war lands its
    ///       vulnerability first / void keeps its three curses up; "bolts" = bolts only.
    /// </summary>
    [HarmonyPatch]
    static class VoidParity
    {
        internal const string P_ON = "rg_void_curse_vuln", P_LEVELS = "rg_void_curse_vuln_levels", P_BENCH = "rg_void_bench";

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }
        static bool BenchOn { get { try { return PropertyManager.GetBool(P_BENCH, false).Item; } catch { return false; } } }

        static string s_TableText;
        static Dictionary<int, double> s_Table = VoidParityRule.ParseLevels(VoidParityRule.DEFAULT_LEVELS);
        static long s_Applied;

        static Dictionary<int, double> Table()
        {
            string t;
            try { t = PropertyManager.GetString(P_LEVELS, VoidParityRule.DEFAULT_LEVELS).Item ?? VoidParityRule.DEFAULT_LEVELS; }
            catch { t = VoidParityRule.DEFAULT_LEVELS; }
            if (!ReferenceEquals(t, s_TableText) && t != s_TableText) { s_Table = VoidParityRule.ParseLevels(t); s_TableText = t; }
            return s_Table;
        }

        static readonly SpellCategory[] CurseCats =
            { SpellCategory.NetherDamageOverTimeRaising, SpellCategory.NetherDamageOverTimeRaising2, SpellCategory.NetherDamageOverTimeRaising3 };

        internal static int StrongestCurse(Creature c)
        {
            var ids = new List<int>(4);
            foreach (var cat in CurseCats)
                foreach (var e in c.EnchantmentManager.GetEnchantments(cat)) ids.Add(e.SpellId);
            return VoidParityRule.StrongestCurse(ids);
        }

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.GetResistanceMod),
                new[] { typeof(DamageType), typeof(WorldObject), typeof(WorldObject), typeof(float) });
            if (m == null) Mod.Log.Error("[RevivalGuard] VoidParity: Creature.GetResistanceMod(DamageType, ...) not found; the curse vulnerability is off");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard VoidParity: void's damage curses on a monster raise the nether damage players deal it by the curse level's factor (rg_void_curse_vuln_levels); players never (false: ACE)");
            PkCommon.RegisterBool(P_BENCH, false, "RevivalGuard VoidParity: the @voidparity bench and setup (test shards only)");
            RegisterString(P_LEVELS, VoidParityRule.DEFAULT_LEVELS, "RevivalGuard VoidParity: curse level:multiplier for rg_void_curse_vuln (unlisted = 1.0, capped at 3.1)");
            CommandManager.TryAddCommand(Command, "voidparity", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Void parity (RevivalGuard): status; 'setup <skill>' and 'bench war|void <level> <seconds> [full|bolts] [fire|bludgeon] [cycle]' on test shards (rg_void_bench).",
                "[setup|bench ...]");
            Mod.Log.Info($"[RevivalGuard] VoidParity: curse vulnerability {(On ? "ON" : "off")} ({P_ON}, table {VoidParityRule.DEFAULT_LEVELS} unless {P_LEVELS}); bench {(BenchOn ? "ON" : "off")} ({P_BENCH})");
            return true;
        }

        static void RegisterString(string key, string def, string desc)
        {
            try
            {
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(key)) s[key] = new Property<string>(def, desc);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {key}: not registered with @modifystring ({e.Message}); read with its default"); }
        }

        /// <summary>The multiplier, after ACE's own protection x vulnerability (and rending) for this damage.</summary>
        static void Postfix(Creature __instance, DamageType damageType, WorldObject attacker, WorldObject weapon, ref float __result)
        {
            try
            {
                if (damageType != DamageType.Nether || __result <= 0f || !On) return;
                bool sourcePlayer = attacker is Player && weapon == null                                  // a curse tick
                    || attacker is SpellProjectile sp && sp.ProjectileSource is Player;                     // a void bolt
                if (!VoidParityRule.Applies(true, true, __instance is Player, sourcePlayer)) return;
                var lvl = StrongestCurse(__instance);
                if (lvl <= 0) return;
                var m = VoidParityRule.Mult(Table(), lvl);
                if (m <= 1.0) return;
                __result = (float)(__result * m);
                Interlocked.Increment(ref s_Applied);
            }
            catch (Exception e) { TickGuard.Report("VoidParity.GetResistanceMod", e); }
        }

        // ---------------------------------------------------------------- the bench (test shards) ----

        sealed class Bench
        {
            public Player P; public Creature T; public string School, Element; public int Level; public bool Full;
            public double Cycle; public DateTime Start, End; public int Casts, Bolts, Setup, Mana; public long Damage;
            public bool Done; public double? Ttk; public uint TargetGuid; public string TargetName;
        }

        static readonly ConcurrentDictionary<uint, Bench> s_Bench = new();   // by player guid

        /// <summary>Damage the bench player does to the bench target (direct and over time): DamageHistory records what
        /// the vital lost, so a kill counts only the health that was there.</summary>
        [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
        static class Meter
        {
            static void Postfix(DamageHistory __instance, WorldObject attacker, uint amount)
            {
                try
                {
                    if (amount == 0 || !(attacker is Player p) || s_Bench.IsEmpty) return;
                    if (!s_Bench.TryGetValue(p.Guid.Full, out var b) || b.Done || __instance.Creature?.Guid.Full != b.TargetGuid) return;
                    Interlocked.Add(ref b.Damage, amount);
                }
                catch (Exception e) { TickGuard.Report("VoidParity.Meter", e); }
            }
        }

        static void Say(Session s, string msg)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
            Mod.Log.Info(msg);
        }

        static void Command(Session session, params string[] a)
        {
            var p = session?.Player;
            if (p == null) return;
            var verb = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            if (verb == "")
            {
                var t = Table();
                Say(session, $"[VoidParity] {P_ON} {(On ? "ON" : "off")}, levels {string.Join(",", t.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value:0.##}"))}, applied {Interlocked.Read(ref s_Applied)}; {P_BENCH} {(BenchOn ? "ON" : "off")}.");
                return;
            }
            if (!BenchOn) { Say(session, $"[VoidParity] {verb}: {P_BENCH} is off (test shards only)."); return; }
            if (verb == "setup")
            {
                if (a.Length < 2 || !int.TryParse(a[1], out var skill) || skill < 1 || skill > 999) { Say(session, "[VoidParity] setup <skill 1-999>"); return; }
                foreach (var sk in new[] { Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic })
                {
                    var cs = p.GetCreatureSkill(sk);
                    cs.AdvancementClass = SkillAdvancementClass.Specialized;
                    var table = Player.GetSkillXPTable(SkillAdvancementClass.Specialized);
                    if (table != null && table.Count > 0) { cs.Ranks = (ushort)(table.Count - 1); cs.ExperienceSpent = table[table.Count - 1]; }
                    // aim at Current (what a cast rolls with: base x vitae, plus buffs): move InitLevel until it lands
                    for (int i = 0; i < 4 && cs.Current != skill; i++)
                    {
                        double ratio = cs.Base > 0 ? (double)cs.Current / cs.Base : 1.0;
                        long wantBase = (long)Math.Round(skill / Math.Max(0.05, ratio)), diff = wantBase - cs.Base;
                        if (diff == 0) diff = skill > cs.Current ? 1 : -1;
                        cs.InitLevel = (uint)Math.Max(0, (long)cs.InitLevel + diff);
                    }
                    session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(p, cs));
                }
                Say(session, $"[VoidParity] setup: War {p.GetCreatureSkill(Skill.WarMagic).Current}, Void {p.GetCreatureSkill(Skill.VoidMagic).Current}, Life {p.GetCreatureSkill(Skill.LifeMagic).Current} (current, with buffs).");
                return;
            }
            if (verb == "invincible")   // the bench caster takes no damage while monsters are spawned beside it
            {
                p.Invincible = a.Length > 1 && a[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                Say(session, $"[VoidParity] invincible {(p.Invincible ? "on" : "off")}.");
                return;
            }
            if (verb == "stop") { if (s_Bench.TryGetValue(p.Guid.Full, out var ob)) Finish(ob, "stopped"); return; }
            if (verb != "bench" || a.Length < 4) { Say(session, "[VoidParity] bench war|void <level 1-8> <seconds> [full|bolts] [fire|bludgeon] [cycle s]"); return; }
            var school = a[1].ToLowerInvariant();
            if (school != "war" && school != "void") { Say(session, "[VoidParity] bench: school is war or void"); return; }
            if (!int.TryParse(a[2], out var level) || level < 1 || level > 8) { Say(session, "[VoidParity] bench: level 1-8"); return; }
            if (!double.TryParse(a[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs) || secs < 1 || secs > 600) { Say(session, "[VoidParity] bench: seconds 1-600"); return; }
            bool full = !(a.Length > 4 && a[4].Equals("bolts", StringComparison.OrdinalIgnoreCase));
            string elem = a.Length > 5 ? a[5].ToLowerInvariant() : "fire";
            double cycle = 0;
            if (a.Length > 6) double.TryParse(a[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cycle);
            var target = p.RequestedAppraisalTarget is uint sel ? p.CurrentLandblock?.GetObject(sel) as Creature : null;
            if (target == null || target is Player || !target.IsAlive)   // else the nearest living monster in view
                target = p.PhysicsObj?.ObjMaint?.GetVisibleObjectsValuesOfTypeCreature()
                    .Where(c => !(c is Player) && !(c is CombatPet) && c.IsAlive && c.Attackable)
                    .OrderBy(c => p.GetDistance(c)).FirstOrDefault();
            if (target == null || target is Player || !target.IsAlive) { Say(session, "[VoidParity] bench: appraise a living monster first."); return; }
            var dist = p.GetDistance(target);
            if (dist > 20f) { Say(session, $"[VoidParity] bench: {target.Name} is {dist:0.0} m away; stand within 20 m (curse projectiles fall short past ~25 m, Destructive Curse needs 10)."); return; }
            // a bench target never fights back (the Pyreal Target Drudge's own Tolerance), so the caster stands and casts
            if (!target.Tolerance.HasFlag(Tolerance.NoAttack)) target.Tolerance |= Tolerance.NoAttack;
            if (s_Bench.TryGetValue(p.Guid.Full, out var old) && !old.Done) Finish(old, "replaced");
            var b = new Bench
            {
                P = p, T = target, TargetGuid = target.Guid.Full, TargetName = target.Name, School = school, Element = elem, Level = level, Full = full,
                Cycle = cycle, Start = DateTime.UtcNow, End = DateTime.UtcNow.AddSeconds(secs),
            };
            s_Bench[p.Guid.Full] = b;
            Say(session, $"[VoidParity] bench {school} {level} {(full ? "full" : "bolts")} {(school == "war" ? elem : "nether")} on {target.Name} 0x{target.Guid.Full:X8} at {dist:0.0} m (lvl {target.Level}, hp {target.Health.Current}/{target.Health.MaxValue}) for {secs:0} s; skill war {p.GetCreatureSkill(Skill.WarMagic).Current} void {p.GetCreatureSkill(Skill.VoidMagic).Current} life {p.GetCreatureSkill(Skill.LifeMagic).Current}; {P_ON} {(On ? "ON" : "off")}.");
            Step(b);
        }

        /// <summary>The spell's cast time from the player's motion table: windup gestures and the cast gesture, at ACE's
        /// player CastSpeed (Player_Magic: retail pcaps' animation speed for the windup and the first half of the cast).</summary>
        static double CastTime(Player p, Spell s)
        {
            try
            {
                double t = 0;
                foreach (var g in s.Formula.WindupGestures)
                    t += ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(p.MotionTableId, MotionStance.Magic, g, Player.CastSpeed);
                var cg = s.Formula.CastGesture;
                if (cg != MotionCommand.Invalid)
                    t += ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(p.MotionTableId, MotionStance.Magic, cg, Player.CastSpeed);
                return t > 0.2 ? t : 1.0;
            }
            catch { return 1.0; }
        }

        static void Step(Bench b)
        {
            if (b.Done) return;
            var t = b.T;
            if (t == null || t.IsDead || !t.IsAlive) { b.Ttk = (DateTime.UtcNow - b.Start).TotalSeconds; Finish(b, "killed"); return; }
            if (DateTime.UtcNow >= b.End || b.P?.Session == null) { Finish(b, "time"); return; }
            bool bludg = b.Element == "bludgeon";
            bool hasVuln = t.EnchantmentManager.HasSpell(VoidParityRule.At(bludg ? VoidParityRule.BludgeonVulnOther : VoidParityRule.FireVulnOther, b.Level));
            bool hasCo = t.EnchantmentManager.GetEnchantments(SpellCategory.NetherDamageOverTimeRaising2).Count > 0;
            bool hasCu = t.EnchantmentManager.GetEnchantments(SpellCategory.NetherDamageOverTimeRaising3).Count > 0;
            bool hasDc = t.EnchantmentManager.GetEnchantments(SpellCategory.NetherDamageOverTimeRaising).Count > 0;
            var id = VoidParityRule.NextSpell(b.School, b.Element, b.Level, b.Full, hasVuln, hasCo, hasCu, hasDc);
            var spell = new Spell(id);
            double cycle = b.Cycle > 0 ? b.Cycle : CastTime(b.P, spell);
            if (!spell.NotFound)
            {
                b.P.TryCastSpell(spell, t, tryResist: !spell.IsProjectile);
                b.Casts++; b.Mana += (int)spell.BaseMana;
                if (b.Casts <= 6) Mod.Log.Info($"[VoidParity] cast {b.Casts}: {spell.Name} ({id}) at {t.Name}, vuln {hasVuln} corrosion {hasCo} corruption {hasCu} curse {hasDc}, enchantments {t.EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive).Count}");
                bool bolt = id == VoidParityRule.At(VoidParityRule.NetherBolt, b.Level) || id == VoidParityRule.At(VoidParityRule.FlameBolt, b.Level)
                    || id == VoidParityRule.At(VoidParityRule.ShockWave, b.Level);
                if (bolt) b.Bolts++; else b.Setup++;
            }
            var chain = new ActionChain();
            chain.AddDelaySeconds(cycle);
            chain.AddAction(b.P, () => Step(b));
            chain.EnqueueChain();
        }

        static void Finish(Bench b, string why)
        {
            if (b.Done) return;
            // let the last projectile land before reading the meter
            var chain = new ActionChain();
            chain.AddDelaySeconds(why == "killed" ? 0 : 1.5);
            chain.AddAction(b.P, () =>
            {
                if (b.Done) return;
                b.Done = true;
                var secs = (b.Ttk ?? (DateTime.UtcNow - b.Start).TotalSeconds);
                var dmg = Interlocked.Read(ref b.Damage);
                var msg = $"[VoidParity] RESULT {b.School} L{b.Level} {(b.Full ? "full" : "bolts")} vs {b.TargetName}: {why}, {secs:0.0} s, damage {dmg}, "
                    + $"{(secs > 0 ? dmg / secs : 0):0.0}/s, casts {b.Casts} (bolts {b.Bolts}, setup {b.Setup}), {(b.Casts > 0 ? (double)dmg / b.Casts : 0):0.0}/cast, "
                    + $"{(b.Mana > 0 ? (double)dmg / b.Mana : 0):0.00}/mana{(b.Ttk != null ? $", TTK {b.Ttk:0.0} s" : "")}; {P_ON} {(On ? "ON" : "off")}";
                Say(b.P.Session, msg);
                s_Bench.TryRemove(new KeyValuePair<uint, Bench>(b.P.Guid.Full, b));
            });
            chain.EnqueueChain();
        }
    }
}
