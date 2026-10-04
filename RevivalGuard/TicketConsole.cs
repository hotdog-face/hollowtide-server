using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace RevivalGuard
{
    /// <summary>
    /// THE TICKET QUEUE, IN THE GAME, WHERE THE PERSON WHO HAS TO ACT ON IT IS STANDING.
    ///
    /// <see cref="TicketDesk"/> has been taking player reports since 2026-09-18 and filing them as
    /// JSON under ~/ace/reports/&lt;yyyy-MM&gt;/. Eighteen of them are sitting there right now. **Nothing
    /// has ever shown them to the owner inside the game.** Reading them meant an ssh session and
    /// `tools/reports/tickets.py`, which is a fine thing to have and the wrong place to be when a
    /// player is stuck and waiting: by the time you have found a terminal they have logged off.
    ///
    /// So this registers four commands. They are deliberately plain text over the chat channel the
    /// admin is already reading, because that needs no new protocol, no new port, and no client
    /// change at all -- an admin on ANY client, including the retail one, gets the queue. The admin
    /// panel's buttons (AcAdminHud) send exactly these strings.
    ///
    ///   @tickets                  the open queue, newest first: id, kind, who, level, how long ago
    ///   @tickets all              closed ones too
    ///   @ticket &lt;id&gt;              one ticket in full, including what the player wrote
    ///   @ticket go &lt;id&gt;           teleport to where the reporter was standing when they filed it
    ///   @ticket close &lt;id&gt;        mark it closed
    ///
    /// `@ticket go` is the one that turns this from a list into a tool. Every ticket already carries
    /// the SERVER's own record of where the character was (`server_location`, written by TicketDesk
    /// beside the player's text precisely because the client's word is not evidence), in exactly the
    /// form `@teleloc` takes. "I'm stuck here" becomes one command.
    ///
    /// ACCESS. Sentinel and above, which is ACE's own bar for `@teleto` -- these read other people's
    /// reports and move you across the world. `CommandManager.TryAddCommand` is public API and checks
    /// the caller's access on every invocation, the same as a built-in command, so nothing here is a
    /// second permission system.
    ///
    /// COST. The queue is read off disk each time it is asked for rather than cached. Eighteen small
    /// files is nothing, a busy shard's month is a few thousand, and a cache that can go stale while
    /// an admin is triaging is worse than a directory listing. If it ever matters, the fix is an
    /// index file, not a cache.
    /// </summary>
    static class TicketConsole
    {
        static readonly string Root = Environment.GetEnvironmentVariable("REVIVAL_REPORTS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports");

        sealed class Ticket
        {
            public string Id, Kind, Status, Account, Character, Location, Report, ReceivedRaw;
            public int Level;
            public DateTime Received;
            public string Path;
        }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Tickets, "tickets", AccessLevel.Sentinel,
                CommandHandlerFlag.RequiresWorld, "The support ticket queue.", "[all]");
            CommandManager.TryAddCommand(TicketCmd, "ticket", AccessLevel.Sentinel,
                CommandHandlerFlag.RequiresWorld, "Show, travel to, or close one ticket.",
                "<id> | go <id> | close <id>");
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>Every ticket on disk. A file that will not parse is skipped and named in the log
        /// rather than taking the whole queue down with it -- a half-written report is exactly the
        /// kind of thing a crash leaves behind, and the other seventeen still matter.</summary>
        static List<Ticket> Load()
        {
            var list = new List<Ticket>();
            if (!Directory.Exists(Root)) return list;
            foreach (var path in Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var r = doc.RootElement;
                    string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    int Int(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                    var t = new Ticket
                    {
                        Id = Str("id") ?? Path.GetFileNameWithoutExtension(path),
                        Kind = Str("kind") ?? "?",
                        Status = Str("status") ?? "open",
                        Account = Str("account") ?? "?",
                        Character = Str("character") ?? "?",
                        Location = Str("server_location"),
                        Report = Str("report") ?? "",
                        ReceivedRaw = Str("received_utc"),
                        Level = Int("level"),
                        Path = path,
                    };
                    DateTime.TryParse(t.ReceivedRaw, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var when);
                    t.Received = when;
                    list.Add(t);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"[RevivalGuard] ticket {path} would not parse and was skipped: {e.Message}");
                }
            }
            return list.OrderByDescending(t => t.Received).ToList();
        }

        static string Ago(DateTime utc)
        {
            if (utc == default) return "unknown";
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
            return $"{(int)d.TotalDays}d ago";
        }

        static void Tickets(Session session, params string[] parameters)
        {
            bool all = parameters.Length > 0 && parameters[0].Equals("all", StringComparison.OrdinalIgnoreCase);
            var list = Load();
            var show = all ? list : list.Where(t => !string.Equals(t.Status, "closed", StringComparison.OrdinalIgnoreCase)).ToList();

            if (show.Count == 0)
            {
                Say(session, all ? "No tickets have ever been filed." : "No open tickets. (@tickets all includes closed ones.)");
                return;
            }

            Say(session, $"--- {show.Count} {(all ? "ticket" : "open ticket")}{(show.Count == 1 ? "" : "s")}"
                + $"{(all ? "" : $" of {list.Count} total")} ---");
            // A cap, because chat is a scrolling buffer and a hundred lines pushes the useful ones
            // off the top. The full set is on disk and tools/reports/tickets.py reads it.
            foreach (var t in show.Take(20))
                Say(session, $"{t.Id}  [{t.Kind}]  {t.Character} (lvl {t.Level}, {t.Account})  {Ago(t.Received)}"
                    + $"{(string.Equals(t.Status, "closed", StringComparison.OrdinalIgnoreCase) ? "  CLOSED" : "")}");
            if (show.Count > 20) Say(session, $"...and {show.Count - 20} more. @ticket <id> shows one.");
            Say(session, "@ticket <id> to read it, @ticket go <id> to stand where they were, @ticket close <id> when it is done.");
        }

        static void TicketCmd(Session session, params string[] parameters)
        {
            if (parameters.Length == 0)
            {
                Say(session, "@ticket <id> | go <id> | close <id>");
                return;
            }

            string verb = parameters[0].ToLowerInvariant();
            bool go = verb == "go", close = verb == "close";
            string id = (go || close) ? (parameters.Length > 1 ? parameters[1] : null) : parameters[0];
            if (string.IsNullOrWhiteSpace(id)) { Say(session, $"@ticket {verb} needs a ticket id."); return; }

            var t = Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (t == null) { Say(session, $"No ticket {id}. @tickets lists them."); return; }

            if (go)
            {
                if (string.IsNullOrWhiteSpace(t.Location))
                {
                    Say(session, $"Ticket {t.Id} has no recorded position.");
                    return;
                }
                // TELEPORTING IS DEVELOPER, AND READING A TICKET IS NOT. These two commands sit at
                // Sentinel so a moderator can triage the queue, but `go` does what `@teleloc` does
                // and must answer to `@teleloc`'s own bar. Calling ACE's handler directly would
                // otherwise launder a Developer action through a Sentinel command, which is exactly
                // the kind of hole a second permission system opens.
                if (session.AccessLevel < AccessLevel.Developer)
                {
                    Say(session, $"@ticket go needs Developer access (it is a teleport). "
                        + $"{t.Character} filed {t.Id} at {t.Location}");
                    return;
                }
                // server_location is written as "0xCELL [x y z] qw qx qy qz" -- one of the three
                // spellings @teleloc's own usage text lists. Hand it to ACE's handler rather than
                // re-implementing the parse, with the brackets split off as separate tokens so the
                // handler sees the bare numbers it expects.
                var args = t.Location.Replace("[", " ").Replace("]", " ")
                                     .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Say(session, $"Going to where {t.Character} filed {t.Id}.");
                ACE.Server.Command.Handlers.AdminCommands.HandleTeleportLOC(session, args);
                return;
            }

            if (close)
            {
                try
                {
                    var text = File.ReadAllText(t.Path);
                    // A targeted replacement rather than a re-serialise: the file is the record of what
                    // the player sent and re-writing it through a parser would quietly normalise their
                    // text. Only the status field changes.
                    var updated = text.Replace("\"status\": \"open\"", "\"status\": \"closed\"")
                                      .Replace("\"status\":\"open\"", "\"status\":\"closed\"");
                    if (updated == text)
                    {
                        Say(session, $"Ticket {t.Id} was already {t.Status}.");
                        return;
                    }
                    File.WriteAllText(t.Path, updated);
                    Say(session, $"Ticket {t.Id} closed.");
                    Mod.Log.Info($"[RevivalGuard] ticket {t.Id} closed by {session?.Player?.Name ?? "console"}");
                }
                catch (Exception e)
                {
                    Say(session, $"Could not close {t.Id}: {e.Message}");
                }
                return;
            }

            Say(session, $"--- ticket {t.Id} [{t.Kind}] {t.Status} ---");
            Say(session, $"{t.Character} (level {t.Level}, account {t.Account}), {Ago(t.Received)}");
            if (!string.IsNullOrWhiteSpace(t.Location)) Say(session, $"at {t.Location}");
            // The player's own text, a few lines at a time. Chat has no scrollback worth relying on,
            // so this shows the head of the report -- which is where a player says what went wrong --
            // and points at the file for the stack traces underneath.
            var lines = (t.Report ?? "").Replace("\r", "").Split('\n')
                          .Where(l => !string.IsNullOrWhiteSpace(l)).Take(12).ToList();
            foreach (var l in lines) Say(session, "  " + (l.Length > 160 ? l.Substring(0, 157) + "..." : l));
            Say(session, $"(full report: {t.Path})");
        }
    }
}
