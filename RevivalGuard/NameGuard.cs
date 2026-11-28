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
    [HarmonyPatch(typeof(CharacterHandler), "CharacterCreateEx")]
    static class NameGuard
    {
        const string FILE = "protected-names.json";
        static string[] s_Folded;

        /// <summary>Read the file now, at mod start, so the log says at once how many names are protected.</summary>
        internal static void LoadNow() => Folded();

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
            // A broken file protects nothing: say so plainly, as an error, so it is not mistaken for working protection.
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] NameGuard: {FILE} could not be read ({e.Message}); NO names are protected until it is fixed"); }
            s_Folded = names.Select(Fold).Where(f => f.Length >= 4).Distinct().ToArray();
            if (s_Folded.Length == 0) Mod.Log.Error($"[RevivalGuard] NameGuard: no names are protected");
            else Mod.Log.Info($"[RevivalGuard] NameGuard: protecting {s_Folded.Length} name(s)");
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
                // the Borg suffix is the shard's own (BorgRule): its accounts may use it, a player may not
                string why = BorgRule.RefuseName(info.Name, BorgRule.IsShardAccountName(session.Account), staff: false)
                             ?? (IsLookalike(info.Name) ? "too close to a protected name" : null);
                if (why == null) return true;
                session.Network.EnqueueSend(new GameMessageCharacterCreateResponse(
                    CharacterGenerationVerificationResponse.NameBanned, ObjectGuid.Invalid, string.Empty));
                Mod.Log.Info($"[RevivalGuard] NameGuard: refused '{info.Name}' for account '{session.Account}' ({why})");
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
