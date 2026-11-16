using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// DRINKING: TIPSY, DRUNK, OUT COLD. Ours, not retail (owner 2026-11-16: "you could drink enough beer and there would
    /// be a blur effect and a reset as if you fell asleep drunk ... id like to make it"). Retail had only the brewed
    /// drinks' Intoxication I/II/III; nothing was drawn and nobody passed out. docs/DIVERGENCES.md "Drinking".
    ///
    /// Each alcoholic drink (DrinkingRule.WEIGHT, every one on the world) adds to a per-character count that wears off
    /// over minutes. Our client is sent the count on the mod channel ("drunk") and draws a sway, then blur and double
    /// vision, then a light stagger (AcDrunk). A drink with no Intoxication spell of its own casts Intoxication I once the
    /// count reaches DrinkingRule.SPELL_AT, so tavern stout is not free of it.
    ///
    /// Reaching DrinkingRule.PASS: the character collapses where it stands, the screen goes black for OUT_S seconds, and
    /// it wakes IN PLACE (never moved: no free recall), sober, with a short hangover. THE SERVER DECIDES: a drink that
    /// would pass you out is refused (not drunk, not used up) while you were hit recently, a monster is near, you are a
    /// player killer with a red player near, or you are in a combat stance. While out, being hit or a monster closing in
    /// wakes you at once.
    ///
    /// The count lives in memory only (a restart sobers everyone). Switches (live ACE properties, default on):
    /// rg_drinking (the whole feature), rg_drinking_passout, rg_drinking_spell, rg_drinking_barkeep.
    /// Admin: `@drunk [count|pass|0]` to set the count or pass out for a test.
    /// </summary>
    public static class Drinking
    {
        public const string P_ON = "rg_drinking", P_PASSOUT = "rg_drinking_passout", P_SPELL = "rg_drinking_spell", P_BARKEEP = "rg_drinking_barkeep";

        sealed class St
        {
            public double Drinks, At;      // the count at At (unix seconds)
            public bool Out;               // passed out now
            public int Token;              // which pass-out a scheduled wake belongs to
            public double OutAt;           // when it began
            public uint HealthAtOut;
            public int Stage;              // the last stage a line was said for
            public double BarkAt;          // the last barkeeper word
        }

        static readonly ConcurrentDictionary<uint, St> s_State = new ConcurrentDictionary<uint, St>();
        static readonly Random s_Rng = new Random();
        static bool s_Registered;

        static bool On => PropertyManager.GetBool(P_ON, true).Item;
        static double Now => ACE.Common.Time.GetUnixTime();

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                void Add(string k, string d) { if (!b.ContainsKey(k)) b[k] = new Property<bool>(true, d); }
                Add(P_ON, "RevivalGuard Drinking: alcohol makes our client's player tipsy, drunk and staggering (ours, not retail)");
                Add(P_PASSOUT, "RevivalGuard Drinking: too many drinks in a short time and the character passes out where it stands");
                Add(P_SPELL, "RevivalGuard Drinking: a drink with no Intoxication of its own casts Intoxication I from three drinks");
                Add(P_BARKEEP, "RevivalGuard Drinking: a barkeeper nearby now and then has a word for a drunk");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Drinking: property defaults not registered: {e.Message}"); }
            CommandManager.TryAddCommand(DrunkCommand, "drunk", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Drinking test: set your drinks count, or pass out (ours, not retail).", "[count | pass | 0]");
            Mod.Log.Info($"[RevivalGuard] Drinking: {DrinkingRule.WEIGHT.Count} drinks, {(On ? "ON" : "off")} ({P_ON}), @drunk");
        }

        static St Get(Player p) => s_State.GetOrAdd(p.Guid.Full, _ => new St { At = Now });

        static double Count(St st) => DrinkingRule.Now(st.Drinks, st.At, Now);

        // ---- the drink ---------------------------------------------------------------------------------------------

        /// <summary>Food.ActOnUse, before anything: false refuses the use (nothing drunk, nothing used up).</summary>
        internal static bool MayDrink(Player p, Food food)
        {
            if (p == null || food == null || !On) return true;
            uint wcid = food.WeenieClassId;
            s_State.TryGetValue(p.Guid.Full, out var st);
            if (st != null && st.Out) { PkCommon.Say(p, "You are out cold."); return false; }
            if (!DrinkingRule.IsAlcohol(wcid) || !PropertyManager.GetBool(P_PASSOUT, true).Item) return true;
            double now = st != null ? Count(st) : 0.0;
            if (!DrinkingRule.PassesOut(DrinkingRule.Add(now, DrinkingRule.Weight(wcid)))) return true;
            var block = Unsafe(p);
            if (block == DrinkingRule.Block.None) return true;
            PkCommon.Say(p, DrinkingRule.RefuseLine(block));
            return false;
        }

        static DrinkingRule.Block Unsafe(Player p) =>
            DrinkingRule.PassOutBlock(p.CombatMode != CombatMode.NonCombat, HitRecently(p), HostileNear(p, DrinkingRule.HOSTILE_M), RedPlayerNear(p));

        /// <summary>Food.ApplyConsumable has used it up: count it.</summary>
        internal static void Drank(Player p, uint wcid)
        {
            if (p == null || p.IsDead || !On) return;
            var st = Get(p);
            if (wcid == DrinkingRule.HANGOVER_CURE) { Cure(p, st); return; }
            if (!DrinkingRule.IsAlcohol(wcid) || st.Out) return;
            double before = Count(st);
            st.Stage = Math.Min(st.Stage, DrinkingRule.Stage(before));   // worn off since: its line may come again
            st.Drinks = DrinkingRule.Add(before, DrinkingRule.Weight(wcid));
            st.At = Now;
            int stage = DrinkingRule.Stage(st.Drinks);
            if (stage > st.Stage) { var line = DrinkingRule.StageLine(st.Stage, stage); if (line != null) PkCommon.Say(p, line); }
            st.Stage = stage;
            if (!DrinkingRule.OwnIntoxication(wcid) && st.Drinks >= DrinkingRule.SPELL_AT && PropertyManager.GetBool(P_SPELL, true).Item)
                Cast(p, DrinkingRule.INTOX_I);
            Send(p, st);
            if (stage >= 2) Barkeep(p, st, stage - 2);
            if (DrinkingRule.PassesOut(st.Drinks) && PropertyManager.GetBool(P_PASSOUT, true).Item)
            {
                // after the drink's own motion has played out
                var chain = new ActionChain();
                chain.AddDelaySeconds(1.5);
                chain.AddAction(p, () => { try { PassOut(p, st, false); } catch (Exception e) { TickGuard.Report("Drinking", e); } });
                chain.EnqueueChain();
            }
        }

        // ---- out cold ----------------------------------------------------------------------------------------------

        static void PassOut(Player p, St st, bool forced)
        {
            if (p.IsDead || p.Session == null || st.Out) return;
            // the world may have changed during the drink: a monster that walked up keeps you on your feet
            if (!forced && Unsafe(p) != DrinkingRule.Block.None)
            {
                st.Drinks = Math.Min(st.Drinks, DrinkingRule.PASS - 0.5); st.At = Now;
                PkCommon.Say(p, "You sway, but the danger nearby keeps you on your feet.");
                Send(p, st);
                return;
            }
            st.Out = true; st.Token++; st.OutAt = Now; st.HealthAtOut = p.Health.Current;
            int token = st.Token;
            PkCommon.Say(p, DrinkingRule.PASS_LINE);
            Others(p, $"{p.Name} slumps to the ground, out cold.");
            // our client lies its own body down (its MoveToState carries it to everyone else); a retail client is shown it
            if (!ModChannel.Capable(p.Session)) p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Sleeping));
            Send(p, st, DrinkingRule.OUT_S);
            Barkeep(p, st, 2, force: true);
            Mod.Log.Info($"[RevivalGuard] Drinking: {p.Name} passed out{(forced ? " (@drunk pass)" : "")}");
            Watch(p, st, token, 1);
        }

        /// <summary>Once a second while out: a hit or a monster closing in wakes you; else wake after OUT_S.</summary>
        static void Watch(Player p, St st, int token, int second)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.0);
            chain.AddAction(p, () =>
            {
                try
                {
                    if (!st.Out || st.Token != token) return;
                    if (p.IsDead || p.Session == null || p.CurrentLandblock == null) { Clear(p, st); return; }
                    bool hurt = p.Health.Current < st.HealthAtOut;
                    if (hurt || HostileNear(p, 12f)) { Wake(p, st, early: true); return; }
                    if (second >= (int)DrinkingRule.OUT_S) { Wake(p, st, early: false); return; }
                    Watch(p, st, token, second + 1);
                }
                catch (Exception e) { TickGuard.Report("Drinking", e); }
            });
            chain.EnqueueChain();
        }

        static void Wake(Player p, St st, bool early)
        {
            st.Out = false; st.Drinks = 0; st.At = Now; st.Stage = 0;
            Sober(p);
            string line = early ? DrinkingRule.WAKE_EARLY_LINE : DrinkingRule.WakeLine(s_Rng.Next());
            PkCommon.Say(p, line);
            Others(p, $"{p.Name} sits up, blinking.");
            if (!ModChannel.Capable(p.Session))
            {
                p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Sitting));
                var chain = new ActionChain();
                chain.AddDelaySeconds(2.0);
                chain.AddAction(p, () => { if (!p.IsDead) p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready)); });
                chain.EnqueueChain();
            }
            Send(p, st, 0, wake: true, line: line, early: early);
            Hangover(p);
        }

        /// <summary>Dead, logged off or gone while out: nothing more to show; the count goes.</summary>
        static void Clear(Player p, St st)
        {
            st.Out = false; st.Drinks = 0; st.At = Now; st.Stage = 0;
            if (p.Session != null) Send(p, st);
        }

        /// <summary>Ulgrim's Hangover Cure (retail 23924, it restores health): ours, it also sobers you and lifts the hangover.</summary>
        static void Cure(Player p, St st)
        {
            if (st.Out) return;
            bool had = Count(st) > 0.01 || p.EnchantmentManager.GetEnchantment(DrinkingRule.HANGOVER_SPELL) != null
                       || p.EnchantmentManager.GetEnchantment(DrinkingRule.INTOX_I) != null;
            st.Drinks = 0; st.At = Now; st.Stage = 0;
            Sober(p);
            var h = p.EnchantmentManager.GetEnchantment(DrinkingRule.HANGOVER_SPELL);
            if (h != null) p.EnchantmentManager.Dispel(h);
            if (had) PkCommon.Say(p, DrinkingRule.CURE_LINE);
            Send(p, st);
        }

        static void Sober(Player p)
        {
            foreach (var id in new[] { DrinkingRule.INTOX_I, DrinkingRule.INTOX_II, DrinkingRule.INTOX_III })
            {
                var e = p.EnchantmentManager.GetEnchantment(id);
                if (e != null) p.EnchantmentManager.Dispel(e);
            }
        }

        /// <summary>Feeblemind Self I (Self -10), held for HANGOVER_S instead of its own 60 s.</summary>
        static void Hangover(Player p)
        {
            try
            {
                var spell = new Spell(DrinkingRule.HANGOVER_SPELL);
                if (spell.NotFound) return;
                var r = p.EnchantmentManager.Add(spell, p, null);
                if (r?.Enchantment == null) return;
                r.Enchantment.Duration = DrinkingRule.HANGOVER_S;
                p.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(p.Session, new ACE.Server.Network.Structure.Enchantment(p, r.Enchantment)));
                p.HandleSpellHooks(spell);
                PkCommon.Say(p, DrinkingRule.HANGOVER_LINE, ChatMessageType.Magic);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Drinking: hangover: {e.Message}"); }
        }

        static void Cast(Player p, uint id)
        {
            try
            {
                var spell = new Spell(id);
                if (!spell.NotFound) p.TryCastSpell(spell, p, null, tryResist: false);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Drinking: cast {id}: {e.Message}"); }
        }

        // ---- what is around ----------------------------------------------------------------------------------------

        /// <summary>Hit by someone else in the last HIT_RECENT_S seconds (ACE's damage log), or a PK timer running.</summary>
        static bool HitRecently(Player p)
        {
            try
            {
                if (p.PKTimerActive) return true;
                var cut = DateTime.UtcNow.AddSeconds(-DrinkingRule.HIT_RECENT_S);
                var log = p.DamageHistory?.Log;
                if (log == null) return false;
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    var e = log[i];
                    if (e == null || e.Time < cut) continue;
                    if (e.Amount > 0 && e.Attacker != p.Guid) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>A living monster (attackable, not a player or a pet) within <paramref name="m"/> metres, or one anywhere in
        /// sight that is after you.</summary>
        static bool HostileNear(Player p, float m)
        {
            try
            {
                var near = p.PhysicsObj?.ObjMaint?.GetVisibleObjectsValuesWhere(o => o?.WeenieObj?.WorldObject is Creature c
                    && !(c is Player) && !(c is CombatPet) && c.IsMonster && c.Attackable && !c.IsDead);
                if (near == null) return false;
                foreach (var o in near)
                {
                    var c = (Creature)o.WeenieObj.WorldObject;
                    if (c.AttackTarget == p) return true;
                    if (p.GetDistance(c) <= m) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>A player killer, with another red player within PK_M (the dangerous case).</summary>
        static bool RedPlayerNear(Player p)
        {
            try
            {
                if (!p.IsPKType) return false;
                foreach (var o in p.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                    if (o != null && o != p && o.IsPK && !o.IsDead && p.GetDistance(o) <= DrinkingRule.PK_M) return true;
            }
            catch { }
            return false;
        }

        /// <summary>An emote-coloured line to the players near, not to the drinker.</summary>
        static void Others(Player p, string text)
        {
            try
            {
                var msg = new GameMessageSystemChat(text, ChatMessageType.Emote);
                foreach (var o in p.PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                    if (o != null && o != p && o.Session != null && p.GetDistance(o) <= 20f) o.Session.Network.EnqueueSend(msg);
            }
            catch { }
        }

        /// <summary>A barkeeper within 15 m now and then says a word (one per two minutes per drinker, half the time).</summary>
        static void Barkeep(Player p, St st, int pool, bool force = false)
        {
            try
            {
                if (!PropertyManager.GetBool(P_BARKEEP, true).Item) return;
                if (!force && (Now - st.BarkAt < DrinkingRule.BARKEEP_GAP_S || s_Rng.NextDouble() > 0.5)) return;
                var npc = p.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o?.WeenieObj?.WorldObject is Creature c
                        && !(c is Player) && !c.Attackable && DrinkingRule.IsBarkeep(c.Name))
                    .Select(o => (Creature)o.WeenieObj.WorldObject)
                    .Where(c => p.GetDistance(c) <= 15f)
                    .OrderBy(c => p.GetDistance(c))
                    .FirstOrDefault();
                if (npc == null) return;
                st.BarkAt = Now;
                var lines = DrinkingRule.BARKEEP_LINES[Math.Clamp(pool, 0, DrinkingRule.BARKEEP_LINES.Length - 1)];
                npc.EnqueueBroadcast(new GameMessageHearSpeech(lines[s_Rng.Next(lines.Length)], npc.Name, npc.Guid.Full, ChatMessageType.Speech), 30f);
            }
            catch { }
        }

        // ---- to our client -----------------------------------------------------------------------------------------

        /// <summary>"drunk": {"d": count now, "r": wear-off per second, "p": the pass-out count, "o": seconds out cold
        /// starting now (0 = not out), "w": 1 on waking, "e": 1 when woken early (a hit, a monster: up at once), "l": the
        /// wake line}.</summary>
        static void Send(Player p, St st, double outSecs = 0, bool wake = false, string line = null, bool early = false)
        {
            if (p?.Session == null || !ModChannel.Capable(p.Session)) return;
            var ci = CultureInfo.InvariantCulture;
            string json = "{\"d\":" + Count(st).ToString("0.###", ci) + ",\"r\":" + DrinkingRule.DECAY_PER_S.ToString("0.######", ci)
                        + ",\"p\":" + DrinkingRule.PASS.ToString("0.##", ci) + ",\"o\":" + outSecs.ToString("0.#", ci)
                        + ",\"w\":" + (wake ? 1 : 0) + (early ? ",\"e\":1" : "") + (line != null ? ",\"l\":" + Telemetry.J(line) : "") + "}";
            ModChannel.Send(p.Session, "drunk", json);
        }

        /// <summary>From ModChannel's hello: a character that relogs while drunk keeps its sway (out cold is not resumed).</summary>
        internal static void Hello(Session session)
        {
            var p = session?.Player;
            if (p == null || !s_State.TryGetValue(p.Guid.Full, out var st)) return;
            if (st.Out && Now - st.OutAt > DrinkingRule.OUT_S + 5) { st.Out = false; st.Drinks = 0; st.At = Now; }
            if (Count(st) > 0.01) Send(p, st);
        }

        // ---- admin -------------------------------------------------------------------------------------------------

        static void DrunkCommand(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            var st = Get(p);
            string a = parameters != null && parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            if (a == "pass")
            {
                var why = Unsafe(p);
                if (why != DrinkingRule.Block.None) { PkCommon.Say(p, "[Drinking] refused: " + DrinkingRule.RefuseLine(why)); return; }
                st.Drinks = DrinkingRule.PASS; st.At = Now;
                PassOut(p, st, true);
                return;
            }
            if (a.Length > 0 && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                st.Drinks = Math.Clamp(n, 0.0, DrinkingRule.PASS - 0.01); st.At = Now; st.Stage = DrinkingRule.Stage(st.Drinks);
                if (n <= 0) { Sober(p); var h = p.EnchantmentManager.GetEnchantment(DrinkingRule.HANGOVER_SPELL); if (h != null) p.EnchantmentManager.Dispel(h); }
                Send(p, st);
            }
            PkCommon.Say(p, $"[Drinking] count {Count(st):0.00} of {DrinkingRule.PASS} (stage {DrinkingRule.Stage(Count(st))}, out {st.Out}); "
                          + $"switch {P_ON} {(On ? "ON" : "off")}; this client {(ModChannel.Capable(session) ? "draws it" : "does not read the mod channel")}");
        }
    }

    /// <summary>A drink is refused before its motion when it would pass you out somewhere unsafe, or while out cold. The
    /// patch's Prepare registers the feature (Mod.cs needs no line).</summary>
    [HarmonyPatch(typeof(Food), nameof(Food.ActOnUse))]
    static class DrinkingUse
    {
        static bool Prepare() { Drinking.Register(); return true; }
        static bool Prefix(Food __instance, WorldObject activator)
        {
            try { return !(activator is Player p) || Drinking.MayDrink(p, __instance); }
            catch (Exception e) { TickGuard.Report("Drinking", e); return true; }
        }
    }

    /// <summary>The drink went down (it was still in the pack when its motion ended): count it.</summary>
    [HarmonyPatch(typeof(Food), nameof(Food.ApplyConsumable))]
    static class DrinkingApply
    {
        static void Prefix(Food __instance, Player player, out uint __state)
        {
            __state = 0;
            try
            {
                if (player == null || player.IsDead) return;
                uint w = __instance.WeenieClassId;
                if ((DrinkingRule.IsAlcohol(w) || w == DrinkingRule.HANGOVER_CURE)
                    && player.FindObject(__instance.Guid.Full, Player.SearchLocations.MyInventory) != null) __state = w;
            }
            catch { }
        }

        static void Postfix(Player player, uint __state)
        {
            if (__state == 0) return;
            try { Drinking.Drank(player, __state); }
            catch (Exception e) { TickGuard.Report("Drinking", e); }
        }
    }
}
