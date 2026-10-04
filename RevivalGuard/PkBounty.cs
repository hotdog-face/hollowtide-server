using System.Text;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE BOUNTY BOARD (docs/PK-EXPANSION.md, idea 3). Ours; retail never had one. Off until
    /// `pk_bounty_enabled` is true.
    ///
    /// A player puts pyreals on a player killer's head. The pyreals leave the poster's pack at once
    /// (escrow); the shard keeps pk_bounty_fee of them (10%, a pyreal sink), and the rest is paid to
    /// whoever makes an HONOURABLE kill of the target (PkCommon.Judge: not an ally, not the same
    /// account or connection, not a newcomer or someone far beneath the killer, not the third kill
    /// of the day). The poster's own account can never collect its own posting.
    ///
    /// WHAT KEEPS IT FROM BEING A GRIEFING TOOL OR A MONEY PUMP:
    ///   * only PKs of pk_bounty_min_target_level (50) and up can be targeted, never an NPK, never an
    ///     ally, never your own account;
    ///   * the total on one head is capped (pk_bounty_max_amount), one posting per poster per head;
    ///   * after a bounty is paid, nobody can post on that head for a day;
    ///   * a bounty that stands pk_bounty_days (7) unclaimed returns half of what was escrowed to the
    ///     poster; a cancelled one likewise: so posting and pulling costs money every time;
    ///   * the target is told a bounty stands on them, never by whom.
    /// Collusion (a friend "kills" the target and splits the money) is the classic failure; the
    /// honour check shuts out the same account, connection, allegiance and fellowship, and what is
    /// left costs the colluders a PK death's item drop every time, while the poster, not the shard,
    /// is the one paying.
    ///
    /// Payouts are made in trade notes and pyreals into the pack; anything that does not fit waits
    /// as money owed until `@bounty collect`.
    ///
    ///     @bounty                          the board (our client: the PK board's Bounties tab)
    ///     @bounty post <name> <pyreals>    post, or add to your posting
    ///     @bounty cancel <name>            withdraw yours (half comes back)
    ///     @bounty collect                  take what you are owed
    /// </summary>
    internal static class PkBounty
    {
        internal const string P_MIN_TARGET = "pk_bounty_min_target_level", P_MIN_AMOUNT = "pk_bounty_min_amount",
            P_MAX_AMOUNT = "pk_bounty_max_amount", P_FEE = "pk_bounty_fee", P_DAYS = "pk_bounty_days";
        const long COOLDOWN = 86400;
        const uint PYREAL = 273;
        // the trade notes, largest first (ace_world: Trade Note (250,000) .. (100), MaxStackSize 250)
        static readonly (uint wcid, long value)[] NOTES =
        {
            (20630, 250000), (2627, 100000), (2626, 50000), (2625, 10000), (2624, 5000), (2623, 1000), (2622, 500), (2621, 100),
        };

        internal sealed class Posting
        {
            public uint Poster { get; set; }
            public string PosterName { get; set; }
            public uint PosterAccount { get; set; }
            public long Amount { get; set; }      // what the killer will be paid (after the fee)
            public long At { get; set; }
            public long Expires { get; set; }
        }

        internal sealed class Target
        {
            public string Name { get; set; }
            public int Level { get; set; }
            public List<Posting> Posts { get; set; } = new List<Posting>();
        }

        internal sealed class State
        {
            public Dictionary<uint, Target> Targets { get; set; } = new Dictionary<uint, Target>();
            public Dictionary<uint, long> Owed { get; set; } = new Dictionary<uint, long>();
            public Dictionary<uint, long> Cooldown { get; set; } = new Dictionary<uint, long>();
            public List<string> Log { get; set; } = new List<string>();
            public List<Event> Recent { get; set; } = new List<Event>();
        }

        /// <summary>One finished bounty for the board's Recent list: a claim, an expiry or a
        /// withdrawal. Kept in bounties.json beside the ledger, the last RECENT_KEEP.</summary>
        internal sealed class Event
        {
            public long At { get; set; }
            public string Kind { get; set; }       // "claim" | "expire" | "withdraw"
            public string Target { get; set; }
            public string Posters { get; set; }    // "A" or "A, B" or "A and 3 others"
            public string Claimer { get; set; }    // claims only
            public long Amount { get; set; }       // paid out (claim) or the posting (expire, withdraw)
            public long Returned { get; set; }     // half back to the poster (expire, withdraw)
        }
        const int RECENT_KEEP = 50, RECENT_SHOWN = 12;

        static void Record(string kind, string target, IEnumerable<string> posters, string claimer, long amount, long returned)
        {
            var names = posters.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            string who = names.Count == 0 ? "" : names.Count <= 2 ? string.Join(", ", names) : $"{names[0]} and {names.Count - 1} others";
            s_State.Recent.Add(new Event { At = PkCommon.Now(), Kind = kind, Target = target, Posters = who, Claimer = claimer, Amount = amount, Returned = returned });
            if (s_State.Recent.Count > RECENT_KEEP) s_State.Recent.RemoveRange(0, s_State.Recent.Count - RECENT_KEEP);
        }

        static readonly object s_Lock = new object();
        static State s_State = new State();
        static string s_Path;

        internal static bool On => PkCommon.On(PkCommon.P_BOUNTY);

        internal static void Register()
        {
            Load();
            CommandManager.TryAddCommand(Command, "bounty", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "The bounty board: pyreals on a player killer's head.", "[post <name> <pyreals> | cancel <name> | collect]");
        }

        // ------------------------------------------------------------------ commands

        static void Command(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            if (!On) { PkCommon.Say(p, "The bounty board is not open on this world."); return; }
            var args = parameters ?? new string[0];
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                Expire();
                switch (verb)
                {
                    case "post": Post(p, args.Skip(1).ToArray()); break;
                    case "cancel": Cancel(p, string.Join(" ", args.Skip(1)).Trim()); break;
                    case "collect": Collect(p, false); break;
                    default: List(p); return;
                }
                if (ModChannel.Capable(session)) PkBoard.Send(session, false, null);
            }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] PkBounty: @bounty {string.Join(" ", args)} by {p.Name}: {e}");
                PkCommon.Say(p, "That did not work. It has been logged for staff.");
            }
        }

        static void List(Player p)
        {
            if (ModChannel.Capable(p.Session)) { PkBoard.Send(p.Session, true, "bounties"); return; }
            var rows = Snapshot();
            if (rows.Count == 0) PkCommon.Say(p, "No bounties stand. @bounty post <name> <pyreals> to post one.");
            else
            {
                PkCommon.Say(p, "--- Bounties ---");
                foreach (var (g, t, total) in rows.Take(20))
                    PkCommon.Say(p, $"{t.Name} (level {t.Level}): {PkCommon.Pyreals(total)} from {t.Posts.Count} {(t.Posts.Count == 1 ? "poster" : "posters")}");
            }
            long owed = OwedTo(p.Guid.Full);
            if (owed > 0) PkCommon.Say(p, $"You are owed {PkCommon.Pyreals(owed)}. @bounty collect to take it.");
        }

        static void Post(Player p, string[] args)
        {
            if (args.Length < 2 || !long.TryParse(args[^1].Replace(",", ""), out long amount))
            { PkCommon.Say(p, "Usage: @bounty post <name> <pyreals>"); return; }
            string name = string.Join(" ", args.Take(args.Length - 1)).Trim();
            var target = PlayerManager.FindByName(name);
            if (target == null) { PkCommon.Say(p, $"There is no one called {name}."); return; }
            uint tg = target.Guid.Full;
            if (tg == p.Guid.Full) { PkCommon.Say(p, "You cannot post a bounty on yourself."); return; }
            if (target.Account != null && p.Account != null && target.Account.AccountId == p.Account.AccountId)
            { PkCommon.Say(p, "You cannot post a bounty on a character of your own account."); return; }
            long now = PkCommon.Now();
            lock (s_Lock)
                if (s_State.Cooldown.TryGetValue(tg, out var cool) && now < cool)
                { PkCommon.Say(p, $"A bounty on {target.Name} was paid recently. No new bounty can be posted on them for {Math.Max(1, (cool - now + 3599) / 3600)} more hours."); return; }
            // A PK in the respite after a death reads as NPK for a few minutes (ACE PK_DeathTick);
            // they are still a player killer for the board.
            var status = (PlayerKillerStatus)(target.GetProperty(PropertyInt.PlayerKillerStatus) ?? (int)PlayerKillerStatus.NPK);
            bool respite = PlayerManager.GetOnlinePlayer(tg) is Player tp && tp.MinimumTimeSincePk != null;
            if (!status.HasFlag(PlayerKillerStatus.PK) && !respite) { PkCommon.Say(p, $"{target.Name} is not a player killer. Bounties are only for player killers."); return; }
            int level = target.Level ?? 1;
            long minLevel = PkCommon.Long(P_MIN_TARGET, 50);
            if (level < minLevel) { PkCommon.Say(p, $"{target.Name} is level {level}. Bounties start at level {minLevel}."); return; }
            uint pm = p.MonarchId ?? p.Guid.Full, tm = target.MonarchId ?? tg;
            if (pm == tm) { PkCommon.Say(p, $"{target.Name} is of your own allegiance."); return; }
            long min = PkCommon.Long(P_MIN_AMOUNT, 10000), max = PkCommon.Long(P_MAX_AMOUNT, 2000000);
            if (amount < min) { PkCommon.Say(p, $"The smallest bounty is {PkCommon.Pyreals(min)}."); return; }
            double fee = Math.Clamp(PkCommon.Dbl(P_FEE, 0.10), 0, 0.9);
            long net = (long)Math.Floor(amount * (1 - fee));
            lock (s_Lock)
            {
                long standing = s_State.Targets.TryGetValue(tg, out var t0) ? t0.Posts.Sum(x => x.Amount) : 0;
                if (standing + net > max) { PkCommon.Say(p, $"The bounty on {target.Name} is already {PkCommon.Pyreals(standing)}; the most one head can carry is {PkCommon.Pyreals(max)}."); return; }
            }
            if ((p.CoinValue ?? 0) < amount) { PkCommon.Say(p, $"You do not carry {PkCommon.Pyreals(amount)}. Bounties are posted in pyreals from your pack."); return; }
            if (!p.TryConsumeFromInventoryWithNetworking(PYREAL, (int)Math.Min(amount, int.MaxValue)))
            { PkCommon.Say(p, "The pyreals could not be taken from your pack. Nothing was posted."); return; }
            long total;
            lock (s_Lock)
            {
                if (!s_State.Targets.TryGetValue(tg, out var t)) s_State.Targets[tg] = t = new Target();
                t.Name = target.Name; t.Level = level;
                var mine = t.Posts.FirstOrDefault(x => x.Poster == p.Guid.Full);
                long expires = now + (long)(PkCommon.Dbl(P_DAYS, 7) * 86400);
                if (mine == null) t.Posts.Add(new Posting { Poster = p.Guid.Full, PosterName = p.Name, PosterAccount = p.Account?.AccountId ?? 0, Amount = net, At = now, Expires = expires });
                else { mine.Amount += net; mine.Expires = expires; }
                total = t.Posts.Sum(x => x.Amount);
                LogLine($"{p.Name} posted {amount:N0} ({net:N0} after the fee) on {target.Name}");
                Save();
            }
            PkCommon.Say(p, $"You put {PkCommon.Pyreals(amount)} on {target.Name}'s head. The shard keeps {PkCommon.Pyreals(amount - net)}; {PkCommon.Pyreals(net)} goes to whoever kills them honourably. It stands {PkCommon.Dbl(P_DAYS, 7):0.#} days.");
            var online = PlayerManager.GetOnlinePlayer(tg);
            if (online != null) PkCommon.Say(online, $"A bounty stands on your head: {PkCommon.Pyreals(total)}.");
            if (total >= 100000 && total - net < 100000) PkCommon.World($"A bounty of {PkCommon.Pyreals(total)} now stands on {target.Name}'s head.");
            Mod.Log.Info($"[RevivalGuard] PkBounty: {p.Name} posted {amount} ({net} net) on {target.Name} (0x{tg:X8}); total {total}");
        }

        static void Cancel(Player p, string name)
        {
            if (name.Length == 0) { PkCommon.Say(p, "Usage: @bounty cancel <name>"); return; }
            long back = 0; string who = null;
            lock (s_Lock)
            {
                foreach (var kv in s_State.Targets)
                {
                    if (!string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    var mine = kv.Value.Posts.FirstOrDefault(x => x.Poster == p.Guid.Full);
                    if (mine == null) break;
                    kv.Value.Posts.Remove(mine);
                    back = mine.Amount / 2; who = kv.Value.Name;
                    Owe(p.Guid.Full, back);
                    LogLine($"{p.Name} withdrew {mine.Amount:N0} on {who}; {back:N0} returned");
                    Record("withdraw", who, new[] { p.Name }, null, mine.Amount, back);
                    break;
                }
                Tidy();
                Save();
            }
            if (who == null) { PkCommon.Say(p, $"You have no bounty on {name}."); return; }
            PkCommon.Say(p, $"You withdraw your bounty on {who}. Half comes back to you: {PkCommon.Pyreals(back)}.");
            Collect(p, true);
        }

        // ------------------------------------------------------------------ the kill

        internal static void OnKill(Player killer, Player victim, PkCommon.Judgement j)
        {
            uint tg = victim.Guid.Full;
            List<Posting> paid;
            lock (s_Lock)
            {
                if (!s_State.Targets.TryGetValue(tg, out var t) || t.Posts.Count == 0) return;
                if (j.Verdict != PkCommon.Verdict.Honourable)
                {
                    PkCommon.Say(killer, $"The bounty on {victim.Name} is not paid for that kill: {j.Why}.");
                    return;
                }
                uint acct = killer.Account?.AccountId ?? 0;
                paid = t.Posts.Where(x => x.PosterAccount == 0 || x.PosterAccount != acct).ToList();
                if (paid.Count == 0) { PkCommon.Say(killer, $"The only bounty on {victim.Name} is your own."); return; }
                foreach (var x in paid) t.Posts.Remove(x);
                long sum = paid.Sum(x => x.Amount);
                Owe(killer.Guid.Full, sum);
                s_State.Cooldown[tg] = PkCommon.Now() + COOLDOWN;
                LogLine($"{killer.Name} claimed {sum:N0} on {victim.Name} from {paid.Count} posting(s)");
                Record("claim", victim.Name, paid.Select(x => x.PosterName), killer.Name, sum, 0);
                Tidy();
                Save();
            }
            long total = paid.Sum(x => x.Amount);
            PkCommon.World($"{killer.Name} has claimed the bounty of {PkCommon.Pyreals(total)} on {victim.Name}'s head.");
            foreach (var x in paid)
            {
                var poster = PlayerManager.GetOnlinePlayer(x.Poster);
                if (poster != null && poster != killer) PkCommon.Say(poster, $"Your bounty on {victim.Name} was claimed by {killer.Name}.");
            }
            try { killer.AddTitle(CharacterTitle.BountyHunter); } catch { }
            PkRenown.NoteTitle(killer, CharacterTitle.BountyHunter);
            Mod.Log.Info($"[RevivalGuard] PkBounty: {killer.Name} claimed {total} on {victim.Name} (0x{tg:X8})");
            Collect(killer, true);
        }

        internal static void AtLogin(Player p)
        {
            if (!On) return;
            long owed = OwedTo(p.Guid.Full);
            if (owed > 0) PkCommon.Say(p, $"You are owed {PkCommon.Pyreals(owed)} from the bounty board. @bounty collect to take it.");
        }

        // ------------------------------------------------------------------ money

        static void Owe(uint g, long amount)
        {
            if (amount <= 0) return;
            s_State.Owed[g] = (s_State.Owed.TryGetValue(g, out var had) ? had : 0) + amount;
        }

        static long OwedTo(uint g) { lock (s_Lock) return s_State.Owed.TryGetValue(g, out var v) ? v : 0; }

        /// <summary>Pay what is owed into the pack, notes first; what does not fit stays owed.</summary>
        static void Collect(Player p, bool quiet)
        {
            long owed;
            lock (s_Lock)
            {
                owed = s_State.Owed.TryGetValue(p.Guid.Full, out var v) ? v : 0;
                if (owed > 0) s_State.Owed.Remove(p.Guid.Full);   // taken out now; whatever is not delivered goes back
            }
            if (owed <= 0) { if (!quiet) PkCommon.Say(p, "You are owed nothing."); return; }
            long delivered = 0, left = owed;
            try
            {
                foreach (var (wcid, value) in NOTES)
                {
                    while (left >= value)
                    {
                        int n = (int)Math.Min(250, left / value);
                        var item = WorldObjectFactory.CreateNewWorldObject(wcid);
                        if (item == null) break;
                        item.SetStackSize(n);
                        if (!p.TryCreateInInventoryWithNetworking(item)) { item.Destroy(); goto done; }
                        delivered += n * value; left -= n * value;
                    }
                }
                while (left > 0)
                {
                    int n = (int)Math.Min(25000, left);
                    var coins = WorldObjectFactory.CreateNewWorldObject(PYREAL);
                    if (coins == null) break;
                    coins.SetStackSize(n);
                    if (!p.TryCreateInInventoryWithNetworking(coins)) { coins.Destroy(); break; }
                    delivered += n; left -= n;
                }
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkBounty: paying {p.Name}: {e.Message}"); }
            done:
            if (left > 0) lock (s_Lock) { Owe(p.Guid.Full, left); Save(); }
            else lock (s_Lock) Save();
            if (delivered > 0) PkCommon.Say(p, $"You receive {PkCommon.Pyreals(delivered)} from the bounty board.");
            if (left > 0) PkCommon.Say(p, $"{PkCommon.Pyreals(left)} did not fit in your pack. Make room, then @bounty collect.");
            Mod.Log.Info($"[RevivalGuard] PkBounty: paid {p.Name} {delivered}, still owed {left}");
        }

        /// <summary>Bounties past their time: half of each goes back to its poster.</summary>
        static void Expire()
        {
            long now = PkCommon.Now();
            lock (s_Lock)
            {
                bool changed = false;
                foreach (var t in s_State.Targets.Values)
                    foreach (var x in t.Posts.Where(x => x.Expires <= now).ToList())
                    {
                        t.Posts.Remove(x);
                        Owe(x.Poster, x.Amount / 2);
                        LogLine($"{x.PosterName}'s {x.Amount:N0} on {t.Name} expired; {x.Amount / 2:N0} owed back");
                        Record("expire", t.Name, new[] { x.PosterName }, null, x.Amount, x.Amount / 2);
                        changed = true;
                    }
                foreach (var g in s_State.Cooldown.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) { s_State.Cooldown.Remove(g); changed = true; }
                if (changed) { Tidy(); Save(); }
            }
        }

        static void Tidy()
        {
            foreach (var g in s_State.Targets.Where(kv => kv.Value.Posts.Count == 0).Select(kv => kv.Key).ToList()) s_State.Targets.Remove(g);
        }

        static void LogLine(string line)
        {
            s_State.Log.Add(DateTime.UtcNow.ToString("MM/dd HH:mm") + " " + line);
            if (s_State.Log.Count > 300) s_State.Log.RemoveRange(0, s_State.Log.Count - 300);
        }

        static List<(uint g, Target t, long total)> Snapshot()
        {
            lock (s_Lock)
                return s_State.Targets.Select(kv => (kv.Key, kv.Value, kv.Value.Posts.Sum(x => x.Amount)))
                    .OrderByDescending(x => x.Item3).ToList();
        }

        // ------------------------------------------------------------------ the board's data

        internal static void AppendJson(StringBuilder sb, Player asker)
        {
            Expire();
            long now = PkCommon.Now();
            sb.Append(",\"bounties\":[");
            int n = 0;
            foreach (var (g, t, total) in Snapshot().Take(40))
            {
                if (n++ > 0) sb.Append(',');
                var mine = t.Posts.FirstOrDefault(x => x.Poster == asker.Guid.Full);
                long exp = t.Posts.Count > 0 ? t.Posts.Max(x => x.Expires) : now;
                sb.Append("{\"n\":").Append(PkCommon.Q(t.Name)).Append(",\"l\":").Append(t.Level).Append(",\"amt\":").Append(total)
                  .Append(",\"posters\":").Append(t.Posts.Count).Append(",\"exp\":").Append(exp).Append(",\"mine\":").Append(mine?.Amount ?? 0)
                  .Append(",\"online\":").Append(PlayerManager.GetOnlinePlayer(g) != null ? 1 : 0).Append('}');
            }
            sb.Append("],\"recent\":[");
            lock (s_Lock)
            {
                int r = 0;
                for (int i = s_State.Recent.Count - 1; i >= 0 && r < RECENT_SHOWN; i--, r++)
                {
                    var e = s_State.Recent[i];
                    if (r > 0) sb.Append(',');
                    sb.Append("{\"at\":").Append(e.At).Append(",\"k\":").Append(PkCommon.Q(e.Kind)).Append(",\"t\":").Append(PkCommon.Q(e.Target))
                      .Append(",\"p\":").Append(PkCommon.Q(e.Posters)).Append(",\"c\":").Append(PkCommon.Q(e.Claimer ?? ""))
                      .Append(",\"amt\":").Append(e.Amount).Append(",\"ret\":").Append(e.Returned).Append('}');
                }
            }
            sb.Append("],\"owed\":").Append(OwedTo(asker.Guid.Full))
              .Append(",\"bmin\":").Append(PkCommon.Long(P_MIN_AMOUNT, 10000)).Append(",\"bmax\":").Append(PkCommon.Long(P_MAX_AMOUNT, 2000000))
              .Append(",\"blevel\":").Append(PkCommon.Long(P_MIN_TARGET, 50))
              .Append(",\"fee\":").Append(PkCommon.Dbl(P_FEE, 0.10).ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"days\":").Append(PkCommon.Dbl(P_DAYS, 7).ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"coins\":").Append(asker.CoinValue ?? 0);
        }

        // ------------------------------------------------------------------ the file

        static void Load()
        {
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir == null) return;
                s_Path = Path.Combine(dir, "bounties.json");
                if (File.Exists(s_Path)) s_State = JsonSerializer.Deserialize<State>(File.ReadAllText(s_Path)) ?? new State();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkBounty: cannot read {s_Path}: {e.Message}; starting empty"); }
        }

        static void Save()
        {
            if (s_Path == null) return;
            try
            {
                var tmp = s_Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_State));
                File.Move(tmp, s_Path, true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkBounty: cannot write {s_Path}: {e.Message}"); }
        }
    }
}
