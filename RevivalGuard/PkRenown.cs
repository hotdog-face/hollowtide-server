using System.Text;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PK RENOWN: THE LADDER, KILL STREAKS, WANTED AND SEASON TITLES (docs/PK-EXPANSION.md, idea 2).
    /// Ours. Off until `pk_renown_enabled` is true.
    ///
    /// Every PK kill of a PK is judged by PkCommon.Judge. An honourable kill earns renown:
    ///     10 x (victim level / killer level, held to 0.5..2), divided by 1 + the times this killer
    ///     has already killed this victim today, and shared out when more than two players did the
    ///     damage; plus 2 per kill of a streak the victim had running (3 or more), 25 for a victim
    ///     who was Wanted, and 10 for a victim carrying infamy (justice).
    /// A kill of or by a Hollow Company mercenary is no kill at all: no renown, no infamy, no death
    /// (owner 2026-10-22, "players only").
    /// A kill of a newcomer (under pk_honor_min_level, 15) or of someone far beneath the killer earns
    /// INFAMY instead, one point that fades after a day each; three and the killer is branded with
    /// retail's own "Murderer" title and is worth justice renown to anyone who kills them.
    /// Nothing is ever taken from the victim: renown only grows, so a weak player is never pushed
    /// down the ladder by the strong (retail PK's death spiral is what drove players off Darktide).
    ///
    /// Five honourable kills without dying makes a player WANTED (a world line); ending a streak
    /// of five or more is announced too. Titles, all retail's (ACE CharacterTitle): Duelist at 100
    /// renown in a season, Bloodletter at 500, Dereth's Gladiator at 1,500, Kingslayer for killing
    /// the ladder's leader, Murderer for infamy; at a season's end the leader is Warlord of Dereth
    /// and second and third are Gladiator Champion.
    ///
    ///     @pkladder                 the ladder (our client: the PK board window)
    ///     @pkladder me              your record this season
    ///     @pkladder season <name>   (admin) end the season: titles, archive, a fresh ladder
    ///     @pkladder rejudge [apply] (admin) judge the season again under today's rules (a preview, then apply);
    ///                               it also runs by itself, once, the first time the ladder is used after a
    ///                               deploy that raised RULES (PkRules.cs)
    ///
    /// State: PkLedger/ledger.json beside the dll (ModFolder); past seasons in PkLedger/seasons.json.
    /// </summary>
    internal static partial class PkRenown
    {
        internal const string P_SEASON = "pk_season_name";
        const int LADDER_SHOWN = 25;

        // Rec, KillEntry, State, the scoring (Apply) and the re-judge are in PkRules.cs (pure C#, tested by NetTest).

        internal sealed class SeasonArchive
        {
            public string Season { get; set; }
            public long Ended { get; set; }
            public List<string> Top { get; set; } = new List<string>();
        }

        static readonly object s_Lock = new object();
        static State s_State = new State();
        static string s_Path, s_ArchivePath;

        internal static bool On => PkCommon.On(PkCommon.P_RENOWN);

        internal static void Register()
        {
            Load();
            CheckTitleIds();
            CommandManager.TryAddCommand(Command, "pkladder", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The player-killer renown ladder.", "[me | season <name>]");
        }

        // ------------------------------------------------------------------ the kill

        /// <summary>ACE's own PK-death step (Player_Death.cs HandlePKDeathBroadcast): the victim is
        /// still PK and topDamager is the player ACE credits with the kill.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandlePKDeathBroadcast))]
        static class OnPkDeath
        {
            static void Postfix(Player __instance, DamageHistoryInfo topDamager)
            {
                try
                {
                    if (!On && !PkBounty.On) return;
                    if (topDamager == null || !topDamager.IsPlayer || !__instance.IsPKDeath(topDamager)) return;
                    var killer = topDamager.TryGetAttacker() as Player;
                    if (killer == null || killer == __instance) return;
                    int attackers = 1;
                    try
                    {
                        attackers = __instance.DamageHistory.Damagers
                            .Count(d => d.IsPlayer && d.Guid != __instance.Guid && d.TotalDamage > 0);
                    }
                    catch { }
                    int prior = PriorKills(killer.Guid.Full, __instance.Guid.Full);
                    var j = PkCommon.Judge(killer, __instance, attackers, prior);
                    Mod.Log.Info($"[RevivalGuard] PK kill: {killer.Name} (L{killer.Level}) killed {__instance.Name} (L{__instance.Level}), "
                        + $"{j.Verdict}{(j.Why != null ? " (" + j.Why + ")" : "")}, attackers {j.Attackers}, prior {prior}");
                    if (On) Score(killer, __instance, j);
                    if (PkBounty.On) PkBounty.OnKill(killer, __instance, j);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkRenown: death of {__instance?.Name}: {e}"); }
            }
        }

        static int PriorKills(uint killer, uint victim)
        {
            EnsureRejudged();
            lock (s_Lock) return PriorKillsIn(s_State, killer, victim, PkCommon.Now(), PkCommon.Dbl(PkCommon.P_REPEAT_HOURS, PkCommon.DEFAULT_REPEAT_HOURS));
        }

        static void Score(Player killer, Player victim, PkCommon.Judgement j)
        {
            Outcome o;
            lock (s_Lock)
            {
                o = Apply(s_State, new KillIn
                {
                    Killer = killer.Guid.Full, Victim = victim.Guid.Full, KillerName = killer.Name, VictimName = victim.Name,
                    KillerLevel = killer.Level ?? 1, VictimLevel = victim.Level ?? 1, KillerOf = PkCommon.Of(killer), At = PkCommon.Now(),
                }, j);
                Save();
            }
            foreach (var line in o.KillerLines) PkCommon.Say(killer, line);
            foreach (var w in o.World) PkCommon.World(w);
            foreach (var (_, t) in o.Titles) Grant(killer, (CharacterTitle)t);
        }

        static Rec Get(Player p)
        {
            if (!s_State.Chars.TryGetValue(p.Guid.Full, out var r)) s_State.Chars[p.Guid.Full] = r = new Rec();
            r.Name = p.Name;
            return r;
        }

        /// <summary>The PkRules title ids must be ACE's own; a mismatch is logged, never thrown.</summary>
        static void CheckTitleIds()
        {
            var want = new (uint id, CharacterTitle t)[]
            {
                (T_BLOODLETTER, CharacterTitle.Bloodletter), (T_DUELIST, CharacterTitle.Duelist), (T_KINGSLAYER, CharacterTitle.Kingslayer),
                (T_WARLORD, CharacterTitle.WarlordofDereth), (T_BOUNTY_HUNTER, CharacterTitle.BountyHunter), (T_GLADIATOR, CharacterTitle.DerethsGladiator),
                (T_CHAMPION, CharacterTitle.GladiatorChampion), (T_MURDERER, CharacterTitle.Murderer),
            };
            foreach (var (id, t) in want)
                if ((uint)t != id) Mod.Log.Warn($"[RevivalGuard] PkRenown: title {t} is {(uint)t} in ACE, {id} in PkRules.cs");
        }

        // ------------------------------------------------------------------ the re-judge

        sealed class AceWorld : IWorld
        {
            public bool? IsMerc(uint guid)
            {
                var p = PlayerManager.FindByGuid(guid);
                return p == null ? (bool?)null : Mercenaries.IsMercAccount(p.Account?.AccountName);
            }
            public int? LevelNow(uint guid) => PlayerManager.FindByGuid(guid)?.Level;
        }

        static bool s_RejudgeChecked;

        /// <summary>A ledger kept under older rules is judged again once, the first time the ladder is used after
        /// the deploy (a kill, a command, the board, a login): by then the world is open and every character can
        /// be looked up. The old file is kept beside it as ledger.json.before-rules-N.</summary>
        static void EnsureRejudged()
        {
            if (s_RejudgeChecked) return;
            RejudgeResult res = null;
            lock (s_Lock)
            {
                if (s_RejudgeChecked) return;
                s_RejudgeChecked = true;
                if (s_State.Rules >= RULES) return;
                if (s_State.Kills.Count == 0) { s_State.Rules = RULES; return; }
                try
                {
                    res = RunRejudge();
                    if (s_Path != null && File.Exists(s_Path)) File.Copy(s_Path, s_Path + ".before-rules-" + RULES, true);
                    s_State = res.Fresh;
                    Save();
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkRenown: re-judge failed, the ladder is unchanged: {e}"); return; }
            }
            LogRejudge(res, "automatic, rules " + RULES);
            HandOverTitles(res);
        }

        static RejudgeResult RunRejudge() =>
            Rejudge(s_State, new AceWorld(), PkCommon.Long(PkCommon.P_MIN_LEVEL, PkCommon.DEFAULT_MIN_LEVEL),
                PkCommon.Dbl(PkCommon.P_RATIO, PkCommon.DEFAULT_RATIO), PkCommon.Dbl(PkCommon.P_REPEAT_HOURS, PkCommon.DEFAULT_REPEAT_HOURS), PkCommon.Now());

        static void LogRejudge(RejudgeResult res, string why)
        {
            Mod.Log.Info($"[RevivalGuard] PkRenown: season re-judged ({why}): {res.Kills} kills, {res.Changed} changed, {res.Mercs} with a mercenary, "
                + $"{res.Skipped} of removed characters left alone, {res.LevelsFromToday} judged with the killer's level today");
            foreach (var l in res.Lines) Mod.Log.Info("[RevivalGuard] PkRenown: re-judge " + l);
            foreach (var (_, n, t) in res.Taken) Mod.Log.Info($"[RevivalGuard] PkRenown: re-judge: {n} loses {TitleWord(t)} from the ladder (earned only from mercenary kills)");
            foreach (var (_, n, t) in res.Granted) Mod.Log.Info($"[RevivalGuard] PkRenown: re-judge: {n} earns {TitleWord(t)}");
        }

        /// <summary>Titles the re-judge gives: now to a player online, else at the next login (PendingTitles).</summary>
        static void HandOverTitles(RejudgeResult res)
        {
            foreach (var g in res.Granted.Select(x => x.guid).Distinct())
            {
                var o = PlayerManager.GetOnlinePlayer(g);
                if (o == null) continue;
                List<uint> pending;
                lock (s_Lock)
                {
                    if (!s_State.Chars.TryGetValue(g, out var r) || r.PendingTitles.Count == 0) continue;
                    pending = r.PendingTitles.ToList(); r.PendingTitles.Clear(); Save();
                }
                foreach (var t in pending) Grant(o, (CharacterTitle)t);
            }
        }

        /// <summary>A PK title granted elsewhere (the bounty board's Bounty Hunter), so the ladder row shows it.</summary>
        internal static void NoteTitle(Player p, CharacterTitle t)
        {
            if (!On || p == null) return;
            lock (s_Lock)
            {
                var r = Get(p);
                if (r.Titles.Contains((uint)t)) return;
                r.Titles.Add((uint)t);
                Save();
            }
        }

        internal static bool IsWanted(uint guid) { lock (s_Lock) return s_State.Chars.TryGetValue(guid, out var r) && r.Streak >= WANTED_AT; }

        static void Grant(Player p, CharacterTitle t)
        {
            try { p.AddTitle(t); PkCommon.Say(p, $"You have earned the title {TitleName(t)}."); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkRenown: title {t} for {p.Name}: {e.Message}"); }
        }

        /// <summary>The PK titles in the order a ladder row lists them.</summary>
        static readonly CharacterTitle[] ORDER =
        {
            CharacterTitle.WarlordofDereth, CharacterTitle.GladiatorChampion, CharacterTitle.DerethsGladiator,
            CharacterTitle.Bloodletter, CharacterTitle.Duelist, CharacterTitle.Kingslayer, CharacterTitle.BountyHunter,
            CharacterTitle.Murderer,
        };

        static string TitleName(CharacterTitle t) => t switch
        {
            CharacterTitle.DerethsGladiator => "Dereth's Gladiator",
            CharacterTitle.WarlordofDereth => "Warlord of Dereth",
            CharacterTitle.GladiatorChampion => "Gladiator Champion",
            CharacterTitle.BountyHunter => "Bounty Hunter",
            _ => t.ToString(),
        };

        /// <summary>Titles earned while offline (a season's end), handed over at the next login.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class AtLogin
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    if (On) EnsureRejudged();
                    List<uint> pending = null;
                    lock (s_Lock)
                        if (s_State.Chars.TryGetValue(__instance.Guid.Full, out var r) && r.PendingTitles.Count > 0)
                        { pending = r.PendingTitles.ToList(); r.PendingTitles.Clear(); Save(); }
                    if (pending != null) foreach (var t in pending) Grant(__instance, (CharacterTitle)t);
                    PkBounty.AtLogin(__instance);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkRenown: login of {__instance?.Name}: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ the command

        static void Command(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            if (!On) { PkCommon.Say(p, "The renown ladder is not open on this world."); return; }
            EnsureRejudged();
            var args = parameters ?? new string[0];
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (verb == "rejudge")
            {
                if (session.AccessLevel < AccessLevel.Admin) { PkCommon.Say(p, "Only an admin can judge the season again."); return; }
                Rejudge(p, args.Length > 1 && args[1].ToLowerInvariant() == "apply");
                return;
            }
            if (verb == "season")
            {
                if (session.AccessLevel < AccessLevel.Admin) { PkCommon.Say(p, "Only an admin can end a season."); return; }
                string name = string.Join(" ", args.Skip(1)).Trim();
                if (name.Length == 0) { PkCommon.Say(p, "Usage: @pkladder season <name of the new season>"); return; }
                EndSeason(p, name);
                return;
            }
            // @pkladder remove <name>: take one character's record off this season's ladder (admin).
            // The ladder is live state beside the dll, and test characters that fight on the live
            // shard (the 2026-09-30 death sweep's +Deathsweep Alpha, rank 1 after one test kill) had no
            // way off it short of ending the season for everyone.
            if (verb == "remove")
            {
                if (session.AccessLevel < AccessLevel.Admin) { PkCommon.Say(p, "Only an admin can remove a ladder record."); return; }
                string who = string.Join(" ", args.Skip(1)).Trim().TrimStart('+');
                if (who.Length == 0) { PkCommon.Say(p, "Usage: @pkladder remove <character name>"); return; }
                string gone = null;
                lock (s_Lock)
                {
                    var hit = s_State.Chars.FirstOrDefault(kv => string.Equals((kv.Value.Name ?? "").TrimStart('+'), who, StringComparison.OrdinalIgnoreCase));
                    if (hit.Value != null)
                    {
                        gone = hit.Value.Name; s_State.Chars.Remove(hit.Key);
                        s_State.Removed ??= new Dictionary<uint, long>();
                        s_State.Removed[hit.Key] = PkCommon.Now();   // a re-judge leaves their kills so far off the ladder
                        Save();
                    }
                }
                PkCommon.Say(p, gone != null ? $"{gone} is off the {Season()} ladder." : $"No ladder record is named {who}.");
                if (gone != null) Mod.Log.Info($"[RevivalGuard] PkRenown: {p.Name} removed {gone} from the ladder");
                return;
            }
            if (verb == "me") { Me(p); return; }
            if (ModChannel.Capable(session)) { PkBoard.Send(session, true, "ladder"); return; }
            var (rows, _) = Ladder(p.Guid.Full);
            PkCommon.Say(p, $"--- {Season()}: the renown ladder ---");
            if (rows.Count == 0) PkCommon.Say(p, "No player killer has earned renown yet this season.");
            int i = 0;
            foreach (var (g, r) in rows.Take(10))
                PkCommon.Say(p, $"{++i}. {r.Name}: {r.Renown} renown, {PkCommon.N(r.Kills, "kill")}, streak {r.Streak}{(r.Streak >= WANTED_AT ? " (Wanted)" : "")}");
            Me(p);
        }

        /// <summary>@pkladder rejudge: what judging the season again under today's rules would change (the top ten
        /// before and after, and the titles); `apply` does it, keeping the old file beside it.</summary>
        static void Rejudge(Player admin, bool apply)
        {
            RejudgeResult res;
            List<string> before;
            lock (s_Lock)
            {
                before = Top(s_State, 10);
                res = RunRejudge();
                if (apply)
                {
                    try { if (s_Path != null && File.Exists(s_Path)) File.Copy(s_Path, s_Path + ".before-rejudge-" + PkCommon.Now(), true); }
                    catch (Exception e) { PkCommon.Say(admin, $"Could not keep a copy of the ledger ({e.Message}); nothing changed."); return; }
                    s_State = res.Fresh;
                    Save();
                }
            }
            var after = Top(res.Fresh, 10);
            PkCommon.Say(admin, $"{(apply ? "Judged again" : "Preview")}: {res.Kills} kills, {res.Changed} judged differently, {res.Mercs} with a mercenary"
                + $"{(res.LevelsFromToday > 0 ? $", {res.LevelsFromToday} judged with the killer's level today" : "")}.");
            PkCommon.Say(admin, "Before: " + (before.Count > 0 ? string.Join("; ", before) : "nobody"));
            PkCommon.Say(admin, "After: " + (after.Count > 0 ? string.Join("; ", after) : "nobody"));
            foreach (var (_, n, t) in res.Taken) PkCommon.Say(admin, $"{n} loses {TitleWord(t)} from the ladder (only mercenary kills earned it).");
            foreach (var (_, n, t) in res.Granted) PkCommon.Say(admin, $"{n} earns {TitleWord(t)}.");
            if (!apply) PkCommon.Say(admin, "Nothing has changed yet. @pkladder rejudge apply does it.");
            LogRejudge(res, apply ? $"{admin.Name}, applied" : $"{admin.Name}, preview");
            if (apply) HandOverTitles(res);
        }

        static List<string> Top(State s, int n) =>
            s.Chars.Where(kv => kv.Value.Renown > 0).OrderByDescending(kv => kv.Value.Renown).ThenByDescending(kv => kv.Value.Kills).ThenBy(kv => kv.Value.Name)
                .Take(n).Select((kv, i) => $"{i + 1}. {kv.Value.Name} {kv.Value.Renown}").ToList();

        static void Me(Player p)
        {
            var (rows, rank) = Ladder(p.Guid.Full);
            Rec r;
            lock (s_Lock) s_State.Chars.TryGetValue(p.Guid.Full, out r);
            if (r == null) { PkCommon.Say(p, "You have no renown this season yet."); return; }
            int inf = InfamyOf(r, PkCommon.Now());
            PkCommon.Say(p, $"You: {(rank > 0 ? "rank " + rank + ", " : "")}{r.Renown} renown, {PkCommon.N(r.Kills, "kill")}, {PkCommon.N(r.Deaths, "death")}, streak {r.Streak} (best {r.Best}){(inf > 0 ? $", infamy {inf}" : "")}.");
        }

        internal static string Season()
        {
            lock (s_Lock) return s_State.Season ?? PkCommon.Str(P_SEASON, "Season 1");
        }

        /// <summary>The ladder, best first, and the asker's rank (0 = unranked).</summary>
        internal static (List<(uint g, Rec r)> rows, int rank) Ladder(uint asker)
        {
            lock (s_Lock)
            {
                var rows = s_State.Chars.Where(kv => kv.Value.Renown > 0)
                    .OrderByDescending(kv => kv.Value.Renown).ThenByDescending(kv => kv.Value.Kills).ThenBy(kv => kv.Value.Name)
                    .Select(kv => (kv.Key, kv.Value)).ToList();
                int rank = rows.FindIndex(x => x.Key == asker) + 1;
                return (rows, rank);
            }
        }

        static void EndSeason(Player admin, string next)
        {
            var (rows, _) = Ladder(0);
            string ended = Season();
            var archive = new SeasonArchive { Season = ended, Ended = PkCommon.Now() };
            for (int i = 0; i < rows.Count && i < 10; i++) archive.Top.Add($"{i + 1}. {rows[i].r.Name}: {rows[i].r.Renown} renown, {PkCommon.N(rows[i].r.Kills, "kill")}");
            var awards = new List<(uint g, string name, CharacterTitle t)>();
            if (rows.Count > 0) awards.Add((rows[0].g, rows[0].r.Name, CharacterTitle.WarlordofDereth));
            for (int i = 1; i < Math.Min(3, rows.Count); i++) awards.Add((rows[i].g, rows[i].r.Name, CharacterTitle.GladiatorChampion));
            lock (s_Lock)
            {
                var fresh = new State { Season = next, SeasonStarted = PkCommon.Now(), Rules = RULES };
                // The new season opens with last season's winners carrying their title on the ladder
                // (a row appears once they earn renown); an offline winner gets it at the next login.
                foreach (var (g, name, t) in awards)
                {
                    var online = PlayerManager.GetOnlinePlayer(g);
                    var rec = new Rec { Name = name, Titles = new List<uint> { (uint)t } };
                    if (online == null) rec.PendingTitles.Add((uint)t);
                    fresh.Chars[g] = rec;
                }
                s_State = fresh;
                Save();
                try
                {
                    var list = File.Exists(s_ArchivePath) ? JsonSerializer.Deserialize<List<SeasonArchive>>(File.ReadAllText(s_ArchivePath)) ?? new List<SeasonArchive>() : new List<SeasonArchive>();
                    list.Add(archive);
                    var tmp = s_ArchivePath + ".tmp";   // temp then move, like the state file
                    File.WriteAllText(tmp, JsonSerializer.Serialize(list));
                    File.Move(tmp, s_ArchivePath, true);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkRenown: season archive: {e.Message}"); }
            }
            foreach (var (g, _, t) in awards) { var o = PlayerManager.GetOnlinePlayer(g); if (o != null) Grant(o, t); }
            PkCommon.World(rows.Count > 0
                ? $"The PK season {ended} is over. {rows[0].r.Name} ends it as Warlord of Dereth with {rows[0].r.Renown} renown. {next} begins."
                : $"The PK season {ended} is over. {next} begins.");
            Mod.Log.Info($"[RevivalGuard] PkRenown: {admin.Name} ended {ended}; {next} begins; top: {string.Join(" | ", archive.Top)}");
        }

        // ------------------------------------------------------------------ the board's data

        internal static void AppendJson(StringBuilder sb, uint asker)
        {
            EnsureRejudged();
            var (rows, rank) = Ladder(asker);
            long now = PkCommon.Now();
            sb.Append(",\"season\":").Append(PkCommon.Q(Season())).Append(",\"ladder\":[");
            int n = 0;
            foreach (var (g, r) in rows.Take(LADDER_SHOWN))
            {
                if (n++ > 0) sb.Append(',');
                Row(sb, g, r, n, now);
            }
            sb.Append("],\"wanted\":[");
            n = 0;
            lock (s_Lock)
                foreach (var kv in s_State.Chars.Where(kv => kv.Value.Streak >= WANTED_AT).OrderByDescending(kv => kv.Value.Streak))
                {
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"n\":").Append(PkCommon.Q(kv.Value.Name)).Append(",\"s\":").Append(kv.Value.Streak).Append('}');
                }
            sb.Append(']');
            Rec me;
            lock (s_Lock) s_State.Chars.TryGetValue(asker, out me);
            if (me != null) { sb.Append(",\"me\":"); Row(sb, asker, me, rank, now); }
        }

        static void Row(StringBuilder sb, uint g, Rec r, int rank, long now)
        {
            sb.Append("{\"rank\":").Append(rank).Append(",\"n\":").Append(PkCommon.Q(r.Name)).Append(",\"r\":").Append(r.Renown)
              .Append(",\"k\":").Append(r.Kills).Append(",\"d\":").Append(r.Deaths).Append(",\"s\":").Append(r.Streak)
              .Append(",\"b\":").Append(r.Best).Append(",\"i\":").Append(InfamyOf(r, now)).Append(",\"t\":[");
            // the PK titles earned this season, highest first (retail's names, as the title list shows them)
            int n = 0;
            foreach (var id in ORDER.Where(o => r.Titles.Contains((uint)o)))
            {
                if (n++ > 0) sb.Append(',');
                sb.Append(PkCommon.Q(TitleName(id)));
            }
            sb.Append("]}");
        }

        // ------------------------------------------------------------------ the file

        static void Load()
        {
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir == null) return;
                s_Path = Path.Combine(dir, "ledger.json");
                s_ArchivePath = Path.Combine(dir, "seasons.json");
                if (File.Exists(s_Path)) s_State = JsonSerializer.Deserialize<State>(File.ReadAllText(s_Path)) ?? new State();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkRenown: cannot read {s_Path}: {e.Message}; starting empty"); }
        }

        static void Save()
        {
            if (s_Path == null) return;
            try
            {
                if (s_State.Season == null) { s_State.Season = PkCommon.Str(P_SEASON, "Season 1"); s_State.SeasonStarted = PkCommon.Now(); }
                var tmp = s_Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_State));
                File.Move(tmp, s_Path, true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkRenown: cannot write {s_Path}: {e.Message}"); }
        }
    }

    /// <summary>
    /// THE PK BOARD: the ladder, the bounties and the shrines in one window of our client
    /// (Assets/Streaming/AcPkBoardHud.cs), sent over the mod channel ("pkboard", JSON). The window
    /// exists only when the shard sends it one: nothing is sent unless a PK feature is on, so with
    /// all three off our client shows nothing new at all. `@pkboard` (or /pkboard) asks for it;
    /// a retail client gets the same as chat lines from @pkladder, @bounty and @shrines.
    /// </summary>
    internal static class PkBoard
    {
        internal static void Register()
        {
            CommandManager.TryAddCommand(Command, "pkboard", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The player-killer board: renown ladder, bounties and Shadow Shrines.", "[ladder | bounties | shrines]");
        }

        static void Command(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            string tab = parameters != null && parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            bool refresh = tab == "refresh";
            if (!PkCommon.AnyOn)
            {
                // An open window asking for its refresh after the features were turned off is told
                // to close, quietly; anyone else gets the one line.
                if (refresh) { if (ModChannel.Capable(session)) ModChannel.Send(session, "pkboard", "{\"open\":0,\"on\":{\"renown\":0,\"bounty\":0,\"shrines\":0}}"); return; }
                PkCommon.Say(p, "The player-killer board is not open on this world.");
                return;
            }
            if (!ModChannel.Capable(session))
            {
                if (PkRenown.On) PkCommon.Say(p, "@pkladder shows the renown ladder.");
                if (PkBounty.On) PkCommon.Say(p, "@bounty lists the bounties.");
                if (PkShrines.On) PkCommon.Say(p, "@shrines shows who holds the Shadow Shrines.");
                return;
            }
            Send(session, !refresh, refresh ? null : tab);
        }

        /// <summary>The board's data to one connection; `open` asks the client to show the window
        /// (on the named tab), a refresh only updates it if it is already open.</summary>
        internal static void Send(Session session, bool open, string tab)
        {
            var p = session?.Player;
            if (p == null || !ModChannel.Capable(session) || !PkCommon.AnyOn) return;
            var sb = new StringBuilder("{\"open\":").Append(open ? 1 : 0).Append(",\"now\":").Append(PkCommon.Now());
            if (!string.IsNullOrEmpty(tab)) sb.Append(",\"tab\":").Append(PkCommon.Q(tab));
            sb.Append(",\"on\":{\"renown\":").Append(PkRenown.On ? 1 : 0).Append(",\"bounty\":").Append(PkBounty.On ? 1 : 0)
              .Append(",\"shrines\":").Append(PkShrines.On ? 1 : 0).Append('}');
            if (PkRenown.On) PkRenown.AppendJson(sb, p.Guid.Full);
            if (PkBounty.On) PkBounty.AppendJson(sb, p);
            if (PkShrines.On) PkShrines.AppendJson(sb, p);
            sb.Append('}');
            ModChannel.Send(session, "pkboard", sb.ToString());
        }
    }
}
