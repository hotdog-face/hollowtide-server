using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE BORG'S STARTING LOOK (owner-approved 2026-11-08, docs/proposals/the-borg.md "The retail look"; side-by-sides in
    /// docs/proposals/borg-retail/). A Borg ("... of Borg" on an allowlisted account, BorgSwitch.IsBorg) starts in the
    /// black-top squad's clothes and earns everything else ("EARN IT"): Puffy Shirt (DyeSpringBlack, shade 0), Pants in
    /// DyeDarkGreen (a woman: Flared Pants and black Shoes, so boots don't hide the flare; a man: Leather Boots), and an
    /// Acid Dagger. Every piece is bonded, attuned and worth nothing: a look, not loot, kept through a Darktide death.
    /// Given at login and after a lifestone return: plain clothing in the way goes to the pack; armour the Borg earned is
    /// never replaced (a slot it covers stays as it is).
    /// MercKit skips Borg characters.
    /// </summary>
    public static class BorgKit
    {
        const uint PUFFY_SHIRT = 2591, PANTS = 127, PANTS_ALT = 907706, FLARED_PANTS = 2597, LEATHER_BOOTS = 115, SHOES = 132,
                   ACID_DAGGER = 3778;
        const int DYE_SPRING_BLACK = 93, DYE_DARK_GREEN = 84, BLACK = 39;

        static WorldObject Piece(uint wcid, int? palette, double? shade)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) return null;
            if (palette.HasValue) wo.SetProperty(PropertyInt.PaletteTemplate, palette.Value);
            if (shade.HasValue) wo.SetProperty(PropertyFloat.Shade, shade.Value);
            wo.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            wo.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            wo.SetProperty(PropertyInt.Value, 0);
            return wo;
        }

        static bool Armoured(Player p, EquipMask slot) =>
            p.EquippedObjects.Values.Any(o => ((o.CurrentWieldedLocation ?? 0) & slot) != 0 && o.ItemType == ItemType.Armor);
        static bool Worn(Player p, params uint[] wcids) => p.EquippedObjects.Values.Any(o => wcids.Contains(o.WeenieClassId));

        static bool Covered(Player p, EquipMask slot) => p.EquippedObjects.Values.Any(o => ((o.CurrentWieldedLocation ?? 0) & slot) != 0);

        static readonly HashSet<uint> KIT = new HashSet<uint> { PUFFY_SHIRT, PANTS, PANTS_ALT, FLARED_PANTS, LEATHER_BOOTS, SHOES, ACID_DAGGER };

        /// <summary>PLAIN CLOTHES MAKE ROOM (a new character starts in the creator's tunic, breeches and boots): worn clothing
        /// in this slot that is not the kit is removed (a bot's starter clothes; worth nothing). Armour (ItemType.Armor) is never touched: what a Borg earned
        /// stays on.</summary>
        static void ClearClothing(Player p, EquipMask slot)
        {
            foreach (var o in p.EquippedObjects.Values.ToList())
            {
                if (((o.CurrentWieldedLocation ?? 0) & slot) == 0 || KIT.Contains(o.WeenieClassId)) continue;
                if (o.ItemType != ItemType.Clothing) { Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} keeps {o.Name} ({o.ItemType})"); continue; }
                // a bot's starter clothes are worth nothing: gone (ACE's dequip leaves an item in no container)
                bool off = p.TryDequipObjectWithNetworking(o.Guid, out var gone, Player.DequipObjectAction.ConsumeItem);
                Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} {(off ? "takes off" : "could not take off")} {o.Name}");
            }
        }

        static bool Wear(Player p, WorldObject wo, List<string> given)
        {
            if (wo == null) return false;
            var at = wo.ValidLocations ?? EquipMask.None;
            // CLOTHING LAYERS (a shirt and pants both cover the abdomen, boots and pants the lower leg): only armour, or the
            // same kind of item already worn there (a weapon, a second shirt), keeps a piece off; ACE's own rules decide the rest
            var under = p.EquippedObjects.Values.Where(o => ((o.CurrentWieldedLocation ?? 0) & at) != 0
                // the kit's own Leather Boots are ItemType.Armor and cover the lower leg (0x180) as Pants do (0xC4): they
                // kept every man's Pants off (live 2026-11-08: Cranius, Maximus, Nexus in no pants). Kit never blocks kit.
                && (o.ItemType == ItemType.Armor && !KIT.Contains(o.WeenieClassId) || o.ItemType == wo.ItemType && wo.ItemType != ItemType.Clothing
                    || o.ValidLocations == wo.ValidLocations)).ToList();
            string why = at == EquipMask.None ? "no slot" : under.Count > 0 ? "slot taken: " + string.Join(", ",
                under.Select(o => $"{o.Name} {o.CurrentWieldedLocation}")) : null;
            // INTO THE PACK FIRST, THEN WIELDED AS A PLAYER WOULD (owner report 261109-0015, "sefa showed up missing pieces
            // in inspect"): TryEquipObjectWithNetworking on a new object sends the wearer only the wield events, never the
            // object, so the wearer's own client had no name or look for it. Created in the pack, the client is told of it;
            // GetAndWieldItem then moves it on with every message a wield sends.
            if (why == null)
            {
                if (!p.TryCreateInInventoryWithNetworking(wo)) why = "pack full";
                else
                {
                    // A MOMENT LATER (shard2 2026-11-09): wielded in the same tick, the wearer's client had the wield before
                    // the new object and left it in the pack ("dressed" on the server, nothing worn in his own view)
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(1.5);
                    chain.AddAction(p, () =>
                    {
                        if (PlayerManager.GetOnlinePlayer(p.Guid) != p) return;
                        p.HandleActionGetAndWieldItem(wo.Guid.Full, at);
                        if (p.EquippedObjects.ContainsKey(wo.Guid)) return;
                        Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} can't wear {wo.Name} ({at}): wield refused");
                        p.TryConsumeFromInventoryWithNetworking(wo);
                    });
                    chain.EnqueueChain();
                }
            }
            if (why != null) { Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} can't wear {wo.Name} ({at}): {why}"); wo.Destroy(); return false; }
            given.Add(wo.Name);
            return true;
        }

        internal static void Dress(Player p)
        {
            var given = new List<string>();
            bool woman = p.GetProperty(PropertyInt.Gender) == (int)Gender.Female;
            foreach (var o in p.EquippedObjects.Values.ToList())   // the creator's own Flared Pants: the kit's colour, in place
                if ((o.WeenieClassId == FLARED_PANTS || o.WeenieClassId == PANTS || o.WeenieClassId == PANTS_ALT)
                    && o.GetProperty(PropertyInt.PaletteTemplate) != DYE_DARK_GREEN
                    || woman && o.WeenieClassId == LEATHER_BOOTS)   // a woman wears shoes: boots would hide the flare
                {
                    p.TryDequipObjectWithNetworking(o.Guid, out _, Player.DequipObjectAction.ConsumeItem);
                    Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} takes off {o.Name} (the creator's colour)");
                }
            ClearClothing(p, EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear);
            ClearClothing(p, EquipMask.UpperLegWear | EquipMask.LowerLegWear | EquipMask.FootWear | EquipMask.HeadWear);
            if (!Armoured(p, EquipMask.ChestWear)) Wear(p, Piece(PUFFY_SHIRT, DYE_SPRING_BLACK, 0.0), given);
            if (!Armoured(p, EquipMask.UpperLegWear) && !Worn(p, PANTS, PANTS_ALT, FLARED_PANTS))
            {
                if (woman) Wear(p, Piece(FLARED_PANTS, DYE_DARK_GREEN, 0.0), given);
                else if (!Wear(p, Piece(PANTS, DYE_DARK_GREEN, 0.0), given)) Wear(p, Piece(PANTS_ALT, DYE_DARK_GREEN, 0.0), given);
            }
            if (!Armoured(p, EquipMask.FootWear) && !Worn(p, LEATHER_BOOTS, SHOES))
                Wear(p, woman ? Piece(SHOES, BLACK, 0.0) : Piece(LEATHER_BOOTS, null, null), given);
            if (p.GetEquippedMeleeWeapon() == null && !Covered(p, EquipMask.MeleeWeapon | EquipMask.TwoHanded | EquipMask.MissileWeapon))
                Wear(p, Piece(ACID_DAGGER, null, null), given);
            if (given.Count > 0) Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} dressed: {string.Join(", ", given)}");
        }

        static void Later(Player p, double secs, string why)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(secs);
            chain.AddAction(p, () =>
            {
                try { if (PlayerManager.GetOnlinePlayer(p.Guid) == p && !p.IsDead && BorgSwitch.IsBorg(p)) Dress(p); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BorgKit: {p.Name} ({why}): {e.Message}"); }
            });
            chain.EnqueueChain();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnLogin
        {
            static void Postfix(Player __instance)
            {
                try { if (BorgSwitch.IsBorg(__instance) && BorgSwitch.On) Later(__instance, 8.0, "login"); }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] BorgKit login: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ThreadSafeTeleportOnDeath))]
        static class OnLifestone
        {
            static void Postfix(Player __instance)
            {
                try { if (BorgSwitch.IsBorg(__instance)) Later(__instance, 12.0, "lifestone"); }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] BorgKit lifestone: " + e.Message); }
            }
        }
    }
}
