using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.Managers;
using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;
using HarmonyLib;
using log4net;

namespace RevivalGuard
{
    /// <summary>
    /// ANTI-CHEAT GUARDS FOR THE SHARD. The client is in the attacker's hands (a modified client, or an
    /// AI agent writing packets), so every rule that matters is enforced here, on the server.
    /// docs/security/ANTI-CHEAT.md has the audit behind each guard; tools/security/ has the hostile tests
    /// that prove each one.
    ///
    ///   MovementGuard  - ANTI-CHEAT #1/#2/#12: speed, short teleports and lag-switch warps
    ///   VendorGuard    - #10: a purchase total that would wrap a uint (retail's January 2001 exploit)
    ///   FellowshipGuard- #13: the 9-member cap raced across parallel landblock threads
    ///   FloodGuard     - plan item 3: game-action floods from macros and bots
    ///   ChatGuard      - chat spam: a crash tool (the retail DDoS-then-dupe) and a nuisance to everyone
    ///   TicketDesk     - player reports and support tickets (retail's AbuseLogRequest), filed for staff
    ///
    /// Each guard logs under [RevivalGuard] and counts offences per character, for staff review.
    /// </summary>
    public class Mod : IHarmonyMod
    {
        internal static readonly ILog Log = LogManager.GetLogger(typeof(Mod));
        const string ID = "revival.guard";
        Harmony m_Harmony;

        public void Initialize()
        {
            m_Harmony = new Harmony(ID);
            m_Harmony.PatchAll(typeof(Mod).Assembly);
            // The ticket queue is chat commands, not Harmony patches, so it registers itself rather
            // than being found by PatchAll. See TicketConsole for why it is commands and not a panel.
            TicketConsole.Register();
            AdminOps.Register();
            PresenceWatch.Register();
            SeasonalCalendar.Register();
            SeasonalDrops.Validate();
            LandblockReload.Register();
            Broadcast.Register();
            KeepCharacter.Register();
            MagicRim.Register();
            PkDeathLoot.Register();
            Keyrings.Register();
            AuditTrail.Register();
            CharacterSnapshot.Register();
            Bestow.Register();
            SetHeritage.Register();
            QuestLog.Register();
            ModChannel.Register();
            Mail.Register();
            DiscordBridge.Register();
            ItemLink.Register();
            PerfStat.Register();
            AccountCreationCap.Register();
            LoginTokens.Register();
            LfgBoard.Register();
            FeatureBoard.Register();
            ItemLock.Register();
            RagdollCorpse.Register();
            Mercenaries.Register();
            Mansions.Register();
            TrophyMounts.Register();
            ServerFirsts.Register();
            MonsterAi.Register();
            EventDirector.Register();
            ExtraDeathLines.Register();
            // The PK expansion (docs/PK-EXPANSION.md): every part off until its property is set.
            PkCommon.RegisterProperties();
            PkRenown.Register();
            PkBounty.Register();
            PkShrines.Register();
            PkBoard.Register();
            try { Log.Info($"[RevivalGuard] PK expansion: renown {(PkRenown.On ? "ON" : "off")}, bounties {(PkBounty.On ? "ON" : "off")}, shrines {(PkShrines.On ? "ON" : "off")}"); }
            catch (Exception e) { Log.Warn($"[RevivalGuard] PK expansion: properties not readable yet ({e.Message})"); }
            try { NameGuard.LoadNow(); } catch (Exception e) { Log.Error($"[RevivalGuard] NameGuard: {e.Message}"); }   // says at start how many names are protected
            Log.Info("[RevivalGuard] loaded: movement, vendor, fellowship, flood and chat guards, "
                + "the ticket desk (@tickets / @ticket), presence (@who / @stuck), the seasonal "
                + "calendar (@festival) and its seasonal drops (SeasonalDrops), the teleport watchdog and completion rescue, chess "
                + "checkmate, the stuck-chest healer, the academy exit amnesty and give-after-yes "
                + "(a Luminance token is taken only when the player answers Yes), @create placed "
                + "inside a cell (CreateInCell), one landblock back from the database (@reloadblock), "
                + "the world broadcast with a look first (@broadcast), and the blue rim on magic items "
                + "whose weenie never carried UiEffects (MagicRim), and retail's PK death loot: wielded "
                + "gear at any level and every rare (PkDeathLoot), and keyrings that open their chests and "
                + "spend a key per unlock (Keyrings), the audit trail on disk and in game (@audit), and character "
                + "snapshots with a confirmed restore (@snapshot), and bonded items into an offline "
                + "character's pack (@bestow), and the looking-for-group board (@lfgboard), and the feature request board (@features), and locked items (@lockitem / @unlockitem), and cell furniture taken out of the world (HiddenCellStatics), and no house purchase waits for dev accounts (DevHousing), and the allegiance hall: bank, hall board, hall portal, lawn corpses, kill board and raid alarm (/mansion), and ragdoll corpses where the body lands (@ragdollcorpse), and the login screen's population count on the login port (PopulationQuery), and combat pets that only pick targets in line of sight (PetLos, pet_los_targets), and dyes that take only on clothing whose art has the colour (DyeArt, dye_by_art), and hovering NPCs reported lower than their physics (HoverDrop, hover_drop), and generator spawns placed in the air dropped to the ground (SpawnGround, rg_spawn_ground), and Azimar greeting players by name, level, hour and visit, with paced blessings (AzimarVoice, azimar_voice)");
        }

        public void Dispose()
        {
            m_Harmony?.UnpatchAll(ID);
            Log.Info("[RevivalGuard] unloaded");
        }
    }

    /// <summary>Per-character offence counters, shared by the guards.</summary>
    static class Offences
    {
        static readonly ConcurrentDictionary<uint, int> s_Count = new ConcurrentDictionary<uint, int>();
        public static int Add(Player p, string what, string detail)
        {
            int n = s_Count.AddOrUpdate(p.Guid.Full, 1, (_, v) => v + 1);
            // every offence at first, then every tenth, so a sustained cheat can't flood the log
            if (n <= 5 || n % 10 == 0)
                Mod.Log.Warn($"[RevivalGuard] {what}: {p.Name} (0x{p.Guid.Full:X8}, offence {n}) {detail}");
            return n;
        }
    }

    /// <summary>
    /// MOVEMENT. ACE's UpdatePlayerPosition accepted any client position up to 50 m away unless it also
    /// crossed more than one landblock (Player_Tick.cs), so a modified client could move up to 50 m per
    /// update, and a lag-switcher warped on reconnect. A player can't outrun their own run rate: retail's
    /// RunForward cycle is 4 m/s at run rate 1 (MotionTable), and ACE computes the rate from the Run skill
    /// and burden (Creature.GetRunRate). So the distance allowed since the last ACCEPTED update is
    /// 4 m/s x run rate x elapsed time, x SLACK for lag and jumps, plus a flat MARGIN. Anything beyond that
    /// is refused and the client is snapped back to the server's position -- the same response ACE's own
    /// z-hack check uses. Teleports, portals and server-forced moves are not client moves and are exempt.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    static class MovementGuard
    {
        const float BASE_RUN = 4.0f;      // m/s at run rate 1 (retail RunForward)
        // The live values are in tunables.json on the server (Tunables): a published limit is one a
        // cheater stays just under. The defaults here are deliberately MORE lenient than the live ones,
        // so a missing file can only ever let more through, never snap an honest player back.
        static float SLACK => (float)Tunables.D("movement.slack", 2.0);           // jump arcs, slopes, rounding
        static float MARGIN => (float)Tunables.D("movement.margin", 8.0);         // metres always allowed on top of the budget
        static double MAX_DT => Tunables.D("movement.max_dt", 3.0);               // a longer silence earns no more than this: a lag switch gains little
        static double BURST_SECONDS => Tunables.D("movement.burst_seconds", 3.0); // the budget holds up to this much running

        // A BUDGET, NOT A PER-REPORT LIMIT. Our client's reports arrive bunched: the bridge's own
        // movement messages and the client's relay both carry positions, so two reports a third of a
        // second apart can be 8-18 m apart while the player runs honestly (measured 2026-09-18,
        // playtest P16: entering combat while running snapped the owner back three times in 10 s).
        // So each report spends from a budget that refills at the character's run speed (x SLACK)
        // and holds up to BURST_SECONDS of running. Sustained speed beyond SLACK, or a jump beyond
        // the budget plus MARGIN, is still refused.

        // MEASURED FROM THE CLIENT'S OWN LAST REPORT, not from the server's copy of the player.
        // The first version measured from p.Location, and the server moves that copy itself: a Use
        // out of reach runs ACE's own MoveToObject on the server's physics, so the server's player
        // walks toward the target while the client's reports lag behind or lead it. Owner playtest
        // 2026-09-18: "moved 9.5 m in 0.07 s" beside the Academy lifestone, the snap-back cancelled
        // the walk, and the bind never happened (so death and /ls went to the old bind point); the
        // same snap is the reported pop-back by the lifestone. A cheater gains nothing from the
        // change: every report is still held to what could have been run since the last report.
        // The baseline is re-taken from the server after a teleport or a forced position, and after
        // a refusal, so a refused jump cannot become the new starting point.
        internal sealed class State
        {
            public DateTime At; public ACE.Entity.Position Pos; public float Budget = float.NaN; public ACE.Entity.Position StaleFrom; public DateTime TeleportedAt;
            public ushort SnapStamp; public DateTime SnapAt; public int StaleDrops;   // the last snap-back's ForcePosition sequence
            public ACE.Entity.Position ReqPos; public ushort ReqStamp;               // the newest AutonomousPosition and its echoed sequence
        }
        internal static readonly ConditionalWeakTable<Player, State> s_State = new ConditionalWeakTable<Player, State>();

        internal static void Rebaseline(Player p) { if (s_State.TryGetValue(p, out var st)) st.Pos = null; }

        /// <summary>A teleport is starting: remember where from, so a report still carrying the old
        /// spot (sent before the client heard of the teleport) is recognised as stale, not a cheat.</summary>
        internal static void TeleportFrom(Player p)
        {
            var st = s_State.GetOrCreateValue(p);
            st.Pos = null;
            st.StaleFrom = p.Location != null ? new ACE.Entity.Position(p.Location) : null;
            st.TeleportedAt = DateTime.UtcNow;
        }

        const double SILENCE_MAX = 30.0;   // seconds of silence a report can be credited with as plain running

        /// <summary>Whether plain running (BASE_RUN x rate, no SLACK) for the real seconds since the baseline, at most
        /// SILENCE_MAX, covers <paramref name="moved"/>. A teleport restarts the count (its destination is the baseline).</summary>
        internal static bool SilenceAllows(State st, DateTime now, float rate, float moved, out double silent, out float runAllowed)
        {
            var since = st.TeleportedAt > st.At ? st.TeleportedAt : st.At;
            silent = Math.Max(0.0, (now - since).TotalSeconds);
            runAllowed = (float)(BASE_RUN * rate * Math.Min(silent, SILENCE_MAX));
            return silent > MAX_DT && moved <= runAllowed;
        }

        const double STALE_WINDOW = 3.0;   // seconds after a snap a report from before it can still be in flight

        /// <summary>A report the client sent before our snap-back reached it (its echoed ForcePosition sequence is older than
        /// the snap's): dropped quietly. The stamp comes from the AutonomousPosition that stored this very Position object,
        /// or from the MoveToState that did.</summary>
        static bool StaleAfterSnap(Player p, State st, ACE.Entity.Position pos, DateTime now)
        {
            if (st.SnapAt == default) return false;
            int stamp = -1;
            if (ReferenceEquals(st.ReqPos, pos)) stamp = st.ReqStamp;
            else if (p.CurrentMoveToState != null && ReferenceEquals(p.CurrentMoveToState.Position, pos)) stamp = p.CurrentMoveToState.ForcePositionSequence;
            if (stamp < 0) return false;
            double since = (now - st.SnapAt).TotalSeconds;
            if (!MovementBudget.StaleAfterSnap((ushort)stamp, st.SnapStamp, since, STALE_WINDOW)) return false;
            if (st.StaleDrops++ == 0)
                Mod.Log.Info($"[RevivalGuard] movement: {p.Name} (0x{p.Guid.Full:X8}) report from before the snap-back dropped " +
                             $"(sequence {stamp}, snap {st.SnapStamp}, {since:F2} s after)");
            return true;
        }

        static bool Prefix(Player __instance, ACE.Entity.Position newPosition, bool forceUpdate, ref bool __result)
        {
            var p = __instance;
            var st = s_State.GetOrCreateValue(p);
            var now = DateTime.UtcNow;
            if (forceUpdate || p.Teleporting || p.Location == null || newPosition == null)
            { st.Pos = null; st.At = now; return true; }
            if (StaleAfterSnap(p, st, newPosition, now)) { __result = false; return false; }   // no offence, no new snap
            if (st.Pos == null || st.At == default)
            {
                // first report after login, a teleport or a forced move: measure from the server's copy
                st.Pos = new ACE.Entity.Position(p.Location);
                if (st.At == default) st.At = now;
            }

            double dt = Math.Min(MAX_DT, Math.Max(0.0, (now - st.At).TotalSeconds));
            float rate = 1f;
            try { rate = Math.Max(1f, p.GetRunRate()); } catch { }
            float moved = st.Pos.Distance2D(newPosition);
            float budget = st.Budget;
            bool fits = MovementBudget.Judge(ref budget, dt, rate, moved, (float)SLACK, (float)MARGIN, (float)BURST_SECONDS, out float allowed);

            // A LONG HONEST SILENCE. The client holds its reports while a room's cell is unresolved (pending
            // room, "outside every room", after a correction), and MAX_DT credits such a silence with 3 s at
            // most, so the report that ends it looked like a speed hack: Mystic Warrior 2026-10-19 12:17 UTC,
            // gaps of 17-22 s, 6.6-8.7 m/s against his 10.8 m/s run speed, snapped back five times
            // (docs/findings/FINDINGS-raise-and-snapback.md). So a report is also accepted if it is within
            // plain running (no SLACK, no MARGIN) for the real time since the baseline, up to SILENCE_MAX.
            // A lag switch gains nothing: this is exactly what running the whole silence would have covered.
            st.Budget = budget;
            if (!fits && SilenceAllows(st, now, rate, moved, out double silent, out float runAllowed))
            {
                Mod.Log.Info($"[RevivalGuard] movement silence: {p.Name} (0x{p.Guid.Full:X8}) moved {moved:F1} m after {silent:F1} s without a report " +
                             $"(run allowance {runAllowed:F1} m at run rate {rate:F2}, budget {allowed:F1} m); accepted");
                st.Pos = new ACE.Entity.Position(newPosition);
                st.At = now;
                return true;
            }

            if (!fits)
            {
                // THE BUDGET IS KEPT (2026-10-20 triage: 43 snapbacks for 7 players). It used to be zeroed here, so the
                // next honest report (Lou: 0.12 s later) failed too and one refusal became a chain. Keeping it gives a
                // cheater nothing: the budget only ever holds real elapsed time at the capped rate (Judge).
                // A STALE REPORT FROM BEFORE A TELEPORT is refused the same way but is not an offence:
                // a sweep bot "moved 40915.0 m in 1.52 s" (2026-09-19) was the client's last position
                // report from the old landblock, arriving after the teleport. Refusing it keeps the
                // player at the destination; logging it as cheating would bury real offences.
                bool stale = st.StaleFrom != null && (now - st.TeleportedAt).TotalSeconds < 10
                             && st.StaleFrom.Distance2D(newPosition) < 50f;
                if (!stale)
                Offences.Add(p, "movement", $"moved {moved:F1} m in {dt:F2} s (budget allowed {allowed:F1} m at run rate {rate:F2}); snapped back to {p.Location.ToLOCString()}");
                var seq = p.Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
                if (seq != null && seq.Length >= 2) { st.SnapStamp = BitConverter.ToUInt16(seq, 0); st.SnapAt = now; st.StaleDrops = 0; }
                p.SendUpdatePosition();
                // The server's position stands AS OF NOW: measuring the next attempt from the last
                // accepted update instead would let a cheater who keeps trying earn a longer and longer
                // window (up to MAX_DT) and so a steady ~2x speed.
                st.Pos = new ACE.Entity.Position(p.Location);
                st.At = now;
                __result = false;
                return false;   // ACE's UpdatePlayerPosition is skipped: the server keeps its position
            }
            st.Pos = new ACE.Entity.Position(newPosition);
            st.At = now;
            return true;
        }
    }

    /// <summary>
    /// The movement budget's arithmetic, pure so tools/modcheck can test it without a server.
    /// </summary>
    public static class MovementBudget
    {
        public const float BASE_RUN = 4.0f;   // m/s at run rate 1 (retail RunForward)

        /// <summary>Refill <paramref name="budget"/> (NaN = new: full) by <paramref name="dt"/> seconds at run speed x
        /// slack, capped at burstSeconds of it; a move fits within budget + margin. A fitting move spends the budget, down
        /// to -margin: MARGIN is borrowed and paid back by time, not given again with every report (before, each report at
        /// an empty budget got the whole margin free, so a client sending reports fast enough moved as fast as it liked).
        /// A refused move spends nothing, so a refusal does not cost the next honest report its time.</summary>
        public static bool Judge(ref float budget, double dt, float rate, float moved, float slack, float margin, float burstSeconds, out float allowed)
        {
            float speed = BASE_RUN * rate * slack;
            float burst = speed * burstSeconds;
            if (float.IsNaN(budget)) budget = burst;
            budget = Math.Min(burst, budget + (float)(speed * Math.Max(0.0, dt)));
            allowed = budget + margin;
            if (moved > allowed) return false;
            budget = Math.Max(-margin, budget - moved);
            return true;
        }

        /// <summary>A position report sent before the client saw the snap-back: its ForcePosition sequence is older than the
        /// one the snap carried (16-bit, wrapping). Only within <paramref name="window"/> seconds of the snap, so a client
        /// that never echoes the sequence is judged normally again after that.</summary>
        public static bool StaleAfterSnap(ushort reportStamp, ushort snapStamp, double sinceSnap, double window)
        {
            if (sinceSnap < 0 || sinceSnap > window) return false;
            ushort behind = (ushort)(snapStamp - reportStamp);
            return behind != 0 && behind < 0x8000;
        }
    }

    /// <summary>
    /// IN-FLIGHT REPORTS AFTER A SNAP-BACK. A position report the client sent before the snap reached it still carries the
    /// old spot, so it failed the guard again (the second offence 0.12 s after the first, 2026-10-20 triage) and earned a
    /// second snap. Each report echoes the newest ForcePosition sequence the client has seen (MoveStamps; ACE reads it and
    /// ignores it): one older than our snap's is dropped quietly, no offence and no new snap. Dropping gives a cheater
    /// nothing; a stamp from the future is judged as usual.
    /// </summary>
    [HarmonyPatch(typeof(ACE.Server.Network.GameAction.Actions.GameActionAutonomousPosition), nameof(ACE.Server.Network.GameAction.Actions.GameActionAutonomousPosition.Handle))]
    static class AutonomousStamp
    {
        [ThreadStatic] static int t_Stamp;   // the report's ForcePosition sequence + 1 while its handler runs; 0 = none

        static void Prefix(ClientMessage message)
        {
            t_Stamp = 0;
            try
            {
                var st = message.Payload.BaseStream; long pos = st.Position;
                _ = new ACE.Entity.Position(message.Payload);
                message.Payload.ReadUInt16(); message.Payload.ReadUInt16(); message.Payload.ReadUInt16();
                t_Stamp = message.Payload.ReadUInt16() + 1;
                st.Position = pos;   // the real handler reads it again
            }
            catch { t_Stamp = 0; }
        }

        // The stamp is kept beside the very Position object the handler stored as RequestedLocation, and judged when the
        // tick hands that object to UpdatePlayerPosition (MovementGuard). Not a patch on SetRequestedLocation: two field
        // stores, so the JIT may inline it into its callers past any patch.
        static void Postfix(Session session)
        {
            var p = session?.Player;
            if (p == null || t_Stamp == 0) return;
            var st = MovementGuard.s_State.GetOrCreateValue(p);
            st.ReqPos = p.RequestedLocation; st.ReqStamp = (ushort)(t_Stamp - 1);
        }

        static Exception Finalizer(Exception __exception) { t_Stamp = 0; return __exception; }
    }

    /// <summary>A teleport (portal, recall, admin, death) moves the player without a client report:
    /// the movement guard measures the next report from the destination, not from the old spot.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Teleport))]
    static class TeleportRebaseline
    {
        static void Prefix(Player __instance) => MovementGuard.TeleportFrom(__instance);
        static void Postfix(Player __instance) => MovementGuard.Rebaseline(__instance);
    }

    /// <summary>
    /// VENDOR PRICE OVERFLOW. ACE sums a purchase into a uint totalPrice with a "detect rollover?"
    /// comment and no check (Vendor.BuyItems_ValidateTransaction). Retail's January 2001 exploit was the
    /// same arithmetic: a big enough buy list wrapped the price to almost nothing. Before ACE's own
    /// validation, the list is priced in 64 bits, and anything past what a uint (or a purse) can hold is
    /// refused.
    /// </summary>
    [HarmonyPatch(typeof(Vendor), nameof(Vendor.BuyItems_ValidateTransaction))]
    static class VendorGuard
    {
        static bool Prefix(Vendor __instance, List<ItemProfile> itemProfiles, Player player, ref bool __result)
        {
            ulong total = 0;
            foreach (var ip in itemProfiles)
            {
                var guid = new ACE.Entity.ObjectGuid(ip.ObjectGuid);
                WorldObject wo = null;
                if (__instance.DefaultItemsForSale.TryGetValue(guid, out var d)) wo = d;
                else if (__instance.UniqueItemsForSale.TryGetValue(guid, out var u)) wo = u;
                if (wo == null) continue;
                ulong each = __instance.GetSellCost(wo);
                total += each * (ulong)Math.Max(0, (long)ip.Amount);
                if (total > int.MaxValue)
                {
                    Offences.Add(player, "vendor", $"buy list at {__instance.Name} totals over {int.MaxValue:N0} (refused before ACE's uint sum could wrap)");
                    player.SendTransientError("That purchase is too large.");
                    __result = false;
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// FELLOWSHIP SIZE. ACE checks Count >= MaxFellows and then adds, at invite AND at accept
    /// (Fellowship.AddFellowshipMember / AddConfirmedMember) -- a check-then-act that two acceptances on
    /// different landblock threads can both pass, which is the shape of the retail "more than nine in a
    /// fellowship" trick. Both paths run under one lock, so the check and the add are one step.
    /// </summary>
    [HarmonyPatch]
    static class FellowshipGuard
    {
        static readonly object s_Lock = new object();

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Fellowship), nameof(Fellowship.AddFellowshipMember));
            yield return AccessTools.Method(typeof(Fellowship), nameof(Fellowship.AddConfirmedMember));
        }

        static void Prefix(out bool __state) { __state = false; Monitor.Enter(s_Lock, ref __state); }
        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) Monitor.Exit(s_Lock);
            return __exception;
        }
    }

    /// <summary>
    /// ACTION FLOODS. A human produces a few game actions a second; a macro or an AI-driven client can
    /// produce hundreds, which is how the retail vendor/trade/give dupes were raced. Non-movement game
    /// actions are capped per session per second; the excess is dropped and counted. Movement and
    /// position messages are exempt (MovementGuard judges those).
    /// </summary>
    [HarmonyPatch(typeof(InboundMessageManager), nameof(InboundMessageManager.HandleGameAction))]
    static class FloodGuard
    {
        const int PER_SECOND = 30;
        sealed class Bucket { public long Second; public int Count; }
        static readonly ConditionalWeakTable<Session, Bucket> s_Buckets = new ConditionalWeakTable<Session, Bucket>();

        static bool Prefix(GameActionType opcode, ClientMessage message, Session session)
        {
            switch (opcode)
            {
                case GameActionType.AutonomousPosition:
                case GameActionType.MoveToState:
                case GameActionType.Jump:
                case GameActionType.JumpNonAutonomous:
                case GameActionType.DoMovementCommand:
                case GameActionType.StopMovementCommand:
                case GameActionType.AutonomyLevel:
                    return true;
            }
            if (session?.Player == null) return true;
            if (opcode == GameActionType.AbuseLogRequest) { TicketDesk.Take(message, session); return false; }   // ACE has no handler
            if (!ChatGuard.Allow(opcode, message, session)) return false;
            var b = s_Buckets.GetOrCreateValue(session);
            long sec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            lock (b)
            {
                if (b.Second != sec) { b.Second = sec; b.Count = 0; }
                if (++b.Count <= PER_SECOND) return true;
                if (b.Count == PER_SECOND + 1)
                    Offences.Add(session.Player, "flood", $"more than {PER_SECOND} game actions in one second (last {opcode}); the excess is dropped");
                return false;
            }
        }
    }

    /// <summary>
    /// CHAT SPAM. Retail players flooded chat to lag and crash servers, then used the crash for a
    /// rollback dupe (AC-CHEAT-HISTORY.md), and spam ruins the game for everyone else anyway (owner,
    /// 2026-09-18). Per character: at most MAX_MSGS chat messages in WINDOW seconds, and the same text
    /// at most MAX_REPEAT times in REPEAT_WINDOW seconds; anything past that is dropped with a short
    /// notice to the sender only. Admin accounts are exempt so the owner's commands and the test harness always work;
    /// every other level, Developer included, is held to the limit.
    /// Say, tells, channel chat and emotes count; commands a player may use (/ and @) count as chat too,
    /// since they cost the server the same.
    /// </summary>
    static class ChatGuard
    {
        const int MAX_MSGS = 6; const double WINDOW = 10;
        const int MAX_REPEAT = 2; const double REPEAT_WINDOW = 30;

        // THE CLIENT'S OWN QUERIES. Hollowtide sends these by itself and consumes the replies: at every
        // world entry @revivalclient, @questlog, @rgexpansion, @ragdollcorpse (twice) and WorldClock's
        // @time, plus the bridge's @pop (every 20 s), @myquests (login, then 5 min) and staff @gps polls.
        // That is eight Talks in the first seconds of a login, so every player was told "You are
        // sending messages too quickly" on arrival, logged an offence, and had their first real line
        // eaten (Windows first-run test, 2026-10-01). They get their own bucket, MAX_AUTO per WINDOW,
        // outside the player's allowance and the repeat rule; past it they count as chat again.
        static readonly HashSet<string> s_Auto = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "@revivalclient 1", "@questlog", "@rgexpansion 0", "@rgexpansion 1", "@ragdollcorpse 0",
            "@ragdollcorpse 1", "@time", "@pop", "@myquests", "@gps",
            "@features",   // the feature request window asks when it opens and once a minute while open
        };
        const int MAX_AUTO = 16;

        sealed class Hist { public readonly Queue<DateTime> Times = new Queue<DateTime>(); public readonly Queue<DateTime> Auto = new Queue<DateTime>(); public readonly Queue<(DateTime at, string text)> Texts = new Queue<(DateTime, string)>(); public DateTime NoticeAt; }
        static readonly ConditionalWeakTable<Session, Hist> s_Hist = new ConditionalWeakTable<Session, Hist>();

        public static bool Allow(GameActionType opcode, ClientMessage message, Session session)
        {
            switch (opcode)
            {
                case GameActionType.Talk: case GameActionType.Tell: case GameActionType.ChatChannel:
                case GameActionType.TalkDirect: case GameActionType.Emote: break;
                default: return true;
            }
            if (session.AccessLevel >= AccessLevel.Admin) return true;   // the owner and the harness only

            string text = null;
            try
            {
                var st = message.Payload.BaseStream; long pos = st.Position;
                try { text = ChatRepeatKey.Of((uint)opcode, message.Payload); }   // ChatChannel: channel first
                finally { st.Position = pos; }   // the real handler reads it again
            }
            catch { }

            var h = s_Hist.GetOrCreateValue(session);
            var now = DateTime.UtcNow;
            lock (h)
            {
                while (h.Times.Count > 0 && (now - h.Times.Peek()).TotalSeconds > WINDOW) h.Times.Dequeue();
                while (h.Texts.Count > 0 && (now - h.Texts.Peek().at).TotalSeconds > REPEAT_WINDOW) h.Texts.Dequeue();
                while (h.Auto.Count > 0 && (now - h.Auto.Peek()).TotalSeconds > WINDOW) h.Auto.Dequeue();
                if (opcode == GameActionType.Talk && text != null && h.Auto.Count < MAX_AUTO && s_Auto.Contains(text.Trim()))
                {
                    h.Auto.Enqueue(now);
                    return true;
                }
                string why = null;
                if (h.Times.Count >= MAX_MSGS) why = "You are sending messages too quickly. Please wait a moment.";
                else if (text != null && h.Texts.Count(t => string.Equals(t.text, text, StringComparison.OrdinalIgnoreCase)) >= MAX_REPEAT)
                    why = "You have already said that. Please don't repeat yourself.";
                if (why != null)
                {
                    if ((now - h.NoticeAt).TotalSeconds > 3) { h.NoticeAt = now; session.Player.SendTransientError(why); }
                    Offences.Add(session.Player, "chat", $"{opcode} dropped: {why}");
                    return false;
                }
                h.Times.Enqueue(now);
                if (text != null) h.Texts.Enqueue((now, text));
                return true;
            }
        }
    }

}
