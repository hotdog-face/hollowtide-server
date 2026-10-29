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

        static bool Names(string text, string who) =>
            text != null && text.IndexOf(who, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
