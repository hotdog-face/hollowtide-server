using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.DatLoader;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>The hollow company can fight.</summary>
    public static class MercKit
    {
        const string P_ON = "merc_kit";
        const string P_BLESS = "merc_kit_bless";
        const string P_LEVEL = "merc_kit_level";
        const long DEFAULT_LEVEL = 0;
        internal static int KitLevel
        {
            get { try { return (int)Math.Clamp(PropertyManager.GetLong(P_LEVEL, DEFAULT_LEVEL).Item, 0, 275); } catch { return (int)DEFAULT_LEVEL; } }
        }
        const int KITS = 6;
        const double REBUFF_MIN = 20;
        const uint KIT_HEALING = 629;   // Adept Healing Kit

        static readonly (EquipMask slot, uint wcid)[] ARMOR =
        {
            (EquipMask.ChestArmor, 71), (EquipMask.UpperLegArmor, 80), (EquipMask.HandWear, 55), (EquipMask.FootWear, 115),
        };

        // Azimar's blessing, the same spells he casts on any player who uses him.
        static readonly uint[] AZIMAR = {
            4290, 4296, 4298, 4304, 4318, 4324, 4328, 4459, 4461, 4463, 4465, 4467, 4469, 4471, 4493, 4495, 4505,
            4509, 4511, 4517, 4521, 4525, 4529, 4533, 4537, 4541, 4547, 4551, 4555, 4557, 4559, 4563, 4565, 4571,
            4577, 4581, 4585, 4589, 4591, 4595, 4601, 4603, 4607, 4613, 4615, 4619, 4623, 4627, 4630, 4637, 4639,
            5060, 5098, 5410, 5778, 5802, 5826, 5850, 5874,
            4497, 6115,
        };

        internal static bool On
        {
            get { try { return PropertyManager.GetBool(P_ON, true).Item; } catch { return true; } }
        }

        public static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard MercKit: Hollow Company level, kit and buffs");
                if (!b.ContainsKey(P_BLESS)) b[P_BLESS] = new Property<bool>(false, "RevivalGuard MercKit: mercenaries also get Azimar's blessing (default false: weapon, armor and kits only)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: switch not registered ({e.Message})"); }
            try
            {
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_LEVEL)) l[P_LEVEL] = new Property<long>(DEFAULT_LEVEL, "RevivalGuard MercKit: a merc below this level is raised to it and the grant spent; 0 = off (levels and skills as earned)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: level switch not registered ({e.Message})"); }
            Mod.Log.Info($"[RevivalGuard] MercKit: bonded weapon, chainmail and kits; Azimar's blessing {(Bless ? "on" : "off")}, every {REBUFF_MIN} min; level floor {KitLevel} (0 = off) (merc_kit)");
        }

        /// <summary>The weapon skill this merc fights with: its current weapon's, else Brannoc's Heavy, Sefa's Light.</summary>
        internal static Skill WeaponSkill(Player p)
        {
            var w = p.GetEquippedMeleeWeapon();
            if (w?.WeaponSkill is Skill s && s != Skill.None) return s;
            return (p.Name ?? "").StartsWith("Sefa", StringComparison.OrdinalIgnoreCase) ? Skill.LightWeapons : Skill.HeavyWeapons;
        }

        internal static uint WeaponFor(Skill s) => s == Skill.LightWeapons ? 338u : 351u;

        // ---- once: level and skills ----------------------------------------------------------------------
        /// <summary>Up to the merc_kit_level floor with ACE's GrantXP.</summary>
        internal static bool LevelUp(Player p)
        {
            int kit = KitLevel;
            if (kit <= 0 || (p.Level ?? 1) >= kit) return false;
            var list = DatManager.PortalDat.XpTable.CharacterLevelXPList;
            long need = (long)list[kit] - (p.TotalExperience ?? 0);
            if (need <= 0) return false;
            Mod.Log.Info($"[RevivalGuard] MercKit: {p.Name} level {p.Level} -> {kit} ({need:N0} xp)");
            p.GrantXP(need, XpType.Admin, ShareType.None);
            return true;
        }

        /// <summary>Unspent experience (more than SPEND_MIN): train the fighting skills from the credits, then spend it by share.</summary>
        const long SPEND_MIN = 100_000;
        internal static void Spend(Player p)
        {
            if ((p.AvailableExperience ?? 0) < SPEND_MIN) return;
            var weapon = WeaponSkill(p);
            foreach (var s in new[] { weapon, Skill.MeleeDefense, Skill.Healing, Skill.MagicDefense })
            {
                var cs = p.GetCreatureSkill(s);
                if (cs.AdvancementClass < SkillAdvancementClass.Trained && !p.TrainSkill(s))
                    Mod.Log.Warn($"[RevivalGuard] MercKit: {p.Name} could not train {s} ({p.AvailableSkillCredits} credits)");
            }
            if (p.GetCreatureSkill(weapon).AdvancementClass == SkillAdvancementClass.Trained)
                p.SpecializeSkill(weapon);     // false (and no change) when the credits are short

            long pool = p.AvailableExperience ?? 0;
            var shares = new (string what, double share, Func<long, bool> raise)[]
            {
                ("weapon", 0.30, a => RaiseSkill(p, weapon, a)),
                ("melee defense", 0.25, a => RaiseSkill(p, Skill.MeleeDefense, a)),
                ("strength", 0.07, a => RaiseAttr(p, PropertyAttribute.Strength, a)),
                ("endurance", 0.07, a => RaiseAttr(p, PropertyAttribute.Endurance, a)),
                ("coordination", 0.07, a => RaiseAttr(p, PropertyAttribute.Coordination, a)),
                ("quickness", 0.07, a => RaiseAttr(p, PropertyAttribute.Quickness, a)),
                ("healing", 0.05, a => RaiseSkill(p, Skill.Healing, a)),
                ("magic defense", 0.05, a => RaiseSkill(p, Skill.MagicDefense, a)),
                ("health", 0.05, a => RaiseVital(p, PropertyAttribute2nd.MaxHealth, a)),
            };
            foreach (var (what, share, raise) in shares)
                raise((long)(pool * share));
            Mod.Log.Info($"[RevivalGuard] MercKit: {p.Name} level {p.Level}; {weapon} {p.GetCreatureSkill(weapon).AdvancementClass} " +
                         $"{p.GetCreatureSkill(weapon).Current}, melee defense {p.GetCreatureSkill(Skill.MeleeDefense).Current}, " +
                         $"healing {p.GetCreatureSkill(Skill.Healing).Current}, health {p.Health.MaxValue}; {p.AvailableExperience} xp left");
        }

        // ACE refuses an amount past the max rank, so each raise is capped at what is left to the top.
        static bool RaiseSkill(Player p, Skill s, long amount)
        {
            var cs = p.GetCreatureSkill(s, false);
            if (cs == null || cs.AdvancementClass < SkillAdvancementClass.Trained || cs.IsMaxRank) return false;
            uint a = (uint)Math.Min(Math.Min(amount, p.AvailableExperience ?? 0), cs.ExperienceLeft);
            return a > 0 && p.HandleActionRaiseSkill(s, a);
        }

        static bool RaiseAttr(Player p, PropertyAttribute at, long amount)
        {
            if (!p.Attributes.TryGetValue(at, out var ca) || ca.IsMaxRank) return false;
            uint a = (uint)Math.Min(Math.Min(amount, p.AvailableExperience ?? 0), ca.ExperienceLeft);
            return a > 0 && p.HandleActionRaiseAttribute(at, a);
        }

        static bool RaiseVital(Player p, PropertyAttribute2nd v, long amount)
        {
            if (!p.Vitals.TryGetValue(v, out var cv) || cv.IsMaxRank) return false;
            uint a = (uint)Math.Min(Math.Min(amount, p.AvailableExperience ?? 0), cv.ExperienceLeft);
            return a > 0 && p.HandleActionRaiseVital(v, a);
        }

        // ---- kit ------------------------------------------------------------------------------------------
        static WorldObject Bonded(uint wcid)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) return null;
            wo.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            wo.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            wo.SetProperty(PropertyInt.Value, 0);
            return wo;
        }

        static bool Covered(Player p, EquipMask slot) => p.EquippedObjects.Values.Any(o => ((o.CurrentWieldedLocation ?? 0) & slot) != 0);

        internal static void Outfit(Player p)
        {
            var given = new List<string>();
            if (p.GetEquippedMeleeWeapon() == null && !Covered(p, EquipMask.MeleeWeapon | EquipMask.TwoHanded))
            {
                var w = Bonded(WeaponFor(WeaponSkill(p)));
                if (w != null && p.TryEquipObjectWithNetworking(w, EquipMask.MeleeWeapon)) given.Add(w.Name);
                else if (w != null) w.Destroy();
            }
            foreach (var (slot, wcid) in ARMOR)
            {
                if (Covered(p, slot)) continue;
                var a = Bonded(wcid);
                if (a == null) continue;
                var at = a.ValidLocations ?? slot;
                if (!Covered(p, at) && p.TryEquipObjectWithNetworking(a, at)) given.Add(a.Name);
                else a.Destroy();
            }
            int kits = p.GetNumInventoryItemsOfWCID(KIT_HEALING);
            if (kits < KITS)
            {
                for (int i = kits; i < KITS; i++)
                {
                    var k = Bonded(KIT_HEALING);
                    if (k == null || !p.TryCreateInInventoryWithNetworking(k)) { k?.Destroy(); break; }
                }
                given.Add($"{KITS - kits} healing kits");
            }
            if (given.Count > 0)
                Mod.Log.Info($"[RevivalGuard] MercKit: {p.Name} kitted: {string.Join(", ", given)}");
        }

        // ---- buffs ----------------------------------------------------------------------------------------
        internal static bool Bless
        {
            get { try { return PropertyManager.GetBool(P_BLESS, false).Item; } catch { return false; } }
        }

        internal static void Buff(Player p)
        {
            if (!Bless) return;
            // Azimar's creature and life spells, then his weapon auras at the level he gives a player of this level
            var ids = AZIMAR.ToList();
            int aura = AzimarWeaponBlessing.LevelFor(p);
            if (aura > 0) ids.AddRange(AzimarWeaponBlessing.Auras.Select(a => AzimarWeaponBlessing.SpellId(a.other1, aura)));
            var chain = new ActionChain();
            foreach (var id in ids)
            {
                chain.AddDelaySeconds(0.2);
                chain.AddAction(p, () =>
                {
                    try
                    {
                        if (PlayerManager.GetOnlinePlayer(p.Guid) == null || p.IsDead) return;
                        var spell = new Spell(id);
                        if (spell.NotFound) { Mod.Log.Warn($"[RevivalGuard] MercKit: spell {id} not found"); return; }
                        p.TryCastSpell_WithRedirects(spell, p, p, tryResist: false);
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: buff {id} on {p.Name}: {e.Message}"); }
                });
            }
            chain.EnqueueChain();
        }

        /// <summary>Level (once), kit, buffs: in that order, a moment apart.</summary>
        internal static void Ready(Player p, string why)
        {
            // the Borg earn their gear: no merc kit for an "... of Borg" character (BorgKit dresses them)
            if (!On || p == null || !Mercenaries.IsMerc(p) || BorgSwitch.IsBorg(p)) return;
            bool leveled = false;
            var chain = new ActionChain();
            chain.AddDelaySeconds(why == "login" ? 8.0 : 12.0);   // a lifestone return: past the portal space
            chain.AddAction(p, () =>
            {
                try
                {
                    if (PlayerManager.GetOnlinePlayer(p.Guid) == null || p.IsDead) return;
                    leveled = LevelUp(p);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: {p.Name} level ({why}): {e}"); }
            });
            chain.AddDelaySeconds(2.0);           // the queued grant has landed
            chain.AddAction(p, () =>
            {
                try
                {
                    if (PlayerManager.GetOnlinePlayer(p.Guid) == null || p.IsDead) return;
                    if (leveled) Spend(p);
                    Outfit(p);
                    Buff(p);
                    Mod.Log.Info($"[RevivalGuard] MercKit: {p.Name} ready ({why}), level {p.Level}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: {p.Name} ({why}): {e}"); }
            });
            chain.EnqueueChain();
        }

        /// <summary>Every REBUFF minutes while this login lasts: buffs again (the kit too, if something went missing).</summary>
        internal static void Loop(Player p, int session)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(REBUFF_MIN * 60);
            chain.AddAction(p, () =>
            {
                bool newer; lock (Sessions) newer = Sessions.TryGetValue(p.Guid.Full, out var s) && s != session;   // a login writes it under this lock
                if (PlayerManager.GetOnlinePlayer(p.Guid) != p || newer) return;
                if (On && !p.IsDead) { try { Outfit(p); Buff(p); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: rebuff {p.Name}: {e.Message}"); } }
                Loop(p, session);
            });
            chain.EnqueueChain();
        }

        internal static readonly Dictionary<uint, int> Sessions = new Dictionary<uint, int>();
        static int s_Next;

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnLogin
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p == null || !Mercenaries.IsMerc(p) || BorgSwitch.IsBorg(p)) return;
                    int n;
                    lock (Sessions) { n = ++s_Next; Sessions[p.Guid.Full] = n; }
                    Ready(p, "login");
                    Loop(p, n);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: login: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ThreadSafeTeleportOnDeath))]
        static class OnLifestone
        {
            static void Postfix(Player __instance)
            {
                try { if (__instance != null && Mercenaries.IsMerc(__instance)) Ready(__instance, "lifestone"); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MercKit: lifestone: {e.Message}"); }
            }
        }
    }
}
