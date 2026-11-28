using System;
using System.Collections.Generic;
using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The blue icon rim on magic items.</summary>
    [HarmonyPatch]
    static class MagicRim
    {
        const ItemType WEARABLE = ItemType.Armor | ItemType.Clothing | ItemType.Jewelry | ItemType.MeleeWeapon;
        const ItemType RANGED_OR_CASTER = ItemType.MissileWeapon | ItemType.Caster;
        static int s_Hooked;
        internal static long Stamped;

        static IEnumerable<MethodBase> TargetMethods()
        {
            // Both overloads are declared on WorldObjectFactory itself (AGENTS.md: patch the declaring type).
            var t = typeof(WorldObjectFactory);
            var fromWeenie = AccessTools.Method(t, nameof(WorldObjectFactory.CreateWorldObject), new[] { typeof(ACE.Entity.Models.Weenie), typeof(ObjectGuid) });
            var fromBiota = AccessTools.Method(t, nameof(WorldObjectFactory.CreateWorldObject), new[] { typeof(ACE.Entity.Models.Biota) });
            foreach (var (m, nm) in new[] { (fromWeenie, "CreateWorldObject(Weenie, ObjectGuid)"), (fromBiota, "CreateWorldObject(Biota)") })
            {
                if (m == null) { Mod.Log.Error($"[RevivalGuard] MagicRim: WorldObjectFactory.{nm} not found; items from that path keep the plain rim"); continue; }
                s_Hooked++;
                yield return m;
            }
        }

        public static void Register()
        {
            Mod.Log.Info($"[RevivalGuard] MagicRim: {s_Hooked}/2 creation paths hooked");
        }

        static void Postfix(WorldObject __result)
        {
            try
            {
                if (!Qualifies(__result)) return;
                __result.UiEffects = UiEffects.Magical;
                Stamped++;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MagicRim: {e.GetType().Name} stamping {__result?.Name}: {e.Message}"); }
        }

        internal static bool Qualifies(WorldObject wo)
        {
            if (wo == null || wo is Creature) return false;
            if (wo.UiEffects != null) return false;
            var spells = wo.Biota?.PropertiesSpellBook;
            if (spells == null || spells.Count == 0) return false;
            if ((wo.ItemMaxMana ?? 0) <= 0) return false;
            var type = wo.ItemType;
            if ((type & WEARABLE) != 0) return true;
            if ((type & RANGED_OR_CASTER) != 0) return wo.W_DamageType == DamageType.Undef;
            return false;
        }
    }
}
