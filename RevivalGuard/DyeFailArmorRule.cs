namespace RevivalGuard
{
    /// <summary>DyeFailArmor's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// A failed dye (recipes 3844, 3845, 9068) takes Armor Level off the target; retail's floor is zero ("20 Armor Value is
    /// lost (cannot go below zero)", AC wiki Dyeing). ACE adds the loss unfloored, so a failed dye left a cloth shirt at
    /// AL -50, and ACE's CalcArmorMod turns negative AL into extra damage (1.75x at -50).</summary>
    public static class DyeFailArmorRule
    {
        /// <summary>The Armor Level after a recipe adds delta. Null: leave the property as it is (an item with no Armor
        /// Level gets none from a loss).</summary>
        public static int? After(int? current, int delta)
        {
            if (delta >= 0) return (current ?? 0) + delta;
            if (current == null) return null;
            return System.Math.Max(0, current.Value + delta);
        }
    }
}
