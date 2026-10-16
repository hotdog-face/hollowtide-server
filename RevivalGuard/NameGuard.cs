using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Handlers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// NO ONE ELSE MAY LOOK LIKE THE OWNER (owner 2026-10-16: no player may impersonate the owner's character by a
    /// sound-alike name).
    ///
    /// A new character whose name is close to a protected name is refused as a banned name, unless the account creating it
    /// is staff. "Close" is measured on a folded form of the name: lower case, letters only, doubled letters collapsed, and
    /// look-alike letters folded together (i/l, o/0, vv/w, rn/m), then within an edit distance that grows with the length
    /// (two for a long name). For a protected "Ab Cdef": "Ab Cdef", "A B Cdef", "Abb Cdef", "Ab Cdeg", "Ab Cde" and
    /// "AbCdef" are all refused; "Ab" alone or "Cdef" alone are not.
    ///
    /// The protected names are Mods/RevivalGuard/protected-names.json (a JSON array of strings) on the server only, so no
    /// name is in the source; with no file, nothing is protected and a warning is logged. DiscordBridge uses <see cref="IsLookalike"/> too: only the Discord server's owner is shown in
    /// game under a protected name.
    /// </summary>
    [HarmonyPatch(typeof(CharacterHandler), "CharacterCreateEx")]
    static class NameGuard
    {
        const string FILE = "protected-names.json";
        static string[] s_Folded;

        static string[] Folded()
        {
            if (s_Folded != null) return s_Folded;
            var names = new List<string>();
            try
            {
                string folder = null;
                try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; } catch (Exception) { }
                if (string.IsNullOrEmpty(folder)) folder = Path.Combine(ModManager.ModPath, "RevivalGuard");
                string path = Path.Combine(folder, FILE);
                if (File.Exists(path)) names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? names;
                else Mod.Log.Warn($"[RevivalGuard] NameGuard: no {FILE}; no names are protected");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NameGuard: {FILE}: {e.Message}; protecting the default"); }
            s_Folded = names.Select(Fold).Where(f => f.Length >= 4).Distinct().ToArray();
            return s_Folded;
        }

        /// <summary>Lower case, letters only, look-alikes folded, doubled letters collapsed.</summary>
        internal static string Fold(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.ToLowerInvariant().Replace('0', 'o').Replace('1', 'l').Replace('3', 'e').Replace('5', 's').Replace('|', 'l');
            s = s.Replace("vv", "w").Replace("rn", "m");
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 'a' || c > 'z') continue;
                char f = c == 'i' ? 'l' : c;
                if (sb.Length > 0 && sb[sb.Length - 1] == f) continue;   // doubled letters count once
                sb.Append(f);
            }
            return sb.ToString();
        }

        static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                var cur = new int[b.Length + 1];
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = cur;
            }
            return prev[b.Length];
        }

        /// <summary>Is this name too close to a protected name?</summary>
        internal static bool IsLookalike(string name)
        {
            string f = Fold(name);
            if (f.Length == 0) return false;
            foreach (var p in Folded())
            {
                int allowed = p.Length >= 8 ? 2 : 1;
                if (f.Contains(p) || Distance(f, p) <= allowed) return true;
            }
            return false;
        }

        static bool Prefix(ClientMessage message, Session session)
        {
            try
            {
                if (session == null || session.AccessLevel > AccessLevel.Player) return true;   // staff may use their own names
                var stream = message.Payload.BaseStream;
                long pos = stream.Position;
                var info = new ACE.Entity.CharacterCreateInfo();
                info.Unpack(message.Payload);
                stream.Position = pos;                 // ACE's own Unpack reads it again from here
                if (!IsLookalike(info.Name)) return true;
                session.Network.EnqueueSend(new GameMessageCharacterCreateResponse(
                    CharacterGenerationVerificationResponse.NameBanned, ObjectGuid.Invalid, string.Empty));
                Mod.Log.Info($"[RevivalGuard] NameGuard: refused '{info.Name}' for account '{session.Account}' (too close to a protected name)");
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] NameGuard: {e.Message}; letting ACE decide");
                return true;
            }
        }
    }
}
