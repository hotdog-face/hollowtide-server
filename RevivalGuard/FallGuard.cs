using System;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FALL DAMAGE THE CLIENT'S REPORTS SHOW (FallWatch, 7 days to 2026-10-24: 188 hurt landings, 8,974 damage). ACE hurts a
    /// PK by its server body's speed at every touch of the ground, so one fall bouncing down a cliff face was charged up to
    /// 9 times (21 landings, 1,584 extra damage), and 15 outdoor landings were deeper than any drop near the client.
    /// FallGuardState caps a descent's damage at the fall the client's own reports show, and holds a hit the reports do not
    /// show yet for the landing report (10 s). Switch rg_fall_guard, default OFF (a change from ACE, which owns this rule,
    /// for the owner to approve): off, ACE's damage stands and each decision is logged as a shadow, so live shows what it
    /// would change. Findings: docs/findings/phantom-falls-2026-10.md.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.TakeDamage_Falling))]
    static class FallGuard
    {
        internal const string P_ON = "rg_fall_guard";
        [ThreadStatic] static bool t_Own;
        internal static bool On => PropertyManager.GetBool(P_ON, false).Item;

        static bool Prefix(Player __instance, ref float amount)
        {
            if (t_Own) return true;
            try
            {
                var p = __instance;
                if (p == null || p.IsDead || p.Invincible || amount <= 0f) return true;
                var st = FallTrace.s_State.GetOrCreateValue(p); double t = FallTrace.Now;
                float drop = st.Guard.Drop(t), allowed = st.Guard.Allowed(t);
                float pay = st.Guard.Hit(t, amount);
                bool on = On;
                Mod.Log.Info($"[RevivalGuard] FallGuard: {p.Name} ACE {amount:0} -> {pay:0} now, {st.Guard.Pending:0} held; the client's reports show a "
                             + $"{drop:0.0} m fall (allows {allowed:0}){(on ? "" : " [off: ACE's damage stands]")}");
                if (!on) return true;
                if (pay <= 0f) return false;
                amount = pay;
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] FallGuard: " + e.Message); }
            return true;
        }

        /// <summary>After each client report: pay a held hit the report now shows, or give it up when none did in time.</summary>
        internal static void AfterReport(Player p, FallTrace.St st)
        {
            try
            {
                if (st.Guard.Pending <= 0f) return;
                double t = FallTrace.Now;
                float pay = st.Guard.Settle(t, out float dropped);
                if (dropped > 0f)
                    Mod.Log.Info($"[RevivalGuard] FallGuard: {p.Name} held {dropped:0} dropped: no report showed that fall{(On ? "" : " [off]")}");
                if (pay <= 0f) return;
                Mod.Log.Info($"[RevivalGuard] FallGuard: {p.Name} held {pay:0} paid: the client's report shows a {st.Guard.Drop(t):0.0} m fall{(On ? "" : " [off]")}");
                if (!On || p.IsDead) return;
                t_Own = true;
                try { p.TakeDamage_Falling(pay); } finally { t_Own = false; }
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] FallGuard: " + e.Message); }
        }
    }

    /// <summary>A teleport starts the client's reports afresh: a portal to lower ground is not a fall.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Teleport))]
    static class FallGuardTeleport
    {
        static void Postfix(Player __instance)
        {
            try { FallTrace.s_State.GetOrCreateValue(__instance).Guard.Reset(); } catch { }
        }
    }
}
