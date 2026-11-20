namespace RevivalGuard
{
    /// <summary>TinkerLock's rule and words, pure (no ACE types), so tools/nettest compiles and checks them by path.</summary>
    public static class TinkerLockRule
    {
        public const int ALLOWED_WIELDER = 38;      // PropertyInstanceId.AllowedWielder
        public const int COPY_TO_TARGET = 3;        // ModificationOperation.CopyFromSourceToTarget
        public const int COPY_TO_RESULT = 4;        // ModificationOperation.CopyFromSourceToResult
        public const int SOURCE_PLAYER = 0;         // RecipeSourceType.Player

        public enum Bind { None, Target, Result }

        /// <summary>What one recipe_mods_i_i_d row locks to the player who applies the recipe. ACE's ModifyInstanceID
        /// writes ModifyInstanceIDRuleSet(prop, sourceMod, ...) = sourceMod's guid for AllowedWielder: on CopyFromSourceToTarget
        /// sourceMod is the player only when the row's source is Player (0); on CopyFromSourceToResult ACE passes the player
        /// whatever the row says (RecipeManager.cs "// ??"). AllowedActivator (31) is left out on purpose: ACE never refuses
        /// anything on it (only the appraisal flag reads it), so warning for its 400 recipes would warn of a lock that is not
        /// there.</summary>
        public static Bind Row(int stat, int op, int source)
        {
            if (stat != ALLOWED_WIELDER) return Bind.None;
            if (op == COPY_TO_TARGET && source == SOURCE_PLAYER) return Bind.Target;
            if (op == COPY_TO_RESULT) return Bind.Result;
            return Bind.None;
        }

        /// <summary>The question before the recipe runs (owner 2026-11-20: "so its more clear to players how it works").</summary>
        public static string Ask(string locked, string who, string source) =>
            $"This locks the {locked} to {who}: only {who} will be able to wield it, and it can't be undone. Apply the {source}?";

        /// <summary>The refusal when the item is already locked to another character.</summary>
        public static string Taken(string target, string owner, string source) =>
            $"The {target} is locked to {owner}: only {owner} can wield it, so you can't apply the {source} to it.";

        /// <summary>After No (or no answer): nothing was used or changed.</summary>
        public static string Declined(string target, string source) => $"You keep the {source}. The {target} is unchanged.";

        /// <summary>Who an existing lock names: the online or offline character's name, else the item's CraftsmanName (the
        /// binding recipes copy it from the player), else a plain phrase.</summary>
        public static string OwnerName(string byGuid, string craftsman) =>
            !string.IsNullOrEmpty(byGuid) ? byGuid : !string.IsNullOrEmpty(craftsman) ? craftsman : "another character";

        // ---- @unlockwield (owner 2026-11-20, "Staff command, next restart") ----

        /// <summary>The ways "<character> <item name>" can be read: split at a comma if there is one, else every word break,
        /// longest character name first (names and items both have spaces).</summary>
        public static System.Collections.Generic.List<(string who, string item)> Splits(string[] words)
        {
            var list = new System.Collections.Generic.List<(string, string)>();
            var all = string.Join(" ", words ?? new string[0]).Trim();
            int comma = all.IndexOf(',');
            if (comma > 0) { list.Add((all.Substring(0, comma).Trim(), all.Substring(comma + 1).Trim())); return list; }
            var w = all.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = w.Length - 1; i >= 1; i--)
                list.Add((string.Join(" ", w, 0, i), string.Join(" ", w, i, w.Length - i)));
            return list;
        }

        /// <summary>The items a query names: "0x&lt;guid&gt;" that guid; else every exact name (any case); else every name
        /// containing the query.</summary>
        public static System.Collections.Generic.List<int> Pick(System.Collections.Generic.IReadOnlyList<(uint guid, string name)> items, string query)
        {
            var hits = new System.Collections.Generic.List<int>();
            query = (query ?? "").Trim();
            if (query.Length == 0) return hits;
            if (query.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(query.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint g))
            {
                for (int i = 0; i < items.Count; i++) if (items[i].guid == g) hits.Add(i);
                return hits;
            }
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i].name, query, System.StringComparison.OrdinalIgnoreCase)) hits.Add(i);
            if (hits.Count > 0) return hits;
            for (int i = 0; i < items.Count; i++)
                if (items[i].name != null && items[i].name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(i);
            return hits;
        }
    }
}
