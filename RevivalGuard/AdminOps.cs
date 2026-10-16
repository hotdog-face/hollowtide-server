using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// WHAT THE ADMIN NEEDED BY HAND ON LAUNCH NIGHT, AS COMMANDS (owner, 2026-10-12: "update the admin panel to be
    /// more useful knowing what we know now about the game"). The admin panel's Server tab has a button for each.
    ///
    ///   @funnel [hours]     the new players of the last day (default 24 h) from the telemetry log: characters made,
    ///                       in the browser or the desktop app, how many entered the world, how long they stayed,
    ///                       how many kept their browser character and came back; plus the auto (watchdog) reports
    ///                       in that time, which is where the browser's missing content shows up first.
    ///   @lockouts [clear]   the addresses StaffLoginGate has locked out, and with "clear", lift them all. That
    ///                       night the owner's own address was locked out and only a restart cleared it.
    ///   @resetlogin <character> <password>   RESCUE A LOST GUEST (owner, 2026-10-16: keep players coming back with their
    ///                       in-game identity). A browser guest who lost their browser data cannot log in as their
    ///                       account any more; this sets a new password on the account that owns the named character
    ///                       (online or not), and says which account it was, so the player can log in as it from any
    ///                       browser (?login=1) or the desktop app. Admin only; staff accounts are refused; the audit
    ///                       trail gets the character and the account, never the password (AuditTrailCommands hides
    ///                       the last word of this verb). The password is the LAST word (6 to 64 characters, no
    ///                       spaces) so a character name with spaces still works. The admin panel's Server tab has
    ///                       the button (it makes a password when the box is empty).
    /// (The third button, "report go", is @ticket go latest: TicketConsole.)
    /// </summary>
    static class AdminOps
    {
        static readonly string Reports = Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports");

        internal static void Register()
        {
            CommandManager.TryAddCommand(Funnel, "funnel", AccessLevel.Sentinel, CommandHandlerFlag.None,
                "New players of the last day: made, entered, stayed, kept, came back.", "[hours]");
            CommandManager.TryAddCommand(Lockouts, "lockouts", AccessLevel.Admin, CommandHandlerFlag.None,
                "Addresses locked out of logins; 'clear' lifts them all.", "[clear]");
            CommandManager.TryAddCommand(ResetLogin, "resetlogin", AccessLevel.Admin, CommandHandlerFlag.None,
                "Set a new password on the account that owns a character (a player who lost their browser data).",
                "<character name> <new password>");
        }

        /// <summary>The answer starts with this so the admin panel can pick it out of the chat.</summary>
        internal const string RESCUE_REPLY = "[Rescue] ";

        static void ResetLogin(Session session, params string[] p)
        {
            string who = session?.Player?.Name ?? "the console";
            try
            {
                if (p == null || p.Length < 2) { Say(session, RESCUE_REPLY + "Usage: @resetlogin <character name> <new password>"); return; }
                string pw = p[p.Length - 1];
                string name = string.Join(" ", p.Take(p.Length - 1)).Trim().TrimStart('+');
                if (pw.Length < 6 || pw.Length > 64) { Say(session, RESCUE_REPLY + "The password must be 6 to 64 characters, no spaces."); return; }
                var found = PlayerManager.FindByName(name);
                if (found == null) { Say(session, RESCUE_REPLY + $"No character is named '{name}'."); return; }
                uint accountId = found.Account?.AccountId ?? 0;
                var account = accountId != 0 ? DatabaseManager.Authentication.GetAccountById(accountId) : null;
                if (account == null) { Say(session, RESCUE_REPLY + $"{found.Name}'s account could not be read."); return; }
                if (account.AccessLevel > 0)
                {
                    Say(session, RESCUE_REPLY + $"{found.Name} belongs to a staff account; its password is not reset here.");
                    AuditTrail.Record("resetlogin", who, "console", $"refused: {found.Name} is on staff account {account.AccountName}");
                    return;
                }
                account.SetPassword(pw);
                DatabaseManager.Authentication.UpdateAccount(account);
                AuditTrail.Record("resetlogin", who, "console", $"new password set on account {account.AccountName} (character {found.Name})");
                Mod.Log.Info($"[RevivalGuard] AdminOps: {who} reset the password of account '{account.AccountName}' (character {found.Name})");
                Say(session, RESCUE_REPLY + $"OK {found.Name}'s account is \"{account.AccountName}\"; the new password is set.");
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] AdminOps: resetlogin: " + e.Message);
                Say(session, RESCUE_REPLY + "The password could not be reset: " + e.Message);
            }
        }

        static void Say(Session s, string text)
        {
            if (s == null) Console.WriteLine(text);
            else s.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        sealed class Acct
        {
            public bool Web, Entered, Kept, Staff;
            public long Secs;
            public int Level;
            public string Name;
            public HashSet<string> Days = new HashSet<string>();
            public DateTime First = DateTime.MaxValue;
        }

        static void Funnel(Session session, params string[] parameters)
        {
            double hours = parameters.Length > 0 && double.TryParse(parameters[0], out var h) ? Math.Clamp(h, 1, 24 * 14) : 24;
            var since = DateTime.UtcNow.AddHours(-hours);
            var accts = new Dictionary<string, Acct>();
            var dir = Path.Combine(Reports, "events");
            try
            {
                foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl").OrderBy(x => x) : Enumerable.Empty<string>())
                {
                    if (File.GetLastWriteTimeUtc(f) < since.AddDays(-14)) continue;
                    foreach (var line in File.ReadLines(f))
                    {
                        JsonElement e;
                        try { e = JsonDocument.Parse(line).RootElement; } catch { continue; }
                        string Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        long Num(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                        var a = Str("a");
                        if (a == null) continue;
                        if (!accts.TryGetValue(a, out var r)) accts[a] = r = new Acct();
                        if (e.TryGetProperty("staff", out _) || e.TryGetProperty("merc", out _)) r.Staff = true;
                        if (Num("web") != 0) r.Web = true;
                        DateTime.TryParse(Str("t"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t);
                        string ev = Str("ev");
                        if (ev == "chargen" && t < r.First) { r.First = t; r.Name = Str("c"); }
                        if (ev == "login" || ev == "where") { r.Entered = true; r.Days.Add(t.ToString("yyyy-MM-dd")); }
                        if (ev == "logout") r.Secs += Num("secs");
                        if (ev == "keep") r.Kept = true;
                        r.Level = Math.Max(r.Level, (int)Num("lvl"));
                    }
                }
            }
            catch (Exception ex) { Say(session, "@funnel: could not read the telemetry: " + ex.Message); return; }

            var fresh = accts.Values.Where(r => !r.Staff && r.First >= since && r.First != DateTime.MaxValue).ToList();
            string Row(bool web)
            {
                var g = fresh.Where(r => r.Web == web).ToList();
                if (g.Count == 0) return "none";
                int entered = g.Count(r => r.Entered), stayed10 = g.Count(r => r.Secs >= 600), back = g.Count(r => r.Days.Count >= 2);
                double avgMin = g.Count > 0 ? g.Average(r => r.Secs) / 60.0 : 0;
                return $"{g.Count} made, {entered} entered, {stayed10} played 10+ min, average {avgMin:0} min, best level {g.Max(r => r.Level)}"
                     + (web ? $", {g.Count(r => r.Kept)} kept" : "") + $", {back} came back another day";
            }
            Say(session, $"New players in the last {hours:0} h (staff and sellswords left out):");
            Say(session, "  browser: " + Row(true));
            Say(session, "  desktop: " + Row(false));
            if (fresh.Count > 0)
                Say(session, $"  newest{(fresh.Count > 12 ? " 12" : "")}: " + string.Join(", ", fresh.OrderByDescending(r => r.First).Take(12)
                    .Select(r => $"{r.Name} ({(r.Web ? "web" : "app")}, L{r.Level}, {r.Secs / 60}m)")));
            // the watchdog's automatic reports in the same window: content gaps, refused raises, errors
            try
            {
                int auto = 0, content = 0;
                foreach (var f in Directory.GetFiles(Reports, "*.json", SearchOption.AllDirectories))
                {
                    if (File.GetLastWriteTimeUtc(f) < since) continue;
                    var txt = File.ReadAllText(f);
                    if (!txt.Contains("\"kind\": \"auto\"") && !txt.Contains("\"kind\":\"auto\"")) continue;
                    auto++;
                    if (txt.Contains("[content]")) content++;
                }
                Say(session, $"  automatic reports: {auto}" + (content > 0 ? $", {content} about missing content (@tickets)" : ""));
            }
            catch { }
        }

        static void Lockouts(Session session, params string[] parameters)
        {
            if (parameters.Length > 0 && parameters[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                int n = StaffLoginGate.ClearLockouts();
                Say(session, n == 0 ? "No address was locked out." : $"Lifted {n} lockout(s); their failed-password counts are forgotten too.");
                Mod.Log.Info($"[RevivalGuard] lockouts cleared by {session?.Player?.Name ?? "the console"}: {n}");
                return;
            }
            var list = StaffLoginGate.Lockouts();
            if (list.Count == 0) { Say(session, "No address is locked out of logins."); return; }
            foreach (var (key, until) in list)
                Say(session, $"  {key}: locked out for {(int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes)} more min");
            Say(session, "@lockouts clear lifts them.");
        }
    }
}
