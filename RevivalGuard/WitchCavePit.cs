using System;
using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The dark below the crone's dais is a fatal fall.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    static class WitchCavePit
    {
        const ushort LANDBLOCK = 0x0069;
        const float DEATH_Z = -51.0f;   // the pit floor is -54; the bridge, dais and hall are -48

        static bool Prepare()
        {
            Mod.Log.Info("[RevivalGuard] Witch Cave pit: falling below z " + DEATH_Z + " in landblock 0x0069 is fatal");
            return true;
        }

        // Reads the player's Location after the call, not the report and not the return value
        static void Postfix(Player __instance)
        {
            try
            {
                var p = __instance;
                var at = p?.Location;
                if (at == null || p.IsDead || p.Invincible || p.Teleporting) return;
                if (at.LandblockId.Landblock != LANDBLOCK || at.PositionZ >= DEATH_Z) return;
                var newPosition = at;
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    "You fall into the dark below the Crone's dais, and it does not give you back.", ChatMessageType.Broadcast));
                Mod.Log.Info($"[RevivalGuard] Witch Cave pit: {p.Name} fell in at {newPosition}");
                p.TakeDamage_Falling(p.Health.MaxValue * 10f);
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] Witch Cave pit: " + e.Message); }
        }
    }
}
