namespace RevivalGuard
{
    /// <summary>
    /// OffHandGrip's rule, pure (no ACE types), so tools/nettest checks it by path. Where retail hung an item in the
    /// shield slot, from every ParentEvent and create in the 2014-2017 retail captures (docs/findings/weapon-audit-2026-10.md):
    ///   shield (armor)          -> ParentLocation Shield 3,      Placement Shield 6          (ACE agrees)
    ///   off-hand weapon         -> ParentLocation LeftWeapon 8,  Placement RightHandCombat 1 (ACE: RightHandNonCombat 2)
    ///   off-hand cestus/nekode  -> ParentLocation LeftUnarmed 9, Placement RightHandCombat 1 (ACE: LeftWeapon 8, 2)
    /// Placement 2 is a different grip in the weapon's own setup: a spear or staff slid 0.3-0.4 m along the shaft, a
    /// nekode turned. Hold point 9 on the body is turned ~180 degrees from 8.
    /// </summary>
    public static class OffHandGripRule
    {
        public const uint SHIELD_SLOT = 0x00200000;
        public const int LEFT_WEAPON = 8, LEFT_UNARMED = 9, RIGHT_HAND_COMBAT = 1;
        public const int STYLE_UNARMED = 1, WEAPON_TYPE_UNARMED = 1;

        /// <summary>True with retail's hold point and placement for a non-armor item in the shield slot; false leaves
        /// ACE's answer (main hand, missile, shields, anything else).</summary>
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
