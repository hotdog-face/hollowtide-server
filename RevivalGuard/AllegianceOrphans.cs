using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>A deleted patron leaves no orphaned vassals.</summary>
    static class AllegianceOrphans
    {
        const string P_ON = "rg_allegiance_orphans";
        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;

        /// <summary>Every player whose patron is gone, freed.</summary>
        internal static int Sweep(string why)
        {
            var all = PlayerManager.GetAllPlayers();
            var orphans = AllegianceOrphanRule.Orphans(all.Select(p => (p.Guid.Full, p.PatronId)), g => PlayerManager.FindByGuid(g) != null);
            foreach (var g in orphans) Free(PlayerManager.FindByGuid(g), why);
            return orphans.Count;
        }

        /// <summary>The vassals of a character that is already gone from PlayerManager, freed.</summary>
        internal static void FreeVassalsOf(uint deadGuid, string why)
        {
            foreach (var v in PlayerManager.GetAllPlayers().Where(p => p.PatronId == deadGuid).ToList()) Free(v, why);
        }

        /// <summary>ACE's DoHandlePlayerDelete for one vassal</summary>
        static void Free(IPlayer vassal, string why)
        {
            if (vassal == null) return;
            var all = PlayerManager.GetAllPlayers();
            var under = AllegianceOrphanRule.Descendants(vassal.Guid.Full, all.Select(p => (p.Guid.Full, p.PatronId)))
                                            .Select(g => PlayerManager.FindByGuid(g)).Where(p => p != null).ToList();
            uint? oldPatron = vassal.PatronId;
            var oldMonarchs = new HashSet<uint>();
            if (vassal.MonarchId.HasValue) oldMonarchs.Add(vassal.MonarchId.Value);
            foreach (var p in under) if (p.MonarchId.HasValue) oldMonarchs.Add(p.MonarchId.Value);

            foreach (var m in oldMonarchs)
                if (AllegianceManager.Players.TryGetValue(new ACE.Entity.ObjectGuid(m), out var node) && node?.Allegiance != null)
                    AllegianceManager.RemoveCache(node.Allegiance);

            vassal.PatronId = null;
            vassal.UpdateProperty(PropertyInstanceId.Monarch, null, true);
            foreach (var p in under) p.UpdateProperty(PropertyInstanceId.Monarch, vassal.Guid.Full, true);

            var players = new List<IPlayer> { vassal };
            players.AddRange(under);
            foreach (var p in players) AllegianceManager.Rebuild(AllegianceManager.GetAllegiance(p));
            foreach (var p in players) AllegianceManager.LoadPlayer(p);
            foreach (var p in players) AllegianceManager.HandleNoAllegiance(p);
            foreach (var p in players) p.SaveBiotaToDatabase();
            foreach (var p in players) Player.CheckAllegianceHouse(p.Guid);

            Mod.Log.Warn($"[RevivalGuard] AllegianceOrphans: {vassal.Name} (0x{vassal.Guid.Full:X8}) freed from patron 0x{oldPatron ?? 0:X8} ({why}); "
                         + $"{under.Count} under it now take it as monarch: {string.Join(", ", under.Select(p => p.Name))}");
        }
    }

    [HarmonyPatch(typeof(AllegianceManager), "DoHandlePlayerDelete")]
    static class AllegianceOrphansOnDelete
    {
        static bool Prefix(uint playerGuid)
        {
            try
            {
                if (!AllegianceOrphans.On || PlayerManager.FindByGuid(playerGuid) != null) return true;   // stock path works
                AllegianceOrphans.FreeVassalsOf(playerGuid, "their patron was deleted");
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AllegianceOrphans on delete: {e.Message}"); return true; }
        }

        static Exception Finalizer(Exception __exception, uint playerGuid)
        {
            if (__exception == null) return null;
            Mod.Log.Warn($"[RevivalGuard] AllegianceOrphans: delete of 0x{playerGuid:X8} threw {__exception.GetType().Name}: {__exception.Message}; freeing its vassals by guid instead");
            try { AllegianceOrphans.FreeVassalsOf(playerGuid, "their patron was deleted (stock delete threw)"); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AllegianceOrphans after a failed delete: {e.Message}"); }
            return null;
        }
    }

    /// <summary>An allegiance node whose character is gone has no Player</summary>
    [HarmonyPatch(typeof(Allegiance), nameof(Allegiance.BuildOfficers))]
    static class AllegianceOfficersNullSafe
    {
        static bool Prefix(Allegiance __instance)
        {
            try
            {
                if (!AllegianceOrphans.On || __instance.Members == null) return true;
                __instance.Officers = __instance.Members.Where(i => i.Value?.Player?.AllegianceOfficerRank != null)
                                                        .ToDictionary(i => i.Key, i => i.Value);
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AllegianceOfficersNullSafe: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Initialize))]
    static class AllegianceOrphansAtStart
    {
        static void Postfix()
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!AllegianceOrphans.On) return;
                    int n = AllegianceOrphans.Sweep("start-up sweep: their patron no longer exists");
                    if (n > 0) Mod.Log.Warn($"[RevivalGuard] AllegianceOrphans: {n} player(s) with a deleted patron freed at start-up");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AllegianceOrphans start-up sweep: {e.Message}"); }
            }));
        }
    }
}
