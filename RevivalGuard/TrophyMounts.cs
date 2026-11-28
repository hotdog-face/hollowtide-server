using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Trophy mounts: a boss killed by a player leaves a mounted trophy in its corpse</summary>
    [HarmonyPatch]
    static class TrophyMounts
    {
        internal const uint TUSKER = 900060, LUGIAN = 900061, QUEEN = 900062, SWORD = 900063, BAEL = 900205;

        /// <summary>Boss weenie -> trophy weenie.</summary>
        static readonly Dictionary<uint, uint> s_Boss = new Dictionary<uint, uint>
        {
            [1629] = TUSKER, [21525] = TUSKER, [22592] = TUSKER, [22593] = TUSKER,
            [11996] = LUGIAN, [12249] = LUGIAN,
            [36794] = QUEEN, [11048] = QUEEN, [11049] = QUEEN, [11483] = QUEEN, [43530] = QUEEN,
            [1621] = QUEEN, [6639] = QUEEN, [72427] = QUEEN, [72428] = QUEEN,
            [2491] = SWORD,
            // Bael'Zharon, world boss 900200 (WorldBoss.cs).
            [900200] = BAEL,
        };

        /// <summary>How often a trophy drops, by trophy; not listed = every kill.</summary>
        static readonly Dictionary<uint, double> s_Chance = new Dictionary<uint, double>
        {
            [TUSKER] = 0.02, [LUGIAN] = 0.02,
        };

        static bool s_Hooked;
        static int s_Given;

        public static void Register() =>
            Mod.Log.Info($"[RevivalGuard] TrophyMounts: {(s_Hooked ? $"on, {s_Boss.Count} bosses" : "OFF (Creature.GenerateTreasure not found)")}");

        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            if (m == null) Mod.Log.Error("[RevivalGuard] TrophyMounts: Creature.GenerateTreasure(DamageHistoryInfo, Corpse) not found; no trophies");
            s_Hooked = m != null;
            return m;
        }

        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static void Postfix(Creature __instance, DamageHistoryInfo killer, Corpse corpse, List<WorldObject> __result)
        {
            try
            {
                if (__instance == null || __instance is Player || killer == null) return;
                if (!s_Boss.TryGetValue(__instance.WeenieClassId, out var wcid)) return;
                if (!(killer.TryGetPetOwnerOrAttacker() is Player player)) return;   // a player's kill only

                // A trophy the creature's own create list already dropped is inscribed, not doubled.
                var trophy = corpse?.Inventory.Values.FirstOrDefault(i => i.WeenieClassId == wcid)
                             ?? __result?.FirstOrDefault(i => i.WeenieClassId == wcid);
                bool listed = trophy != null;
                if (!listed && s_Chance.TryGetValue(wcid, out var chance) && ThreadSafeRandom.Next(0.0f, 1.0f) >= chance) return;
                if (trophy == null) trophy = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (trophy == null) { Mod.Log.Warn($"[RevivalGuard] TrophyMounts: weenie {wcid} missing; {__instance.Name} left no trophy"); return; }
                trophy.SetProperty(PropertyString.LongDesc, Inscription(__instance.Name, player.Name, DateTime.UtcNow));

                if (!listed)
                {
                    if (corpse != null) { if (!corpse.TryAddToInventory(trophy)) { trophy.Destroy(); return; } }
                    else __result?.Add(trophy);
                }
                s_Given++;
                Mod.Log.Info($"[RevivalGuard] TrophyMounts: {__instance.Name} (wcid {__instance.WeenieClassId}) slain by {player.Name}; {trophy.Name} 0x{trophy.Guid.Full:X8} in {(corpse != null ? "the corpse" : "the loot")} ({s_Given} this run)");
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] TrophyMounts: {e.GetType().Name} on the death of {__instance?.Name}: {e.Message}");
            }
        }

        /// <summary>The appraisal line.</summary>
        internal static string Inscription(string boss, string killer, DateTime when) =>
            $"Slain by {killer}.\n\nThe {boss} fell on {when:MMMM d, yyyy}. Hang it on a wall hook.";
    }
}
