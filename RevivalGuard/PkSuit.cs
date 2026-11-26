using System.Globalization;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Mutations;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using K = RevivalGuard.PkSuitRule.Kind;

namespace RevivalGuard
{
    /// <summary>
    /// `@pksuit` (Admin, ours, not retail; owner 2026-11-22, docs/proposals/staff-event-toolkit.md item 1): everything the
    /// staff member WEARS and WIELDS becomes PvP-ready in place: the legendary spells PkSuitRule plans for its slot, the
    /// tinkers a 10-tink retail piece carries (ACE's own mutation scripts, so the numbers are a player's tinker's),
    /// Bonded (Player.CalculateDeathItems skips Bonded != 0: never drops on death), and item mana for an evening. Wield
    /// requirements are not touched. New spells on worn items are cast at once (CreateItemSpell, as equipping does).
    ///
    /// `@pksuit undo` puts back every property and spell the verb changed, on the worn, wielded and carried items that
    /// have a record. Records: PkSuit/&lt;item guid&gt;.json beside the mod: the value each changed property had before the
    /// FIRST @pksuit (absent = it had none) and the spells it added. A second @pksuit keeps the first values.
    /// Every item is named in the Audit channel (AuditTrail keeps it on disk).
    /// </summary>
    public static class PkSuit
    {
        const AccessLevel LEVEL = AccessLevel.Admin;
        static string s_Folder;
        static bool s_Checked;
        static HashSet<uint> s_Missing = new HashSet<uint>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "pksuit", LEVEL, CommandHandlerFlag.RequiresWorld,
                "Staff (ours, not retail): make everything you wear and wield PvP-ready: legendary cantrips per slot, 10 tinkers, imbue, Bonded, full mana. '@pksuit undo' reverts.",
                "[undo]");
            // PkSuitRule keys skills by ACE's enum values: say so loudly on a build where they moved.
            var moved = PkSuitRule.SKILL_SPELL.Keys.Where(k => !Enum.IsDefined(typeof(Skill), k)).ToList();
            if (Skill.HeavyWeapons.ToString() != ((Skill)44).ToString() || moved.Count > 0)
                Mod.Log.Error("[RevivalGuard] PkSuit: ACE Skill enum values moved; PkSuitRule.SKILL_SPELL needs updating");
            Mod.Log.Info("[RevivalGuard] PkSuit: @pksuit [undo] (Admin)");
        }

        static void Say(Session s, string text) => s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        /// <summary>Every planned id against the server's own spell table, once; missing ones are logged and skipped.</summary>
        static void CheckSpells()
        {
            if (s_Checked) return;
            s_Checked = true;
            foreach (var id in PkSuitRule.NAMES.Keys)
            {
                Spell sp = null;
                try { sp = new Spell(id); } catch (Exception) { }
                if (sp == null || sp.NotFound) { s_Missing.Add(id); Mod.Log.Warn($"[RevivalGuard] PkSuit: spell {id} ({PkSuitRule.NAMES[id]}) is not in this server's spell table; skipped"); }
                else if (sp.Name != PkSuitRule.NAMES[id]) Mod.Log.Warn($"[RevivalGuard] PkSuit: spell {id} is '{sp.Name}' here, the plan says '{PkSuitRule.NAMES[id]}'");
            }
            Mod.Log.Info($"[RevivalGuard] PkSuit: {PkSuitRule.NAMES.Count - s_Missing.Count} of {PkSuitRule.NAMES.Count} legendary spells found in the spell table");
        }

        static void Handle(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            if (session.AccessLevel < LEVEL) { Say(session, "@pksuit is staff only."); return; }
            s_Folder ??= ModFolder.Sub("PkSuit");
            if (s_Folder == null) { Say(session, "[PK suit] No folder for undo records beside the mod; nothing was changed."); return; }
            try
            {
                if (parameters.Length > 0 && parameters[0].Equals("undo", StringComparison.OrdinalIgnoreCase)) Undo(session, p);
                else Apply(session, p);
            }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] PkSuit: {e}");
                Say(session, $"[PK suit] Stopped: {e.GetType().Name}: {e.Message}");
            }
        }

        internal static K KindOf(WorldObject wo)
        {
            var at = wo.CurrentWieldedLocation ?? EquipMask.None;
            if ((at & (EquipMask.NeckWear | EquipMask.WristWear | EquipMask.FingerWear)) != 0) return K.Jewelry;
            if ((at & (EquipMask.TrinketOne | EquipMask.Sigil | EquipMask.MissileAmmo)) != 0) return K.Other;
            if ((at & EquipMask.Cloak) != 0) return K.Cloak;
            if ((at & EquipMask.Shield) != 0) return K.Shield;
            if (wo is Caster) return K.Caster;
            if (wo is MissileLauncher) return K.Missile;
            if (wo is MeleeWeapon) return K.Melee;
            if ((at & (EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.TwoHanded)) != 0) return K.Other;
            if ((wo.ItemType & ItemType.Armor) != 0) return K.Armor;
            if ((wo.ItemType & ItemType.Clothing) != 0) return K.Clothing;
            return K.Other;
        }

        static MaterialType MaterialOf(uint m) => m switch
        {
            SignedLootRule.STEEL => MaterialType.Steel, SignedLootRule.IRON => MaterialType.Iron, SignedLootRule.GRANITE => MaterialType.Granite,
            SignedLootRule.MAHOGANY => MaterialType.Mahogany, SignedLootRule.BRASS => MaterialType.Brass,
            SignedLootRule.GREEN_GARNET => MaterialType.GreenGarnet, SignedLootRule.SUNSTONE => MaterialType.Sunstone,
            SignedLootRule.FIRE_OPAL => MaterialType.FireOpal,
            _ => MaterialType.BlackOpal,
        };

        /// <summary>One tinker exactly as RecipeManager applies it: the mutation script, then the TinkerLog entry.
        /// Also PvpKit's (the missile pack's launchers).</summary>
        internal static bool Tink(WorldObject wo, uint mutation)
        {
            var script = MutationCache.GetMutation(mutation);
            if (script == null) return false;
            int before = wo.NumTimesTinkered;
            script.TryMutate(wo);
            if (wo.NumTimesTinkered == before) return false;
            wo.TinkerLog = (wo.TinkerLog != null ? wo.TinkerLog + "," : "") + (uint)MaterialOf(mutation);
            return true;
        }

        // ---------------------------------------------------------------- apply
        static void Apply(Session session, Player p)
        {
            CheckSpells();
            var items = p.EquippedObjects.Values.Where(w => w != null).OrderBy(w => (uint)(w.CurrentWieldedLocation ?? 0)).ThenBy(w => w.Guid.Full).ToList();
            if (items.Count == 0) { Say(session, "[PK suit] You are wearing and wielding nothing."); return; }
            var kinds = items.Select((w, i) => (key: i, kind: KindOf(w))).ToList();
            var skills = p.Skills.Where(kv => kv.Value.AdvancementClass >= SkillAdvancementClass.Trained).Select(kv => (int)kv.Key).ToList();
            var plan = PkSuitRule.Plan(kinds, skills);

            int changed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var wo = items[i];
                var kind = kinds[i].kind;
                var before = Snapshot(wo);
                var spellsBefore = new HashSet<int>(wo.Biota.GetKnownSpellsIds(wo.BiotaDatabaseLock));
                var added = new List<uint>();

                foreach (var id in plan.Spells[i])
                {
                    if (s_Missing.Contains(id) || spellsBefore.Contains((int)id)) continue;
                    wo.Biota.GetOrAddKnownSpell((int)id, wo.BiotaDatabaseLock, out bool ok);
                    if (ok) added.Add(id);
                }

                var t = PkSuitRule.TinkerPlan(kind, wo.NumTimesTinkered, wo.ImbuedEffect != ImbuedEffectType.Undef, (wo.ElementalDamageMod ?? 0) > 0);
                int tinks = 0;
                if (t.Imbue != 0 && Tink(wo, t.Imbue))
                {
                    tinks++;
                    if (RecipeManager.IconUnderlay.TryGetValue(wo.ImbuedEffect, out var underlay)) wo.IconUnderlayId = underlay;
                }
                foreach (var m in t.Steps) if (Tink(wo, m)) tinks++;

                wo.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);

                bool hasSpells = wo.Biota.GetKnownSpellsIds(wo.BiotaDatabaseLock).Count > 0;
                if (hasSpells)
                {
                    if ((wo.ManaRate ?? 0) == 0) wo.ManaRate = PkSuitRule.DEFAULT_MANA_RATE;
                    int max = PkSuitRule.EveningMana(wo.ItemMaxMana ?? 0, -(wo.ManaRate ?? 0));
                    wo.ItemMaxMana = max;
                    wo.ItemCurMana = max;
                }

                // cast what is new, as equipping would (Player.TryActivateSpells)
                foreach (var id in added)
                    if (!wo.HasProcSpell(id) && id != (wo.SpellDID ?? 0)) p.CreateItemSpell(wo, id);
                if (added.Count > 0) wo.OnSpellsActivated();

                var diff = Diff(before, Snapshot(wo));
                if (diff.Count == 0 && added.Count == 0) continue;
                changed++;
                Record(wo, diff, added, p.Name);
                wo.ChangesDetected = true;
                wo.SaveBiotaToDatabase();

                var names = string.Join(", ", added.Select(s => PkSuitRule.NAMES[s].Replace("Legendary ", "")));
                var line = $"{wo.Name} ({kind}): {(added.Count > 0 ? "+" + names : "no new spells")}"
                         + $"{(tinks > 0 ? $"; {tinks} tinkers (now {wo.NumTimesTinkered})" : "")}"
                         + $"{(wo.ArmorLevel != null && kind is K.Armor or K.Shield ? $"; AL {wo.ArmorLevel}" : "")}"
                         + $"{(wo.ImbuedEffect != ImbuedEffectType.Undef ? $"; {wo.ImbuedEffect}" : "")}; Bonded";
                Say(session, "[PK suit] " + line);
                PlayerManager.BroadcastToAuditChannel(p, $"{p.Name} @pksuit 0x{wo.Guid.Full:X8} {line}");
            }
            if (plan.Unplaced.Count > 0)
                Say(session, $"[PK suit] No room on your worn jewelry/armour/clothing for: {string.Join(", ", plan.Unplaced.Select(s => PkSuitRule.NAMES[s]))}.");
            if (s_Missing.Count > 0)
                Say(session, $"[PK suit] Not in this server's spell table, skipped: {string.Join(", ", s_Missing.Select(s => $"{s} {PkSuitRule.NAMES[s]}"))}.");
            Say(session, $"[PK suit] {changed} of {items.Count} items changed. '@pksuit undo' puts them back.");
        }

        // ---------------------------------------------------------------- property snapshot, diff, undo records
        // Keys are "i:<PropertyInt>", "f:", "s:", "b:", "d:" (DataId), "l:" (Int64); a null value means "had none".
        static Dictionary<string, string> Snapshot(WorldObject wo)
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in wo.GetAllPropertyInt()) d["i:" + (ushort)kv.Key] = kv.Value.ToString(CultureInfo.InvariantCulture);
            foreach (var kv in wo.GetAllPropertyFloat()) d["f:" + (ushort)kv.Key] = kv.Value.ToString("R", CultureInfo.InvariantCulture);
            foreach (var kv in wo.GetAllPropertyString()) d["s:" + (ushort)kv.Key] = kv.Value;
            foreach (var kv in wo.GetAllPropertyBools()) d["b:" + (ushort)kv.Key] = kv.Value ? "1" : "0";
            foreach (var kv in wo.GetAllPropertyDataId()) d["d:" + (ushort)kv.Key] = kv.Value.ToString(CultureInfo.InvariantCulture);
            foreach (var kv in wo.GetAllPropertyInt64()) d["l:" + (ushort)kv.Key] = kv.Value.ToString(CultureInfo.InvariantCulture);
            return d;
        }

        /// <summary>The before value of every key that changed (null: it was absent).</summary>
        static Dictionary<string, string> Diff(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var d = new Dictionary<string, string>();
            foreach (var k in a.Keys.Union(b.Keys))
            {
                a.TryGetValue(k, out var va); b.TryGetValue(k, out var vb);
                if (va != vb) d[k] = va;
            }
            return d;
        }

        sealed class Rec { public uint Guid { get; set; } public string Name { get; set; } public string By { get; set; } public string At { get; set; }
            public List<uint> Spells { get; set; } = new List<uint>(); public Dictionary<string, string> Props { get; set; } = new Dictionary<string, string>(); }

        static string PathOf(uint guid) => System.IO.Path.Combine(s_Folder, $"{guid:X8}.json");

        static Rec Load(uint guid)
        {
            var path = PathOf(guid);
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<Rec>(File.ReadAllText(path)); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkSuit: unreadable {path}: {e.Message}"); return null; }
        }

        static void Record(WorldObject wo, Dictionary<string, string> diff, List<uint> added, string by)
        {
            var rec = Load(wo.Guid.Full) ?? new Rec { Guid = wo.Guid.Full, Name = wo.Name, By = by, At = DateTime.UtcNow.ToString("u") };
            foreach (var kv in diff) if (!rec.Props.ContainsKey(kv.Key)) rec.Props[kv.Key] = kv.Value;   // keep the first value
            foreach (var s in added) if (!rec.Spells.Contains(s)) rec.Spells.Add(s);
            var tmp = PathOf(wo.Guid.Full) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec));
            File.Move(tmp, PathOf(wo.Guid.Full), true);
        }

        static void Undo(Session session, Player p)
        {
            var worn = new HashSet<uint>(p.EquippedObjects.Keys.Select(g => g.Full));
            int n = 0;
            foreach (var wo in p.GetAllPossessions().Where(w => w != null))
            {
                var rec = Load(wo.Guid.Full);
                if (rec == null) continue;
                foreach (var id in rec.Spells)
                {
                    if (worn.Contains(wo.Guid.Full)) p.RemoveItemSpell(wo, id, silent: true);
                    wo.Biota.TryRemoveKnownSpell((int)id, wo.BiotaDatabaseLock);
                }
                foreach (var kv in rec.Props) Restore(wo, kv.Key, kv.Value);
                wo.ChangesDetected = true;
                wo.SaveBiotaToDatabase();
                File.Delete(PathOf(wo.Guid.Full));
                n++;
                var line = $"{wo.Name}: {rec.Spells.Count} spells removed, {rec.Props.Count} properties restored";
                Say(session, "[PK suit undo] " + line);
                PlayerManager.BroadcastToAuditChannel(p, $"{p.Name} @pksuit undo 0x{wo.Guid.Full:X8} {line}");
            }
            Say(session, n == 0 ? "[PK suit undo] Nothing you wear or carry has a PK suit record." : $"[PK suit undo] {n} items put back as they were.");
        }

        static void Restore(WorldObject wo, string key, string v)
        {
            var id = ushort.Parse(key.Substring(2), CultureInfo.InvariantCulture);
            switch (key[0])
            {
                case 'i': if (v == null) wo.RemoveProperty((PropertyInt)id); else wo.SetProperty((PropertyInt)id, int.Parse(v, CultureInfo.InvariantCulture)); break;
                case 'f': if (v == null) wo.RemoveProperty((PropertyFloat)id); else wo.SetProperty((PropertyFloat)id, double.Parse(v, CultureInfo.InvariantCulture)); break;
                case 's': if (v == null) wo.RemoveProperty((PropertyString)id); else wo.SetProperty((PropertyString)id, v); break;
                case 'b': if (v == null) wo.RemoveProperty((PropertyBool)id); else wo.SetProperty((PropertyBool)id, v == "1"); break;
                case 'd': if (v == null) wo.RemoveProperty((PropertyDataId)id); else wo.SetProperty((PropertyDataId)id, uint.Parse(v, CultureInfo.InvariantCulture)); break;
                case 'l': if (v == null) wo.RemoveProperty((PropertyInt64)id); else wo.SetProperty((PropertyInt64)id, long.Parse(v, CultureInfo.InvariantCulture)); break;
            }
        }
    }
}
