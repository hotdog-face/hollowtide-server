using System;
using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// A trade closes for both sides when one of them leaves the world (log out, character switch, disconnect, crash).
    /// ACE's LogOut_Inner closes the partner's side, but only once the session ends: a crash or a dropped link left the
    /// partner's trade open for the whole session timeout (DefaultSessionTimeout 300 s on our shards; box 2026-10-29:
    /// a kill -9 closed it at 5:01). And: a PK's delayed logoff (frozen for pk_timer, often a dropped
    /// link) kept the trade open the whole time; a leave during an accepted trade's 0.5 s escrow hit
    /// HandleActionCloseTradeNegotiations' TradeTransferInProgress early return, so the partner's window stayed open
    /// against nobody for good; the leaver's own state was never cleared; and the partner was told only "cancelled".
    /// Here the leave closes it at LogOut (first) and LogOut_Inner, waits out an escrow, and a 2 s sweep closes any
    /// trade whose partner is gone or whose partner's client has sent nothing for 20 s (a crash closes in ~20 s). Items never leave a pack before the escrow, so closing moves nothing: each side
    /// keeps what it offered. Rule: TradeLeaveRule.cs (tools/nettest checks it).
    /// </summary>
    static class TradeLeaveClose
    {
        static readonly MethodInfo s_SetTrading = AccessTools.PropertySetter(typeof(Player), nameof(Player.IsTrading));   // private set
        static readonly FieldInfo s_Accepted = AccessTools.Field(typeof(Player), "TradeAccepted");
        static DateTime s_NextSweep;

        internal static void Leaving(Player leaver)
        {
            if (leaver == null || !leaver.IsTrading) return;
            var partnerGuid = leaver.TradePartner;
            var partner = partnerGuid == ObjectGuid.Invalid ? null : PlayerManager.GetOnlinePlayer(partnerGuid);
            if (partner != null && partner != leaver)
            {
                var act = TradeLeaveRule.Decide(partner.IsTrading && partner.TradePartner == leaver.Guid,
                    leaver.TradeTransferInProgress || partner.TradeTransferInProgress);
                if (act == TradeLeaveRule.Act.Wait)
                {
                    // the escrow chain runs ResetTrade on both, not Close; close after it, leaver state included
                    Retry(partner, leaver, TradeLeaveRule.WAIT_TRIES);
                    return;
                }
                if (act == TradeLeaveRule.Act.Close)
                    Close(partner, leaver.Guid, leaver.Name);
            }
            // cleared here, ACE's LogOut_Inner skips its own partner close (no second "cancelled" line)
            Close(leaver, partnerGuid, null);
        }

        static void Retry(Player partner, Player leaver, int triesLeft)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(TradeLeaveRule.WAIT_S);
            chain.AddAction(partner, () =>
            {
                try
                {
                    if (leaver.TradeTransferInProgress || partner.TradeTransferInProgress)
                    {
                        if (triesLeft > 1) { Retry(partner, leaver, triesLeft - 1); return; }
                        Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose: escrow between {leaver.Name} and {partner.Name} still running after {TradeLeaveRule.WAIT_TRIES} s; the sweep will close it");
                        return;
                    }
                    if (partner.IsTrading && partner.TradePartner == leaver.Guid)
                        Close(partner, leaver.Guid, leaver.Name);
                    if (leaver.IsTrading) Close(leaver, partner.Guid, null);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose retry: {e.Message}"); }
            });
            chain.EnqueueChain();
        }

        /// <summary>Ends <paramref name="p"/>'s side as HandleActionCloseTradeNegotiations does, minus its generic
        /// TradeClosed error; <paramref name="leaverName"/> non-null sends the reason line.</summary>
        static void Close(Player p, ObjectGuid otherGuid, string leaverName)
        {
            if (p.TradeTransferInProgress) return;
            if (s_SetTrading == null || s_Accepted == null) { p.HandleActionCloseTradeNegotiations(); return; }   // ACE renamed them
            s_SetTrading.Invoke(p, new object[] { false });
            s_Accepted.SetValue(p, false);
            p.ItemsInTradeWindow.Clear();
            p.TradePartner = ObjectGuid.Invalid;
            if (otherGuid != ObjectGuid.Invalid) p.KnownTradeObjs.Remove(otherGuid);
            try
            {
                var s = p.Session;
                if (s?.Network == null) return;
                s.Network.EnqueueSend(new GameEventCloseTrade(s, EndTradeReason.Normal));
                if (leaverName != null)
                    s.Network.EnqueueSend(new GameMessageSystemChat(TradeLeaveRule.Line(leaverName), ChatMessageType.Broadcast));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose send to {p.Name}: {e.Message}"); }
        }

        /// <summary>Seconds since the last packet from this player's client: ACE pushes TimeoutTick to now +
        /// DefaultSessionTimeout on every packet it processes (NetworkSession.ProcessPacket).</summary>
        static double SilentSeconds(Player p)
        {
            var n = p.Session?.Network;
            if (n == null) return 0;
            var last = n.TimeoutTick - TimeSpan.FromSeconds(NetworkManager.DefaultSessionTimeout).Ticks;
            return Math.Max(0, (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerSecond);
        }

        /// <summary>Catch-all for any path that skipped LogOut (ForceLogoff, a throw mid-logout): a trade whose partner
        /// is offline, or online and no longer trading with this player, closes.</summary>
        internal static void Sweep()
        {
            if (DateTime.UtcNow < s_NextSweep) return;
            s_NextSweep = DateTime.UtcNow.AddSeconds(TradeLeaveRule.SWEEP_S);
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (!p.IsTrading || p.TradeTransferInProgress || p.IsLoggingOut) continue;
                var partnerGuid = p.TradePartner;
                var partner = partnerGuid == ObjectGuid.Invalid ? null : PlayerManager.GetOnlinePlayer(partnerGuid);
                if (partner != null && partner.TradeTransferInProgress) continue;
                if (partner != null && partner.IsTrading && partner.TradePartner == p.Guid)
                {
                    // a two-sided trade: the partner's link gone quiet (a crash or a pulled cable) counts as leaving,
                    // rather than holding the trade open for ACE's session timeout (300 s on our shards)
                    var quiet = SilentSeconds(partner);
                    if (TradeLeaveRule.LinkLost(quiet))
                    {
                        Mod.Log.Info($"[RevivalGuard] TradeLeaveClose: {partner.Name} silent {quiet:F0} s, closing the trade with {p.Name}");
                        var gone = partner;
                        var c = new ActionChain();
                        c.AddAction(p, () => { try { Leaving(gone); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose link: {e.Message}"); } });
                        c.EnqueueChain();
                    }
                    continue;
                }
                if (!TradeLeaveRule.Orphaned(true, partner != null && !partner.IsLoggingOut,
                        partner != null && partner.IsTrading && partner.TradePartner == p.Guid)) continue;
                var name = partner?.Name ?? (partnerGuid == ObjectGuid.Invalid ? null : PlayerManager.FindByGuid(partnerGuid)?.Name);
                var chain = new ActionChain();
                chain.AddAction(p, () =>
                {
                    try { if (p.IsTrading && p.TradePartner == partnerGuid && !p.TradeTransferInProgress) Close(p, partnerGuid, name); }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose sweep: {e.Message}"); }
                });
                chain.EnqueueChain();
                Mod.Log.Info($"[RevivalGuard] TradeLeaveClose: {p.Name}'s trade with {name ?? "?"} closed by the sweep (partner gone)");
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.LogOut))]
    static class TradeLeaveClose_LogOut
    {
        // before PK delayed logoff's early return: a frozen, maybe disconnected, player does not keep a trade open
        static void Prefix(Player __instance)
        {
            try { TradeLeaveClose.Leaving(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose LogOut: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.LogOut_Inner))]
    static class TradeLeaveClose_LogOutInner
    {
        // PlayerManager.Tick's PK queue and the dev forcelogoff call this directly
        static void Prefix(Player __instance)
        {
            try { TradeLeaveClose.Leaving(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TradeLeaveClose LogOut_Inner: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class TradeLeaveClose_Tick
    {
        static void Postfix()
        {
            try { TradeLeaveClose.Sweep(); }
            catch (Exception e) { TickGuard.Report("TradeLeaveClose", e); }
        }
    }
}
