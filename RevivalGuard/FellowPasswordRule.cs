using System;
using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>
    /// The pure part of FellowPassword.cs (no ACE types: tools/nettest compiles it by path, FellowPasswordCase.cs). Owner
    /// 2026-11-20 "Fellowship password yes lets do it" (other AC servers offer it, docs/outreach/scene-scan-2026-11-08.md
    /// item 5): a leader sets a password, anyone who knows it joins from anywhere without an invite.
    /// </summary>
    public sealed class FellowPasswordRule
    {
        public const int MAX_LEN = 24;
        public const int MAX_WRONG = 5;
        public static readonly TimeSpan WRONG_WINDOW = TimeSpan.FromMinutes(1);

        public enum Outcome { Ok, Off, Throttled, Self, AlreadyInOther, AlreadyInThis, NotOnline, NoPassword, Wrong, Locked, Full, Olthoi, Refused }

        /// <summary>What the server knows when someone asks to join.</summary>
        public struct Facts
        {
            public bool On;
            public bool JoinerInFellowship, JoinerInLeadersFellowship, JoinerIsLeader, JoinerIsOlthoi;
            public bool LeaderOnline;           // online and not hidden behind Appear Offline (see FellowPassword.Join)
            public string Password;             // the leader's fellowship's password, null for none or not leading
            public bool LeaderRefused;          // a merc or Borg leads or is in it: never joinable (DESIGN-DECISIONS: fellowships never)
            public bool Open;                   // listed open on the Looking for Group board (LfgBoard): no password asked
            public bool Locked;                 // a quest lock (Fellowship.IsLocked)
            public int Members, Max;
        }

        readonly Dictionary<uint, List<DateTime>> m_Wrong = new Dictionary<uint, List<DateTime>>();

        /// <summary>A password the leader may set: one word, 1 to MAX_LEN characters. Null when it is not one.</summary>
        public static string Clean(string word)
        {
            if (word == null) return null;
            word = word.Trim();
            if (word.Length == 0 || word.Length > MAX_LEN) return null;
            foreach (char c in word) if (char.IsWhiteSpace(c) || char.IsControl(c)) return null;
            if (word.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
            return word;
        }

        /// <summary>Case does not matter: a password read aloud in chat should work as typed.</summary>
        public static bool Matches(string stored, string given) =>
            stored != null && given != null && string.Equals(stored, given.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>"/fjoin Some Name secret" (after the command): the last word is the password, the rest the leader's name.</summary>
        public static bool SplitJoin(string rest, out string leader, out string password)
        {
            leader = password = null;
            rest = (rest ?? "").Trim();
            int sp = rest.LastIndexOfAny(new[] { ' ', '\t' });
            if (sp <= 0) return false;
            leader = rest.Substring(0, sp).Trim();
            password = rest.Substring(sp + 1).Trim();
            return leader.Length > 0 && password.Length > 0;
        }

        public Outcome Decide(uint joiner, Facts f, string given, DateTime now)
        {
            if (!f.On) return Outcome.Off;
            if (Throttled(joiner, now)) return Outcome.Throttled;
            if (f.JoinerIsLeader) return Outcome.Self;
            if (f.JoinerInLeadersFellowship) return Outcome.AlreadyInThis;
            if (f.JoinerInFellowship) return Outcome.AlreadyInOther;
            if (!f.LeaderOnline) return Outcome.NotOnline;
            if (f.LeaderRefused) return Outcome.Refused;
            if (!f.Open)
            {
                if (f.Password == null) return Outcome.NoPassword;
                if (!Matches(f.Password, given)) { Wrong(joiner, now); return Outcome.Wrong; }
            }
            if (f.JoinerIsOlthoi) return Outcome.Olthoi;
            if (f.Locked) return Outcome.Locked;
            if (f.Members >= f.Max) return Outcome.Full;
            return Outcome.Ok;
        }

        /// <summary>The one line the joiner reads. No em dashes (player text).</summary>
        public static string Line(Outcome o, string leader, int max) => o switch
        {
            Outcome.Off => "Fellowship passwords are turned off on this world.",
            Outcome.Throttled => "Too many wrong fellowship passwords. Try again in a minute.",
            Outcome.Self => "You lead that fellowship.",
            Outcome.AlreadyInThis => $"You are already in {leader}'s fellowship.",
            Outcome.AlreadyInOther => "You are already in a fellowship. Leave it first to join another.",
            Outcome.NotOnline => $"{leader} is not online.",
            Outcome.NoPassword => $"{leader} is not leading a fellowship with a password.",
            Outcome.Wrong => $"Wrong password for {leader}'s fellowship.",
            Outcome.Olthoi => "The Olthoi's hunger for destruction is too great to understand a request for fellowship.",
            Outcome.Locked => $"{leader}'s fellowship is locked and takes no new members.",
            Outcome.Full => $"{leader}'s fellowship is full ({max} of {max}).",
            Outcome.Refused => $"{leader}'s fellowship does not take players.",
            _ => $"You join {leader}'s fellowship.",
        };

        bool Throttled(uint joiner, DateTime now)
        {
            lock (m_Wrong)
            {
                if (!m_Wrong.TryGetValue(joiner, out var l)) return false;
                l.RemoveAll(t => now - t > WRONG_WINDOW);
                if (l.Count == 0) { m_Wrong.Remove(joiner); return false; }
                return l.Count >= MAX_WRONG;
            }
        }

        void Wrong(uint joiner, DateTime now)
        {
            lock (m_Wrong)
            {
                if (m_Wrong.Count > 4096) m_Wrong.Clear();
                if (!m_Wrong.TryGetValue(joiner, out var l)) m_Wrong[joiner] = l = new List<DateTime>();
                l.Add(now);
            }
        }
    }
}
