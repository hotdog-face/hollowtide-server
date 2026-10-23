using System;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A DRINK STARTED OFF WALKABLE GROUND (SERVER-SIDE) FINISHES ON A TIMER. With allow_fast_chug every PK here takes
    /// ApplyConsumableWithAnimationCallbacks, which waits for the server body's motion-done of Drink and then Ready. When
    /// ACE's physics has that body out of Contact/OnWalkable (crowded on a dungeon ramp, see ServerFooting), the motion is
    /// never queued, the use never ends, and the player is "too busy" until ChugWatchdog (Genki, 261023-0010: "it lags a
    /// lot, re-logging fixes it"). Here such a drink takes ACE's own timed path instead (the body of ApplyConsumable when
    /// fast chug is off), which always sends UseDone. A drink on walkable ground keeps the retail fast chug.
    /// Switch: rg_chug_footing (default on).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.ApplyConsumable))]
    static class ChugFooting
    {
        const string P_ON = "rg_chug_footing";
        static double s_LastLog;

        static bool Prefix(Player __instance, MotionCommand useMotion, Action action, float animMod)
        {
            try
            {
                var p = __instance;
                if (p == null || !p.FastTick || !PropertyManager.GetBool("allow_fast_chug").Item) return true;
                if (ServerFooting.Walkable(p) || !PropertyManager.GetBool(P_ON, true).Item) return true;

                var now = ACE.Common.Time.GetUnixTime();
                if (now - s_LastLog > 5)
                {
                    s_LastLog = now;
                    Mod.Log.Warn($"[RevivalGuard] chug footing: {p.Name} (0x{p.Guid.Full:X8}) {useMotion} off walkable ground ({ServerFooting.Describe(p)}): timed path");
                }
                Timed(p, useMotion, action, animMod);
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] chug footing: {e.Message}"); return true; }
        }

        // Player.ApplyConsumable's own body below its allow_fast_chug branch (ACE Player_Use.cs), unchanged.
        static void Timed(Player p, MotionCommand useMotion, Action action, float animMod)
        {
            p.IsBusy = true;
            var actionChain = new ActionChain();
            var prevStance = p.CurrentMotionState.Stance;
            var animTime = 0.0f;
            if (prevStance != MotionStance.NonCombat)
                animTime = p.EnqueueMotion_Force(actionChain, MotionStance.NonCombat, MotionCommand.Ready, (MotionCommand)prevStance);
            var useAnimTime = p.EnqueueMotion_Force(actionChain, MotionStance.NonCombat, useMotion, null, 1.0f, animMod);
            animTime += useAnimTime;
            actionChain.AddAction(p, action);
            if (animMod == 1.0f)
                animTime += p.EnqueueMotion_Force(actionChain, MotionStance.NonCombat, MotionCommand.Ready, useMotion);
            else
                actionChain.AddDelaySeconds(useAnimTime * (1.0f - animMod));
            if (prevStance != MotionStance.NonCombat)
                animTime += p.EnqueueMotion_Force(actionChain, prevStance, MotionCommand.Ready, MotionCommand.NonCombat);
            actionChain.AddAction(p, () => { p.IsBusy = false; });
            actionChain.EnqueueChain();
            p.LastUseTime = animTime;
        }
    }
}
