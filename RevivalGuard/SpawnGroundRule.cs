namespace RevivalGuard
{
    /// <summary>SpawnGround's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class SpawnGroundRule
    {
        /// <summary>Metres a spawn is looked under for ground (Raining Mad Cows' generators sit ~160 m up).</summary>
        public const float MAX_DROP = 200f;

        /// <summary>A creature a generator just placed is dropped only when retail's server would have let it fall: a
        /// non-player with Gravity, not Static, not AI-immobile (tentacles, Rynthid stones, the Horde stay where the data
        /// puts them), and touching nothing after ACE's placement.</summary>
        public static bool ShouldTry(bool creature, bool player, bool fromGenerator, bool gravity, bool isStatic,
                                     bool aiImmobile, bool contact)
            => creature && !player && fromGenerator && gravity && !isStatic && !aiImmobile && !contact;

        /// <summary>The sweep down is taken only when it lands on walkable ground within reach; anything else (no floor
        /// below, a wall, a slope too steep to stand on) leaves ACE's position alone. A spawn that starts in an EnvCell
        /// (a room: a keep's floor, an upper storey) must land in that same cell: its room's own floor, never the cell or
        /// terrain under it (report 261024-0031, a Candeth Keep collector about 20 ft under the ground). One that starts
        /// outdoors must land outdoors: a fall cannot reach a room or a dungeon under the landscape.</summary>
        public static bool Accept(float fell, bool landed, bool walkable, float max, bool startIndoors = false, bool sameCell = true,
                                  bool landIndoors = false)
            => landed && walkable && fell > 0.01f && fell <= max && (startIndoors ? sameCell : !landIndoors);

        /// <summary>An EnvCell (indoor) id: the low half 0x0100 and up.</summary>
        public static bool Indoors(uint cell) => (cell & 0xFFFFu) >= 0x100u;
    }
}
