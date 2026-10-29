using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ONE GOAL PER FESTIVAL (owner 2026-10-21, seasonal improvement #2). The goals are world data
    /// (tools/gpubox-ace/gen_festival_goals.py: quest counters, KillQuest3 on the festival creatures, emote sets on
    /// the turn-in NPCs, six trophy weenies 902200-902205). This file does what data cannot:
    ///   * a counter only moves while its festival runs (SeasonalCalendar, forced states included) and not after this
    ///     festival's trophy was taken: retail creatures such as the Old Zombies or the bunnies also live outside it;
    ///   * a kill goal starts at its first festival kill (ACE counts a kill task only once the quest is on the record),
    ///     and every count says "Harvest Hunt: 7 of 20" instead of ACE's "You have killed 7 Pumpkin Kins!";
    ///   * the three Costumers are vendors, and ACE runs no Use emote for a vendor: their Use set (the hand-in) runs
    ///     before the shop opens;
    ///   * a trophy is named for the year it was won: "Frostfell 2026 Snowglobe" (Frostfell's January days count for
    ///     the December it began in).
    /// docs/EVENT-DIRECTOR.md "Festival goals" has the table.
    /// </summary>
    static class FestivalGoals
    {
        internal sealed class Goal
        {
            public string Counter, Title, Ask, Unit, Festival, HandIn;
            public string[] Keys;    // SeasonalCalendar keys it runs in
            public int N;
            public bool Kill;
            public uint Trophy;
        }

        internal static readonly Goal[] Goals =
        {
            new Goal { Counter = "HarvestHunt", Title = "Harvest Hunt", N = 20, Kill = true, Keys = new[] { "festivalseason" },
                Festival = "Festival Season", Ask = "Slay 20 creatures of the harvest", Unit = "creatures of the harvest slain", Trophy = 902200,
                HandIn = "the Festival Costumer in Glenden Wood" },
            new Goal { Counter = "PresentHunt", Title = "Present Hunt", N = 6, Keys = new[] { "frostfell" },
                Festival = "Frostfell", Ask = "Find 6 of the 12 hidden gift boxes", Unit = "hidden gift boxes found", Trophy = 902201,
                HandIn = "the Frostfell Costumer in Holtburg" },
            new Goal { Counter = "SpringCleaning", Title = "Spring Cleaning", N = 25, Kill = true, Keys = new[] { "spring" },
                Festival = "the Spring Festival", Ask = "Thin out 25 of spring's young creatures", Unit = "young creatures thinned out", Trophy = 902202,
                HandIn = "the Spring Costumer in Holtburg" },
            new Goal { Counter = "CandleRaceWins", Title = "Candle Race Champion", N = 3, Keys = new[] { "wedding" },
                Festival = "the Royal Wedding Anniversary", Ask = "Win Hoshino Kiri's candle race 3 times", Unit = "candle races won", Trophy = 902203 },
            new Goal { Counter = "NightClubNights", Title = "Night Club Regular", N = 3, Keys = new[] { "nightclub", "anniversary" },
                Festival = "the Anniversary", Ask = "Dance at the Night Club on 3 nights", Unit = "nights at the Night Club", Trophy = 902204 },
            new Goal { Counter = "NewYearPeas", Title = "Peas for the New Year", N = 5, Keys = new[] { "newyear" },
                Festival = "the New Year", Ask = "Bring Rayeesh 5 bowls of black-eyed peas", Unit = "bowls of black-eyed peas given", Trophy = 902205 },
        };

        // Costumers (vendors) whose Use set is a goal's hand-in: Festival, Frostfell, Spring.
        internal static readonly HashSet<uint> VendorHandIns = new HashSet<uint> { 900730, 900731, 900732 };

        // Trophy wcid -> (festival key for the year, name, long description); {0} is the year.
        static readonly Dictionary<uint, (string key, string name, string desc)> Trophies = new Dictionary<uint, (string, string, string)>
        {
            { 902200, ("festivalseason", "Festival Season {0} Jack o' Lantern",
                "A great jack o' lantern carved for Festival Season {0}, the Harvest Hunt's prize: twenty creatures of the harvest slain.") },
            { 902201, ("frostfell", "Frostfell {0} Snowglobe",
                "A snow globe of a cabin in the woods, from Frostfell {0}: the Present Hunt's prize for six hidden gift boxes found.") },
            { 902202, ("spring", "Spring Festival {0} Sunflowers",
                "Sunflowers in a crystal vase from the Spring Festival of {0}, for Spring Cleaning: twenty-five of spring's young creatures thinned out.") },
            { 902203, ("wedding", "Royal Wedding {0} Cake Topper",
                "The figures from the top of the royal wedding cake, given at the Royal Wedding Anniversary of {0} to a three-time winner of Hoshino Kiri's candle race.") },
            { 902204, ("anniversary", "Anniversary Balloons",   // owner 2026-10-28: no year in the name (it stays in the description)
                "Balloons from the Anniversary of {0}, for three nights of dancing at the Night Club.") },
            { 902205, ("newyear", "New Year {0} Champagne",
                "A bottle of crystal champagne for the New Year of {0}, Rayeesh's thanks for five bowls of black-eyed peas. Far too much to drink.") },
        };

        static readonly Dictionary<string, Goal> s_ByCounter = Goals.ToDictionary(g => g.Counter, StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> s_Missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static Goal ByCounter(string questFormat) =>
            questFormat != null && s_ByCounter.TryGetValue(QuestManager.GetQuestName(questFormat), out var g) ? g : null;

        internal static bool Running(Goal g) => g.Keys.Any(SeasonalCalendar.IsFestivalActive);

        /// <summary>This festival's trophy taken: its flag is still inside its 45-day timer.</summary>
        internal static bool TrophyTaken(Player p, Goal g)
        {
            var t = g.Counter + "Trophy";
            return p.QuestManager.HasQuest(t) && !p.QuestManager.CanSolve(t);
        }

        /// <summary>
        /// EACH YEAR STARTS FROM ZERO (owner 2026-10-21: "Reset each year"). A counter last moved more than STALE_DAYS
        /// ago belongs to an earlier year's festival (no window is longer than 31 days, and the same festival comes back
        /// a year later), so it is erased before it is read or moved. Keyed on the record's own LastTimeCompleted: no
        /// extra state, and the same answer after any restart. Run at login (before any hand-in can read it) and before
        /// every count.
        /// </summary>
        const double STALE_DAYS = 60;

        internal static bool ResetIfStale(QuestManager qm, Goal g)
        {
            var q = qm.GetQuest(g.Counter);
            if (q == null) return false;
            double age = ACE.Common.Time.GetUnixTime() - q.LastTimeCompleted;
            if (age < STALE_DAYS * 86400) return false;
            qm.Erase(g.Counter);
            return true;
        }

        internal static void ResetStale(Player p)
        {
            foreach (var g in Goals)
                if (ResetIfStale(p.QuestManager, g))
                    Mod.Log.Info($"[RevivalGuard] FestivalGoals: {p.Name}'s {g.Counter} was from an earlier year; cleared");
        }

        /// <summary>The year a trophy is for, at UTC-5 like the calendar: Frostfell's January days are December's.</summary>
        internal static int Year(string key)
        {
            var local = DateTime.UtcNow.AddHours(-5);
            return key == "frostfell" && local.Month == 1 ? local.Year - 1 : local.Year;
        }

        internal static void Validate()
        {
            foreach (var g in Goals)
                foreach (var k in g.Keys)
                    if (!SeasonalCalendar.IsFestivalKey(k)) Mod.Log.Warn($"[RevivalGuard] FestivalGoals: {g.Counter}: no festival '{k}'");
            Mod.Log.Info($"[RevivalGuard] FestivalGoals: {Goals.Length} festival goals ({string.Join(", ", Goals.Select(g => g.Title))}), "
                + "trophies 902200-902205 (tools/gpubox-ace/festival-goals.sql)");
        }

        /// <summary>A festival kill goal open to this player now (festival on, no trophy yet, row present, not maxed):
        /// clears a stale count, starts the counter at 0 with the "begun" line if it is not on the record, and says yes.</summary>
        internal static bool BeginKill(Player p, Goal g)
        {
            var qm = p?.QuestManager;
            if (qm == null || !g.Kill || !Running(g) || TrophyTaken(p, g) || !HasRow(g.Counter)) return false;
            ResetIfStale(qm, g);
            if (qm.IsMaxSolves(g.Counter)) return false;
            if (!qm.HasQuest(g.Counter))
            {
                qm.SetQuestCompletions(g.Counter, 0);
                Say(p, $"Festival goal begun: {g.Title}. {g.Ask} during {g.Festival} for a house trophy marked with the year"
                    + (g.HandIn != null ? $", from {g.HandIn}" : "") + ". Your Journal tracks it.");
            }
            return true;
        }

        internal static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        internal static string Progress(Goal g, int n)
        {
            if (n < g.N) return $"{g.Title}: {n} of {g.N} {g.Unit}.";
            var prize = Trophies.TryGetValue(g.Trophy, out var t) ? string.Format(t.name, Year(t.key)) : "your prize";
            return g.HandIn != null
                ? $"{g.Title} complete! See {g.HandIn} for your {prize}."
                : $"{g.Title} complete!";
        }

        /// <summary>A trophy just made gets the year in its name and description.</summary>
        internal static void NameTrophy(WorldObject wo)
        {
            if (wo == null || !Trophies.TryGetValue(wo.WeenieClassId, out var t)) return;
            int y = Year(t.key);
            wo.Name = string.Format(t.name, y);
            wo.LongDesc = string.Format(t.desc, y);
        }

        /// <summary>Is the quest row there? Logged once a name: festival-goals.sql not applied means no goal at all.</summary>
        internal static bool HasRow(string name)
        {
            if (DatabaseManager.World.GetCachedQuest(name) != null) return true;
            lock (s_Missing)
                if (s_Missing.Add(name))
                    Mod.Log.Warn($"[RevivalGuard] FestivalGoals: no quest row '{name}' (tools/gpubox-ace/festival-goals.sql not applied?)");
            return false;
        }
    }

    /// <summary>A goal counter moves only in its festival and before this festival's trophy; then the count is said.</summary>
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Stamp))]
    static class FestivalGoalsStamp
    {
        static bool s_Done;
        static bool Prepare() { if (!s_Done) { s_Done = true; try { FestivalGoals.Validate(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals: {e.Message}"); } } return true; }

        static bool Prefix(QuestManager __instance, string questFormat, ref int __state)
        {
            __state = -1;
            try
            {
                var g = FestivalGoals.ByCounter(questFormat);
                if (g == null || !(__instance.Creature is Player p)) return true;
                if (!FestivalGoals.Running(g) || FestivalGoals.TrophyTaken(p, g)) return false;
                FestivalGoals.ResetIfStale(__instance, g);
                __state = __instance.GetCurrentSolves(g.Counter);
                return true;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals stamp: {e.Message}"); return true; }
        }

        static void Postfix(QuestManager __instance, string questFormat, int __state)
        {
            if (__state < 0) return;
            try
            {
                var g = FestivalGoals.ByCounter(questFormat);
                var p = __instance.Creature as Player;
                int now = __instance.GetCurrentSolves(g.Counter);
                if (p != null && now > __state) FestivalGoals.Say(p, FestivalGoals.Progress(g, now));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals stamp after: {e.Message}"); }
        }
    }

    /// <summary>
    /// A festival kill goal: ACE counts a kill task only once its quest is on the record (QuestManager.HandleKillTask
    /// returns on !HasQuest). These goals start themselves at the first festival kill, with a line saying what they
    /// are, and the count goes through Stamp above (gate and progress line) instead of ACE's "You have killed 7 ...".
    /// </summary>
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.HandleKillTask))]
    static class FestivalGoalsKill
    {
        static bool Prefix(QuestManager __instance, string killQuestName)
        {
            try
            {
                var g = FestivalGoals.ByCounter(killQuestName);
                if (g == null) return true;
                if (!(__instance.Creature is Player p) || !g.Kill) return false;
                if (!FestivalGoals.BeginKill(p, g)) return false;
                __instance.Stamp(g.Counter);
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals kill: {e.Message}"); return true; }
        }
    }

    /// <summary>
    /// The first festival kill. This ACE's Creature.OnDeath_HandleKillTask calls HandleKillTask only for a damager whose
    /// record already holds the kill quest, so the start in FestivalGoalsKill never ran and Harvest Hunt never counted
    /// (box shard 2026-10-22). Before ACE's pass, each player in the damage history (a pet counts for its owner) and
    /// their fellows in range get the counter started; ACE then counts it through HandleKillTask as usual.
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath_HandleKillTask))]
    static class FestivalGoalsFirstKill
    {
        static void Prefix(Creature __instance, string killQuest)
        {
            try
            {
                var g = FestivalGoals.ByCounter(killQuest);
                if (g == null || !g.Kill || __instance?.DamageHistory == null) return;
                foreach (var info in __instance.DamageHistory.TotalDamage.Values.ToList())
                {
                    if (info == null || info.TotalDamage <= 0) continue;
                    var p = info.TryGetAttacker() as Player ?? (info.PetOwner != null ? info.TryGetPetOwner() : null);
                    if (p == null) continue;
                    FestivalGoals.BeginKill(p, g);
                    if (p.Fellowship != null)
                        foreach (var fellow in p.Fellowship.WithinRange(p))
                            FestivalGoals.BeginKill(fellow, g);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals first kill: {e.Message}"); }
        }
    }

    /// <summary>Last year's unfinished (or unclaimed) counts are cleared at login, before any hand-in can read them.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class FestivalGoalsLogin
    {
        static void Postfix(Player __instance)
        {
            try { if (__instance?.QuestManager != null) FestivalGoals.ResetStale(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals login: {e.Message}"); }
        }
    }

    /// <summary>The Costumers are vendors: ACE's Vendor.ActOnUse opens the shop and never runs a Use emote. Their Use
    /// set is a goal's hand-in, so it runs first (a set that does not pay says nothing).</summary>
    [HarmonyPatch(typeof(Vendor), nameof(Vendor.ActOnUse), new Type[] { typeof(WorldObject) })]
    static class FestivalGoalsVendorUse
    {
        static void Prefix(Vendor __instance, WorldObject wo)
        {
            try
            {
                if (__instance == null || !FestivalGoals.VendorHandIns.Contains(__instance.WeenieClassId)) return;
                if (!(wo is Player p) || p.IsBusy || __instance.EmoteManager == null) return;
                __instance.EmoteManager.OnUse(p);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals vendor use: {e.Message}"); }
        }
    }

    /// <summary>A trophy is named for its year when it is made (any path: the hand-in's Give, @ci).</summary>
    [HarmonyPatch]
    static class FestivalGoalsTrophyName
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(WorldObjectFactory), nameof(WorldObjectFactory.CreateWorldObject), new[] { typeof(Weenie), typeof(ObjectGuid) });
            if (m == null) Mod.Log.Error("[RevivalGuard] FestivalGoals: WorldObjectFactory.CreateWorldObject(Weenie, ObjectGuid) not found; trophies carry no year");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Postfix(WorldObject __result)
        {
            if (__result == null || __result.WeenieClassId < 902200 || __result.WeenieClassId > 902205) return;
            try { FestivalGoals.NameTrophy(__result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] FestivalGoals trophy name: {e.Message}"); }
        }
    }
}
