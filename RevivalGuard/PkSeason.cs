using System.Globalization;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// DARKTIDE SEASON 1 ON THE WORLD LOOP (rules and words: PkSeasonRules.cs). Nothing happens until an admin plans a
    /// season (the admin panel's Events tab, "s1 plan"): with no PkLedger/schedule.json this is inert. A planned season
    /// opens by itself at its PK Night (the ladder so far is archived as the Preseason, no titles), announces a day and
    /// an hour before, posts the season's top three after every recorded PK Night round, warns a day before the end,
    /// and ends by itself after the last PK Night with the titles (EndSeason). Needs pk_renown_enabled (live: on).
    ///
    ///     @pkseason                    (admin) the plan and where it stands
    ///     @pkseason plan [yyyy-mm-dd] [nights]  plan Darktide Season 1 (default: Sunday 2026-12-06, 5 PK Nights to Jan 31)
    ///     @pkseason cancel             drop a plan that has not opened yet
    ///     @pkseason start              open the planned season now
    ///     @pkseason end                end the running season now, with its titles
    /// </summary>
    internal static partial class PkRenown
    {
        static PkSeasonRules.Schedule s_Plan;
        static string s_PlanPath;
        static double s_PlanNext;

        internal static void RegisterSeason()
        {
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir != null)
                {
                    s_PlanPath = Path.Combine(dir, "schedule.json");
                    if (File.Exists(s_PlanPath)) s_Plan = JsonSerializer.Deserialize<PkSeasonRules.Schedule>(File.ReadAllText(s_PlanPath));
                }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkSeason: cannot read {s_PlanPath}: {e.Message}; no season planned"); }
            CommandManager.TryAddCommand(SeasonCommand, "pkseason", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "The dated PK season (Darktide Season 1): plan, open, end.", "[plan [yyyy-mm-dd] [nights] | cancel | start | end]");
            Mod.Log.Info("[RevivalGuard] PkSeason: " + (s_Plan == null ? "no season planned" : PlanWords(s_Plan)));
        }

        static string PlanWords(PkSeasonRules.Schedule s) =>
            s.Ended ? $"{s.Name} is over"
            : $"{s.Name} {(s.Started ? "running since" : "opens")} {PkSeasonRules.NightWords(s.StartAt, false)}, ends after PK Night on "
              + $"{PkSeasonRules.NightWords(s.EndAt, true)} ({PkSeasonRules.Rounds(s)} PK Nights); then {s.NextName}";

        static void SavePlan()
        {
            if (s_PlanPath == null) return;
            try
            {
                if (s_Plan == null) { if (File.Exists(s_PlanPath)) File.Delete(s_PlanPath); return; }
                var tmp = s_PlanPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Plan));
                File.Move(tmp, s_PlanPath, true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkSeason: cannot write {s_PlanPath}: {e.Message}"); }
        }

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        static class SeasonTick
        {
            static void Postfix()
            {
                var now = ACE.Common.Time.GetUnixTime();
                if (now < s_PlanNext) return;
                s_PlanNext = now + 20;
                try { if (s_Plan != null && On) SeasonStep(PkCommon.Now()); }
                catch (Exception e) { TickGuard.Report("PkSeason", e); }
            }
        }

        static void SeasonStep(long now)
        {
            PkSeasonRules.Schedule s;
            PkSeasonRules.Step step;
            string notice;
            lock (s_Lock)
            {
                s = s_Plan;
                if (s == null) return;
                step = PkSeasonRules.Due(s, now, out notice);
                if (step == PkSeasonRules.Step.Notice) { s.Said.Add(notice); SavePlan(); }
            }
            switch (step)
            {
                case PkSeasonRules.Step.Start: OpenSeason("the schedule"); break;
                case PkSeasonRules.Step.End: CloseSeason("the schedule"); break;
                case PkSeasonRules.Step.Notice:
                    var top = TopRows(1);
                    PkCommon.World(notice switch
                    {
                        "open-day" => PkSeasonRules.OpenDayLine(s),
                        "open-hour" => PkSeasonRules.OpenHourLine(s),
                        _ => PkSeasonRules.EndDayLine(s, top.Count > 0 ? top[0].name : null, top.Count > 0 ? top[0].renown : 0),
                    });
                    Mod.Log.Info($"[RevivalGuard] PkSeason: told {notice} for {s.Name}");
                    break;
            }
        }

        static List<(string name, long renown)> TopRows(int n) => Ladder(0).rows.Take(n).Select(x => (x.r.Name, (long)x.r.Renown)).ToList();

        /// <summary>The opening: the ladder so far is archived as the Preseason without titles, the season's own begins.</summary>
        static void OpenSeason(string who)
        {
            PkSeasonRules.Schedule s;
            lock (s_Lock)
            {
                s = s_Plan;
                if (s == null || s.Started) return;
                s.Started = true;
                SavePlan();
            }
            EndSeason(who, s.Name, false, PkSeasonRules.PRESEASON);
            PkCommon.World(PkSeasonRules.OpenLine(s, PkTrophies.On));
            Mod.Log.Info($"[RevivalGuard] PkSeason: {s.Name} opened by {who}; {PlanWords(s)}");
        }

        static void CloseSeason(string who)
        {
            PkSeasonRules.Schedule s;
            lock (s_Lock)
            {
                s = s_Plan;
                if (s == null || !s.Started || s.Ended) return;
                s.Ended = true;
                SavePlan();
            }
            EndSeason(who, s.NextName, true);
            Mod.Log.Info($"[RevivalGuard] PkSeason: {s.Name} ended by {who}; {s.NextName} begins (no end date)");
        }

        /// <summary>PK Night's end (a recorded round): which of the season's PK Nights it was and the top three, while a season runs.</summary>
        internal static void SeasonFinale()
        {
            try
            {
                PkSeasonRules.Schedule s;
                lock (s_Lock) s = s_Plan;
                if (!On || s == null || !s.Started || s.Ended) return;
                PkCommon.World(PkSeasonRules.FinaleLine(s, PkCommon.Now(), TopRows(3)));
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkSeason: finale: {e.Message}"); }
        }

        /// <summary>The season's name for the PK board, with its end while it runs.</summary>
        internal static string SeasonLabel()
        {
            PkSeasonRules.Schedule s;
            lock (s_Lock) s = s_Plan;
            return PkSeasonRules.Label(Season(), s, PkCommon.Now());
        }

        static void SeasonCommand(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            var args = parameters ?? new string[0];
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (!On) { PkCommon.Say(p, "The renown ladder is off on this world (pk_renown_enabled), so no PK season can run."); return; }
            PkSeasonRules.Schedule s;
            lock (s_Lock) s = s_Plan;
            switch (verb)
            {
                case "plan":
                {
                    if (s != null && s.Started && !s.Ended) { PkCommon.Say(p, $"{s.Name} is running; end it first (@pkseason end)."); return; }
                    var sunday = PkSeasonRules.PROPOSED_SUNDAY;
                    int rounds = PkSeasonRules.PROPOSED_ROUNDS;
                    if (args.Length > 1 && !DateTime.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out sunday))
                    { PkCommon.Say(p, "Usage: @pkseason plan [yyyy-mm-dd, a PK Night Sunday] [PK Nights]"); return; }
                    if (args.Length > 2 && !int.TryParse(args[2], out rounds)) { PkCommon.Say(p, "Usage: @pkseason plan [yyyy-mm-dd] [PK Nights]"); return; }
                    PkSeasonRules.Schedule plan;
                    try { plan = PkSeasonRules.Plan(sunday, rounds, PkSeasonRules.PROPOSED_NAME, PkSeasonRules.PROPOSED_NEXT); }
                    catch (ArgumentException e) { PkCommon.Say(p, $"Not planned: {e.Message}."); return; }
                    if (plan.StartAt <= PkCommon.Now()) { PkCommon.Say(p, "Not planned: that PK Night has already begun. @pkseason start opens a season now."); return; }
                    lock (s_Lock) { s_Plan = plan; SavePlan(); }
                    PkCommon.Say(p, "Planned: " + PlanWords(plan) + ". Players are told a day and an hour before it opens.");
                    Mod.Log.Info($"[RevivalGuard] PkSeason: {p.Name} planned {PlanWords(plan)}");
                    return;
                }
                case "cancel":
                    if (s == null || s.Ended) { PkCommon.Say(p, "No season is planned."); return; }
                    if (s.Started) { PkCommon.Say(p, $"{s.Name} is already running; @pkseason end ends it with its titles."); return; }
                    lock (s_Lock) { s_Plan = null; SavePlan(); }
                    PkCommon.Say(p, $"{s.Name} is no longer planned. Nothing was announced past what players were already told.");
                    Mod.Log.Info($"[RevivalGuard] PkSeason: {p.Name} cancelled {s.Name}");
                    return;
                case "start":
                    if (s == null || s.Ended) { PkCommon.Say(p, "No season is planned (@pkseason plan first)."); return; }
                    if (s.Started) { PkCommon.Say(p, $"{s.Name} is already running."); return; }
                    lock (s_Lock) { s.StartAt = Math.Min(s.StartAt, PkCommon.Now()); SavePlan(); }
                    OpenSeason(p.Name);
                    PkCommon.Say(p, $"{s.Name} is open. The old ladder is archived as the {PkSeasonRules.PRESEASON}.");
                    return;
                case "end":
                    if (s == null || !s.Started || s.Ended) { PkCommon.Say(p, "No planned season is running."); return; }
                    CloseSeason(p.Name);
                    PkCommon.Say(p, $"{s.Name} is over; {s.NextName} runs with no end date.");
                    return;
                default:
                    PkCommon.Say(p, s == null ? $"No PK season is planned. The ladder's season now: {Season()}." : PlanWords(s) + ".");
                    if (s != null && s.Started && !s.Ended)
                        PkCommon.Say(p, PkSeasonRules.FinaleLine(s, PkCommon.Now(), TopRows(3)));
                    PkCommon.Say(p, $"Darktide Trophies: {(PkTrophies.On ? "ON" : "off")} (pk_trophies_enabled).");
                    return;
            }
        }
    }
}
