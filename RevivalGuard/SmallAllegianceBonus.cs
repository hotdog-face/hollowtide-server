using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// SMALL-ALLEGIANCE BONUS (owner 2026-11-25, picked from three: "Bonus for small allegiances: smaller allegiances get
    /// extra XP pass-up and luminance, fading as they grow; nobody is forced out"; numbers docs/proposals/allegiance-balance.md).
    /// Ours, not retail (docs/DIVERGENCES.md "Small-allegiance bonus").
    ///
    ///   Pass-up: the XP a member earns is counted (1 + bonus) times when ACE passes it up the chain (AllegianceManager.
    ///   PassXP, direct call only, so each patron above gets ACE's own share of the bigger amount). The member's own XP is
    ///   unchanged. Luminance: a member's kill and quest luminance gets (1 + bonus) (Player.AddLuminance, after the
    ///   fellowship split). Bonus: AllegianceBalanceRule.Bonus by the allegiance's distinct ACCOUNTS, monarch's included
    ///   (owner 2026-11-27 "alt-proof": a main and its alts are one; one account alone gets nothing), and no pass-up bonus
    ///   between two characters of one account. Never for an allegiance whose monarch is a bot or staff (Recruiting.BotOrStaff).
    ///
    /// Switch rg_small_alleg_bonus (default OFF). Numbers, each a server property so they move without a stage:
    /// rg_small_alleg_full (10), rg_small_alleg_zero (40), rg_small_alleg_passup (0.25), rg_small_alleg_lum (0.12).
    /// </summary>
    public static class SmallAllegianceBonus
    {
        internal const string P_ON = "rg_small_alleg_bonus", P_FULL = "rg_small_alleg_full", P_ZERO = "rg_small_alleg_zero",
            P_PASSUP = "rg_small_alleg_passup", P_LUM = "rg_small_alleg_lum";

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard: small allegiances pass up more XP and earn more luminance, fading as they grow (docs/proposals/allegiance-balance.md)");
            PkCommon.RegisterLong(P_FULL, AllegianceBalanceRule.FULL_UP_TO, "RevivalGuard small-allegiance bonus: full bonus up to this many members");
            PkCommon.RegisterLong(P_ZERO, AllegianceBalanceRule.ZERO_AT, "RevivalGuard small-allegiance bonus: no bonus from this many members");
            PkCommon.RegisterDouble(P_PASSUP, AllegianceBalanceRule.PASSUP_MAX, "RevivalGuard small-allegiance bonus: extra XP pass-up at full bonus (0.25 = +25%)");
            PkCommon.RegisterDouble(P_LUM, AllegianceBalanceRule.LUM_MAX, "RevivalGuard small-allegiance bonus: extra luminance at full bonus (0.12 = +12%)");
            Mod.Log.Info($"[RevivalGuard] SmallAllegianceBonus: {(On ? "ON" : "off")} ({P_ON})");
        }

        // what the bonus has added since start-up (staff: @allegiancedir), so its effect can be read without a debugger
        static long s_PassupTimes, s_PassupExtra, s_LumTimes, s_LumExtra, s_SameAccount;
        internal static string Paid() =>
            $"small-allegiance bonus {(On ? "ON" : "off")}: pass-up boosted {s_PassupTimes} times (+{s_PassupExtra:N0} XP counted), luminance boosted {s_LumTimes} times (+{s_LumExtra:N0}); {s_SameAccount} same-account passes left unboosted";

        internal static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        static long Full => PkCommon.Long(P_FULL, AllegianceBalanceRule.FULL_UP_TO);
        static long Zero => PkCommon.Long(P_ZERO, AllegianceBalanceRule.ZERO_AT);

        /// <summary>(pass-up, luminance) bonus fractions for an allegiance of this many ACCOUNTS now; (0, 0) while off.</summary>
        internal static (double passup, double lum) For(int members)
        {
            if (!On) return (0, 0);
            return (AllegianceBalanceRule.Bonus(members, Full, Zero, PkCommon.Dbl(P_PASSUP, AllegianceBalanceRule.PASSUP_MAX)),
                    AllegianceBalanceRule.Bonus(members, Full, Zero, PkCommon.Dbl(P_LUM, AllegianceBalanceRule.LUM_MAX)));
        }

        internal static (double passup, double lum) For(Allegiance a)
        {
            if (a == null || !On) return (0, 0);
            if (Recruiting.BotOrStaff(a.Monarch?.Player)) return (0, 0);
            return For(AccountsOf(a));
        }

        internal static uint AccountOf(IPlayer p)
        {
            try { return p?.Account?.AccountId ?? 0; } catch { return 0; }
        }

        internal static DateTime? LastLogin(IPlayer p)
        {
            try
            {
                var t = p?.GetProperty(PropertyFloat.LoginTimestamp);
                return t == null || t <= 0 ? (DateTime?)null : DateTimeOffset.FromUnixTimeMilliseconds((long)(t.Value * 1000)).UtcDateTime;
            }
            catch { return null; }
        }

        /// <summary>(distinct accounts, distinct accounts active this week) of these characters; no database reads (ACE
        /// keeps every character's Account in memory).</summary>
        internal static (int accounts, int active) Count(IEnumerable<IPlayer> members, DateTime now)
        {
            var who = new List<AllegianceBalanceRule.Who>();
            foreach (var ip in members)
            {
                if (ip == null) continue;
                bool on = PlayerManager.GetOnlinePlayer(ip.Guid) != null;
                who.Add(new AllegianceBalanceRule.Who { Guid = ip.Guid.Full, Account = AccountOf(ip), Active = AllegianceFinderRule.ActiveThisWeek(LastLogin(ip), on, now) });
            }
            return AllegianceBalanceRule.Accounts(who);
        }

        const int ACCOUNTS_CACHE_S = 60;
        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, (int n, DateTime at)> s_Accounts = new Dictionary<uint, (int, DateTime)>();

        /// <summary>The allegiance's distinct accounts, monarch's included (a minute's cache: pass-up runs on every kill).</summary>
        internal static int AccountsOf(Allegiance a)
        {
            var now = DateTime.UtcNow;
            uint key = a.Monarch?.PlayerGuid.Full ?? 0;
            lock (s_Lock) if (s_Accounts.TryGetValue(key, out var c) && (now - c.at).TotalSeconds < ACCOUNTS_CACHE_S) return c.n;
            var members = new List<IPlayer>();
            foreach (var node in a.Members.Values.ToArray()) { var ip = node?.Player; if (ip != null) members.Add(ip); }
            int n = Count(members, now).accounts;
            lock (s_Lock) s_Accounts[key] = (n, now);
            return n;
        }

        [HarmonyPatch(typeof(AllegianceManager), nameof(AllegianceManager.PassXP))]
        static class PassUp
        {
            static void Prefix(AllegianceNode vassalNode, ref ulong amount, bool direct)
            {
                try
                {
                    if (!direct || amount == 0 || vassalNode?.Patron == null) return;
                    var b = For(vassalNode.Allegiance).passup;
                    if (b <= 0) return;
                    // one player's alts can't farm the bonus: none between two characters of one account
                    b = AllegianceBalanceRule.PassupFor(b, AccountOf(vassalNode.Player), AccountOf(vassalNode.Patron.Player));
                    if (b <= 0) { System.Threading.Interlocked.Increment(ref s_SameAccount); return; }
                    var was = amount;
                    amount = AllegianceBalanceRule.Apply(amount, b);
                    System.Threading.Interlocked.Increment(ref s_PassupTimes);
                    System.Threading.Interlocked.Add(ref s_PassupExtra, (long)Math.Min(amount - was, (ulong)long.MaxValue));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SmallAllegianceBonus pass-up: {e.Message}"); }
            }
        }

        [HarmonyPatch]
        static class Luminance
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(Player), "AddLuminance", new[] { typeof(long), typeof(XpType) });
                if (m == null) Mod.Log.Error("[RevivalGuard] SmallAllegianceBonus: Player.AddLuminance not found; no luminance bonus");
                return m;
            }

            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static void Prefix(Player __instance, ref long amount, XpType xpType)
            {
                try
                {
                    if (amount <= 0 || (xpType != XpType.Kill && xpType != XpType.Quest)) return;
                    var b = For(__instance?.Allegiance).lum;
                    if (b <= 0) return;
                    var was = amount;
                    amount = AllegianceBalanceRule.Apply(amount, b);
                    System.Threading.Interlocked.Increment(ref s_LumTimes);
                    System.Threading.Interlocked.Add(ref s_LumExtra, amount - was);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SmallAllegianceBonus luminance: {e.Message}"); }
            }
        }
    }
}
