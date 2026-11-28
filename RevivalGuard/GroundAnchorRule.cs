namespace RevivalGuard
{
    /// <summary>GroundAnchor's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class GroundAnchorRule
    {
        /// <summary>ACE's z-pos check: a report more than this above LastGroundPos, over a second after the last jump</summary>
        public const float RISE = 10f;

        /// <summary>True when ACE's z-pos check would fire only because LastGroundPos is stale</summary>
        public static bool Reanchor(bool outdoors, float reportZ, float lastGroundZ, float terrainZ) =>
            outdoors && reportZ - lastGroundZ > RISE && reportZ - terrainZ <= RISE;
    }
}
