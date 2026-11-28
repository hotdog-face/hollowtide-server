using System;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The borg's red shield.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TryEquipObject))]
    public static class BorgShield
    {
        const uint AEGIS = 23356, BORG_AEGIS = 911316, RED = 0x33000DA0;   // BORG_AEGIS: the bonded mid suit's (BorgKit)

        static void Prefix(Creature __instance, WorldObject worldObject)
        {
            try
            {
                // (and the Borg's Weeping weapons that have no Weeping model: mace, axe, dirk, club, hatchet, crossbow, BorgKit)
                bool glow = worldObject != null && (worldObject.WeenieClassId >= 911341 && worldObject.WeenieClassId <= 911345
                                                    || worldObject.WeenieClassId == 911347);
                if (worldObject == null || !glow && worldObject.WeenieClassId != AEGIS && worldObject.WeenieClassId != BORG_AEGIS) return;
                bool borg = __instance is Player p && BorgSwitch.IsBorg(p);
                worldObject.DefaultScriptId = borg ? RED : (uint?)null;
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] BorgShield: " + e.Message); }
        }
    }
}
