using System;

namespace RevivalGuard
{
    /// <summary>
    /// WHO IS A BORG (pure C#, nettest BorgRuleCase). A Borg is a character on one of the shard's own accounts (the
    /// reserved hcmerc* accounts, tools/borg runs them) whose name ends " of Borg". The name alone never makes one: a
    /// player could call a character "X of Borg" (M5 test 2026-11-19, owner: "fix"), so every check goes through
    /// <see cref="IsBorg"/> with the account, and new player characters may not take the suffix (<see cref="RefuseName"/>).
    /// In the server, BorgSwitch.IsBorg(Player) / IsBorgAccount pass the allowlist (Mercenaries.IsMercAccount).
    /// </summary>
    public static class BorgRule
    {
        public const string ACCOUNT_PREFIX = "hcmerc";
        const string SUFFIX = " of Borg";

        /// <summary>The reserved account prefix: no player may make such an account (AccountCreationCap).</summary>
        public static bool IsShardAccountName(string accountName) =>
            accountName != null && accountName.Trim().StartsWith(ACCOUNT_PREFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>The name part only. Never a Borg check by itself: use <see cref="IsBorg"/>.</summary>
        public static bool HasBorgSuffix(string characterName) =>
            characterName != null && characterName.TrimEnd().EndsWith(SUFFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>A Borg: a shard account (the caller says whether the account is one) and the Borg suffix.</summary>
        public static bool IsBorg(bool shardAccount, string characterName) => shardAccount && HasBorgSuffix(characterName);

        /// <summary>The Hollow Company alone: a mercenary account (the caller's allowlist answer) that is not a Borg. In PK
        /// (the honour check, PK Night's hill) the Borg count as players (owner 2026-11-22, docs/proposals/pk-night-v2.md
        /// item 5); the sellswords never do.</summary>
        public static bool IsHollowCompany(bool mercAccount, string characterName) => mercAccount && !IsBorg(true, characterName);

        /// <summary>Why a new character's name is refused, or null when it may be made. Staff and the shard's own accounts
        /// are exempt; anyone else may not end a name in " of Borg" (any case). For logs only.</summary>
        public static string RefuseName(string characterName, bool shardAccount, bool staff)
        {
            if (shardAccount || staff) return null;
            return HasBorgSuffix(characterName) ? "borg suffix" : null;
        }
    }
}
