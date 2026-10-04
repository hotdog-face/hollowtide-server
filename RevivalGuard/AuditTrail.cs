using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// @AUDIT: THE AUDIT TRAIL YOU CAN READ IN GAME.
    ///
    /// From docs/ADMIN-AND-VENUE-IDEAS.md §2 item 4: "Every admin command already goes to the Audit
    /// channel; a searchable view of it is what you will want the first time two admins disagree
    /// about what happened." ACE's Audit channel is live chat only: whoever was not listening at the
    /// time has nothing, and BroadcastToAuditChannel's own log line is commented out
    /// (PlayerManager.cs). With full PvP and retail item loss on this shard, and admin verbs that
    /// move and destroy things, there was no record of who did what.
    ///
    /// THREE SEAMS, ALL POSTFIXES, NONE OF THEM CHANGE WHAT ACE DOES:
    ///
    ///   cmd     CommandManager.GetCommandHandler, the one place every command passes through: the
    ///           in-game path (GameActionTalk) and the console loop both call it before invoking the
    ///           handler. A command whose handler is above Player level is recorded once it is
    ///           authorised, with its parameters. Player-level verbs are not recorded.
    ///   denied  The same seam when the answer was NotAuthorized: somebody tried a staff verb they do
    ///           not hold. That only ever happens for staff verbs, so it is always worth a line.
    ///   audit   PlayerManager.BroadcastToAuditChannel: the line ACE's own verbs post about what they
    ///           did ("X has deleted 0x..:Name", "X used smite all"), plus ours (@reloadblock).
    ///   pk      Player.CalculateDeathItems, after PkDeathLoot's transpiler has shaped the list: a PK
    ///           or PKLite death, who killed whom, where, and every item that dropped. Normal deaths
    ///           are not recorded; ACE's [CORPSE] log line covers those.
    ///
    /// STORAGE. Audit/YYYY-MM.log beside the mod dll (ModFolder), one tab-separated line per entry:
    /// UTC time, kind, who, where (landblock or "console"), text. Append-only, flushed per entry, so
    /// a crash loses nothing and the owner can grep it over ssh. The last MEMORY entries (this month
    /// and last) are kept in memory for the verb; older months stay on disk.
    ///
    ///   @audit                     the last 20 entries
    ///   @audit 50                  the last 50 (up to 100; the chat window shows about 24 lines)
    ///   @audit smite               the last 20 whose text, who or kind contains "smite"
    ///   @audit Name teleto 40  every word must match; a bare number is the count
    ///   @audit file                where the files are
    ///
    /// A read of the trail is not itself recorded; browsing it would bury the entries. Sentinel
    /// level, the same as @who and @tickets. Admin command feedback is ours, not retail's.
    /// </summary>
    public static class AuditTrail
    {
        const int MEMORY = 5000;
        const int DEFAULT_SHOW = 20;
        const int MAX_SHOW = 100;
        const int MAX_TEXT = 400;

        sealed class Entry { public DateTime At; public string Kind, Who, Where, Text; }

        static readonly LinkedList<Entry> s_Entries = new LinkedList<Entry>();
        static readonly object s_Lock = new object();
        static string s_Folder;
        static DateTime s_WriteFailedAt;

        internal static void Register()
        {
            s_Folder = ModFolder.Sub("Audit");
            int loaded = 0;
            if (s_Folder != null)
            {
                var now = DateTime.UtcNow;
                loaded += LoadMonth(now.AddMonths(-1));
                loaded += LoadMonth(now);
            }
            CommandManager.TryAddCommand(Handle, "audit", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "The audit trail: staff commands, ACE's Audit channel lines and PK deaths, newest last. Words filter, a number is the count.",
                "[count] [words...] | file");
            Mod.Log.Info($"[RevivalGuard] AuditTrail: {loaded} entries in memory from {s_Folder ?? "(no folder; memory only)"}");
        }

        // ---- recording -------------------------------------------------------------------------

        /// <summary>One entry: to memory and to this month's file. Never throws; the trail must not
        /// be able to break the command it is watching.</summary>
        public static void Record(string kind, string who, string where, string text)
        {
            try
            {
                var e = new Entry
                {
                    At = DateTime.UtcNow,
                    Kind = Clean(kind ?? "?"),
                    Who = Clean(who ?? "?"),
                    Where = Clean(where ?? "?"),
                    Text = Clean(text ?? ""),
                };
                if (e.Text.Length > MAX_TEXT) e.Text = e.Text.Substring(0, MAX_TEXT) + "...";
                string line = $"{e.At:yyyy-MM-ddTHH:mm:ssZ}\t{e.Kind}\t{e.Who}\t{e.Where}\t{e.Text}\n";
                lock (s_Lock)
                {
                    s_Entries.AddLast(e);
                    while (s_Entries.Count > MEMORY) s_Entries.RemoveFirst();
                    if (s_Folder != null)
                    {
                        try { File.AppendAllText(MonthPath(e.At), line); }
                        catch (Exception ex)
                        {
                            // once a minute, not once per entry: a full disk must not flood the log
                            if ((DateTime.UtcNow - s_WriteFailedAt).TotalSeconds > 60)
                            {
                                s_WriteFailedAt = DateTime.UtcNow;
                                Mod.Log.Error($"[RevivalGuard] AuditTrail: cannot append to {MonthPath(e.At)}: {ex.Message}; entries are in memory only");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"[RevivalGuard] AuditTrail: {ex.GetType().Name} recording '{kind}': {ex.Message}");
            }
        }

        /// <summary>Tabs and line breaks are the file's separators, so they become spaces.</summary>
        static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        static string MonthPath(DateTime utc) => Path.Combine(s_Folder, $"{utc:yyyy-MM}.log");

        static int LoadMonth(DateTime utc)
        {
            var path = MonthPath(utc);
            if (!File.Exists(path)) return 0;
            int n = 0;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var parts = line.Split('\t', 5);
                    if (parts.Length < 5) continue;
                    if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) continue;
                    lock (s_Lock)
                    {
                        s_Entries.AddLast(new Entry { At = at, Kind = parts[1], Who = parts[2], Where = parts[3], Text = parts[4] });
                        while (s_Entries.Count > MEMORY) s_Entries.RemoveFirst();
                    }
                    n++;
                }
            }
            catch (Exception ex)
            {
                // a torn file must not take the mod down; whatever parsed is in memory
                Mod.Log.Error($"[RevivalGuard] AuditTrail: could not read {path}: {ex.Message}");
            }
            return n;
        }

        /// <summary>"0x018A" for a player in the world, "console" for the server console.</summary>
        internal static string WhereOf(Player p)
        {
            if (p == null) return "console";
            try { return p.Location != null ? $"0x{p.Location.LandblockId.Landblock:X4}" : "nowhere"; }
            catch { return "?"; }
        }

        // ---- the verb --------------------------------------------------------------------------

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Handle(Session session, params string[] parameters)
        {
            if (parameters.Length == 1 && parameters[0].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                Say(session, s_Folder != null
                    ? $"Audit files: {s_Folder}/YYYY-MM.log (tab-separated: UTC time, kind, who, where, text). This month: {MonthPath(DateTime.UtcNow)}"
                    : "No audit folder could be created beside the mod dll; entries are in memory only. See the server log.");
                return;
            }

            int count = DEFAULT_SHOW;
            var words = new List<string>();
            foreach (var p in parameters)
            {
                if (int.TryParse(p, out var n) && n > 0) count = Math.Min(MAX_SHOW, n);
                else if (p.Length > 0) words.Add(p);
            }

            List<Entry> picked;
            int total;
            lock (s_Lock)
            {
                total = s_Entries.Count;
                IEnumerable<Entry> q = s_Entries;
                if (words.Count > 0)
                    q = q.Where(e => words.All(w =>
                        e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)
                        || e.Who.Contains(w, StringComparison.OrdinalIgnoreCase)
                        || e.Kind.Equals(w, StringComparison.OrdinalIgnoreCase)
                        || e.Where.Contains(w, StringComparison.OrdinalIgnoreCase)));
                picked = q.TakeLast(count).ToList();
            }

            var filter = words.Count > 0 ? $" matching '{string.Join(" ", words)}'" : "";
            if (picked.Count == 0)
            {
                Say(session, $"Audit trail: nothing{filter} among {total} entries in memory (this month and last; older months are on disk, @audit file).");
                return;
            }
            Say(session, $"Audit trail, last {picked.Count}{filter} of {total} in memory, oldest first, times UTC:");
            foreach (var e in picked)
                Say(session, Format(e));
        }

        static string Format(Entry e)
        {
            // ACE's own audit lines begin with the actor's name; do not print it twice
            var text = e.Text.StartsWith(e.Who + " ", StringComparison.Ordinal) ? e.Text : $"{e.Who}: {e.Text}";
            return $"{e.At:MM-dd HH:mm} [{e.Kind}] {text} ({e.Where})";
        }
    }

    /// <summary>Every command, in game or on the console, passes through GetCommandHandler before its
    /// handler runs. Recorded once authorised, or when refused, for handlers above Player level.</summary>
    [HarmonyPatch(typeof(CommandManager), nameof(CommandManager.GetCommandHandler))]
    static class AuditTrailCommands
    {
        static void Postfix(Session session, string[] parameters, CommandHandlerInfo commandInfo, CommandHandlerResponse __result)
        {
            try
            {
                if (commandInfo?.Attribute == null) return;
                bool ok = __result == CommandHandlerResponse.Ok || __result == CommandHandlerResponse.SudoOk;
                bool denied = __result == CommandHandlerResponse.NotAuthorized;
                if (!ok && !denied) return;
                if (commandInfo.Attribute.Access <= AccessLevel.Player) return;
                var verb = commandInfo.Attribute.Command;
                if (ok && string.Equals(verb, "audit", StringComparison.OrdinalIgnoreCase)) return;

                var who = session?.Player?.Name ?? (session != null ? $"account {session.Account}" : "console");
                var args = parameters != null && parameters.Length > 0 ? " " + string.Join(" ", parameters) : "";
                if (ok)
                    AuditTrail.Record("cmd", who, AuditTrail.WhereOf(session?.Player), $"@{verb}{args}");
                else
                    AuditTrail.Record("denied", who, AuditTrail.WhereOf(session?.Player),
                        $"@{verb}{args} refused: needs {commandInfo.Attribute.Access}, has {session?.AccessLevel.ToString() ?? "?"}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AuditTrail: {e.GetType().Name} on a command: {e.Message}"); }
        }
    }

    /// <summary>What ACE's verbs (and ours) say they did. Declared on PlayerManager, static.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.BroadcastToAuditChannel))]
    static class AuditTrailChannel
    {
        static void Postfix(Player issuer, string message)
        {
            try { AuditTrail.Record("audit", issuer?.Name ?? "console", AuditTrail.WhereOf(issuer), message); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AuditTrail: {e.GetType().Name} on an audit line: {e.Message}"); }
        }
    }

    /// <summary>A PK or PKLite death and what it cost. CalculateDeathItems is declared on Player and
    /// PkDeathLoot already transpiles it; this postfix reads the final list it returns.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.CalculateDeathItems))]
    static class AuditTrailPkDeath
    {
        static void Postfix(Player __instance, Corpse corpse, List<WorldObject> __result)
        {
            try
            {
                var p = __instance;
                if (p == null || corpse == null) return;
                var killerId = corpse.KillerId;
                bool pk = p.IsPKDeath(killerId), lite = !pk && p.IsPKLiteDeath(killerId);
                if (!pk && !lite) return;
                var killer = killerId.HasValue ? PlayerManager.FindByGuid(killerId.Value)?.Name : null;
                killer = killer ?? $"0x{killerId ?? 0:X8}";
                var items = __result ?? new List<WorldObject>();
                var sb = new StringBuilder();
                sb.Append(pk ? "PK death: " : "PKLite death: ");
                sb.Append($"{p.Name} (level {p.Level ?? 1}) killed by {killer}; dropped {items.Count}");
                if (items.Count > 0)
                {
                    sb.Append(": ");
                    sb.Append(string.Join(", ", items.Take(12).Select(i => $"{i.Name}{(i.StackSize > 1 ? $" x{i.StackSize}" : "")}")));
                    if (items.Count > 12) sb.Append($", +{items.Count - 12} more");
                }
                AuditTrail.Record("pk", p.Name, AuditTrail.WhereOf(p), sb.ToString());
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AuditTrail: {e.GetType().Name} on a death: {e.Message}"); }
        }
    }
}
