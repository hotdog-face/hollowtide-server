namespace RevivalGuard
{
    /// <summary>The pure part of FellowshipShare: each member's share of an even-split fellowship's XP, by fellowship size.</summary>
    public static class FellowshipShareRule
    {
        // index = fellowship size; retail's floats as the exe stores them
        static readonly float[] s_Retail = { 0f, 1.0f, 0.75f, 0.6f, 0.55f, 0.5f, 0.45f, 0.4f, 0.35f, 0.3111111f, 0.28f };

        /// <summary>Retail's per-member share for count fellows, or null outside 1..10.</summary>
        public static double? EvenSplit(int count)
        {
            if (count < 1 || count >= s_Retail.Length) return null;
            return s_Retail[count];
        }
    }
}
