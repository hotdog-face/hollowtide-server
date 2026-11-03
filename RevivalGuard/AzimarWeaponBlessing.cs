using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// AZIMAR'S WEAPON BLESSINGS (2026-11-02; Discord request "can buffbot also buff weapons? heart drinker, swift
    /// killer, etc."; owner 2026-11-02 "yes i agree with your recommendation": on, a level below an Item Magic
    /// specialist, shorter than a self-cast, all tunable). NOT RETAIL: retail's Pumpkin Buffer (32205), whose spells
    /// Azimar's creature and life blessing copies, cast 59 "Incantation of ... Other" creature/life spells and no item
    /// spells.
    /// His emote table (gen_buff_zefir.py) has an inert step tagged "AzimarWeapon" after his creature and life spells
    /// (EmoteType 0: ACE does nothing with it, so without this rule nothing happens). Here it casts the six weapon auras
    /// ("Aura of ... Other": Blood Drinker, Heart Seeker, Swift Killer, Defender, Spirit Drinker, Hermetic Link) on the
    /// player. ACE keeps them on the PLAYER and adds them to whatever enchantable weapon or wand is wielded
    /// (WorldObject_Weapon, BaseDamageMod), so one set fits a sword, a bow and a wand, and a swapped weapon has them.
    /// A lower level never surpasses a stronger one already on the player (a mage's own buffs win). Then the tell
    /// "AzimarLines:weapon" (AzimarVoice) says nothing with a weapon in hand, else weapon.none / weapon.proof.
    ///   @modifybool azimar_weapon_buffs false      off (no auras, no tell)
    ///   @modifylong azimar_weapon_level N          a fixed spell level 1-8 for everyone; 0 = by character level (TIERS)
    ///   @modifystring azimar_weapon_tiers "1:1,..." character level : the level an Item Magic specialist casts there
    ///   @modifylong azimar_weapon_duration_s N     seconds each aura lasts (never longer than the spell's own)
    ///   @modifylong azimar_weapon_max_level N      characters above level N get none; 0 = no cap
    /// </summary>
    static class AzimarWeaponBlessing
    {
        internal const string TAG = "AzimarWeapon";
        internal const string P_ON = "azimar_weapon_buffs", P_LEVEL = "azimar_weapon_level", P_TIERS = "azimar_weapon_tiers",
            P_DURATION = "azimar_weapon_duration_s", P_MAX = "azimar_weapon_max_level";
        // The level an Item Magic specialist casts from each character level (an estimate for the owner to tune: the
        // character level a specialised mage usually reaches each spell level's difficulty); Azimar casts one below.
        internal const string DEFAULT_TIERS = "1:1,6:2,12:3,20:4,30:5,40:6,60:7,100:8";
        internal const long DEFAULT_DURATION = 1800;   // 30 min (a self-cast aura runs its spell's own, 1 h or more)
        const float GAP = 0.2f;                        // his paced casts' own gap (gen_buff_zefir.py CAST_GAP)

        /// <summary>Each aura's "Other I" id; levels I-VII are the next six ids and the Incantation (VIII) the one after
        /// (ace_world.spell, read 2026-11-02: e.g. Blood Drinker Other I 5990 ... VII 5996, Incantation 5997).</summary>
        internal static readonly (string name, uint other1)[] Auras =
        {
            ("Blood Drinker", 5990), ("Heart Seeker", 6007), ("Swift Killer", 6024),
            ("Defender", 5999), ("Spirit Drinker", 6015), ("Hermetic Link", 5982),
        };

        internal static uint SpellId(uint other1, int level) => other1 + (uint)(Math.Clamp(level, 1, 8) - 1);

        /// <summary>The spell level a specialist casts at this character level, from a "charLevel:spellLevel,..." table.</summary>
        internal static int SpecialistLevel(string tiers, int charLevel)
        {
            int best = 1;
            foreach (var part in (tiers ?? DEFAULT_TIERS).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out var at) && int.TryParse(kv[1].Trim(), out var lv) && charLevel >= at)
                    best = Math.Max(best, lv);
            }
            return Math.Clamp(best, 1, 8);
        }

        /// <summary>The level Azimar casts for this character, or 0 for none (switch off, over the cap).</summary>
        internal static int LevelFor(bool on, long fixedLevel, string tiers, long maxCharLevel, int charLevel)
        {
            if (!on || (maxCharLevel > 0 && charLevel > maxCharLevel)) return 0;
            if (fixedLevel > 0) return (int)Math.Clamp(fixedLevel, 1, 8);
            return Math.Max(1, SpecialistLevel(tiers, charLevel) - 1);
        }

        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;

        internal static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard AzimarWeaponBlessing: Azimar also casts the weapon auras");
                var l = MonsterAi.Inner(DefaultPropertyManager.DefaultLongProperties);
                if (!l.ContainsKey(P_LEVEL)) l[P_LEVEL] = new Property<long>(0, "RevivalGuard AzimarWeaponBlessing: fixed aura level 1-8; 0 = one below a specialist at the character's level");
                if (!l.ContainsKey(P_DURATION)) l[P_DURATION] = new Property<long>(DEFAULT_DURATION, "RevivalGuard AzimarWeaponBlessing: seconds each aura lasts (capped at the spell's own)");
                if (!l.ContainsKey(P_MAX)) l[P_MAX] = new Property<long>(0, "RevivalGuard AzimarWeaponBlessing: no auras above this character level; 0 = no cap");
                var s = MonsterAi.Inner(DefaultPropertyManager.DefaultStringProperties);
                if (!s.ContainsKey(P_TIERS)) s[P_TIERS] = new Property<string>(DEFAULT_TIERS, "RevivalGuard AzimarWeaponBlessing: charLevel:spellLevel an Item Magic specialist casts");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarWeaponBlessing: switches not registered ({e.Message}); still read at their defaults"); }
            Mod.Log.Info("[RevivalGuard] AzimarWeaponBlessing: Azimar casts weapon auras a level below a specialist (azimar_weapon_buffs)");
        }

        /// <summary>The level for this player now, from the switches.</summary>
        internal static int LevelFor(Player p)
        {
            long lvl = 0, max = 0; string tiers = DEFAULT_TIERS;
            try { lvl = PropertyManager.GetLong(P_LEVEL, 0).Item; max = PropertyManager.GetLong(P_MAX, 0).Item; tiers = PropertyManager.GetString(P_TIERS, DEFAULT_TIERS).Item; } catch { }
            return LevelFor(On, lvl, tiers, max, p.Level ?? 1);
        }

        /// <summary>The emote step: cast the six auras on the player, paced, each trimmed to the duration switch.
        /// Returns the seconds the casts take (the next step waits for them), or -1 when none are cast.</summary>
        internal static float Bless(WorldObject azimar, Player p)
        {
            int level = LevelFor(p);
            if (level <= 0) return -1;
            long dur = DEFAULT_DURATION;
            try { dur = PropertyManager.GetLong(P_DURATION, DEFAULT_DURATION).Item; } catch { }
            var chain = new ActionChain();
            foreach (var (name, other1) in Auras)
            {
                var id = SpellId(other1, level);
                chain.AddDelaySeconds(GAP);
                chain.AddAction(azimar, () =>
                {
                    try
                    {
                        if (PlayerManager.GetOnlinePlayer(p.Guid) == null) return;
                        var spell = new Spell(id);
                        if (spell.NotFound) { Mod.Log.Warn($"[RevivalGuard] AzimarWeaponBlessing: spell {id} ({name} {level}) not found"); return; }
                        azimar.TryCastSpell_WithRedirects(spell, p, azimar);
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarWeaponBlessing: {name}: {e.Message}"); }
                });
            }
            // then each of his auras that took is cut to the duration switch
            chain.AddDelaySeconds(GAP);
            chain.AddAction(azimar, () =>
            {
                try
                {
                    var report = Auras.Select(a => Trim(p, azimar, SpellId(a.other1, level), dur));
                    Mod.Log.Info($"[RevivalGuard] AzimarWeaponBlessing: {p.Name} (level {p.Level}) level {level} auras, {dur} s: {string.Join(", ", report)}");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AzimarWeaponBlessing trim: {e.Message}"); }
            });
            chain.EnqueueChain();
            return GAP * (Auras.Length + 2);
        }

        /// <summary>His aura on the player (if it took: a stronger one already there is left alone) lasts dur seconds at
        /// most from now.</summary>
        static string Trim(Player p, WorldObject azimar, uint spellId, long dur)
        {
            var e = p.EnchantmentManager.GetEnchantment(spellId, azimar.Guid.Full);
            if (e == null) return $"{spellId} not on (a stronger one holds)";
            // time left = Duration + StartTime (ACE's StartTime runs down from 0 as the enchantment ages)
            var left = e.Duration + e.StartTime;
            if (dur <= 0 || e.Duration <= 0 || left <= dur) return $"{spellId} {left:0} s";
            e.Duration = dur - e.StartTime;
            p.ChangesDetected = true;
            p.Session?.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(p.Session, new ACE.Server.Network.Structure.Enchantment(p, e)));
            return $"{spellId} {left:0} to {dur} s";
        }
    }
}
