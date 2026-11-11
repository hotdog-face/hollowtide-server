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
    /// <summary>
    /// A RETAIL CLIENT THAT LOST A MONSTER GETS IT BACK (Demonchild 2026-11-11, level 75 in the Olthoi dungeon 0x02FC on a
    /// retail client: "getting smashed by invisible bugs ... nada on screen cant target nothing but im being hit", and the
    /// day before). The retail client drops an object on its own: one it could not place in a cell is queued for
    /// destruction and gone 25 s later (acclient_2013_decompiled.c ~90088-90106, CObjectMaint::AddObjectToBeDestroyed
    /// 261338), and the server is not told. ACE still counts it as known, so it never sends it again while it stays in
    /// sight, and its swings, moves and spells keep arriving for a guid the client no longer has. The client makes a
    /// placeholder ("null object") for each such guid and every 20 s asks for it with 0xF6EA ForceObjdesc
    /// (CObjectMaint::UseTime 261030-261082). Retail answered with the object; ACE's Player.HandleActionForceObjDescSend
    /// sends only an ObjDescEvent (its appearance), which a client without the object cannot use, so the monster stays
    /// invisible and untargetable while it hits. ACE's own /objsend says "rare cases of invisible object bugs".
    /// Here a request for a world object the server already counts as known to this player is answered with the whole
    /// object again (ACE's TrackObject: CreateObject, its wielded items, its current move). Anything else (an item in a
    /// pack, a guid it never sent, a hidden object) keeps ACE's answer; one resend per guid per 10 s.
    /// Switch: rg_lost_object_resend (on).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionForceObjDescSend))]
    static class LostObjectResend
    {
        internal const string P_ON = "rg_lost_object_resend";
        static readonly ConcurrentDictionary<ulong, double> s_Last = new();
        static double s_LastLog, s_LastPrune;
        internal static int Resent;
        static bool s_Registered;

        static bool Prepare()
        {
            if (!s_Registered)
            {
                s_Registered = true;
                CommandManager.TryAddCommand(LostTest, "lostobjtest", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                    "Admin test aid (ours, not retail): your client forgets the nearest monster (as a retail client drops one it cannot place) and the monster comes for you; the server keeps it as known.", "");
            }
            return true;
        }

        /// <summary>@lostobjtest: what a retail client does on its own, on demand: a DeleteObject to this client only for
        /// the nearest monster the server counts as known (still known), and the monster set on the player so its moves
        /// and swings arrive for a guid the client no longer has.</summary>
        static void LostTest(Session session, params string[] parameters)
        {
            var p = session?.Player; var om = p?.PhysicsObj?.ObjMaint;
            if (om == null) return;
            var c = om.GetKnownObjectsValues().Select(o => o?.WeenieObj?.WorldObject).OfType<Creature>()
                .Where(x => !(x is Player) && !x.IsDead && x.Location != null && x.Attackable)
                .OrderBy(x => p.Location.DistanceTo(x.Location)).FirstOrDefault();
            if (c == null) { p.SendMessage("[lostobjtest] no monster known to your client"); return; }
            session.Network.EnqueueSend(new GameMessageDeleteObject(c));
            c.AttackTarget = p; c.WakeUp(false);
            p.SendMessage($"[lostobjtest] your client forgot {c.Name} 0x{c.Guid.Full:X8} ({p.Location.DistanceTo(c.Location):0.0} m); still known to the server ({om.KnownObjectsContainsKey(c.Guid.Full)}); it is coming for you");
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

        /// <param name="known">the server counts the guid as known to this player (it was sent once)</param>
        /// <param name="inWorld">it stands in the world (not in a pack or wielded)</param>
        /// <param name="serverOnly">ACE's Visibility: never shown to clients</param>
        public static bool Resend(bool known, bool inWorld, bool serverOnly, double now, double lastResend) =>
            known && inWorld && !serverOnly && now - lastResend >= MIN_GAP;
    }
}
