using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using ACE.Database;
using ACE.Server.Network;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Does this account exist, asked before logging in.</summary>
    [HarmonyPatch]
    static class AccountQuery
    {
        internal const uint MAGIC_Q = 0x51415448;   // "HTAQ" little-endian
        internal const uint MAGIC_A = 0x41415448;   // "htaa"
        const ushort SESSION_ID = 0xFFFF;
        const int MAX_NAME = 50;
        const int PER_WINDOW = 20;
        static readonly TimeSpan WINDOW = TimeSpan.FromMinutes(10);
        static readonly TimeSpan PER_ANSWER = TimeSpan.FromSeconds(1);

        static readonly bool s_On = Environment.GetEnvironmentVariable("REVIVAL_ACCOUNT_QUERY") != "0";

        sealed class Seen { public readonly Queue<DateTime> At = new Queue<DateTime>(); public DateTime LastAnswer; public bool Warned; }
        static readonly ConcurrentDictionary<string, Seen> s_Seen = new ConcurrentDictionary<string, Seen>();
        static DateTime s_PruneAt;

        // The per-address limit alone is no limit
        const int GLOBAL_PER_SECOND = 20;
        const int MAX_SEEN = 5000;
        static readonly object s_BudgetLock = new object();
        static long s_BudgetSecond; static int s_BudgetUsed; static DateTime s_BudgetWarned;

        static bool WithinBudget(DateTime now)
        {
            long second = now.Ticks / TimeSpan.TicksPerSecond;
            lock (s_BudgetLock)
            {
                if (second != s_BudgetSecond) { s_BudgetSecond = second; s_BudgetUsed = 0; }
                if (++s_BudgetUsed <= GLOBAL_PER_SECOND) return true;
                if (now - s_BudgetWarned > WINDOW)
                {
                    s_BudgetWarned = now;
                    Mod.Log.Warn($"[RevivalGuard] account query: more than {GLOBAL_PER_SECOND} outside questions in a second; dropping the rest");
                }
                return false;
            }
        }

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

                ours = true;
                var now = DateTime.UtcNow;
                var real = Gateway.RealAddressForLimits(endPoint);   // a browser player's own address, not the gateway's loopback
                string key = real.ToString();
                bool local = StaffLoginGate.IsLocal(real);
                if (!local && (!WithinBudget(now) || (s_Seen.Count >= MAX_SEEN && !s_Seen.ContainsKey(key)))) return false;
                var seen = s_Seen.GetOrAdd(key, _ => new Seen());
                byte answer = 255;
                // The shard's own network is not limited: StaffLoginGate.IsLocal.
                if (!local)
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
                           : AccountCreationCap.Reached(real) ? (byte)3 : (byte)0;
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
