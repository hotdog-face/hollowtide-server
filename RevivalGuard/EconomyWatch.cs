using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Economy watch: notice what the rules did not stop.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Heartbeat))]
    static class EconomyWatch
    {
        // The live limits are in tunables.json on the server (Tunables), not in the published source.
        static double WINDOW_MINUTES => Tunables.D("economy.window_minutes", 10);
        static long COIN_LIMIT => Tunables.L("economy.coin_limit", 1_000_000);          // pyreals gained in one window
        static double XP_LIMIT_FRACTION => Tunables.D("economy.xp_limit_fraction", 4.0); // windows' worth of the next level's cost, below level 126
        static long XP_LIMIT_FLOOR => Tunables.L("economy.xp_limit_floor", 100_000_000);

        sealed class Sample { public DateTime At; public long Coin, Xp; }
        // The flag gate lives on the character's history: one flag per window per character (EconomyFlags.cs).
        sealed class History { public readonly Queue<Sample> Samples = new Queue<Sample>(); public readonly FlagGate Gate = new FlagGate(); }
        static readonly ConditionalWeakTable<Player, History> s_History = new ConditionalWeakTable<Player, History>();

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "reports", "economy");
        static readonly BackgroundAppender s_Writer = new BackgroundAppender(m => Mod.Log.Warn($"[RevivalGuard] economy watch: {m}"));

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
                if (!h.Gate.TryFlag(now, WINDOW_MINUTES)) return;   // one flag per window per character

                string line = $"{now:o}\t{p.Name}\t0x{p.Guid.Full:X8}\t{p.Session.Account}\tlevel {p.Level}\t{why}\t{p.Location?.ToLOCString()}";
                Mod.Log.Warn($"[RevivalGuard] economy: {p.Name} {why}");
                s_Writer.Enqueue(Path.Combine(Dir, now.ToString("yyyy-MM-dd") + ".log"), line);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] economy watch: {e.Message}"); }
        }
    }
}
