using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// MESSAGES TO OFFLINE FRIENDS (docs/CHAT-IMPROVEMENTS-01.md piece 5, ours; retail had none).
    ///
    ///   /mail Name, text     leaves a message for a character who is offline; they get it as a tell when they next
    ///                        log in, marked "(mail, Oct 13)". To someone online it says to use /tell instead.
    ///   /mail                says how it works and how many of yours are waiting.
    ///
    /// Kept small on purpose: 300 characters a message, 5 waiting per sender for one person and 20 in all, 30 days
    /// before an unread one is dropped. Never to or from a mercenary. Stored in mail.json beside the mod, written
    /// through a temp file, under one lock.
    /// </summary>
    public static class Mail
    {
        const string FILE = "mail.json";
        const int MAX_LEN = 300, MAX_PER_PAIR = 5, MAX_PER_SENDER = 20, KEEP_DAYS = 30;
        const double DELIVER_AFTER_S = 6.0;   // after the login's own messages, so it is not lost in them

        sealed class Note
        {
            public string From { get; set; }
            public string To { get; set; }      // the recipient's name as stored, compared without case
            public string Text { get; set; }
            public DateTime Utc { get; set; }
        }

        static readonly object s_Lock = new object();
        static List<Note> s_Notes;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        public static void Register()
        {
            CommandManager.TryAddCommand(HandleMail, "mail", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Leave a message for someone who is offline: they get it when they next log in.", "Name, message");
            lock (s_Lock) Load();
            Mod.Log.Info($"[RevivalGuard] Mail: on, {s_Notes.Count} waiting; /mail");
        }

        static string FilePath()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(ModManager.ModPath, "RevivalGuard");
            return Path.Combine(folder, FILE);
        }

        static void Load()
        {
            s_Notes = new List<Note>();
            try
            {
                var p = FilePath();
                if (File.Exists(p)) s_Notes = JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(p), s_Json) ?? new List<Note>();
                s_Notes.RemoveAll(n => n.Utc < DateTime.UtcNow.AddDays(-KEEP_DAYS));
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mail: could not read {FILE}: {e.Message}; starting empty"); }
        }

        static void Save()
        {
            try
            {
                var p = FilePath(); var tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Notes, s_Json));
                File.Move(tmp, p, overwrite: true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Mail: could not write {FILE}: {e.Message}"); }
        }

        static void Say(Session s, string text) =>
            s?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static bool IsMercName(string name) =>
            name != null && (name.EndsWith(" the Mercenary", StringComparison.Ordinal) || name.EndsWith(" the Sellsword", StringComparison.Ordinal));

        static void HandleMail(Session session, params string[] parameters)
        {
            var me = session?.Player;
            if (me == null) return;
            string all = string.Join(" ", parameters ?? Array.Empty<string>()).Trim();
            int comma = all.IndexOf(',');
            if (all.Length == 0 || comma <= 0)
            {
                int mine; lock (s_Lock) mine = s_Notes.Count(n => n.From == me.Name);
                Say(session, "Leave a message for someone who is offline: /mail Name, your message. They get it when they next log in."
                             + (mine > 0 ? $" {mine} of yours {(mine == 1 ? "is" : "are")} waiting to be read." : ""));
                return;
            }
            if (Mercenaries.IsMerc(me)) return;
            string to = all.Substring(0, comma).Trim().TrimStart('+');
            string text = all.Substring(comma + 1).Trim();
            if (text.Length == 0) { Say(session, "Write a message after the comma: /mail Name, your message"); return; }
            if (text.Length > MAX_LEN) { Say(session, $"That message is too long: {MAX_LEN} characters at most."); return; }

            var target = PlayerManager.FindByName(to, out bool online) ?? PlayerManager.FindByName("+" + to, out online);
            if (target == null || IsMercName(target.Name)) { Say(session, $"There is no character named {to}."); return; }
            if (target.Guid == me.Guid) { Say(session, "You can't mail yourself."); return; }
            if (online) { Say(session, $"{target.Name.TrimStart('+')} is online now: send a tell instead (/tell {target.Name.TrimStart('+')}, ...)."); return; }

            lock (s_Lock)
            {
                if (s_Notes.Count(n => n.From == me.Name) >= MAX_PER_SENDER) { Say(session, $"You have {MAX_PER_SENDER} messages waiting already. Wait for some to be read."); return; }
                if (s_Notes.Count(n => n.From == me.Name && string.Equals(n.To, target.Name, StringComparison.OrdinalIgnoreCase)) >= MAX_PER_PAIR)
                { Say(session, $"{target.Name.TrimStart('+')} already has {MAX_PER_PAIR} messages from you waiting."); return; }
                s_Notes.Add(new Note { From = me.Name, To = target.Name, Text = text, Utc = DateTime.UtcNow });
                Save();
            }
            Say(session, $"Your message will reach {target.Name.TrimStart('+')} when they next log in.");
            Mod.Log.Info($"[RevivalGuard] Mail: {me.Name} -> {target.Name} ({text.Length} chars)");
        }

        static void Deliver(Player p)
        {
            List<Note> mine;
            lock (s_Lock)
            {
                mine = s_Notes.Where(n => string.Equals(n.To, p.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Utc).ToList();
                if (mine.Count == 0) return;
                s_Notes.RemoveAll(n => string.Equals(n.To, p.Name, StringComparison.OrdinalIgnoreCase));
                Save();
            }
            // As a tell, so it lands in the Tells channel, the Social tab and the sender's conversation tab, and a click
            // on the name answers it. Marked as mail with the day it was sent: it is not a live tell.
            foreach (var n in mine)
                p.Session?.Network.EnqueueSend(new GameMessageSystemChat($"{n.From} tells you, \"(mail, {n.Utc:MMM d}) {n.Text}\"", ChatMessageType.Tell));
            Mod.Log.Info($"[RevivalGuard] Mail: {mine.Count} delivered to {p.Name}");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p == null || Mercenaries.IsMerc(p)) return;
                    bool any; lock (s_Lock) any = s_Notes != null && s_Notes.Any(n => string.Equals(n.To, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (!any) return;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(DELIVER_AFTER_S);
                    chain.AddAction(p, () => Deliver(p));
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Mail enter: {e.Message}"); }
            }
        }
    }
}
