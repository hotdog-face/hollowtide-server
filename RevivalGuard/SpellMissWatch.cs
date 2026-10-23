using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// SPELL MISSES, MEASURED (2026-10-22, The Forsaken Moustache: "20% of my spells hit an invisible wall" and "a DOT
    /// resisting 12 times in a row vs a level 135"). READ ONLY: nothing here changes what ACE does.
    /// 1. A player's targeted bolt that ends on the environment (SpellProjectile.OnCollideEnvironment): one log line and a
    ///    "spellwall" event with the caster's and target's cells and positions, the bolt's flight from its spawn (0 m = ACE's
    ///    launch sight test failed, LaunchSpellProjectiles), and the player's walls/hits so far, so a wall rate and the
    ///    places it happens can be read off the events instead of guessed.
    /// 2. Resist streaks (WorldObject.TryResistSpell): at RESIST_STREAK resists in a row on one target, a line and a
    ///    "resist_streak" event with the caster's school skill, the target's effective magic defense, ACE's resist chance
    ///    (SkillCheck.GetSkillChance, 0.03 factor, as MagicDefenseCheck) and the odds of the streak at that chance.
    ///   @modifybool spell_miss_log false   no lines or events
    /// </summary>
    [HarmonyPatch]
    static class SpellMissWatch
    {
        internal const string P_LOG = "spell_miss_log";
        const int RESIST_STREAK = 6;
        const double WALL_EVERY_S = 1.0;   // per player: at most one wall line/event a second (a multi-bolt spell is one)

        sealed class Tally { public int Walls, WallsAtLaunch, Hits, Streak; public uint StreakTarget; public double LastWall = -1e9; }
        static readonly ConditionalWeakTable<Player, Tally> s_Tally = new();

        static bool On => PropertyManager.GetBool(P_LOG, true).Item;

        static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        static string Where(WorldObject o) =>
            o?.Location == null ? "\"?\"" : $"\"{o.Location.Cell:X8} {F(o.Location.PositionX)} {F(o.Location.PositionY)} {F(o.Location.PositionZ)}\"";

        internal static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_LOG)) b[P_LOG] = new Property<bool>(true, "RevivalGuard SpellMissWatch: log players' bolts that hit the environment and resist streaks (read only)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SpellMissWatch: switch not registered with @modifybool ({e.Message}); read at its default"); }
        }

        // ---- 1. bolts that end on the environment ------------------------------------------------------------------

        [HarmonyPatch]
        static class Wall
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(SpellProjectile), nameof(SpellProjectile.OnCollideEnvironment), Type.EmptyTypes);
                if (m == null) Mod.Log.Error("[RevivalGuard] SpellMissWatch: SpellProjectile.OnCollideEnvironment() not found; walls not logged");
                return m;
            }

            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() == null) return false;
                Register();
                Mod.Log.Info("[RevivalGuard] SpellMissWatch: players' bolts that hit the environment and resist streaks are logged (spell_miss_log)");
                return true;
            }

            static void Prefix(SpellProjectile __instance)
            {
                try
                {
                    if (!(__instance.ProjectileSource is Player p) || !(__instance.ProjectileTarget is Creature t) || t == p || !On) return;
                    var tl = s_Tally.GetOrCreateValue(p);
                    double flight = __instance.SpawnPos != null && __instance.Location != null ? __instance.SpawnPos.DistanceTo(__instance.Location) : -1;
                    bool atLaunch = flight >= 0 && flight < 0.05;
                    int walls, launch, hits;
                    double now = Timers.RunningTime;
                    lock (tl)
                    {
                        tl.Walls++; if (atLaunch) tl.WallsAtLaunch++;
                        walls = tl.Walls; launch = tl.WallsAtLaunch; hits = tl.Hits;
                        if (now - tl.LastWall < WALL_EVERY_S) return;
                        tl.LastWall = now;
                    }
                    double dist = p.Location != null && t.Location != null ? p.Location.DistanceTo(t.Location) : -1;
                    double dz = p.Location != null && t.Location != null ? t.Location.PositionZ - p.Location.PositionZ : 0;
                    string spell = __instance.Spell?.Name ?? "?";
                    Mod.Log.Info($"[RevivalGuard] SpellMissWatch: {p.Name} {spell} -> {t.Name} 0x{t.Guid.Full:X8} hit the environment "
                        + $"{(atLaunch ? "at launch (the spawn was out of the caster's sight)" : F(flight) + " m from its spawn")}; caster {Where(p)}, "
                        + $"target {Where(t)}, {F(dist)} m, dz {F(dz)}; bolt {Where(__instance)}; walls {walls} ({launch} at launch), hits {hits}");
                    Telemetry.Emit("spellwall", p, $",\"spell\":{Telemetry.J(spell)},\"sid\":{__instance.Spell?.Id ?? 0},\"tgt\":{Telemetry.J(t.Name)},\"twcid\":{t.WeenieClassId}"
                        + $",\"flight\":{F(flight)},\"launch\":{(atLaunch ? 1 : 0)},\"dist\":{F(dist)},\"dz\":{F(dz)}"
                        + $",\"me\":{Where(p)},\"them\":{Where(t)},\"bolt\":{Where(__instance)},\"walls\":{walls},\"hits\":{hits}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SpellMissWatch wall: {e.GetType().Name}: {e.Message}"); }
            }
        }

        /// <summary>The bolt that reached its own target, for the walls-to-hits ratio.</summary>
        [HarmonyPatch]
        static class Hit
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(SpellProjectile), nameof(SpellProjectile.OnCollideObject), new[] { typeof(WorldObject) });
                if (m == null) Mod.Log.Error("[RevivalGuard] SpellMissWatch: SpellProjectile.OnCollideObject(WorldObject) not found; hits not counted");
                return m;
            }

            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Prefix(SpellProjectile __instance, WorldObject target)
            {
                try
                {
                    if (!(__instance.ProjectileSource is Player p) || target == null || target != __instance.ProjectileTarget) return;
                    var tl = s_Tally.GetOrCreateValue(p);
                    lock (tl) tl.Hits++;
                }
                catch { }
            }
        }

        // ---- 2. resist streaks -----------------------------------------------------------------------------------

        [HarmonyPatch]
        static class Resist
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(WorldObject), nameof(WorldObject.TryResistSpell),
                    new[] { typeof(WorldObject), typeof(Spell), typeof(WorldObject), typeof(bool) });
                if (m == null) Mod.Log.Error("[RevivalGuard] SpellMissWatch: WorldObject.TryResistSpell(WorldObject, Spell, WorldObject, bool) not found; resist streaks not logged");
                return m;
            }

            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Postfix(WorldObject __instance, WorldObject target, Spell spell, WorldObject itemCaster, bool projectileHit, bool __result)
            {
                try
                {
                    if (!(__instance is Player p) || itemCaster != null || !(target is Creature c) || c == p || spell == null) return;
                    // only casts that reached the roll (TryResistSpell's own early outs return false without one)
                    if (spell.IsSelfTargeted || !spell.IsResistable || (spell.NumProjectiles > 0 && !projectileHit)) return;
                    var tl = s_Tally.GetOrCreateValue(p);
                    int streak;
                    lock (tl)
                    {
                        if (!__result) { tl.Streak = 0; return; }
                        if (tl.StreakTarget != c.Guid.Full) { tl.StreakTarget = c.Guid.Full; tl.Streak = 0; }
                        streak = ++tl.Streak;
                    }
                    if (streak < RESIST_STREAK || !On) return;
                    uint skill = p.GetCreatureSkill(spell.School).Current;
                    uint md = c.GetEffectiveMagicDefense();
                    double land = SkillCheck.GetSkillChance(skill, md);
                    double odds = Math.Pow(1.0 - land, streak);
                    Mod.Log.Info($"[RevivalGuard] SpellMissWatch: {p.Name} resisted {streak} in a row by {c.Name} 0x{c.Guid.Full:X8} (level {c.Level}), "
                        + $"{spell.Name} ({spell.School} {skill} vs magic defense {md}): resist chance {F((1.0 - land) * 100)}%, a streak this long {odds:0.###E+0}"
                        + $"{(p.Vitae < 1f ? $", vitae {F((1f - p.Vitae) * 100)}%" : "")}");
                    Telemetry.Emit("resist_streak", p, $",\"n\":{streak},\"spell\":{Telemetry.J(spell.Name)},\"sid\":{spell.Id},\"school\":\"{spell.School}\",\"skill\":{skill}"
                        + $",\"tgt\":{Telemetry.J(c.Name)},\"twcid\":{c.WeenieClassId},\"tlvl\":{c.Level ?? 0},\"md\":{md},\"resist_pct\":{F((1.0 - land) * 100)}"
                        + $",\"vitae\":{F(p.Vitae)}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SpellMissWatch resist: {e.GetType().Name}: {e.Message}"); }
            }
        }
    }
}
