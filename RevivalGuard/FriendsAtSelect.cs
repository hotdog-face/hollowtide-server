using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Managers;
using ACE.Server.Network.GameMessages.Messages;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Who is online, on the character select screen.</summary>
    public static class FriendsAtSelect
    {
        static readonly HashSet<uint> s_Capable = new HashSet<uint>();
        static bool s_Loaded;
        static DateTime s_NextTick;
        static string FilePath => Path.Combine(Path.GetDirectoryName(typeof(FriendsAtSelect).Assembly.Location) ?? ".", "capable-accounts.txt");

        static void Load()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                        if (uint.TryParse(line.Trim(), out uint a)) s_Capable.Add(a);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] capable-accounts.txt: {e.Message}"); }
        }

        /// <summary>From ModChannel's hello: this account's client reads the channel.</summary>
        internal static void NoteCapable(Session s)
        {
            if (s == null) return;
            Load();
            if (!s_Capable.Add(s.AccountId)) return;
            try { File.AppendAllText(FilePath, s.AccountId + Environment.NewLine); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] capable-accounts.txt: {e.Message}"); }
        }

        static string Esc(string t) => (t ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        internal static void Send(Session s)
        {
            if (s == null || s.Characters == null) return;
            Load();
            if (!s_Capable.Contains(s.AccountId)) return;
            var seen = new HashSet<uint>();
            var mine = new HashSet<uint>(s.Characters.Select(c => c.Id));
            var rows = new List<(string name, bool on, int lvl)>();
            foreach (var c in s.Characters.ToList())
            {
                if (c.IsDeleted || c.CharacterPropertiesFriendList == null) continue;
                foreach (var f in c.CharacterPropertiesFriendList.ToList())
                {
                    if (mine.Contains(f.FriendId) || !seen.Add(f.FriendId)) continue;
                    var p = PlayerManager.FindByGuid(new ObjectGuid(f.FriendId));
                    if (p == null) continue;
                    rows.Add((p.Name, PlayerManager.GetOnlinePlayer(f.FriendId) != null, p.Level ?? 0));
                }
            }
            var sb = new StringBuilder("[");
            foreach (var r in rows.OrderByDescending(r => r.on).ThenBy(r => r.name, StringComparer.OrdinalIgnoreCase))
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append("{\"n\":\"").Append(Esc(r.name)).Append("\",\"o\":").Append(r.on ? "true" : "false")
                  .Append(",\"l\":").Append(r.lvl).Append('}');   // the level
            }
            sb.Append(']');
            ModChannel.Send(s, "friends", sb.ToString());
        }

        static readonly System.Reflection.FieldInfo s_SessionMap = AccessTools.Field(typeof(NetworkManager), "sessionMap");

        /// <summary>Every 20 s: refresh every session parked at character select.</summary>
        internal static void Tick()
        {
            if (DateTime.UtcNow < s_NextTick) return;
            s_NextTick = DateTime.UtcNow.AddSeconds(20);
            if (!(s_SessionMap?.GetValue(null) is Session[] map)) return;
            foreach (var s in map.ToArray())
                if (s != null && (s.Player != null || s.State == ACE.Server.Network.Enum.SessionState.AuthConnected))
                    try { Send(s); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] friends at select: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameMessageCharacterList), MethodType.Constructor, new[] { typeof(List<Character>), typeof(Session) })]
    static class FriendsAtSelect_List
    {
        static void Postfix(Session session)
        {
            try { FriendsAtSelect.Send(session); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] friends at select: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class FriendsAtSelect_Tick
    {
        static void Postfix()
        {
            // the session-map reflection in Tick sits outside its per-session try
            try { FriendsAtSelect.Tick(); }
            catch (Exception e) { TickGuard.Report("FriendsAtSelect", e); }
        }
    }
}
