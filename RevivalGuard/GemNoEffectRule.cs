namespace RevivalGuard
{
    /// <summary>GemNoEffectGuard's refusal line, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class GemNoEffectRule
    {
        /// <summary>The line for a gem with no effect of its own. The Arcanum's XP and luminance gems and trinkets (Gems of
        /// Knowledge, Gems of Luminance, Ancient Trinkets) are a turn-in: their Use or LongDesc text names "an Agent of the
        /// Arcanum" (ACE world data, e.g. 36509, 52748). Report 261029-0008: "cannot be used by itself" read as a bug.</summary>
        public static string Message(string name, string useText, string longDesc)
        {
            if (name == "Coalesced Aetheria") return "Use an Aetheria Mana Stone on the Coalesced Aetheria to reveal it.";
            if (Names(useText, "Agent of the Arcanum") || Names(longDesc, "Agent of the Arcanum"))
                return $"Turn the {name} in to an Agent of the Arcanum to use it.";
            return $"The {name} cannot be used by itself.";
        }

        public enum Needs { Weapon, Wand, WeaponOrWand, Armor }

        /// <summary>The line for a gem whose spell lands on a wielded or worn item when none can take it (report
        /// 261123-0021: Rage of Grael with no weapon wielded). The gem is kept. `unenchantable`: something is wielded but
        /// it resists all magic.</summary>
        public static string WieldFirst(string name, Needs needs, bool unenchantable = false)
        {
            if (unenchantable) return $"Your wielded item cannot be enchanted, so the {name} was not used.";
            if (needs == Needs.Armor) return $"Wear enchantable armor first: the {name} enchants what you wear. It was not used.";
            string what = needs == Needs.Wand ? "a casting implement"
                : needs == Needs.WeaponOrWand ? "a weapon or casting implement" : "a weapon";
            return $"Wield {what} first: the {name} enchants what you wield. It was not used.";
        }

        static bool Names(string text, string who) =>
            text != null && text.IndexOf(who, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
