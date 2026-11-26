using System;
using System.Collections.Generic;
using System.Text;

namespace RevivalGuard
{
    /// <summary>
    /// #pk-deaths, THE PURE HALF (no ACE types; tools/nettest compiles this file by path). Player idea (Rekah, Discord
    /// #ideas): "PK death channel in discord so we know who killed us if we are not at keys"; owner 2026-11-26 "i like
    /// this idea if its possible. a new channel that shows this info." Who is posted, the line, and the 10 s batches.
    /// PkDeathFeed.cs hooks the death; DiscordBridge posts.
    /// </summary>
    public static class PkDeathFeedRule
    {
        public const double WINDOW_S = 10;       // kills inside one window go out as one post (a big fight cannot spam)
        public const int MAX_LINES = 15;         // lines in one post; the rest become "+N more kills"
        public const int MAX_CHARS = 1900;       // Discord's limit is 2000

        /// <summary>Post this kill? Staff on either side (a "+" name or an account above Player) never; a Borg or
        /// mercenary killing another one never; a bot and a player, either way round, yes.</summary>
        public static bool ShouldPost(string killer, string victim, bool killerStaff, bool victimStaff, bool killerBot, bool victimBot)
        {
            if (string.IsNullOrWhiteSpace(killer) || string.IsNullOrWhiteSpace(victim)) return false;
            if (killerStaff || victimStaff || killer.StartsWith("+") || victim.StartsWith("+")) return false;
            if (killerBot && victimBot) return false;
            return true;
        }

        /// <summary>The killing blow's damage type as a word (ACE DamageType flags), or null for none or a mix.</summary>
        public static string DamageWord(uint damageType)
        {
            switch (damageType & 0x0FFFFFFFu)
            {
                case 0x1: return "slashing";
                case 0x2: return "piercing";
                case 0x4: return "bludgeoning";
                case 0x8: return "cold";
                case 0x10: return "fire";
                case 0x20: return "acid";
                case 0x40: return "lightning";
                case 0x80: return "health drain";
                case 0x100: return "stamina drain";
                case 0x200: return "mana drain";
                case 0x400: return "nether";
                default: return null;
            }
        }

        /// <summary>Discord markdown in a name would format or hide parts of it.</summary>
        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s) { if ("*_~`|>\\<".IndexOf(c) >= 0) sb.Append('\\'); sb.Append(c); }
            return sb.ToString();
        }

        /// <summary>One kill: "&lt;t:UNIX:t&gt; **Killer** killed **Victim** at 42.1N, 33.6E near Holtburg (fire)", with
        /// " [PK Night]" when it was on PK Night's hill. where is DeathLogRule.Where's words ("at ...", "in ...").</summary>
        public static string Line(string killer, string victim, string where, long unixSeconds, uint damageType, bool pkNight)
        {
            var sb = new StringBuilder();
            sb.Append("<t:").Append(unixSeconds).Append(":t> ");
            sb.Append("**").Append(Escape(Clip(killer, 40))).Append("** killed **").Append(Escape(Clip(victim, 40))).Append("**");
            if (!string.IsNullOrWhiteSpace(where)) sb.Append(' ').Append(Escape(Clip(where, 80)));
            var dmg = DamageWord(damageType);
            if (dmg != null) sb.Append(" (").Append(dmg).Append(')');
            if (pkNight) sb.Append(" [PK Night]");
            return sb.ToString();
        }

        static string Clip(string s, int max) => s == null ? "" : s.Length > max ? s.Substring(0, max) : s;

        /// <summary>The post for one window's lines: up to MAX_LINES (and MAX_CHARS), then "+N more kills".</summary>
        public static string Post(IList<string> lines)
        {
            if (lines == null || lines.Count == 0) return null;
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var l in lines)
            {
                if (shown >= MAX_LINES || sb.Length + l.Length + 40 > MAX_CHARS) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(l);
                shown++;
            }
            int more = lines.Count - shown;
            if (more > 0) sb.Append('\n').Append("+").Append(more).Append(more == 1 ? " more kill" : " more kills");
            return sb.ToString();
        }

        /// <summary>The 10 s window. Add() a line as it happens; Due(now) hands back the post once the first line in it
        /// is WINDOW_S old, and starts again. Not thread safe: the caller locks.</summary>
        public sealed class Batch
        {
            readonly List<string> m_Lines = new List<string>();
            double m_OpenedAt = -1;

            public int Count => m_Lines.Count;

            public void Add(string line, double now)
            {
                if (string.IsNullOrEmpty(line)) return;
                if (m_Lines.Count == 0) m_OpenedAt = now;
                m_Lines.Add(line);
            }

            public string Due(double now)
            {
                if (m_Lines.Count == 0 || now - m_OpenedAt < WINDOW_S) return null;
                var post = Post(m_Lines);
                m_Lines.Clear();
                m_OpenedAt = -1;
                return post;
            }
        }
    }
}
