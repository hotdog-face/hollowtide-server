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
            PresenceWatch.Register();
            SeasonalCalendar.Register();
            SeasonalDrops.Validate();
            LandblockReload.Register();
            Broadcast.Register();
            MagicRim.Register();
            PkDeathLoot.Register();
            Keyrings.Register();
            AuditTrail.Register();
            CharacterSnapshot.Register();
            Bestow.Register();
            SetHeritage.Register();
            QuestLog.Register();
            ModChannel.Register();
            AccountCreationCap.Register();
            LoginTokens.Register();
            LfgBoard.Register();
            ItemLock.Register();
            RagdollCorpse.Register();
            Mansions.Register();
            TrophyMounts.Register();
            MonsterAi.Register();
            EventDirector.Register();
            // The PK expansion (docs/PK-EXPANSION.md): every part off until its property is set.
            PkCommon.RegisterProperties();
            PkRenown.Register();
            PkBounty.Register();
            PkShrines.Register();
            PkBoard.Register();
            try { Log.Info($"[RevivalGuard] PK expansion: renown {(PkRenown.On ? "ON" : "off")}, bounties {(PkBounty.On ? "ON" : "off")}, shrines {(PkShrines.On ? "ON" : "off")}"); }
            catch (Exception e) { Log.Warn($"[RevivalGuard] PK expansion: properties not readable yet ({e.Message})"); }
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
                + "character's pack (@bestow), and the looking-for-group board (@lfgboard), and locked items (@lockitem / @unlockitem), and cell furniture taken out of the world (HiddenCellStatics), and no house purchase waits for dev accounts (DevHousing), and the allegiance hall: bank, hall board, hall portal, lawn corpses, kill board and raid alarm (/mansion), and ragdoll corpses where the body lands (@ragdollcorpse), and the login screen's population count on the login port (PopulationQuery)");
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
        internal sealed class State { public DateTime At; public ACE.Entity.Position Pos; public float Budget = -1f; public ACE.Entity.Position StaleFrom; public DateTime TeleportedAt; }
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

        static bool Prefix(Player __instance, ACE.Entity.Position newPosition, bool forceUpdate, ref bool __result)
        {
            var p = __instance;
            var st = s_State.GetOrCreateValue(p);
            var now = DateTime.UtcNow;
            if (forceUpdate || p.Teleporting || p.Location == null || newPosition == null)
            { st.Pos = null; st.At = now; return true; }
            if (st.Pos == null || st.At == default)
            {
                // first report after login, a teleport or a forced move: measure from the server's copy
                st.Pos = new ACE.Entity.Position(p.Location);
                if (st.At == default) st.At = now;
            }

            double dt = Math.Min(MAX_DT, Math.Max(0.0, (now - st.At).TotalSeconds));
            float rate = 1f;
            try { rate = Math.Max(1f, p.GetRunRate()); } catch { }
            float speed = BASE_RUN * rate * SLACK;
            float burst = (float)(speed * BURST_SECONDS);
            if (st.Budget < 0f) st.Budget = burst;
            st.Budget = Math.Min(burst, st.Budget + (float)(speed * dt));
            float allowed = st.Budget + MARGIN;
            float moved = st.Pos.Distance2D(newPosition);

            if (moved > allowed)
            {
                st.Budget = 0f;
                // A STALE REPORT FROM BEFORE A TELEPORT is refused the same way but is not an offence:
                // a sweep bot "moved 40915.0 m in 1.52 s" (2026-09-19) was the client's last position
                // report from the old landblock, arriving after the teleport. Refusing it keeps the
                // player at the destination; logging it as cheating would bury real offences.
                bool stale = st.StaleFrom != null && (now - st.TeleportedAt).TotalSeconds < 10
                             && st.StaleFrom.Distance2D(newPosition) < 50f;
                if (!stale)
                Offences.Add(p, "movement", $"moved {moved:F1} m in {dt:F2} s (budget allowed {allowed:F1} m at run rate {rate:F2}); snapped back to {p.Location.ToLOCString()}");
                p.Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
                p.SendUpdatePosition();
                // The server's position stands AS OF NOW: measuring the next attempt from the last
                // accepted update instead would let a cheater who keeps trying earn a longer and longer
                // window (up to MAX_DT) and so a steady ~2x speed.
                st.Pos = new ACE.Entity.Position(p.Location);
                st.At = now;
                __result = false;
                return false;   // ACE's UpdatePlayerPosition is skipped: the server keeps its position
            }
            st.Budget = Math.Max(0f, st.Budget - moved);
            st.Pos = new ACE.Entity.Position(newPosition);
            st.At = now;
            return true;
        }
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
                if (opcode != GameActionType.TalkDirect) text = message.Payload.ReadString16L();
                st.Position = pos;   // the real handler reads it again
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
