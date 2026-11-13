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
    ///
    /// THE BONDED SUITS (owner 2026-11-12: "lets make them their armor and have it be bonded. there were two suits. one
    /// for lower level ranges and one for mid level. then once they are high enough level for a epic and legendary suit
    /// they make their own"). The weenies 911300-911316 (datafix 2026-11-12-borg-bonded-suits.sql, tools/borg/suit_sql.py)
    /// replace the plain kit when they exist: LOW (under 40) the black tops at AL 120 as Pathwarden plate, bald; MID
    /// (40 to 79) the gold Celdon suit, silver helm, Sollerets and red Aegis at AL 240 (wields at 40), as the Amateur
    /// Explorer set. In its band the suit is the look: earned armor in the slots it covers goes to the pack (kept), and
    /// crossing 40 swaps low for mid. From 80 nothing is given or taken: the Borg wear what they earn and tailor this
    /// look onto it (tools/borg/look.py). Without the datafix, the plain kit as before.
    /// </summary>
    public static class BorgKit
    {
        const uint PUFFY_SHIRT = 2591, PANTS = 127, PANTS_ALT = 907706, FLARED_PANTS = 2597, LEATHER_BOOTS = 115, SHOES = 132,
                   ACID_DAGGER = 3778;
        const int DYE_SPRING_BLACK = 93, DYE_DARK_GREEN = 84, BLACK = 39;
        // the bonded suits (tools/borg/suit_sql.py): low, then mid
        const uint S_SHIRT = 911300, S_PANTS = 911301, S_FLARED = 911302, S_BOOTS = 911303, S_SHOES = 911304,
                   S_BREAST = 911310, S_GIRTH = 911311, S_LEGGINGS = 911312, S_SLEEVES = 911313, S_SOLLERETS = 911314,
                   S_HELM = 911315, S_AEGIS = 911316;
        internal const int MID_FROM = 40, OWN_FROM = 80;   // tools/borg/look.py MID_FROM, OWN_FROM
        static readonly uint[] LOW_SUIT = { S_SHIRT, S_PANTS, S_FLARED, S_BOOTS, S_SHOES };
        static readonly uint[] MID_BODY = { S_BREAST, S_GIRTH, S_LEGGINGS, S_SLEEVES, S_SOLLERETS, S_HELM };
        internal static bool IsSuit(uint wcid) => wcid >= 911300 && wcid <= 911316;   // (weapons: IsSignature)
        static bool? s_suits;
        /// <summary>The datafix is in: the suit weenies exist (asked once; a missing one is logged by ACE).</summary>
        static bool Suits => s_suits ??= ACE.Database.DatabaseManager.World.GetCachedWeenie(S_SHIRT) != null;

        // the slots a suit holds for its look: earned armor there goes to the pack. Low leaves the head bare (retail's
        // black tops are bald) and the hands free; mid leaves the hands free.
        const EquipMask BODY = EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor | EquipMask.LowerArmArmor
                             | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor | EquipMask.FootWear | EquipMask.HeadWear;

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

        static readonly HashSet<uint> KIT = new HashSet<uint> { PUFFY_SHIRT, PANTS, PANTS_ALT, FLARED_PANTS, LEATHER_BOOTS, SHOES, ACID_DAGGER,
            S_SHIRT, S_PANTS, S_FLARED, S_BOOTS, S_SHOES, S_BREAST, S_GIRTH, S_LEGGINGS, S_SLEEVES, S_SOLLERETS, S_HELM, S_AEGIS };
        static readonly HashSet<uint> PLAIN_KIT = new HashSet<uint> { PUFFY_SHIRT, PANTS, PANTS_ALT, FLARED_PANTS, LEATHER_BOOTS, SHOES };

        /// <summary>A suit piece, or the plain kit's (bonded at run time) when the suit weenies are not in the world DB.</summary>
        static WorldObject Suit(uint suit, uint plain, int? palette, double? shade) =>
            (Suits ? WorldObjectFactory.CreateNewWorldObject(suit) : null) ?? Piece(plain, palette, shade);

        /// <summary>Our own pieces in the pack or worn, by class id.</summary>
        static List<WorldObject> Owned(Player p, params uint[] wcids) =>
            p.EquippedObjects.Values.Where(o => wcids.Contains(o.WeenieClassId))
             .Concat(wcids.SelectMany(w => p.GetInventoryItemsOfWCID(w))).ToList();

        /// <summary>Pieces of the kit that are no longer ours to wear go: consumed (bonded, worth nothing).</summary>
        static void Retire(Player p, IEnumerable<WorldObject> pieces, string why)
        {
            foreach (var o in pieces.ToList())
            {
                bool gone = p.EquippedObjects.ContainsKey(o.Guid)
                    ? p.TryDequipObjectWithNetworking(o.Guid, out _, Player.DequipObjectAction.ConsumeItem)
                    : p.TryConsumeFromInventoryWithNetworking(o);
                Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} {(gone ? "retires" : "could not retire")} {o.Name} ({why})");
            }
        }

        /// <summary>THE SUIT IS THE LOOK: earned armor in the suit's slots goes to the pack, kept (never destroyed; left on
        /// when the pack is full).</summary>
        static void ToPack(Player p, EquipMask slots)
        {
            foreach (var o in p.EquippedObjects.Values.ToList())
            {
                // (the suit's own pieces stay; the creator's Leather Boots are wcid 115 like the old kit's, but not ours: they go)
                if (((o.CurrentWieldedLocation ?? 0) & slots) == 0 || IsSuit(o.WeenieClassId) || o.ItemType != ItemType.Armor) continue;
                if (p.GetFreeInventorySlots() < 1) { Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} keeps {o.Name} on: pack full"); continue; }
                if (p.TryDequipObjectWithNetworking(o.Guid, out var off, Player.DequipObjectAction.DequipToPack)
                    && p.TryCreateInInventoryWithNetworking(off))
                    Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} packs {o.Name} (the suit's slot)");
                else Mod.Log.Warn($"[RevivalGuard] BorgKit: {p.Name} could not pack {o.Name}");
            }
        }

        /// <summary>One suit piece worn: already on, or the carried one put on, or a new one made.</summary>
        static void WearSuit(Player p, uint wcid, List<string> given)
        {
            if (Worn(p, wcid)) return;
            var have = p.GetInventoryItemsOfWCID(wcid).FirstOrDefault();
            if (have == null) { Wear(p, Suit(wcid, 0, null, null), given); return; }
            var at = have.ValidLocations ?? EquipMask.None;
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.5);
            chain.AddAction(p, () =>
            {
                if (PlayerManager.GetOnlinePlayer(p.Guid) != p || p.EquippedObjects.ContainsKey(have.Guid)) return;
                p.HandleActionGetAndWieldItem(have.Guid.Full, at);
                if (!p.EquippedObjects.ContainsKey(have.Guid))
                    Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} can't put on {have.Name} ({at}): wield refused");
            });
            chain.EnqueueChain();
            given.Add(have.Name);
        }

        // THE SIGNATURE WEAPONS (owner 2026-11-12: "same with the weapons they were designed to use. bonded so they actually
        // use them and designed their stats to they are useful and fair for their level ranges"). Low: the Acid set,
        // mid: the Weeping set (tools/borg/suit_sql.py WEAPONS: base +20). Kind: what the Borg wields (its skill and weapon
        // type), else its best trained weapon skill. Damage follows its level by Curve (the same table as tools/borg/look.py
        // WEAPON_CURVE, anchored on the quest and loot weapons of each level: docs/borg/GEAR-TRACK.md).
        const uint W_LOW = 911320, W_MID = 911340;
        enum Kind { HeavySword, HeavyMace, HeavyAxe, LightDagger, LightMace, LightAxe, Bow, Crossbow, Wand }
        internal static bool IsSignature(uint wcid) => wcid >= 911320 && wcid <= 911348;

        static readonly Dictionary<string, (int lv, double v)[]> Curve = new Dictionary<string, (int, double)[]>
        {
            ["low heavy"] = new[] { (1, 18.0), (25, 30.0), (39, 32.0) },
            ["low light"] = new[] { (1, 14.0), (10, 22.0), (25, 30.0), (39, 34.0) },
            ["low bow"] = new[] { (1, 1.5), (20, 2.1), (39, 2.3) },
            ["low crossbow"] = new[] { (1, 1.7), (20, 2.3), (39, 2.5) },
            ["low war"] = new[] { (1, 1.0), (35, 1.05), (39, 1.05) },
            ["mid heavy"] = new[] { (40, 44.0), (60, 50.0), (79, 62.0) },
            ["mid light"] = new[] { (40, 36.0), (79, 44.0) },
            ["mid bow"] = new[] { (40, 2.3), (79, 2.55) },
            ["mid crossbow"] = new[] { (40, 2.5), (79, 2.75) },
            ["mid war"] = new[] { (40, 1.07), (79, 1.12) },
        };

        static double At(string key, int lv)
        {
            var pts = Curve[key];
            lv = Math.Max(pts[0].lv, Math.Min(pts[pts.Length - 1].lv, lv));
            for (int i = 0; i + 1 < pts.Length; i++)
                if (lv >= pts[i].lv && lv <= pts[i + 1].lv)
                    return pts[i].v + (pts[i + 1].v - pts[i].v) * (lv - pts[i].lv) / (pts[i + 1].lv - pts[i].lv);
            return pts[pts.Length - 1].v;
        }

        static string Group(Kind k) => k <= Kind.HeavyAxe ? "heavy" : k <= Kind.LightAxe ? "light"
            : k == Kind.Bow ? "bow" : k == Kind.Crossbow ? "crossbow" : "war";

        static Kind? KindOf(WorldObject w)
        {
            if (w == null) return null;
            if (IsSignature(w.WeenieClassId)) return (Kind)((w.WeenieClassId - 911320) % 20);
            if (w.ItemType == ItemType.Caster) return Kind.Wand;
            if (w.ItemType == ItemType.MissileWeapon) return w.W_WeaponType == WeaponType.Crossbow ? Kind.Crossbow : Kind.Bow;
            bool light = w.WeaponSkill == Skill.LightWeapons || w.WeaponSkill == Skill.FinesseWeapons;
            switch (w.W_WeaponType)
            {
                case WeaponType.Sword: return light ? Kind.LightDagger : Kind.HeavySword;
                case WeaponType.Axe: return light ? Kind.LightAxe : Kind.HeavyAxe;
                case WeaponType.Dagger: return light ? Kind.LightDagger : Kind.HeavySword;
                default: return light ? Kind.LightMace : Kind.HeavyMace;
            }
        }

        static bool Trained(Player p, Skill s) => p.GetCreatureSkill(s).AdvancementClass >= SkillAdvancementClass.Trained;

        /// <summary>No weapon in hand: its best trained weapon skill (a "mage" without War Magic fights as Heavy, as the
        /// brain does); a Heavy fighter's default is the mace (the Academy Mace it starts with), a Light one's the dagger.</summary>
        static Kind ByTraining(Player p)
        {
            Skill best = Skill.HeavyWeapons; uint top = 0;
            foreach (var s in new[] { Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.MissileWeapons, Skill.WarMagic })
            {
                if (!Trained(p, s)) continue;
                var cur = p.GetCreatureSkill(s).Current;
                if (cur > top) { top = cur; best = s; }
            }
            return best == Skill.WarMagic ? Kind.Wand : best == Skill.MissileWeapons ? Kind.Bow
                 : best == Skill.HeavyWeapons ? Kind.HeavyMace : Kind.LightDagger;
        }

        /// <summary>A signature weapon's numbers for this level (the instance, not the weenie).</summary>
        static void Tune(Player p, WorldObject w, string band)
        {
            var k = KindOf(w).Value; var g = Group(k); int lv = p.Level ?? 1;
            var v = At(band + " " + g, lv);
            if (g == "heavy" || g == "light") { if (w.Damage != (int)Math.Round(v)) w.Damage = (int)Math.Round(v); }
            else if (g == "war") w.ElementalDamageMod = Math.Round(v, 2);
            else w.DamageMod = Math.Round(v, 2);
        }

        /// <summary>THE BAND'S WEAPON IN HAND: the other band's signature retired, the old acid dagger of the plain kit too;
        /// any other weapon goes to the pack (kept); the signature made if not carried, set to our level, wielded.</summary>
        static void Arm(Player p, string band, List<string> given)
        {
            var hand = p.GetEquippedMeleeWeapon() ?? p.GetEquippedMissileLauncher() ?? p.GetEquippedWand();
            var kind = KindOf(hand) ?? ByTraining(p);
            if (kind == Kind.Wand && !Trained(p, Skill.WarMagic)) kind = Kind.HeavyMace;
            uint want = (band == "low" ? W_LOW : W_MID) + (uint)kind;
            var others = Enumerable.Range(0, 9).Select(i => (band == "low" ? W_MID : W_LOW) + (uint)i)
                .Concat(Enumerable.Range(0, 9).Select(i => (band == "low" ? W_LOW : W_MID) + (uint)i).Where(w => w != want)).ToArray();
            Retire(p, Owned(p, others), "the " + band + " band's weapon");
            Retire(p, Owned(p, ACID_DAGGER).Where(o => o.GetProperty(PropertyInt.Bonded) == (int)BondedStatus.Bonded), "the signature weapon");
            var mine = Owned(p, want).FirstOrDefault();
            if (mine == null)
            {
                mine = WorldObjectFactory.CreateNewWorldObject(want);
                if (mine == null || !p.TryCreateInInventoryWithNetworking(mine)) { mine?.Destroy(); return; }
            }
            Tune(p, mine, band);
            if (p.EquippedObjects.ContainsKey(mine.Guid)) return;
            foreach (var o in p.EquippedObjects.Values.Where(o => (o.ItemType & (ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster)) != 0).ToList())
                if (p.GetFreeInventorySlots() > 0 && p.TryDequipObjectWithNetworking(o.Guid, out var off, Player.DequipObjectAction.DequipToPack))
                {
                    p.TryCreateInInventoryWithNetworking(off);
                    Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} packs {o.Name} (the signature weapon)");
                }
            var at = mine.ValidLocations ?? EquipMask.None;
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.5);
            chain.AddAction(p, () =>
            {
                if (PlayerManager.GetOnlinePlayer(p.Guid) != p || p.EquippedObjects.ContainsKey(mine.Guid)) return;
                p.HandleActionGetAndWieldItem(mine.Guid.Full, at);
                if (!p.EquippedObjects.ContainsKey(mine.Guid)) Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} can't wield {mine.Name}");
            });
            chain.EnqueueChain();
            given.Add(mine.Name);
        }

        /// <summary>THE MID SUIT (40 to 79): the low suit retired, earned armor in its slots to the pack, the suit on; the
        /// Aegis only with a one-handed weapon or a caster (a bow or a two-hander keeps it in the pack).</summary>
        static void DressMid(Player p, List<string> given)
        {
            Retire(p, Owned(p, LOW_SUIT), "the mid suit from level 40");
            Retire(p, Owned(p, PLAIN_KIT.ToArray()).Where(o => o.GetProperty(PropertyInt.Bonded) == (int)BondedStatus.Bonded),
                   "the mid suit from level 40");
            ToPack(p, BODY);
            foreach (var w in MID_BODY) WearSuit(p, w, given);
            Arm(p, "mid", given);
            var held = p.GetEquippedMissileLauncher();
            if (held != null || Covered(p, EquipMask.TwoHanded | EquipMask.MissileWeapon)
                || Owned(p, W_MID + (uint)Kind.Bow, W_MID + (uint)Kind.Crossbow).Count > 0)
            {
                if (Owned(p, S_AEGIS).Count == 0) { var a = Suit(S_AEGIS, 0, null, null); if (a == null || !p.TryCreateInInventoryWithNetworking(a)) a?.Destroy(); }
                return;
            }
            var shield = p.EquippedObjects.Values.FirstOrDefault(o => ((o.CurrentWieldedLocation ?? 0) & EquipMask.Shield) != 0);
            if (shield != null && shield.WeenieClassId != S_AEGIS && p.GetFreeInventorySlots() > 0
                && p.TryDequipObjectWithNetworking(shield.Guid, out var off, Player.DequipObjectAction.DequipToPack))
                p.TryCreateInInventoryWithNetworking(off);
            WearSuit(p, S_AEGIS, given);
        }

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
            int lv = p.Level ?? 1;
            if (Suits && lv >= OWN_FROM) return;            // their own armor from 80: nothing given or taken
            if (Suits && lv >= MID_FROM)
            {
                DressMid(p, given);
                if (given.Count > 0) Mod.Log.Info($"[RevivalGuard] BorgKit: {p.Name} (level {lv}) mid suit: {string.Join(", ", given)}");
                return;
            }
            if (Suits)
            {
                // THE LOW SUIT replaces the plain kit (bonded at run time before the datafix) and holds its slots
                Retire(p, Owned(p, PLAIN_KIT.ToArray()).Where(o => o.GetProperty(PropertyInt.Bonded) == (int)BondedStatus.Bonded),
                       "the bonded low suit");
                ToPack(p, BODY);
            }
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
            if (!Armoured(p, EquipMask.ChestWear) && !Worn(p, S_SHIRT)) Wear(p, Suit(S_SHIRT, PUFFY_SHIRT, DYE_SPRING_BLACK, 0.0), given);
            if (!Armoured(p, EquipMask.UpperLegWear) && !Worn(p, PANTS, PANTS_ALT, FLARED_PANTS, S_PANTS, S_FLARED))
            {
                if (woman) Wear(p, Suit(S_FLARED, FLARED_PANTS, DYE_DARK_GREEN, 0.0), given);
                else if (!Wear(p, Suit(S_PANTS, PANTS, DYE_DARK_GREEN, 0.0), given)) Wear(p, Piece(PANTS_ALT, DYE_DARK_GREEN, 0.0), given);
            }
            if (!Armoured(p, EquipMask.FootWear) && !Worn(p, LEATHER_BOOTS, SHOES, S_BOOTS, S_SHOES))
                Wear(p, woman ? Suit(S_SHOES, SHOES, BLACK, 0.0) : Suit(S_BOOTS, LEATHER_BOOTS, null, null), given);
            if (Suits) Arm(p, "low", given);
            else if (p.GetEquippedMeleeWeapon() == null && !Covered(p, EquipMask.MeleeWeapon | EquipMask.TwoHanded | EquipMask.MissileWeapon))
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

        /// <summary>CROSSING A BAND: a level-up to 40 swaps low for mid at once (and to 80 leaves things be).</summary>
        [HarmonyPatch]
        static class OnLevelUp
        {
            static System.Reflection.MethodBase Target() => AccessTools.Method(typeof(Player), "CheckForLevelup");
            static bool Prepare() => Target() != null;
            static System.Reflection.MethodBase TargetMethod() => Target();
            static void Prefix(Player __instance, out int __state) => __state = __instance.Level ?? 1;
            static void Postfix(Player __instance, int __state)
            {
                try
                {
                    int now = __instance.Level ?? 1;
                    // every level: the signature weapon's numbers follow the level; at 40 the suits swap
                    if (Suits && now > __state && now <= OWN_FROM && BorgSwitch.On && BorgSwitch.IsBorg(__instance)) Later(__instance, 3.0, "level " + now);
                }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] BorgKit level-up: " + e.Message); }
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
