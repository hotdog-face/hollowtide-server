using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// EVERY PLAYER CHARACTER IS ON THE DERETH NETWORK BY DEFAULT (owner 2026-11-11: "lets have it on for all players by
    /// default", and "On, no notice"). kazin's Dereth.Network mod (docs/proposals/dereth-network-join.md) is opt-in per
    /// character and has no server-wide default, so at login a character that has never chosen is put on the network the
    /// way "/dn on" does it (their public NetworkWorld.Dn), silently: the lines their mod would say to it then (the
    /// "/dn on" answer, and the hub's later directory-page line) are dropped. "/dn off" keeps working and is never undone:
    ///   * "never chosen" = no record in their store (NetworkWorld.SettingsOf is null). "/dn on" and "/dn off" both
    ///     leave a record, and their store never prunes one, so a character that turned it off has a record and stays off;
    ///   * the default is applied once per character, marked on it (DONE), so it is never re-applied even if their store
    ///     were lost.
    /// Never for non-players: staff (access level above Player), the Hollow Company and the Borg (hcmerc* accounts, " of
    /// Borg" names), and the dev fleet's accounts (devbot*, harness*, loadbot*). Their mod off (no key, not loaded):
    /// nothing happens and nothing is marked, so the next login tries again.
    ///   @modifybool rg_dn_default_on false     off (characters already put on stay on)
    /// </summary>
    static class DnDefaultOn
    {
        internal const string P_ON = "rg_dn_default_on";
        internal const PropertyBool DONE = (PropertyBool)29170;   // on the character: the default was applied (or it had chosen)
        static readonly string[] FLEET_PREFIXES = { "devbot", "harness", "loadbot" };
        const double QUIET_SECONDS = 120;   // the hub's directory answer to the join comes within seconds

        internal static void Register() =>
            PkCommon.RegisterBool(P_ON, true, "RevivalGuard DnDefaultOn: a player character that never chose is put on the Dereth Network at login, silently (false: opt-in only)");

        /// <summary>Why this character is not a player for the default, or null when it is one.</summary>
        internal static string NotAPlayer(Player p)
        {
            var acct = p.Account;
            if (acct == null) return "no account";
            if (acct.AccessLevel > 0) return "staff";
            string a = acct.AccountName ?? "";
            if (Mercenaries.IsReservedName(a) || BorgSwitch.IsBorgName(p.Name)) return "merc/borg";
            foreach (var f in FLEET_PREFIXES)
                if (a.StartsWith(f, StringComparison.OrdinalIgnoreCase)) return "dev fleet";
            return null;
        }

        // ---- their mod, by reflection (it may load after us, and we never build against it) ----
        static PropertyInfo s_Current;     // Dereth.Network.Mod.Current (internal static Runtime?)
        static bool s_Patched;
        static readonly object s_Lock = new object();

        static object World()
        {
            if (s_Current == null)
            {
                var mod = AccessTools.TypeByName("Dereth.Network.Mod");
                if (mod == null) return null;
                s_Current = AccessTools.Property(mod, "Current");
                if (s_Current == null) return null;
            }
            var runtime = s_Current.GetValue(null);
            return runtime == null ? null : AccessTools.Property(runtime.GetType(), "World")?.GetValue(runtime);
        }

        /// <summary>Their AceHost.ShowSystem gets our prefix once, the first time the default is applied.</summary>
        static bool EnsureQuiet()
        {
            lock (s_Lock)
            {
                if (s_Patched) return true;
                var target = AccessTools.Method("Dereth.Network.AceHost:ShowSystem", new[] { typeof(uint), typeof(string) });
                if (target == null) { Mod.Log.Warn("[RevivalGuard] DnDefaultOn: Dereth.Network's AceHost.ShowSystem not found; default not applied"); return false; }
                new Harmony("revival.guard.dndefault").Patch(target, prefix: new HarmonyMethod(typeof(DnDefaultOn), nameof(ShowSystemPrefix)));
                return s_Patched = true;
            }
        }

        [ThreadStatic] static uint t_Joining;
        static readonly ConcurrentDictionary<uint, DateTime> s_QuietUntil = new ConcurrentDictionary<uint, DateTime>();
        const string DIRECTORY_PAGE = "Your directory page: ";

        /// <summary>The lines their mod says to a character just put on silently: every line during the join, and the
        /// directory's answer to it for a short while after (its page, or why it has none). False = not shown.</summary>
        static bool ShowSystemPrefix(uint __0, string __1)
        {
            if (t_Joining != 0 && t_Joining == __0) return false;
            if (!s_QuietUntil.TryGetValue(__0, out var until)) return true;
            if (DateTime.UtcNow > until) { s_QuietUntil.TryRemove(__0, out _); return true; }
            string t = __1 ?? "";
            if (t.StartsWith(DIRECTORY_PAGE, StringComparison.Ordinal) || t.StartsWith("This network's hub keeps no directory", StringComparison.Ordinal)
                || t.StartsWith("The Dereth Network's directory holds", StringComparison.Ordinal)
                || t.StartsWith("The Dereth Network's operator has taken this character out of the directory", StringComparison.Ordinal))
            {
                s_QuietUntil.TryRemove(__0, out _);   // once: the directory's one answer to the join (a later "@dn" shows its page)
                Mod.Log.Info($"[RevivalGuard] DnDefaultOn: {__0:X8} not shown: {t}");
                return false;
            }
            return true;
        }

        static bool? IsOnOf(object world, uint guid)
        {
            var settings = AccessTools.Method(world.GetType(), "SettingsOf")?.Invoke(world, new object[] { guid });
            if (settings == null) return null;
            return AccessTools.Property(settings.GetType(), "On")?.GetValue(settings) as bool?;
        }

        /// <summary>PlayerEnterWorld calls PlayerManager.SwitchPlayerFromOfflineToOnline, which is where their mod sees the
        /// login, so after it the character is theirs. Before our client's own "@dn" probe (a few seconds into the world),
        /// so the client reads "on".</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                var p = __instance;
                try
                {
                    if (p == null || p.GetProperty(DONE) == true || !PropertyManager.GetBool(P_ON, true).Item) return;
                    string why = NotAPlayer(p);
                    if (why != null) return;
                    var world = World();
                    if (world == null) return;   // their mod off or not loaded: try at the next login
                    uint guid = p.Guid.Full;
                    var before = IsOnOf(world, guid);
                    if (before != null)
                    {
                        // it has a record: it chose (on or off) or was taken off by the network; never changed here
                        p.SetProperty(DONE, true);
                        Mod.Log.Info($"[RevivalGuard] DnDefaultOn: {p.Name} already chose ({(before == true ? "on" : "off")}); left as it is");
                        return;
                    }
                    if (!EnsureQuiet()) return;
                    var dn = AccessTools.Method(world.GetType(), "Dn", new[] { typeof(uint), typeof(IReadOnlyList<string>) });
                    if (dn == null) { Mod.Log.Warn("[RevivalGuard] DnDefaultOn: NetworkWorld.Dn not found; default not applied"); return; }
                    s_QuietUntil[guid] = DateTime.UtcNow.AddSeconds(QUIET_SECONDS);
                    t_Joining = guid;
                    try { dn.Invoke(world, new object[] { guid, new List<string> { "on" } }); }
                    finally { t_Joining = 0; }
                    if (IsOnOf(world, guid) == true)
                    {
                        p.SetProperty(DONE, true);
                        Mod.Log.Info($"[RevivalGuard] DnDefaultOn: {p.Name} ({p.Account?.AccountName}) put on the Dereth Network (default)");
                    }
                    else
                    {
                        s_QuietUntil.TryRemove(guid, out _);
                        Mod.Log.Warn($"[RevivalGuard] DnDefaultOn: {p.Name} could not be put on the network; tried again at the next login");
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DnDefaultOn {p?.Name}: {e.GetBaseException().Message}"); }
            }
        }
    }
}
