using System;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE GAUNTLET ARENA SAYS WHY IT REFUSED YOU (owner report 261115-0024: "tell players exactly why i cant enter").
    /// The three society arena portals (world data, unchanged) send one DirectBroadcast, "You do not have access to this
    /// arena.", for every refusal: no fellowship (QuestNoFellow), no ticket stamp on the fellowship, an expired one, or a
    /// stamp for another society's arena. That line is rewritten here, after EmoteManager.Replace, into the actual reason,
    /// read from the same fellowship and player quest state the arena and the Master of the Gauntlet check. Every client
    /// gets it, retail included. Wording and order: GauntletAccessRule.
    /// </summary>
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.Replace))]
    static class GauntletAccess
    {
        static void Postfix(WorldObject source, WorldObject target, ref string __result)
        {
            try
            {
                if (__result != GauntletAccessRule.AceRefusal) return;
                var arena = GauntletAccessRule.ArenaFor(source?.WeenieClassId ?? 0);
                if (arena == null || !(target is Player p)) return;

                var fq = p.Fellowship?.QuestManager;
                bool inFellow = p.Fellowship != null;
                bool live = false, expired = false;
                string other = null;
                if (fq != null)
                {
                    foreach (var s in new[] { arena.Stamp1, arena.Stamp2 })
                    {
                        if (!fq.HasQuest(s)) continue;
                        if (fq.CanSolve(s)) expired = true; else live = true;
                    }
                    foreach (var a in GauntletAccessRule.Arenas)
                    {
                        if (a == arena || other != null) continue;
                        if ((fq.HasQuest(a.Stamp1) && !fq.CanSolve(a.Stamp1)) || (fq.HasQuest(a.Stamp2) && !fq.CanSolve(a.Stamp2))) other = a.Society;
                    }
                }
                bool member = p.QuestManager?.HasQuest(arena.MemberFlag) ?? false;
                var reason = GauntletAccessRule.Reason(arena, inFellow, live, expired, other, member);
                if (reason == null) return;
                __result = reason;
                Mod.Log.Info($"[RevivalGuard] GauntletAccess: {p.Name} refused at the {arena.Society} arena: fellowship {inFellow}, stamp live {live} expired {expired}, other {other ?? "none"}, member {member}");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] GauntletAccess: {e.GetType().Name} ({e.Message}); ACE's line kept"); }
        }
    }
}
