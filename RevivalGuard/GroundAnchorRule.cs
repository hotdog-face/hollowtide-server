namespace RevivalGuard
{
    /// <summary>GroundAnchor's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class GroundAnchorRule
    {
        /// <summary>ACE's z-pos check (Player_Tick.cs UpdatePlayerPosition): a report more than this above LastGroundPos,
        /// over a second after the last jump, snaps the player back to LastGroundPos.</summary>
        public const float RISE = 10f;

        /// <summary>True when ACE's z-pos check would fire only because LastGroundPos is stale: the report rises more than
        /// RISE above the last ground report, but outdoors it is within RISE of the terrain under it, so the player is
        /// running up a hill, not flying. Indoors (env cell index 0x100 and up) the terrain says nothing: ACE's check stands.</summary>
        public static bool Reanchor(bool outdoors, float reportZ, float lastGroundZ, float terrainZ) =>
            outdoors && reportZ - lastGroundZ > RISE && reportZ - terrainZ <= RISE;
    }
}
