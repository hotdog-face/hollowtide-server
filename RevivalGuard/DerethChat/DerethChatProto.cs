// Dereth cross-server chat: the wire format and the filters, shared by the hub and the ACE mod.
// No ACE types here, so the hub (cross-server-chat/hub) compiles this same file. AGPL-3.0, like the rest of the
// server source. Design: docs/proposals/cross-server-chat.md.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DerethChat
{
    /// <summary>One JSON object per WebSocket text frame. "t" says what it is; unused fields are left out.</summary>
    public sealed class Frame
    {
        public string t { get; set; }        // challenge hello welcome say ok rejected servers who notice error
        public int? v { get; set; }          // protocol version (hello, welcome)
        public string id { get; set; }       // the sender's id for a say, echoed in ok / rejected
        public string server { get; set; }   // hello: the server's id; say from the hub: the sender server's display name
        public string name { get; set; }     // character name
        public string text { get; set; }
        public bool? bot { get; set; }       // the sending server marks its own automated characters
        public string nonce { get; set; }
        public string sig { get; set; }
        public string why { get; set; }
        public string[] servers { get; set; }

        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        public string ToJson() => JsonSerializer.Serialize(this, s_Json);
        public static Frame Parse(string json) { try { return JsonSerializer.Deserialize<Frame>(json); } catch { return null; } }
    }

    public static class Proto
    {
        public const int VERSION = 1;
        public const int MAX_TEXT = 200, MAX_NAME = 32;

        /// <summary>What a server signs to prove it holds its key: bound to the protocol, its id and the hub's nonce.</summary>
        public static byte[] HelloPayload(string serverId, string nonce) => Encoding.UTF8.GetBytes($"dereth-chat/{VERSION}|{serverId}|{nonce}");

        public static string Sign(ECDsa key, string serverId, string nonce) => Convert.ToBase64String(key.SignData(HelloPayload(serverId, nonce), HashAlgorithmName.SHA256));

        public static bool Verify(string publicKeyB64, string serverId, string nonce, string sigB64)
        {
            try
            {
                using var k = ECDsa.Create();
                k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64), out _);
                return k.VerifyData(HelloPayload(serverId, nonce), Convert.FromBase64String(sigB64), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }

        public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        /// <summary>One line, printable, spaces collapsed, capped. Emoji and other characters past the game font are dropped.</summary>
        public static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max + 8));
            bool space = false;
            foreach (char c in s)
            {
                if (c < ' ' || char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (c >= 0xD800) continue;   // surrogates (emoji) and private use
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString();
        }

        /// <summary>A character name as the game allows it: letters, spaces, apostrophes, hyphens, and ACE's "+" staff mark
        /// in front (kept, so a server's staff show as staff, as in its own chat).</summary>
        public static string CleanName(string n)
        {
            n = CleanText(n ?? "", MAX_NAME + 1).Trim();
            bool staff = n.StartsWith("+");
            n = n.TrimStart('+').Trim();
            if (n.Length == 0 || n.Length > MAX_NAME) return null;
            bool letter = false;
            foreach (char c in n)
            {
                if (char.IsLetter(c)) { letter = true; continue; }
                if (c == ' ' || c == '\'' || c == '-') continue;
                return null;
            }
            return letter ? (staff ? "+" + n : n) : null;
        }

        // A web address in any common shape: a scheme, www., a dotted name with a known ending, an IP, or an invite.
        static readonly Regex s_Link = new Regex(
            // no spaces around a plain dot: "home. Me too" is a sentence, not an address
            @"(\b[a-z][a-z0-9+.-]*://)|(\bwww\.)|(\b\d{1,3}(\.\d{1,3}){3}\b)|(discord\s*\.\s*gg)|" +
            @"(\b[a-z0-9-]{2,}(\.|\s*\(dot\)\s*|\s*\[dot\]\s*)(com|net|org|gg|io|me|tv|ly|xyz|co|us|uk|info|biz|ru|cn|de|app|dev|link|site|online|shop|club|live)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool HasLink(string s) => !string.IsNullOrEmpty(s) && s_Link.IsMatch(s);

        /// <summary>Everything a line goes through before anyone sees it, on the hub and on each server. Null = refused (why says so).</summary>
        public static string Filter(string text, out string why)
        {
            why = null;
            var t = CleanText(text, MAX_TEXT);
            if (t.Length == 0) { why = "empty"; return null; }
            if (HasLink(t)) { why = "link"; return null; }
            // NO WORD MASKING (owner 2026-11-08): words pass unchanged; each player's own client applies its profanity
            // setting (Hollowtide's Options, retail's own filter), as for any other chat.
            return t;
        }

        /// <summary>How a line reads in chat: "Name (Server): text", "Name (Server, bot): text" for a tagged bot.</summary>
        public static string Line(string server, string name, string text, bool bot = false) => $"{name} ({server}{(bot ? ", bot" : "")}): {text}";
    }

    /// <summary>A token bucket per key: <c>burst</c> lines at once, then one every <c>every</c>.</summary>
    public sealed class RateLimiter
    {
        readonly double m_Burst, m_PerSecond;
        readonly Dictionary<string, (double tokens, DateTime at)> m_Buckets = new Dictionary<string, (double, DateTime)>();
        public RateLimiter(int burst, TimeSpan every) { m_Burst = burst; m_PerSecond = 1.0 / every.TotalSeconds; }

        public bool Take(string key, DateTime now)
        {
            lock (m_Buckets)
            {
                if (m_Buckets.Count > 5000)   // forget the idle ones now and then
                {
                    var stale = new List<string>();
                    foreach (var kv in m_Buckets) if ((now - kv.Value.at).TotalSeconds * m_PerSecond >= m_Burst) stale.Add(kv.Key);
                    foreach (var k in stale) m_Buckets.Remove(k);
                }
                double tokens = m_Burst;
                if (m_Buckets.TryGetValue(key, out var b)) tokens = Math.Min(m_Burst, b.tokens + (now - b.at).TotalSeconds * m_PerSecond);
                if (tokens < 1) { m_Buckets[key] = (tokens, now); return false; }
                m_Buckets[key] = (tokens - 1, now);
                return true;
            }
        }
    }
}
