namespace RevivalGuard
{
    /// <summary>DyeFailArmor's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class DyeFailArmorRule
    {
        /// <summary>The Armor Level after a recipe adds delta.</summary>
        public static int? After(int? current, int delta)
        {
            if (delta >= 0) return (current ?? 0) + delta;
            if (current == null) return null;
            return System.Math.Max(0, current.Value + delta);
        }
    }
}
