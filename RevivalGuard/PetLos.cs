using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PET LINE OF SIGHT (owner 2026-10-19 "Skip unseen", docs/findings/FINDINGS-pet-los.md). NOT RETAIL-PROVEN.
    /// ACE CombatPet.FindNextTarget takes the nearest monster in ObjMaint's visible targets with no sight test,
    /// and ACE has no pathfinding, so a pet locked onto a monster behind a wall grinds against it until it expires.
    /// This prefix runs the same selection (GetNearbyMonsters, nearest first, inside VisualAwarenessRangeSq) but
    /// takes the nearest one that passes ACE's own WorldObject.IsDirectVisible (the test monsters use for
    /// projectile spells). At most MAX_TESTS sight tests per call; a pet with nothing in sight scans again
    /// after RETRY_S, not every tick. Any error falls back to ACE's own method.
    ///   @modifybool pet_los_targets false   ACE's original behaviour
    ///   @modifybool pet_los_log true        a "[RevivalGuard] PetLos:" line per pick and per scan with none in sight
    /// </summary>
    [HarmonyPatch]
    static class PetLos
    {
        internal const string P_ON = "pet_los_targets", P_LOG = "pet_los_log";
        const int MAX_TESTS = 6;
        const double RETRY_S = 1.0;

        sealed class Next { public double At; }
        static readonly ConditionalWeakTable<CombatPet, Next> s_Next = new();
        static long s_Picks, s_Skipped, s_None;
        internal static string Counts => $"picks {s_Picks}, walled targets skipped {s_Skipped}, scans with none in sight {s_None}";

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(CombatPet), nameof(CombatPet.FindNextTarget), Type.EmptyTypes);
            if (m == null) Mod.Log.Error("[RevivalGuard] PetLos: CombatPet.FindNextTarget() not found; pets keep ACE's targeting");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard PetLos: combat pets only pick targets in line of sight (IsDirectVisible)");
                if (!b.ContainsKey(P_LOG)) b[P_LOG] = new Property<bool>(false, "RevivalGuard PetLos: log each pet target pick and each scan with nothing in sight");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PetLos: switches not registered with @modifybool ({e.Message}); still read at defaults"); }
            Mod.Log.Info("[RevivalGuard] PetLos: combat pets skip targets out of line of sight (pet_los_targets, pet_los_log)");
            return true;
        }

        static string M(CombatPet pet, Creature t) =>
            pet.Location != null && t.Location != null ? $"{pet.Location.DistanceTo(t.Location):F1} m" : "? m";

        static bool Prefix(CombatPet __instance, ref bool __result)
        {
            try
            {
                if (!PropertyManager.GetBool(P_ON, true).Item) return true;
                var pet = __instance;
                var next = s_Next.GetOrCreateValue(pet);
                double now = Timers.RunningTime;
                if (now < next.At) { __result = false; return false; }

                var monsters = pet.GetNearbyMonsters();
                if (monsters.Count == 0) { __result = false; return false; }

                var nearest = pet.BuildTargetDistance(monsters, true);
                float rangeSq = pet.VisualAwarenessRangeSq;
                int tests = 0;
                List<string> skipped = null;
                foreach (var td in nearest)
                {
                    if (td.Distance > rangeSq || tests >= MAX_TESTS) break;
                    tests++;
                    if (pet.IsDirectVisible(td.Target))
                    {
                        pet.AttackTarget = td.Target;
                        Interlocked.Increment(ref s_Picks);
                        if (PropertyManager.GetBool(P_LOG, false).Item)
                            Mod.Log.Info($"[RevivalGuard] PetLos: {pet.Name} 0x{pet.Guid.Full:X8} picked {td.Target.Name} 0x{td.Target.Guid.Full:X8} "
                                + $"at {M(pet, td.Target)} in sight (of {monsters.Count} in its visible list); skipped not in sight: {(skipped == null ? "none" : string.Join(", ", skipped))}");
                        __result = true;
                        return false;
                    }
                    Interlocked.Increment(ref s_Skipped);
                    (skipped ??= new List<string>()).Add($"{td.Target.Name} 0x{td.Target.Guid.Full:X8} {M(pet, td.Target)}");
                }

                if (tests > 0)
                {
                    Interlocked.Increment(ref s_None);
                    next.At = now + RETRY_S;
                    if (PropertyManager.GetBool(P_LOG, false).Item)
                        Mod.Log.Info($"[RevivalGuard] PetLos: {pet.Name} 0x{pet.Guid.Full:X8} no target in sight, skipped: {string.Join(", ", skipped)}");
                }
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] PetLos: {e.GetType().Name}: {e.Message}; ACE's targeting used");
                return true;
            }
        }
    }
}
