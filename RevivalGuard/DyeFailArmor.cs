using System.Reflection;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Failed DYES floor armor at zero.</summary>
    [HarmonyPatch]
    static class DyeFailArmor
    {
        const string P_ON = "rg_dye_fail_floor";
        internal static long Floored;

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(RecipeManager), nameof(RecipeManager.ModifyInt));
            if (m == null) Mod.Log.Error("[RevivalGuard] DyeFailArmor: RecipeManager.ModifyInt not found; failed dyes keep ACE's unfloored armour loss");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard DyeFailArmor: a failed dye's Armor Level loss stops at zero, as retail (false: ACE's unfloored loss)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DyeFailArmor: switch not registered with @modifybool ({e.Message}); still read at its default"); }
            Mod.Log.Info("[RevivalGuard] DyeFailArmor: a failed dye's armour loss stops at zero (rg_dye_fail_floor)");
            return true;
        }

        static bool Prefix(Player player, RecipeModsInt intMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            try
            {
                if (intMod == null || intMod.Stat != (int)PropertyInt.ArmorLevel || intMod.Value >= 0
                    || (ModificationOperation)intMod.Enum != ModificationOperation.Add) return true;
                if (!PropertyManager.GetBool(P_ON, true).Item) return true;
                var wo = RecipeManager.GetTargetMod((ModificationType)intMod.Index, source, target, player, result);
                if (wo == null) return true;
                int? before = wo.GetProperty(PropertyInt.ArmorLevel);
                int? after = DyeFailArmorRule.After(before, intMod.Value);
                if (after != null) player.UpdateProperty(wo, PropertyInt.ArmorLevel, after.Value);
                modified.Add(wo.Guid.Full);
                if (after != (before ?? 0) + intMod.Value) Interlocked.Increment(ref Floored);
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DyeFailArmor: {e.GetType().Name} ({e.Message}); ACE's rule used"); return true; }
        }
    }
}
