using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Bind = RevivalGuard.TinkerLockRule.Bind;

namespace RevivalGuard
{
    /// <summary>
    /// A RECIPE THAT LOCKS THE ITEM TO WHOEVER APPLIES IT ASKS FIRST (owner 2026-11-20, "yes do the fix so its more clear
    /// to players how it works as well").
    ///
    /// Retail recipe data (Lucky White Rabbit's Foot 8751, the slayer skulls and stones, Luminous Amber, ivory, bow
    /// handles, the Sword of Lost Light infusions...) carries a recipe_mods_i_i_d row that copies the player into the
    /// item's AllowedWielder, and ACE's Player_Inventory then refuses every other character ("You do not own that item!").
    /// None of these recipes has a skill, so ACE shows no dialog: on live two players applied a rabbit's foot and a skull
    /// with a level 10 alt and found their level 220 weapons wieldable only by the alt.
    ///
    /// Now, before such a recipe runs, the player is asked with ACE's Yes/No dialog (TinkerLockRule.Ask); only Yes runs
    /// it, through ACE's own path again (clap, and the skill-chance dialog where the recipe has one). An item already
    /// locked to another character is refused outright, so it is never locked to the wrong character a second time. An
    /// item already locked to the applier is not asked about: nothing about who can wield it changes.
    ///
    /// The recipes are read from the world database at start (every AllowedWielder row TinkerLockRule.Row counts), not a
    /// list of ids; a recipe missing from that read (a load failure) is judged from its own cached mods.
    /// </summary>
    /// <para>PATCHED ON <c>RecipeManager</c>, which declares <c>UseObjectOnTarget</c> (one overload). Every use-with of a
    /// cookbook recipe comes through it (WorldObject_Use.HandleActionUseOnTarget), and ACE's own craft confirmation calls it
    /// again with confirmed = true, which this leaves alone.</para>
    [HarmonyPatch(typeof(RecipeManager), nameof(RecipeManager.UseObjectOnTarget))]
    public static class TinkerLock
    {
        const double APPROVAL_TTL = 60.0;    // a Yes is good for the one recipe it answered, for this long

        static Dictionary<uint, Bind> s_Binding;     // recipe id -> what it locks, from the world DB at start

        sealed class Approval { public uint Source; public uint Target; public DateTime At; }
        static readonly ConditionalWeakTable<Player, Approval> s_Approved = new ConditionalWeakTable<Player, Approval>();

        public static void Register()
        {
            using var ctx = new WorldDbContext();
            var rows = ctx.RecipeModsIID.Where(i => i.Stat == TinkerLockRule.ALLOWED_WIELDER)
                .Select(i => new { i.RecipeMod.RecipeId, i.Enum, i.Source }).ToList();
            var map = new Dictionary<uint, Bind>();
            foreach (var r in rows)
            {
                var b = TinkerLockRule.Row(TinkerLockRule.ALLOWED_WIELDER, r.Enum, r.Source);
                if (b != Bind.None) map[r.RecipeId] = b;
            }
            s_Binding = map;
            Mod.Log.Info($"[RevivalGuard] TinkerLock: {map.Count} recipes lock the item to whoever applies them "
                + $"({map.Count(kv => kv.Value == Bind.Target)} the item itself, {map.Count(kv => kv.Value == Bind.Result)} the item they make); each asks first");
        }

        internal static Bind Binds(Recipe recipe)
        {
            var map = s_Binding;
            if (map != null) return map.TryGetValue(recipe.Id, out var b) ? b : Bind.None;
            foreach (var mod in recipe.RecipeMod)
                foreach (var i in mod.RecipeModsIID)
                {
                    var b = TinkerLockRule.Row(i.Stat, i.Enum, i.Source);
                    if (b != Bind.None) return b;
                }
            return Bind.None;
        }

        static bool Prefix(Player player, WorldObject source, WorldObject target, bool confirmed)
        {
            try
            {
                if (confirmed || player?.Session == null || source == null || target == null || source == target) return true;
                if (player.IsBusy) return true;                               // ACE's "too busy" stands
                var book = DatabaseManager.World.GetCachedCookbook(source.WeenieClassId, target.WeenieClassId);
                var recipe = book?.Recipe;
                if (recipe == null) return true;                              // no binding recipe is a code-built one
                var bind = Binds(recipe);
                if (bind == Bind.None) return true;

                uint me = player.Guid.Full;
                uint owner = target.GetProperty(PropertyInstanceId.AllowedWielder) ?? 0;
                if (owner != 0 && owner != me)
                {
                    string name = TinkerLockRule.OwnerName(PlayerManager.FindByGuid(new ObjectGuid(owner))?.Name,
                                                           target.GetProperty(PropertyString.CraftsmanName));
                    Say(player, TinkerLockRule.Taken(target.NameWithMaterial, name, source.Name));
                    player.SendUseDoneEvent();
                    return false;
                }
                if (owner == me) return true;                                  // already this character's: nothing new
                // ACE's own requirement check first (it says why), so nobody is asked about a recipe that cannot run
                if (!RecipeManager.VerifyRequirements(recipe, player, source, target))
                {
                    player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                    return false;
                }
                if (TakeApproval(player, source, target)) return true;

                string locked = target.NameWithMaterial;
                if (bind == Bind.Result && recipe.SuccessWCID != 0)
                    locked = DatabaseManager.World.GetCachedWeenie(recipe.SuccessWCID)?.GetProperty(PropertyString.Name) ?? locked;
                string ask = TinkerLockRule.Ask(locked, player.Name, source.Name);
                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_TinkerLock(player.Guid, source.Guid, target.Guid), ask))
                {
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                    return false;
                }
                Mod.Log.Info($"[RevivalGuard] TinkerLock: asked {player.Name} before recipe {recipe.Id} ({source.Name} on {target.NameWithMaterial} 0x{target.Guid.Full:X8})");
                player.SendUseDoneEvent();
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] TinkerLock: {e.Message}");      // never let the question break crafting
                return true;
            }
        }

        internal static void Say(Player p, string text)
        {
            // chat only: our client echoes a transient string into chat as well, so both read twice (box run 2026-11-20)
            p.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Craft));
        }

        internal static void Approve(Player p, WorldObject source, WorldObject target) =>
            s_Approved.AddOrUpdate(p, new Approval { Source = source.Guid.Full, Target = target.Guid.Full, At = DateTime.UtcNow });

        static bool TakeApproval(Player p, WorldObject source, WorldObject target)
        {
            if (!s_Approved.TryGetValue(p, out var a)) return false;
            s_Approved.Remove(p);
            return a.Source == source.Guid.Full && a.Target == target.Guid.Full && (DateTime.UtcNow - a.At).TotalSeconds < APPROVAL_TTL;
        }
    }

    /// <summary>The Yes/No for a binding recipe. Yes runs ACE's UseObjectOnTarget again with the approval in hand; No or a
    /// timeout says nothing was used.</summary>
    public class Confirmation_TinkerLock : Confirmation
    {
        readonly ObjectGuid m_Source, m_Target;

        public Confirmation_TinkerLock(ObjectGuid player, ObjectGuid source, ObjectGuid target) : base(player, ConfirmationType.Yes_No)
        {
            m_Source = source;
            m_Target = target;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var p = Player;
            if (p?.Session == null) return;
            var source = p.FindObject(m_Source.Full, Player.SearchLocations.LocationsICanMove);
            var target = p.FindObject(m_Target.Full, Player.SearchLocations.LocationsICanMove);
            if (!response)
            {
                if (source != null && target != null) TinkerLock.Say(p, TinkerLockRule.Declined(target.NameWithMaterial, source.Name));
                return;
            }
            if (source == null || target == null) return;
            TinkerLock.Approve(p, source, target);
            RecipeManager.UseObjectOnTarget(p, source, target);
        }
    }
}
