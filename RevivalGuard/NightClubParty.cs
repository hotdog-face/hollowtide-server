using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE NIGHT CLUB HAS A POINT (owner 2026-10-28, all four ideas approved, plus dancing creatures). Before this the
    /// attendant's teleport counted the night and there was nothing to do inside. Now, while the Night Club or the
    /// Anniversary runs:
    ///   * DANCE FOR YOUR NIGHT: a night counts for Night Club Regular (counter NightClubNights, FestivalGoals.cs) only
    ///     after about a minute of dancing inside the club (landblock 0x8A04). Told on arrival, at 20 and 40 s, and when
    ///     it counts. The attendants no longer stamp anything (tools/gpubox-ace/gen_festival_goals.py section 8).
    ///   * NIGHTLY PRIZE: each counted night (one per 20 h, NightClubTonight) gives a retail Anniversary Sparkler, any
    ///     colour. The third night still pays the Anniversary Balloons, now in the club (pack full: any attendant).
    ///   * A DJ WHO CALLS DANCES: the Night Club DJ (902630, tools/gpubox-ace/gen_nightclub_party.py) names a dance every
    ///     CALL_S while anyone is inside. Everyone who does it within WINDOW_S gets the party buff: more creature XP for
    ///     an hour. No stacking: a new buff replaces the old one only if it is at least as big.
    ///   * CROWD BONUS: BASE plus PER_DANCER for each other dancer on the same call (distinct accounts, so one account's
    ///     characters count once; not addresses, which every browser player shares through the web gateway), at most CAP.
    ///   * THE FLOOR DANCES: retail's own drudge and tusker dancers in the floor play their default animation (client,
    ///     AcStaticAnim.cs); the Night Club Revelers (902631-902634) hold the drudge dance, break into a one-shot now and
    ///     then, and join each call. The DJ (902630) is invisible, on the drudge DJ's spot, and only speaks.
    /// Staff: @nightclub [status | call [dance] | newnight | reset] (the last two on yourself, for testing). Dances arrive as the MoveToState our client and retail's send: a held
    /// pose is the forward command (*dance* = DrudgeDanceState), a one-shot is in the command list.
    /// </summary>
    static class NightClubParty
    {
        internal const ushort CLUB = 0x8A04;
        const uint DJ = 902630;
        static readonly HashSet<uint> REVELERS = new HashSet<uint> { 902631, 902632, 902633, 902634 };
        static readonly uint[] SPARKLERS = { 34191, 34192, 34193, 34194, 34195, 34196, 34197 };   // retail Anniversary Sparklers
        const uint BALLOONS = 902204;
        const string NIGHTS = "NightClubNights", TONIGHT = "NightClubTonight", TROPHY = "NightClubNightsTrophy";

        const double NEED_S = 60, ONESHOT_S = 6, CALL_S = 180, FIRST_CALL_S = 45, WINDOW_S = 15, BUFF_S = 3600;
        const double BASE = 0.05, PER_DANCER = 0.01, CAP = 0.10;
        internal const PropertyFloat BUFF_PCT = (PropertyFloat)29140;      // the party buff's size (0.05 = +5%)
        internal const PropertyInt64 BUFF_UNTIL = (PropertyInt64)29141;    // and when it ends (unix seconds)

        sealed class Dance
        {
            public string Name, Type; public MotionCommand[] Motions;
            public bool Held => Motions[0] == MotionCommand.DrudgeDanceState;
        }

        // What the DJ calls; Type is what a player types (the Emotes panel has each one too).
        static readonly Dance[] DANCES =
        {
            new Dance { Name = "the Drudge Dance", Type = "*dance*", Motions = new[] { MotionCommand.DrudgeDanceState, MotionCommand.DrudgeDance } },
            new Dance { Name = "the YMCA", Type = "*ymca*", Motions = new[] { MotionCommand.YMCA } },
            new Dance { Name = "the Little Teapot", Type = "*teapot*", Motions = new[] { MotionCommand.Teapot } },
            new Dance { Name = "the Atoyot", Type = "*atoyot*", Motions = new[] { MotionCommand.ATOYOT } },
            new Dance { Name = "Musical Chairs", Type = "*musical chair*", Motions = new[] { MotionCommand.HaveASeat } },
        };
        static readonly HashSet<MotionCommand> ANY_DANCE = new HashSet<MotionCommand>(DANCES.SelectMany(d => d.Motions));
        static readonly MotionCommand[] ONE_SHOTS = { MotionCommand.YMCA, MotionCommand.Teapot, MotionCommand.ATOYOT, MotionCommand.HaveASeat, MotionCommand.DrudgeDance };

        sealed class Guest
        {
            public double Secs, OneShotUntil, Said, CountedAt;
            public bool Inside, SoulHeld;   // SoulHeld: *dance* said, and no move since (see OnSoulEmote)
            public MotionCommand LastOneShot; public double LastOneShotAt;
        }

        sealed class Call
        {
            public Dance Dance; public double Ends;
            public readonly HashSet<uint> Did = new HashSet<uint>();
        }

        static readonly object s_Lock = new object();
        static readonly Dictionary<uint, Guest> s_Guests = new Dictionary<uint, Guest>();
        static Call s_Call;
        static double s_Last, s_NextCall, s_NextFloor;
        static int s_LastDance = -1;
        static readonly Random s_Rng = new Random();

        static double Now => ACE.Common.Time.GetUnixTime();
        internal static bool Open => SeasonalCalendar.IsFestivalActive("nightclub") || SeasonalCalendar.IsFestivalActive("anniversary");
        static bool InClub(Player p) => p?.Location != null && p.Location.LandblockId.Landblock == CLUB && p.Location.Indoors;

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "nightclub", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "The Night Club party: who is dancing, the DJ's next call; 'call [dance]' calls one now; 'newnight' and 'reset' (yourself) for testing.",
                "[status | call [dance] | newnight | reset]");
            Mod.Log.Info($"[RevivalGuard] NightClubParty: dance {NEED_S:0} s for the night, a DJ call every {CALL_S:0} s "
                + $"(+{BASE:P0} creature XP for 1 h, +{PER_DANCER:P0} a dancer, at most {CAP:P0}); @nightclub");
        }

        // ---- the dance, as the client sends it --------------------------------------------------------------------

        internal static void OnMove(Player p, MoveToState m)
        {
            var raw = m?.RawMotionState;
            if (raw == null || !InClub(p)) return;
            var did = new List<MotionCommand>();
            if (ANY_DANCE.Contains(raw.ForwardCommand)) did.Add(raw.ForwardCommand);
            if (raw.Commands != null) foreach (var c in raw.Commands) if (c != null && ANY_DANCE.Contains(c.MotionCommand)) did.Add(c.MotionCommand);
            if (did.Count == 0)
            {
                lock (s_Lock) if (s_Guests.TryGetValue(p.Guid.Full, out var g0) && raw.ForwardCommand != MotionCommand.DrudgeDanceState) g0.SoulHeld = false;
                return;
            }
            double now = Now;
            lock (s_Lock)
            {
                var g = GuestOf(p);
                if (raw.ForwardCommand != MotionCommand.DrudgeDanceState) g.SoulHeld = false;   // moved: the held dance is over
                foreach (var c in did)
                {
                    if (c != MotionCommand.DrudgeDanceState) { g.OneShotUntil = now + ONESHOT_S; g.LastOneShot = c; g.LastOneShotAt = now; }
                    if (s_Call != null && now <= s_Call.Ends && s_Call.Dance.Motions.Contains(c)) s_Call.Did.Add(p.Guid.Full);
                }
            }
        }

        /// <summary>
        /// THE EMOTE TEXT COUNTS TOO. A client that already holds the drudge dance (it held it through a teleport) sends no
        /// new MoveToState when *dance* is typed again, only the SoulEmote text (box test 2026-10-28: night 2 never counted).
        /// The text is the DAT ChatPoseTable's onlookers' line ("%p" as his or her), so it maps back to the motion.
        /// </summary>
        internal static void OnSoulEmote(Player p, string text)
        {
            if (string.IsNullOrEmpty(text) || !InClub(p) || !SoulText().TryGetValue(Norm(text), out var c)) return;
            double now = Now;
            lock (s_Lock)
            {
                var g = GuestOf(p);
                if (c == MotionCommand.DrudgeDanceState) g.SoulHeld = true;
                else { g.OneShotUntil = now + ONESHOT_S; g.LastOneShot = c; g.LastOneShotAt = now; }
                if (s_Call != null && now <= s_Call.Ends && s_Call.Dance.Motions.Contains(c)) s_Call.Did.Add(p.Guid.Full);
            }
        }

        static Dictionary<string, MotionCommand> s_SoulText;
        static string Norm(string t) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(t.Trim().Trim('*').Trim().ToLowerInvariant(), @"\b(his|her)\b", "%p"), @"\s+", " ");

        static Dictionary<string, MotionCommand> SoulText()
        {
            if (s_SoulText != null) return s_SoulText;
            var map = new Dictionary<string, MotionCommand>();
            try
            {
                var t = ACE.DatLoader.DatManager.PortalDat?.ChatPoseTable;
                if (t != null)
                {
                    foreach (var kv in t.ChatEmoteHash)
                        if (Enum.TryParse(kv.Key, true, out MotionCommand mc) && ANY_DANCE.Contains(mc) && !string.IsNullOrEmpty(kv.Value?.OtherEmote))
                            map[Norm(kv.Value.OtherEmote)] = mc;
                    foreach (var kv in t.ChatPoseHash)          // a client that sends the trigger as typed ("dance")
                        if (Enum.TryParse(kv.Value, true, out MotionCommand mc) && ANY_DANCE.Contains(mc)) map[Norm(kv.Key)] = mc;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NightClubParty: ChatPoseTable: {e.Message}"); }
            Mod.Log.Info($"[RevivalGuard] NightClubParty: {map.Count} dance emote lines from the ChatPoseTable");
            return s_SoulText = map;
        }

        static Guest GuestOf(Player p)
        {
            if (!s_Guests.TryGetValue(p.Guid.Full, out var g)) s_Guests[p.Guid.Full] = g = new Guest();
            return g;
        }

        static bool Holding(Player p, MotionCommand c) => p.CurrentMoveToState?.RawMotionState?.ForwardCommand == c;

        // ---- every second -----------------------------------------------------------------------------------------

        internal static void Tick()
        {
            double now = Now;
            if (now - s_Last < 1.0) return;
            double dt = Math.Min(now - s_Last, 2.0);
            s_Last = now;
            if (!Open) { lock (s_Lock) { if (s_Guests.Count > 0 || s_Call != null) { s_Guests.Clear(); s_Call = null; } } return; }

            var inside = PlayerManager.GetAllOnline().Where(InClub).ToList();
            var here = new HashSet<uint>(inside.Select(p => p.Guid.Full));
            var count = new List<Player>();
            lock (s_Lock)
            {
                foreach (var kv in s_Guests) if (!here.Contains(kv.Key)) { kv.Value.Inside = false; kv.Value.SoulHeld = false; }
                foreach (var p in inside)
                {
                    var g = GuestOf(p);
                    if (!g.Inside) { g.Inside = true; Later(p, () => Arrive(p)); }
                    bool held = Holding(p, MotionCommand.DrudgeDanceState) || g.SoulHeld;
                    if (s_Call != null && now <= s_Call.Ends && s_Call.Dance.Held && held) s_Call.Did.Add(p.Guid.Full);
                    bool dancing = held || now < g.OneShotUntil;
                    if (CountedTonight(p)) continue;
                    if (g.Secs >= NEED_S)
                    {
                        if (now - g.CountedAt < 30) continue;          // counting now, or the pack was full and it did not take
                        g.Secs = 0; g.Said = 0;                         // a new night since the last one counted
                    }
                    if (!dancing) continue;
                    g.Secs += dt;
                    if (g.Secs >= NEED_S) { g.CountedAt = now; count.Add(p); }
                    else if (g.Secs >= 40 && g.Said < 40) { g.Said = 40; Say(p, "Dancing: 40 of 60 seconds. Keep it up!"); }
                    else if (g.Secs >= 20 && g.Said < 20) { g.Said = 20; Say(p, "Dancing: 20 of 60 seconds for tonight's night."); }
                }
                if (inside.Count == 0) { s_Call = null; s_NextCall = 0; }
                else if (s_NextCall == 0) s_NextCall = now + FIRST_CALL_S;
            }
            foreach (var p in count) Later(p, () => CountNight(p));
            if (inside.Count == 0) return;

            var dj = Find(inside[0], o => o.WeenieClassId == DJ).FirstOrDefault();
            Call ended = null;
            lock (s_Lock)
            {
                if (s_Call != null && now > s_Call.Ends) { ended = s_Call; s_Call = null; s_NextCall = now + CALL_S; }
            }
            if (ended != null) EndCall(ended, inside, dj);
            else if (s_Call == null && now >= s_NextCall) StartCall(null, inside, dj);

            if (now >= s_NextFloor) { s_NextFloor = now + 20; Floor(inside[0]); }
        }

        // ---- arriving and counting --------------------------------------------------------------------------------

        static void Arrive(Player p)
        {
            if (!Open || !InClub(p)) return;
            var g = FestivalGoals.ByCounter(NIGHTS);
            if (CountedTonight(p))
                Say(p, "Welcome back to the Night Club! Tonight already counts. The DJ calls a dance every few minutes: join in for a party buff.");
            else
                Say(p, "Welcome to the Night Club! Dance on the floor to count tonight's night: about a minute of dancing (type *dance*, "
                    + "or pick a dance on the Emotes panel). The DJ calls a dance every few minutes: join in for a party buff.");
            if (g != null && FestivalGoals.Running(g) && !FestivalGoals.TrophyTaken(p, g))
                Say(p, $"Night Club Regular: {p.QuestManager.GetCurrentSolves(NIGHTS)} of {g.N} nights. Three nights earn the Anniversary Balloons for your house.");
        }

        static bool CountedTonight(Player p) => p.QuestManager.HasQuest(TONIGHT) && !p.QuestManager.CanSolve(TONIGHT);

        static void CountNight(Player p)
        {
            var qm = p.QuestManager;
            if (!Open || qm == null || CountedTonight(p)) return;
            qm.Stamp(TONIGHT);
            Say(p, "Tonight counts! Thanks for dancing at the Night Club.");
            Mod.Log.Info($"[RevivalGuard] NightClubParty: {p.Name} danced a night");

            var wcid = SPARKLERS[s_Rng.Next(SPARKLERS.Length)];
            var gift = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (gift != null && p.TryCreateInInventoryWithNetworking(gift))
                Say(p, $"Tonight's keepsake: a {gift.Name}. Wield it to light it up.");
            else
            {
                gift?.Destroy();
                Say(p, "Your pack is full, so tonight's keepsake (an Anniversary Sparkler) stays with the club.");
            }

            var g = FestivalGoals.ByCounter(NIGHTS);
            if (g == null || !FestivalGoals.Running(g) || FestivalGoals.TrophyTaken(p, g) || !FestivalGoals.HasRow(NIGHTS)) return;
            qm.Stamp(NIGHTS);                                       // FestivalGoalsStamp says "Night Club Regular: 2 of 3 ..."
            if (qm.GetCurrentSolves(NIGHTS) < g.N)
            {
                Say(p, "Come back on another day and dance again for the next night.");
                return;
            }
            var balloons = WorldObjectFactory.CreateNewWorldObject(BALLOONS);
            if (balloons != null && p.TryCreateInInventoryWithNetworking(balloons))
            {
                qm.Stamp(TROPHY);
                qm.Erase(NIGHTS);
                Say(p, $"Three nights on the floor! You are one of our regulars now: the {balloons.Name} are in your pack, for your house.");
                Mod.Log.Info($"[RevivalGuard] NightClubParty: {p.Name} took the Anniversary Balloons in the club");
            }
            else
            {
                balloons?.Destroy();
                Say(p, "Three nights on the floor! Your pack is full: make room, then see any Night Club Attendant for your Anniversary Balloons.");
            }
        }

        // ---- the DJ ---------------------------------------------------------------------------------------------

        static void StartCall(Dance pick, List<Player> inside, WorldObject dj)
        {
            if (pick == null)
            {
                int i;
                do i = s_Rng.Next(DANCES.Length); while (DANCES.Length > 1 && i == s_LastDance);
                s_LastDance = i;
                pick = DANCES[i];
            }
            var call = new Call { Dance = pick, Ends = Now + WINDOW_S };
            lock (s_Lock) s_Call = call;
            string line = $"Everybody, do {pick.Name}! Type {pick.Type} or pick it on the Emotes panel. You have {WINDOW_S:0} seconds!";
            Speak(dj, inside, line);
            var motion = pick.Motions[0];
            if (dj is Creature c) Later(c, () => Move(c, motion));
            foreach (var r in Find(inside[0], o => REVELERS.Contains(o.WeenieClassId)).OfType<Creature>()) Later(r, () => Move(r, motion));
            // anyone already holding the drudge dance is on the call
            if (pick.Held)
                lock (s_Lock) foreach (var p in inside) if (Holding(p, MotionCommand.DrudgeDanceState) || (s_Guests.TryGetValue(p.Guid.Full, out var g) && g.SoulHeld)) call.Did.Add(p.Guid.Full);
            Mod.Log.Info($"[RevivalGuard] NightClubParty: the DJ called {pick.Name} ({inside.Count} inside)");
        }

        static void EndCall(Call call, List<Player> inside, WorldObject dj)
        {
            var did = inside.Where(p => call.Did.Contains(p.Guid.Full)).ToList();
            int crowd = did.Select(p => p.Session?.AccountId ?? p.Guid.Full).Distinct().Count();   // accounts, not addresses: browser players share the gateway's
            double pct = Pct(crowd);
            if (did.Count == 0) Speak(dj, inside, "Nobody? Warm up, I'll call another in a few minutes.");
            else Speak(dj, inside, crowd == 1 ? "Nice moves! Party buff for you." : $"Look at that, {crowd} of you on the floor! Party buff for everyone who danced.");
            foreach (var p in inside)
            {
                if (did.Contains(p)) Later(p, () => Grant(p, pct, crowd));
                else Say(p, $"You missed {call.Dance.Name}. The DJ calls another dance in about {CALL_S / 60:0} minutes.");
            }
            if (dj is Creature c) Later(c, () => Move(c, MotionCommand.Ready));
            Mod.Log.Info($"[RevivalGuard] NightClubParty: {call.Dance.Name}: {did.Count} danced ({crowd} distinct), buff {pct:P0}");
        }

        internal static double Pct(int crowd) => crowd <= 0 ? 0 : Math.Min(CAP, BASE + PER_DANCER * (crowd - 1));

        static void Grant(Player p, double pct, int crowd)
        {
            double now = Now;
            double have = p.GetProperty(BUFF_PCT) ?? 0;
            long until = p.GetProperty(BUFF_UNTIL) ?? 0;
            string with = crowd > 1 ? $" ({crowd} dancers on the call)" : "";
            if (until > now && have > pct + 1e-9)
            {
                Say(p, $"Good dancing! You keep your bigger party buff: +{have:P0} experience from creatures, {Mins(until - now)} left.");
                return;
            }
            p.SetProperty(BUFF_PCT, pct);
            p.SetProperty(BUFF_UNTIL, (long)(now + BUFF_S));
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.EnchantUpGreen));
            Say(p, $"Party buff: +{pct:P0} experience from creatures for 1 hour{with}. More dancers on a call make it bigger, up to +{CAP:P0}.");
        }

        internal static double BuffNow(Player p)
        {
            long until = p.GetProperty(BUFF_UNTIL) ?? 0;
            if (until <= Now) return 0;
            return Math.Min(CAP, Math.Max(0, p.GetProperty(BUFF_PCT) ?? 0));
        }

        internal static void OnLogin(Player p)
        {
            long until = p.GetProperty(BUFF_UNTIL) ?? 0;
            if (until > Now) Say(p, $"Night Club party buff: +{BuffNow(p):P0} experience from creatures, {Mins(until - Now)} left.");
        }

        static string Mins(double s) { int m = Math.Max(1, (int)Math.Round(s / 60)); return m == 1 ? "1 minute" : $"{m} minutes"; }

        // ---- the floor ------------------------------------------------------------------------------------------

        /// <summary>Every reveler holds the drudge dance; now and then one breaks into a one-shot and goes back to it.</summary>
        static void Floor(Player anyone)
        {
            bool calling; lock (s_Lock) calling = s_Call != null;
            var revelers = Find(anyone, o => REVELERS.Contains(o.WeenieClassId)).OfType<Creature>().Where(c => !c.IsDead).ToList();
            foreach (var r in revelers)
            {
                if (calling) continue;
                if (s_Rng.NextDouble() < 0.3) { var m = ONE_SHOTS[s_Rng.Next(ONE_SHOTS.Length)]; Later(r, () => Move(r, m)); }
                else if (r.CurrentMotionState?.MotionState?.ForwardCommand != MotionCommand.DrudgeDanceState) Later(r, () => Move(r, MotionCommand.DrudgeDanceState));
            }
        }

        /// <summary>A held pose persists (a newcomer sees it); a one-shot plays and the drudge dance comes back after it.</summary>
        static void Move(Creature c, MotionCommand m)
        {
            if (c?.PhysicsObj == null || c.IsDead) return;
            if (m == MotionCommand.DrudgeDanceState || m == MotionCommand.Ready)
            {
                c.ExecuteMotionPersist(new Motion(MotionStance.NonCombat, m));
                return;
            }
            c.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat, m));
            if (!REVELERS.Contains(c.WeenieClassId)) return;
            var chain = new ActionChain();
            chain.AddDelaySeconds(5);
            chain.AddAction(c, () => Move(c, MotionCommand.DrudgeDanceState));
            chain.EnqueueChain();
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        static IEnumerable<WorldObject> Find(Player anyone, Func<WorldObject, bool> pred) =>
            anyone?.CurrentLandblock?.GetAllWorldObjectsForDiagnostics().Where(o => o != null && pred(o)) ?? Enumerable.Empty<WorldObject>();

        /// <summary>To each player in the club, as the DJ's speech. Not a broadcast from the DJ: the DJ is invisible (the
        /// drudge DJ in the floor is the one you see, owner 261029-0005), and ACE broadcasts only to players who know
        /// the object, which nobody does (box 2026-10-29: the calls never arrived).</summary>
        static void Speak(WorldObject dj, List<Player> inside, string text)
        {
            foreach (var p in inside)
            {
                if (dj != null) p.Session?.Network?.EnqueueSend(new GameMessageHearSpeech(text, dj.Name, dj.Guid.Full, ChatMessageType.Speech));
                else Say(p, "Night Club DJ: " + text);
            }
        }

        /// <summary>World changes on the object's own landblock thread, not the tick's.</summary>
        static void Later(WorldObject o, Action a)
        {
            var chain = new ActionChain();
            chain.AddAction(o, () => { try { a(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NightClubParty: {e.Message}"); } });
            chain.EnqueueChain();
        }

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            var inside = PlayerManager.GetAllOnline().Where(InClub).ToList();
            if (verb == "newnight" || verb == "reset")
            {
                // testing: newnight forgets tonight (the next minute of dancing counts another night); reset clears it all
                var qm = me?.QuestManager;
                if (qm == null) return;
                qm.Erase(TONIGHT);
                if (verb == "reset") { qm.Erase(NIGHTS); qm.Erase(TROPHY); me.RemoveProperty(BUFF_PCT); me.RemoveProperty(BUFF_UNTIL); }
                lock (s_Lock) if (s_Guests.TryGetValue(me.Guid.Full, out var g)) { g.Secs = 0; g.Said = 0; g.CountedAt = 0; }
                Say(me, verb == "reset" ? "Night Club: your nights, trophy flag, tonight and party buff are cleared." : "Night Club: tonight is forgotten; dance a minute to count another night.");
                return;
            }
            if (verb == "call")
            {
                if (!Open) { Say(me, "The Night Club is closed (neither the Night Club nor the Anniversary is running)."); return; }
                if (inside.Count == 0) { Say(me, "Nobody is in the Night Club."); return; }
                string want = string.Join(" ", args.Skip(1)).Trim();
                var pick = want.Length == 0 ? null : DANCES.FirstOrDefault(d => d.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 || d.Type.Contains(want.ToLowerInvariant()));
                if (want.Length > 0 && pick == null) { Say(me, "Dances: " + string.Join(", ", DANCES.Select(d => d.Name))); return; }
                StartCall(pick, inside, Find(inside[0], o => o.WeenieClassId == DJ).FirstOrDefault());
                return;
            }
            double now = Now;
            lock (s_Lock)
            {
                var who = inside.Select(p => { s_Guests.TryGetValue(p.Guid.Full, out var g); return $"{p.Name} {(g?.Secs ?? 0):0}s{(CountedTonight(p) ? " (counted)" : "")} buff {BuffNow(p):P0} "
                    + $"[fwd {p.CurrentMoveToState?.RawMotionState?.ForwardCommand}{(g?.SoulHeld == true ? ", said *dance*" : "")}]"; });
                Say(me, $"Night Club {(Open ? "open" : "closed")}; inside: {(inside.Count == 0 ? "nobody" : string.Join(", ", who))}. "
                    + (s_Call != null ? $"Calling {s_Call.Dance.Name}, {s_Call.Did.Count} on it, {Math.Max(0, s_Call.Ends - now):0} s left." : s_NextCall > 0 ? $"Next call in {Math.Max(0, s_NextCall - now):0} s." : "No call due.")
                    + $" DJ {(inside.Count > 0 && Find(inside[0], o => o.WeenieClassId == DJ).Any() ? "here" : "not found")}.");
            }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class NightClubPartyTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { NightClubParty.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] NightClubParty: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { NightClubParty.Tick(); }
            catch (Exception e) { TickGuard.Report("NightClubParty", e); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnMoveToState))]
    static class NightClubPartyMove
    {
        static void Postfix(Player __instance, MoveToState moveToState)
        {
            try { NightClubParty.OnMove(__instance, moveToState); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NightClubParty move: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionSoulEmote))]
    static class NightClubPartySoulEmote
    {
        static void Postfix(Player __instance, string message)
        {
            try { NightClubParty.OnSoulEmote(__instance, message); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NightClubParty soul emote: {e.Message}"); }
        }
    }

    /// <summary>The party buff: creature-kill XP while it lasts (as the PK shrine boon, PkShrines.Boon).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.EarnXP))]
    static class NightClubPartyXp
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, double> s_Logged = new System.Collections.Concurrent.ConcurrentDictionary<uint, double>();

        static void Prefix(Player __instance, ref long amount, XpType xpType)
        {
            try
            {
                if (xpType != XpType.Kill || amount <= 0) return;
                double b = NightClubParty.BuffNow(__instance);
                if (b <= 0) return;
                long before = amount;
                amount = (long)Math.Round(amount * (1 + b));
                double now = ACE.Common.Time.GetUnixTime();   // one line per player per ten minutes, for staff
                if (!s_Logged.TryGetValue(__instance.Guid.Full, out var at) || now - at > 600)
                {
                    s_Logged[__instance.Guid.Full] = now;
                    Mod.Log.Info($"[RevivalGuard] NightClubParty: party buff for {__instance.Name} ({b:P0}): creature XP {before} -> {amount}");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NightClubParty xp: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class NightClubPartyLogin
    {
        static void Postfix(Player __instance)
        {
            try { NightClubParty.OnLogin(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] NightClubParty login: {e.Message}"); }
        }
    }
}
