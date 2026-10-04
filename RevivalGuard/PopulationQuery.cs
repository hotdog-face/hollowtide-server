using System;
using System.Collections.Concurrent;
using System.Net;
using ACE.Server.Managers;
using ACE.Server.Network;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// HOW MANY ARE ON, ASKED WITHOUT LOGGING IN. The login screen shows the server's population
    /// top left (owner 2026-09-28: "login screen top left should show server population"), and it
    /// has no session to ask through. Retail's server list in the launcher showed population; the
    /// retail client itself throws the counts in Login__WorldInfo 0xF7E1 away
    /// (ClientUISystem::Handle_Login__WorldInfo @005641a0 keeps only the name), and ACE sends that
    /// only after a password anyway.
    ///
    /// So the question rides the login port itself (UDP 9000), which is already forwarded for
    /// players outside the LAN, instead of a new port: a 20-byte AC packet header with Sequence
    /// 'HTPQ', Flags 0, Id 0xFFFF, Size 4, then 'HTPQ' again as the payload. ClientPacket.Unpack
    /// accepts it (no optional headers, no fragments), and unpatched ACE would drop it in
    /// NetworkManager.ProcessPacket as an unsolicited packet for a session id that cannot exist.
    /// This prefix answers it instead with 8 bytes, 'HTPA' + the int32 online count, and nothing
    /// else: no names, no accounts, no admin state. The reply is smaller than the question, and one
    /// address gets at most one reply a second, so the port cannot be used to amplify.
    ///
    /// REVIVAL_POP_QUERY=0 turns it off (the packet then falls through to ACE as before).
    /// </summary>
    [HarmonyPatch]
    static class PopulationQuery
    {
        internal const uint MAGIC_Q = 0x51505448;   // "HTPQ" little-endian
        internal const uint MAGIC_A = 0x41505448;   // "HTPA"
        const ushort SESSION_ID = 0xFFFF;
        static readonly TimeSpan PER_ADDRESS = TimeSpan.FromSeconds(1);

        static readonly bool s_On = Environment.GetEnvironmentVariable("REVIVAL_POP_QUERY") != "0";
        static readonly ConcurrentDictionary<string, DateTime> s_Last = new ConcurrentDictionary<string, DateTime>();
        static DateTime s_PruneAt;

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Managers.NetworkManager:ProcessPacket");

        static bool Prefix(ConnectionListener connectionListener, ClientPacket packet, IPEndPoint endPoint)
        {
            try
            {
                if (!s_On || packet == null || endPoint == null) return true;
                var h = packet.Header;
                if (h.Sequence != MAGIC_Q || h.Id != SESSION_ID || (uint)h.Flags != 0 || h.Size != 4) return true;
                var d = packet.Data;
                if (d == null || d.Length != 4) return true;
                long pos = d.Position;
                d.Position = 0;
                uint body = (uint)(d.ReadByte() | (d.ReadByte() << 8) | (d.ReadByte() << 16) | (d.ReadByte() << 24));
                d.Position = pos;
                if (body != MAGIC_Q) return true;

                // It is ours from here on: never hand it to ACE, answered or not.
                var now = DateTime.UtcNow;
                string key = endPoint.Address.ToString();
                if (s_Last.TryGetValue(key, out var last) && now - last < PER_ADDRESS) return false;
                s_Last[key] = now;
                if (now > s_PruneAt)
                {
                    s_PruneAt = now.AddMinutes(1);
                    foreach (var kv in s_Last)
                        if (now - kv.Value > TimeSpan.FromMinutes(1)) s_Last.TryRemove(kv.Key, out _);
                }

                int n = PlayerManager.GetOnlineCount();
                var reply = new byte[8];
                BitConverter.GetBytes(MAGIC_A).CopyTo(reply, 0);
                BitConverter.GetBytes(n).CopyTo(reply, 4);
                connectionListener?.Socket?.SendTo(reply, endPoint);
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] population query: {e.Message}");
                return true;
            }
        }
    }
}
