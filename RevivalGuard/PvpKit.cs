using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
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
    /// A second press is refused while the caller still holds a kit helm, unless "again".
    /// </summary>
    public static class PvpKit
    {
        const AccessLevel LEVEL = AccessLevel.Admin;
        internal const string P_ON = "rg_pvp_kit";
        static bool On { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "pvpkit", LEVEL, CommandHandlerFlag.RequiresWorld,
                "Staff (ours, not retail): your retail end-game PvP kit in your pack (5 Dexterous + 4 Defenders Haebrean, jewelry, crossbows, baton, wand, ammo, kits), Bonded, set tags and gear ratings on; wear it, then @pksuit. Needs rg_pvp_kit on.",
                "[again]");
            if ((int)PropertyInt.GearDamage != PvpKitRule.GEAR_DAMAGE || (int)PropertyInt.GearDamageResist != PvpKitRule.GEAR_DAMAGE_RESIST
                || (int)PropertyInt.GearCritDamage != PvpKitRule.GEAR_CRIT_DAMAGE || (int)PropertyInt.GearCritDamageResist != PvpKitRule.GEAR_CRIT_DAMAGE_RESIST
                || (int)PropertyInt.GearPKDamageRating != PvpKitRule.GEAR_PK_DAMAGE || (int)PropertyInt.GearPKDamageResistRating != PvpKitRule.GEAR_PK_DAMAGE_RESIST
                || (int)EquipmentSet.Dexterous != PvpKitRule.SET_DEXTEROUS || (int)EquipmentSet.Defenders != PvpKitRule.SET_DEFENDERS)
                Mod.Log.Error("[RevivalGuard] PvpKit: ACE PropertyInt/EquipmentSet values moved; PvpKitRule needs updating");
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

        static void Give(Session session, Player p, bool again)
        {
            var kit = PvpKitRule.Kit();
            if (!again && p.GetAllPossessions().Any(o => o != null && o.WeenieClassId == kit[0].Wcid && o.EquipmentSetId == (EquipmentSet)PvpKitRule.SET_DEXTEROUS
                                                       && (o.GetProperty(PropertyInt.Attuned) ?? 0) != 0))
            { Say(session, "[PvP kit] You already hold a kit helm. '@pvpkit again' gives another full kit."); return; }

            // build everything first: a missing weenie or a full pack gives nothing
            var made = new List<(PvpKitRule.Entry e, WorldObject wo)>();
            foreach (var e in kit)
            {
                int left = e.Count;
                while (left > 0)
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(e.Wcid);
                    if (wo == null)
                    {
                        foreach (var m in made) m.wo.Destroy();
                        Say(session, $"[PvP kit] Weenie {e.Wcid} ({e.Name}) is not in this server's world data. Nothing was given.");
                        return;
                    }
                    int n = Math.Min(left, Math.Max(1, (int)(wo.MaxStackSize ?? 1)));
                    if (n > 1) wo.SetStackSize(n);
                    left -= n;
                    Dress(wo, e);
                    made.Add((e, wo));
                }
            }
            int free = p.GetFreeInventorySlots();
            if (free < made.Count)
            {
                foreach (var m in made) m.wo.Destroy();
                Say(session, $"[PvP kit] Needs {made.Count} free pack slots, you have {free}. Nothing was given.");
                return;
            }

            int given = 0;
            foreach (var (e, wo) in made)
            {
                if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); Say(session, $"[PvP kit] Could not put {e.Name} in your pack."); continue; }
                given++;
                var line = Describe(e, wo);
                PlayerManager.BroadcastToAuditChannel(p, $"{p.Name} @pvpkit 0x{wo.Guid.Full:X8} {line}");
                Mod.Log.Info($"[RevivalGuard] PvpKit: {p.Name} got 0x{wo.Guid.Full:X8} {line}");
            }
            var t = PvpKitRule.WornTotals(kit);
            int R(int k) => t.TryGetValue(k, out var v) ? v : 0;
            Say(session, $"[PvP kit] {given} of {made.Count} items in your pack, Bonded and Attuned: 5 Dexterous + 4 Defenders Haebrean, smock, trousers, cloak, "
                       + "necklace, 2 bracelets, 2 rings, Mechanical Scarab, Frost Crossbow, Frost Compound Crossbow, Frost Baton, Wand, quarrels, kits, elixirs, mana stones.");
            Say(session, $"[PvP kit] Worn with one weapon: damage {R(PvpKitRule.GEAR_DAMAGE)}, crit damage {R(PvpKitRule.GEAR_CRIT_DAMAGE)}, "
                       + $"damage resist {R(PvpKitRule.GEAR_DAMAGE_RESIST)}, crit damage resist {R(PvpKitRule.GEAR_CRIT_DAMAGE_RESIST)}, "
                       + $"PK damage {R(PvpKitRule.GEAR_PK_DAMAGE)}, PK resist {R(PvpKitRule.GEAR_PK_DAMAGE_RESIST)}. Wear it, then press pk suit.");
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
