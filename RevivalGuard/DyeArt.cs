using System.Collections.Concurrent;
using System.Reflection;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// DYES BY ART (owner 2026-10-19 "Yes lets do the dye improvements"; docs/findings/FINDINGS-dye-by-art.md). Ours.
    /// ACE dyes any Clothing flagged Dyable (RecipeManager_New.GetNewRecipe). A dye sets PaletteTemplate; when the item's
    /// ClothingTable (ClothingBase) has no ByShade entry for it, Creature_Networking falls back to the table's first entry
    /// and the item looks wrong. Here the 9 classic pots and the 10 rare eternals take on a Clothing item iff its table has
    /// the dye's template, whatever Dyable says: ~590 more items per dye, and Dyable items without the art are refused.
    /// Kept as ACE has them: cookbook pairs to other recipes (stamps, flags, wedding raiment). Refused: society armour
    /// (faction colours stay) and items only the Pigmentation Apparatus serves. The Apparatus itself is untouched.
    ///   @modifybool dye_by_art false   ACE's original rule
    /// The client's hover cursor follows the same rule from StreamingAssets/dye-art.json (tools/crafting/build_dye_art.py).
    /// </summary>
    static class DyeArt
    {
        internal const string P_ON = "dye_by_art";
        const uint RECIPE_CLASSIC = 3844, RECIPE_ETERNAL = 9068, APPARATUS = 31987;

        static readonly HashSet<uint> CLASSIC = new() { 8043, 8044, 8045, 8650, 8651, 8652, 11475, 11476, 11477 };
        static bool Eternal(uint wcid) => wcid >= 30082 && wcid <= 30091;
        static uint Kind(uint wcid) => CLASSIC.Contains(wcid) ? RECIPE_CLASSIC : Eternal(wcid) ? RECIPE_ETERNAL : 0;

        // ClothingTable id -> its ByShade templates. Built on first use: the DATs load after mods start.
        static readonly ConcurrentDictionary<uint, HashSet<uint>> s_Templates = new();
        static int s_Scanned;
        internal static long Allowed, Refused;

        internal static void RegisterSwitch()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard DyeArt: dyes take on clothing whose art has the colour (false: ACE's Dyable rule)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DyeArt: switch not registered with @modifybool ({e.Message}); still read at its default"); }
        }

        static bool On() => PropertyManager.GetBool(P_ON, true).Item;

        static void Scan()
        {
            if (Interlocked.Exchange(ref s_Scanned, 1) != 0) return;
            try
            {
                int tables = 0, dyeable = 0;
                foreach (var id in DatManager.PortalDat.AllFiles.Keys.Where(k => (k >> 24) == 0x10).ToList())
                {
                    var t = DatManager.PortalDat.ReadFromDat<ClothingTable>(id);
                    if (t == null) continue;
                    var set = new HashSet<uint>(t.ClothingSubPalEffects.Keys);
                    s_Templates[id] = set;
                    tables++;
                    if (set.Any(p => p >= 84 && p <= 93)) dyeable++;
                }
                Mod.Log.Info($"[RevivalGuard] DyeArt: {tables} clothing tables read, {dyeable} carry a dye colour");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DyeArt: clothing table scan failed ({e.Message}); tables read one at a time"); }
        }

        static bool HasTemplate(uint clothingBase, uint template)
        {
            Scan();
            if (!s_Templates.TryGetValue(clothingBase, out var set))
            {
                var t = DatManager.PortalDat.ReadFromDat<ClothingTable>(clothingBase);
                set = t == null ? new HashSet<uint>() : new HashSet<uint>(t.ClothingSubPalEffects.Keys);
                s_Templates[clothingBase] = set;
            }
            return set.Contains(template);
        }

        /// <summary>0 when this is not ours to decide (not a dye, a special cookbook pair, or the switch is off);
        /// otherwise the recipe to use, or uint.MaxValue to refuse.</summary>
        static uint Decide(WorldObject source, WorldObject target, out CookBook cook)
        {
            cook = null;
            if (source == null || target == null) return 0;
            uint kind = Kind(source.WeenieClassId);
            if (kind == 0 || !On()) return 0;
            cook = DatabaseManager.World.GetCachedCookbook(source.WeenieClassId, target.WeenieClassId);
            if (cook != null && cook.RecipeId != RECIPE_CLASSIC && cook.RecipeId != RECIPE_ETERNAL) return 0;
            if (target.WeenieType != WeenieType.Clothing || target.IsSocietyArmor) return uint.MaxValue;
            if (cook == null && DatabaseManager.World.GetCachedCookbook(APPARATUS, target.WeenieClassId) != null) return uint.MaxValue;
            var cb = target.ClothingBase;
            var tpl = source.PaletteTemplate;
            if (cb == null || tpl == null || !HasTemplate(cb.Value, (uint)tpl.Value)) return uint.MaxValue;
            return kind;
        }

        static void Stage(string what, Exception e) => Mod.Log.Warn($"[RevivalGuard] DyeArt: {what} failed ({e.Message}); ACE's rule used");

        /// <summary>The recipe itself: every path that looks up a recipe (first use and the confirmed one).</summary>
        [HarmonyPatch]
        static class RecipeGate
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(RecipeManager), nameof(RecipeManager.GetRecipe),
                    new[] { typeof(Player), typeof(WorldObject), typeof(WorldObject) });
                if (m == null) Mod.Log.Error("[RevivalGuard] DyeArt: RecipeManager.GetRecipe not found; dyes keep ACE's rule");
                return m;
            }

            static bool Prepare(MethodBase original)
            {
                if (original != null) return true;
                if (TargetMethod() == null) return false;
                RegisterSwitch();
                Mod.Log.Info("[RevivalGuard] DyeArt: dyes take on clothing whose art has the colour (dye_by_art)");
                return true;
            }

            static bool Prefix(WorldObject source, WorldObject target, ref Recipe __result)
            {
                try
                {
                    uint d = Decide(source, target, out var cook);
                    if (d == 0) return true;
                    if (d == uint.MaxValue) { __result = null; return false; }
                    __result = cook?.Recipe ?? DatabaseManager.World.GetCachedRecipe(d);
                    return __result == null;   // missing recipe row: let ACE try
                }
                catch (Exception e) { Stage("recipe check", e); return true; }
            }
        }

        /// <summary>The refusal line, in place of ACE's "cannot be used on". ACE's own earlier checks run first.</summary>
        [HarmonyPatch]
        static class RefusalLine
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.DeclaredMethod(typeof(RecipeManager), nameof(RecipeManager.UseObjectOnTarget),
                    new[] { typeof(Player), typeof(WorldObject), typeof(WorldObject), typeof(bool) });
                if (m == null) Mod.Log.Error("[RevivalGuard] DyeArt: RecipeManager.UseObjectOnTarget not found; ACE's refusal line kept");
                return m;
            }

            static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

            static bool Prefix(Player player, WorldObject source, WorldObject target)
            {
                try
                {
                    if (player?.Session == null || player.IsBusy || source == target) return true;
                    if (!PropertyManager.GetBool("allow_combat_mode_crafting").Item && player.CombatMode != CombatMode.NonCombat) return true;
                    uint d = Decide(source, target, out _);
                    if (d == 0) return true;
                    if (d != uint.MaxValue) { Interlocked.Increment(ref Allowed); return true; }
                    Interlocked.Increment(ref Refused);
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {target.NameWithMaterial} can't take this colour.", ChatMessageType.Craft));
                    player.SendUseDoneEvent();
                    return false;
                }
                catch (Exception e) { Stage("refusal", e); return true; }
            }
        }
    }
}
