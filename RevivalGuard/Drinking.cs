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
    /// <summary>DRINKING: TIPSY, DRUNK, out cold.</summary>
    public static class Drinking
    {
        public const string P_ON = "rg_drinking", P_PASSOUT = "rg_drinking_passout", P_SPELL = "rg_drinking_spell", P_BARKEEP = "rg_drinking_barkeep",
                            P_CARRY = "rg_drinking_carry";

        sealed class St
        {
            public double Drinks, At;      // the count at At (unix seconds)
            public bool Out;               // passed out now
            public int Token;              // which pass-out a scheduled wake belongs to
            public double OutAt;           // when it began
            public uint HealthAtOut;
            public int Stage;              // the last stage a line was said for
            public double BarkAt;          // the last barkeeper word
            public bool Hungover;          // the hangover after a normal wake, until rested (DrinkingRule.REST_NEEDED_S)
            public double HangoverAt, Rested, GroanAt;
            public bool HalfSaid;
            public int HToken;             // which hangover a scheduled tick belongs to
            public double PicD, PicAt, PicR;   // the picture's own count (DrinkingRule.PICTURE_S): set at PicAt, falling PicR/s
            public bool CarryOk;
            public DrinkingRule.Tavern CarryTo;
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
                Add(P_CARRY, "RevivalGuard Drinking: a drinker who passes out outdoors or in a town wakes in a random town tavern (off: wakes where they fell)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Drinking: property defaults not registered: {e.Message}"); }
            CommandManager.TryAddCommand(DrunkCommand, "drunk", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Drinking test: set your drinks count, or pass out (ours, not retail).", "[count | pass | 0]");
            Mod.Log.Info($"[RevivalGuard] Drinking: {DrinkingRule.WEIGHT.Count} drinks, {(On ? "ON" : "off")} ({P_ON}), @drunk");
        }

        static St Get(Player p) => s_State.GetOrAdd(p.Guid.Full, _ => new St { At = Now });

        static double Count(St st) => DrinkingRule.Now(st.Drinks, st.At, Now);

        static double PicNow(St st) => DrinkingRule.Picture(st.PicD, st.PicR, Now - st.PicAt);

        /// <summary>The picture shows d now and is clear secs from now.</summary>
        static void SetPicture(St st, double d, double secs)
        {
            st.PicD = Math.Max(0.0, d); st.PicAt = Now; st.PicR = DrinkingRule.PictureRate(st.PicD, secs);
        }

        static void Calm(Player p, St st)
        {
            st.Drinks = Math.Min(Count(st), DrinkingRule.BLOCK_COUNT); st.At = Now;
            st.Stage = Math.Min(st.Stage, DrinkingRule.Stage(st.Drinks));
            SetPicture(st, Math.Min(PicNow(st), DrinkingRule.BLOCK_PICTURE), DrinkingRule.BLOCK_PICTURE_S);
            Send(p, st);
        }

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
            Calm(p, Get(p));
            return false;
        }

        static DrinkingRule.Block Unsafe(Player p) =>
            DrinkingRule.PassOutBlock(p.CombatMode != CombatMode.NonCombat, HitRecently(p), HostileNear(p, DrinkingRule.HOSTILE_M), RedPlayerNear(p));

        /// <summary>Food.ApplyConsumable has used it up: count it.</summary>
        internal static void Drank(Player p, uint wcid)
        {
            if (p == null || p.IsDead || !On) return;
            var st = Get(p);
            if (DrinkingRule.IsCure(wcid))
            {
                // the diner's coffee only while its switch is on (RevivalGuard Diner, rg_diner_coffee)
                if (wcid == DrinkingRule.DINER_COFFEE && !PropertyManager.GetBool(Diner.P_COFFEE, true).Item) return;
                Cure(p, st, wcid == DrinkingRule.DINER_COFFEE ? DrinkingRule.COFFEE_CURE_LINE : DrinkingRule.CURE_LINE);
                return;
            }
            if (!DrinkingRule.IsAlcohol(wcid) || st.Out) return;
            double before = Count(st);
            st.Stage = Math.Min(st.Stage, DrinkingRule.Stage(before));   // worn off since: its line may come again
            st.Drinks = DrinkingRule.Add(before, DrinkingRule.Weight(wcid));
            st.At = Now;
            SetPicture(st, st.Drinks, DrinkingRule.PICTURE_S);
            int stage = DrinkingRule.Stage(st.Drinks);
            if (stage > st.Stage) { var line = DrinkingRule.StageLine(st.Stage, stage); if (line != null) PkCommon.Say(p, line); }
            st.Stage = stage;
            bool passes = DrinkingRule.PassesOut(st.Drinks) && PropertyManager.GetBool(P_PASSOUT, true).Item;
            // not on the drink that passes you out: its sparkle hid the Woah, and waking sobers you
            if (!passes && !DrinkingRule.OwnIntoxication(wcid) && st.Drinks >= DrinkingRule.SPELL_AT && PropertyManager.GetBool(P_SPELL, true).Item)
                Cast(p, DrinkingRule.INTOX_I);
            Send(p, st);
            if (stage >= 2) Barkeep(p, st, stage - 2);
            if (passes)
            {
                // after the drink's own motion has played out
                var chain = new ActionChain();
                chain.AddDelaySeconds(1.5);
                chain.AddAction(p, () => { try { PassOut(p, st, false); } catch (Exception e) { TickGuard.Report("Drinking", e); } });
                chain.EnqueueChain();
            }
        }

        // ---- out cold ----------------------------------------------------------------------------------------------

        public const string FIRST_DRAWING_CLIENT = "0.1.116";
        static bool LiesItselfDown(Player p) => ModChannel.Capable(p.Session)
            && ModChannel.CompareVersions(ModChannel.ClientVersion(p.Session), FIRST_DRAWING_CLIENT) >= 0;

        /// <summary>The first client that plays the stumble itself.</summary>
        public const string FIRST_STUMBLE_CLIENT = "0.1.117";
        static bool Stumbles(Player p) => ModChannel.Capable(p.Session)
            && ModChannel.CompareVersions(ModChannel.ClientVersion(p.Session), FIRST_STUMBLE_CLIENT) >= 0;

        static void PassOut(Player p, St st, bool forced)
        {
            if (p.IsDead || p.Session == null || st.Out) return;
            // the world may have changed during the drink: a monster that walked up keeps you on your feet
            if (!forced && Unsafe(p) != DrinkingRule.Block.None)
            {
                PkCommon.Say(p, "You sway, but the danger nearby keeps you on your feet. The fright sobers you up a little.");
                Calm(p, st);
                return;
            }
            st.Out = true; st.Token++; st.OutAt = Now; st.HealthAtOut = p.Health.Current;
            st.CarryOk = CarryPossible(p); st.CarryTo = null;
            int token = st.Token;
            bool stumbles = Stumbles(p), selfDown = LiesItselfDown(p);
            double fallAt = stumbles ? DrinkingRule.FALL_AT_S : selfDown ? 0.0 : DrinkingRule.SERVER_WOAH_S;
            double lead = stumbles ? DrinkingRule.STUMBLE_S : fallAt;
            if (fallAt > 0) Others(p, string.Format(DrinkingRule.Pick(DrinkingRule.SWAY_OTHERS, s_Rng.Next()), p.Name));
            // Woah is the server's for everyone
            if (stumbles || !selfDown) p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Woah));
            // a carry adds portal space before the wake: the client's own fallback wake must wait for it
            Send(p, st, DrinkingRule.OUT_S + lead + (st.CarryOk ? DrinkingRule.CARRY_EXTRA_S : 0), stumble: stumbles ? DrinkingRule.STUMBLE_S : 0);
            After(p, fallAt, () =>
            {
                if (!st.Out || st.Token != token || p.IsDead) return;
                PkCommon.Say(p, DrinkingRule.PASS_LINE);
                Others(p, string.Format(DrinkingRule.Pick(DrinkingRule.FALL_OTHERS, s_Rng.Next()), p.Name));
                if (!stumbles && !selfDown) p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.PossumState));
                Barkeep(p, st, 2, force: true);
            });
            Mod.Log.Info($"[RevivalGuard] Drinking: {p.Name} passed out{(forced ? " (@drunk pass)" : "")} ({(stumbles ? "stumbles" : selfDown ? "lies down" : "server shows it")})");
            Watch(p, st, token, 1, (int)Math.Ceiling(DrinkingRule.OUT_S + lead));
        }

        /// <summary>Run a on the player's chain after secs (at once for 0).</summary>
        static void After(Player p, double secs, Action a)
        {
            if (secs <= 0) { try { a(); } catch (Exception e) { TickGuard.Report("Drinking", e); } return; }
            var chain = new ActionChain();
            chain.AddDelaySeconds(secs);
            chain.AddAction(p, () => { try { a(); } catch (Exception e) { TickGuard.Report("Drinking", e); } });
            chain.EnqueueChain();
        }

        /// <summary>Once a second while out (the stumble included): a hit or a monster closing in wakes you; else wake after total seconds.</summary>
        static void Watch(Player p, St st, int token, int second, int total)
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
                    if (hurt || HostileNear(p, 12f) || RedPlayerNear(p)) { Wake(p, st, early: true); return; }
                    if (second >= total) { if (!Carry(p, st, token)) Wake(p, st, early: false); return; }
                    Watch(p, st, token, second + 1, total);
                }
                catch (Exception e) { TickGuard.Report("Drinking", e); }
            });
            chain.EnqueueChain();
        }

        static void Wake(Player p, St st, bool early, DrinkingRule.Tavern carried = null)
        {
            st.Out = false; st.Drinks = 0; st.At = Now; st.Stage = 0; st.CarryTo = null;
            SetPicture(st, 0, 0);
            Sober(p);
            string line = early ? DrinkingRule.WAKE_EARLY_LINE
                        : carried != null ? string.Format(DrinkingRule.Pick(DrinkingRule.CARRY_LINES, s_Rng.Next()), carried.Town)
                        : DrinkingRule.WakeLine(s_Rng.Next());
            PkCommon.Say(p, line);
            Others(p, string.Format(DrinkingRule.WAKE_OTHERS, p.Name));
            if (!LiesItselfDown(p)) p.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, MotionCommand.Ready));
            if (early)
            {
                // woken by danger: on your feet at once, no health taken, only the short hangover
                Send(p, st, 0, wake: true, line: line, early: true);
                Hangover(p);
                return;
            }
            int cur = (int)p.Health.Current, after = DrinkingRule.HealthAfterWaking(cur, (int)p.Health.MaxValue);
            if (after < cur) p.UpdateVital(p.Health, after);
            Send(p, st, 0, wake: true, line: line, hangover: DrinkingRule.HANGOVER_MAX_S);
            StartHangover(p, st);
            if (!BriefClient(p))
            {
                int h = st.HToken;
                After(p, DrinkingRule.OLD_CLIENT_HANGOVER_VIEW_S, () => { if (st.Hungover && st.HToken == h && p.Session != null) Send(p, st, hangover: 0); });
            }
        }

        /// <summary>The first client whose own picture is brief by design (a hard cap, no hangover sway past a few seconds).</summary>
        public const string FIRST_BRIEF_CLIENT = "0.1.118";
        static bool BriefClient(Player p) => ModChannel.Capable(p.Session)
            && ModChannel.CompareVersions(ModChannel.ClientVersion(p.Session), FIRST_BRIEF_CLIENT) >= 0;

        // ---- carried off to a tavern ---------------------------------------------------------------------------------

        /// <summary>May this pass-out end in a tavern: the switch, and an outdoor block (or a building on one).</summary>
        static bool CarryPossible(Player p)
        {
            if (!PropertyManager.GetBool(P_CARRY, true).Item || DrinkingRule.TAVERNS.Length == 0) return false;
            var lb = p.CurrentLandblock;
            return lb != null && !lb.IsDungeon && p.Location != null;
        }

        /// <summary>At the end of out cold: carry the body to a tavern and wake it there.</summary>
        static bool Carry(Player p, St st, int token)
        {
            if (!st.CarryOk || p.Location == null) return false;
            bool pk = p.IsPKType;
            var t = DrinkingRule.ChooseTavern(DrinkingRule.TAVERNS, x => TavernSafe(p, x, pk), p.Level ?? 1, p.Location.Landblock, s_Rng.Next());
            if (t == null) { Mod.Log.Info($"[RevivalGuard] Drinking: {p.Name} wakes where they fell (no safe tavern)"); return false; }
            st.CarryTo = t;
            Others(p, string.Format(DrinkingRule.CARRY_GONE_OTHERS, p.Name));
            var to = new ACE.Entity.Position(t.Cell, new System.Numerics.Vector3(t.X, t.Y, t.Z), new System.Numerics.Quaternion(0, 0, t.Qz, t.Qw));
            Mod.Log.Info($"[RevivalGuard] Drinking: {p.Name} carried to {t.Town} ({t.Barkeep}, 0x{t.Cell:X8})");
            p.Teleport(to);
            CarryWatch(p, st, token, t, 1, -1);
            return true;
        }

        /// <summary>No raid on the town, and for a player killer no other red player within CARRY_PK_BLOCKS land blocks.</summary>
        static bool TavernSafe(Player p, DrinkingRule.Tavern t, bool pk)
        {
            uint lb = t.Cell >> 16;
            if (lb == 0xA9B4 && TownRaid.Running) return false;
            if (!pk) return true;
            foreach (var o in PlayerManager.GetAllOnline())
                if (o != null && o != p && o.IsPK && !o.IsDead && o.Location != null
                    && DrinkingRule.BlockDistance(o.Location.Landblock, lb) <= DrinkingRule.CARRY_PK_BLOCKS) return false;
            return true;
        }

        /// <summary>Once a second while carried</summary>
        static void CarryWatch(Player p, St st, int token, DrinkingRule.Tavern t, int second, int arrivedAt)
        {
            After(p, 1.0, () =>
            {
                if (!st.Out || st.Token != token) return;
                if (p.IsDead || p.Session == null) { Clear(p, st); return; }
                bool there = !p.Teleporting && p.CurrentLandblock != null && p.Location != null && p.Location.Landblock == (t.Cell >> 16);
                if (there && arrivedAt < 0) { arrivedAt = second; Others(p, string.Format(DrinkingRule.CARRY_ARRIVE_OTHERS, p.Name)); }
                if (there && (p.Health.Current < st.HealthAtOut || HostileNear(p, 12f))) { Wake(p, st, early: true); return; }
                if (there && second - arrivedAt >= DrinkingRule.CARRY_WAKE_AFTER_S) { Wake(p, st, early: false, carried: t); return; }
                if (!there && second >= DrinkingRule.CARRY_TIMEOUT_S) { Wake(p, st, early: false); return; }
                CarryWatch(p, st, token, t, second + 1, arrivedAt);
            });
        }

        // ---- the hangover: until you rest ------------------------------------------------------------------------------

        /// <summary>Self, Coordination and Quickness -10, held until you have rested DrinkingRule.REST_NEEDED_S in all, or HANGOVER_MAX_S</summary>
        static void StartHangover(Player p, St st)
        {
            foreach (var id in new[] { DrinkingRule.HANGOVER_SPELL, DrinkingRule.HANGOVER_COORD, DrinkingRule.HANGOVER_QUICK })
                Hold(p, id, DrinkingRule.HANGOVER_MAX_S);
            st.Hungover = true; st.HangoverAt = Now; st.Rested = 0; st.HalfSaid = false; st.GroanAt = Now; st.HToken++;
            PkCommon.Say(p, DrinkingRule.HANGOVER_REST_LINE, ChatMessageType.Magic);
            HangoverTick(p, st, st.HToken);
        }

        static void HangoverTick(Player p, St st, int token)
        {
            After(p, DrinkingRule.HANGOVER_TICK_S, () =>
            {
                if (!st.Hungover || st.HToken != token) return;
                if (p.Session == null) return;   // logged off: Hello picks it up again
                if (p.IsDead) { Lift(p, st, null); return; }
                bool combat = p.CombatMode != CombatMode.NonCombat;
                var md = p.CurrentMovementData;
                uint fwd = md != null && md.MovementType == MovementType.Invalid && md.Invalid != null ? (uint)md.Invalid.State.ForwardCommand : 0u;
                bool resting = DrinkingRule.Resting(fwd, combat);
                if (resting) st.Rested += DrinkingRule.HANGOVER_TICK_S;
                if (!st.HalfSaid && st.Rested >= DrinkingRule.REST_NEEDED_S / 2) { st.HalfSaid = true; PkCommon.Say(p, DrinkingRule.HANGOVER_HALF_LINE, ChatMessageType.Magic); }
                if (DrinkingRule.HangoverOver(st.Rested, Now - st.HangoverAt)) { Lift(p, st, DrinkingRule.HANGOVER_OVER_LINE); return; }
                if (!resting && !combat && Now - st.GroanAt >= DrinkingRule.GROAN_GAP_S)
                {
                    st.GroanAt = Now;
                    PkCommon.Say(p, DrinkingRule.Pick(DrinkingRule.GROAN_LINES, s_Rng.Next()));
                    if (s_Rng.NextDouble() < 0.5) Others(p, string.Format(DrinkingRule.GROAN_OTHERS, p.Name));
                }
                HangoverTick(p, st, token);
            });
        }

        /// <summary>The hangover is over: its three spells go (only ours: a held one of the same id would be ours too).</summary>
        static void Lift(Player p, St st, string line)
        {
            st.Hungover = false; st.HToken++;
            foreach (var id in new[] { DrinkingRule.HANGOVER_SPELL, DrinkingRule.HANGOVER_COORD, DrinkingRule.HANGOVER_QUICK })
            {
                var e = p.EnchantmentManager.GetEnchantment(id);
                if (e != null) p.EnchantmentManager.Dispel(e);
            }
            if (line != null) PkCommon.Say(p, line, ChatMessageType.Magic);
            if (p.Session != null) Send(p, st, hangover: 0);
        }

        /// <summary>Dead, logged off or gone while out: nothing more to show; the count goes.</summary>
        static void Clear(Player p, St st)
        {
            st.Out = false; st.Drinks = 0; st.At = Now; st.Stage = 0; st.CarryTo = null;
            SetPicture(st, 0, 0);
            if (p.Session != null) Send(p, st);
        }

        internal static void Died(Player p)
        {
            if (p == null || !s_State.TryGetValue(p.Guid.Full, out var st)) return;
            if (st.Out) st.Token++;
            Clear(p, st);
        }

        /// <summary>Ulgrim's Hangover Cure (retail 23924, it restores health): ours, it also sobers you and lifts the hangover.</summary>
        static void Cure(Player p, St st, string line)
        {
            if (st.Out) return;
            bool had = Count(st) > 0.01 || st.Hungover || p.EnchantmentManager.GetEnchantment(DrinkingRule.HANGOVER_SPELL) != null
                       || p.EnchantmentManager.GetEnchantment(DrinkingRule.INTOX_I) != null;
            st.Drinks = 0; st.At = Now; st.Stage = 0;
            SetPicture(st, 0, 0);
            Sober(p);
            Lift(p, st, null);
            if (had) PkCommon.Say(p, line);
        }

        static void Sober(Player p)
        {
            foreach (var id in new[] { DrinkingRule.INTOX_I, DrinkingRule.INTOX_II, DrinkingRule.INTOX_III })
            {
                var e = p.EnchantmentManager.GetEnchantment(id);
                if (e != null) p.EnchantmentManager.Dispel(e);
            }
        }

        /// <summary>Woken early (danger): only Feeblemind Self I (Self -10), held for HANGOVER_S instead of its own 60 s.</summary>
        static void Hangover(Player p)
        {
            if (Hold(p, DrinkingRule.HANGOVER_SPELL, DrinkingRule.HANGOVER_S)) PkCommon.Say(p, DrinkingRule.HANGOVER_LINE, ChatMessageType.Magic);
        }

        /// <summary>A self spell on the player, held secs instead of its own length.</summary>
        static bool Hold(Player p, uint id, double secs)
        {
            try
            {
                var spell = new Spell(id);
                if (spell.NotFound) return false;
                var r = p.EnchantmentManager.Add(spell, p, null);
                if (r?.Enchantment == null) return false;
                r.Enchantment.Duration = secs;
                p.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(p.Session, new ACE.Server.Network.Structure.Enchantment(p, r.Enchantment)));
                p.HandleSpellHooks(spell);
                return true;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Drinking: hold {id}: {e.Message}"); return false; }
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
                    if (DrinkingRule.IsHit(e.Amount, e.Attacker == p.Guid, e.Attacker == ACE.Entity.ObjectGuid.Invalid)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>A living monster (attackable, not a player or a pet) within m metres, or one anywhere in sight that is after you.</summary>
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

        static void Send(Player p, St st, double outSecs = 0, bool wake = false, string line = null, bool early = false,
                         double stumble = 0, double hangover = -1)
        {
            if (p?.Session == null || !ModChannel.Capable(p.Session)) return;
            var ci = CultureInfo.InvariantCulture;
            // the picture's own count and fall (brief by design), not the slow count: every client extrapolates d - r * t
            string json = "{\"d\":" + PicNow(st).ToString("0.###", ci) + ",\"r\":" + st.PicR.ToString("0.######", ci)
                        + ",\"p\":" + DrinkingRule.PASS.ToString("0.##", ci) + ",\"o\":" + outSecs.ToString("0.#", ci)
                        + (stumble > 0 ? ",\"s\":" + stumble.ToString("0.##", ci) : "")
                        + ",\"w\":" + (wake ? 1 : 0) + (early ? ",\"e\":1" : "") + (line != null ? ",\"l\":" + Telemetry.J(line) : "")
                        + (hangover >= 0 ? ",\"hk\":1,\"h\":" + hangover.ToString("0", ci) : "") + "}";
            ModChannel.Send(p.Session, "drunk", json);
        }

        /// <summary>From ModChannel's hello: a character that relogs while drunk keeps its sway, and one hung over keeps the hangover.</summary>
        internal static void Hello(Session session)
        {
            var p = session?.Player;
            if (p == null || !On) return;
            // no state: a client that kept a stale count is told it is sober
            if (!s_State.TryGetValue(p.Guid.Full, out var st)) { Send(p, new St { At = Now }); return; }
            // a relog while out cold (or being carried) ends it: the client starts fresh, so the count goes too
            if (st.Out) { st.Token++; st.Out = false; st.Drinks = 0; st.At = Now; st.CarryTo = null; SetPicture(st, 0, 0); }
            if (st.Hungover)
            {
                double left = DrinkingRule.HANGOVER_MAX_S - (Now - st.HangoverAt);
                if (left <= 0) { Lift(p, st, DrinkingRule.HANGOVER_OVER_LINE); return; }
                st.HToken++;
                HangoverTick(p, st, st.HToken);
                Send(p, st, hangover: BriefClient(p) ? left : 0);
                return;
            }
            Send(p, st);
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
                SetPicture(st, st.Drinks, DrinkingRule.PICTURE_S);
                if (n <= 0) { Sober(p); Lift(p, st, null); }
                Send(p, st);
            }
            string ver = ModChannel.ClientVersion(session) is var v && v.Length > 0 ? v : "no version";
            PkCommon.Say(p, $"[Drinking] count {Count(st):0.00} of {DrinkingRule.PASS} (stage {DrinkingRule.Stage(Count(st))}, out {st.Out}); "
                          + $"picture {PicNow(st):0.00} (clear in {(st.PicR > 0 ? PicNow(st) / st.PicR : 0):0}s); "
                          + $"carry {(PropertyManager.GetBool(P_CARRY, true).Item ? "on" : "off")} ({DrinkingRule.TAVERNS.Length} taverns{(st.CarryTo != null ? ", now to " + st.CarryTo.Town : "")}); "
                          + $"hangover {(st.Hungover ? $"{Now - st.HangoverAt:0}s in, rested {st.Rested:0}s of {DrinkingRule.REST_NEEDED_S:0}" : "no")}; "
                          + $"switch {P_ON} {(On ? "ON" : "off")}; this client ({ver}) "
                          + (Stumbles(p) ? "plays the stumble" : LiesItselfDown(p) ? "lies itself down (no stumble before " + FIRST_STUMBLE_CLIENT + ")" : "is shown it by the server (needs " + FIRST_DRAWING_CLIENT + ")"));
        }
    }

    /// <summary>A drink is refused before its motion when it would pass you out somewhere unsafe, or while out cold.</summary>
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

    /// <summary>Player.Die (declared on Player): a death sobers you.</summary>
    [HarmonyPatch]
    static class DrinkingDeath
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(typeof(Player), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) });
        static bool Prepare()
        {
            if (TargetMethod() != null) return true;
            Mod.Log.Warn("[RevivalGuard] Drinking: Player.Die not found; a death does not sober");
            return false;
        }
        static void Postfix(Player __instance)
        {
            try { Drinking.Died(__instance); }
            catch (Exception e) { TickGuard.Report("Drinking", e); }
        }
    }

    /// <summary>The drink went down: count it.</summary>
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
                if ((DrinkingRule.IsAlcohol(w) || DrinkingRule.IsCure(w))
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
