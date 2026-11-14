namespace RevivalGuard
{
    /// <summary>
    /// The pure part of FellowshipShare (no ACE types; tools/nettest compiles it by path, FellowshipShareCase): each
    /// member's share of an even-split fellowship's XP, by fellowship size. Retail's table is
    /// FellowshipSystem::GetEvenSplitXPPctg @005b9ba0 in acclient_2013-09-06.exe (float constants: 9 = 0.3111111f at
    /// 0x7e72b8, 10 = 0.28f at 0x7e72b4). The Fever Dreams notes (Feb 2002) say 9-member fellowships went from 30% to
    /// 31%, so the total plateaus at 280% from 7 members on. ACE has 0.3 for 9 (270%) and no 10.
    /// </summary>
    public static class FellowshipShareRule
    {
        // index = fellowship size; retail's floats as the exe stores them
        static readonly float[] s_Retail = { 0f, 1.0f, 0.75f, 0.6f, 0.55f, 0.5f, 0.45f, 0.4f, 0.35f, 0.3111111f, 0.28f };

        /// <summary>Retail's per-member share for <paramref name="count"/> fellows, or null outside 1..10 (retail
        /// returns 0 there; the caller keeps ACE's value instead).</summary>
        public static double? EvenSplit(int count)
        {
            if (count < 1 || count >= s_Retail.Length) return null;
            return s_Retail[count];
        }
    }
}
