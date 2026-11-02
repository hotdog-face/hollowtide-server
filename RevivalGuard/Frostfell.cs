using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// FROSTFELL, REVIVED (2026-10-29, for the owner's review before Dec 4; docs/EVENT-TESTS/frostfell.md). Retail's
    /// Frostfell content stays as it was; this adds on top, only while Frostfell runs (SeasonalCalendar, forced included):
    ///   * ONE LINE PER GIFT BOX: retail's "3 of 12 gift boxes found." and our "Present Hunt: 3 of 6" become one line
    ///     (FrostfellRule.BoxLine). The retail line is held back only while the Present Hunt will count this box.
    ///   * THE COSTUMER SAYS WHY: the Frostfell Costumer (900731, a vendor) tells a player, once a day, what Frostfell
    ///     holds and where (the boxes, Raymond, Frost Haven, Osdryd, the carols), then the Present Hunt count each visit.
    ///   * THE ABOMINABLE SNOWMAN SCALES (FrostfellRule.SnowmanMax): fun for one and for ten. When he falls, everyone who
    ///     hurt him gets Frostfell Cheer sized by how many fought, and the line that his Frozen Presents melt with a
    ///     Hot Coal from the Scold Lumps around him (retail's own loop, never told in game).
    ///   * CAROLS AT THE GARLAND ARCH (new, for the owner's yes; switch rg_frostfell_carols, default on): every CAROL_S while
    ///     someone stands at Holtburg's arch, the Costumer calls a cheer, wave or clap; all who join within WINDOW_S get
    ///     Frostfell Cheer, bigger with more carolers. A small group activity for any level, in the newbie town.
    ///   * FROSTFELL CHEER: creature XP for an hour (FrostfellRule.Cheer), never stacked over the Night Club party buff:
    ///     the bigger of the two counts.
    /// Staff: @frostfell [status | carol [cheer|wave|clap] | snowman | reset] (reset: your own boxes, hunt and deliveries).
    /// </summary>
    static class Frostfell
    {
        internal const string KEY = "frostfell";
        const uint COSTUMER = 900731, SNOWMAN = 87120;
        const ushort HOLTBURG = 0xA9B4, FROST_HAVEN = 0x9F29;
        // the Garland Arch over the road, 0xA9B40021 (108, 12) (2026-12-01-frostfell-3-arch.sql): landblock-grid metres
        const float ARCH_X = 0xA9 * 192f + 108f, ARCH_Y = 0xB4 * 192f + 12f, ARCH_R = 25f;
        const double CAROL_S = 240, FIRST_CAROL_S = 30, WINDOW_S = 20, INTRO_S = 20 * 3600;
        const string BOX_LINE = "%tqc of %tqm gift boxes found.";
        internal const string P_CAROLS = "rg_frostfell_carols";
        internal const PropertyFloat CHEER_PCT = (PropertyFloat)29150;
        internal const PropertyInt64 CHEER_UNTIL = (PropertyInt64)29151;

        internal static bool On => SeasonalCalendar.IsFestivalActive(KEY);
        static double Now => ACE.Common.Time.GetUnixTime();
        static readonly Random s_Rng = new Random();

        internal static void Register()
        {
            try
            {
                var b = typeof(ReadOnlyDictionary<string, Property<bool>>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Select(f => f.GetValue(DefaultPropertyManager.DefaultBooleanProperties)).OfType<IDictionary<string, Property<bool>>>().FirstOrDefault();
                if (b != null && !b.ContainsKey(P_CAROLS)) b[P_CAROLS] = new Property<bool>(true, "Frostfell: the Costumer calls carols at Holtburg's Garland Arch (Frostfell Cheer)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell: {P_CAROLS} not registered with @modifybool ({e.Message}); it is still read"); }
            CommandManager.TryAddCommand(Cmd, "frostfell", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "Frostfell: status; 'carol [cheer|wave|clap]' calls a carol now; 'snowman' his scaling; 'reset' clears your own boxes, hunt and deliveries.",
                "[status | carol [cheer|wave|clap] | snowman | raid | reset]");
            Mod.Log.Info($"[RevivalGuard] Frostfell: one gift-box line, the Costumer's intro, the Snowman scales ({FrostfellRule.SOLO:N0} + {FrostfellRule.PER:N0} a fighter), "
                + $"carols {(CarolsOn ? "on" : "off")} ({P_CAROLS}); @frostfell");
        }

        static bool CarolsOn
        {
            get { try { return PropertyManager.GetBool(P_CAROLS, true).Item; } catch { return true; } }
        }

        // ---- 1. one gift-box line ---------------------------------------------------------------------------------

        static readonly ConcurrentDictionary<uint, (string text, double at)> s_BoxLine = new ConcurrentDictionary<uint, (string, double)>();

        /// <summary>Before ACE runs an emote action: hold retail's gift-box count back when the Present Hunt will count this
        /// box too, so SayBoxProgress can say both in one line. False skips ACE's action.</summary>
        internal static bool OnEmote(EmoteManager mgr, PropertiesEmote set, PropertiesEmoteAction a, WorldObject target)
        {
            if (a != null && target is Player pz && mgr?.WorldObject != null && PUZZLE.Contains(mgr.WorldObject.WeenieClassId)) PuzzleStep(pz, set, a);
            if (a == null || a.Type != (uint)EmoteType.DirectBroadcast || a.Message != BOX_LINE || !(target is Player p)) return true;
            var g = FestivalGoals.ByCounter("PresentHunt");
            if (g == null || !FestivalGoals.Running(g) || FestivalGoals.TrophyTaken(p, g) || !FestivalGoals.HasRow(g.Counter)) return true;
            FestivalGoals.ResetIfStale(p.QuestManager, g);
            if (p.QuestManager.GetCurrentSolves(g.Counter) >= g.N) return true;   // the hunt is done: retail's line alone
            var text = mgr.Replace(a.Message, mgr.WorldObject, target, set?.Quest);
            s_BoxLine[p.Guid.Full] = (text, Now);
            // the Present Hunt stamp follows in the same emote; if it never moves, retail's line goes out as it was
            var chain = new ActionChain();
            chain.AddDelaySeconds(4);
            chain.AddAction(p, () => { if (s_BoxLine.TryRemove(p.Guid.Full, out var t)) Say(p, t.text); });
            chain.EnqueueChain();
            return false;
        }

        /// <summary>From FestivalGoalsStamp, after a goal's count moved: a gift box's one line. True when said.</summary>
        internal static bool SayBoxProgress(Player p, FestivalGoals.Goal g, int n)
        {
            if (p == null || g?.Counter != "PresentHunt" || !s_BoxLine.TryRemove(p.Guid.Full, out var t)) return false;
            if (Now - t.at > 10) return false;
            Say(p, FrostfellRule.BoxLine(t.text, n, g.N, FestivalGoals.PrizeName(g)));
            return true;
        }

        // ---- 1b. Santa's present puzzle: a wrong guess closes the first present again ------------------------------

        // the sixteen presents of the five patterns (88349 and its siblings spawn them)
        static readonly HashSet<uint> PUZZLE = new HashSet<uint>(Enumerable.Range(88332, 8).Concat(Enumerable.Range(88350, 8)).Select(i => (uint)i));
        static readonly ConcurrentDictionary<uint, int> s_FirstGuess = new ConcurrentDictionary<uint, int>();

        /// <summary>
        /// Ticket 261030-0015 ("not all the presents open"): retail's data opens the first present of a pair (its bit in
        /// SclavusPresentsOpened, and SclavusPresentFirstGuess) but a wrong second guess clears neither, so the first one
        /// stays "already opened" and can never be matched, and the next present is judged against it. Here a wrong guess
        /// closes the first present again and forgets the first guess, as a memory game should.
        /// </summary>
        static void PuzzleStep(Player p, PropertiesEmote set, PropertiesEmoteAction a)
        {
            if (a.Type == (uint)EmoteType.SetQuestBitsOn && a.Message == "SclavusPresentsOpened" && set?.Quest == "SclavusPresentFirstGuess")
                s_FirstGuess[p.Guid.Full] = (int)(a.Amount ?? 0);
            else if (a.Type == (uint)EmoteType.DirectBroadcast && a.Message == "Sorry this was not a match.")
            {
                if (s_FirstGuess.TryRemove(p.Guid.Full, out int bits) && bits > 0)
                    p.QuestManager.SetQuestBits("SclavusPresentsOpened", bits, false);
                p.QuestManager.Erase("SclavusPresentFirstGuess");
                Say(p, "The first present closes up again. Open any two to try another pair.");
            }
        }

        /// <summary>The imposter Santa Sclavus falls (tickets 261030-0018/0019): each fighter gets a Present to unwrap
        /// (his corpse also carries treasure now, gen_frostfell_santa.py).</summary>
        internal static void ImposterFell(Creature c)
        {
            var fighters = c.DamageHistory?.TotalDamage.Values.ToList()
                .Where(i => i != null && i.TotalDamage > 0).Select(i => i.TryGetPetOwnerOrAttacker() as Player).Where(x => x != null).Distinct().ToList();
            if (fighters == null) return;
            foreach (var p in fighters)
            {
                var pl = p;
                Later(pl, () =>
                {
                    var gift = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(36897);
                    if (gift != null && pl.TryCreateInInventoryWithNetworking(gift))
                        Say(pl, "The imposter's sack bursts: one of the stolen presents is yours. The real Santa Sclavus waits behind the door.");
                    else { gift?.Destroy(); Say(pl, "The imposter's sack bursts, but your pack is too full for a present. The real Santa waits behind the door."); }
                });
            }
        }

        // ---- 2. the Costumer says why -----------------------------------------------------------------------------

        static readonly ConcurrentDictionary<uint, double> s_Told = new ConcurrentDictionary<uint, double>();

        internal static void OnCostumer(Vendor v, Player p)
        {
            if (!On || v?.WeenieClassId != COSTUMER || p == null || p.IsBusy) return;
            var g = FestivalGoals.ByCounter("PresentHunt");
            int n = g != null ? p.QuestManager.GetCurrentSolves(g.Counter) : 0;
            bool taken = g != null && FestivalGoals.TrophyTaken(p, g);
            if (g != null && !taken && n >= g.N) return;          // her own hand-in speaks (FestivalGoals' Use set)
            double now = Now;
            bool intro = !s_Told.TryGetValue(p.Guid.Full, out var at) || now - at > INTRO_S;
            var lines = new List<string>();
            if (intro)
            {
                s_Told[p.Guid.Full] = now;
                lines.Add("Merry Frostfell! Twelve gift boxes are hidden across Dereth this winter. Find six of them and come back to me "
                    + "for a snow globe for your house. The twelfth box you find holds a pet.");
                lines.Add("Raymond the Gift Gopher, far north at 95.7N 56.5W by the Fiun Outpost (the Town Network Annex), needs four "
                    + "packages delivered in 30 minutes. He pays with a title and a holiday gift.");
                lines.Add("Frost Haven, at 68.5S 25.4E, is under assault by the Abominable Snowman. He grows with every fighter, so bring "
                    + "friends or go alone. When he falls the Scold rise from the snow: break them into Scold Lumps for the Hot Coals that melt his Frozen Presents.");
                lines.Add("Brave souls of level 180 and up can help Osdryd in Sawato. Your Journal's Seasons tab shows where everything is.");
                if (CarolsOn) lines.Add("And stay by the Garland Arch: when I call a carol, join in for Frostfell Cheer, more experience from creatures.");
            }
            if (g != null && FestivalGoals.Running(g))
                lines.Add(taken ? "You have this year's snow globe already. Raymond and Frost Haven still need you!"
                                : $"Present Hunt: {n} of {g.N} gift boxes found. {g.N - n} more for your {FestivalGoals.PrizeName(g)}.");
            var chain = new ActionChain();
            foreach (var l in lines)
            {
                var line = l;
                chain.AddAction(v, () => p.Session?.Network?.EnqueueSend(new GameMessageHearSpeech(line, v.Name, v.Guid.Full, ChatMessageType.Tell)));
                chain.AddDelaySeconds(1.5);
            }
            chain.EnqueueChain();
        }

        // ---- 3. the Abominable Snowman scales ---------------------------------------------------------------------

        sealed class Fight { public uint RetailMax; public readonly HashSet<uint> Who = new HashSet<uint>(); public readonly HashSet<uint> Accounts = new HashSet<uint>(); }
        static readonly ConditionalWeakTable<Creature, Fight> s_Fights = new ConditionalWeakTable<Creature, Fight>();

        internal static void OnDamage(EmoteManager mgr, Creature attacker)
        {
            if (!(mgr?.WorldObject is Creature c) || c.WeenieClassId != SNOWMAN || c.IsDead || !On) return;
            var who = attacker is Player p ? p : (attacker as CombatPet)?.P_PetOwner;
            if (who == null) return;
            var f = s_Fights.GetValue(c, _ => new Fight { RetailMax = c.Health.MaxValue });
            int n;
            lock (f)
            {
                if (!f.Who.Add(who.Guid.Full)) return;
                f.Accounts.Add(who.Session?.AccountId ?? who.Guid.Full);
                n = f.Who.Count;
            }
            uint oldMax = c.Health.MaxValue, want = FrostfellRule.SnowmanMax(n, f.RetailMax);
            if (want == oldMax) return;
            if (want > oldMax || n == 1)
            {
                var (max, cur) = FrostfellRule.Resize(oldMax, c.Health.Current, want);
                c.Health.StartingValue = (uint)Math.Max(1, (long)c.Health.StartingValue + max - oldMax);   // MaxValue = StartingValue + Endurance/2
                c.UpdateVital(c.Health, Math.Min(cur, c.Health.MaxValue));
            }
            Mod.Log.Info($"[RevivalGuard] Frostfell: the Abominable Snowman 0x{c.Guid.Full:X8}: fighter {n} ({who.Name}); max health {oldMax:N0} -> {c.Health.MaxValue:N0}, now {c.Health.Current:N0}");
            if (n > 1)
                foreach (var pl in Near(c, 60f))
                    Say(pl, $"The Abominable Snowman swells with the cold: {n} fighters against him now.");
        }

        internal static void OnDeath(Creature c)
        {
            if (c != null && On && PRESENTS.Contains(c.WeenieClassId)) { PresentBroken(c); return; }
            if (c != null && On && c.WeenieClassId == 88395) { ImposterFell(c); return; }
            if (c == null || c.WeenieClassId != SNOWMAN || !On || c.DamageHistory == null) return;
            var fighters = c.DamageHistory.TotalDamage.Values.ToList()
                .Where(i => i != null && i.TotalDamage > 0).Select(i => i.TryGetPetOwnerOrAttacker() as Player).Where(x => x != null).Distinct().ToList();
            int crowd = fighters.Select(x => x.Session?.AccountId ?? x.Guid.Full).Distinct().Count();
            double pct = FrostfellRule.Cheer(crowd);
            foreach (var p in fighters)
            {
                var pl = p;
                Later(pl, () =>
                {
                    Say(pl, "The Abominable Snowman is broken! His corpse holds Frozen Presents. The Scold rise from the snow now: break them down "
                        + "to Scold Lumps, which carry Hot Coals. Use a Hot Coal on a Frozen Present to melt it.");
                    Grant(pl, pct, crowd, crowd > 1 ? $"{crowd} of you brought him down" : "you brought him down alone");
                });
            }
            Mod.Log.Info($"[RevivalGuard] Frostfell: the Abominable Snowman 0x{c.Guid.Full:X8} fell to {fighters.Count} player(s); Frostfell Cheer {pct:P0}");
        }

        // ---- 3b. the Present Raids scale with their defenders -----------------------------------------------------

        // Tufa's Gurogs (Grump, Mastermind, Max) and Yaraq's Drudge Pilferers; the presents they go for
        static readonly HashSet<uint> RAIDERS = new HashSet<uint> { 73214, 73215, 73216, 73228 };
        // Tufa's attackable Holiday Present and Yaraq's pile (73229 is the pile's usable reward twin, not the raid's target)
        static readonly HashSet<uint> PRESENTS = new HashSet<uint> { 73218, 73226 };
        static readonly ushort[] RAID_BLOCKS = { 0x866C, 0x7D64 };
        sealed class Scaled { }
        static readonly ConditionalWeakTable<Creature, Scaled> s_Scaled = new ConditionalWeakTable<Creature, Scaled>();
        static double s_NextRaid;

        /// <summary>Each raider and present, once, the moment it is seen: raiders' health by FrostfellRule.RaiderShare,
        /// the present's by FrostfellRule.PresentShare, for the defenders within RAID_R of it then.</summary>
        // the running raid's pacing and countdown (tickets 261030-0011, -0013)
        static readonly HashSet<uint> WAVE_GENS = new HashSet<uint> { 73218, 73226 };      // the raids' wave generators
        static readonly HashSet<uint> WAVES = new HashSet<uint> { 73220, 73222, 73231 };   // what they spawn: one wave each
        static string s_RaidKey;
        static FrostfellRule.RaidPacing s_Pacing = new FrostfellRule.RaidPacing();
        static int s_LastMark;

        /// <summary>A wave generator's spawn is held while the defenders have their calm (GeneratorProfile.ProcessQueue).</summary>
        internal static bool HoldWave(GeneratorProfile prof)
        {
            if (prof?.Generator == null || !WAVE_GENS.Contains(prof.Generator.WeenieClassId)) return false;
            if (prof.Biota == null || !WAVES.Contains(prof.Biota.WeenieClassId)) return false;
            return s_RaidKey != null && s_Pacing.Calm(Now);
        }

        static void RaidDirector(double now)
        {
            var ev = EventDirector.CurrentEvent();
            string key = ev?.key != null && ev.Value.key.StartsWith("presents-", StringComparison.OrdinalIgnoreCase) ? ev.Value.key : null;
            if (key != s_RaidKey) { s_RaidKey = key; s_Pacing = new FrostfellRule.RaidPacing(); s_LastMark = 99; }
            if (key == null) return;
            var lb = IfLoaded((ushort)(key.EndsWith("tufa") ? 0x866C : 0x7D64));
            if (lb == null) return;
            var cs = lb.GetAllWorldObjectsForDiagnostics().OfType<Creature>().Where(c => !c.IsDead).ToList();
            var present = cs.FirstOrDefault(c => PRESENTS.Contains(c.WeenieClassId));
            int raiders = cs.Count(c => RAIDERS.Contains(c.WeenieClassId));
            var defenders = present != null ? Near(present, FrostfellRule.RAID_R).Where(p => !Mercenaries.IsMerc(p)).ToList() : new List<Player>();
            int n = defenders.Select(p => p.Session?.AccountId ?? p.Guid.Full).Distinct().Count();
            var turn = s_Pacing.Update(now, raiders, n);
            if (turn == "calm") foreach (var p in defenders) Say(p, $"The raiders fall back! Catch your breath: the next wave comes in {FrostfellRule.RaidPacing.CALM_S:0} seconds.");
            else if (turn == "wave") foreach (var p in defenders) Say(p, "Here they come again! Defend the Holiday Present!");
            if (present == null) return;
            int mins = (int)Math.Ceiling((ev.Value.endsAt - now) / 60);
            foreach (var mark in FrostfellRule.TIMER_MARKS)
                if (mins <= mark && s_LastMark > mark)
                {
                    s_LastMark = mark;
                    string line = FrostfellRule.TimerLine(mark, present.Health.Current / (double)Math.Max(1u, present.Health.MaxValue));
                    foreach (var p in defenders) Say(p, line);
                    break;
                }
        }

        /// <summary>The present broken (ticket 261030-0012: "needs a transition out"): it bursts on screen and the
        /// defenders are told what happened, instead of the present just vanishing.</summary>
        internal static void PresentBroken(Creature c)
        {
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.Explode));
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.DisappearDestroy));
            foreach (var p in Near(c, FrostfellRule.RAID_R))
                Say(p, c.WeenieClassId == 73226
                    ? "The pile of presents collapses as the Drudges grab the last of it and scatter into the sand."
                    : "The Holiday Present splits open with a crack, and the Gurogs carry off what spills out.");
        }

        static void RaidTick(double now)
        {
            if (now < s_NextRaid) return;
            s_NextRaid = now + 2;
            try { RaidDirector(now); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell raid director: {e.Message}"); }
            foreach (var b in RAID_BLOCKS)
            {
                var lb = IfLoaded(b);
                if (lb == null) continue;
                foreach (var c in lb.GetAllWorldObjectsForDiagnostics().OfType<Creature>())
                {
                    bool raider = RAIDERS.Contains(c.WeenieClassId), present = PRESENTS.Contains(c.WeenieClassId);
                    if ((!raider && !present) || c.IsDead || s_Scaled.TryGetValue(c, out _)) continue;
                    s_Scaled.Add(c, new Scaled());
                    int n = Near(c, FrostfellRule.RAID_R).Where(p => !Mercenaries.IsMerc(p)).Select(p => p.Session?.AccountId ?? p.Guid.Full).Distinct().Count();
                    double k = raider ? FrostfellRule.RaiderShare(n) : FrostfellRule.PresentShare(n);
                    uint oldMax = c.Health.MaxValue, want = (uint)Math.Max(1, Math.Round(oldMax * k));
                    if (want == oldMax) continue;
                    var cc = c;
                    Later(cc, () =>
                    {
                        if (cc.IsDead) return;
                        cc.Health.StartingValue = (uint)Math.Max(1, (long)cc.Health.StartingValue + want - oldMax);
                        cc.UpdateVital(cc.Health, cc.Health.MaxValue);
                    });
                    Mod.Log.Info($"[RevivalGuard] Frostfell: raid {c.Name} 0x{c.Guid.Full:X8}: {n} defender(s), health {oldMax:N0} -> {want:N0}");
                }
            }
        }

        /// <summary>A Present Raid won (EventDirector: held to the end, or every raider gone): retail's usable reward present
        /// appears where the raid's present stood, for 3 minutes (wiki: the reward present stays 3 minutes after the raid).
        /// Its own retail emotes do the rest: 20 h timer, title (Tufa 857 Present Protector, Yaraq 859 Loss Prevention),
        /// 20% of a level's XP up to 100,000,000, 5,000 luminance and one random Holiday Rush item. Retail's generator chain
        /// makes it a child of the reset stopgap, which the event's stop sweep removes at once, so it is spawned on its own
        /// here (docs/EVENT-TESTS/holiday-rush-audit.md). Before: title 857 at both towns and an old Present (36897).</summary>
        internal static void RaidWon(string key, List<Player> near)
        {
            bool yaraq = key != null && key.EndsWith("yaraq", StringComparison.OrdinalIgnoreCase);
            uint wcid = yaraq ? 73229u : 73217u;
            // where the raid gens stand (landblock_instance): ace73230 at 0x7D640033, ace73219 at 0x866C001F
            var at = yaraq ? new Position(0x7D640033, 157.233f, 53.5103f, 15.1578f, 0f, 0f, 0f, 1f)
                           : new Position(0x866C001F, 90.4732f, 167.825f, 14.055f, 0f, 0f, 0f, 1f);
            var who = near.Where(x => x != null && !Mercenaries.IsMerc(x)).ToList();
            if (!LandblockManager.IsLoaded(new LandblockId((at.Cell & 0xFFFF0000) | 0xFFFF)))
            {
                Mod.Log.Info($"[RevivalGuard] Frostfell: {key} won, its landblock is not loaded; no reward present");
                return;
            }
            var gift = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(wcid);
            if (gift == null) { Mod.Log.Warn($"[RevivalGuard] Frostfell: no weenie {wcid} for the raid's reward present"); return; }
            gift.Location = at;
            gift.Lifespan = REWARD_S;
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!gift.EnterWorld()) { Mod.Log.Warn($"[RevivalGuard] Frostfell: {key} reward present did not enter the world"); return; }
                    foreach (var p in who)
                        Say(p, yaraq
                            ? "The Drudges are gone and the presents are safe! Use the Holiday Presents in the town within 3 minutes for your reward."
                            : "The Gurogs are gone and the present still stands! Use the Holiday Present in the town within 3 minutes for your reward.");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell: {key} reward present: {e.Message}"); }
            }));
            Mod.Log.Info($"[RevivalGuard] Frostfell: {key} won; reward present {wcid} for {REWARD_S} s, {who.Count} defender(s) near");
        }

        const int REWARD_S = 180;

        // ---- 4. carols at the Garland Arch ------------------------------------------------------------------------

        sealed class Carol { public string Name, Type; public MotionCommand Motion; }
        static readonly Carol[] CAROLS =
        {
            new Carol { Name = "a cheer", Type = "*cheer*", Motion = MotionCommand.Cheer },
            new Carol { Name = "a wave", Type = "*wave*", Motion = MotionCommand.Wave },
            new Carol { Name = "a clap", Type = "*clap*", Motion = MotionCommand.ClapHands },
        };
        sealed class Call { public Carol Carol; public double Ends; public readonly HashSet<uint> Did = new HashSet<uint>(); }
        static readonly object s_Lock = new object();
        static Call s_Call;
        static double s_Last, s_NextCall;
        static int s_LastCarol = -1;

        static (float x, float y) Grid(Position at) =>
            ((at.LandblockId.Landblock >> 8) * 192f + at.PositionX, (at.LandblockId.Landblock & 0xFF) * 192f + at.PositionY);

        static bool AtArch(Player p)
        {
            if (p?.Location == null) return false;
            ushort lb = p.Location.LandblockId.Landblock;
            if ((lb >> 8) < 0xA8 || (lb >> 8) > 0xAA || (lb & 0xFF) < 0xB3 || (lb & 0xFF) > 0xB5) return false;
            var (x, y) = Grid(p.Location);
            return (x - ARCH_X) * (x - ARCH_X) + (y - ARCH_Y) * (y - ARCH_Y) <= ARCH_R * ARCH_R;
        }

        internal static void Tick()
        {
            double now = Now;
            if (now - s_Last < 1.0) return;
            s_Last = now;
            if (On) RaidTick(now);
            if (!On || !CarolsOn) { lock (s_Lock) { s_Call = null; s_NextCall = 0; } return; }
            var here = PlayerManager.GetAllOnline().Where(AtArch).ToList();
            Call ended = null;
            lock (s_Lock)
            {
                if (here.Count == 0) { s_Call = null; s_NextCall = 0; return; }
                if (s_NextCall == 0) s_NextCall = now + FIRST_CAROL_S;
                if (s_Call != null && now > s_Call.Ends) { ended = s_Call; s_Call = null; s_NextCall = now + CAROL_S; }
            }
            var costumer = Find(here[0], o => o.WeenieClassId == COSTUMER).FirstOrDefault();
            if (ended != null) EndCarol(ended, here, costumer);
            else if (s_Call == null && now >= s_NextCall) StartCarol(null, here, costumer);
        }

        static void StartCarol(Carol pick, List<Player> here, WorldObject costumer)
        {
            if (pick == null)
            {
                int i;
                do i = s_Rng.Next(CAROLS.Length); while (CAROLS.Length > 1 && i == s_LastCarol);
                s_LastCarol = i;
                pick = CAROLS[i];
            }
            lock (s_Lock) s_Call = new Call { Carol = pick, Ends = Now + WINDOW_S };
            Speak(costumer, here, $"Carol time! Everyone by the Garland Arch, give {pick.Name} for Frostfell! Type {pick.Type} or pick it on the Emotes panel. You have {WINDOW_S:0} seconds!");
            if (costumer is Creature c) Later(c, () => c.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, pick.Motion)));
            Mod.Log.Info($"[RevivalGuard] Frostfell: the Costumer called {pick.Name} ({here.Count} at the arch)");
        }

        static void EndCarol(Call call, List<Player> here, WorldObject costumer)
        {
            var did = here.Where(p => call.Did.Contains(p.Guid.Full)).ToList();
            int crowd = did.Select(p => p.Session?.AccountId ?? p.Guid.Full).Distinct().Count();
            double pct = FrostfellRule.Cheer(crowd);
            if (did.Count == 0) Speak(costumer, here, "Nobody? I will call another carol in a few minutes. Bring a friend!");
            else Speak(costumer, here, crowd == 1 ? "Lovely! Frostfell Cheer to you." : $"{crowd} of you! That is the Frostfell spirit. Frostfell Cheer for everyone who joined in.");
            foreach (var p in here)
            {
                var pl = p;
                if (did.Contains(p)) Later(pl, () => Grant(pl, pct, crowd, crowd > 1 ? $"{crowd} carolers" : null));
                else Say(pl, $"You missed the carol. The Frostfell Costumer calls another in about {CAROL_S / 60:0} minutes.");
            }
            Mod.Log.Info($"[RevivalGuard] Frostfell: carol {call.Carol.Name}: {did.Count} joined ({crowd} distinct), cheer {pct:P0}");
        }

        internal static void OnMove(Player p, MoveToState m)
        {
            var raw = m?.RawMotionState;
            if (raw == null || s_Call == null || !AtArch(p)) return;
            lock (s_Lock)
            {
                if (s_Call == null || Now > s_Call.Ends) return;
                var want = s_Call.Carol.Motion;
                if (raw.ForwardCommand == want || (raw.Commands != null && raw.Commands.Any(c => c != null && c.MotionCommand == want)))
                    s_Call.Did.Add(p.Guid.Full);
            }
        }

        /// <summary>A typed emote arrives as its onlookers' text too (NightClubParty.OnSoulEmote has the why).</summary>
        internal static void OnSoulEmote(Player p, string text)
        {
            if (string.IsNullOrEmpty(text) || s_Call == null || !AtArch(p) || !SoulText().TryGetValue(Norm(text), out var c)) return;
            lock (s_Lock) if (s_Call != null && Now <= s_Call.Ends && s_Call.Carol.Motion == c) s_Call.Did.Add(p.Guid.Full);
        }

        static Dictionary<string, MotionCommand> s_SoulText;
        static string Norm(string t) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(t.Trim().Trim('*').Trim().ToLowerInvariant(), @"\b(his|her)\b", "%p"), @"\s+", " ");

        static Dictionary<string, MotionCommand> SoulText()
        {
            if (s_SoulText != null) return s_SoulText;
            var want = new HashSet<MotionCommand>(CAROLS.Select(x => x.Motion));
            var map = new Dictionary<string, MotionCommand>();
            try
            {
                var t = ACE.DatLoader.DatManager.PortalDat?.ChatPoseTable;
                if (t != null)
                {
                    foreach (var kv in t.ChatEmoteHash)
                        if (Enum.TryParse(kv.Key, true, out MotionCommand mc) && want.Contains(mc) && !string.IsNullOrEmpty(kv.Value?.OtherEmote))
                            map[Norm(kv.Value.OtherEmote)] = mc;
                    foreach (var kv in t.ChatPoseHash)
                        if (Enum.TryParse(kv.Value, true, out MotionCommand mc) && want.Contains(mc)) map[Norm(kv.Key)] = mc;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell: ChatPoseTable: {e.Message}"); }
            return s_SoulText = map;
        }

        // ---- 5. Frostfell Cheer -----------------------------------------------------------------------------------

        static void Grant(Player p, double pct, int crowd, string why)
        {
            double now = Now;
            double have = p.GetProperty(CHEER_PCT) ?? 0;
            long until = p.GetProperty(CHEER_UNTIL) ?? 0;
            if (until > now && have > pct + 1e-9)
            {
                Say(p, $"You keep your bigger Frostfell Cheer: +{have:P0} experience from creatures, {Mins(until - now)} left.");
                return;
            }
            p.SetProperty(CHEER_PCT, pct);
            p.SetProperty(CHEER_UNTIL, (long)(now + FrostfellRule.CHEER_S));
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.EnchantUpGreen));
            Say(p, $"Frostfell Cheer: +{pct:P0} experience from creatures for 1 hour{(why != null ? $" ({why})" : "")}. More friends make it bigger, up to +{FrostfellRule.CHEER_CAP:P0}.");
        }

        internal static double CheerNow(Player p)
        {
            long until = p.GetProperty(CHEER_UNTIL) ?? 0;
            if (until <= Now) return 0;
            return Math.Min(FrostfellRule.CHEER_CAP, Math.Max(0, p.GetProperty(CHEER_PCT) ?? 0));
        }

        internal static void OnLogin(Player p)
        {
            long until = p.GetProperty(CHEER_UNTIL) ?? 0;
            if (until > Now) Say(p, $"Frostfell Cheer: +{CheerNow(p):P0} experience from creatures, {Mins(until - Now)} left.");
        }

        static string Mins(double s) { int m = Math.Max(1, (int)Math.Round(s / 60)); return m == 1 ? "1 minute" : $"{m} minutes"; }

        // ---- helpers ----------------------------------------------------------------------------------------------

        /// <summary>Players within r metres outdoors (the landblocks round it too; Position.DistanceTo spans them).</summary>
        static IEnumerable<Player> Near(WorldObject o, float r) =>
            PlayerManager.GetAllOnline().Where(p => p?.Location != null && o?.Location != null
                && Math.Abs((p.Location.LandblockId.Landblock >> 8) - (o.Location.LandblockId.Landblock >> 8)) <= 1
                && Math.Abs((p.Location.LandblockId.Landblock & 0xFF) - (o.Location.LandblockId.Landblock & 0xFF)) <= 1
                && p.Location.DistanceTo(o.Location) <= r);

        static IEnumerable<WorldObject> Find(Player anyone, Func<WorldObject, bool> pred) =>
            anyone?.CurrentLandblock?.GetAllWorldObjectsForDiagnostics().Where(o => o != null && pred(o)) ?? Enumerable.Empty<WorldObject>();

        static void Speak(WorldObject who, List<Player> to, string text)
        {
            foreach (var p in to)
            {
                if (who != null) p.Session?.Network?.EnqueueSend(new GameMessageHearSpeech(text, who.Name, who.Guid.Full, ChatMessageType.Speech));
                else Say(p, "Frostfell Costumer: " + text);
            }
        }

        /// <summary>A landblock only if it is already loaded. LandblockManager.GetLandblock LOADS it: the raid tick asked
        /// for Tufa and Yaraq every 2 s, so with nobody there each landblock unloaded after ACE's 5 idle minutes and was
        /// loaded again at once, and a running raid's generators spawned a fresh Holiday Present and wave every 5 minutes
        /// (box 2026-11-01 04:43 to 05:13).</summary>
        static Landblock IfLoaded(ushort block)
        {
            var id = new LandblockId((uint)block << 16 | 0xFFFF);
            return LandblockManager.IsLoaded(id) ? LandblockManager.GetLandblock(id, false) : null;
        }

        static void Later(WorldObject o, Action a)
        {
            var chain = new ActionChain();
            chain.AddAction(o, () => { try { a(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell: {e.Message}"); } });
            chain.EnqueueChain();
        }

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static readonly string[] RESET =
        {
            "GiftBoxesFound", "GiftBoxesFoundCounter", "GiftBoxesWait", "PresentHunt", "PresentHuntTrophy",
            "GiftGopher_List", "GiftGopher_Timer", "GiftGopher_Count", "GiftGopher_Wait", "GiftGopher_ListsComplete",
            "GurogPresentRaidsWait", "DrudgePresentRaidsWait",
        };

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (verb == "reset")
            {
                foreach (var q in RESET) me.QuestManager.Erase(q);
                me.RemoveProperty(CHEER_PCT); me.RemoveProperty(CHEER_UNTIL);
                s_Told.TryRemove(me.Guid.Full, out _);
                Say(me, "Frostfell: your gift boxes, Present Hunt, Raymond's deliveries, raid present timers, Frostfell Cheer and the Costumer's intro are cleared.");
                return;
            }
            if (verb == "carol")
            {
                var here = PlayerManager.GetAllOnline().Where(AtArch).ToList();
                if (!On) { Say(me, "Frostfell is not running."); return; }
                if (here.Count == 0) { Say(me, "Nobody is at the Garland Arch in Holtburg."); return; }
                string want = string.Join(" ", args.Skip(1)).Trim().ToLowerInvariant();
                var pick = want.Length == 0 ? null : CAROLS.FirstOrDefault(c => c.Type.Contains(want));
                StartCarol(pick, here, Find(here[0], o => o.WeenieClassId == COSTUMER).FirstOrDefault());
                return;
            }
            if (verb == "raid")
            {
                var parts = new List<string>();
                foreach (var b in RAID_BLOCKS)
                {
                    var rlb = IfLoaded(b);
                    var cs = rlb?.GetAllWorldObjectsForDiagnostics().OfType<Creature>().Where(c => !c.IsDead && (RAIDERS.Contains(c.WeenieClassId) || PRESENTS.Contains(c.WeenieClassId))).ToList();
                    if (cs == null || cs.Count == 0) continue;
                    foreach (var pr in cs.Where(c => PRESENTS.Contains(c.WeenieClassId))) parts.Add($"{pr.Name} {pr.Health.Current:N0}/{pr.Health.MaxValue:N0}");
                    var rs = cs.Where(c => RAIDERS.Contains(c.WeenieClassId)).ToList();
                    if (rs.Count > 0) parts.Add($"{rs.Count} raiders ({string.Join(", ", rs.GroupBy(c => c.Name).Select(g => $"{g.Count()} {g.Key}"))})");
                }
                Say(me, parts.Count == 0 ? "No Present Raid is up (or its landblock is not loaded)." : "Raid: " + string.Join("; ", parts) + ".");
                return;
            }
            if (verb == "snowman")
            {
                var lb = IfLoaded(FROST_HAVEN);
                var sm = lb?.GetAllWorldObjectsForDiagnostics().OfType<Creature>().Where(c => c.WeenieClassId == SNOWMAN && !c.IsDead).ToList();
                if (sm == null || sm.Count == 0) { Say(me, "No Abominable Snowman is up at Frost Haven (or the landblock is not loaded)."); return; }
                foreach (var c in sm)
                {
                    int n = s_Fights.TryGetValue(c, out var f) ? f.Who.Count : 0;
                    Say(me, $"Abominable Snowman 0x{c.Guid.Full:X8}: {c.Health.Current:N0} of {c.Health.MaxValue:N0} health, {n} fighter(s) this life.");
                }
                return;
            }
            var g = FestivalGoals.ByCounter("PresentHunt");
            int arch = PlayerManager.GetAllOnline().Count(AtArch);
            Say(me, $"Frostfell {(On ? "running" : "not running")}. You: {me.QuestManager.GetCurrentSolves("GiftBoxesFoundCounter")} gift boxes, "
                + $"Present Hunt {(g != null ? me.QuestManager.GetCurrentSolves(g.Counter) : 0)} of 6{(g != null && FestivalGoals.TrophyTaken(me, g) ? " (snow globe taken)" : "")}, "
                + $"Frostfell Cheer {CheerNow(me):P0}. Carols {(CarolsOn ? "on" : "off")}, {arch} at the arch"
                + (s_Call != null ? $", calling {s_Call.Carol.Name} ({s_Call.Did.Count} joined)." : s_NextCall > 0 ? $", next carol in {Math.Max(0, s_NextCall - Now):0} s." : "."));
        }
    }

    [HarmonyPatch]
    static class FrostfellEmote
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(EmoteManager), nameof(EmoteManager.ExecuteEmote), new[] { typeof(PropertiesEmote), typeof(PropertiesEmoteAction), typeof(WorldObject) });
            if (m == null) Mod.Log.Error("[RevivalGuard] Frostfell: EmoteManager.ExecuteEmote(PropertiesEmote, PropertiesEmoteAction, WorldObject) not found; gift boxes say two counts");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static bool Prefix(EmoteManager __instance, PropertiesEmote emoteSet, PropertiesEmoteAction emote, WorldObject targetObject, ref float __result)
        {
            try
            {
                if (Frostfell.OnEmote(__instance, emoteSet, emote, targetObject)) return true;
                __result = 0f;
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell emote: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(Vendor), nameof(Vendor.ActOnUse), new Type[] { typeof(WorldObject) })]
    static class FrostfellCostumer
    {
        static void Prefix(Vendor __instance, WorldObject wo)
        {
            try { if (wo is Player p) Frostfell.OnCostumer(__instance, p); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell costumer: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class FrostfellSnowmanDamage
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(EmoteManager), nameof(EmoteManager.OnDamage), new[] { typeof(Creature) });
            if (m == null) Mod.Log.Error("[RevivalGuard] Frostfell: EmoteManager.OnDamage(Creature) not found; the Snowman keeps retail's health");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(EmoteManager __instance, Creature attacker)
        {
            try { Frostfell.OnDamage(__instance, attacker); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell snowman: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class FrostfellSnowmanDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] Frostfell: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; no Frostfell Cheer for the Snowman");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Creature __instance)
        {
            try { Frostfell.OnDeath(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell snowman death: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class FrostfellTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { Frostfell.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] Frostfell: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { Frostfell.Tick(); }
            catch (Exception e) { TickGuard.Report("Frostfell", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnMoveToState))]
    static class FrostfellMove
    {
        static void Postfix(Player __instance, MoveToState moveToState)
        {
            try { Frostfell.OnMove(__instance, moveToState); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell move: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionSoulEmote))]
    static class FrostfellSoulEmote
    {
        static void Postfix(Player __instance, string message)
        {
            try { Frostfell.OnSoulEmote(__instance, message); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell soul emote: {e.Message}"); }
        }
    }

    /// <summary>Frostfell Cheer: creature-kill XP. The bigger of it and the Night Club party buff counts, never both:
    /// this prefix multiplies by (1 + max) / (1 + party), which with NightClubPartyXp's own (1 + party) gives 1 + max
    /// in either order.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.EarnXP))]
    static class FrostfellCheerXp
    {
        static void Prefix(Player __instance, ref long amount, XpType xpType)
        {
            try
            {
                if (xpType != XpType.Kill || amount <= 0) return;
                double cheer = Frostfell.CheerNow(__instance);
                if (cheer <= 0) return;
                double party = NightClubParty.BuffNow(__instance);
                if (cheer <= party) return;
                amount = (long)Math.Round(amount * (1 + cheer) / (1 + party));
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell xp: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class FrostfellLogin
    {
        static void Postfix(Player __instance)
        {
            try { Frostfell.OnLogin(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell login: {e.Message}"); }
        }
    }

    /// <summary>The raid's calm (Frostfell.HoldWave): a wave generator's queued spawn waits while the defenders have their
    /// 45 seconds; the queue is kept, so the wave comes when the calm ends.</summary>
    [HarmonyPatch(typeof(GeneratorProfile), nameof(GeneratorProfile.ProcessQueue))]
    static class FrostfellRaidCalm
    {
        static bool Prefix(GeneratorProfile __instance)
        {
            try { return !Frostfell.HoldWave(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Frostfell raid calm: {e.Message}"); return true; }
        }
    }
}
