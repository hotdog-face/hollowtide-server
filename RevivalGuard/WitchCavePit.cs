using System;
using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE DARK BELOW THE CRONE'S DAIS IS A FATAL FALL. Owner, 2026-09-29, in the Witch Cave (0x0069):
    /// "at the end where the boss is, you can jump off into a black void and run around in an empty black
    /// space. this should kill players if they fall in there. or jump."
    ///
    /// Under the great hall's bridge and dais (z -48) is a ring of floor at z -54 (cells 0x0100-0x010E)
    /// that was never textured: a player who steps off lands on it and can wander in the black. Nothing
    /// else in the landblock is walkable below z -48 (the hall is the dungeon's lowest floor), so any
    /// accepted position below DEATH_Z in 0x0069 is a fall into the pit.
    ///
    /// The death is retail's own: ACE's Player.TakeDamage_Falling with more damage than anyone has, so the
    /// fall message, the bludgeon death, the corpse where they fell and the release to the lifestone are
    /// all the stock path. It respects @neversaydie (Invincible) and lifestone protection like any fall.
    ///
    /// Why not a Hotspot (retail's lethal-floor object, e.g. the Invisible Shadow Floor Hotspot 8887): its
    /// setup 0x02000638 is one small sphere, so covering a 40 x 30 m ring six metres under a walkway takes
    /// dozens of them sitting just below the bridge, and one staged with @create never fired. A height
    /// rule in one landblock is exact. Ours, not retail.
    /// </summary>
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

        // Reads the player's Location AFTER the call, not the report and not the return value: ACE returns
        // "crossed a landblock", not "accepted" (the first build tested __result and never fired; a bot
        // jumped off the bridge and stood at z -54), and Location is only moved to the report when the
        // move is accepted.
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
