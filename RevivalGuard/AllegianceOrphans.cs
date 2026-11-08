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
    /// <summary>
    /// A DELETED PATRON LEAVES NO ORPHANED VASSALS (ticket 261107-0006, Ah-Mule-Ee: "trying to chain a trade mule ...
    /// getting an error that I've already sworn allegiance").
    ///
    /// ACE frees a deleted character's vassals in AllegianceManager.DoHandlePlayerDelete ("vassals now become monarchs"),
    /// but it looks the character up in PlayerManager first, and this shard's char_delete_instant_teardown drops the
    /// character from PlayerManager straight after queueing that work, so it prints "couldn't find player guid" and does
    /// nothing (the same gap HouseRelease closes for houses). The vassals keep a patron that no longer exists, the
    /// allegiance tree breaks at that link, and ACE's loop check (IsPledgable: "2 players can't swear to each other")
    /// no longer sees the chain.
    ///
    /// The live case: on 2026-10-31 Fletch the Alchemule swore to Ah-Mule-Ee (0x50000456), sworn to Yasir, sworn to the
    /// monarch Tomoki. That Ah-Mule-Ee was deleted about 2026-11-03 and Fletch kept it as patron. A new Ah-Mule-Ee
    /// (0x5000049F) swore to Fletch on 11-03, and on 11-06 Tomoki, the monarch, swore to Fletch too: a loop through the
    /// deleted character. On 11-07 the new Ah-Mule-Ee tried to swear to Fletch again; ACE's "You've already sworn
    /// allegiance." was true (its patron already was Fletch), but the broken tree could not show it.
    ///
    /// Two patches: DoHandlePlayerDelete, when the character is already gone, frees its vassals by the guid (every player
    /// whose PatronId is it); and at start-up every player whose patron does not exist is freed the same way, which
    /// repairs the live allegiance above (Fletch becomes monarch of Tomoki, Yasir and the new Ah-Mule-Ee, as the patron
    /// links say) and any other orphan. Each free mirrors ACE's own steps. Rule: AllegianceOrphanRule (nettest).
    /// Switch: rg_allegiance_orphans.
    /// </summary>
    static class AllegianceOrphans
    {
        const string P_ON = "rg_allegiance_orphans";
        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;

        /// <summary>Every player whose patron is gone, freed. World thread.</summary>
        internal static int Sweep(string why)
        {
            var all = PlayerManager.GetAllPlayers();
            var orphans = AllegianceOrphanRule.Orphans(all.Select(p => (p.Guid.Full, p.PatronId)), g => PlayerManager.FindByGuid(g) != null);
            foreach (var g in orphans) Free(PlayerManager.FindByGuid(g), why);
            return orphans.Count;
        }

        /// <summary>The vassals of a character that is already gone from PlayerManager, freed. World thread.</summary>
        internal static void FreeVassalsOf(uint deadGuid, string why)
        {
            foreach (var v in PlayerManager.GetAllPlayers().Where(p => p.PatronId == deadGuid).ToList()) Free(v, why);
        }

        /// <summary>ACE's DoHandlePlayerDelete for one vassal: no patron, no monarch; everyone under it takes it as monarch;
        /// the old allegiance's cache goes and both are rebuilt, loaded, checked for no allegiance, saved.</summary>
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
