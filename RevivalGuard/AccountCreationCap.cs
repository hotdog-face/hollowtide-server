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
    /// <summary>At most cap new accounts per address per day.</summary>
    [HarmonyPatch]
    static class AccountCreationCap
    {
        internal const string P_LAN = "rg_signup_cap_lan", P_RELAY_CAP = "rg_shared_relay_signups";
        const long RELAY_CAP = 50;
        static readonly int s_Cap = ParseCap();
        static readonly TimeSpan DAY = TimeSpan.FromHours(24);

        static int ParseCap()
        {
            var raw = Environment.GetEnvironmentVariable("REVIVAL_NEW_ACCOUNTS_PER_DAY");
            return int.TryParse(raw, out int n) && n >= 0 ? n : 3;
        }

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method("ACE.Server.Network.Handlers.AuthenticationHandler:DoLogin");

        /// <summary>Make rg_signup_cap_lan and rg_shared_relay_signups ordinary ACE properties, so @modifybool and @modifylong accept them</summary>
        internal static void Register()
        {
            ExpansionMotions.RegisterProperty(P_LAN, false, "RevivalGuard AccountCreationCap: apply the new-account cap to LAN addresses too (testing only; docs/ACCOUNT-SIGNUP.md)");
            try
            {
                var l = MonsterAi.Inner(ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_RELAY_CAP)) l[P_RELAY_CAP] = new ACE.Server.Managers.Property<long>(RELAY_CAP,
                    "RevivalGuard AccountCreationCap: new accounts per 24 h from a shared relay (rg_shared_relays, e.g. webAC); 0 = no cap");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] {P_RELAY_CAP}: not registered ({e.Message}); still read, default {RELAY_CAP}"); }
            StaffLoginGate.Register();
        }

        /// <summary>Would a new account from this address be refused right now?</summary>
        internal static bool Reached(IPAddress addr)
        {
            if (s_Cap <= 0 || addr == null) return false;
            if (StaffLoginGate.IsLocal(addr) && !PropertyManager.GetBool(P_LAN, false).Item) return false;
            long cap = s_Cap;
            if (StaffLoginGate.IsSharedRelay(addr))
                try { cap = PropertyManager.GetLong(P_RELAY_CAP, RELAY_CAP).Item; } catch { cap = RELAY_CAP; }
            return cap > 0 && CreatedToday(addr) >= cap;
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
                if (session == null || loginRequest == null) return true;
                // the address the account will be created from, for CreateAccount (Gateway): a browser player's own
                var addr = Gateway.RealAddress(session.EndPointC2S);
                Gateway.t_LoginReal = addr;
                // The mercenary prefix is reserved
                if (loginRequest.NetAuthType == NetAuthType.AccountPassword
                    && Mercenaries.IsReservedName(loginRequest.Account)
                    && DatabaseManager.Authentication.GetAccountByName(loginRequest.Account) == null)
                {
                    Mod.Log.Warn($"[RevivalGuard] new account '{loginRequest.Account}' refused: the hcmerc prefix is reserved ({addr})");
                    session.Terminate(SessionTerminationReason.NotAuthorizedAccountNotFound,
                        new GameMessageBootAccount(" because that account name is reserved; choose another"));
                    return false;
                }
                if (loginRequest.NetAuthType == NetAuthType.AccountPassword && BorgSwitch.RefuseLogin(session, loginRequest.Account))
                    return false;   // the Borg's off switch (BorgSwitch)
                if (s_Cap <= 0) return true;
                if (!ConfigManager.Config.Server.Accounts.AllowAutoAccountCreation) return true;
                if (loginRequest.NetAuthType != NetAuthType.AccountPassword || loginRequest.Password == "") return true;
                if (addr == null || !Reached(addr)) return true;
                if (DatabaseManager.Authentication.GetAccountByName(loginRequest.Account) != null) return true;

                Mod.Log.Warn($"[RevivalGuard] new account '{loginRequest.Account}' refused: {addr} already made its cap in 24 h");
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
