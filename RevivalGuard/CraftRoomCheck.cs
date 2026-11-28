using System;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    [HarmonyPatch(typeof(RecipeManager), nameof(RecipeManager.HandleRecipe))]
    public static class CraftRoomCheck
    {
        internal const string NoRoom = "You do not have enough pack space to craft that!";
        internal const string TooHeavy = "You are too encumbered to craft that!";

        static bool Prefix(Player player, WorldObject source, WorldObject target, Recipe recipe)
        {
            try
            {
                if (player == null || source == null || target == null || recipe == null) return true;
                if (recipe.IsTinkering()) return true;
                string why = Check(player, source, target, recipe, true) ?? Check(player, source, target, recipe, false);
                if (why == null) return true;
                player.SendTransientError(why);
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] CraftRoomCheck: {e.Message}");
                return true;
            }
        }

        /// <summary>Why this outcome could not be placed, or null when it can (or creates nothing).</summary>
        static string Check(Player p, WorldObject source, WorldObject target, Recipe r, bool success)
        {
            uint wcid = success ? r.SuccessWCID : r.FailWCID;
            if (wcid == 0) return null;
            ACE.Entity.Models.Weenie w = DatabaseManager.World.GetCachedWeenie(wcid);
            if (w == null) return null;                     // ACE reports a missing weenie itself
            uint amount = Math.Max(1u, success ? r.SuccessAmount : r.FailAmount);
            bool containerSlot = w.RequiresBackpackSlotOrIsContainer();

            int freedSlots = 0, freedBurden = 0;
            Freed(p, target, success ? r.SuccessDestroyTargetChance : r.FailDestroyTargetChance,
                  success ? r.SuccessDestroyTargetAmount : r.FailDestroyTargetAmount, containerSlot, ref freedSlots, ref freedBurden);
            Freed(p, source, success ? r.SuccessDestroySourceChance : r.FailDestroySourceChance,
                  success ? r.SuccessDestroySourceAmount : r.FailDestroySourceAmount, containerSlot, ref freedSlots, ref freedBurden);

            // CreateItem makes one object whatever the amount (a stackable gets that stack size), so one slot is always enough.
            int free = containerSlot ? p.GetFreeContainerSlots() : p.GetFreeInventorySlots();
            if (free + freedSlots < 1) return NoRoom;

            int need = w.IsStackable() ? w.GetStackUnitEncumbrance() * (int)amount : (w.GetProperty(PropertyInt.EncumbranceVal) ?? 0);
            if (need - freedBurden > p.GetAvailableBurden()) return TooHeavy;
            return null;
        }

        /// <summary>Room a certain destruction gives back before the create.</summary>
        static void Freed(Player p, WorldObject item, double chance, uint amount, bool containerSlot, ref int slots, ref int burden)
        {
            if (chance < 1.0 || item == null) return;
            if (p.GetInventoryItem(item.Guid) == null) return;
            int stack = Math.Max(1, item.StackSize ?? 1);
            int used = (int)Math.Min((long)amount, stack);
            if (amount >= stack && item.UseBackpackSlot == containerSlot) slots++;
            burden += (item.EncumbranceVal ?? 0) * used / stack;
        }
    }
}
