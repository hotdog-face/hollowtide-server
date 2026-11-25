using System.Runtime.CompilerServices;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// SUMMONING CURVE (owner 2026-11-24 "Smooth the curve"; docs/proposals/summoning-curve.md; rule in PetCurveRule.cs).
    /// Postfix on ACE Creature.GetBaseDamage(body part), the per-strike damage range every monster melee hit starts from:
    /// for a combat pet, with rg_pet_curve on, its DamageMod is multiplied by PetCurveRule.Multiplier, worked out once
    /// per pet from its own weenie values (level, base Strength and Quickness, PowerupTime, motion and combat tables,
    /// body parts). Ratings, crits, armor and resistances still apply after it, as before. Default OFF: nothing changes
    /// until `@modifybool rg_pet_curve true` or the datafix 2026-11-25-summon-3-curve-on.sql.
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.GetBaseDamage), new[] { typeof(PropertiesBodyPart) })]
    static class PetCurve
    {
        sealed class Mult { public double M; }
        static readonly ConditionalWeakTable<CombatPet, Mult> s_Mult = new();

        static bool Prepare()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(PetCurveRule.P_ON))
                    b[PetCurveRule.P_ON] = new Property<bool>(false, "RevivalGuard PetCurve: combat pet damage on the smoothed curve (docs/proposals/summoning-curve.md)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PetCurve: switch not registered with @modifybool ({e.Message}); still read at its default"); }
            Mod.Log.Info($"[RevivalGuard] PetCurve: ready ({PetCurveRule.P_ON}, default off)");
            return true;
        }

        static void Postfix(Creature __instance, BaseDamageMod __result)
        {
            try
            {
                if (__result == null || !(__instance is CombatPet pet)) return;
                if (!PropertyManager.GetBool(PetCurveRule.P_ON, false).Item) return;
                var m = s_Mult.GetValue(pet, Compute).M;
                if (m != 1.0) __result.DamageMod *= (float)m;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PetCurve: {e.GetType().Name}: {e.Message}; ACE's damage used"); }
        }

        static Mult Compute(CombatPet pet)
        {
            var parts = pet.Biota.PropertiesBodyPart?.Select(kv => ((int)kv.Key, kv.Value.DVal, (double)kv.Value.DVar)).ToList()
                ?? new List<(int, int, double)>();
            int level = pet.Level ?? 0;
            double m = PetCurveRule.Multiplier(level, PetCurveRule.HitMean(parts), (int)pet.Strength.Base, (int)pet.Quickness.Base,
                pet.PowerupTime, pet.MotionTableId, pet.CombatTableDID ?? 0);
            Mod.Log.Info($"[RevivalGuard] PetCurve: {pet.Name} [{pet.WeenieClassId}] L{level} motion 0x{pet.MotionTableId:X8} hit x{m:0.000}");
            return new Mult { M = m };
        }
    }
}
