using System.Collections.Concurrent;
using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// ASHERON AND BAEL'ZHARON AT CRAGSTONE, the Lightbringer week's scene (owner OK 2026-10-28; docs/REVIVAL-PLAN-2026-10-28.md
    /// section 4). Retail's was a live dev performance in Dec 2000 with no script in the data (wiki "Bael'Zharon's Defeat",
    /// Dialog, and "Bael'Zharon's Rampage"); this is ours, retail-styled, its lines adapted from that dialogue.
    ///
    ///   cadence    every 4 hours from 00:00 UTC-5 while the "lightbringer" festival runs (SeasonalCalendar), warned at 30,
    ///              10 and 2 minutes like a world boss, and drawn on the map as one (WorldBossSchedule's "worldboss" payload,
    ///              key "lightbringer"; the client gives it its own icon)
    ///   opening    Asheron (902616, a copy of retail's 8675) in the red water below the rock at 24.0N 49.2E; Bael'Zharon
    ///              (902614, a copy of 8821 that never attacks) on the rock's top. Their exchange, then Bael's meteors
    ///              (MeteorStrike 4246) on the players and Asheron in the water below, then "Stand back, citizens!" and he
    ///              leaps down (gone from the rock, landed in the water by Asheron: a teleport with ACE's own effects)
    ///   the fight  the landed Bael (902615, an ordinary monster) fights the players; Asheron and he trade bolts and words.
    ///              He cannot die here (healed each tick, no hit takes his last point), and no hit of his takes more than
    ///              a share of a player's health (MAX_SHARE per WINDOW_S), so it stings, it does not wipe
    ///   2000's rules  a player who strikes him is cursed (Demon Fists, Demon's Tongues, Weight of Eternity; the last lifted
    ///              after WEIGHT_S); a player who talks to him without having struck him gets one of his gifts (Bile of the
    ///              Hopeslayer, Boon of the Demon, Demonskin), lifted when the scene ends
    ///   the end    Bael withdraws "to gather his strength", Asheron points the way to the Chosen and leaves
    ///   one Bael   the world boss Bael'Zharon (WorldBoss.cs) keeps his own rhythm; each scene is placed at least an hour
    ///              clear of his windows, warnings included (LightbringerSceneRule, the list the Event Director keeps clear),
    ///              moved up to 2 hours (or, failing that, skipped), and waits while he is up. The scene's Bael cannot die and drops nothing
    ///   Elysa      Elysa Strathelar (902618, a copy of retail's 20194) beside the Cragstone Chosen for the last 5 minutes
    ///              before each scene, rallying players in lines adapted from her Dec 2000 words; she leaves as it begins
    ///   Ilservian  when A Shadow of Black Ferah (8822) dies, the Remnants of Ilservian (9000) in the Sepulcher speak three
    ///              lines of regret as Ilservian Palacost
    ///
    /// Admin @lbscene (Developer): status | start [seconds of warning, default 60] | stop. A start works outside the week too
    /// (for tests); the schedule runs only inside it.
    /// </summary>
    static class LightbringerScene
    {
        internal const uint BAEL_ROCK = 902614, BAEL_FIGHT = 902615, ASHERON = 902616, ELYSA = 902618;
        const uint BLACK_FERAH = 8822, REMNANTS = 9000;
        internal const string KEY = "lightbringer";
        const string FESTIVAL = "lightbringer";

        // where (landblock 0xBC9D, Cragstone's water; 24.0N 49.2E is the rock's top)
        const uint CELL_ROCK = 0xBC9D003C, CELL_WATER = 0xBC9D003D;
        static Position RockTop() => new Position(CELL_ROCK, 180f, 84f, 19.65f, 0f, 0f, 0f, 1f);          // faces north, to Asheron
        static Position Landing() => Safe(LightbringerSceneRule.SAFE[0]);   // (180, 103): clear of the rock (0024)
        static Position Safe((double x, double y) p) => new Position(CellOf(p.x, p.y), (float)p.x, (float)p.y, 1.12f, 0f, 0f, 0f, 1f);
        /// <summary>The outdoor cell of local (x, y) in landblock 0xBC9D: an exact cell, not ACE's guess from cell 0 (Bael's
        /// home was set from one and his return home jumped him 37 m, box test 2026-10-28).</summary>
        static uint CellOf(double x, double y) => 0xBC9D0000u | (uint)(((int)Math.Clamp(x, 0, 191.9) / 24) * 8 + (int)Math.Clamp(y, 0, 191.9) / 24 + 1);
        static Position AsheronSpot() => new Position(CellOf(180, 114), 180f, 114f, 1.11f, 0f, 0f, 1f, 0f);     // faces south, to the rock
        const double MAP_NS = 24.0, MAP_EW = 49.2;
        static Position ElysaSpot() => new Position(0xBC9C0038, 157.5f, 168.5f, 1.91f, 0f, 0f, 0f, 1f);   // beside the Cragstone Chosen
        const double ELYSA_LEAD = 300, ELYSA_EVERY = 40;

        // the rhythm
        const int UTC_OFFSET_HOURS = -5;
        const double EVERY = 4 * 3600, LEAD = 30 * 60, TICK = 0.5;
        static readonly int[] WARN = { 30, 10, 2 };

        // tuning
        const float SPEECH_RANGE = 150f, HELP_RANGE = 40f, METEOR_RANGE = 45f;
        // all of his attacks together: at most LightbringerFightRule.WINDOW_FRAC of a player's max health per WINDOW_S
        const double CURSE_COOLDOWN_S = 20, WEIGHT_S = 15;

        static readonly uint[] CURSES = { 2045, 2042, 2043 };                 // Demon Fists, Demon's Tongues, Weight of Eternity
        static readonly uint[] GIFTS = { 2049, 2048, 2047 };                  // Bile of the Hopeslayer, Boon of the Demon, Demonskin
        const uint WEIGHT = 2043, METEOR = 4246;
        static readonly uint[] ASHERON_BOLTS = { 4451, 4439, 4447, 4443 };    // Incantations of Lightning, Flame, Frost, Force Bolt
        static readonly uint[] BAEL_BOLTS = { 5332, 5333 };                   // Bael'zharon's Nether Streak, Nether Arc

        // ------------------------------------------------------------------ state (world thread)
        enum Who { Bael, Asheron }
        sealed class Step { public double At; public Action Run; public string Label; }

        static readonly object s_Lock = new object();
        static double s_Start, s_End, s_Lead;     // the scene now warned or running; 0 = none
        static bool s_Forced;
        static readonly List<int> s_Warned = new List<int>();
        static List<Step> s_Steps;
        static int s_StepIdx;
        static Creature s_Bael, s_Asheron, s_Elysa;
        static int s_ElysaLine;
        static double s_ElysaNext;
        static double s_NextTick, s_LastRun, s_LastRunEnd, s_NextWake;
        static double s_SkippedStart;   // a scheduled start an admin stopped: not warned again
        static string s_LastSig;
        static readonly HashSet<uint> s_Attackers = new HashSet<uint>(), s_Helped = new HashSet<uint>();
        static readonly ConcurrentDictionary<uint, (double since, float taken)> s_Taken = new ConcurrentDictionary<uint, (double, float)>();
        static readonly Dictionary<uint, double> s_CursedAt = new Dictionary<uint, double>();
        static readonly List<(uint player, uint spell, double until)> s_Lift = new List<(uint, uint, double)>();
        static readonly Random s_Rng = Random.Shared;   // thread-safe (main 11-14: shared static Randoms are Random.Shared)

        static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        static bool InWeek => SeasonalCalendar.IsFestivalActive(FESTIVAL);

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "lbscene", AccessLevel.Developer, CommandHandlerFlag.None,
                "The Lightbringer week's Asheron and Bael'Zharon scene at Cragstone.", "status | plan | start [warning seconds] [force] [test] | stop | curse | gift | stuck");
            Mod.Log.Info("[RevivalGuard] LightbringerScene: registered (every 4 h from 00:00 UTC-5 during the Lightbringer week)");
        }

        // ------------------------------------------------------------------ the rhythm
        static double Anchor() =>
            (DateTime.SpecifyKind(new DateTime(2026, 1, 1).AddHours(-UTC_OFFSET_HOURS), DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

        /// <summary>The next scene start at or after this moment: each 4-hour slot placed clear of the world boss's windows
        /// (LightbringerSceneRule; a slot that cannot be placed is skipped). The schedule ignores the week; the caller asks InWeek.</summary>
        static double NextStart(double after)
        {
            double best = double.NaN;
            double slot = LightbringerSceneRule.SlotAtOrAfter(Anchor(), after - LightbringerSceneRule.SHIFT);
            for (int i = 0; i < 12; i++, slot += EVERY)
            {
                double p = LightbringerSceneRule.Place(slot, BaelWindows, after);
                if (!double.IsNaN(p) && (double.IsNaN(best) || p < best)) best = p;
                if (!double.IsNaN(best) && slot - LightbringerSceneRule.SHIFT > best) break;
            }
            return double.IsNaN(best) ? LightbringerSceneRule.SlotAtOrAfter(Anchor(), after) : best;
        }

        /// <summary>Bael'Zharon the world boss's windows, warning included (the list the Event Director keeps clear).</summary>
        static IEnumerable<(double from, double to)> BaelWindows(double from, double to) =>
            WorldBossSchedule.WindowsBetween(from, to).Where(w => w.name == "Bael'Zharon").Select(w => (w.from, w.to));

        /// <summary>The world boss himself standing in the world right now (any rule raised him): never two at once.</summary>
        static bool s_TestRun;   // @lbscene start ... force test: the one-Bael wait is skipped (admin box tests only)

        static bool WorldBaelUp()
        {
            try { return WorldBoss.Bosses.Values.Any(b => b.Wcid == WorldBoss.BAEL && WorldBoss.FindAlive(b) != null); }
            catch { return false; }
        }

        /// <summary>For the map: shown while the week runs, or while a forced scene is warned or running.</summary>
        internal static bool OnMap => InWeek || s_Start > 0;

        internal static void Tick()
        {
            double now = Now();
            if (now < s_NextTick) return;
            s_NextTick = now + TICK;
            lock (s_Lock)
            {
                if (s_Start == 0 && InWeek)
                {
                    double n = NextStart(now - 60);   // a start under a minute old still runs (a tick late, a quick restart)
                    if (n - LEAD <= now && n > s_SkippedStart) { s_Start = n; s_Lead = LEAD; s_Forced = false; s_Warned.Clear(); }
                }
                Sweep(now);
                if (s_Start == 0) { LiftDue(now); PushIfChanged(now); return; }

                if (now < s_Start) { Warn(now); ElysaTick(now); }
                else if (s_Steps == null && !s_TestRun && WorldBaelUp())
                {
                    // the world boss is abroad (a forced or old-rule rising the schedule could not see): wait for him
                    s_Start = now + LightbringerSceneRule.STEP; s_Warned.Clear();
                    Mod.Log.Info($"[RevivalGuard] LightbringerScene: the world boss Bael'Zharon is up; the scene waits until {Stamp(s_Start)}");
                }
                else if (s_Steps == null) Begin(now);
                else RunSteps(now);
                LiftDue(now);
            }
            PushIfChanged(now);
        }

        static void Warn(double now)
        {
            double rem = s_Start - now;
            var marks = s_Forced ? new[] { (int)Math.Ceiling(s_Lead / 60) } : WARN;
            var crossed = marks.Where(m => m * 60 >= rem - 0.5 && !s_Warned.Contains(m)).ToList();
            if (crossed.Count == 0) return;
            s_Warned.AddRange(crossed);
            World($"The water below Cragstone runs red. Bael'Zharon comes to Cragstone in {Minutes(rem)}, and Asheron will stand against him there.");
        }

        // ------------------------------------------------------------------ the script
        static void Begin(double now)
        {
            var lb = LandblockManager.GetLandblock(new LandblockId(CELL_ROCK | 0xFFFF), false);
            if (lb != null) lb.HasNoKeepAliveObjects = false;   // held awake for the scene
            s_Attackers.Clear(); s_Helped.Clear(); s_CursedAt.Clear();
            Vanish(s_Elysa, PlayScript.PortalEntry); s_Elysa = null;   // she leaves as the Hopeslayer comes
            HoldDrudges(true);
            s_LastHeal = s_LastHealLine = 0; s_Heals = s_AsheronBolts = s_AsheronHits = s_AshHeals = 0; s_AshOn = false; s_AshBusy = s_AshNextMove = s_AshNextHeal = s_AshNextLine = 0; s_Unsticks = 0; s_Stuck.Reset();
            FightReset();
            s_LastAttack = s_NextRate = s_NextFar = 0; s_Swings = s_SwingHits = s_FarSpells = s_Watchdogs = s_Kills = 0; s_RateSwings = s_RateHits = s_RateSpells = s_RateDogs = 0;
            s_Steps = Script().OrderBy(x => x.At).ToList();   // stable: same-time steps keep their order
            s_StepIdx = 0;
            s_End = s_Start + s_Steps[s_Steps.Count - 1].At + 1;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: begins{(s_Forced ? " (forced)" : "")}; {s_Steps.Count} steps to {Stamp(s_End)}");
            LogWaterMap();
            RunSteps(now);
        }

        static void RunSteps(double now)
        {
            double t = now - s_Start;
            while (s_Steps != null && s_StepIdx < s_Steps.Count && s_Steps[s_StepIdx].At <= t)
            {
                var st = s_Steps[s_StepIdx++];
                try { st.Run(); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: step {st.Label}: {e.Message}"); }
            }
            if (s_Steps != null && now >= s_NextDrudgeSweep) { s_NextDrudgeSweep = now + 4; HoldDrudges(true); }
            if (s_Steps != null && s_StepIdx >= s_Steps.Count) Finish(now, "done");
            else if (s_Bael != null && !s_Bael.IsDestroyed && !s_Bael.IsDead)
            {
                var b = s_Bael;
                HealCheck(now, b);   // he cannot be worn down here, and the players see why (ticket 261028-0014)
                // A monster made by EnterWorld sleeps until a player moves near it: the landed Bael goes for the players
                // in the water at once (the box test 2026-10-28: he stood 2 m from one and never swung)
                if (b.WeenieClassId == BAEL_FIGHT && b.Location != null) Unstick(now, b);
                // Scripted melee (owner 2026-10-28, after ACE's AI could not be made to chase this body reliably): the scene
                // moves him, draws, swings and lands capped hits. His own AI is off (Tolerance NoAttack on 902615).
                if (b.WeenieClassId == BAEL_FIGHT) Melee(now, b);
                if (s_AshOn && b.WeenieClassId == BAEL_FIGHT) AsheronTick(now, b);
                if (b.WeenieClassId == BAEL_FIGHT && t >= FIGHT_FROM) FightTick(now, t, b);
            }
        }

        static List<Step> Script()
        {
            var l = new List<Step>();
            void At(double t, string label, Action a) => l.Add(new Step { At = t, Label = label, Run = a });
            void Line(double t, Who w, string text) => At(t, "line", () => Say(w, text));

            At(0, "arrive", () =>
            {
                s_Asheron = Spawn(ASHERON, AsheronSpot(), PlayScript.PortalExit);
                s_Bael = Spawn(BAEL_ROCK, RockTop(), PlayScript.Create);
                World("Bael'Zharon stands upon the rock above Cragstone's red water! Asheron has come to meet him.");
            });
            Line(4, Who.Bael, "Is any here fool enough to face me in combat? Come. You shall feel my wrath boil the flesh from your bones.");
            Line(11, Who.Asheron, "Ilservian.");
            Line(15, Who.Bael, "That man is dead! Do not call me by his name!");
            Line(20, Who.Asheron, "It is your name, Master Palacost.");
            Line(25, Who.Bael, "Your mother is not here to protect you this time.");
            Line(31, Who.Asheron, "No. She sleeps among the Light, thanks to you.");
            Line(37, Who.Asheron, "Bael'Zharon, I am sorry for what my people did to you, and that your children starved because of us.");
            Line(44, Who.Bael, "SILENCE!");
            Line(48, Who.Asheron, "Ilservian, we are the same man. I live with regret for my loss, and you with rage.");
            Line(55, Who.Bael, "You know nothing of my loss! My master has eaten worlds. It has sucked the light from stars. Its power is in me. You have no chance, children.");
            Line(62, Who.Bael, "Let the sky fall upon you!");
            for (int i = 0; i < 8; i++) At(63 + i * 3, "meteor", Meteor);
            Line(89, Who.Asheron, "Stand back, citizens!");
            At(91, "leap", Leap);
            Line(93, Who.Bael, "Come then, boy. Face me, and bring your outlander dogs.");

            // the fight: bolts both ways, words between
            At(92, "asheron stance", AsheronReady);
            At(94, "asheron casts", () => s_AshOn = true);   // his scripted cast loop from here (AsheronTick)
            Line(97, Who.Asheron, "Citizens! Shards of my light will rise from the water. Bring them to me, and together we will drive him off!");
            At(98, "shard rule", () => Local("Pick up the Shards of Light that rise from the red water and carry them to Asheron. Each one weakens Bael'Zharon."));
            foreach (double t in new[] { 125.0, 210.0, 300.0, 390.0 }) At(t, "shadows", SummonShadows);
            // his spells on far players are in Melee now (between swings); BaelSpell's timed casts waited on nobody near
            for (double t = 112; t < 480; t += 25) At(t, "slam", SlamWarn);
            for (double t = 6; t < 91; t += 4) At(t, "asheron guard", AsheronReady);   // stance and facing from the start (0018)
            for (double t = 101; t < 500; t += 9) At(t, "bael bolt", () => Bolt(Who.Bael));
            var talk = new (Who, string)[]
            {
                (Who.Bael, "You weaken, boy. Should you not scurry back to your ivory tower?"),
                (Who.Asheron, "You cannot defeat me, Ilservian."),
                (Who.Bael, "Amazing these outlander dogs still support you. Did you not use them as fodder against the Olthoi?"),
                (Who.Asheron, "The outlanders you spit on are stronger than you think. I have seen them accomplish great things."),
                (Who.Bael, "I see the same fear in your eyes I saw in your father's."),
                (Who.Asheron, "Ilservian, the Nameless is using you. It cares not for your loss. It wants to be freed. It wants to devour."),
                (Who.Bael, "I AM NOT ILSERVIAN! He is dead! I am more than that little man could ever have been!"),
                (Who.Asheron, "I am not alone in opposing you. You have many enemies."),
            };
            for (int i = 0; i < talk.Length; i++) { var (w, s) = talk[i]; Line(130 + i * 45, w, s); }

            // his going is warned ~30 s ahead and played out (ticket 261028-0017: "beal randomly dissapeared")
            Line(487, Who.Bael, "This cursed light gnaws at me. Savor these moments, Yalain. They will not last.");
            At(488, "waver", () => Local("Bael'Zharon's form begins to waver. He will not hold this place much longer."));
            Line(505, Who.Asheron, "I found a way to imprison you once. You may be again.");
            Line(511, Who.Bael, "The years may wear long on you, boy, but you are mortal yet. Remember I am not.");
            Line(515, Who.Bael, "So little light, Yalain. Your outlanders failed you. I go to gather my strength, and I will return.");
            At(516, "lost", () => { if (!s_Won) Local($"Too little light was gathered ({s_Delivered} of {Needed} shards). Bael'Zharon withdraws, laughing."); });
            At(517, "withdraw", () =>
            {
                Exit(s_Bael); s_Bael = null;
                foreach (var c in s_Adds) Vanish(c, PlayScript.Destroy);   // his shadows go with him
                s_Adds.Clear();
            });
            Line(520, Who.Asheron, "He is gone, for now. Seek the Chosen of Asheron. Assemble the artifacts, and strike at him in his own Sepulcher.");
            At(526, "end", () => World($"Bael'Zharon withdraws from Cragstone to gather his strength. Asheron will meet him here again {Returns()}."));
            At(534, "asheron leaves", () => { Vanish(s_Asheron, PlayScript.PortalEntry); s_Asheron = null; });
            return l;
        }

        // ------------------------------------------------------------------ Elysa rallies the players (the last 5 minutes)
        /// <summary>Adapted from Elysa Strathelar's Dec 2000 words to players (wiki "Bael'Zharon's Defeat", Dialog).</summary>
        static readonly string[] ELYSA_LINES =
        {
            "Fellow Isparian, the Hopeslayer walks this land once more. Your people are in peril.",
            "Who is the strongest monarch you know of? Does that monarch hold fast with the Light, or has the dark won them over?",
            "When the fight begins, shards of Asheron's light will rise from the red water. Carry them to him, and he will turn their light against the Hopeslayer.",
            "The Hopeslayer is vulnerable. Gather strong and trustworthy comrades. Soon Asheron will stand against him at the red water below.",
            "No one person is able to defeat the Hopeslayer, but working together, the sons and daughters of Ispar can overcome his power.",
            "Asheron requests the aid of all the citizens of Dereth, not just its mightiest heroes.",
            "Seek the Skull, the Heart, and the Singularity. The Chosen of Asheron will tell you how.",
        };

        static void ElysaTick(double now)
        {
            if (s_Start - now > ELYSA_LEAD) return;
            if (s_Elysa == null || s_Elysa.IsDestroyed)
            {
                if (!LandblockManager.IsLoaded(new LandblockId(0xBC9CFFFF))) LandblockManager.GetLandblock(new LandblockId(0xBC9CFFFF), false);
                s_Elysa = Spawn(ELYSA, ElysaSpot(), PlayScript.PortalExit);
                s_ElysaLine = 0; s_ElysaNext = now + 4;
                return;
            }
            if (now < s_ElysaNext) return;
            s_ElysaNext = now + ELYSA_EVERY;
            var e = s_Elysa;
            string text = ELYSA_LINES[s_ElysaLine++ % ELYSA_LINES.Length];
            Act(e, () => e.EnqueueBroadcast(new GameMessageHearSpeech(text, e.Name, e.Guid.Full, ChatMessageType.Speech), 60f));
        }

        // ------------------------------------------------------------------ Ilservian, after the Sepulcher's kill
        /// <summary>When A Shadow of Black Ferah dies, the Remnants of Ilservian (9000, in the Sepulcher) speak as Ilservian
        /// Palacost: regret, answering Asheron's "we are the same man" (owner 2026-10-28).</summary>
        static readonly string[] ILSERVIAN_LINES =
        {
            "The shadow is gone, and for a moment the voice is quiet. I remember my own name.",
            "Maitrell... my children. I wanted only bread for them, and the Yalain gave us winter. I became this.",
            "Tell Asheron I heard him. We are the same man. He keeps his grief. I let mine devour the world.",
        };

        internal static void OnFerahDeath(Creature ferah)
        {
            var lb = ferah?.CurrentLandblock;
            if (lb == null) return;
            var rem = lb.GetAllWorldObjectsForDiagnostics().FirstOrDefault(o => o.WeenieClassId == REMNANTS && !o.IsDestroyed);
            if (rem == null) { Mod.Log.Info("[RevivalGuard] LightbringerScene: Black Ferah fell; no Remnants of Ilservian in the landblock"); return; }
            var chain = new Seq();
            chain.AddDelaySeconds(4);
            foreach (var line in ILSERVIAN_LINES)
            {
                string text = line;
                chain.AddAction(rem, () => { if (!rem.IsDestroyed) rem.EnqueueBroadcast(new GameMessageHearSpeech(text, "Ilservian Palacost", rem.Guid.Full, ChatMessageType.Speech), 60f); });
                chain.AddDelaySeconds(6);
            }
            chain.EnqueueChain();
        }

        static string Returns()
        {
            double n = NextStart(Now() + 60);
            return InWeek ? $"in {Minutes(n - Now())}" : "in the next Lightbringer's Week";
        }

        /// <summary>Bael's going, played out: he raises his arms (the cast gesture), the dark storms around him, a burst, and
        /// he is gone four seconds later (ticket 261028-0017).</summary>
        static void Exit(Creature b)
        {
            if (b == null || b.IsDestroyed) return;
            Retire(b, 12);
            if (b.CurrentLandblock == null) { Vanish(b, PlayScript.DisappearDestroy); return; }
            var chain = new Seq();
            chain.AddAction(b, () =>
            {
                if (b.IsDestroyed) return;
                b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell));
                b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.PortalStorm));
                b.EnqueueBroadcast(new GameMessageSystemChat("The dark gathers around Bael'Zharon. The red water boils, and the sky tears open above him!", ChatMessageType.Broadcast), SPEECH_RANGE);
            });
            chain.AddDelaySeconds(2.5);
            chain.AddAction(b, () => { if (!b.IsDestroyed) { b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.BlackMadness)); b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.Explode)); } });
            chain.AddDelaySeconds(1.5);
            chain.AddAction(b, () => { if (!b.IsDestroyed) b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.DisappearDestroy)); });
            chain.AddDelaySeconds(1.0);
            chain.AddAction(b, () => { if (!b.IsDestroyed) b.Destroy(); });
            chain.EnqueueChain();
        }

        static void Local(string text)
        {
            var at = (WorldObject)s_Bael ?? s_Asheron;
            Act(at, () => at.EnqueueBroadcast(new GameMessageSystemChat(text, ChatMessageType.Broadcast), SPEECH_RANGE));
        }

        // ------------------------------------------------------------------ nothing of a scene is ever left behind (ticket 261028-0024)
        // The owner logged in after a stopped test scene and met Bael on the rock and a shadow, with Asheron gone: their
        // timed removal (an action on the object itself) never ran, and only their 15-minute Lifespan took them. Now every
        // actor is tracked: one that is retired is removed from the world thread if it is still there GRACE seconds on,
        // and while no scene runs any scene actor left in Cragstone (902614-902618) is removed.
        static readonly List<WorldObject> s_Spawned = new List<WorldObject>();
        static readonly Dictionary<WorldObject, double> s_Retired = new Dictionary<WorldObject, double>();
        static double s_NextSweep;

        /// <summary>Never saved to the shard: ACE keeps any dynamic object a generator did not make, so after a restart the
        /// scene's Bael (on the rock), Asheron and the shadows came back with no scene around them (ticket 261028-0024).</summary>
        internal static bool NeverSave(WorldObject o)
        {
            if (o == null) return false;
            uint w = o.WeenieClassId;
            if (w == BAEL_ROCK || w == BAEL_FIGHT || w == ASHERON || w == ELYSA) return true;
            var holder = o.Wielder ?? o.Container;
            if (holder != null && holder != o && NeverSave(holder)) return true;   // their staff and sword
            lock (s_Lock) return s_Spawned.Contains(o) || s_ShardItems.Contains(o);   // the shadows, the shards
        }

        static void Retire(WorldObject o, double grace = 8)
        {
            lock (s_Lock) if (o != null && !s_Retired.ContainsKey(o)) s_Retired[o] = Now() + grace;
        }

        static void ForceRemove(WorldObject o, string why)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try { if (!o.IsDestroyed) { o.Destroy(); Mod.Log.Info($"[RevivalGuard] LightbringerScene: removed {o.Name} 0x{o.Guid.Full:X8} ({why})"); } }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: remove {o.Name}: {e.Message}"); }
            }));
        }

        static void Sweep(double now)
        {
            foreach (var kv in s_Retired.ToList())
            {
                if (kv.Key.IsDestroyed) { s_Retired.Remove(kv.Key); s_Spawned.Remove(kv.Key); continue; }
                if (now >= kv.Value) { s_Retired[kv.Key] = now + 10; ForceRemove(kv.Key, "retired, still in the world"); }
            }
            s_Spawned.RemoveAll(o => o == null || o.IsDestroyed);
            if (s_Steps != null || now < s_NextSweep) return;
            s_NextSweep = now + 15;
            // no scene running: nothing of one may stand in Cragstone (a missed removal, a stop between spawn and entry)
            foreach (var o in s_Spawned.ToList()) if (!s_Retired.ContainsKey(o) && o != s_Elysa) Retire(o, 0);
            foreach (uint lbid in new uint[] { 0xBC9DFFFF, 0xBC9CFFFF })
            {
                if (!LandblockManager.IsLoaded(new LandblockId(lbid))) continue;
                var lb = LandblockManager.GetLandblock(new LandblockId(lbid), false);
                foreach (var o in lb.GetAllWorldObjectsForDiagnostics().ToList())
                    if ((o.WeenieClassId == BAEL_ROCK || o.WeenieClassId == BAEL_FIGHT || o.WeenieClassId == ASHERON || (o.WeenieClassId == ELYSA && o != s_Elysa))
                        && !o.IsDestroyed && !s_Retired.ContainsKey(o))
                        ForceRemove(o, "a scene actor with no scene running");
            }
        }

        // ------------------------------------------------------------------ Bael's scripted melee
        // ACE's monster AI fought this body at every turn (box tests 2026-10-28: it stalled, went home, took Asheron as a
        // target it cannot fight, and his sword stance has no run). The scene does it all, the way the slam and bolts are
        // done: every tick he picks the nearest player within ENGAGE m (not on the rock), or Asheron when there is none;
        // out of reach he sheathes to peace (his body runs only there) and runs in; within reach he takes the sword stance,
        // turns and swings every SWING_EVERY s, and a player still in reach SWING_DELAY later takes SWING_FRAC of max health
        // (through the scene's cap). Asheron takes the blows as a show (he takes no damage).
        // Owner's replay 2026-10-29 (ticket 261029-0003): "beal stood there not attacking for a good amount of the fight ... i
        // want him killing everyone." Why he stood: every move goal was clamped to a box (x 158 to 190, y 102 to 130), so a
        // player outside it (the north shore, the west bank) gave a goal where he already stood, out of reach, for good. Now
        // the only limit is the rock's footprint, he runs to SWING_AT m of his target, and a watchdog (WATCHDOG_S without an
        // attack while a player is within ENGAGE) closes the gap at once (a blink to his target) and swings. He hits hard:
        // SWING_FLAT + SWING_FRAC of max health, at most SWING_MAX, so a low-level player falls in a hit or two and a high one
        // feels it. Players beyond FAR_AT get a spell from him between swings, every FAR_EVERY s.
        const float REACH = 8f, SWING_AT = 4f, FAR_AT = 14f;
        const double SWING_EVERY = 2.2, SWING_DELAY = 0.6, MOVE_EVERY = 1.0;   // damage: LightbringerFightRule.SwingFrac
        const double WATCHDOG_S = 3.0, FAR_EVERY = 7.0;
        static double s_NextSwing, s_NextMove, s_LastAttack, s_NextFar, s_NextRate;
        internal static int s_Swings, s_SwingHits, s_FarSpells, s_Watchdogs, s_Kills;
        static int s_RateSwings, s_RateHits, s_RateSpells, s_RateDogs;
        static readonly MotionCommand[] SWINGS = { MotionCommand.AttackHigh1, MotionCommand.AttackMed1, MotionCommand.AttackLow1 };

        /// <summary>A spot SWING_AT m short of the target on Bael's side, never in the rock (north of it instead).</summary>
        static Position Approach(Creature b, WorldObject target, float stop)
        {
            var tp = target.Location.Pos; var bp = b.Location.Pos;
            var v = new System.Numerics.Vector3(tp.X - bp.X, tp.Y - bp.Y, 0f);
            var dir = v.LengthSquared() < 0.01f ? new System.Numerics.Vector3(0f, 1f, 0f) : System.Numerics.Vector3.Normalize(v);
            float tx = tp.X - dir.X * stop, ty = tp.Y - dir.Y * stop;
            if (LightbringerSceneRule.InRock(tx, ty)) ty = (float)LightbringerSceneRule.ROCK_NORTH + 3f;
            tx = Math.Clamp(tx, 2f, 190f);   // stays on this landblock (local metres 0 to 192)
            ty = Math.Clamp(ty, 2f, 190f);
            var to = new Position(CellOf(tx, ty), tx, ty, Math.Max(1.12f, target.Location.PositionZ), 0f, 0f, 0f, 1f);
            to.Rotate(dir);
            return to;
        }

        static void Melee(double now, Creature b)
        {
            if (b.Location == null || s_Won) return;
            if (s_LastAttack == 0) s_LastAttack = now;
            RateLog(now);
            WorldObject target = Foe(b);
            bool duel = target == null;
            if (duel) target = s_Asheron;
            if (target == null || target.IsDestroyed || target.Location == null) return;
            if (!duel) b.AttackTarget = target;
            float d = b.Location.DistanceTo(target.Location);

            // the watchdog: a player near and no attack for WATCHDOG_S: close the gap now and swing
            if (!duel && now - s_LastAttack > WATCHDOG_S && d <= ENGAGE && target.Location.Landblock == b.Location.Landblock)
            {
                s_Watchdogs++; s_RateDogs++;
                Mod.Log.Info($"[RevivalGuard] LightbringerScene: Bael idle {now - s_LastAttack:F1} s, {d:F1} m from {target.Name}; he closes");
                if (d > REACH)
                {
                    var to = Approach(b, target, SWING_AT);
                    Act(b, () =>
                    {
                        b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.Hide));
                        b.FakeTeleport(to);
                        b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.UnHide));
                    });
                    d = SWING_AT;
                }
                s_NextSwing = now;   // and swings at once
            }

            // a player far off gets a spell between swings (they were never touched while he fought the near ones)
            if (!duel && now >= s_NextFar && now >= s_NextSwing)
            {
                var far = PlayersNear(b, RANGED).Where(p => p.Location.DistanceTo(b.Location) > FAR_AT && !LightbringerSceneRule.InRock(p.Location.PositionX, p.Location.PositionY)).ToList();
                if (far.Count > 0)
                {
                    s_NextFar = now + FAR_EVERY;
                    s_NextSwing = now + 1.6;
                    s_LastAttack = now; s_FarSpells++; s_RateSpells++;
                    var fp = far[s_Rng.Next(far.Count)];
                    uint id = new uint[] { 5332, 5333, 2042 }[s_Rng.Next(3)];
                    var c = new Seq();
                    c.AddAction(b, () => { b.TurnTo(fp); b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell)); });
                    c.AddDelaySeconds(0.9);
                    c.AddAction(b, () => { if (!fp.IsDead && fp.CurrentLandblock != null) b.TryCastSpell(new Spell(id), fp, b, null, false, false, false); });
                    c.EnqueueChain();
                    return;
                }
            }

            if (d > REACH)
            {
                if (now < s_NextMove) return;
                s_NextMove = now + MOVE_EVERY;
                var to = Approach(b, target, duel ? 5f : SWING_AT);
                Act(b, () =>
                {
                    if (b.CombatMode != CombatMode.NonCombat) b.SetCombatMode(CombatMode.NonCombat);   // his body runs only in peace
                    b.MoveTo(to, 1.4f);
                });
                return;
            }
            if (now < s_NextSwing) return;
            s_NextSwing = now + SWING_EVERY;
            s_LastAttack = now; s_Swings++; s_RateSwings++;
            var swing = SWINGS[s_Rng.Next(SWINGS.Length)];
            var victim = target;
            var chain = new Seq();
            chain.AddAction(b, () =>
            {
                if (b.CombatMode != CombatMode.Melee) b.SetCombatMode(CombatMode.Melee);   // the sword drawn
                b.TurnTo(victim);
                b.EnqueueBroadcastMotion(new Motion(MotionStance.SwordCombat, swing));
            });
            chain.AddDelaySeconds(SWING_DELAY);
            chain.AddAction(b, () =>
            {
                if (victim is Player p && !p.IsDead && p.Location != null && b.Location != null && p.Location.DistanceTo(b.Location) <= REACH + 2f)
                {
                    float amt = (float)(LightbringerFightRule.SwingFrac(s_FightN, s_Phase) * p.Health.MaxValue);
                    int dealt = p.TakeDamage(b, DamageType.Slash, amt, BodyPart.Chest);
                    s_SwingHits++; s_RateHits++;
                    p.SendMessage($"Bael'Zharon's sword cuts you for {dealt} points of damage!", ChatMessageType.Combat);
                    if (p.IsDead || p.Health.Current == 0) s_Kills++;
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>Bael's attacks in the last minute, in the log (owner 261029-0003: "always be a threat").</summary>
        static void RateLog(double now)
        {
            if (s_NextRate == 0) { s_NextRate = now + 60; return; }
            if (now < s_NextRate) return;
            s_NextRate = now + 60;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: Bael's minute: {s_RateSwings} swings ({s_RateHits} hit), {s_RateSpells} spells at far players, {s_RateDogs} watchdog closes; kills {s_Kills}; fight n {s_FightN} phase {s_Phase} shards {s_Delivered}/{Needed} (made {s_ShardsMade}, lying {s_ShardItems.Count(o => !o.IsDestroyed && o.Container == null)}, held {s_ShardItems.Count(o => !o.IsDestroyed && o.Container != null)}) meteors {s_Meteors} ({s_MeteorHits} hit) shields {s_Shields}");
            s_RateSwings = s_RateHits = s_RateSpells = s_RateDogs = 0;
        }

        // ------------------------------------------------------------------ he draws to fight, sheathes to chase
        // Bael's body (retail 8821's motion table) has no run in a sword stance: with the sword in hand he stood still while
        // players were 15 m off (box test 2026-10-28; unarmed he ran to them). So the blade goes to his pack when his target
        // is out of reach, he runs in, and he draws it again within reach. At most one change every 3 s.
        const float DRAW_AT = 8f, SHEATHE_AT = 11f;   // he stops to swing ~6 m out, center to center (scale 3)
        static double s_NextBlade;

        static void Blade(double now, Creature b)
        {
            if (now < s_NextBlade || b.AttackTarget?.Location == null || b.Location == null) return;
            float d = b.Location.DistanceTo(b.AttackTarget.Location);
            var held = b.EquippedObjects.Values.FirstOrDefault(o => o.WeenieClassId == 902619);
            var packed = b.Inventory.Values.FirstOrDefault(o => o.WeenieClassId == 902619);
            if (held != null && d > SHEATHE_AT)
            {
                s_NextBlade = now + 3;
                Act(b, () =>
                {
                    if (b.TryUnwieldObjectWithBroadcasting(held.Guid, out var item, out _)) b.TryAddToInventory(item);
                    b.SetCombatMode(CombatMode.Melee);
                });
            }
            else if (held == null && packed != null && d <= DRAW_AT)
            {
                s_NextBlade = now + 3;
                Act(b, () => { b.EquipInventoryItems(true); b.SetCombatMode(CombatMode.Melee); });
            }
        }

        // ------------------------------------------------------------------ Bael never stuck in the rock (0024)
        static readonly LightbringerSceneRule.StuckWatch s_Stuck = new LightbringerSceneRule.StuckWatch();
        static double s_LastUnstick, s_NextTrail, s_LastEngageLog, s_NextDuel;
        internal static int s_Unsticks;

        static void Unstick(double now, Creature b)
        {
            var p = b.Location.Pos;
            if (now >= s_NextTrail)
            {
                s_NextTrail = now + 15;   // his trail, for any stuck report (0025 had none to read)
                Mod.Log.Info($"[RevivalGuard] LightbringerScene: Bael at ({p.X:F1}, {p.Y:F1}, {p.Z:F1}) health {b.Health.Current}/{b.Health.MaxValue} target {b.AttackTarget?.Name ?? "-"}");
            }
            double reach = b.AttackTarget?.Location != null ? b.Location.DistanceTo(b.AttackTarget.Location) : double.PositiveInfinity;
            if (!s_Stuck.Update(now, p.X, p.Y, reach) || now - s_LastUnstick < 3) return;
            s_LastUnstick = now;
            s_Stuck.Reset();
            var safe = LightbringerSceneRule.NearestSafe(p.X, p.Y);
            if (!LightbringerSceneRule.InRock(p.X, p.Y))
            {
                // Out of the rock, a stall is his AI pausing between swings or casts (it read as stuck and cost him his
                // target mid-chase, box test 2026-10-28): only a target standing on the rock, which he cannot reach, is let go
                var tg = b.AttackTarget;
                if (tg?.Location == null || !LightbringerSceneRule.InRock(tg.Location.PositionX, tg.Location.PositionY)) return;
                // already on open ground, his target out of reach (on the rock): he lets that one go instead of blinking
                // in place every 3 s (box test 2026-10-28)
                var gone = b.AttackTarget;
                Act(b, () => { if (b.AttackTarget == gone) b.AttackTarget = null; });
                return;
            }
            s_Unsticks++;
            var to = Safe(safe);
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: Bael stuck at ({p.X:F1}, {p.Y:F1}, {p.Z:F1}){(LightbringerSceneRule.InRock(p.X, p.Y) ? " in the rock" : "")}; put back at ({to.PositionX}, {to.PositionY})");
            Act(b, () =>
            {
                b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.Hide));
                b.FakeTeleport(to);
                b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.UnHide));
            });
        }

        static void Finish(double now, string why)
        {
            Exit(s_Bael); s_Bael = null;
            Vanish(s_Asheron, PlayScript.PortalEntry); s_Asheron = null;
            foreach (var c in s_Adds) Vanish(c, PlayScript.Destroy);
            s_Adds.Clear();
            HoldDrudges(false);
            LiftGifts();
            ClearShards();
            Fog(null, false);
            var lb = LandblockManager.GetLandblock(new LandblockId(CELL_ROCK | 0xFFFF), false);
            if (lb != null) lb.HasNoKeepAliveObjects = !lb.GetAllWorldObjectsForDiagnostics().Any(o => o.WeenieClassId == 80007);
            Vanish(s_Elysa, PlayScript.PortalEntry); s_Elysa = null;
            s_LastRun = s_Start; s_LastRunEnd = now;
            s_Start = s_End = 0; s_Steps = null; s_Forced = false; s_TestRun = false; s_Warned.Clear();
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: ended ({why})");
        }

        // ------------------------------------------------------------------ the actors
        static Creature Spawn(uint wcid, Position at, PlayScript fx)
        {
            if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature c)) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: weenie {wcid} missing (gen_revival.py's datafix)"); return null; }
            c.Location = at;
            c.Lifespan = 900;   // gone by itself if a stop is ever missed
            lock (s_Lock) s_Spawned.Add(c);
            // ACE puts a creature's wield list in its pack and equips it later from the monster AI, which a non-attacking
            // NPC never reaches: Asheron's staff and Bael's sword are put in their hands before they enter (0019, 0021)
            try { c.EquipInventoryItems(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: equip {wcid}: {e.Message}"); }
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!c.EnterWorld()) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: {c.Name} could not enter at {at}"); return; }
                    c.EnqueueBroadcast(new GameMessageScript(c.Guid, fx));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: spawn {wcid}: {e.Message}"); }
            }));
            return c;
        }

        static void Vanish(Creature c, PlayScript fx)
        {
            if (c == null || c.IsDestroyed) return;
            Retire(c);
            if (c.CurrentLandblock == null)
            {
                // not in the world yet: its EnterWorld is queued on the world thread ahead of this, so this runs after it
                WorldManager.EnqueueAction(new ActionEventDelegate(() => { try { if (!c.IsDestroyed) c.Destroy(); } catch { } }));
                return;
            }
            var chain = new Seq();
            chain.AddAction(c, () => { if (!c.IsDestroyed) c.EnqueueBroadcast(new GameMessageScript(c.Guid, fx)); });
            chain.AddDelaySeconds(1.5);
            chain.AddAction(c, () => { if (!c.IsDestroyed) c.Destroy(); });
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ timed actions on the world thread
        // The scene's timed actions used to be ActionChains on the creatures themselves. On a fighting creature some never
        // ran: Bael's heal below 40% in the owner's 22:30 playtest (261028-0026, -0027), and the removal of Bael and a
        // shadow after a stopped scene (0024). Now every step goes into this queue, run from the world thread
        // (WorldManager.EnqueueAction, as spawns already were), each at its due time.
        sealed class Seq
        {
            readonly List<(double at, WorldObject wo, Action a)> m_Steps = new List<(double, WorldObject, Action)>();
            double m_T;
            public void AddAction(WorldObject wo, Action a) => m_Steps.Add((m_T, wo, a));
            public void AddDelaySeconds(double s) => m_T += s;
            public void EnqueueChain()
            {
                double now = Now();
                lock (s_LaterLock) foreach (var (at, wo, a) in m_Steps) s_Later.Add((now + at, wo, a));
            }
        }
        static readonly object s_LaterLock = new object();
        static readonly List<(double at, WorldObject wo, Action a)> s_Later = new List<(double, WorldObject, Action)>();

        /// <summary>Runs the due steps, in order, on the world thread. Called every world tick (not throttled).</summary>
        internal static void RunLater()
        {
            List<(double at, WorldObject wo, Action a)> due = null;
            double now = Now();
            lock (s_LaterLock)
            {
                if (s_Later.Count == 0) return;
                for (int i = 0; i < s_Later.Count; i++)
                    if (s_Later[i].at <= now) (due ??= new List<(double, WorldObject, Action)>()).Add(s_Later[i]);
                if (due == null) return;
                s_Later.RemoveAll(x => x.at <= now);
            }
            foreach (var (_, wo, a) in due.OrderBy(x => x.at))
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try { if (wo == null || !wo.IsDestroyed) a(); }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: {wo?.Name}: {e.Message} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
                }));
        }

        static void Act(WorldObject wo, Action a)
        {
            if (wo == null || wo.IsDestroyed) return;
            var chain = new Seq();
            chain.AddAction(wo, () => { try { if (!wo.IsDestroyed) a(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: {wo.Name}: {e.Message} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); } });
            chain.EnqueueChain();
        }

        static void Say(Who w, string text)
        {
            var c = w == Who.Bael ? s_Bael : s_Asheron;
            Act(c, () => c.EnqueueBroadcast(new GameMessageHearSpeech(text, c.Name, c.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE));
        }

        static IEnumerable<Player> PlayersNear(WorldObject c, float range) =>
            PlayerManager.GetAllOnline().Where(p => p != null && !p.IsDead && p.Location != null && c?.Location != null
                && p.Location.DistanceTo(c.Location) <= range);

        /// <summary>One MeteorStrike from the rock on a player in the water below, or on Asheron when nobody stands there.</summary>
        static void Meteor()
        {
            var bael = s_Bael; var ash = s_Asheron;
            if (bael == null || bael.IsDestroyed) return;
            var near = PlayersNear(ash ?? bael, METEOR_RANGE).ToList();
            WorldObject target = near.Count > 0 && s_Rng.Next(3) != 0 ? near[s_Rng.Next(near.Count)] : ash;
            if (target == null) return;
            Act(bael, () => bael.TryCastSpell(new Spell(METEOR), target, bael, null, false, false, false));
        }

        /// <summary>He leaves the rock and lands in the water before Asheron: the rock's Bael never fights, the landed one does.</summary>
        static void Leap()
        {
            var rock = s_Bael;
            if (rock != null && !rock.IsDestroyed)
            {
                Retire(rock);
                var chain = new Seq();
                chain.AddAction(rock, () => { if (!rock.IsDestroyed) rock.EnqueueBroadcast(new GameMessageScript(rock.Guid, PlayScript.Hide)); });
                chain.AddDelaySeconds(0.6);
                chain.AddAction(rock, () => { if (!rock.IsDestroyed) rock.Destroy(); });
                chain.EnqueueChain();
            }
            s_Bael = Spawn(BAEL_FIGHT, Landing(), PlayScript.Explode);
        }

        static void Bolt(Who from)
        {
            var src = from == Who.Bael ? s_Bael : s_Asheron;
            var dst = from == Who.Bael ? s_Asheron : s_Bael;
            if (src == null || dst == null || src.IsDestroyed || dst.IsDestroyed || src.CurrentLandblock == null || dst.CurrentLandblock == null) return;
            if (from == Who.Asheron && !InPosition(src, dst)) return;   // moving into range and sight this time (0019, 0020)
            if (from == Who.Bael && Closing(src)) return;                // closing on a player: no turn toward Asheron
            var ids = from == Who.Bael ? BAEL_BOLTS : ASHERON_BOLTS;
            uint id = ids[s_Rng.Next(ids.Length)];
            // the cast is seen: in the magic stance, turned to the other, the cast gesture, then the bolt (ticket 261028-0013)
            var chain = new Seq();
            chain.AddAction(src, () =>
            {
                if (src.IsDestroyed || dst.IsDestroyed) return;
                // only Asheron is put in the magic stance: Bael's stance is his AI's, with his sword (0021)
                if (src.WeenieClassId == ASHERON && src.CombatMode != CombatMode.Magic) src.SetCombatMode(CombatMode.Magic);
                src.TurnTo(dst);
                src.EnqueueBroadcastMotion(new Motion(src, MotionCommand.CastSpell));
            });
            chain.AddDelaySeconds(1.2);
            chain.AddAction(src, () =>
            {
                if (src.IsDestroyed || dst.IsDestroyed || dst.CurrentLandblock == null) return;
                src.TryCastSpell(new Spell(id), dst, src, null, false, false, false);
                if (src.WeenieClassId == ASHERON) s_AsheronBolts++;
                src.EnqueueBroadcastMotion(new Motion(src, MotionCommand.Ready));
            });
            chain.EnqueueChain();
        }

        /// <summary>Asheron keeps 8 to 16 m from Bael with a clear line to him; otherwise he runs to a spot 11 m from Bael on
        /// his own side (his bolts flew into the rock in the owner's playtest, 261028-0020). True: he can cast from here.</summary>
        static bool InPosition(Creature a, Creature b) => CanCast(a, b, out bool sees) || MoveToCast(a, b, sees);

        static bool CanCast(Creature a, Creature b, out bool sees)
        {
            sees = true;
            if (a.Location == null || b.Location == null) return false;
            float d = a.Location.DistanceTo(b.Location);
            try { sees = a.IsDirectVisible(b); } catch { }
            float min = Foe(b) == null ? 2f : 8f;   // in the duel (no player close) Bael comes to him, and he stands his ground
            return d >= min && d <= 16f && sees;
        }

        static bool MoveToCast(Creature a, Creature b, bool sees)
        {
            if (a.Location == null || b.Location == null) return false;
            var bp = b.Location.Pos; var ap = a.Location.Pos;
            var dir = new System.Numerics.Vector3(ap.X - bp.X, ap.Y - bp.Y, 0f);
            if (dir.LengthSquared() < 0.01f || !sees) dir = new System.Numerics.Vector3(0f, 1f, 0f);   // no line: come round to the open water side
            dir = System.Numerics.Vector3.Normalize(dir);
            float x = Math.Clamp(bp.X + dir.X * 11f, 158f, 190f), y = Math.Clamp(bp.Y + dir.Y * 11f, 104f, 126f);
            var to = new Position(CellOf(x, y), x, y, 1.11f, 0f, 0f, 0f, 1f);
            Act(a, () => { if (a.CombatMode != CombatMode.Magic) a.SetCombatMode(CombatMode.Magic); a.MoveTo(to, 1.0f); });
            return false;
        }

        // ------------------------------------------------------------------ his knock-back slam (owner 2026-10-28)
        // "some bosses have knock-back. i think this boss should also have it." The Unbound Queen's pattern: the shadowfall
        // telegraph (BossWarning.Warn) and a line, SLAM_DELAY to step clear, then every player within SLAM_RADIUS is hit
        // (SLAM_FRAC of max health, through the scene's damage cap) and knocked back (Knockback.PushFrom: our client moves;
        // retail clients are not sent it). Never toward the rock: a push whose landing would be in its footprint goes north.
        const float ENGAGE = 40f, RANGED = 45f;   // he duels Asheron only with no player within ENGAGE (261029-0003)
        const double SLAM_DELAY = 2.0, SLAM_RADIUS = 7.0, SLAM_FRAC = 0.08, SLAM_PUSH_M = 4.5;
        const float SLAM_H = 7f, SLAM_UP = 3f;

        internal static Player Foe(Creature b) =>
            b?.Location == null ? null : PlayersNear(b, ENGAGE).Where(p => !LightbringerSceneRule.InRock(p.Location.PositionX, p.Location.PositionY))
                                                              .OrderBy(p => p.Location.DistanceTo(b.Location)).FirstOrDefault();

        /// <summary>A player within ENGAGE (not on the rock): Bael's AI is closing to melee and must not be turned.</summary>
        static bool Closing(Creature b) =>
            b?.Location != null && PlayersNear(b, ENGAGE).Any(p => !LightbringerSceneRule.InRock(p.Location.PositionX, p.Location.PositionY));

        static void SlamWarn()
        {
            var b = s_Bael;
            if (b == null || b.IsDestroyed || b.WeenieClassId != BAEL_FIGHT || b.Location == null) return;
            if (!PlayersNear(b, (float)SLAM_RADIUS + 4f).Any()) return;   // nobody close: no slam this time
            var at = b.Location.Pos;   // the slam lands where he stands now
            var chain = new Seq();
            chain.AddAction(b, () =>
            {
                b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell));
                try { BossWarning.Warn(b); } catch { }
                b.EnqueueBroadcast(new GameMessageHearSpeech("Down, all of you! Kneel before the Hopeslayer!", b.Name, b.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                b.EnqueueBroadcast(new GameMessageSystemChat("Bael'Zharon raises his sword high over the water. Get back!", ChatMessageType.Broadcast), SPEECH_RANGE);
            });
            chain.AddDelaySeconds(SLAM_DELAY);
            chain.AddAction(b, () => Slam(b, at));
            chain.EnqueueChain();
        }

        static void Slam(Creature b, System.Numerics.Vector3 at)
        {
            if (b.IsDead || b.Location == null) return;
            b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.AttackHigh1));
            b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.Explode));
            s_LastAttack = Now();
            int hit = 0;
            foreach (var p in PlayersNear(b, (float)SLAM_RADIUS + 2f).ToList())
            {
                if (p.Location.Landblock != b.Location.Landblock) continue;
                var d = p.Location.Pos - at; d.Z = 0;
                if (d.Length() > SLAM_RADIUS) continue;
                var dir = d.LengthSquared() < 0.01f ? new System.Numerics.Vector3(0, 1, 0) : System.Numerics.Vector3.Normalize(d);
                var land = p.Location.Pos + dir * (float)SLAM_PUSH_M;
                if (LightbringerSceneRule.InRock(land.X, land.Y)) dir = new System.Numerics.Vector3(0, 1, 0);   // north, away from the rock
                int dealt = p.TakeDamage(b, DamageType.Bludgeon, (float)(LightbringerFightRule.SlamFrac(s_FightN, s_Phase) * p.Health.MaxValue), BodyPart.Chest);
                p.SendMessage($"Bael'Zharon's blow throws you back for {dealt} points of damage!", ChatMessageType.Combat);
                Knockback.PushFrom(p, p.Location.Pos - dir, SLAM_H, SLAM_UP);
                hit++;
            }
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: Bael's slam, {hit} hit");
        }

        /// <summary>Bael's own spells at the players around him, every 10 s once landed (ticket 261028-0021): his Nether
        /// Streak and Arc, or Demon's Tongues; capped like his blows (MAX_SHARE per WINDOW_S).</summary>
        static void BaelSpell()
        {
            var b = s_Bael;
            if (b == null || b.IsDestroyed || b.WeenieClassId != BAEL_FIGHT || b.CurrentLandblock == null) return;
            // A scripted cast turns him and plays a gesture, which cancels the walk his AI just started: while a player is
            // within ENGAGE he is closing to melee, so the scripted spells wait (box test 2026-10-28)
            if (Closing(b)) return;
            // his reach: players beyond his blade (more than 6 m) first, up to RANGED m; anyone near if none is farther
            var near = PlayersNear(b, RANGED).ToList();
            if (near.Count == 0) return;
            var far = near.Where(x => x.Location.DistanceTo(b.Location) > 6f).ToList();
            var pool = far.Count > 0 ? far : near;
            var p = pool[s_Rng.Next(pool.Count)];
            uint id = new uint[] { 5332, 5333, 2042 }[s_Rng.Next(3)];
            var chain = new Seq();
            chain.AddAction(b, () => { if (!b.IsDestroyed) { b.TurnTo(p); b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell)); } });
            chain.AddDelaySeconds(1.0);
            chain.AddAction(b, () => { if (!b.IsDestroyed && !p.IsDead) b.TryCastSpell(new Spell(id), p, b, null, false, false, false); });
            chain.EnqueueChain();
        }

        /// <summary>Asheron takes his stance against the landed Bael (he stood idle in the owner's playtest, 261028-0013).</summary>
        static void AsheronReady()
        {
            var a = s_Asheron; var b = s_Bael;
            if (a == null || a.IsDestroyed || a.CurrentLandblock == null) return;
            Act(a, () => { if (a.CombatMode != CombatMode.Magic) a.SetCombatMode(CombatMode.Magic); if (b != null && !b.IsDestroyed) Face(a, b); });
        }

        // ------------------------------------------------------------------ Asheron, scripted (owner 2026-10-28)
        // "asheron didnt fight very well. he didnt even face bael for much of the fight. also can he heal players around
        // him? i want asheron casting spells the whole time." ACE's Creature.TurnTo(WorldObject) never turns the server's
        // own heading, so the next position update snapped him back to where he had been looking. Now the scene drives
        // him like Bael's melee: each cycle he faces Bael (a heading turn the server keeps, Face), plays the spell's own
        // windup and cast gestures as a player mage does (its scarabs and talisman, at the player cast speed), and looses
        // a retail war spell (bolt, streak or arc) on Bael, his share capped as before (CapOnActor). Out of his 8 to 16 m
        // with a clear line he runs to a spot that has one first. Every ASH_HEAL_EVERY s or so the cycle is a heal on the
        // most wounded player within ASH_HEAL_RANGE m instead: the Heal Other gestures, the heal effect, ASH_HEAL_FRAC of
        // their max health, and now and then a line. Never on Bael.
        static bool s_AshOn;
        static double s_AshBusy, s_AshNextMove, s_AshNextHeal, s_AshNextLine;
        static int s_AshHeals, s_AshLine;
        const double ASH_GAP = 0.2, ASH_HEAL_EVERY = 7, ASH_HEAL_BELOW = 0.85, ASH_HEAL_FRAC = 0.12, ASH_LINE_EVERY = 40;
        const float ASH_HEAL_RANGE = 20f, ASH_CAST_SPEED = 2.0f;   // ACE Player.CastSpeed, from retail pcaps
        const uint ASH_HEAL_SPELL = 4310;   // Incantation of Heal Other: its gestures; the amount is the scene's own
        static readonly uint[] ASHERON_SPELLS =
        {
            4451, 4439, 4447, 4443,   // Incantations of Lightning, Flame, Frost and Force Bolt
            4452, 4440, 4448, 4444,   // ... Streak
            4426, 4423, 4425, 4424,   // ... Arc
        };
        static readonly string[] ASH_HEAL_LINES =
        {
            "Stand fast. The Light is with you.",
            "Rise, friend. I will not lose another of you to him.",
            "Hold on. You do not fight him alone.",
            "Be whole again, and strike true.",
        };

        /// <summary>Turns c to face t with a heading the server keeps (ACE's TurnTo(Position) sets Location's rotation when
        /// the turn completes). Returns the turn's time.</summary>
        static float Face(Creature c, WorldObject t)
        {
            if (c.Location == null || t?.Location == null) return 0f;
            var dir = t.Location.ToGlobal() - c.Location.ToGlobal(); dir.Z = 0f;
            if (dir.LengthSquared() < 0.01f) return 0f;
            var face = new Position(c.Location);
            face.Rotate(System.Numerics.Vector3.Normalize(dir));
            return c.TurnTo(face);
        }

        static void AsheronTick(double now, Creature b)
        {
            var a = s_Asheron;
            if (a == null || a.IsDestroyed || a.Location == null || b.Location == null || now < s_AshBusy) return;
            Player hurt = null;
            if (now >= s_AshNextHeal)
                hurt = PlayersNear(a, ASH_HEAL_RANGE).Where(p => p.Health.Current < p.Health.MaxValue * ASH_HEAL_BELOW)
                                                    .OrderBy(p => (double)p.Health.Current / Math.Max(1u, p.Health.MaxValue)).FirstOrDefault();
            if (hurt != null) { s_AshNextHeal = now + ASH_HEAL_EVERY; Cast(now, a, hurt, ASH_HEAL_SPELL, true); return; }
            if (!CanCast(a, b, out bool sees))
            {
                if (now < s_AshNextMove) return;
                s_AshNextMove = now + 1.5;
                MoveToCast(a, b, sees);
                return;
            }
            Cast(now, a, b, ASHERON_SPELLS[s_Rng.Next(ASHERON_SPELLS.Length)], false);
        }

        /// <summary>One whole cast, as a player mage plays it: turn, windup gestures, the cast gesture, release mid-gesture.</summary>
        static void Cast(double now, Creature a, WorldObject target, uint id, bool heal)
        {
            var spell = new Spell(id);
            List<MotionCommand> winds; MotionCommand gesture;
            try { spell.Formula.GetMonsterFormula(); winds = spell.Formula.WindupGestures; gesture = spell.Formula.CastGesture; }
            catch { winds = new List<MotionCommand>(); gesture = MotionCommand.Invalid; }
            if (gesture == MotionCommand.Invalid) gesture = heal ? MotionCommand.MagicHeal : MotionCommand.MagicBlast;
            float Len(MotionCommand m) { try { return ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(a.MotionTableId, MotionStance.Magic, m, ASH_CAST_SPEED); } catch { return 0f; } }

            float turn = 0f;
            try { turn = Math.Clamp(a.GetRotateDelay(target), 0f, 1.5f); } catch { }
            var chain = new Seq();
            chain.AddAction(a, () =>
            {
                if (a.CombatMode != CombatMode.Magic) a.SetCombatMode(CombatMode.Magic);
                if (!target.IsDestroyed) Face(a, target);
            });
            double t = turn + 0.1;
            chain.AddDelaySeconds(turn + 0.1);
            void Gesture(MotionCommand m, double hold)
            {
                chain.AddAction(a, () =>
                {
                    var mo = new Motion(MotionStance.Magic, m, ASH_CAST_SPEED);
                    mo.MotionState.TurnSpeed = 2.25f;
                    a.CurrentMotionState = mo;
                    a.EnqueueBroadcastMotion(mo);
                });
                chain.AddDelaySeconds(hold); t += hold;
            }
            foreach (var w in winds) { float l = Len(w); if (l > 0f) Gesture(w, l); }
            float cl = Len(gesture); if (cl <= 0f) cl = 1.0f;
            Gesture(gesture, cl * 0.5);
            chain.AddAction(a, () =>
            {
                if (target.IsDestroyed || target.Location == null || a.Location == null) return;
                if (heal) HealPlayer(a, (Player)target);
                else if (target is Creature c && !c.IsDead && c.CurrentLandblock != null)
                {
                    a.TryCastSpell(spell, c, a, null, false, false, false);
                    s_AsheronBolts++;
                }
            });
            chain.AddDelaySeconds(cl * 0.5); t += cl * 0.5;
            chain.AddAction(a, () => { var r = new Motion(MotionStance.Magic, MotionCommand.Ready); a.CurrentMotionState = r; a.EnqueueBroadcastMotion(r); });
            chain.EnqueueChain();
            s_AshBusy = now + t + ASH_GAP;
        }

        static void HealPlayer(Creature a, Player p)
        {
            if (p.IsDead || p.Location == null || p.Location.DistanceTo(a.Location) > ASH_HEAL_RANGE + 5f) return;
            int got = p.UpdateVitalDelta(p.Health, (int)Math.Max(1, p.Health.MaxValue * ASH_HEAL_FRAC));
            if (got > 0) p.DamageHistory.OnHeal((uint)got);
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.HealthUpRed));
            p.SendMessage($"Asheron heals you for {got} points of health.", ChatMessageType.Magic);
            s_AshHeals++;
            double now = Now();
            if (now >= s_AshNextLine)
            {
                s_AshNextLine = now + ASH_LINE_EVERY;
                Say(Who.Asheron, ASH_HEAL_LINES[s_AshLine++ % ASH_HEAL_LINES.Length]);
            }
        }

        // ------------------------------------------------------------------ the fight's rules (owner 2026-10-29)
        // Approved: (1) Shards of Light rise in the red water; carried to Asheron (or brought close to Bael) each weakens
        // him in visible stages, his heal fading; (2) win or lose: enough shards and Asheron banishes him, everyone who
        // fought earns Asheron's Defender; too few and he withdraws mocking; (3) Asheron shields a player about to die near
        // him, once a fight; (4) telegraphed meteors fall on the fighters; (5) a second phase from the halfway mark. All of
        // it scales to who is fighting (LightbringerFightRule): "a real threat that a group of the players present can beat".
        const double FIGHT_FROM = 94, PHASE2_AT = 290, SHARDS_UNTIL = 478;
        const float SHARD_SCALE = 9.0f, SHARD_LIFT = 0.4f;   // 3x read as a blip at 30 m and 6x as a small blob (box captures 2026-10-29)
        const double SHARD_PULSE = 2.5;   // every second pulse also raises retail's level-up pillar of light (a beam to see from afar)
        static double s_NextPulse; static int s_Pulse;
        const uint SHARD_BASE = 31879;            // retail's Radiant Shard: its look; the name and purpose are the scene's
        const string SHARD_NAME = "Shard of Light";
        const float DELIVER_ASHERON = 6f, SHIELD_RANGE = 25f, METEOR_RADIUS = 4f;
        const double METEOR_WARN = 2.0, SHIELD_S = 3.0, SHIELD_TO = 0.3;
        const uint TITLE_DEFENDER = 492;           // CharacterTitle.AsheronsDefender (retail gives it nowhere near here)
        const string WIN_STAMP = "LightbringerStoodWithAsheron";
        static int s_Phase = 1, s_Delivered, s_Stage, s_FightN = 1;
        internal static int s_Meteors, s_MeteorHits, s_Shields, s_ShardsMade;
        static bool s_Won, s_HealGone;
        static double s_NextShard, s_NextMeteor, s_FightBegan;
        static readonly List<WorldObject> s_ShardItems = new List<WorldObject>();
        static readonly HashSet<uint> s_Fighters = new HashSet<uint>(), s_Shielded = new HashSet<uint>(), s_Fogged = new HashSet<uint>(), s_ShardTold = new HashSet<uint>();
        static double s_NextProgress;
        static readonly Dictionary<uint, double> s_ShieldUntil = new Dictionary<uint, double>();
        static readonly Queue<(double t, int n)> s_NSeen = new Queue<(double, int)>();

        static void FightReset()
        {
            s_Phase = 1; s_Delivered = s_Stage = 0; s_FightN = 1; s_Meteors = s_MeteorHits = s_Shields = s_ShardsMade = 0;
            s_Won = s_HealGone = false; s_NextShard = s_NextMeteor = s_FightBegan = 0;
            s_Fighters.Clear(); s_Shielded.Clear(); s_ShieldUntil.Clear(); s_NSeen.Clear(); s_ShardTold.Clear(); s_NextProgress = 0;
        }

        static int Needed => LightbringerFightRule.ShardsNeeded(s_FightN);

        static void FightTick(double now, double t, Creature b)
        {
            if (s_Won || b.Location == null) return;
            if (s_FightBegan == 0) { s_FightBegan = now; s_NextShard = now + 4; s_NextMeteor = now + 12; }
            // who is fighting: the most seen in the last minute (a death does not make it easier for the rest)
            var foes = PlayersNear(b, ENGAGE).Where(p => !LightbringerSceneRule.InRock(p.Location.PositionX, p.Location.PositionY)).ToList();
            foreach (var p in foes) s_Fighters.Add(p.Guid.Full);
            s_NSeen.Enqueue((now, foes.Count));
            while (s_NSeen.Count > 0 && now - s_NSeen.Peek().t > 60) s_NSeen.Dequeue();
            s_FightN = Math.Max(1, s_NSeen.Max(x => x.n));

            if (t >= PHASE2_AT && s_Phase < 2) Phase2(b);
            if (s_Phase >= 2) Fog(b, true);

            // the shards
            s_ShardItems.RemoveAll(o => o == null || o.IsDestroyed);
            foreach (var o in s_ShardItems.ToList())
            {
                var holder = (o.Container as Player) ?? (o.Container?.Container as Player);
                if (holder == null || holder.IsDead || holder.Location == null) continue;
                // the first pickup explains itself (owner 2026-10-29: "why am i getting them")
                if (s_ShardTold.Add(holder.Guid.Full))
                    holder.SendMessage("A Shard of Light! Carry it to Asheron to weaken Bael'Zharon.", ChatMessageType.Broadcast);
                // given to Asheron only, by walking up to him (it left his pack at once near Bael before: "they seem to
                // leave my inventory after being picked up")
                bool toAsh = s_Asheron?.Location != null && holder.Location.DistanceTo(s_Asheron.Location) <= DELIVER_ASHERON;
                if (toAsh) Deliver(holder, o, true, b);
                if (s_Won) return;
            }
            if (s_NextProgress == 0) s_NextProgress = now + 45;
            if (now >= s_NextProgress)
            {
                s_NextProgress = now + 45;
                b.EnqueueBroadcast(new GameMessageSystemChat($"Shards given to Asheron: {s_Delivered} of {Needed}.", ChatMessageType.Broadcast), SPEECH_RANGE);
            }
            if (now >= s_NextPulse)
            {
                // each shard on the ground pulses light upward, white and gold in turn, so it reads against the red water
                s_NextPulse = now + SHARD_PULSE;
                bool beam = true;
                var fx = (s_Pulse++ & 2) == 0 ? PlayScript.EnchantUpWhite : PlayScript.SkillUpYellow;
                foreach (var o in s_ShardItems)
                    if (!o.IsDestroyed && o.Container == null && o.CurrentLandblock != null)
                    {
                        o.EnqueueBroadcast(new GameMessageScript(o.Guid, fx));
                        if (beam) o.EnqueueBroadcast(new GameMessageScript(o.Guid, PlayScript.LevelUp));
                    }
            }
            if (t < SHARDS_UNTIL && now >= s_NextShard)
            {
                s_NextShard = now + LightbringerFightRule.ShardEvery(s_FightN);
                int lying = s_ShardItems.Count(o => o.Container == null && o.CurrentLandblock != null);
                if (lying < LightbringerFightRule.ShardsOut(s_FightN)) SpawnShard(b);
            }

            // the meteors
            if (now >= s_NextMeteor && foes.Count > 0)
            {
                s_NextMeteor = now + LightbringerFightRule.MeteorEvery(s_FightN, s_Phase);
                MeteorFall(b, foes[s_Rng.Next(foes.Count)]);
            }
        }

        static void SpawnShard(Creature b)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(SHARD_BASE);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: shard weenie {SHARD_BASE} missing"); return; }
            // Where players fight. The owner asked for the shallows (261029-0012: "shards are hard to see in the red
            // water"), but ACE's terrain data has no shallows here: every cell from the rock to 30 m north of Asheron is
            // entirely water (the map is logged at each scene's start; the shore is 30-50 m north and far west). So the
            // shards stay in the fight and are made visible instead: three times their size, a light pulse every 2.5 s.
            for (int tries = 0; tries < 12; tries++)
            {
                float x = 166f + (float)s_Rng.NextDouble() * 24f, y = 103f + (float)s_Rng.NextDouble() * 25f;
                if (LightbringerSceneRule.InRock(x, y)) continue;
                var at = new Position(CellOf(x, y), x, y, 1.15f, 0f, 0f, 0f, 1f);
                if (b.Location.DistanceTo(at) < 6f) continue;   // not under his feet
                // above the water, not under it (owner 2026-10-29, a screenshot: the red surface drew over them). The water
                // is drawn at the terrain plane (AcWaterFx.Depth; retail draws it where the terrain is) and bodies sink
                // below it, so 1.15 (the players' feet here) was under the surface (about 2.0): float SHARD_LIFT over it.
                try { at.PositionZ = at.GetTerrainZ() + SHARD_LIFT; } catch { at.PositionZ = 2.0f + SHARD_LIFT; }
                wo.Location = at;
                break;
            }
            if (wo.Location == null) { wo.Destroy(); return; }
            wo.Name = SHARD_NAME;
            wo.LongDesc = "A shard of Asheron's light, torn from the red water. Carry it to Asheron and he will turn its light against the Hopeslayer.";
            wo.Value = 0;
            wo.Lifespan = 600;   // it crumbles by itself in any pack it is left in
            wo.ObjScale = SHARD_SCALE;   // big enough to read from across the water (261029-0012)
            wo.GravityStatus = false;    // it hovers where it is put
            lock (s_Lock) s_ShardItems.Add(wo);
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!wo.EnterWorld()) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: a shard could not enter at {wo.Location}"); return; }
                    wo.EnqueueBroadcast(new GameMessageScript(wo.Guid, PlayScript.EnchantUpWhite));
                    s_ShardsMade++;
                    Mod.Log.Info($"[RevivalGuard] LightbringerScene: shard {s_ShardsMade} at ({wo.Location.PositionX:F0}, {wo.Location.PositionY:F0}, {wo.Location.PositionZ:F1})");
                    if (s_ShardsMade == 1 || s_ShardsMade % 4 == 0)
                        wo.EnqueueBroadcast(new GameMessageSystemChat("A Shard of Light rises from the red water. Carry it to Asheron!", ChatMessageType.Broadcast), SPEECH_RANGE);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: shard: {e.Message}"); }
            }));
        }

        /// <summary>A shore cell: part water, part land (ACE's own cell water type, from the landblock's terrain).</summary>
        static bool Shore(uint cell)
        {
            var w = Water(cell);
            if (w == 'P') return true;
            if (w != 'N') return false;
            // a land cell beside a water one: the water's edge, on the land side (box 2026-10-29: no part-water cell in
            // the fight's area, so the first rule never matched)
            int i = (int)(cell & 0xFFFF) - 1, cx = i / 8, cy = i % 8;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx > 7 || ny > 7) continue;
                    char n = Water((cell & 0xFFFF0000) | (uint)(nx * 8 + ny + 1));
                    if (n == 'E' || n == 'P') return true;
                }
            return false;
        }

        /// <summary>A land cell's water: E entirely, P partly, N not, ? unknown.</summary>
        static char Water(uint cell)
        {
            try
            {
                var c = ACE.Server.Physics.Common.LScape.get_landcell(cell);
                if (c == null) return '?';
                return c.WaterType == ACE.Server.Physics.Common.LandDefs.WaterType.EntirelyWater ? 'E'
                     : c.WaterType == ACE.Server.Physics.Common.LandDefs.WaterType.PartiallyWater ? 'P' : 'N';
            }
            catch { return '?'; }
        }

        static void LogWaterMap()
        {
            var sb = new System.Text.StringBuilder();
            for (int cy = 7; cy >= 0; cy--) { sb.Append(' '); for (int cx = 0; cx < 8; cx++) sb.Append(Water(CELL_ROCK & 0xFFFF0000 | (uint)(cx * 8 + cy + 1))); }
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: 0x{CELL_ROCK >> 16:X4} water by cell, north row first, west to east:{sb}");
        }

        static void Deliver(Player p, WorldObject o, bool toAsheron, Creature b)
        {
            if (!p.TryConsumeFromInventoryWithNetworking(o, 1)) return;
            s_ShardItems.Remove(o);
            s_Delivered++;
            int need = Needed;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: {p.Name} delivers a shard to {(toAsheron ? "Asheron" : "Bael")} ({s_Delivered} of {need}, {s_FightN} fighting)");
            b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.HealthDownVoid));
            if (toAsheron && s_Asheron != null) s_Asheron.EnqueueBroadcast(new GameMessageScript(s_Asheron.Guid, PlayScript.EnchantUpWhite));
            p.SendMessage($"Asheron draws the shard's light into his spell: {s_Delivered} of {need}.", ChatMessageType.Broadcast);
            b.EnqueueBroadcast(new GameMessageSystemChat($"{p.Name} brings a Shard of Light to Asheron: {s_Delivered} of {need}.", ChatMessageType.Broadcast), SPEECH_RANGE);
            s_NextProgress = Now() + 45;
            int st = LightbringerFightRule.Stage(s_Delivered, need);
            if (st >= 3) { Win(b); return; }
            if (st > s_Stage)
            {
                s_Stage = st;
                b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.SkillDownBlack));
                b.EnqueueBroadcast(new GameMessageSystemChat(st == 1 ? "The light sears Bael'Zharon. His dark aura dims."
                    : "Bael'Zharon's aura gutters. The dark no longer closes his wounds!", ChatMessageType.Broadcast), SPEECH_RANGE);
                Say(Who.Asheron, st == 1 ? "His darkness thins! More light, citizens!" : "He weakens! Bring me the light, quickly!");
                Say(Who.Bael, st == 1 ? "That light is nothing. Nothing!" : "Stop them! Tear the light from their hands!");
            }
        }

        /// <summary>Enough light in time: Asheron's banishing, the victory, the reward, then the scene's own ending.</summary>
        static void Win(Creature b)
        {
            s_Won = true; s_AshOn = false;
            double t = Now() - s_Start;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: WON at {t:F0} s ({s_Delivered} shards, {s_FightN} fighting, {s_Fighters.Count} fought)");
            var steps = new List<Step>();
            void At(double dt, string label, Action a) => steps.Add(new Step { At = t + dt, Label = label, Run = a });
            At(0, "banish", () =>
            {
                Say(Who.Asheron, "Now, citizens! Your light, with mine!");
                var a = s_Asheron;
                if (a != null && !a.IsDestroyed)
                    Act(a, () =>
                    {
                        if (s_Bael != null) Face(a, s_Bael);
                        var mo = new Motion(MotionStance.Magic, MotionCommand.MagicPowerUp10, 1.0f); a.EnqueueBroadcastMotion(mo);
                        a.EnqueueBroadcast(new GameMessageScript(a.Guid, PlayScript.EnchantUpWhite));
                    });
            });
            At(2.5, "banish blast", () =>
            {
                var bb = s_Bael;
                if (bb == null || bb.IsDestroyed) return;
                Act(bb, () =>
                {
                    bb.EnqueueBroadcast(new GameMessageScript(bb.Guid, PlayScript.SkillDownBlack));
                    bb.EnqueueBroadcast(new GameMessageScript(bb.Guid, PlayScript.Explode));
                    bb.EnqueueBroadcast(new GameMessageHearSpeech("No! This light... it burns! You have not seen the last of me!", bb.Name, bb.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                });
            });
            At(5, "banished", () =>
            {
                World("Asheron's light breaks the Hopeslayer! The people of Dereth have driven Bael'Zharon from Cragstone.");
                Banish(s_Bael); s_Bael = null;
                foreach (var c in s_Adds) Vanish(c, PlayScript.Destroy);
                s_Adds.Clear();
                Reward();
            });
            At(10, "line", () => Say(Who.Asheron, "You stood with me, and he is broken for a time. Seek the Chosen of Asheron. Assemble the artifacts, and finish this in his own Sepulcher."));
            At(17, "end", () => World($"Bael'Zharon has been driven from Cragstone. Asheron will stand against him here again {Returns()}."));
            At(24, "asheron leaves", () => { Vanish(s_Asheron, PlayScript.PortalEntry); s_Asheron = null; });
            lock (s_Lock)
            {
                if (s_Steps == null) return;
                s_Steps = s_Steps.Take(s_StepIdx).Concat(steps).ToList();
                s_End = s_Start + t + 25;
            }
        }

        static void Banish(Creature b)
        {
            if (b == null || b.IsDestroyed) return;
            Retire(b, 8);
            var chain = new Seq();
            chain.AddAction(b, () => { if (!b.IsDestroyed) { b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.PortalStorm)); b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.DisappearDestroy)); } });
            chain.AddDelaySeconds(1.2);
            chain.AddAction(b, () => { if (!b.IsDestroyed) b.Destroy(); });
            chain.EnqueueChain();
        }

        /// <summary>Everyone who fought (within ENGAGE of Bael at any time): Asheron's Defender, once; the win is stamped.</summary>
        static void Reward()
        {
            int given = 0;
            foreach (var g in s_Fighters)
            {
                var p = PlayerManager.GetOnlinePlayer(g);
                if (p == null) continue;
                try
                {
                    bool had = p.QuestManager.HasQuest(WIN_STAMP);
                    p.QuestManager.Stamp(WIN_STAMP);
                    p.AddTitle(TITLE_DEFENDER);
                    given++;
                    p.SendMessage(had ? "You stood with Asheron again, and Bael'Zharon was driven off."
                                      : "You stood with Asheron, and Bael'Zharon was driven off. You have earned the title Asheron's Defender.", ChatMessageType.Broadcast);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: reward {p.Name}: {e.Message}"); }
            }
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: rewarded {given} of {s_Fighters.Count} who fought");
        }

        static void Phase2(Creature b)
        {
            s_Phase = 2;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: phase 2 ({s_FightN} fighting, {s_Delivered} of {Needed} shards)");
            Act(b, () =>
            {
                b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell));
                b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.BlackMadness));
                b.EnqueueBroadcast(new GameMessageHearSpeech("Enough! Now you will know the Hopeslayer's true wrath!", b.Name, b.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                b.EnqueueBroadcast(new GameMessageSystemChat("The sky darkens over Cragstone. Bael'Zharon's fury grows!", ChatMessageType.Broadcast), SPEECH_RANGE);
            });
            Fog(b, true);
            SummonShadows();
        }

        /// <summary>Phase 2's dark sky, for every player near (retail's admin environs; cleared at the scene's end).</summary>
        static void Fog(WorldObject at, bool on)
        {
            if (on)
            {
                if (at?.Location == null) return;
                foreach (var p in PlayersNear(at, SPEECH_RANGE))
                    if (p.Session != null && s_Fogged.Add(p.Guid.Full))
                        p.Session.Network.EnqueueSend(new GameMessageAdminEnvirons(p.Session, EnvironChangeType.BlackFog));
                return;
            }
            foreach (var g in s_Fogged)
            {
                var p = PlayerManager.GetOnlinePlayer(g);
                if (p?.Session != null) p.Session.Network.EnqueueSend(new GameMessageAdminEnvirons(p.Session, EnvironChangeType.Clear));
            }
            s_Fogged.Clear();
        }

        /// <summary>A meteor's telegraph on the chosen player (the shadowfall on our client, a red glow and words on any),
        /// then METEOR_WARN later the spot is struck: everyone still within METEOR_RADIUS of it is hit.</summary>
        static void MeteorFall(Creature b, Player target)
        {
            var spot = new Position(target.Location);
            s_Meteors++;
            Shadowfall(target);
            target.EnqueueBroadcast(new GameMessageScript(target.Guid, PlayScript.SkillDownRed));
            target.SendMessage("The sky burns above you. A meteor is falling! Move!", ChatMessageType.Broadcast);
            var chain = new Seq();
            chain.AddDelaySeconds(METEOR_WARN);
            chain.AddAction(b, () =>
            {
                if (b.IsDestroyed || b.Location == null) return;
                foreach (var p in PlayersNear(b, RANGED + 10f).ToList())
                {
                    if (p.Location.Landblock != spot.Landblock || p.Location.DistanceTo(spot) > METEOR_RADIUS) continue;
                    s_MeteorHits++;
                    b.TryCastSpell(new Spell(METEOR), p, b, null, false, false, false);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>BossWarning's shadowfall over a body, for this scene whatever rg_boss_telegraphs says.</summary>
        static void Shadowfall(WorldObject on)
        {
            string payload = "{\"k\":\"boss_shadowfall\",\"g\":" + on.Guid.Full + "}";
            foreach (var p in PlayersNear(on, 60f))
                if (p.Session != null && ModChannel.Capable(p.Session)) ModChannel.Send(p.Session, "vfx", payload);
        }

        /// <summary>Any blow on a player during the fight: a killing one, near Asheron, is turned aside once per player per
        /// fight (his shield, SHIELD_S of safety, back to SHIELD_TO of max health).</summary>
        internal static void Shield(Player p, ref float amount)
        {
            if (s_Steps == null || s_Won || amount <= 0 || p == null) return;
            double now = Now();
            if (s_ShieldUntil.TryGetValue(p.Guid.Full, out var until) && now < until) { amount = 0; return; }
            if (amount < p.Health.Current) return;
            var a = s_Asheron;
            if (a?.Location == null || p.Location == null || p.Location.DistanceTo(a.Location) > SHIELD_RANGE) return;
            if (!s_Shielded.Add(p.Guid.Full)) return;
            amount = 0;
            s_Shields++;
            s_ShieldUntil[p.Guid.Full] = now + SHIELD_S;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: Asheron shields {p.Name}");
            var who = p;
            Act(a, () =>
            {
                Face(a, who);
                a.EnqueueBroadcastMotion(new Motion(MotionStance.Magic, MotionCommand.MagicHeal, ASH_CAST_SPEED));
                who.EnqueueBroadcast(new GameMessageScript(who.Guid, PlayScript.ShieldUpYellow));
                int want = (int)(who.Health.MaxValue * SHIELD_TO) - (int)who.Health.Current;
                if (want > 0) who.UpdateVitalDelta(who.Health, want);
                a.EnqueueBroadcast(new GameMessageHearSpeech($"Not today, {who.Name}! Stand behind my light.", a.Name, a.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                who.SendMessage("Asheron's shield turns aside the killing blow! He cannot do it twice.", ChatMessageType.Magic);
            });
        }

        /// <summary>A shadow's hit on a player: at most the scaled share of their max health.</summary>
        internal static void ScaleAdd(Player p, ref float amount)
        {
            if (s_Steps == null || p == null) return;
            amount = Math.Min(amount, (float)(LightbringerFightRule.AddFrac(s_FightN, s_Phase) * p.Health.MaxValue));
        }

        /// <summary>Bael's spell damage on a player, as the scaled share of their max health.</summary>
        internal static void ScaleSpell(Player p, uint spellId, ref float damage)
        {
            double frac = spellId == METEOR ? LightbringerFightRule.MeteorFrac(s_FightN, s_Phase) : LightbringerFightRule.SpellFrac(s_FightN, s_Phase);
            damage = (float)(frac * p.Health.MaxValue);
        }

        static void ClearShards()
        {
            foreach (var o in s_ShardItems.ToList())
            {
                try
                {
                    if (o.IsDestroyed) continue;
                    var holder = (o.Container as Player) ?? (o.Container?.Container as Player);
                    if (holder != null && holder.TryConsumeFromInventoryWithNetworking(o, 1))
                        holder.SendMessage("Your Shard of Light fades as the fight at Cragstone ends.", ChatMessageType.Broadcast);
                    else o.Destroy();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene: shard cleanup: {e.Message}"); }
            }
            s_ShardItems.Clear();
        }

        // ------------------------------------------------------------------ he heals himself, visibly (ticket 261028-0014)
        static double s_LastHeal, s_LastHealLine;
        static int s_Heals, s_AsheronBolts, s_AsheronHits;
        /// <summary>For @lbscene status: degrees between Asheron's server heading and Bael.</summary>
        static string AshFacing()
        {
            try { return s_Asheron?.Location != null && s_Bael?.Location != null ? $"{s_Asheron.GetAngle(s_Bael):F0}" : "-"; } catch { return "?"; }
        }
        static int s_HealLine;
        const double HEAL_BELOW = 0.4, HEAL_EVERY = 45, HEAL_LINE_EVERY = 0;   // rare and meaningful (ticket 261028-0022)
        static readonly string[] HEAL_LINES =
        {
            "Your blows are nothing. The Nameless mends what you break.",
            "I drink the dark, and the dark makes me whole.",
            "Did you think me so easily wounded? Again!",
        };

        /// <summary>Below half health (or near the floor), at most every HEAL_EVERY s: the cast gesture, a heal effect, a line
        /// now and then, and he is whole again. Never silently each tick, as the first build did.</summary>
        static void HealCheck(double now, Creature b)
        {
            // One rule, no fast path: the floor case (every 3 s at 10%) chained six heals in a row in the owner's second
            // playtest. At 1 health he simply cannot be hurt further (CapOnActor) until the heal comes round.
            double frac = (double)b.Health.Current / Math.Max(1u, b.Health.MaxValue);
            if (frac >= HEAL_BELOW || now - s_LastHeal < HEAL_EVERY) return;
            double scale = LightbringerFightRule.HealScale(s_Stage);
            if (scale <= 0)
            {
                if (!s_HealGone) { s_HealGone = true; Local("Bael'Zharon reaches for the dark, and it does not answer. His wounds stay open."); }
                s_LastHeal = now;
                return;
            }
            s_LastHeal = now; s_Heals++;
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: Bael heals at {frac:P0} ({b.Health.Current}/{b.Health.MaxValue})");
            string line = null;
            if (now - s_LastHealLine >= HEAL_LINE_EVERY) { s_LastHealLine = now; line = HEAL_LINES[s_HealLine++ % HEAL_LINES.Length]; }
            var chain = new Seq();
            chain.AddAction(b, () =>
            {
                if (b.IsDestroyed || b.IsDead) return;
                b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell));
                if (line != null) b.EnqueueBroadcast(new GameMessageHearSpeech(line, b.Name, b.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
            });
            chain.AddDelaySeconds(1.0);
            chain.AddAction(b, () =>
            {
                if (b.IsDestroyed || b.IsDead) return;
                b.UpdateVitalDelta(b.Health, (int)((b.Health.MaxValue - b.Health.Current) * scale));
                b.EnqueueBroadcast(new GameMessageScript(b.Guid, PlayScript.HealthUpRed));
                b.EnqueueBroadcast(new GameMessageSystemChat("Bael'Zharon draws on the dark and his wounds close.", ChatMessageType.Broadcast), SPEECH_RANGE);
                b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.Ready));
            });
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ his shadows (ticket 261028-0015)
        static readonly uint[] SHADOWS = { 23564, 23091 };   // Tenebrous Shadow (100), Shadow Wraith (135): retail's shadow creatures
        static readonly List<Creature> s_Adds = new List<Creature>();

        static void SummonShadows()
        {
            var b = s_Bael;
            if (b == null || b.IsDestroyed || b.WeenieClassId != BAEL_FIGHT || b.Location == null) return;
            s_Adds.RemoveAll(c => c == null || c.IsDestroyed || c.IsDead);
            int players = PlayersNear(b, 60f).Count();
            int n = Math.Min(LightbringerFightRule.Shadows(Math.Max(players, s_FightN), s_Phase), 16 - s_Adds.Count);   // scaled (owner 2026-10-29), at most 16 up
            if (n <= 0) return;
            Act(b, () =>
            {
                b.EnqueueBroadcastMotion(new Motion(b, MotionCommand.CastSpell));
                b.EnqueueBroadcast(new GameMessageHearSpeech("Rise, my shadows! Drag them down into the red water!", b.Name, b.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                b.EnqueueBroadcast(new GameMessageSystemChat("Shadows tear free of the red water at Bael'Zharon's call!", ChatMessageType.Broadcast), SPEECH_RANGE);
            });
            for (int i = 0; i < n; i++)
            {
                double ang = s_Rng.NextDouble() * Math.PI * 2, d = 6 + s_Rng.NextDouble() * 6;
                float x = Math.Clamp((float)(b.Location.PositionX + Math.Cos(ang) * d), 166f, 190f);   // west of 166 some spots refused an entry (box test)
                float y = Math.Clamp((float)(b.Location.PositionY + Math.Sin(ang) * d), 102f, 126f);   // north of the rock
                var at = new Position(CellOf(x, y), x, y, 1.11f, 0f, 0f, 0f, 1f);
                var c = Spawn(SHADOWS[i % SHADOWS.Length], at, PlayScript.Create);
                if (c == null) continue;
                s_Adds.Add(c);
                WorldManager.EnqueueAction(new ActionEventDelegate(() => { try { if (!c.IsDestroyed) c.WakeUp(false); } catch { } }));
            }
            Mod.Log.Info($"[RevivalGuard] LightbringerScene: {n} shadow(s) summoned ({players} player(s) near)");
        }

        // ------------------------------------------------------------------ no drudges in the water while he is there (261028-0015)
        const uint DRUDGE_GEN_GUID = 0x7BC9D003;   // Drudge Stalker/Ravener generator (35445) beside the rock, landblock 0xBC9D
        // Owner 2026-10-29: "drudges are still showing in the middle of the fight". The first hold paused one generator
        // (by its guid) and cleared drudges once, at the start: other drudge generators in and around 0xBC9D kept spawning,
        // and drudges walked in. Now every generator whose profile makes a Drudge, in 0xBC9D and the eight landblocks around
        // it, is held for the whole scene, and any drudge within DRUDGE_CLEAR m of the rock is removed every few seconds.
        const float DRUDGE_CLEAR = 110f;
        static readonly List<ACE.Server.Entity.GeneratorProfile> s_HeldProfiles = new List<ACE.Server.Entity.GeneratorProfile>();
        static double s_NextDrudgeSweep; static int s_DrudgesCleared;

        static bool IsDrudgeWcid(uint wcid)
        {
            try
            {
                var w = ACE.Database.DatabaseManager.World.GetCachedWeenie(wcid);
                var n = w == null ? null : ACE.Entity.Models.WeenieExtensions.GetName(w);
                return n != null && n.StartsWith("Drudge", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void HoldDrudges(bool hold)
        {
            if (!hold)
            {
                foreach (var p in s_HeldProfiles) p.NextAvailable = DateTime.UtcNow;
                Mod.Log.Info($"[RevivalGuard] LightbringerScene: drudge generators released ({s_HeldProfiles.Count} profiles; {s_DrudgesCleared} drudges kept out)");
                s_HeldProfiles.Clear(); s_DrudgesCleared = 0;
                return;
            }
            var rock = RockTop();
            uint bx = (CELL_ROCK >> 24) & 0xFF, by = (CELL_ROCK >> 16) & 0xFF;
            int cleared = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var id = new LandblockId((uint)((((bx + dx) & 0xFF) << 24) | (((by + dy) & 0xFF) << 16) | 0xFFFF));
                    if (!LandblockManager.IsLoaded(id)) continue;
                    var lb = LandblockManager.GetLandblock(id, false);
                    if (lb == null) continue;
                    foreach (var wo in lb.GetAllWorldObjectsForDiagnostics().ToList())
                    {
                        if (wo.GeneratorProfiles != null)
                            foreach (var p in wo.GeneratorProfiles)
                                if (IsDrudgeWcid(p.WeenieClassId))
                                {
                                    p.NextAvailable = DateTime.UtcNow.AddHours(1);
                                    if (!s_HeldProfiles.Contains(p)) s_HeldProfiles.Add(p);
                                }
                        if (wo is Creature c && !(wo is Player) && !c.IsDead && !c.IsDestroyed && c.Name != null
                            && c.Name.StartsWith("Drudge", StringComparison.OrdinalIgnoreCase) && !s_Adds.Contains(c)
                            && c.Location != null && c.Location.DistanceTo(rock) < DRUDGE_CLEAR)
                        {
                            cleared++;
                            Act(c, () => { if (!c.IsDestroyed) { c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.Destroy)); c.Destroy(); } });
                        }
                    }
                }
            s_DrudgesCleared += cleared;
            if (cleared > 0) Mod.Log.Info($"[RevivalGuard] LightbringerScene: {cleared} drudge(s) kept out of the fight ({s_HeldProfiles.Count} generator profiles held)");
        }

        // ------------------------------------------------------------------ 2000's rules: strike him and be cursed; speak to him and be given
        internal static bool IsScene(WorldObject wo) =>
            wo != null && (wo.WeenieClassId == BAEL_ROCK || wo.WeenieClassId == BAEL_FIGHT || wo.WeenieClassId == ASHERON);
        /// <summary>The scene's Bael'Zharon is in the world (on the rock or landed): the world boss may not rise now.</summary>
        internal static bool BaelOnStage { get { var b = s_Bael; return b != null && !b.IsDestroyed; } }
        static bool IsBael(WorldObject wo) => wo != null && (wo.WeenieClassId == BAEL_ROCK || wo.WeenieClassId == BAEL_FIGHT);

        internal static void OnHit(Creature victim, WorldObject attacker)
        {
            if (victim == null || victim.WeenieClassId != BAEL_FIGHT) return;
            var p = attacker as Player ?? attacker?.ProjectileSource as Player;
            if (p == null || p.IsDead) return;
            double now = Now();
            lock (s_Lock)
            {
                s_Attackers.Add(p.Guid.Full);
                if (s_CursedAt.TryGetValue(p.Guid.Full, out var at) && now - at < CURSE_COOLDOWN_S) return;
                s_CursedAt[p.Guid.Full] = now;
            }
            uint id = CURSES[s_Rng.Next(CURSES.Length)];
            string line = id == WEIGHT ? "Feel the weight of eternity, worm." : id == 2045 ? "Kneel before me!" : "Burn, little one.";
            var bael = victim;
            Act(bael, () =>
            {
                bael.EnqueueBroadcast(new GameMessageHearSpeech(line, bael.Name, bael.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                bael.TryCastSpell(new Spell(id), p, bael, null, false, false, false);
            });
            if (id == WEIGHT) lock (s_Lock) s_Lift.Add((p.Guid.Full, WEIGHT, now + WEIGHT_S));
        }

        internal static void OnTalk(Player p)
        {
            var bael = s_Bael;
            if (p == null || bael == null || bael.IsDestroyed || p.Location == null || bael.Location == null) return;
            if (p.Location.DistanceTo(bael.Location) > HELP_RANGE) return;
            lock (s_Lock)
            {
                if (s_Attackers.Contains(p.Guid.Full) || !s_Helped.Add(p.Guid.Full)) return;
            }
            uint id = GIFTS[s_Rng.Next(GIFTS.Length)];
            Act(bael, () =>
            {
                bael.EnqueueBroadcast(new GameMessageHearSpeech("You show more sense than your masters. Take this, and remember who gave it.",
                    bael.Name, bael.Guid.Full, ChatMessageType.Speech), SPEECH_RANGE);
                bael.TryCastSpell(new Spell(id), p, bael, null, false, false, false);
            });
            lock (s_Lock) foreach (var g in GIFTS) s_Lift.Add((p.Guid.Full, g, double.MaxValue));   // at the scene's end
        }

        static void LiftDue(double now)
        {
            for (int i = s_Lift.Count - 1; i >= 0; i--)
            {
                var (pg, spell, until) = s_Lift[i];
                if (until > now) continue;
                s_Lift.RemoveAt(i);
                Lift(pg, spell);
            }
        }

        static void LiftGifts()
        {
            for (int i = s_Lift.Count - 1; i >= 0; i--)
            {
                var (pg, spell, _) = s_Lift[i];
                s_Lift.RemoveAt(i);
                Lift(pg, spell);
            }
        }

        static void Lift(uint playerGuid, uint spell)
        {
            var p = PlayerManager.GetOnlinePlayer(playerGuid);
            if (p == null) return;
            Act(p, () =>
            {
                var e = p.EnchantmentManager.GetEnchantment(spell);
                if (e != null) p.EnchantmentManager.Remove(e);
            });
        }

        /// <summary>His hits on a player: at most MAX_SHARE of their max health per WINDOW_S, whatever the spell or blow.</summary>
        internal static void CapOnPlayer(Player p, ref float amount)
        {
            if (p == null || amount <= 0) return;
            double now = Now();
            float cap = (float)(p.Health.MaxValue * LightbringerFightRule.WINDOW_FRAC);
            var e = s_Taken.GetOrAdd(p.Guid.Full, _ => (now, 0f));
            if (now - e.since > LightbringerFightRule.WINDOW_S) e = (now, 0f);
            float allowed = Math.Max(0f, cap - e.taken);
            if (amount > allowed) amount = allowed;
            s_Taken[p.Guid.Full] = (e.since, e.taken + amount);
        }

        /// <summary>Damage to the scene's Bael or Asheron: Asheron takes none; Bael never takes his last point.</summary>
        internal static void CapOnActor(Creature c, ref float amount, WorldObject source = null)
        {
            if (c.WeenieClassId == ASHERON) { amount = 0; return; }
            if (source?.WeenieClassId == ASHERON) { s_AsheronHits++; amount = Math.Min(amount, c.Health.MaxValue * 0.04f); }   // Asheron wears him down, never alone
            float room = Math.Max(0, (int)c.Health.Current - 1);
            if (amount > room) amount = room;
        }

        internal static bool IsSceneAdd(WorldObject source)
        {
            var c = source as Creature ?? source?.ProjectileSource as Creature;
            if (c == null) return false;
            lock (s_Lock) return s_Adds.Contains(c);
        }

        internal static bool IsSceneBaelSource(WorldObject source) =>
            IsBael(source) || IsBael(source?.ProjectileSource);

        // ------------------------------------------------------------------ the map (WorldBossSchedule's payload)
        internal sealed class Wire
        {
            public string key { get; set; } public string name { get; set; } public string where { get; set; }
            public double ns { get; set; } public double ew { get; set; } public string state { get; set; }
            public long start { get; set; } public long end { get; set; } public long next { get; set; }
        }

        internal static Wire Describe(double now)
        {
            lock (s_Lock)
            {
                if (!OnMap) return null;
                var w = new Wire { key = KEY, name = "Asheron and Bael'Zharon", where = "Cragstone", ns = MAP_NS, ew = MAP_EW };
                double next = NextStart(now);
                if (s_Start > 0 && now < s_Start) { w.state = "upcoming"; w.start = (long)s_Start; w.end = (long)(s_Start + 540); w.next = (long)s_Start; }
                else if (s_Start > 0) { w.state = "active"; w.start = (long)s_Start; w.end = (long)(s_End > 0 ? s_End : s_Start + 540); w.next = (long)NextStart(w.end + 1); }
                else
                {
                    bool recent = s_LastRunEnd > 0 && now - s_LastRunEnd < 3600;
                    w.state = recent ? "escaped" : "idle";
                    w.start = recent ? (long)s_LastRun : 0; w.end = recent ? (long)s_LastRunEnd : 0;
                    w.next = InWeek ? (long)next : 0;
                }
                return w;
            }
        }

        static void PushIfChanged(double now)
        {
            var w = Describe(now);
            string sig = w == null ? "hidden" : $"{w.state}|{w.start}|{w.end}|{w.next}";
            if (sig == s_LastSig) return;
            s_LastSig = sig;
            WorldBossSchedule.PushAll();
        }

        // ------------------------------------------------------------------ @lbscene
        static void Cmd(Session session, params string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            double now = Now();
            lock (s_Lock)
            {
                if (verb == "start")
                {
                    if (s_Steps != null) { Reply(session, "A scene is running; @lbscene stop first."); return; }
                    if (s_Start > 0 && !s_Forced) s_SkippedStart = s_Start;   // a forced start takes over a merely warned one
                    double lead = args.Length > 1 && double.TryParse(args[1], out var l) ? Math.Clamp(l, 0, 3600) : 60;
                    bool anyway = args.Any(a => a.Equals("force", StringComparison.OrdinalIgnoreCase));
                    if (!anyway && (!LightbringerSceneRule.Clear(now + lead, BaelWindows(now - 6 * 3600, now + 6 * 3600)) || WorldBaelUp()))
                    {
                        Reply(session, "Refused: the world boss Bael'Zharon is up or due within an hour of that (never two at once). " +
                                       "@lbscene plan shows the clear times; add 'force' to start anyway.");
                        return;
                    }
                    s_Start = now + lead; s_Lead = lead; s_Forced = true; s_Warned.Clear();
                    s_TestRun = args.Any(a => a.Equals("test", StringComparison.OrdinalIgnoreCase));   // box tests: the lair Bael may stay up a day
                    Reply(session, $"Scene forced: Bael'Zharon comes to Cragstone in {Minutes(lead)}.");
                    Mod.Log.Info($"[RevivalGuard] LightbringerScene: forced by {session?.Player?.Name ?? "console"} in {lead:F0} s");
                }
                else if (verb == "plan")
                {
                    // the next slots, where each scene lands, and the world boss's windows it keeps clear of
                    double slot = LightbringerSceneRule.SlotAtOrAfter(Anchor(), now);
                    var sb = new System.Text.StringBuilder("[LightbringerScene] plan (UTC):");
                    for (int i = 0; i < 6; i++, slot += EVERY)
                    {
                        double p = LightbringerSceneRule.Place(slot, BaelWindows, now);
                        sb.Append($"\n  slot {Stamp(slot)}: {(double.IsNaN(p) ? "skipped (the world boss)" : p == slot ? "on time" : $"moved to {Stamp(p)}")}");
                    }
                    foreach (var (f, t) in BaelWindows(now, now + 24 * 3600)) sb.Append($"\n  world boss Bael'Zharon: {Stamp(f)} to {Stamp(t)} (warning included)");
                    if (WorldBaelUp()) sb.Append("\n  the world boss is up now");
                    Reply(session, sb.ToString());
                }
                else if (verb == "stuck")
                {
                    // test the watchdog: put the landed Bael at the foot of the rock where the owner found him (169.7, 79)
                    var bb = s_Bael;
                    if (bb == null || bb.WeenieClassId != BAEL_FIGHT) { Reply(session, "Needs the landed Bael (after the leap)."); return; }
                    Act(bb, () => bb.FakeTeleport(new Position(CellOf(169.7, 79.0), 169.7f, 79.0f, 1.11f, 0f, 0f, 0f, 1f)));
                    Reply(session, "Bael put at (169.7, 79.0), in the rock.");
                }
                else if (verb == "curse" || verb == "gift")
                {
                    // test the rules on yourself as if you had struck him / spoken to him (the hooks are DamageHistory.Add
                    // and Player.HandleActionTalk; this calls what they call)
                    var me = session?.Player;
                    if (me == null || s_Bael == null || s_Bael.IsDestroyed) { Reply(session, "Needs a running scene and a character in the world."); return; }
                    if (verb == "curse") { s_CursedAt.Remove(me.Guid.Full); OnHit(s_Bael.WeenieClassId == BAEL_FIGHT ? s_Bael : null, me); }
                    else { s_Helped.Remove(me.Guid.Full); s_Attackers.Remove(me.Guid.Full); OnTalk(me); }
                    Reply(session, $"Scene {verb} sent.");
                }
                else if (verb == "stop")
                {
                    if (s_Start == 0) { Reply(session, "No scene to stop."); return; }
                    if (!s_Forced) s_SkippedStart = s_Start;   // the schedule does not warn this one again
                    if (s_Steps != null) Finish(now, "stopped by an admin");
                    else { s_Start = 0; s_Forced = false; s_Warned.Clear(); Vanish(s_Elysa, PlayScript.PortalEntry); s_Elysa = null; }
                    Reply(session, "Scene stopped.");
                }
                else
                {
                    string state = s_Start == 0 ? "none" : now < s_Start ? $"warned, begins in {Minutes(s_Start - now)}" : $"running, step {s_StepIdx}/{s_Steps?.Count ?? 0}";
                    Reply(session, $"[LightbringerScene] week {(InWeek ? "on" : "off")}; scene {state}; next scheduled {Stamp(NextStart(now))} UTC; " +
                                   $"Bael {(s_Bael == null ? "-" : $"{s_Bael.Guid} health {s_Bael.Health.Current}/{s_Bael.Health.MaxValue} heals {s_Heals} unstuck {s_Unsticks} swings {s_Swings} hit {s_SwingHits} far {s_FarSpells} dog {s_Watchdogs} kills {s_Kills} | fight n {s_FightN} phase {s_Phase} shards {s_Delivered}/{Needed} stage {s_Stage}{(s_Won ? " WON" : "")} meteors {s_Meteors} hit {s_MeteorHits} shields {s_Shields} fought {s_Fighters.Count}")}, " +
                                   $"Asheron {(s_Asheron == null ? "-" : $"{s_Asheron.Guid} bolts {s_AsheronBolts} hits {s_AsheronHits} heals {s_AshHeals} facing {AshFacing()} mode {s_Asheron.CombatMode}")}; " +
                                   $"held: Bael [{Held(s_Bael)}] Asheron [{Held(s_Asheron)}]; Bael {(s_Bael == null ? "" : $"awake {s_Bael.IsAwake} target {s_Bael.AttackTarget?.Name ?? "-"} mode {s_Bael.CombatMode}")}");
                }
            }
            PushIfChanged(now);
        }

        static string Held(Creature c) =>
            c == null ? "" : string.Join(", ", c.EquippedObjects.Values.Select(o => o.Name)) + (c.Inventory.Count > 0 ? $" / pack: {string.Join(", ", c.Inventory.Values.Select(o => o.Name))}" : "");

        // ------------------------------------------------------------------ small things
        static void World(string text) => PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));
        static void Reply(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }
        static string Stamp(double unix) => DateTime.UnixEpoch.AddSeconds(unix).ToString("yyyy-MM-dd HH:mm");
        static string Minutes(double s)
        {
            int m = (int)Math.Ceiling(Math.Max(0, s) / 60);
            if (m >= 120) return $"{m / 60} hours";
            if (m >= 60) return m % 60 == 0 ? "1 hour" : $"1 hour {m % 60} minutes";
            return m == 1 ? "1 minute" : $"{m} minutes";
        }
    }

    // ---------------------------------------------------------------------- hooks
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class LightbringerSceneTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done)
            {
                s_Done = true;
                try { LightbringerScene.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] LightbringerScene: register: {e.Message}"); }
            }
            return true;
        }
        static void Postfix()
        {
            try { LightbringerScene.RunLater(); } catch (Exception e) { TickGuard.Report("LightbringerScene later", e); }
            try { LightbringerScene.Tick(); }
            catch (Exception e) { TickGuard.Report("LightbringerScene", e); }
        }
    }

    /// <summary>Bael has 23 spells, so ACE's AI often chose a spell and, in spell range, only turned to face his target:
    /// he stood and cast while players were close (owner 2026-10-28, "he doesnt come to me to fight when im in range").
    /// The scene's Bael fights hand to hand with anyone within ENGAGE m (he closes in), and casts at those farther out.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.GetNextAttackType))]
    static class LightbringerSceneAttackType
    {
        static bool Prefix(Creature __instance, ref CombatType __result)
        {
            if (__instance.WeenieClassId != LightbringerScene.BAEL_FIGHT) return true;
            var t = __instance.AttackTarget;
            if (t?.Location == null || __instance.Location == null) return true;
            __result = __instance.Location.DistanceTo(t.Location) <= 25f ? CombatType.Melee : CombatType.Magic;
            return false;
        }
    }

    /// <summary>Asheron moving near Bael "alerted" him (Creature.AlertMonster sets the monster's AttackTarget to the mover),
    /// so every reposition turned Bael from the players to Asheron, an NPC ACE's AI cannot fight: he walked into him and
    /// stalled (box test 2026-10-28). The scene's actors never alert one another.</summary>
    /// <summary>ACE's FindNextTarget (every 5 s) found no target it liked and sent the landed Bael home mid-chase
    /// (MoveToHome: he snapped back to (180, 103), box test 2026-10-28). The scene chooses his foe (the nearest player within
    /// ENGAGE m, not on the rock); with one, that is his target and he is not sent home.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.FindNextTarget))]
    static class LightbringerSceneFindTarget
    {
        static bool Prefix(Creature __instance, ref bool __result)
        {
            if (__instance.WeenieClassId != LightbringerScene.BAEL_FIGHT) return true;
            var foe = LightbringerScene.Foe(__instance);
            if (foe == null) return true;
            __instance.AttackTarget = foe;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.AlertMonster))]
    static class LightbringerSceneNoAlert
    {
        static bool Prefix(Creature __instance, Creature monster, ref bool __result)
        {
            if (!LightbringerScene.IsScene(__instance) || !LightbringerScene.IsScene(monster)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard))]
    static class LightbringerSceneNoSave
    {
        static void Postfix(WorldObject __instance, ref bool __result)
        {
            if (!__result) return;
            try { if (LightbringerScene.NeverSave(__instance)) __result = false; } catch { }
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) })]
    static class LightbringerFerahDeath
    {
        static void Postfix(Creature __instance)
        {
            if (__instance.WeenieClassId != 8822) return;
            try { LightbringerScene.OnFerahDeath(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene Ilservian: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
    static class LightbringerSceneHit
    {
        static void Postfix(DamageHistory __instance, WorldObject attacker)
        {
            try { LightbringerScene.OnHit(__instance.Creature, attacker); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene hit: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionTalk))]
    static class LightbringerSceneTalk
    {
        static void Postfix(Player __instance)
        {
            try { LightbringerScene.OnTalk(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene talk: {e.Message}"); }
        }
    }

    /// <summary>His blows on players (melee goes through Player.TakeDamage).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(BodyPart), typeof(bool), typeof(AttackConditions) })]
    static class LightbringerSceneMelee
    {
        static void Prefix(Player __instance, WorldObject source, ref float _amount)
        {
            try
            {
                if (LightbringerScene.IsSceneAdd(source)) LightbringerScene.ScaleAdd(__instance, ref _amount);
                if (LightbringerScene.IsSceneBaelSource(source) || LightbringerScene.IsSceneAdd(source))
                {
                    LightbringerScene.CapOnPlayer(__instance, ref _amount);
                    LightbringerScene.Shield(__instance, ref _amount);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene melee cap: {e.Message}"); }
        }
    }

    /// <summary>Spell damage both ways (SpellProjectile.DamageTarget writes health directly, not through TakeDamage).</summary>
    [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.DamageTarget))]
    static class LightbringerSceneSpell
    {
        static void Prefix(SpellProjectile __instance, Creature target, ref float damage)
        {
            try
            {
                if (LightbringerScene.IsScene(target)) { LightbringerScene.CapOnActor(target, ref damage, __instance.ProjectileSource); return; }
                if (target is Player pa && LightbringerScene.IsSceneAdd(__instance.ProjectileSource))
                {
                    LightbringerScene.ScaleAdd(pa, ref damage);
                    LightbringerScene.CapOnPlayer(pa, ref damage);
                    LightbringerScene.Shield(pa, ref damage);
                }
                else if (target is Player p && LightbringerScene.IsSceneBaelSource(__instance.ProjectileSource))
                {
                    LightbringerScene.ScaleSpell(p, __instance.Spell?.Id ?? 0, ref damage);
                    LightbringerScene.CapOnPlayer(p, ref damage);
                    LightbringerScene.Shield(p, ref damage);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene spell cap: {e.Message}"); }
        }
    }

    /// <summary>The landed Bael is level 899 (retail 8821's body) and resisted every bolt, so Asheron's casts and the players'
    /// war spells did nothing and his self-heal never showed (box test 2026-10-28). Projectiles that hit him are not
    /// resisted; the damage caps and his visible heal keep him standing.</summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.TryResistSpell))]
    static class LightbringerSceneResist
    {
        static bool Prefix(WorldObject target, bool projectileHit, ref bool __result)
        {
            if (!projectileHit || target?.WeenieClassId != LightbringerScene.BAEL_FIGHT) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>ACE lets no creature hurt another (only combat pets): Asheron's bolts flew and did nothing, so Bael never
    /// needed to heal. The scene's Asheron and Bael may damage each other; Asheron's share is capped (CapOnActor) and
    /// Asheron himself takes none.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.CanDamage))]
    static class LightbringerSceneCanDamage
    {
        static bool Prefix(Creature __instance, Creature target, ref bool __result)
        {
            uint a = __instance.WeenieClassId, b = target?.WeenieClassId ?? 0;
            bool duel = (a == LightbringerScene.ASHERON && b == LightbringerScene.BAEL_FIGHT) || (a == LightbringerScene.BAEL_FIGHT && b == LightbringerScene.ASHERON);
            if (!duel) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>Weapon hits on the scene's actors (Creature.TakeDamage).</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeDamage), new[] { typeof(WorldObject), typeof(DamageType), typeof(float), typeof(bool) })]
    static class LightbringerSceneActor
    {
        static void Prefix(Creature __instance, ref float amount)
        {
            if (!LightbringerScene.IsScene(__instance)) return;
            try { LightbringerScene.CapOnActor(__instance, ref amount); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LightbringerScene actor cap: {e.Message}"); }
        }
    }
}
