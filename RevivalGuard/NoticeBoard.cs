using System.Text;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE HOLTBURG NOTICE BOARD (owner 2026-11-09: "a notice board near the Holtburg lifestone, retail-looking, with
    /// this month's Chronicle, the events on now and coming, and Borg news; data-driven so it updates without a client
    /// release"). docs/HOLTBURG-HUB.md.
    ///
    /// The board is a retail Book in the world (weenie 901101, a copy of retail's own unplaced Holtburg Bulletin Board,
    /// placed by datafix 2026-11-10-holtburg-notice-board.sql) flagged with PropertyBool 29113. Its pages are never in
    /// the database: they are written at read time, the way the Allegiance Hall Board's are (Mansions.cs), so every
    /// client, ours or retail, reads them with the ordinary book window and nothing ships in a client release.
    ///
    ///   page 1      the notices: live events now and next (EventDirector), festivals open and coming
    ///               (SeasonalCalendar), and the short notices of the content file
    ///   pages 2..   this month's Chronicle, in the Turbine voice (docs/outreach/VOICE.md)
    ///   last page   news of the Collective (the Borg), lore only: nothing about how they work
    ///
    /// The words come from NoticeBoard.json beside the dll (ModFolder; SeasonalOverrides.json's folder). It is seeded
    /// from the embedded copy on first read and re-read whenever its modification time changes, so staff edit the file
    /// on the shard and the next reader sees the new month. A file that will not parse keeps the last good words and
    /// says so in the log. `@noticeboard` (Admin) prints the pages to chat, for a check without walking to Holtburg.
    /// </summary>
    static class NoticeBoard
    {
        internal const PropertyBool BOARD = (PropertyBool)29113;
        const string FILE = "NoticeBoard.json";
        const int PAGE_CHARS = 1000;    // the book weenie's max_Num_Chars_Per_Page
        const int MAX_PAGES = 20;

        sealed class Content
        {
            public string Title { get; set; } = "Holtburg Notice Board";
            public string[] Notices { get; set; } = Array.Empty<string>();
            public Chronicle Chronicle { get; set; }
            public News News { get; set; }
        }
        sealed class Chronicle
        {
            public string Title { get; set; } = "";
            public string Month { get; set; } = "";
            public string[] Paragraphs { get; set; } = Array.Empty<string>();
        }
        sealed class News
        {
            public string Title { get; set; } = "";
            public string[] Lines { get; set; } = Array.Empty<string>();
        }

        static readonly object s_Lock = new object();
        static Content s_Content;
        static DateTime s_Stamp;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "noticeboard", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Prints the Holtburg Notice Board's pages (NoticeBoard.json beside the mod, re-read when it changes).", "");
        }

        // ---------------------------------------------------------------- the words

        static string FilePath => SeasonalCalendar.FileBeside(FILE);

        /// <summary>The content file's words, re-read when the file changed; the embedded seed when there is none.</summary>
        static Content Load()
        {
            lock (s_Lock)
            {
                try
                {
                    var path = FilePath;
                    if (!File.Exists(path))
                    {
                        string seed = Seed();
                        try { File.WriteAllText(path, seed); }
                        catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] notice board: could not write {path}: {e.Message}"); }
                        s_Content = JsonSerializer.Deserialize<Content>(seed, s_Json) ?? new Content();
                        s_Stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                        return s_Content;
                    }
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (s_Content != null && stamp == s_Stamp) return s_Content;
                    var parsed = JsonSerializer.Deserialize<Content>(File.ReadAllText(path), s_Json);
                    if (parsed == null) throw new InvalidDataException("empty file");
                    s_Content = parsed;
                    s_Stamp = stamp;
                    Mod.Log.Info($"[RevivalGuard] notice board: read {FILE} ({parsed.Notices.Length} notices, "
                        + $"{parsed.Chronicle?.Paragraphs?.Length ?? 0} chronicle paragraphs, {parsed.News?.Lines?.Length ?? 0} news lines)");
                }
                catch (Exception e)
                {
                    // the last good words stay up; the stamp moves so a broken file is reported once, not per reader
                    Mod.Log.Error($"[RevivalGuard] notice board: {FILE} not read, keeping the last words: {e.Message}");
                    try { s_Stamp = File.GetLastWriteTimeUtc(FilePath); } catch { }
                    s_Content ??= new Content();
                }
                return s_Content;
            }
        }

        static string Seed()
        {
            using var rs = typeof(NoticeBoard).Assembly.GetManifestResourceStream("RevivalGuard.NoticeBoard.json");
            if (rs == null) return "{}";
            using var rd = new StreamReader(rs);
            return rd.ReadToEnd();
        }

        // ---------------------------------------------------------------- the pages

        /// <summary>The board's pages as plain text, written now for whoever reads it.</summary>
        internal static List<string> PageTexts(out string title)
        {
            var c = Load();
            title = string.IsNullOrWhiteSpace(c.Title) ? "Holtburg Notice Board" : c.Title.Trim();
            var pages = new List<string>();

            // page 1: what is on, then the notices
            var sb = new StringBuilder();
            sb.Append(title.ToUpperInvariant()).Append("\n\n");
            var happenings = new List<string>();
            try { happenings.AddRange(EventDirector.BoardLines()); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] notice board: live events: {e.Message}"); }
            try { happenings.AddRange(SeasonalCalendar.BoardLines()); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] notice board: festivals: {e.Message}"); }
            if (happenings.Count > 0)
            {
                sb.Append("HAPPENINGS\n");
                foreach (var h in happenings) sb.Append(h.Trim()).Append('\n');
                sb.Append('\n');
            }
            if (c.Notices != null && c.Notices.Length > 0)
            {
                sb.Append("NOTICES\n");
                foreach (var n in c.Notices) if (!string.IsNullOrWhiteSpace(n)) sb.Append(n.Trim()).Append("\n\n");
            }
            pages.Add(sb.ToString().TrimEnd() + "\n");

            // the Chronicle, packed whole paragraphs to a page
            if (c.Chronicle?.Paragraphs != null && c.Chronicle.Paragraphs.Length > 0)
            {
                string head = (string.IsNullOrWhiteSpace(c.Chronicle.Month) ? "THE CHRONICLE" : "THE CHRONICLE, " + c.Chronicle.Month.Trim().ToUpperInvariant())
                    + (string.IsNullOrWhiteSpace(c.Chronicle.Title) ? "" : "\n" + c.Chronicle.Title.Trim()) + "\n\n";
                Pack(pages, head, c.Chronicle.Paragraphs, "(continued)\n\n");
            }

            // the Collective
            if (c.News?.Lines != null && c.News.Lines.Length > 0)
            {
                string head = (string.IsNullOrWhiteSpace(c.News.Title) ? "OF THE COLLECTIVE" : c.News.Title.Trim().ToUpperInvariant()) + "\n\n";
                Pack(pages, head, c.News.Lines, "(continued)\n\n");
            }

            if (pages.Count > MAX_PAGES) pages.RemoveRange(MAX_PAGES, pages.Count - MAX_PAGES);
            return pages;
        }

        /// <summary>Paragraphs onto pages of at most PAGE_CHARS, never splitting one (a longer one is cut).</summary>
        static void Pack(List<string> pages, string head, string[] paragraphs, string contHead)
        {
            var sb = new StringBuilder(head);
            bool first = true;
            foreach (var raw in paragraphs)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string p = raw.Trim();
                if (p.Length > PAGE_CHARS - contHead.Length - 2) p = p.Substring(0, PAGE_CHARS - contHead.Length - 2);
                if (sb.Length + p.Length + 2 > PAGE_CHARS && !first)
                {
                    pages.Add(sb.ToString().TrimEnd() + "\n");
                    sb = new StringBuilder(contHead);
                }
                sb.Append(p).Append("\n\n");
                first = false;
            }
            if (sb.Length > head.Length) pages.Add(sb.ToString().TrimEnd() + "\n");
        }

        static List<PropertiesBookPageData> Pages(out string title)
        {
            var texts = PageTexts(out title);
            if (title.Length > 60) title = title.Substring(0, 60);
            return texts.Select(t => new PropertiesBookPageData
            {
                AuthorId = 0, AuthorName = "", AuthorAccount = "", IgnoreAuthor = false,
                PageText = t.Length > PAGE_CHARS ? t.Substring(0, PAGE_CHARS) : t,
            }).ToList();
        }

        // ---------------------------------------------------------------- reading it

        static bool IsBoard(WorldObject wo) => wo is Book && (wo.GetProperty(BOARD) ?? false);

        // A board stands in the landblock, never in a pack or on a hook: ACE's own ReadBook/ReadBookPage look only
        // in the pack and the last hook, which is why the page requests are answered here.
        const Player.SearchLocations WHERE = Player.SearchLocations.Landblock | Player.SearchLocations.MyInventory | Player.SearchLocations.LastUsedHook;

        static void SendBoard(Book board, Player reader)
        {
            var pages = Pages(out var title);
            // authorId = the board itself, so the client titles the window with the inscription (Mansions.SendBoard)
            reader.Session.Network.EnqueueSend(new GameEventBookDataResponse(reader.Session, board.Guid.Full,
                PAGE_CHARS, pages.Count, pages, title, board.Guid.Full, title, false));
        }

        [HarmonyPatch(typeof(Book), nameof(Book.ActOnUse))]
        static class Read
        {
            static bool Prefix(Book __instance, WorldObject worldObject)
            {
                if (!IsBoard(__instance) || !(worldObject is Player p)) return true;
                try { SendBoard(__instance, p); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] notice board read: {e}"); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ReadBook))]
        static class Data
        {
            static bool Prefix(Player __instance, uint bookGuid)
            {
                var b = __instance.FindObject(bookGuid, WHERE) as Book;
                if (!IsBoard(b)) return true;
                try { SendBoard(b, __instance); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] notice board data: {e}"); }
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ReadBookPage))]
        static class Page
        {
            static bool Prefix(Player __instance, uint bookGuid, int pageNum)
            {
                var b = __instance.FindObject(bookGuid, WHERE) as Book;
                if (!IsBoard(b)) return true;
                try
                {
                    var pages = Pages(out _);
                    if (pageNum >= 0 && pageNum < pages.Count)
                        __instance.Session.Network.EnqueueSend(new GameEventBookPageDataResponse(__instance.Session, bookGuid, pageNum, pages[pageNum]));
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] notice board page: {e}"); }
                return false;
            }
        }

        /// <summary>Nobody pins anything on it: the pages are written at read time.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionBookAddPage))]
        static class NoAdd
        {
            static bool Prefix(Player __instance, uint bookGuid)
            {
                if (!IsBoard(__instance.FindObject(bookGuid, WHERE))) return true;
                __instance.Session.Network.EnqueueSend(new GameEventBookAddPageResponse(__instance.Session, bookGuid, -1, false));
                return false;
            }
        }

        // ---------------------------------------------------------------- @noticeboard

        static void Cmd(Session session, params string[] parameters)
        {
            try
            {
                var pages = PageTexts(out var title);
                Say(session, $"{title}: {pages.Count} page{(pages.Count == 1 ? "" : "s")} ({FilePath})");
                for (int i = 0; i < pages.Count; i++)
                {
                    Say(session, $"--- page {i + 1} ---");
                    foreach (var line in pages[i].Split('\n')) if (line.Trim().Length > 0) Say(session, line);
                }
            }
            catch (Exception e) { Say(session, "notice board: " + e.Message); }
        }

        static void Say(Session s, string text) =>
            s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }
}
