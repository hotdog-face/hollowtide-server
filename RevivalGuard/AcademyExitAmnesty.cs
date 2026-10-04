using System.Collections.Generic;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A CHARACTER WHO NEVER WALKED OUT OF THE ACADEMY CAN NEVER RECALL, FOR THE REST OF ITS LIFE.
    ///
    /// Owner, 2026-09-21, standing in Holtburg: "it says *You must exit the Training Academy before
    /// that command will be available to you* in chat. so this flag for completing the academy quest
    /// is still not allowing me to append to life stones."
    ///
    /// `PropertyBool.RecallsDisabled` (107) is set on every new character
    /// (Factories/PlayerFactory.cs:455) and gates SEVEN separate things -- lifestone attunement and
    /// every recall in Player_Location.cs (:73, :140, :213, :282, :375, :496, :580), each answering
    /// with `WeenieError.ExitTrainingAcademyToUseCommand`.
    ///
    /// NOTHING IN ACE'S CODE EVER CLEARS IT. It is cleared by CONTENT, and only by content: an
    /// `EmoteCategory.Portal` emote of type `SetBoolStat`(107, 0) hanging off the four academy exit
    /// portals -- weenies 29337 Exit to Shoushi, 29338 Exit to Holtburg, 29339 Exit to Sanamar,
    /// 29340 Exit to Yaraq -- and a matching pair on Jonathan (29317/29324/29325/29326), the NPC who
    /// sends you out. Walk through the door and the flag lifts. Arrive in the world any other way and
    /// it never does.
    ///
    /// "Any other way" is not an edge case. It is every character this project makes: admin-created,
    /// restored from a rebuild, spawned by `@teleloc` out of the academy, or town-spawned through
    /// ACE's own `allow_town_spawn`. The owner's own character is one. And there is no in-game way
    /// out of it -- you cannot go back and use the portal, because the portal is in the academy and
    /// you are not; `@fixbusy` does not touch it; nothing tells the player what is wrong beyond a
    /// sentence about a building they have never seen.
    ///
    /// THE RULE HERE IS THE FLAG'S OWN MEANING, NOT A NEW POLICY. RecallsDisabled means "this
    /// character has not left the tutorial yet". So on login: if the flag is set and the character is
    /// demonstrably NOT IN the tutorial, it has left, and the flag is stale. Clear it and say so.
    /// A character still inside the academy keeps it, which is the point -- a new player should still
    /// have to walk out of the front door, and that path is untouched.
    ///
    /// THE ACADEMY LANDBLOCKS are the twenty that hold those four exit portals -- the five copies of
    /// each academy, and the few blocks each one spans. They are listed rather than queried because a
    /// mod that reaches into the world database on every login costs more than a set lookup, and this
    /// list only changes if the academies themselves are re-authored. Re-derive it with:
    ///
    ///     select distinct landblock from landblock_instance
    ///      where weenie_Class_Id in (29337,29338,29339,29340) order by landblock;
    ///
    /// WHY LOGIN AND NOT A LANDBLOCK-CHANGE HOOK. A stale flag is a property of the CHARACTER, not of
    /// a moment, and the characters that have it have had it for days. Login is the one event every
    /// affected character is guaranteed to reach, it happens once, and it is the point at which the
    /// player is about to try the thing that will fail.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    public static class AcademyExitAmnesty
    {
        /// <summary>The twenty landblocks holding weenies 29337-29340, the academy exit portals.
        /// See the class note for the query that regenerates this.</summary>
        static readonly HashSet<ushort> ACADEMY = new HashSet<ushort>
        {
            0x7202, 0x7203, 0x7204, 0x7302, 0x7303, 0x7F03, 0x7F04, 0x8002, 0x8003, 0x8004,
            0x8602, 0x8603, 0x8604, 0x8702, 0x8703, 0x8C04, 0x8D02, 0x8D03, 0x8D04, 0x8E02,
        };

        static void Postfix(Player __instance)
        {
            var p = __instance;
            if (p == null || !p.RecallsDisabled || p.Location == null) return;

            // Position.Landblock is the high 16 bits of the cell id -- the block, not the cell.
            var block = (ushort)(p.Location.Cell >> 16);
            if (ACADEMY.Contains(block)) return;   // still in the tutorial; the front door is theirs to walk

            p.RecallsDisabled = false;
            Mod.Log.Info($"[RevivalGuard] academy exit amnesty: {p.Name} (0x{p.Guid.Full:X8}) had "
                + $"RecallsDisabled set while standing in landblock 0x{block:X4}, which is not a "
                + $"Training Academy. It never walked out through an exit portal, so nothing was ever "
                + $"going to clear the flag and it could not attune to a lifestone or recall. Cleared.");
        }
    }
}
