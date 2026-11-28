using System;
using ACE.Server.Managers;

namespace RevivalGuard
{
    /// <summary>Under-used dungeons, the live side.</summary>
    internal static class HuntGrounds
    {
        internal const string P_ON = "rg_hunt_grounds", P_BUSY = "rg_champion_busy_ground_mult", P_QUIET = "rg_champion_quiet_ground_mult",
            P_HUNT = "rg_champion_hunt_chance";

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard: fewer champions on the Tusker and Stomper grounds, more in under-used dungeons, and the Event Director's champion hunts there (HuntGroundsRule)");
            PkCommon.RegisterDouble(P_BUSY, HuntGroundsRule.BUSY_MULT, "RevivalGuard hunt grounds: champion chance multiplier on the busy Tusker and Stomper grounds");
            PkCommon.RegisterDouble(P_QUIET, HuntGroundsRule.QUIET_MULT, "RevivalGuard hunt grounds: champion chance multiplier in the under-used dungeons");
            PkCommon.RegisterDouble(P_HUNT, HuntGroundsRule.HUNT_CHANCE, "RevivalGuard hunt grounds: champion chance in the champion hunt's dungeon while it runs");
            int quiet = 0, busy = 0;
            foreach (var g in HuntGroundsRule.Grounds) if (g.Kind == HuntGroundsRule.Kind.Busy) busy++; else quiet++;
            Mod.Log.Info($"[RevivalGuard] HuntGrounds: {(On ? "ON" : "off")} ({P_ON}); {busy} busy grounds, {quiet} quiet dungeons (log {HuntGroundsRule.BUILT_FROM})");
        }

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        /// <summary>The running hunt's dungeon (0: none) and the champions slain there since it began.</summary>
        internal static volatile ushort HuntLb;
        static int s_Slain;
        internal static int Slain => s_Slain;

        internal static void HuntStarted(ushort lb) { HuntLb = lb; System.Threading.Interlocked.Exchange(ref s_Slain, 0); }
        internal static void HuntEnded() { HuntLb = 0; }
        internal static void ChampionSlain(ushort lb) { if (lb != 0 && lb == HuntLb) System.Threading.Interlocked.Increment(ref s_Slain); }

        /// <summary>The champion chance for a spawn on this landblock (MonsterFun's base when off).</summary>
        internal static double ChampionChance(double baseChance, ushort lb)
        {
            ushort hunt = HuntLb;
            if (!On && hunt == 0) return baseChance;
            double hc = PkCommon.Dbl(P_HUNT, HuntGroundsRule.HUNT_CHANCE);
            // switched off, a hunt staff started by hand still gets its champions; nothing else changes
            if (!On) return HuntGroundsRule.ChampionChance(baseChance, lb, hunt, 1, 1, hc);
            return HuntGroundsRule.ChampionChance(baseChance, lb, hunt, PkCommon.Dbl(P_BUSY, HuntGroundsRule.BUSY_MULT),
                PkCommon.Dbl(P_QUIET, HuntGroundsRule.QUIET_MULT), hc);
        }
    }
}
