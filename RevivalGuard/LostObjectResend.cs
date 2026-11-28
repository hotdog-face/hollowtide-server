using System;
using System.Collections.Concurrent;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A retail client that lost a monster GETS it back.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionForceObjDescSend))]
    static class LostObjectResend
    {
        internal const string P_ON = "rg_lost_object_resend";
        static readonly ConcurrentDictionary<ulong, double> s_Last = new();
        static double s_LastLog, s_LastPrune, s_LastUnknownLog;
        internal static int Resent;
        static bool s_Registered;

        static bool Prepare()
        {
            if (!s_Registered)
            {
                s_Registered = true;
                CommandManager.TryAddCommand(LostTest, "lostobjtest", AccessLevel.Developer, CommandHandlerFlag.None,
                    "Admin test aid (ours, not retail): your client (or the named player's) forgets the nearest monster (as a retail client drops one it cannot place) and the monster comes for that player; the server keeps it as known.", "[player name]");
            }
            return true;
        }

        static void LostTest(Session session, params string[] parameters)
        {
            string who = parameters != null && parameters.Length > 0 ? string.Join(" ", parameters) : null;
            var p = who != null ? PlayerManager.GetOnlinePlayer(who) : session?.Player;
            void Say(string m) { if (session?.Player != null) session.Player.SendMessage(m); else Console.WriteLine(m); }
            if (p == null) { Say($"[lostobjtest] no online player {who}"); return; }
            var om = p.PhysicsObj?.ObjMaint;
            if (om == null) return;
            var c = om.GetKnownObjectsValues().Select(o => o?.WeenieObj?.WorldObject).OfType<Creature>()
                .Where(x => !(x is Player) && !x.IsDead && x.Location != null && x.Attackable)
                .OrderBy(x => p.Location.DistanceTo(x.Location)).FirstOrDefault();
            if (c == null) { Say($"[lostobjtest] no monster known to {p.Name}'s client"); return; }
            p.Session.Network.EnqueueSend(new GameMessageDeleteObject(c));
            c.AttackTarget = p; c.WakeUp(false);
            string msg = $"[lostobjtest] {p.Name}'s client forgot {c.Name} 0x{c.Guid.Full:X8} ({p.Location.DistanceTo(c.Location):0.0} m); still known to the server ({om.KnownObjectsContainsKey(c.Guid.Full)}); it is coming for {p.Name}";
            p.SendMessage(msg); if (session?.Player != p) Say(msg);
            Mod.Log.Info($"[RevivalGuard] lost object test: {p.Name} client told to forget {c.Name} 0x{c.Guid.Full:X8}");
        }

        static bool Prefix(Player __instance, uint itemGuid)
        {
            try
            {
                var p = __instance; var om = p?.PhysicsObj?.ObjMaint;
                if (om == null || !PropertyManager.GetBool(P_ON, true).Item) return true;
                var wo = om.GetKnownObject(itemGuid)?.WeenieObj?.WorldObject;
                double now = ACE.Common.Time.GetUnixTime();
                ulong key = ((ulong)p.Guid.Full << 32) | itemGuid;
                double last = s_Last.TryGetValue(key, out var l) ? l : double.NegativeInfinity;
                if (wo == null && now - s_LastUnknownLog > 30 && p.CurrentLandblock?.GetObject(itemGuid) is Creature hitter
                    && hitter.AttackTarget == p)
                {
                    // Hollowtide asks for its attacker when it has no object for it (ServerLink.TickLostAttacker).
                    s_LastUnknownLog = now;
                    Mod.Log.Info($"[RevivalGuard] lost object: {p.Name} (0x{p.Guid.Full:X8}) asked for {hitter.Name} (0x{itemGuid:X8}) attacking it, " +
                        $"which the server never counted as known to it; {p.Location?.DistanceTo(hitter.Location) ?? -1:0.0} m away at {hitter.Location?.ToLOCString()}, player at {p.Location?.ToLOCString()}");
                }
                if (!LostObjectRule.Resend(wo != null, wo != null && wo.CurrentLandblock != null && wo.Location != null,
                        wo != null && wo.Visibility, now, last)) return true;
                s_Last[key] = now;
                if (now - s_LastPrune > 600) { s_LastPrune = now; foreach (var kv in s_Last) if (now - kv.Value > 60) s_Last.TryRemove(kv.Key, out _); }
                p.TrackObject(wo);
                Resent++;
                if (now - s_LastLog > 5)
                {
                    s_LastLog = now;
                    Mod.Log.Info($"[RevivalGuard] lost object: {p.Name} (0x{p.Guid.Full:X8}) asked for {wo.Name} (0x{itemGuid:X8}, " +
                        $"{wo.Location.ToLOCString()}, {p.Location?.DistanceTo(wo.Location) ?? -1:0.0} m away) its client no longer had; sent it again ({Resent} since start)");
                }
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] lost object: {e.Message}"); return true; }
        }
    }

    /// <summary>LostObjectResend's rule, pure (tools/modcheck): resend the whole object, or leave ACE's appearance-only answer.</summary>
    public static class LostObjectRule
    {
        public const double MIN_GAP = 10.0;

        public static bool Resend(bool known, bool inWorld, bool serverOnly, double now, double lastResend) =>
            known && inWorld && !serverOnly && now - lastResend >= MIN_GAP;
    }
}
