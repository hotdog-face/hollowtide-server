using System;

namespace RevivalGuard
{
    /// <summary>Who is a borg (pure C#, nettest BorgRuleCase).</summary>
    public static class BorgRule
    {
        public const string ACCOUNT_PREFIX = "hcmerc";
        const string SUFFIX = " of Borg";

        /// <summary>The reserved account prefix: no player may make such an account (AccountCreationCap).</summary>
        public static bool IsShardAccountName(string accountName) =>
            accountName != null && accountName.Trim().StartsWith(ACCOUNT_PREFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>The name part only.</summary>
        public static bool HasBorgSuffix(string characterName) =>
            characterName != null && characterName.TrimEnd().EndsWith(SUFFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>A Borg: a shard account (the caller says whether the account is one) and the Borg suffix.</summary>
        public static bool IsBorg(bool shardAccount, string characterName) => shardAccount && HasBorgSuffix(characterName);

        /// <summary>The Hollow Company alone: a mercenary account (the caller's allowlist answer) that is not a Borg.</summary>
        public static bool IsHollowCompany(bool mercAccount, string characterName) => mercAccount && !IsBorg(true, characterName);

        /// <summary>Why a new character's name is refused, or null when it may be made.</summary>
        public static string RefuseName(string characterName, bool shardAccount, bool staff)
        {
            if (shardAccount || staff) return null;
            return HasBorgSuffix(characterName) ? "borg suffix" : null;
        }
    }
}
