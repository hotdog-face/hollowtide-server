using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>
    /// Our own spells, not retail's. Off by default (server property rg_experimental_spells); their world rows, the six
    /// player scrolls and the switch are the three spells-golive datafixes in tools/public-shard/datafixes. Off, nothing
    /// is injected and every patch returns at once. docs/content-review/experimental-spells/README.md has the numbers.
    ///
    /// Built as Nether Rift is (NetherRift.cs): a SpellBase cloned from a retail spell goes into the loaded table under
    /// ids 60101-60108, ACE's row comes from the SQL, and the id never reaches a retail client (learning stamps quest
    /// flag RevivalExp&lt;id&gt; instead of the spellbook; only an opted-in session is sent MagicUpdateSpell; no favourite).
    ///   player-facing  60101 Nether Protection Self (data only), 60102 Gossamer Tide (Imperil V that spreads),
    ///                  60103 Shared Blood (fellowship heal paid in the caster's health), 60104 Nullify Void Magic Self
    ///                  (data only), 60105 Fellowship Recall (to the leader, open sky only)
    ///   STAFF ONLY     60106 Tempest of the Unseen, 60107 Call to Gather, 60108 Eclipse: refused for any caster below
    ///                  Sentinel (never learnable, never known, and the cast itself is refused).
    ///                  60109-60117, staff only too (ExperimentalStaffSpells.cs, owner 2026-11-27 "more staff only spells"):
    ///                  Banishing Gale, Unmaking, Raven's Reckoning, Skyward Send-Off, Starfall, Grand Finale, Visitation,
    ///                  Fowl Weather, Bovine Downpour.
    ///                  60118-60128, round 4 (ExperimentalRound4.cs): ten more staff spells and Void Chains for players.
    ///   @expspells players on|off   Banishing Gale also knocks players back and Skyward Send-Off's bolt throws them (per caster, default off)
    ///   @expspells            status (admin)        @expspells inject   add them now, without a restart
    ///   @expspells teach      learn all of them     @expspells json     the client's spell.json rows and retail numbers
    ///   @expspells curse      Destructive Curse V on yourself (to try Nullify Void Magic)
    /// </summary>
    static partial class ExperimentalSpells
    {
        internal const string P_ON = "rg_experimental_spells";
        internal const uint FIRST = 60101, LAST = 60128;
        const uint NETHER_WARD = 60101, TIDE = 60102, SHARED_BLOOD = 60103, NULLIFY_VOID = 60104, FELLOW_RECALL = 60105,
                   TEMPEST = 60106, GATHER = 60107, ECLIPSE = 60108;
        const float TIDE_RADIUS = 5f; const int TIDE_EXTRA = 4;
        const float BLOOD_RADIUS = 15f; const int BLOOD_MIN = 40, BLOOD_MAX = 75, BLOOD_COST = 25, BLOOD_FELLOWS = 8;
        const float SHOW_RANGE = 96f; const double ECLIPSE_S = 120;

        sealed class Def
        {
            public uint Id, Template, Power, Mana, Order; public string Name = "", Desc = ""; public bool Staff;
            public double? Duration; public uint? Category, TargetEffect; public MagicSchool? School;
            public uint Gesture;   // a retail spell whose formula (so its windup and cast gestures) this one takes; 0 = the template's
            public float Range;    // metres, whatever the caster's skill; 0 = the template's (Heal Other I reaches about 5 m: "Out of Range!" at 7)
            public uint Talisman;  // a talisman component that replaces the formula's last one: the cast gesture alone (59 Hazel = MagicPortal)
            public uint Icon;      // our own icon (expansion/textures/0x06FF06xx.png, tools/img/exp_spell_icons.py); 0 = the template's
        }

        // Template = the retail spell whose school, target, range, flags, components and effects the new one keeps.
        // Gesture (owner 11-27: "bad character animation"): Heal Self I is a lead-scarab spell, so it has no windup at all,
        // only a small hand to the heart. The staff spells take a retail level VII/VIII formula instead, for its windup and
        // its talisman's cast gesture: 3818 Curse of Raven Fury (its own), 3807 Force Ring, 2120 MagicBlast (two-handed),
        // 2121 MagicRecoilMissile (a hurl), 1834 Firestorm, 2821 retail's firework rings (MagicVision), 2053 MagicPray
        // (arms raised), 2052 MagicClap, 2366 Bovine Intervention. Staff never need or burn the components (below).
        static readonly Def[] s_Defs =
        {
            new Def { Id = NETHER_WARD, Template = 2157, Name = "Nether Protection Self", Power = 300, Mana = 70, Order = 9101, Duration = 900,
                Category = 60101, TargetEffect = (uint)PlayScript.ShieldUpPurple,
                Desc = "Reduces damage the caster takes from Nether by 25%." },
            new Def { Id = TIDE, Template = 1326, Name = "Gossamer Tide", Power = 225, Mana = 120, Order = 9102, Duration = 60,
                Desc = "Decreases the target's natural armor by 150 points, and that of up to four other monsters within 5 meters of it. Lasts one minute." },
            new Def { Id = SHARED_BLOOD, Template = 2073, Name = "Shared Blood", Power = 300, Mana = 80, Order = 9103,
                Desc = "Restores 40-75 points of Health to each wounded fellowship member within 15 meters. The caster loses 25 points of Health for each fellow healed." },
            new Def { Id = NULLIFY_VOID, Template = 1990, Name = "Nullify Void Magic Self", Power = 300, Mana = 120, Order = 9104, School = MagicSchool.VoidMagic,
                Desc = "Dispels 1-3 negative Void Magic enchantments of level 7 or lower from the caster." },
            new Def { Id = FELLOW_RECALL, Template = 1635, Name = "Fellowship Recall", Power = 300, Mana = 150, Order = 9105,
                Desc = "Transports the caster to the leader of their fellowship. Both must stand under open sky, and neither may have fought another player recently." },
            new Def { Id = TEMPEST, Template = 6, Name = "Tempest of the Unseen", Power = 1, Mana = 1, Order = 9106, Staff = true, Icon = 0x06FF0606, Gesture = 2053, Talisman = 59,
                Desc = "Staff only. A portal storm breaks over the caster for half a minute: a ring of portals opens around them one by one, with thunder for everyone nearby, and fades away. It harms no one." },
            new Def { Id = GATHER, Template = 6, Name = "Call to Gather", Power = 1, Mana = 1, Order = 9107, Staff = true, Icon = 0x06FF0607,
                Desc = "Staff only. Brings every player within 96 meters to a ring around the caster." },
            new Def { Id = ECLIPSE, Template = 6, Name = "Eclipse", Power = 1, Mana = 1, Order = 9108, Staff = true, Icon = 0x06FF0608,
                Desc = "Staff only. Darkens the sky for everyone within 96 meters for two minutes. Cast again to lift it." },
            new Def { Id = GALE, Template = 6, Name = "Banishing Gale", Power = 1, Mana = 1, Order = 9109, Staff = true, Icon = 0x06FF0609, Gesture = 3807,
                Desc = "Staff only. A shockwave bursts from the caster and hurls every monster within 20 meters high into the air and far away. It does no damage." },
            new Def { Id = UNMAKING, Template = 5, Range = 60f, Name = "Unmaking", Power = 1, Mana = 1, Order = 9110, Staff = true, Icon = 0x06FF0610, Gesture = 2120,
                Desc = "Staff only. Lightning and fire strike the target down in an instant, the shadow takes it, and it dies." },
            new Def { Id = RAVEN, Template = 6, Name = "Raven's Reckoning", Power = 1, Mana = 1, Order = 9111, Staff = true, Icon = 0x06FF0611, Gesture = 3818,
                Desc = "Staff only. The ring of Raven Fury, but every monster a bolt touches dies. Players are never harmed." },
            new Def { Id = SENDOFF, Template = 5, Range = 60f, Name = "Skyward Send-Off", Power = 1, Mana = 1, Order = 9112, Staff = true, Icon = 0x06FF0612, Gesture = 2121,
                Desc = "Staff only. Hurls a slow bolt at the target that can be dodged. A monster it hits dies and its body is thrown far along the bolt's path, high and spinning. A player it hits is only thrown, never harmed. A miss does nothing." },
            new Def { Id = STARFALL, Template = 6, Name = "Starfall", Power = 1, Mana = 1, Order = 9113, Staff = true, Icon = 0x06FF0613, Gesture = 1834,
                Desc = "Staff only. Bright stars rain around the caster for fifteen seconds. They harm no one." },
            new Def { Id = FINALE, Template = 6, Name = "Grand Finale", Power = 1, Mana = 1, Order = 9114, Staff = true, Icon = 0x06FF0614, Gesture = 2821,
                Desc = "Staff only. Fifteen seconds of fireworks in every color rise from around the caster. They harm no one." },
            new Def { Id = VISITATION, Template = 6, Name = "Visitation", Power = 1, Mana = 1, Order = 9115, Staff = true, Icon = 0x06FF0615, Gesture = 2053,
                Desc = "Staff only. A towering image of Asheron arrives in light before the caster, speaks and casts, and departs after 45 seconds. Cast again for Bael'Zharon. Open sky only." },
            new Def { Id = FOWL, Template = 6, Name = "Fowl Weather", Power = 1, Mana = 1, Order = 9116, Staff = true, Icon = 0x06FF0616, Gesture = 2052,
                Desc = "Staff only. A flock of chickens, one of them enormous, follows the caster for a minute and attacks whatever the caster has selected. They never harm the caster." },
            new Def { Id = COWS, Template = 6, Name = "Bovine Downpour", Power = 1, Mana = 1, Order = 9117, Staff = true, Icon = 0x06FF0617, Gesture = 2366,
                Desc = "Staff only. Mooing cows fall from the sky around the caster for twelve seconds. They harm no one." },
            // Round 4 (owner's picks, ROUND4-PICKS.md; ExperimentalRound4.cs). All ten staff ones are cast on yourself.
            // Round 5: seven of the icons are retail's own DAT icons of the thing (an Olthoi 0x060010E7, a bottle of water 0x06001447,
            // the Dragon of Power mask 0x06006530, Big Chief Hagra's head 0x06001E37, a penguin 0x060036F6, the Black Skull of
            // Xikma 0x060064F6, a golden chest 0x06001FF8): the owner turned our drawn ones down.
            new Def { Id = ERUPTION, Template = 6, Name = "Olthoi Eruption", Power = 1, Mana = 1, Order = 9118, Staff = true, Icon = 0x060010E7, Gesture = 2120,
                Desc = "Staff only. The ground cracks open and a ring of Olthoi bursts up around the caster and tears into the monsters nearby for 45 seconds. Open sky only." },
            new Def { Id = STORM, Template = 6, Name = "Lightning Storm", Power = 1, Mana = 1, Order = 9119, Staff = true, Icon = 0x06FF0619, Gesture = 2053,
                Desc = "Staff only. Lightning strikes all around the caster for twenty seconds. Monsters it hits are thrown through the air, unharmed. Open sky only." },
            new Def { Id = WAVE, Template = 6, Name = "Tidal Wave", Power = 1, Mana = 1, Order = 9120, Staff = true, Icon = 0x06001447, Gesture = 3807,
                Desc = "Staff only. A wall of water sweeps out from the caster and knocks over every monster within 36 meters. It does no damage. Open sky only." },
            new Def { Id = FLYOVER, Template = 6, Name = "Dragon Flyover", Power = 1, Mana = 1, Order = 9121, Staff = true, Icon = 0x06006530, Gesture = 2366,
                Desc = "Staff only. A giant winged Mukkir, the nearest thing to a dragon in Dereth's skies, swoops low over the caster, roars and breathes fire: monsters within 16 meters burn. Players are never harmed. Open sky only." },
            new Def { Id = BIGHEAD, Template = 6, Name = "Big Head Mode", Power = 1, Mana = 1, Order = 9122, Staff = true, Icon = 0x06001E37, Gesture = 2052,
                Desc = "Staff only. Every player within 40 meters has a giant head for one minute. It is only a look." },
            new Def { Id = MOON, Template = 6, Name = "Moon Gravity", Power = 1, Mana = 1, Order = 9123, Staff = true, Icon = 0x06FF0623, Gesture = 2053,
                Desc = "Staff only. Every player within 40 meters jumps five times as high and falls slowly for one minute. Nobody is hurt by the landing." },
            new Def { Id = GUISE, Template = 6, Name = "Penguin Parade", Power = 1, Mana = 1, Order = 9124, Staff = true, Icon = 0x060036F6, Gesture = 2821,
                Desc = "Staff only. Every player within 40 meters is a penguin for one minute. It is only a look." },
            new Def { Id = ARENA, Template = 6, Name = "Arena Wall", Power = 1, Mana = 1, Order = 9125, Staff = true, Icon = 0x06FF0625, Gesture = 3807,
                Desc = "Staff only. A glowing ring of walls 15 meters around the caster that no player or monster can pass for one minute. Cast again to lower it. Open sky only." },
            new Def { Id = BOSS, Template = 6, Name = "Boss Summon", Power = 1, Mana = 1, Order = 9126, Staff = true, Icon = 0x060064F6, Gesture = 3818,
                Desc = "Staff only. The sky tears open and one of the event bosses, chosen at random, steps out in front of the caster. It is an enemy: it fights the caster and everyone near, and stays until it is killed. Open sky only." },
            new Def { Id = TREASURE, Template = 6, Name = "Treasure Rain", Power = 1, Mana = 1, Order = 9127, Staff = true, Icon = 0x06001FF8, Gesture = 2821,
                Desc = "Staff only. Gold rains down around the caster and treasure bags land on the ground. Each player may open one bag for a small prize." },
            // player-facing: Slowness Other VI's row and target rules, Brittle Bones' level VII formula, Void Magic
            new Def { Id = CHAINS, Template = 1420, Name = "Void Chains", Power = 300, Mana = 80, Order = 9128, Duration = 6, Category = 60128, School = MagicSchool.VoidMagic,
                Icon = 0x06FF0628, Gesture = 2068, Range = 30f,
                Desc = "Chains of the void hold a monster where it stands for 6 seconds. It can still attack what is in reach. Bosses cannot be chained, and a monster that breaks free cannot be chained again for 20 seconds." },
        };

        internal static bool HookDats, HookCast, HookLearn, HookKnown, HookFavorite;
        static readonly object s_Lock = new object();
        static bool s_Injected;
        [ThreadStatic] static bool t_Spreading;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (int token, List<Session> seen)> s_Eclipse = new();
        static int s_EclipseToken;

        internal static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static bool Live => s_Injected && On;
        internal static bool Loaded => Live;
        internal static bool Ours(uint id) => id >= FIRST && id <= LAST;
        static Def? DefOf(uint id) { foreach (var d in s_Defs) if (d.Id == id) return d; return null; }
        static string Stamp(uint id) => "RevivalExp" + id;
        static bool Staff(Player? p) => p?.Session != null && p.Session.AccessLevel >= AccessLevel.Sentinel;
        static bool Allowed(Player p, uint id) => DefOf(id) is Def d && (!d.Staff || Staff(p));
        static void Say(Player p, string text) => p.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Magic));

        internal static void Register()
        {
            ExpansionMotions.RegisterProperty(P_ON, false, "RevivalGuard ExperimentalSpells: spells 60101-60128 (docs/content-review/experimental-spells); needs the spells-golive datafixes");
            CommandManager.TryAddCommand(Handle, "expspells", AccessLevel.Admin, CommandHandlerFlag.None,
                "Experimental spells (RevivalGuard, off by default): status; inject; teach; json; curse; players on|off.", "[inject|teach|json|curse|players on|off]");
            Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {(On ? "ON" : "off")} ({P_ON}); hooks dats {Y(HookDats)} cast {Y(HookCast)} learn {Y(HookLearn)} known {Y(HookKnown)} favourite {Y(HookFavorite)} collide {Y(HookCollide)} drop {Y(HookDrop)} scroll {Y(ExperimentalScrolls.HookNew && ExperimentalScrolls.HookLoad)} root {Y(HookRoot)} bag {Y(HookBag)} wall {Y(HookWall)}");
        }

        static string Y(bool b) => b ? "ok" : "MISSING";

        static void Handle(Session session, params string[] parameters)
        {
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            var p = session?.Player;
            var sb = new StringBuilder();
            if (verb == "inject") sb.Append(Inject() ? "[ExpSpells] spells are in the loaded spell table." : $"[ExpSpells] not added ({P_ON} off, or see ACE_Log.txt).");
            else if (verb == "teach")
            {
                if (p == null) sb.Append("[ExpSpells] teach: in game only.");
                else if (!Live) sb.Append($"[ExpSpells] teach: {P_ON} is off or the spells are not loaded.");
                else { foreach (var d in s_Defs) p.LearnSpellWithNetworking(d.Id, false); WarmImages(p); sb.Append($"[ExpSpells] taught {FIRST}-{LAST} (staff spells only to staff)."); }
            }
            else if (verb == "curse")
            {
                if (p == null || !Live) sb.Append("[ExpSpells] curse: in game, with the spells on.");
                else { p.TryCastSpell(new Spell(5343), p, tryResist: false); sb.Append("[ExpSpells] Destructive Curse V is on you."); }
            }
            else if (verb == "players")
            {
                bool on = parameters.Length > 1 && parameters[1].ToLowerInvariant() == "on";
                if (p == null) sb.Append("[ExpSpells] players: in game only.");
                else
                {
                    if (on) s_GalePlayers[p.Guid.Full] = 1; else s_GalePlayers.TryRemove(p.Guid.Full, out _);
                    sb.Append(on ? "[ExpSpells] Players are fair game for you now: Banishing Gale throws them, Skyward Send-Off's bolt throws one it hits (unharmed), and Fowl Weather's chickens attack one you select."
                                 : "[ExpSpells] Banishing Gale, Skyward Send-Off and Fowl Weather leave players alone again.");
                }
            }
            else if (verb == "json") { foreach (var d in s_Defs) sb.Append(Json(d)).Append('\n'); }
            else
            {
                sb.Append($"[ExpSpells] {P_ON} {(On ? "ON" : "off")}; {(s_Injected ? "in the table" : "not loaded")}.");
                foreach (var d in s_Defs)
                    sb.Append($" {d.Id} {d.Name}: row {(DatabaseManager.World.GetCachedSpell(d.Id) != null ? "ok" : "MISSING")}{(s_Injected && new Spell(d.Id).NotFound ? " NOTFOUND" : "")};");
            }
            if (session?.Network != null) session.Network.EnqueueSend(new GameMessageSystemChat(sb.ToString(), ChatMessageType.Broadcast));
            else Mod.Log.Info(sb.ToString());
        }

        /// <summary>One line per spell: the client's expansion/spells/&lt;name&gt;/spell.json row, as the loaded table has it,
        /// with the template's own mana, power and duration beside it ("retail") for the README's comparison.</summary>
        static string Json(Def d)
        {
            var table = DatManager.PortalDat.SpellTable.Spells;
            if (!table.TryGetValue(d.Id, out var b)) return "{\"id\":" + d.Id + ",\"error\":\"not loaded\"}";
            table.TryGetValue(d.Template, out var t);
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            string E(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "{\"id\":" + d.Id + ",\"name\":\"" + E(b.Name) + "\",\"description\":\"" + E(b.Desc) + "\",\"school\":" + (int)b.School
                + ",\"icon\":\"0x" + b.Icon.ToString("X8") + "\",\"category\":" + (uint)b.Category + ",\"bitfield\":" + b.Bitfield + ",\"baseMana\":" + b.BaseMana
                + ",\"manaMod\":" + b.ManaMod + ",\"baseRange\":" + F(b.BaseRangeConstant) + ",\"rangeMod\":" + F(b.BaseRangeMod) + ",\"power\":" + b.Power
                + ",\"metaSpellType\":" + (int)b.MetaSpellType + ",\"duration\":" + F(b.Duration) + ",\"formula\":[" + string.Join(",", b.Formula) + "]"
                + ",\"words\":\"" + E(b.GetSpellWords(DatManager.PortalDat.SpellComponentsTable)) + "\",\"displayOrder\":" + b.DisplayOrder
                + ",\"nonComponentTarget\":" + b.NonComponentTargetType + ",\"staffOnly\":" + (d.Staff ? "true" : "false")
                + ",\"retail\":{\"id\":" + d.Template + ",\"name\":\"" + E(t?.Name ?? "") + "\",\"mana\":" + (t?.BaseMana ?? 0) + ",\"power\":" + (t?.Power ?? 0)
                + ",\"duration\":" + F(t?.Duration ?? 0) + "}}";
        }

        static bool Inject()
        {
            lock (s_Lock)
            {
                if (s_Injected) return true;
                if (!On) return false;
                try
                {
                    var table = DatManager.PortalDat.SpellTable.Spells;
                    foreach (var d in s_Defs)
                    {
                        if (table.ContainsKey(d.Id)) continue;
                        if (!table.TryGetValue(d.Template, out var src)) { Mod.Log.Error($"[RevivalGuard] ExperimentalSpells: template {d.Template} missing for {d.Id}"); return false; }
                        var sb = (SpellBase)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(src, null)!;
                        var t = Traverse.Create(sb);
                        t.Property("Name").SetValue(d.Name);
                        t.Property("Desc").SetValue(d.Desc);
                        t.Property("MetaSpellId").SetValue(d.Id);
                        t.Property("Power").SetValue(d.Power);
                        t.Property("BaseMana").SetValue(d.Mana);
                        t.Property("DisplayOrder").SetValue(d.Order);
                        // its own list, not the template's; a staff spell always takes Heal Self I's components
                        var from = d.Gesture != 0 && table.TryGetValue(d.Gesture, out var g) ? g : d.Staff && table.TryGetValue(6, out var heal) ? heal : src;
                        var formula = new List<uint>(from.Formula);
                        if (d.Talisman != 0 && formula.Count > 0) formula[formula.Count - 1] = d.Talisman;
                        t.Property("Formula").SetValue(formula);
                        if (d.Icon != 0) t.Property("Icon").SetValue(d.Icon);
                        if (d.Range > 0f) { t.Property("BaseRangeConstant").SetValue(d.Range); t.Property("BaseRangeMod").SetValue(0f); }
                        if (d.Duration.HasValue) t.Property("Duration").SetValue(d.Duration.Value);
                        if (d.Category.HasValue) t.Property("Category").SetValue((SpellCategory)d.Category.Value);
                        if (d.TargetEffect.HasValue) t.Property("TargetEffect").SetValue(d.TargetEffect.Value);
                        if (d.School.HasValue) t.Property("School").SetValue(d.School.Value);
                        table[d.Id] = sb;
                    }
                    s_Injected = true;
                    Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {s_Defs.Length} draft spells added to the loaded spell table ({FIRST}-{LAST})");
                    return true;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ExperimentalSpells: inject failed: {e.Message}"); return false; }
            }
        }

        internal static void AfterDats() { if (On) Inject(); }

        /// <summary>From ExpansionMotions when a session opts in: show the known ones in its spellbook.</summary>
        internal static void OnOptIn(Session s)
        {
            var p = s?.Player;
            if (!Live || p == null) return;
            foreach (var d in s_Defs)
                if (Allowed(p, d.Id) && p.QuestManager.HasQuest(Stamp(d.Id))) s!.Network.EnqueueSend(new GameEventMagicUpdateSpell(s, (ushort)d.Id));
            // Visitation's two bodies, shown small once so this client has built them before the first cast (WarmImages)
            var warm = new ActionChain();
            warm.AddDelaySeconds(8);
            warm.AddAction(WorldManager.ActionQueue, () => WarmImages(p));
            warm.EnqueueChain();
        }

        // ------------------------------------------------------------------ learning (a flag, never the spellbook)

        internal static bool LearnPrefix(Player p, uint spellId, bool uiOutput)
        {
            if (!Ours(spellId)) return true;
            if (!Live || !Allowed(p, spellId)) return false;   // staff spells: nobody below Sentinel ever learns one
            if (p.QuestManager.HasQuest(Stamp(spellId)))
            {
                if (uiOutput) p.Session.Network.EnqueueSend(new GameMessageSystemChat("You already know that spell!", ChatMessageType.Broadcast));
                return false;
            }
            p.QuestManager.Stamp(Stamp(spellId));
            if (ExpansionMotions.OptedIn(p.Session)) p.Session.Network.EnqueueSend(new GameEventMagicUpdateSpell(p.Session, (ushort)spellId));
            if (uiOutput)
            {
                p.ApplyVisualEffects(PlayScript.SkillUpPurple);
                p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You learn the {DefOf(spellId)!.Name} spell.\n", ChatMessageType.Broadcast));
            }
            return false;
        }

        internal static void KnownPostfix(Player p, uint spellId, ref bool __result)
        {
            if (!Ours(spellId)) return;
            __result = Live && Allowed(p, spellId) && p.QuestManager.HasQuest(Stamp(spellId));
        }

        // ------------------------------------------------------------------ the casts

        /// <summary>WorldObject.HandleCastSpell prefix. False = ACE's own effect is skipped (ours ran, or the cast is refused).</summary>
        internal static bool CastPrefix(WorldObject caster, Spell spell, WorldObject target, ref bool __result)
        {
            if (spell == null || !Ours(spell.Id)) return true;
            __result = false;
            if (!Live) return false;
            var d = DefOf(spell.Id)!;
            var p = caster as Player;
            if (d.Staff && !Staff(p))
            {
                // the server-side refusal: whatever sent the cast (a client, an item, an emote), a staff spell does nothing
                if (p != null) Say(p, $"{d.Name} answers only to the staff of Hollowtide.");
                Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: {caster?.Name} refused staff spell {d.Id} {d.Name}");
                return false;
            }
            switch (spell.Id)
            {
                case NETHER_WARD: case TIDE: case NULLIFY_VOID: __result = true; return true;   // ACE's own enchantment / dispel
                case SHARED_BLOOD: if (p != null) SharedBlood(p); return false;
                case FELLOW_RECALL: if (p != null) FellowRecall(p); return false;
                case TEMPEST: Tempest(p!); return false;
                case GATHER: Gather(p!); return false;
                case ECLIPSE: Eclipse(p!); return false;
                case RAVEN: Raven(p!); return false;
                case GALE: Gale(p!); return false;
                case UNMAKING: Unmake(p!, target); return false;
                case SENDOFF: SendOff(p!, target); return false;
                case STARFALL: Starfall(p!); return false;
                case FINALE: Finale(p!); return false;
                case VISITATION: Visitation(p!); return false;
                case FOWL: Fowl(p!); return false;
                case COWS: Cows(p!); return false;
                case ERUPTION: Eruption(p!); return false;
                case STORM: Storm(p!); return false;
                case WAVE: Wave(p!); return false;
                case FLYOVER: Flyover(p!); return false;
                case BIGHEAD: BigHead(p!); return false;
                case MOON: Moon(p!); return false;
                case GUISE: Guise(p!); return false;
                case ARENA: Arena(p!); return false;
                case BOSS: Boss(p!); return false;
                case TREASURE: Treasure(p!); return false;
                case CHAINS: if (!ChainsOk(p, target)) return false; __result = true; return true;   // ACE's own enchantment: the resist roll is behind us
            }
            return false;
        }

        /// <summary>Gossamer Tide: after the Imperil lands on the target, the same spell (its own resist roll each) on up
        /// to four other living monsters within 5 m of it. Never players, pets or anything unattackable.</summary>
        internal static void CastPostfix(WorldObject caster, Spell spell, WorldObject target, bool __result)
        {
            if (spell != null && spell.Id == CHAINS && Live && __result && target is Creature held) { ChainsLand(caster as Player, held); return; }
            if (t_Spreading || spell == null || spell.Id != TIDE || !Live || !__result) return;
            if (caster is not Player p || target is not Creature first || first is Player || first.Location == null) return;
            var near = new List<(float d, Creature c)>();
            foreach (var c in p.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeCreature())
            {
                if (c == null || c == first || c is Player || c is CombatPet || !c.IsAlive || !c.Attackable || c.Location == null) continue;
                float dist = c.Location.DistanceTo(first.Location);
                if (dist <= TIDE_RADIUS) near.Add((dist, c));
            }
            near.Sort((a, b) => a.d.CompareTo(b.d));
            t_Spreading = true;
            try { for (int i = 0; i < near.Count && i < TIDE_EXTRA; i++) p.TryCastSpell(spell, near[i].c); }
            finally { t_Spreading = false; }
        }

        static void SharedBlood(Player p)
        {
            var f = p.Fellowship;
            if (f == null) { Say(p, "Shared Blood needs a fellowship to share it with."); return; }
            var hurt = new List<Player>();
            foreach (var o in f.GetFellowshipMembers().Values)
            {
                if (o == null || o == p || !o.IsAlive || o.Location == null || o.Location.DistanceTo(p.Location) > BLOOD_RADIUS) continue;
                if (o.Health.Current < o.Health.MaxValue && hurt.Count < BLOOD_FELLOWS) hurt.Add(o);
            }
            if (hurt.Count == 0) { Say(p, "No wounded fellow is close enough to share your blood."); return; }
            int cost = BLOOD_COST * hurt.Count;
            if (p.Health.Current <= cost) { Say(p, "You do not have the health to spare."); return; }
            foreach (var o in hurt)
            {
                int got = o.UpdateVitalDelta(o.Health, ThreadSafeRandom.Next(BLOOD_MIN, BLOOD_MAX));
                o.EnqueueBroadcast(new GameMessageScript(o.Guid, PlayScript.HealthUpRed));
                Say(o, $"{p.Name} casts Shared Blood and restores {got} points of your health.");
                Say(p, $"You restore {got} points of health to {o.Name}.");
            }
            p.UpdateVitalDelta(p.Health, -cost);
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.HealthDownRed));
            Say(p, $"You lose {cost} points of health to Shared Blood.");
        }

        static void FellowRecall(Player p)
        {
            var f = p.Fellowship;
            if (f == null) { Say(p, "You are not in a fellowship."); return; }
            var leader = PlayerManager.GetOnlinePlayer(f.FellowshipLeaderGuid);
            if (leader == null || leader.Location == null) { Say(p, "Your fellowship's leader cannot be found."); return; }
            if (leader == p) { Say(p, "You lead this fellowship; there is no one to return to."); return; }
            if (p.PKTimerActive || leader.PKTimerActive)
            {
                p.Session.Network.EnqueueSend(new GameEventWeenieError(p.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }
            if (p.RecallsDisabled || leader.RecallsDisabled || p.Location.Indoors || leader.Location.Indoors || leader.Teleporting)
            {
                Say(p, "Fellowship Recall works only when you and your leader both stand under open sky.");
                return;
            }
            p.EnqueueBroadcast(new GameMessageSystemChat($"{p.Name} is recalling to the fellowship.", ChatMessageType.Recall), 96f, ChatMessageType.Recall);
            var chain = new ActionChain();
            chain.AddDelaySeconds(2.0);   // retail's recalls wait two seconds too (HandleCastSpell_PortalRecall)
            chain.AddAction(p, () =>
            {
                if (leader.Location == null || leader.Location.Indoors || p.PKTimerActive || leader.PKTimerActive) return;
                p.Teleport(new Position(leader.Location));
            });
            chain.EnqueueChain();
        }

        static IEnumerable<Player> PlayersNear(Player p, float range, bool self)
        {
            foreach (var o in PlayerManager.GetAllOnline())
            {
                if (o?.Session == null || o.Location == null || (!self && o == p)) continue;
                if (o.Location.DistanceTo(p.Location) <= range) yield return o;
            }
        }

        const double TEMPEST_S = 30;   // owner 11-27 round 3: "that spell also just turns off. it needs to fade out and last longer" (it was 12 s)

        static void Tempest(Player p)
        {
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.BaelZharonSmite));
            StormPortals(p, TEMPEST_S);   // owner 11-27: "says it makes a portal storm but i see no portals at all"
            var seen = PlayersNear(p, SHOW_RANGE, true).ToList();
            var sounds = new[] { EnvironChangeType.Thunder1Sound, EnvironChangeType.Thunder3Sound, EnvironChangeType.Thunder2Sound, EnvironChangeType.Thunder4Sound };
            var chain = new ActionChain();
            // The client's storm plays 8 s for each script and fades over its last seconds: one every 7 s keeps it turning
            // for the whole spell, and the last one is left to fade by itself while the portals close one by one.
            int beats = (int)(TEMPEST_S / 3.5);
            for (int i = 0; i < beats; i++)
            {
                int n = i;
                chain.AddAction(WorldManager.ActionQueue, () =>
                {
                    if (p.Location == null) return;
                    if (n % 2 == 0 && n * 3.5 < TEMPEST_S - 8) p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.PortalStorm));
                    foreach (var o in seen)
                    {
                        if (o.Session == null) continue;
                        if (n < beats - 2) o.Session.Network.EnqueueSend(new GameMessageAdminEnvirons(o.Session, sounds[n % sounds.Length]));   // the thunder ends before the fade
                        if (o != p && n < 3) o.EnqueueBroadcast(new GameMessageScript(o.Guid, PlayScript.SkillUpPurple));
                    }
                });
                chain.AddDelaySeconds(3.5);
            }
            chain.EnqueueChain();
            foreach (var o in seen) Say(o, $"The sky splits open above {p.Name}!");
        }

        static void Gather(Player p)
        {
            var crowd = PlayersNear(p, SHOW_RANGE, false).Where(o => !o.Teleporting).ToList();
            for (int i = 0; i < crowd.Count; i++)
            {
                // a ring 3 m out, widening by a metre every 12 players
                var spot = new Position(p.Location);
                spot.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(2 * Math.PI * (i % 12) / Math.Min(12, crowd.Count)));
                var to = spot.InFrontOf(3.0 + i / 12);
                Say(crowd[i], $"{p.Name} calls you to gather.");
                crowd[i].Teleport(to);
            }
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.PortalEntry));
            Say(p, crowd.Count == 0 ? "No one is near enough to gather." : $"You gather {crowd.Count} {(crowd.Count == 1 ? "player" : "players")} to you.");
        }

        static void Eclipse(Player p)
        {
            uint me = p.Guid.Full;
            if (s_Eclipse.TryRemove(me, out var on)) { Lift(on.seen); Say(p, "The light returns."); return; }
            var seen = PlayersNear(p, SHOW_RANGE, true).ToList().ConvertAll(o => o.Session);
            int token = Interlocked.Increment(ref s_EclipseToken);
            s_Eclipse[me] = (token, seen);
            foreach (var s in seen)
            {
                s.Network.EnqueueSend(new GameMessageAdminEnvirons(s, EnvironChangeType.BlackFog));
                s.Network.EnqueueSend(new GameMessageAdminEnvirons(s, EnvironChangeType.Thunder1Sound));
                if (s.Player != null) Say(s.Player, "A shadow passes over the sun.");
            }
            var chain = new ActionChain();
            chain.AddDelaySeconds(ECLIPSE_S);
            // on the world's queue, not the caster's: the light must come back even if the caster has logged off
            chain.AddAction(WorldManager.ActionQueue, () =>
            {
                if (s_Eclipse.TryGetValue(me, out var cur) && cur.token == token && s_Eclipse.TryRemove(me, out cur)) Lift(cur.seen);
            });
            chain.EnqueueChain();
        }

        static void Lift(List<Session> seen)
        {
            foreach (var s in seen)
                if (s?.Player != null) s.Network.EnqueueSend(new GameMessageAdminEnvirons(s, EnvironChangeType.Clear));
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsDats
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(DatManager), nameof(DatManager.Initialize), new[] { typeof(string), typeof(bool), typeof(bool) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookDats = TargetMethod() != null);
        static void Postfix()
        {
            try { ExperimentalSpells.AfterDats(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: after DATs: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsCast
    {
        // WorldObject DECLARES HandleCastSpell (protected); every cast that lands goes through it.
        static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(WorldObject), "HandleCastSpell", new[] { typeof(Spell), typeof(WorldObject),
            typeof(WorldObject), typeof(WorldObject), typeof(bool), typeof(bool), typeof(bool) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookCast = TargetMethod() != null);
        static bool Prefix(WorldObject __instance, Spell spell, WorldObject target, ref bool __result)
        {
            try { return ExperimentalSpells.CastPrefix(__instance, spell, target, ref __result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: cast: {e.Message}"); return spell == null || !ExperimentalSpells.Ours(spell.Id); }
        }
        static void Postfix(WorldObject __instance, Spell spell, WorldObject target, bool __result)
        {
            try { ExperimentalSpells.CastPostfix(__instance, spell, target, __result); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: spread: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsLearn
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.LearnSpellWithNetworking), new[] { typeof(uint), typeof(bool) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookLearn = TargetMethod() != null);
        static bool Prefix(Player __instance, uint spellId, bool uiOutput)
        {
            try { return ExperimentalSpells.LearnPrefix(__instance, spellId, uiOutput); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: learn: {e.Message}"); return !ExperimentalSpells.Ours(spellId); }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsKnown
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.SpellIsKnown), new[] { typeof(uint) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookKnown = TargetMethod() != null);
        static void Postfix(Player __instance, uint spellId, ref bool __result)
        {
            try { ExperimentalSpells.KnownPostfix(__instance, spellId, ref __result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: known: {e.Message}"); if (ExperimentalSpells.Ours(spellId)) __result = false; }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsFavorite
    {
        // a favourite is saved and sent in PlayerDescription to whatever client logs in next: never for our ids
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.HandleActionAddSpellFavorite), new[] { typeof(uint), typeof(uint), typeof(uint) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookFavorite = TargetMethod() != null);
        static bool Prefix(uint spellId) => !ExperimentalSpells.Ours(spellId);
    }
}
