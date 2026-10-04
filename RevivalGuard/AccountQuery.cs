using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using ACE.Database;
using ACE.Server.Network;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// DOES THIS ACCOUNT EXIST, ASKED BEFORE LOGGING IN (docs/ACCOUNT-SIGNUP.md, owner's "option B",
    /// 2026-10-01). ACE's AllowAutoAccountCreation makes a new, empty account out of any name it does
    /// not know, so a typo in the account field used to land a player in a fresh account with no
    /// characters. The login screen now asks first and, when the answer is "no", asks the player
    /// "This account doesn't exist yet. Create it?" before it sends the password.
    ///
    /// Same shape as <see cref="PopulationQuery"/>, on the login port that is already forwarded: a
    /// 20-byte AC header with Sequence 'HTAQ', Flags 0, Id 0xFFFF, Size 4+n, then 'HTAQ' and the n
    /// bytes of the name (1..50, ACE's own cap in HandleLoginRequest). The answer is 5 bytes,
    /// 'HTAA' and one byte: 1 exists, 0 does not, 3 does not and this address has made its new
    /// accounts for today (AccountCreationCap), 2 asked too often. Nothing else: no access level,
    /// no ban state, no characters. The reply is smaller than the question, so it cannot amplify.
    ///
    /// RATE LIMIT, per address: one answer a second, and at most PER_WINDOW questions in WINDOW;
    /// past that the answer is 2 until the window rolls. That is enough for a person retyping a
    /// name and slow for anyone walking a list of names. Loopback, LAN and Tailscale addresses are
    /// not limited at all (StaffLoginGate.IsLocal).
    ///
    /// REVIVAL_ACCOUNT_QUERY=0 turns it off (the packet then falls through to ACE, which drops it).
    /// </summary>
    [HarmonyPatch]
    static class AccountQuery
    {
        internal const uint MAGIC_Q = 0x51415448;   // "HTAQ" little-endian
        internal const uint MAGIC_A = 0x41415448;   // "HTAA"
        const ushort SESSION_ID = 0xFFFF;
        const int MAX_NAME = 50;
        const int PER_WINDOW = 20;
        static readonly TimeSpan WINDOW = TimeSpan.FromMinutes(10);
        static readonly TimeSpan PER_ANSWER = TimeSpan.FromSeconds(1);

        static readonly bool s_On = Environment.GetEnvironmentVariable("REVIVAL_ACCOUNT_QUERY") != "0";

        sealed class Seen { public readonly Queue<DateTime> At = new Queue<DateTime>(); public DateTime LastAnswer; public bool Warned; }
        static readonly ConcurrentDictionary<string, Seen> s_Seen = new ConcurrentDictionary<string, Seen>();
        static DateTime s_PruneAt;

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Managers.NetworkManager:ProcessPacket");

        static bool Prefix(ConnectionListener connectionListener, ClientPacket packet, IPEndPoint endPoint)
        {
            bool ours = false;
            try
            {
                if (!s_On || packet == null || endPoint == null) return true;
                var h = packet.Header;
                if (h.Sequence != MAGIC_Q || h.Id != SESSION_ID || (uint)h.Flags != 0) return true;
                var d = packet.Data;
                if (d == null || h.Size < 5 || h.Size > 4 + MAX_NAME || d.Length != h.Size) return true;
                long pos = d.Position;
                d.Position = 0;
                var buf = new byte[d.Length];
                int got = d.Read(buf, 0, buf.Length);
                d.Position = pos;
                if (got != buf.Length || BitConverter.ToUInt32(buf, 0) != MAGIC_Q) return true;

                // Ours from here on: never hand it to ACE, answered or not.
                ours = true;
                var now = DateTime.UtcNow;
                string key = endPoint.Address.ToString();
                var seen = s_Seen.GetOrAdd(key, _ => new Seen());
                byte answer = 255;
                // The shard's own network is not limited (the owner was told "slow down" on his
                // first login after a test from the same Mac, 2026-10-01): StaffLoginGate.IsLocal.
                if (!StaffLoginGate.IsLocal(endPoint.Address))
                lock (seen)
                {
                    if (now - seen.LastAnswer < PER_ANSWER) return false;
                    seen.LastAnswer = now;
                    while (seen.At.Count > 0 && now - seen.At.Peek() > WINDOW) seen.At.Dequeue();
                    if (seen.At.Count >= PER_WINDOW)
                    {
                        answer = 2;
                        if (!seen.Warned)
                        {
                            seen.Warned = true;
                            Mod.Log.Warn($"[RevivalGuard] account query: {key} passed {PER_WINDOW} questions in {WINDOW.TotalMinutes:F0} min; answering 'slow down'");
                        }
                    }
                    else
                    {
                        seen.At.Enqueue(now);
                        seen.Warned = false;
                        answer = 255;
                    }
                }
                if (answer == 255)
                {
                    string name = NameOf(buf);
                    answer = name != null && DatabaseManager.Authentication.GetAccountByName(name) != null ? (byte)1
                           : AccountCreationCap.Reached(endPoint.Address) ? (byte)3 : (byte)0;
                }
                if (now > s_PruneAt)
                {
                    s_PruneAt = now.AddMinutes(5);
                    foreach (var kv in s_Seen)
                        if (now - kv.Value.LastAnswer > WINDOW) s_Seen.TryRemove(kv.Key, out _);
                }

                var reply = new byte[5];
                BitConverter.GetBytes(MAGIC_A).CopyTo(reply, 0);
                reply[4] = answer;
                connectionListener?.Socket?.SendTo(reply, endPoint);
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] account query: {e.Message}");
                return !ours;
            }
        }

        /// <summary>The name after the magic: printable ASCII only, trimmed; null if anything else.</summary>
        static string NameOf(byte[] buf)
        {
            var chars = new char[buf.Length - 4];
            for (int i = 4; i < buf.Length; i++)
            {
                byte b = buf[i];
                if (b < 0x20 || b > 0x7E) return null;
                chars[i - 4] = (char)b;
            }
            string s = new string(chars).Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
