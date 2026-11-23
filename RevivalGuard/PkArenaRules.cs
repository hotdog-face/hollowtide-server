using System;
using System.Collections.Generic;
using System.Linq;

namespace RevivalGuard
{
    /// <summary>
    /// THE ARENA QUEUE'S RULES, PURE C# (no ACE types) so NetTest runs them (tools/nettest/PkArenaCase.cs): who may be
    /// matched with whom, how a 2v2 is split, and the rating a result moves. PkArena.cs feeds these from live players.
    ///
    /// Matching: the longest waiter is served first. Everyone in a match must be within the level band of that waiter,
    /// and the band widens the longer they wait (a thin population still gets a fight). No two characters in one match
    /// share an account, or a connection address (PkCommon.SameAddress, which honours pk_honor_ignore_ip). Borg and
    /// Hollow Company mercenaries are never queued unless pk_arena_bots is on (PkArena checks that at the door).
    /// </summary>
    internal static class PkArenaRules
    {
        internal enum Mode { Duel = 0, Pairs = 1, Ffa = 2 }

        internal static readonly string[] MODE_WORD = { "1v1", "2v2", "ffa" };

        internal static bool TryMode(string s, out Mode m)
        {
            m = Mode.Duel;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "1v1": case "1": case "duel": m = Mode.Duel; return true;
                case "2v2": case "2": case "pairs": m = Mode.Pairs; return true;
                case "ffa": case "all": m = Mode.Ffa; return true;
                default: return false;
            }
        }

        /// <summary>One character waiting. Account and Address stand for "same account" / "same connection".</summary>
        internal sealed class Waiter
        {
            public uint Guid; public string Name = ""; public int Level; public uint Account; public string Address = "";
            public long Since; public int Rating = START_RATING;
        }

        internal const int START_RATING = 1000, K = 32;

        /// <summary>The level gap allowed after waiting `waited` seconds: base, plus `perMinute` for each whole minute, up to `cap`.</summary>
        internal static int Band(long waited, int baseBand, int perMinute, int cap) =>
            (int)Math.Min(cap, baseBand + perMinute * Math.Max(0, waited / 60));

        static bool Clash(Waiter a, Waiter b) =>
            a.Account == b.Account || (!string.IsNullOrEmpty(a.Address) && a.Address == b.Address);

        /// <summary>How many fighters a mode needs; ffa starts with `ffaMin` once its first waiter has waited `ffaWait` s.</summary>
        internal static int Need(Mode m, long firstWaited, int ffaMin, long ffaWait) =>
            m == Mode.Duel ? 2 : m == Mode.Pairs ? 4 : firstWaited >= ffaWait ? ffaMin : 4;

        /// <summary>The next match for one mode's queue (oldest first), or null. The oldest waiter is always in it; the
        /// rest are the closest in level to them, within the band, with no account or address shared.</summary>
        internal static List<Waiter> Pick(IReadOnlyList<Waiter> queue, Mode m, long now, int baseBand, int perMinute, int cap,
            int ffaMin = 3, long ffaWait = 60)
        {
            var q = queue.OrderBy(w => w.Since).ToList();
            for (int i = 0; i < q.Count; i++)
            {
                var head = q[i];
                long waited = now - head.Since;
                int need = Need(m, waited, ffaMin, ffaWait);
                int band = Band(waited, baseBand, perMinute, cap);
                var chosen = new List<Waiter> { head };
                foreach (var c in q.Where(w => w != head && Math.Abs(w.Level - head.Level) <= band)
                                   .OrderBy(w => Math.Abs(w.Level - head.Level)).ThenBy(w => w.Since))
                {
                    if (chosen.Any(x => Clash(x, c))) continue;
                    chosen.Add(c);
                    if (chosen.Count == need) return chosen;
                }
                // The oldest who cannot be matched yet does not hold up the others behind them.
            }
            return null;
        }

        /// <summary>The sides of a match: 1v1 and ffa one fighter a side; 2v2 the strongest with the weakest.</summary>
        internal static List<List<Waiter>> Sides(List<Waiter> f, Mode m)
        {
            if (m != Mode.Pairs || f.Count != 4) return f.Select(w => new List<Waiter> { w }).ToList();
            var s = f.OrderByDescending(w => w.Level).ToList();
            return new List<List<Waiter>> { new List<Waiter> { s[0], s[3] }, new List<Waiter> { s[1], s[2] } };
        }

        /// <summary>Elo (K 32). The rating change of each side in a finished match: `placeOf[i]` 0 for the winning side, 1 for the
        /// rest; all 0 is a draw. Ffa scores each pair of sides as a game of its own, averaged.</summary>
        internal static int[] Changes(double[] sideRating, int[] placeOf)
        {
            int n = sideRating.Length;
            var d = new int[n];
            if (n < 2) return d;
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double score = placeOf[i] == placeOf[j] ? 0.5 : placeOf[i] < placeOf[j] ? 1 : 0;
                    sum += K * (score - 1.0 / (1.0 + Math.Pow(10, (sideRating[j] - sideRating[i]) / 400.0)));
                }
                d[i] = (int)Math.Round(sum / (n - 1));
            }
            return d;
        }
    }
}
