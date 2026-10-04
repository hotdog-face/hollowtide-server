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
    /// A kill of a newcomer (under pk_honor_min_level) or of someone far beneath the killer earns
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
    ///
    /// State: PkLedger/ledger.json beside the dll (ModFolder); past seasons in PkLedger/seasons.json.
    /// </summary>
    internal static class PkRenown
    {
        internal const string P_SEASON = "pk_season_name";
        const int WANTED_AT = 5, KILL_LOG_KEEP = 2000, LADDER_SHOWN = 25;
        const long INFAMY_FADE = 86400;

        internal sealed class Rec
        {
            public string Name { get; set; }
            public long Renown { get; set; }
            public int Kills { get; set; }
            public int Deaths { get; set; }
            public int Streak { get; set; }
            public int Best { get; set; }
            public int Infamy { get; set; }
            public long InfamyAt { get; set; }
            public List<uint> Titles { get; set; } = new List<uint>();        // granted this season
            public List<uint> PendingTitles { get; set; } = new List<uint>(); // earned while offline
        }

        internal sealed class KillEntry
        {
            public long At { get; set; }
            public uint Killer { get; set; }
            public uint Victim { get; set; }
            public bool Counts { get; set; }    // counts toward the repeat rule
            public int Points { get; set; }
            public string Why { get; set; }
        }

        internal sealed class State
        {
            public string Season { get; set; }
            public long SeasonStarted { get; set; }
            public Dictionary<uint, Rec> Chars { get; set; } = new Dictionary<uint, Rec>();
            public List<KillEntry> Kills { get; set; } = new List<KillEntry>();
        }

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
            long since = PkCommon.Now() - (long)(PkCommon.Dbl(PkCommon.P_REPEAT_HOURS, 24) * 3600);
            lock (s_Lock) return s_State.Kills.Count(k => k.Counts && k.Killer == killer && k.Victim == victim && k.At >= since);
        }

        static void Score(Player killer, Player victim, PkCommon.Judgement j)
        {
            long now = PkCommon.Now();
            var lines = new List<(Player to, string line)>();
            var world = new List<string>();
            var titles = new List<(Player p, CharacterTitle t)>();
            lock (s_Lock)
            {
                var kr = Get(killer); var vr = Get(victim);
                bool wasLeader = Leader() == victim.Guid.Full;
                int victimStreak = vr.Streak;
                int victimInfamy = InfamyOf(vr, now);
                // a counted death ends the victim's streak, whatever the verdict (an ally's kill does not)
                if (j.Verdict != PkCommon.Verdict.NotCounted || j.Why.StartsWith("killed by you"))
                {
                    vr.Deaths++;
                    vr.Streak = 0;
                    if (victimStreak >= WANTED_AT)
                        world.Add($"{killer.Name} has ended {victim.Name}'s killing streak of {victimStreak}.");
                }

                int pts = 0;
                switch (j.Verdict)
                {
                    case PkCommon.Verdict.Honourable:
                    {
                        double ratio = Math.Clamp((double)(victim.Level ?? 1) / Math.Max(1, killer.Level ?? 1), 0.5, 2.0);
                        double raw = 10 * ratio;
                        var extra = new List<string>();
                        if (victimStreak >= 3) { raw += 2 * victimStreak; extra.Add($"ended a streak of {victimStreak}"); }
                        if (victimStreak >= WANTED_AT) { raw += 25; extra.Add("a Wanted player killer"); }
                        if (victimInfamy >= 3) { raw += 10; extra.Add("justice on a Murderer"); }
                        pts = Math.Max(1, (int)Math.Round(raw * j.Weight));
                        kr.Renown += pts; kr.Kills++; kr.Streak++;
                        kr.Best = Math.Max(kr.Best, kr.Streak);
                        string why = j.Weight < 1 ? (j.Attackers > 2 ? $", shared among {j.Attackers}" : ", less for a victim you killed today") : "";
                        lines.Add((killer, $"+{pts} renown for {victim.Name}{(extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : "")}{why}. Renown {kr.Renown}, streak {kr.Streak}."));
                        if (kr.Streak == WANTED_AT)
                            world.Add($"{killer.Name}{PkCommon.Of(killer)} has killed {WANTED_AT} player killers without falling and is now Wanted.");
                        else if (kr.Streak > WANTED_AT && kr.Streak % 5 == 0)
                            world.Add($"{killer.Name}'s killing streak reaches {kr.Streak}.");
                        foreach (var (at, t) in new[] { (100L, CharacterTitle.Duelist), (500L, CharacterTitle.Bloodletter), (1500L, CharacterTitle.DerethsGladiator) })
                            if (kr.Renown >= at && !kr.Titles.Contains((uint)t)) { kr.Titles.Add((uint)t); titles.Add((killer, t)); }
                        if (wasLeader && !kr.Titles.Contains((uint)CharacterTitle.Kingslayer))
                        { kr.Titles.Add((uint)CharacterTitle.Kingslayer); titles.Add((killer, CharacterTitle.Kingslayer)); }
                        break;
                    }
                    case PkCommon.Verdict.Infamous:
                    {
                        int inf = InfamyOf(kr, now) + 1;
                        kr.Infamy = inf; kr.InfamyAt = now;
                        kr.Streak = 0;
                        lines.Add((killer, $"No renown for killing {j.Why}. Your infamy is {inf}; it fades a point a day."));
                        if (inf >= 3 && !kr.Titles.Contains((uint)CharacterTitle.Murderer))
                        {
                            kr.Titles.Add((uint)CharacterTitle.Murderer); titles.Add((killer, CharacterTitle.Murderer));
                            world.Add($"{killer.Name} preys on the weak and is branded a Murderer. Killing them is justice.");
                        }
                        break;
                    }
                    default:
                        lines.Add((killer, $"That kill earns no renown: {j.Why}."));
                        break;
                }

                s_State.Kills.Add(new KillEntry
                {
                    At = now, Killer = killer.Guid.Full, Victim = victim.Guid.Full, Points = pts, Why = j.Why,
                    Counts = j.Verdict == PkCommon.Verdict.Honourable || (j.Why?.StartsWith("killed by you") ?? false),
                });
                if (s_State.Kills.Count > KILL_LOG_KEEP) s_State.Kills.RemoveRange(0, s_State.Kills.Count - KILL_LOG_KEEP);
                Save();
            }
            foreach (var (to, line) in lines) PkCommon.Say(to, line);
            foreach (var w in world) PkCommon.World(w);
            foreach (var (p, t) in titles) Grant(p, t);
        }

        static Rec Get(Player p)
        {
            if (!s_State.Chars.TryGetValue(p.Guid.Full, out var r)) s_State.Chars[p.Guid.Full] = r = new Rec();
            r.Name = p.Name;
            return r;
        }

        static int InfamyOf(Rec r, long now)
        {
            if (r.Infamy <= 0) return 0;
            long faded = Math.Max(0, (now - r.InfamyAt) / INFAMY_FADE);
            return (int)Math.Max(0, r.Infamy - faded);
        }

        /// <summary>The ladder's leader (guid), or 0.</summary>
        static uint Leader() =>
            s_State.Chars.Where(kv => kv.Value.Renown > 0).OrderByDescending(kv => kv.Value.Renown).ThenByDescending(kv => kv.Value.Kills)
                .Select(kv => kv.Key).FirstOrDefault();

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
            var args = parameters ?? new string[0];
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
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
                    if (hit.Value != null) { gone = hit.Value.Name; s_State.Chars.Remove(hit.Key); Save(); }
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
                var fresh = new State { Season = next, SeasonStarted = PkCommon.Now() };
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
                    File.WriteAllText(s_ArchivePath, JsonSerializer.Serialize(list));
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
