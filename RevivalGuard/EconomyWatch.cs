using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ECONOMY WATCH: notice what the rules did not stop. Anti-cheat plan item 6 (docs/security/
    /// ANTI-CHEAT.md): automation (AI agents included) and any exploit nobody has thought of yet show up
    /// the same way, as coin or experience arriving faster than a person can earn it. Each online
    /// character's coin and total experience are sampled on its heartbeat (~5 s, on the landblock
    /// thread, so the reads are safe) and compared with the sample about ten minutes earlier; a gain
    /// past the limits below is written to ~/ace/reports/economy/&lt;yyyy-MM-dd&gt;.log and the server log
    /// for staff to look at. Nothing is taken or blocked: a flag is a question, not a verdict.
    /// Staff accounts are skipped (admin commands create coin and experience on purpose).
    ///
    /// The limits are deliberately generous and are ours, not retail's. A dupe or a runaway macro
    /// clears them by orders of magnitude; tune them from real players' logs.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Heartbeat))]
    static class EconomyWatch
    {
        // The live limits are in tunables.json on the server (Tunables), not in the published source.
        // These defaults are more lenient than the live ones: a missing file only flags less.
        static double WINDOW_MINUTES => Tunables.D("economy.window_minutes", 10);
        static long COIN_LIMIT => Tunables.L("economy.coin_limit", 1_000_000);          // pyreals gained in one window
        static double XP_LIMIT_FRACTION => Tunables.D("economy.xp_limit_fraction", 4.0); // windows' worth of the next level's cost, below level 126
        static long XP_LIMIT_FLOOR => Tunables.L("economy.xp_limit_floor", 100_000_000);

        sealed class Sample { public DateTime At; public long Coin, Xp; public DateTime LastFlag; }
        sealed class History { public readonly Queue<Sample> Samples = new Queue<Sample>(); }
        static readonly ConditionalWeakTable<Player, History> s_History = new ConditionalWeakTable<Player, History>();

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports", "economy");
        static readonly object s_FileLock = new object();

        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                if (p?.Session == null || p.Session.AccessLevel >= AccessLevel.Sentinel) return;
                var h = s_History.GetOrCreateValue(p);
                var now = DateTime.UtcNow;
                var cur = new Sample { At = now, Coin = p.CoinValue ?? 0, Xp = p.TotalExperience ?? 0 };
                h.Samples.Enqueue(cur);
                Sample oldest = null;
                while (h.Samples.Count > 0 && (now - h.Samples.Peek().At).TotalMinutes > WINDOW_MINUTES)
                    oldest = h.Samples.Dequeue();
                if (oldest == null) return;   // not a full window yet

                long coinGain = cur.Coin - oldest.Coin, xpGain = cur.Xp - oldest.Xp;
                long xpLimit = XP_LIMIT_FLOOR;
                try
                {
                    int lvl = p.Level ?? 1;
                    if (lvl < 126)
                    {
                        var table = ACE.DatLoader.DatManager.PortalDat.XpTable.CharacterLevelXPList;
                        if (lvl + 1 < table.Count) xpLimit = Math.Max(xpLimit, (long)((table[lvl + 1] - table[lvl]) * XP_LIMIT_FRACTION));
                    }
                }
                catch { }

                string why = coinGain > COIN_LIMIT ? $"coin +{coinGain:N0} in {WINDOW_MINUTES} min (limit {COIN_LIMIT:N0})"
                           : xpGain > xpLimit ? $"experience +{xpGain:N0} in {WINDOW_MINUTES} min (limit {xpLimit:N0})"
                           : null;
                if (why == null) return;
                var last = h.Samples.Count > 0 ? h.Samples.Peek() : cur;
                if ((now - cur.LastFlag).TotalMinutes < WINDOW_MINUTES && cur.LastFlag != default) return;
                foreach (var s in h.Samples) s.LastFlag = now;   // one flag per window per character

                string line = $"{now:o}\t{p.Name}\t0x{p.Guid.Full:X8}\t{p.Session.Account}\tlevel {p.Level}\t{why}\t{p.Location?.ToLOCString()}";
                Mod.Log.Warn($"[RevivalGuard] economy: {p.Name} {why}");
                lock (s_FileLock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Path.Combine(Dir, now.ToString("yyyy-MM-dd") + ".log"), line + Environment.NewLine);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] economy watch: {e.Message}"); }
        }
    }
}
