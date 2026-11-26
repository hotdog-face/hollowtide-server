using System.Collections.Concurrent;
using ACE.Common;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>
    /// `@adminkit` (AccessLevel.Admin, ours, not retail; owner 2026-11-25): the admin panel's Ready kit, to make the owner's
    /// character "ready for anything". Behind one switch, `rg_admin_kit` (default OFF): off, every verb says so and does
    /// nothing, and the hooks below never act. AuditTrail records every use (an above-Player command), and each verb also
    /// posts what it did to ACE's Audit channel. ACE's own staff verbs are called where they exist (@buff, @run, @pk,
    /// @attackable, @adminvision); the rest is ours.
    ///
    ///   ready                 cure, then every self buff at level 8 (ACE @buff: creature, life, item, banes and
    ///                         Impenetrability on worn armour and shields), then full vitals
    ///   cure                  vitae off, harmful enchantments off you and your worn gear (retail's death purge, as asked)
    ///   restock               components your known spells take, ammunition for the wielded launcher, kits, elixirs,
    ///                         mana charges (AdminKitRule.SUPPLIES); worn items' mana full; pack items' uses full
    ///   revive|nofall|ignore|run|see [on|off]   toggles: revive in place with no vitae (memory), no fall damage (memory),
    ///                         monsters ignore you (ACE @attackable, saved), ACE's Sentinel run boost (wears off), ACE
    ///                         @adminvision (see cloaked staff and hidden objects; off takes a relog to hide them again)
    ///   pk                    flip your own PK and NPK (ACE @pk)
    ///   tp &lt;NS&gt; &lt;EW&gt;           travel to map coordinates (the map's click; ACE's AdvocateTeleport placement)
    ///   goto &lt;0xguid|name&gt;      go to a player, keeping where you stood;  back: return there
    ///   smite &lt;0xguid&gt; [player] kill the target (a player only with "player": the panel asks first)
    ///   clear [metres]        kill every attackable monster within that range (default 30)
    ///   freeze &lt;0xguid&gt; [secs]  a monster stops moving and attacking for that long (default 30); unfreeze &lt;0xguid&gt;
    ///   scout &lt;0xguid|name&gt;     a player's level, vitals, attributes, top skills, ratings and worn gear, in chat
    ///   status                the switch and every toggle, one line (the panel's orbs)
    /// </summary>
    public static class AdminKit
    {
        const AccessLevel LEVEL = AccessLevel.Admin;
        const string TAG = "[AdminKit] (ours) ";
        static readonly ConcurrentDictionary<uint, byte> s_Revive = new ConcurrentDictionary<uint, byte>();
        static readonly ConcurrentDictionary<uint, byte> s_NoFall = new ConcurrentDictionary<uint, byte>();
        static readonly ConcurrentDictionary<uint, Position> s_Back = new ConcurrentDictionary<uint, Position>();
        static readonly ConcurrentDictionary<uint, double> s_Frozen = new ConcurrentDictionary<uint, double>();

        internal static bool On => PropertyManager.GetBool(AdminKitRule.P_ON, false).Item;

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "adminkit", LEVEL, CommandHandlerFlag.RequiresWorld,
                "Staff (ours, not retail): the admin panel's Ready kit. Switch rg_admin_kit.",
                "ready | cure | restock | revive|nofall|ignore|run|see [on|off] | pk | tp <NS> <EW> | goto <who> | back | smite <0xguid> | clear [m] | freeze <0xguid> [s] | unfreeze <0xguid> | scout <who> | status");
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(AdminKitRule.P_ON))
                    b[AdminKitRule.P_ON] = new Property<bool>(false, "RevivalGuard AdminKit: the admin panel's Ready kit (@adminkit, Admin only; false: every verb refuses)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AdminKit: switch not registered with @modifybool ({e.Message}); read at its default"); }
            Mod.Log.Info($"[RevivalGuard] AdminKit: @adminkit (Admin; switch {AdminKitRule.P_ON} {(On ? "ON" : "off")})");
        }

        static void Say(Session s, string text) => s?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static bool Staff(Player p) => p?.Session != null && p.Session.AccessLevel >= LEVEL;

        static void Audit(Player p, string what) => PlayerManager.BroadcastToAuditChannel(p, $"{p.Name} admin kit: {what}");

        static void Handle(Session session, params string[] args)
        {
            var p = session?.Player;
            if (p == null) return;
            string verb = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            string a1 = args != null && args.Length > 1 ? args[1] : null;
            string rest = args != null && args.Length > 1 ? string.Join(" ", args.Skip(1)) : "";
            if (verb == "status") { Status(p); return; }
            if (!On) { Say(session, TAG + $"off on this server: the Ready kit needs the server setting {AdminKitRule.P_ON} turned ON (Server tab > Setting)."); return; }
            try
            {
                switch (verb)
                {
                    case "ready": Cure(p); SentinelCommands.HandleBuff(session); p.SetMaxVitals(); Audit(p, "ready (cure, buffs, vitals)");
                        Say(session, TAG + "ready: cured, every self buff at level 8 (armour banes too), full vitals."); break;
                    case "cure": Cure(p); p.SetMaxVitals(); Audit(p, "cure"); Say(session, TAG + "cure: vitae and harmful spells removed, vitals full."); break;
                    case "restock": Restock(p); break;
                    case "revive": Toggle(p, "revive", s_Revive, a1); break;
                    case "nofall": Toggle(p, "nofall", s_NoFall, a1); break;
                    case "ignore": Ignore(p, a1); break;
                    case "run": Run(p, a1); break;
                    case "see": See(p, a1); break;
                    case "pk": Pk(p); break;
                    case "tp": Tp(p, rest); break;
                    case "goto": GoTo(p, rest); break;
                    case "back": Back(p); break;
                    case "smite": Smite(p, a1, args.Length > 2 && args[2].ToLowerInvariant() == "player"); break;
                    case "clear": Clear(p, a1); break;
                    case "freeze": Freeze(p, a1, args.Length > 2 ? args[2] : null); break;
                    case "unfreeze": Unfreeze(p, a1); break;
                    case "scout": Scout(p, rest); break;
                    default: Say(session, TAG + "unknown: " + verb); break;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] AdminKit {verb}: {e.GetType().Name}: {e.Message}");
                Say(session, TAG + verb + " failed: " + e.Message);
            }
        }

        // ---- status and toggles -----------------------------------------------------------------------------

        static bool Ignoring(Player p) => p.GetProperty(PropertyBool.Attackable) == false;
        static bool Running(Player p) => p.EnchantmentManager.HasSpell((uint)SpellId.SentinelRun);
        static bool IsPk(Player p) => p.PlayerKillerStatus == PlayerKillerStatus.PK;

        static void Status(Player p)
        {
            uint g = p.Guid.Full;
            Say(p.Session, AdminKitRule.StatusLine(On, s_Revive.ContainsKey(g), s_NoFall.ContainsKey(g), Ignoring(p), Running(p), p.Adminvision, IsPk(p)));
        }

        static bool Want(string arg, bool now) => arg == null ? !now : arg.ToLowerInvariant() == "on";

        static void Toggle(Player p, string key, ConcurrentDictionary<uint, byte> set, string arg)
        {
            bool on = Want(arg, set.ContainsKey(p.Guid.Full));
            if (on) set[p.Guid.Full] = 1; else set.TryRemove(p.Guid.Full, out _);
            Audit(p, $"{key} {(on ? "on" : "off")}");
            Say(p.Session, TAG + key + (on ? " ON" : " OFF") + (key == "revive"
                ? (on ? ": you get up where you fall, no vitae, no corpse, buffs back (memory only: off after a relog or restart)." : ": deaths are normal again.")
                : (on ? ": no damage from falls (memory only: off after a relog or restart)." : ": falls hurt again.")));
        }

        static void Ignore(Player p, string arg)
        {
            bool on = Want(arg, Ignoring(p));
            AdvocateCommands.HandleAttackable(p.Session, on ? "off" : "on");   // ACE @attackable: off = monsters ignore you
            Audit(p, "monsters ignore " + (on ? "on" : "off"));
            Say(p.Session, TAG + "ignore" + (on ? " ON: monsters leave you alone unless you attack first (saved on the character)." : " OFF: monsters attack you normally."));
        }

        static void Run(Player p, string arg)
        {
            bool on = Want(arg, Running(p));
            SentinelCommands.HandleRun(p.Session, on ? "on" : "off");
            Audit(p, "run boost " + (on ? "on" : "off"));
            Say(p.Session, TAG + "run" + (on ? " ON: ACE's Sentinel run boost (it wears off after a while)." : " OFF"));
        }

        static void See(Player p, string arg)
        {
            bool on = Want(arg, p.Adminvision);
            AdminCommands.HandleAdminvision(p.Session, on ? "on" : "off");
            Audit(p, "see hidden " + (on ? "on" : "off"));
            Say(p.Session, TAG + "see" + (on ? " ON: you see cloaked staff and hidden objects." : " OFF: they hide again after you relog."));
        }

        static void Pk(Player p)
        {
            bool toPk = !IsPk(p);
            AdminCommands.HandlePk(p.Session, toPk ? "pk" : "npk");
            Say(p.Session, TAG + "pk" + (IsPk(p) ? " ON: you are a player killer." : " OFF: you are NPK."));
        }

        // ---- buff, cure, restock ----------------------------------------------------------------------------

        static void Cure(Player p)
        {
            if (p.EnchantmentManager.GetVitae() != null) p.EnchantmentManager.RemoveVitae();
            p.EnchantmentManager.RemoveAllBadEnchantments();
            p.Session.Network.EnqueueSend(new GameEventMagicPurgeBadEnchantments(p.Session));
            foreach (var item in p.EquippedObjects.Values.ToList())
                item.EnchantmentManager?.RemoveAllBadEnchantments();
        }

        static void Restock(Player p)
        {

            // Supplies and ammunition first: the components come last, so a full pack or burden cuts them, not the kits.
            int made = 0, failed = 0, heavy = 0;
            var got = new List<string>();
            var bow = p.GetEquippedMissileLauncher();
            if (bow != null)
            {
                int skill = (int)p.GetCreatureSkill(Skill.MissileWeapons).Current;
                foreach (var wcid in AdminKitRule.AmmoFor((int)(bow.AmmoType ?? 0), (int)bow.W_DamageType, skill))
                {
                    int n = TopUp(p, wcid, _ => AdminKitRule.AMMO_TARGET, ref failed, ref heavy, out string name);
                    if (name == null) continue;
                    made += n;
                    got.Add($"{name} to {AdminKitRule.AMMO_TARGET}");
                    break;
                }
            }
            else got.Add("no launcher wielded, so no ammunition");
            foreach (var s in AdminKitRule.SUPPLIES)
            {
                made += TopUp(p, s.wcid, _ => s.target, ref failed, ref heavy, out _);
                got.Add($"{s.name} to {s.target}");
            }

            // Components the known spells take as this character casts them (a focus swaps in scarab + prismatic taper), for
            // each school's highest level known only: the lower levels' formulas would fill a pack (82 kinds on a test
            // character, 2026-11-25) for spells nobody casts once the top level is known.
            var top = new Dictionary<MagicSchool, uint>();
            var spells = new List<Spell>();
            foreach (var id in p.Biota.GetKnownSpellsIds(p.BiotaDatabaseLock))
            {
                var spell = new Spell((uint)id);
                if (spell.NotFound || spell.Formula == null) continue;
                spells.Add(spell);
                uint lvl = spell.Formula.Level;
                if (!top.TryGetValue(spell.School, out var t) || lvl > t) top[spell.School] = lvl;
            }
            var comps = new HashSet<uint>();
            foreach (var spell in spells)
            {
                if (spell.Formula.Level < top[spell.School]) continue;
                spell.Formula.GetPlayerFormula(p);
                foreach (var c in spell.Formula.CurrentFormula ?? spell.Formula.PlayerFormula ?? new List<uint>())
                {
                    var wcid = Spell.GetComponentWCID(c);
                    if (wcid != 0) comps.Add(wcid);
                }
            }
            int compMade = 0;
            foreach (var wcid in comps) compMade += TopUp(p, wcid, AdminKitRule.ComponentTarget, ref failed, ref heavy, out _);
            made += compMade;
            got.Add($"{comps.Count} kinds of spell component for your top-level spells");

            // Worn items' mana full, pack items' uses full.
            int charged = 0, repaired = 0;
            foreach (var item in p.EquippedObjects.Values.ToList())
                if ((item.ItemMaxMana ?? 0) > 0 && (item.ItemCurMana ?? 0) < item.ItemMaxMana)
                { p.UpdateProperty(item, PropertyInt.ItemCurMana, item.ItemMaxMana); charged++; }
            foreach (var item in p.GetAllPossessions())
                if ((item.MaxStructure ?? 0) > 0 && (item.Structure ?? 0) < item.MaxStructure)
                { p.UpdateProperty(item, PropertyInt.Structure, item.MaxStructure); repaired++; }

            Audit(p, $"restock: {made} stacks, {charged} worn items charged, {repaired} repaired, {failed} did not fit, {heavy} too heavy");
            Say(p.Session, TAG + $"restock: {made} stacks made: {string.Join(", ", got)}. {charged} worn items' mana full, {repaired} items' uses full."
                + (failed > 0 ? $" {failed} did not fit: make room and press again." : "")
                + (heavy > 0 ? $" {heavy} left out to keep you under full burden." : ""));
        }

        /// <summary>Stacks of `wcid` until the pack holds target(name), counting what is there (AdminVoid.TopUp's way); never
        /// past full burden (100%: slower movement and weaker defense beyond it).</summary>
        static int TopUp(Player p, uint wcid, Func<string, int> target, ref int failed, ref int heavy, out string name)
        {
            name = null;
            int have = p.GetNumInventoryItemsOfWCID(wcid), made = 0, want = -1;
            while (true)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
                if (wo == null) { failed++; return made; }
                name = wo.Name;
                if (want < 0) want = target(wo.Name);
                if (have >= want) { wo.Destroy(); return made; }
                int n = Math.Min(want - have, Math.Max(1, (int)(wo.MaxStackSize ?? 1)));
                if (n > 1) wo.SetStackSize(n);
                if ((p.EncumbranceVal ?? 0) + (wo.EncumbranceVal ?? 0) > p.GetEncumbranceCapacity()) { wo.Destroy(); heavy++; return made; }
                if (!p.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); failed++; return made; }
                have += wo.StackSize ?? 1;
                made++;
            }
        }

        // ---- travel -----------------------------------------------------------------------------------------

        static void Keep(Player p) => s_Back[p.Guid.Full] = new Position(p.Location);

        static void Tp(Player p, string text)
        {
            if (!AdminKitRule.ParseCoords(text, out float ns, out float ew)) { Say(p.Session, TAG + "tp: give map coordinates like 42.1N 33.6E"); return; }
            AdminKitRule.CellOf(ns, ew, out uint cell, out float x, out float y);
            var lb = LScape.get_landblock(cell);
            if (lb != null && lb.WaterType == LandDefs.WaterType.EntirelyWater) { Say(p.Session, TAG + "tp: that spot is open water"); return; }
            var pos = new Position(cell, x, y, 0f, 0f, 0f, 0f, 1f);
            pos.AdjustMapCoords();
            Keep(p);
            Audit(p, $"tp {ns:0.0} {ew:0.0}");
            Say(p.Session, TAG + $"tp: going to {pos.GetMapCoordStr()} (back returns you).");
            p.Teleport(pos);
        }

        static Player FindPlayer(string who)
        {
            who = (who ?? "").Trim();
            if (who.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(who.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var g))
                return PlayerManager.GetOnlinePlayer(g);
            return who.Length > 0 ? PlayerManager.GetOnlinePlayer(who.TrimStart('+')) ?? PlayerManager.GetOnlinePlayer("+" + who.TrimStart('+')) : null;
        }

        static void GoTo(Player p, string who)
        {
            var t = FindPlayer(who);
            if (t == null || t == p) { Say(p.Session, TAG + "goto: no player online by that name (select one, or type a name)"); return; }
            Keep(p);
            Audit(p, "goto " + t.Name);
            Say(p.Session, TAG + $"goto: going to {t.Name} (back returns you).");
            p.Teleport(new Position(t.Location));
        }

        static void Back(Player p)
        {
            if (!s_Back.TryRemove(p.Guid.Full, out var pos)) { Say(p.Session, TAG + "back: nowhere kept (go to a player or a map spot first)"); return; }
            Audit(p, "back");
            Say(p.Session, TAG + "back: returning you to where you stood.");
            p.Teleport(pos);
        }

        // ---- combat -----------------------------------------------------------------------------------------

        static Creature Target(Player p, string arg)
        {
            if (arg == null || !arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(arg.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var g) || g == p.Guid.Full) return null;
            return p.CurrentLandblock?.GetObject(new ObjectGuid(g)) as Creature;
        }

        static bool UseTakeDamage => PropertyManager.GetBool("smite_uses_takedamage").Item;

        static void Smite(Player p, string arg, bool player)
        {
            var c = Target(p, arg);
            if (c == null || c.IsDead) { Say(p.Session, TAG + "smite: select a living creature first"); return; }
            if (c is Player && !player) { Say(p.Session, TAG + "smite: that is a player; the panel asks before smiting a player"); return; }
            c.Smite(p, UseTakeDamage);
            Audit(p, $"smite {c.Name} (0x{c.Guid.Full:X8})");
            Say(p.Session, TAG + "smite: " + c.Name);
        }

        static bool Monster(Creature c, Player p) =>
            c != null && !(c is Player) && c != p && c.Attackable && !c.IsDead && c.PetOwner == null;

        static void Clear(Player p, string arg)
        {
            float r = AdminKitRule.ClearRadius(arg);
            int n = 0;
            foreach (var obj in p.PhysicsObj.ObjMaint.GetVisibleObjectsValues().ToList())
            {
                var c = obj.WeenieObj?.WorldObject as Creature;
                if (!Monster(c, p) || c.Location == null || p.Location.DistanceTo(c.Location) > r) continue;
                c.Smite(p, UseTakeDamage);
                n++;
            }
            Audit(p, $"clear {r:0} m: {n} monsters");
            Say(p.Session, TAG + $"clear: {n} monsters within {r:0} m.");
        }

        static double Now => Time.GetUnixTime();

        static void Freeze(Player p, string arg, string secs)
        {
            var c = Target(p, arg);
            if (c is Player) { Say(p.Session, TAG + "freeze: players move on their own clients and cannot be frozen; monsters only"); return; }
            if (!Monster(c, p)) { Say(p.Session, TAG + "freeze: select a monster first"); return; }
            int s = AdminKitRule.FreezeSeconds(secs);
            s_Frozen[c.Guid.Full] = Now + s;
            c.CancelMoveTo();
            c.PhysicsObj?.StopCompletely(false);
            c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.Ready));
            Audit(p, $"freeze {c.Name} (0x{c.Guid.Full:X8}) {s} s");
            Say(p.Session, TAG + $"freeze: {c.Name} stands still for {s} s.");
        }

        static void Unfreeze(Player p, string arg)
        {
            var c = Target(p, arg);
            if (c == null || !s_Frozen.TryRemove(c.Guid.Full, out _)) { Say(p.Session, TAG + "unfreeze: that is not frozen"); return; }
            Say(p.Session, TAG + "unfreeze: " + c.Name);
        }

        static void Scout(Player p, string who)
        {
            var t = FindPlayer(who);
            if (t == null) { Say(p.Session, TAG + "scout: select a player (or type a name)"); return; }
            Audit(p, "scout " + t.Name);
            var sb = new StringBuilder();
            sb.Append(TAG).Append($"scout {t.Name}: level {t.Level}, {t.PlayerKillerStatus}, health {t.Health.Current}/{t.Health.MaxValue}, "
                + $"stamina {t.Stamina.Current}/{t.Stamina.MaxValue}, mana {t.Mana.Current}/{t.Mana.MaxValue}");
            Say(p.Session, sb.ToString());
            Say(p.Session, $"  Str {t.Strength.Current}  End {t.Endurance.Current}  Coord {t.Coordination.Current}  Quick {t.Quickness.Current}  Focus {t.Focus.Current}  Self {t.Self.Current};"
                + $" damage rating {t.GetDamageRating()}, resist {t.GetDamageResistRating()}, crit {t.GetCritRating()}");
            var skills = t.Skills.Values.Where(s => s.AdvancementClass >= SkillAdvancementClass.Trained)
                .OrderByDescending(s => s.Current).Take(10).Select(s => $"{s.Skill} {s.Current}{(s.AdvancementClass == SkillAdvancementClass.Specialized ? "*" : "")}");
            Say(p.Session, "  Skills (* spec): " + string.Join(", ", skills));
            var worn = t.EquippedObjects.Values.OrderBy(o => (int)(o.CurrentWieldedLocation ?? 0))
                .Select(o => o.Name + ((o.ArmorLevel ?? 0) > 0 ? $" AL{o.ArmorLevel}" : "") + ((o.ItemMaxMana ?? 0) > 0 ? $" {o.ItemCurMana}/{o.ItemMaxMana}mana" : "")).ToList();
            for (int i = 0; i < worn.Count; i += 6)
                Say(p.Session, (i == 0 ? "  Worn: " : "        ") + string.Join("; ", worn.Skip(i).Take(6)));
        }

        // ---- the hooks: revive in place, no fall damage, frozen monsters -------------------------------------

        static bool Reviving(Player p) => !s_Revive.IsEmpty && s_Revive.ContainsKey(p.Guid.Full) && Staff(p) && On;

        /// <summary>No corpse for a reviving admin: everything stays on them.</summary>
        [HarmonyPatch(typeof(Creature), "CreateCorpse")]
        static class NoCorpse
        {
            static bool Prefix(Creature __instance)
            {
                try { return !(__instance is Player p && Reviving(p)); } catch { return true; }
            }
        }

        /// <summary>Up again where they fell: no lifestone trip, vitae off, vitals full, buffs back.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.ThreadSafeTeleportOnDeath))]
        static class ReviveInPlace
        {
            static bool Prefix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (!Reviving(p) || p.IsLoggingOut) return true;
                    if (p.EnchantmentManager.GetVitae() != null) p.EnchantmentManager.RemoveVitae();
                    p.SetCombatMode(CombatMode.NonCombat);
                    p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready));
                    p.DamageHistory.Reset();
                    p.SetMaxVitals();
                    p.IsInDeathProcess = false;
                    p.CreateSentinelBuffPlayers(new[] { p }, true, 8);
                    p.SetMaxVitals();
                    p.SendUpdatePosition(true);   // the body where the server has it, with a fresh teleport stamp (ForceHomeStamp)
                    Audit(p, "revived in place");
                    Say(p.Session, TAG + "revive: up again where you fell; no vitae, buffs back.");
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn("[RevivalGuard] AdminKit revive: " + e.Message); return true; }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TakeDamage_Falling))]
        static class NoFall
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Player __instance)
            {
                try { return s_NoFall.IsEmpty || !s_NoFall.ContainsKey(__instance.Guid.Full) || !Staff(__instance) || !On; } catch { return true; }
            }
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
        static class Frozen
        {
            static bool Prefix(Creature __instance, double currentUnixTime)
            {
                if (s_Frozen.IsEmpty) return true;
                try
                {
                    if (!s_Frozen.TryGetValue(__instance.Guid.Full, out var until)) return true;
                    if (currentUnixTime >= until || !On || __instance.IsDead) { s_Frozen.TryRemove(__instance.Guid.Full, out _); return true; }
                    __instance.NextMonsterTickTime = currentUnixTime + 0.2;
                    return false;
                }
                catch { return true; }
            }
        }
    }
}
