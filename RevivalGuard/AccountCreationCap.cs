using System;
using System.Linq;
using System.Net;
using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Packets;
using HarmonyLib;
using Microsoft.EntityFrameworkCore;

namespace RevivalGuard
{
    /// <summary>
    /// AT MOST CAP NEW ACCOUNTS PER ADDRESS PER DAY (docs/ACCOUNT-SIGNUP.md, owner 2026-10-01).
    /// ACE's AllowAutoAccountCreation makes an account for any unknown name with a password, so one
    /// script could fill the auth table. This prefix on AuthenticationHandler.DoLogin refuses the
    /// login BEFORE ACE creates the account when the address has already created CAP accounts in the
    /// last 24 hours, counted from ace_auth.account's own create_Time / create_I_P (so the count
    /// survives a restart). Logins to accounts that exist are never touched, and neither is
    /// multiboxing: MaximumAllowedSessionsPerIPAddress stays as configured.
    ///
    /// Loopback, LAN and Tailscale addresses are exempt (the fleet and load tests make accounts from
    /// there), the same rule as StaffLoginGate's throttle, unless the shard property
    /// rg_signup_cap_lan is true (for testing the cap from the LAN). REVIVAL_NEW_ACCOUNTS_PER_DAY sets
    /// CAP (default 3; 0 turns the cap off). The login screen learns about the cap before it sends a
    /// password, through AccountQuery's answer 3, and says so in words; this boot is the backstop
    /// for a client that did not ask.
    /// </summary>
    [HarmonyPatch]
    static class AccountCreationCap
    {
        internal const string P_LAN = "rg_signup_cap_lan";
        static readonly int s_Cap = ParseCap();
        static readonly TimeSpan DAY = TimeSpan.FromHours(24);

        static int ParseCap()
        {
            var raw = Environment.GetEnvironmentVariable("REVIVAL_NEW_ACCOUNTS_PER_DAY");
            return int.TryParse(raw, out int n) && n >= 0 ? n : 3;
        }

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:DoLogin");

        /// <summary>Make rg_signup_cap_lan an ordinary ACE property, so @modifybool accepts it.</summary>
        internal static void Register() =>
            ExpansionMotions.RegisterProperty(P_LAN, false, "RevivalGuard AccountCreationCap: apply the new-account cap to LAN addresses too (testing only; docs/ACCOUNT-SIGNUP.md)");

        /// <summary>Would a new account from this address be refused right now?</summary>
        internal static bool Reached(IPAddress addr)
        {
            if (s_Cap <= 0 || addr == null) return false;
            if (StaffLoginGate.IsLocal(addr) && !PropertyManager.GetBool(P_LAN, false).Item) return false;
            return CreatedToday(addr) >= s_Cap;
        }

        static int CreatedToday(IPAddress addr)
        {
            var a = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
            var want = a.GetAddressBytes();
            var since = DateTime.UtcNow - DAY;
            using (var ctx = new AuthDbContext())
            {
                var ips = ctx.Account.AsNoTracking().Where(r => r.CreateTime >= since).Select(r => r.CreateIP).ToList();
                return ips.Count(ip => ip != null && ip.SequenceEqual(want));
            }
        }

        static bool Prefix(Session session, PacketInboundLoginRequest loginRequest)
        {
            try
            {
                if (s_Cap <= 0 || session == null || loginRequest == null) return true;
                if (!ConfigManager.Config.Server.Accounts.AllowAutoAccountCreation) return true;
                if (loginRequest.NetAuthType != NetAuthType.AccountPassword || loginRequest.Password == "") return true;
                var addr = session.EndPointC2S?.Address;
                if (addr == null || !Reached(addr)) return true;
                if (DatabaseManager.Authentication.GetAccountByName(loginRequest.Account) != null) return true;

                Mod.Log.Warn($"[RevivalGuard] new account '{loginRequest.Account}' refused: {addr} already made {s_Cap} in 24 h");
                session.Terminate(SessionTerminationReason.NotAuthorizedAccountNotFound,
                    new GameMessageBootAccount(" because this network has made too many new accounts today; try again tomorrow"));
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Error("[RevivalGuard] AccountCreationCap: " + e.Message);
                return true;
            }
        }
    }
}
