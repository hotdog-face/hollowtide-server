namespace RevivalGuard
{
    /// <summary>OffHandGrip's rule, pure (no ACE types), so tools/nettest checks it by path.</summary>
    public static class OffHandGripRule
    {
        public const uint SHIELD_SLOT = 0x00200000;
        public const int LEFT_WEAPON = 8, LEFT_UNARMED = 9, RIGHT_HAND_COMBAT = 1;
        public const int STYLE_UNARMED = 1, WEAPON_TYPE_UNARMED = 1;

        /// <summary>True with retail's hold point and placement for a non-armor item in the shield slot; false leaves ACE's answer.</summary>
        public static bool Retail(uint wieldedLocation, bool isArmor, int defaultCombatStyle, int weaponType,
                                  out int parentLocation, out int placement)
        {
            parentLocation = 0; placement = 0;
            if (wieldedLocation != SHIELD_SLOT || isArmor) return false;
            bool unarmed = defaultCombatStyle == STYLE_UNARMED || weaponType == WEAPON_TYPE_UNARMED;
            parentLocation = unarmed ? LEFT_UNARMED : LEFT_WEAPON;
            placement = RIGHT_HAND_COMBAT;
            return true;
        }
    }
}
