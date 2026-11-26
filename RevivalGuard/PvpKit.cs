using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// `@pvpkit [again]` (Admin, ours, not retail; owner 2026-11-25, docs/plans/an admin account-pvp-build-2026-11-25.md section 6):
    /// hands the caller the retail end-game kit PvpKitRule lists, Bonded and Attuned, with the 5 Dexterous + 4 Defenders
    /// set tags and his sheet's gear ratings already on the pieces. Wear it, then "pk suit" (@pksuit) adds the legendary
    /// cantrips, tinks and imbues. Behind rg_pvp_kit (default OFF). Staff only; every item is named in the Audit channel.
    /// Two parts, each given once: the suit (skipped while the caller holds a kit helm) and the missile pack (F7
    /// 261126-0017; skipped while he holds a kit launcher): one Pack with a bow and a crossbow (the best his BASE Missile
    /// Weapons wields), already legendary-cantripped, ten-tinked and imbued, jewelry with cantrips and ratings, matching
    /// ammo, Tumerok Salted Meat and Massive Mana Charges. "again" gives both regardless.
    /// </summary>
    public static class PvpKit
    {
        const AccessLevel LEVEL = AccessLevel.Admin;
        internal const string P_ON = "rg_pvp_kit";
        static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "pvpkit", LEVEL, CommandHandlerFlag.RequiresWorld,
                "Staff (ours, not retail): your retail end-game PvP kit (5 Dexterous + 4 Defenders Haebrean, crossbows, baton, wand, kits) plus a missile pack (suited bow and crossbow, suited jewelry, arrows, quarrels, Tumerok meat, Massive Mana Charges), Bonded; each part once. Needs rg_pvp_kit on.",
                "[again]");
            if ((int)PropertyInt.GearDamage != PvpKitRule.GEAR_DAMAGE || (int)PropertyInt.GearDamageResist != PvpKitRule.GEAR_DAMAGE_RESIST
                || (int)PropertyInt.GearCritDamage != PvpKitRule.GEAR_CRIT_DAMAGE || (int)PropertyInt.GearCritDamageResist != PvpKitRule.GEAR_CRIT_DAMAGE_RESIST
                || (int)PropertyInt.GearPKDamageRating != PvpKitRule.GEAR_PK_DAMAGE || (int)PropertyInt.GearPKDamageResistRating != PvpKitRule.GEAR_PK_DAMAGE_RESIST
                || (int)PropertyInt.GearHealingBoost != PvpKitRule.GEAR_HEALING_BOOST || (int)PropertyInt.GearMaxHealth != PvpKitRule.GEAR_MAX_HEALTH
                || (int)EquipmentSet.Dexterous != PvpKitRule.SET_DEXTEROUS || (int)EquipmentSet.Defenders != PvpKitRule.SET_DEFENDERS
                || (int)Skill.MissileWeapons != 47)
                Mod.Log.Error("[RevivalGuard] PvpKit: ACE PropertyInt/EquipmentSet/Skill values moved; PvpKitRule needs updating");
            // known to @modifybool (and the admin panel's switch buttons), default off
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new ACE.Server.Managers.Property<bool>(false, "RevivalGuard PvpKit: @pvpkit hands an admin the retail end-game PvP kit (staff only)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PvpKit: {P_ON} not registered with @modifybool ({e.Message}); set it by datafix"); }
            Mod.Log.Info($"[RevivalGuard] PvpKit: @pvpkit (Admin; {P_ON} {(On ? "on" : "off")})");
        }

        static void Say(Session s, string text) => s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            if (session.AccessLevel < LEVEL) { Say(session, "@pvpkit is staff only."); return; }
            if (!On) { Say(session, $"[PvP kit] Off on this server ({P_ON}). Nothing was given."); return; }
            try { Give(session, p, parameters.Length > 0 && parameters[0].Equals("again", StringComparison.OrdinalIgnoreCase)); }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] PvpKit: {e}");
                Say(session, $"[PvP kit] Stopped: {e.GetType().Name}: {e.Message}");
            }
        }

        static bool KitPiece(WorldObject o) => o != null && (o.GetProperty(PropertyInt.Attuned) ?? 0) != 0 && (o.GetProperty(PropertyInt.Bonded) ?? 0) != 0;

        static void Give(Session session, Player p, bool again)
        {
            var kit = PvpKitRule.Kit();
            var held = p.GetAllPossessions();
            bool hasSuit = !again && held.Any(o => KitPiece(o) && o.WeenieClassId == kit[0].Wcid && o.EquipmentSetId == (EquipmentSet)PvpKitRule.SET_DEXTEROUS);
            var launchers = new HashSet<uint>(PvpKitRule.LAUNCHERS.SelectMany(t => new[] { t.Bow, t.Crossbow }));
            bool hasPack = !again && held.Any(o => KitPiece(o) && launchers.Contains(o.WeenieClassId) && (o.GetProperty(PropertyInt.GearPKDamageRating) ?? 0) > 0);
            if (hasSuit && hasPack)
            { Say(session, "[PvP kit] You already hold the kit and its missile pack. '@pvpkit again' gives both again."); return; }

            if (hasSuit) Say(session, "[PvP kit] You already hold a kit helm, so the suit part is skipped.");
            else GiveSuit(session, p, kit);
            if (hasPack) Say(session, "[PvP kit] You already hold a kit bow or crossbow, so the missile pack is skipped.");
            else GiveMissilePack(session, p);

            var t = PvpKitRule.WornTotals(PvpKitRule.All());
            int R(int k) => t.TryGetValue(k, out var v) ? v : 0;
            Say(session, $"[PvP kit] Worn with one weapon: damage {R(PvpKitRule.GEAR_DAMAGE)}, crit damage {R(PvpKitRule.GEAR_CRIT_DAMAGE)}, "
                       + $"damage resist {R(PvpKitRule.GEAR_DAMAGE_RESIST)}, crit damage resist {R(PvpKitRule.GEAR_CRIT_DAMAGE_RESIST)}, "
                       + $"PK damage {R(PvpKitRule.GEAR_PK_DAMAGE)}, PK resist {R(PvpKitRule.GEAR_PK_DAMAGE_RESIST)}, "
                       + $"vitality {R(PvpKitRule.GEAR_MAX_HEALTH)}, healing boost {R(PvpKitRule.GEAR_HEALING_BOOST)}. Wear it, then press pk suit for the armour.");
        }

        /// <summary>Creates every entry (stacks split at the weenie's max). Null when a weenie is missing (all destroyed).</summary>
        static List<(PvpKitRule.Entry e, WorldObject wo)> Build(Session session, IEnumerable<PvpKitRule.Entry> entries, Dictionary<PvpKitRule.Entry, WorldObject> premade = null)
        {
            var made = new List<(PvpKitRule.Entry e, WorldObject wo)>();
            foreach (var e in entries)
            {
                if (premade != null && premade.TryGetValue(e, out var pre)) { Dress(pre, e); made.Add((e, pre)); continue; }
                int left = e.Count;
                while (left > 0)
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(e.Wcid);
                    if (wo == null)
                    {
                        foreach (var m in made) m.wo.Destroy();
                        if (premade != null) foreach (var pw in premade.Values) if (!made.Any(m => m.wo == pw)) pw.Destroy();
                        Say(session, $"[PvP kit] Weenie {e.Wcid} ({e.Name}) is not in this server's world data. Nothing was given from that part.");
                        return null;
                    }
                    int n = Math.Min(left, Math.Max(1, (int)(wo.MaxStackSize ?? 1)));
                    if (n > 1) wo.SetStackSize(n);
                    left -= n;
                    Dress(wo, e);
                    made.Add((e, wo));
                }
            }
            return made;
        }

        static void Audit(Player p, PvpKitRule.Entry e, WorldObject wo, string extra = null)
        {
            var line = Describe(e, wo) + (extra != null ? "; " + extra : "");
            PlayerManager.BroadcastToAuditChannel(p, $"{p.Name} @pvpkit 0x{wo.Guid.Full:X8} {line}");
            Mod.Log.Info($"[RevivalGuard] PvpKit: {p.Name} got 0x{wo.Guid.Full:X8} {line}");
        }

        static void GiveSuit(Session session, Player p, List<PvpKitRule.Entry> kit)
        {
            // build everything first: a missing weenie or a full pack gives nothing
            var made = Build(session, kit);
            if (made == null) return;
            int free = p.GetFreeInventorySlots();
            if (free < made.Count)
            {
                foreach (var m in made) m.wo.Destroy();
                Say(session, $"[PvP kit] The suit needs {made.Count} free pack slots, you have {free}. The suit was not given.");
                return;
            }
            int given = 0;
            foreach (var (e, wo) in made)
            {
                if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); Say(session, $"[PvP kit] Could not put {e.Name} in your pack."); continue; }
                given++;
                Audit(p, e, wo);
            }
            Say(session, $"[PvP kit] Suit: {given} of {made.Count} items in your pack, Bonded and Attuned: 5 Dexterous + 4 Defenders Haebrean, smock, trousers, cloak, "
                       + "Mechanical Scarab, Frost Crossbow, Frost Compound Crossbow, Frost Baton, Wand, frost quarrels, kits, elixirs, mana stones.");
        }

        // ---------------------------------------------------------------- the missile pack
        static readonly HashSet<uint> s_BadSpells = new HashSet<uint>();
        static bool s_SpellsChecked;

        static bool SpellOk(uint id)
        {
            if (!s_SpellsChecked)
            {
                s_SpellsChecked = true;
                foreach (var sid in PkSuitRule.NAMES.Keys)
                {
                    Spell sp = null;
                    try { sp = new Spell(sid); } catch (Exception) { }
                    if (sp == null || sp.NotFound) { s_BadSpells.Add(sid); Mod.Log.Warn($"[RevivalGuard] PvpKit: spell {sid} ({PkSuitRule.NAMES[sid]}) not in this server's spell table; skipped"); }
                }
            }
            return !s_BadSpells.Contains(id);
        }

        static int AddSpells(WorldObject wo, IEnumerable<uint> ids)
        {
            var known = new HashSet<int>(wo.Biota.GetKnownSpellsIds(wo.BiotaDatabaseLock));
            int n = 0;
            foreach (var id in ids)
            {
                if (!SpellOk(id) || known.Contains((int)id)) continue;
                wo.Biota.GetOrAddKnownSpell((int)id, wo.BiotaDatabaseLock, out bool ok);
                if (ok) n++;
            }
            return n;
        }

        static void FillMana(WorldObject wo)
        {
            if (wo.Biota.GetKnownSpellsIds(wo.BiotaDatabaseLock).Count == 0) return;
            if ((wo.ManaRate ?? 0) == 0) wo.ManaRate = PkSuitRule.DEFAULT_MANA_RATE;
            int max = PkSuitRule.EveningMana(wo.ItemMaxMana ?? 0, -(wo.ManaRate ?? 0));
            wo.ItemMaxMana = max;
            wo.ItemCurMana = max;
        }

        /// <summary>A launcher as @pksuit would leave it: the missile legendaries, an imbue and mahogany to ten tinks.
        /// The crossbow takes Crippling Blow (his retail Gold Crossbow of the Fallen had it), the bow Armor Rending.</summary>
        static string SuitLauncher(WorldObject wo, bool crossbow)
        {
            int spells = AddSpells(wo, PkSuitRule.ItemSpells(PkSuitRule.Kind.Missile));
            var plan = PkSuitRule.TinkerPlan(PkSuitRule.Kind.Missile, wo.NumTimesTinkered, wo.ImbuedEffect != ImbuedEffectType.Undef, false);
            if (crossbow && plan.Imbue == SignedLootRule.SUNSTONE) plan.Imbue = SignedLootRule.FIRE_OPAL;
            int tinks = 0;
            if (plan.Imbue != 0 && PkSuit.Tink(wo, plan.Imbue))
            {
                tinks++;
                if (RecipeManager.IconUnderlay.TryGetValue(wo.ImbuedEffect, out var underlay)) wo.IconUnderlayId = underlay;
            }
            foreach (var m in plan.Steps) if (PkSuit.Tink(wo, m)) tinks++;
            FillMana(wo);
            return $"+{spells} legendary spells, {tinks} tinkers (now {wo.NumTimesTinkered}), {wo.ImbuedEffect}";
        }

        static void GiveMissilePack(Session session, Player p)
        {
            int baseMissile = (int)p.GetCreatureSkill(Skill.MissileWeapons).Base;
            var tier = PvpKitRule.PickLaunchers(baseMissile, out bool wieldable);
            var bow = WorldObjectFactory.CreateNewWorldObject(tier.Bow);
            var xbow = WorldObjectFactory.CreateNewWorldObject(tier.Crossbow);
            var pack = WorldObjectFactory.CreateNewWorldObject(PvpKitRule.PACK_WCID) as Container;
            if (bow == null || xbow == null || pack == null)
            {
                bow?.Destroy(); xbow?.Destroy(); pack?.Destroy();
                Say(session, $"[PvP kit] {tier.BowName}, {tier.CrossbowName} or the Pack is not in this server's world data. The missile pack was not given.");
                return;
            }
            var entries = PvpKitRule.MissilePack(tier, (int)bow.W_DamageType, (int)xbow.W_DamageType);
            var made = Build(session, entries, new Dictionary<PvpKitRule.Entry, WorldObject> { { entries[0], bow }, { entries[1], xbow } });
            if (made == null) { pack.Destroy(); return; }

            var notes = new Dictionary<WorldObject, string>
            {
                { bow, SuitLauncher(bow, false) },
                { xbow, SuitLauncher(xbow, true) },
            };
            // jewelry: wearer cantrips not already on both launchers, the rated-jewelry wield level, mana
            var both = new HashSet<uint>(bow.Biota.GetKnownSpellsIds(bow.BiotaDatabaseLock).Select(i => (uint)i)
                .Intersect(xbow.Biota.GetKnownSpellsIds(xbow.BiotaDatabaseLock).Select(i => (uint)i)));
            var trained = p.Skills.Where(kv => kv.Value.AdvancementClass >= SkillAdvancementClass.Trained).Select(kv => (int)kv.Key).ToList();
            var jewelry = made.Where(m => m.e.Kind == PvpKitRule.Kind.Jewelry).Select(m => m.wo).ToList();
            var plan = PvpKitRule.JewelrySpells(trained, both, jewelry.Count);
            for (int i = 0; i < jewelry.Count; i++)
            {
                var j = jewelry[i];
                AddSpells(j, plan[i]);
                FillMana(j);
                if (j.WieldRequirements == WieldRequirement.Invalid)
                {
                    j.WieldRequirements = WieldRequirement.Level;
                    j.WieldSkillType = (int)Skill.Axe;   // as ACE's loot sets it (pcap data)
                    j.WieldDifficulty = PvpKitRule.RATED_WIELD_LEVEL;
                }
                notes[j] = string.Join(", ", plan[i].Where(SpellOk).Select(s => PkSuitRule.NAMES[s].Replace("Legendary ", "")));
            }

            if (made.Count > PvpKitRule.PACK_SLOTS)
            { foreach (var m in made) m.wo.Destroy(); pack.Destroy(); Say(session, $"[PvP kit] The missile pack holds {made.Count} items, more than a Pack's {PvpKitRule.PACK_SLOTS}. Not given."); return; }
            pack.Name = "PvP Kit Pack";
            foreach (var (e, wo) in made)
                if (!pack.TryAddToInventory(wo))
                { foreach (var m in made) m.wo.Destroy(); pack.Destroy(); Say(session, $"[PvP kit] Could not pack {e.Name}. The missile pack was not given."); return; }

            if (p.GetFreeContainerSlots() < 1)
            {
                foreach (var m in made) m.wo.Destroy(); pack.Destroy();
                Say(session, "[PvP kit] The missile pack comes in its own Pack and all your side-pack slots are full. Empty or drop one side pack, then press pvp kit again.");
                return;
            }
            if (!p.TryCreateInInventoryWithNetworking(pack))
            {
                foreach (var m in made) m.wo.Destroy(); pack.Destroy();
                Say(session, $"[PvP kit] Could not give the PvP Kit Pack (burden {pack.EncumbranceVal ?? 0}; you can carry {p.GetAvailableBurden()} more). Nothing was given.");
                return;
            }
            foreach (var (e, wo) in made)
            {
                wo.OwnerId = p.Guid.Full;   // root owner, as a pack loaded from the database has it
                wo.SaveBiotaToDatabase();
                notes.TryGetValue(wo, out var n);
                Audit(p, e, wo, n);
            }

            Say(session, $"[PvP kit] Missile pack: a new side pack, \"PvP Kit Pack\", {made.Count} items, Bonded and Attuned, burden {pack.EncumbranceVal ?? 0}.");
            Say(session, $"[PvP kit] {tier.BowName} and {tier.CrossbowName}: {notes[bow]} / {notes[xbow]}; Legendary Blood Thirst, Heart Thirst, Defender, Swift Hunter; PK 1/1.");
            Say(session, $"[PvP kit] Jewelry (necklace, 2 bracelets, 2 rings): 4 legendary cantrips each, PK 1/1, vitality 3 (necklace, bracelets) or healing boost 3 (rings); needs level {PvpKitRule.RATED_WIELD_LEVEL}.");
            Say(session, $"[PvP kit] {entries[7].Count} {entries[7].Name}s, {entries[8].Count} {entries[8].Name}s (they match the launchers' element), 100 Tumerok Salted Meat, 10 Massive Mana Charges.");
            if (!wieldable)
                Say(session, $"[PvP kit] Your base Missile Weapons is {baseMissile}: even these launchers need {tier.MinBase}, so you cannot wield them yet.");
            else if (tier != PvpKitRule.LAUNCHERS[0])
                Say(session, $"[PvP kit] Your base Missile Weapons is {baseMissile}; Stormwood needs {PvpKitRule.LAUNCHERS[0].MinBase}, so you got the best you can wield.");
        }

        static void Dress(WorldObject wo, PvpKitRule.Entry e)
        {
            wo.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            wo.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            if (e.Set != 0) wo.EquipmentSetId = (EquipmentSet)e.Set;
            foreach (var kv in e.Ratings) wo.SetProperty((PropertyInt)kv.Key, kv.Value);
        }

        static string Describe(PvpKitRule.Entry e, WorldObject wo)
        {
            var bits = new List<string>();
            if ((wo.StackSize ?? 1) > 1) bits.Add($"x{wo.StackSize}");
            if (e.Set != 0) bits.Add(((EquipmentSet)e.Set).ToString());
            foreach (var kv in e.Ratings) bits.Add($"{(PropertyInt)kv.Key} {kv.Value}");
            return $"{wo.Name}{(bits.Count > 0 ? " (" + string.Join(", ", bits) + ")" : "")}";
        }
    }
}
