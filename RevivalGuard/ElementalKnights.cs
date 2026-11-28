using System.Reflection;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Elemental knight armour. - The colour, ON whenever our tables are served</summary>
    static class ElementalKnights
    {
        internal const string P_ON = "rg_elemental_knights", P_FORCE = "elemental_knight_drop_force";
        static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static bool Looks => PkCommon.On("rg_custom_clothing");
        static long Force => PropertyManager.GetLong(P_FORCE, 0).Item;
        static int s_Registered;

        static void Register()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 1) return;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard ElementalKnights: Empyrean knights and guards drop Elemental Knight armour pieces and tokens, Guardians open the keeper's gate (off)");
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_FORCE)) l[P_FORCE] = new Property<long>(0, "RevivalGuard ElementalKnights: 1-100 forces the piece and token drop percent (tests); 0 uses the table");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ElementalKnights: switches not registered ({e.Message}); read at their defaults (off)"); }
            Mod.Log.Info($"[RevivalGuard] ElementalKnights: drops {(On ? "ON" : "off")} ({P_ON}); {ElementalKnightRule.Knights.Count} knight weenies; colours follow rg_custom_clothing");
        }

        static double R() => ThreadSafeRandom.Next(0.0f, 1.0f);

        // ------------------------------------------------------------------ the colour
        [HarmonyPatch]
        static class Colour
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(Creature), nameof(Creature.CalculateObjDesc));
            static bool Prepare(MethodBase original) { Register(); return original != null || TargetMethod() != null; }

            static void Postfix(Creature __instance, ACE.Entity.ObjDesc __result)
            {
                try
                {
                    if (__result == null || __result.TextureChanges.Count == 0 || !Looks) return;
                    foreach (var w in __instance.EquippedObjects.Values)
                    {
                        if (w.ClothingBase is not uint cb || !ElementalKnightRule.IsTable(cb) || ElementalKnightRule.Repal(w.PaletteTemplate) < 0) continue;
                        var table = ACE.DatLoader.DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.ClothingTable>(cb);
                        if (table == null) continue;
                        var parts = new HashSet<byte>();
                        foreach (var e in table.ClothingBaseEffects.Values)
                            foreach (var o in e.CloObjectEffects) parts.Add((byte)o.Index);
                        foreach (var t in __result.TextureChanges)
                            if (parts.Contains(t.PartIndex)) t.NewTexture = ElementalKnightRule.Shown(t.NewTexture, w.PaletteTemplate);
                    }
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ElementalKnights: colour: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ the drops
        [HarmonyPatch]
        static class Drop
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(Creature), "GenerateTreasure", new[] { typeof(DamageHistoryInfo), typeof(Corpse) });
            static bool Prepare(MethodBase original) { Register(); return original != null || TargetMethod() != null; }

            static void Postfix(Creature __instance, DamageHistoryInfo killer, Corpse corpse)
            {
                try
                {
                    if (corpse == null || __instance == null || __instance is Player || killer == null || !On) return;
                    if (!ElementalKnightRule.Knights.ContainsKey(__instance.WeenieClassId)) return;
                    if (!(killer.TryGetPetOwnerOrAttacker() is Player player) || Mercenaries.IsMerc(player)) return;
                    if (ElementalKnightRule.IsGate(__instance.WeenieClassId))
                        for (int f = 0; f < ElementalKnightRule.FINISHES; f++) player.QuestManager.Stamp(ElementalKnightRule.SlainQuest(f));
                    double tokenChance = Force > 0 ? Math.Min(100, Force) / 100.0 : ElementalKnightRule.TOKEN_CHANCE;
                    if (R() < tokenChance) Put(corpse, ElementalKnightRule.TokenFor(__instance.WeenieClassId), null, __instance, player);
                    double chance = ElementalKnightRule.Chance(true, __instance.WeenieClassId, Force);
                    var hit = ElementalKnightRule.Drop(__instance.WeenieClassId, chance, R(), R());
                    if (hit != null) Put(corpse, hit.Value.wcid, hit.Value.template, __instance, player);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ElementalKnights: drop: {e.Message}"); }
            }

            static void Put(Corpse corpse, uint wcid, int? template, Creature c, Player player)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) { Mod.Log.Warn($"[RevivalGuard] ElementalKnights: weenie {wcid} missing (elemental-knight datafix not applied?)"); return; }
                if (template.HasValue) wo.PaletteTemplate = template.Value;
                if (!corpse.TryAddToInventory(wo)) { wo.Destroy(); return; }
                Mod.Log.Info($"[RevivalGuard] ElementalKnights: {wo.Name} ({wcid}, template {template?.ToString() ?? "-"}) in the corpse of {c.Name} ({c.WeenieClassId}), killed by {player.Name}");
            }
        }
    }
}
