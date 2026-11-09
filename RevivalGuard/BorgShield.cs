using System;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE BORG'S RED SHIELD (owner 2026-11-08: "if its correct then make the custom red colored particles for their shield").
    /// The Sanguinary Aegis (wcid 23356) carries blue sparkles in the DAT (setup 0x02000577, script 0x330003E2, emitter
    /// 0x32000289, blue textures); the retail Borg's red glow came from their Weeping weapons (script 0x33000DA0, red
    /// emitter 0x3200056C). Worn by a Borg (BorgSwitch.IsBorg, "... of Borg" on an allowlisted account), the shield's
    /// PhysicsDesc DefaultScript carries that red script; our client (AcWielded, ServerLink.WireScriptOf) plays a 0x33
    /// value in place of the setup's own effect. Anyone else wearing one, a looter included, gets it cleared: the item is the
    /// ordinary shield, earned in play like the rest of the Borg's kit, and only the Borg's shows red. Older clients play the
    /// setup's blue as before (a 0x33 value is no PlayScript type in any table).
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TryEquipObject))]
    public static class BorgShield
    {
        const uint AEGIS = 23356, RED = 0x33000DA0;

        static void Prefix(Creature __instance, WorldObject worldObject)
        {
            try
            {
                if (worldObject == null || worldObject.WeenieClassId != AEGIS) return;
                bool borg = __instance is Player p && BorgSwitch.IsBorg(p);
                worldObject.DefaultScriptId = borg ? RED : (uint?)null;
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] BorgShield: " + e.Message); }
        }
    }
}
