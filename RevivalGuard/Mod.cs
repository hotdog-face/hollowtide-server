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
    /// <summary>Anti-cheat guards for the shard.</summary>
    public class Mod : IHarmonyMod
    {
        internal static readonly ILog Log = LogManager.GetLogger(typeof(Mod));
        const string ID = "revival.guard";
        Harmony m_Harmony;

        public void Initialize()
        {
            m_Harmony = new Harmony(ID);
            s_Failed.Clear(); s_PatchedClasses = 0;
            PatchEach();
            // The ticket queue is chat commands, not Harmony patches, so it registers itself rather than being found by PatchEach.
            Try(nameof(TicketConsole), TicketConsole.Register);
            Try(nameof(AdminOps), AdminOps.Register);
            Try(nameof(Decor), Decor.Register);
            Try(nameof(PresenceWatch), PresenceWatch.Register);
            Try(nameof(SeasonalCalendar), SeasonalCalendar.Register);
            Try(nameof(AzimarTheme), AzimarTheme.Register);
            Try(nameof(SeasonalDrops), SeasonalDrops.Validate);
            Try(nameof(LandblockReload), LandblockReload.Register);
            Try(nameof(WeenieReload), WeenieReload.Register);
            Try(nameof(Broadcast), Broadcast.Register);
            Try(nameof(KeepCharacter), KeepCharacter.Register);
            Try(nameof(MagicRim), MagicRim.Register);
            Try(nameof(PkDeathLoot), PkDeathLoot.Register);
            Try(nameof(Keyrings), Keyrings.Register);
            Try(nameof(TradeSafe), TradeSafe.Register);
            Try(nameof(AuditTrail), AuditTrail.Register);
            Try(nameof(CharacterSnapshot), CharacterSnapshot.Register);
            Try(nameof(Bestow), Bestow.Register);
            Try(nameof(PkSuit), PkSuit.Register);   // @pksuit [undo]: staff PK-ready suit
            Try(nameof(PvpKit), PvpKit.Register);   // @pvpkit [again]: staff retail end-game PvP kit, rg_pvp_kit
            Try(nameof(AdminKit), AdminKit.Register);   // @adminkit: the admin panel's Ready kit, rg_admin_kit
            Try(nameof(SetHeritage), SetHeritage.Register);
            Try(nameof(QuestLog), QuestLog.Register);
            Try(nameof(ModChannel), ModChannel.Register);
            Try(nameof(Mail), Mail.Register);
            Try(nameof(DeathLog), DeathLog.Register);          // /deaths: your last 10 deaths
            Try(nameof(NewcomerNotes), NewcomerNotes.Register);   // first-gank note under 30, swap-coin notice at 30
            Try(nameof(AwayMessage), AwayMessage.Register);    // /away: an automatic answer to tells (Feature Requests #33)
            Try(nameof(FellowPassword), FellowPassword.Register);   // /fpass <word> | /fjoin <leader> <password>
            Try(nameof(Broker), Broker.Register);
            Try(nameof(TownCrier), TownCrier.Register);        // the Holtburg Town Crier's question desk (rg_crier, off by default)
            Try(nameof(DressingRoom), DressingRoom.Register);
            Try(nameof(CustomClothing), CustomClothing.Register);   // our ClothingTables 0x1000F000+ (rune-skin suits; rg_custom_clothing, off)
            Try(nameof(LookShowcase), LookShowcase.Register);       // the Staff Base look showcase's effects (rg_look_showcase, off)
            Try(nameof(LookLabMake), LookLabMake.Register);         // the Look Lab's Make it now: @looklab, admins, one-off items (rg_look_lab_make, off)
            Try(nameof(DiscordBridge), DiscordBridge.Register);
            Try(nameof(DerethChatHost), DerethChatHost.Register);   // the cross-server Dereth channel (off without dereth-chat.json)
            Try(nameof(ItemLink), ItemLink.Register);
            Try(nameof(NoticeBoard), NoticeBoard.Register);      // @noticeboard: the Holtburg board's pages, for a check from anywhere
            Try(nameof(PerfStat), PerfStat.Register);
            Try(nameof(AccountCreationCap), AccountCreationCap.Register);
            Try(nameof(RetailWelcome), RetailWelcome.Register);
            Try(nameof(NewcomerWelcome), NewcomerWelcome.Register);   // a brand-new player's first character welcomed on General + Discord (rg_newcomer_welcome, off)
            Try(nameof(WhatsNew), WhatsNew.Register);            // last logout to our client for its what's-new card (rg_whats_new, on)
            Try(nameof(DnDefaultOn), DnDefaultOn.Register);      // every player character on the Dereth Network by default, silently
            Try(nameof(LoginTokens), LoginTokens.Register);
            Try(nameof(PasswordRecovery), PasswordRecovery.Register);
            Try(nameof(LfgBoard), LfgBoard.Register);
            Try(nameof(Recruiting), Recruiting.Register);      // /recruit: player allegiances that welcome new players, offered at level 10
            Try(nameof(SmallAllegianceBonus), SmallAllegianceBonus.Register);   // small allegiances pass up more XP and earn more luminance (rg_small_alleg_bonus, off)
            Try(nameof(ChessLobby), ChessLobby.Register);      // /chess: the Game Center lobby, temporary boards, records, titles, ladder (rg_chess_lobby, off)
            Try(nameof(Leaderboards), Leaderboards.Register);      // /top: level, XP, luminance, kills, PK, quests
            Try(nameof(FeatureBoard), FeatureBoard.Register);
            Try(nameof(ItemLock), ItemLock.Register);
            Try(nameof(TinkerLock), TinkerLock.Register);       // a recipe that locks the item to its applier asks first
            Try(nameof(UnlockWield), UnlockWield.Register);     // @unlockwield: staff undo a binding recipe's wield lock
            Try(nameof(AutoStack), AutoStack.Register);
            Try(nameof(TreeStatsUpload), TreeStatsUpload.Register);   // characters on treestats.net (off without treestats.json)
            Try(nameof(RagdollCorpse), RagdollCorpse.Register);
            Try(nameof(Mercenaries), Mercenaries.Register);
            Try(nameof(BorgSwitch), BorgSwitch.Register);        // @borg on|off|status: the Borg's off switch (admin panel)
            Try(nameof(MercKit), MercKit.Register);
            Try(nameof(Mansions), Mansions.Register);
            Try(nameof(TrophyMounts), TrophyMounts.Register);
            Try(nameof(ServerFirsts), ServerFirsts.Register);
            Try(nameof(MonsterAi), MonsterAi.Register);
            Try(nameof(MonsterFun), MonsterFun.Register);
            Try(nameof(HuntGrounds), HuntGrounds.Register);     // fewer champions on the Tusker/Stomper grounds, more in quiet dungeons, Director champion hunts
            Try(nameof(EventDirector), EventDirector.Register);
            Try(nameof(ScavengerHunt), ScavengerHunt.Register);
            Try(nameof(ExtraDeathLines), ExtraDeathLines.Register);
            Try(nameof(HoltburgGuards), HoltburgGuards.Register);    // Holtburg's Royal Guards talk, patrol and fight monsters in town (rg_holtburg_guards)
            Try(nameof(TickContainment), TickContainment.Register);   // one object's tick exception is contained, not the end of the world
            Try(nameof(ForeverChest), ForeverChest.Register);   // the chest beside the Holtburg grocer keeps what players leave in it (rg_forever_chest)
            Try(nameof(TargetDummy), TargetDummy.Register);
            // The PK expansion (docs/PK-EXPANSION.md): every part off until its property is set.
            Try(nameof(PkCommon), PkCommon.RegisterProperties);
            Try(nameof(PkRenown), PkRenown.Register);
            Try("PkSeason", PkRenown.RegisterSeason);      // a dated season (Darktide Season 1): inert until an admin plans one
            Try(nameof(PkTrophies), PkTrophies.Register);     // a Darktide Trophy per honourable kill (pk_trophies_enabled, off)
            Try(nameof(PkBounty), PkBounty.Register);
            Try(nameof(PkShrines), PkShrines.Register);
            Try(nameof(PkBoard), PkBoard.Register);
            Try(nameof(PkArena), PkArena.Register);
            Try(nameof(PkNight), PkNight.Register);           // Sunday 2 PM Pacific, hold the spot (rg_pk_night, off by default)
            Try(nameof(PkBattlefield), PkBattlefield.Register);   // PK zones keep player corpses, capped (pk_battlefield_corpses, off)
            Try(nameof(MoveGuard), MoveGuard.Register);         // rg_moveguard_mode 0 off / 1 log (default) / 2 enforce; rg_moveguard_staff
            Try(nameof(MeleeLatchGuard), MeleeLatchGuard.Register);   // rg_melee_latch_mode 0 off / 1 log (default) / 2 finish
            Try(nameof(MeleeRestart), () => PkCommon.RegisterBool(MeleeRestart.P_ON, true, "RevivalGuard MeleeRestart: a new melee attack replaces one still waiting on its walk to a monster it cannot reach, as a jump frees it (false: ACE drops the new attack)"));
            Try(nameof(MotionQueueWatchdog), () => PkCommon.RegisterBool(MotionQueueWatchdog.P_BACKLOG, true, "RevivalGuard MotionQueueWatchdog: drain a PK body's motion queue that moves but never catches up (table Falling on the ground 1 s, or 3+ swings waiting 2 s), so uses stop waiting behind it (false: only a queue stuck for 8 s is drained)"));
            Try(nameof(HoltburgOnly), () => PkCommon.RegisterBool(HoltburgOnly.P_ON, true, "RevivalGuard HoltburgOnly: every new character starts in Holtburg whatever town the client asked for (false: the client's town)"));
            Try(nameof(GroundSettle), () => PkCommon.RegisterBool(GroundSettle.P_ON, true, "RevivalGuard GroundSettle: an at-rest PK body ACE left off the ground is ticked so it lands (false: ACE)"));
            Try(nameof(LostObjectResend), () => PkCommon.RegisterBool(LostObjectResend.P_ON, true, "RevivalGuard LostObjectResend: a retail client asking for a monster it lost (0xF6EA) is sent the whole object again (false: ACE, appearance only)"));
            try { Log.Info($"[RevivalGuard] PK expansion: renown {(PkRenown.On ? "ON" : "off")}, bounties {(PkBounty.On ? "ON" : "off")}, shrines {(PkShrines.On ? "ON" : "off")}, PK Night {(PkNight.On ? "ON" : "off")}"); }
            catch (Exception e) { Log.Warn($"[RevivalGuard] PK expansion: properties not readable yet ({e.Message})"); }
            try { NameGuard.LoadNow(); } catch (Exception e) { Log.Error($"[RevivalGuard] NameGuard: {e.Message}"); }   // says at start how many names are protected
            try { Log.Info($"[RevivalGuard] moveguard: {(MoveGuard.Mode == MoveGuardRule.ENFORCE ? "ENFORCE" : MoveGuard.Mode == MoveGuardRule.LOG ? "log only" : "off")} (rg_moveguard_mode)"); } catch { }
            Log.Info($"[RevivalGuard] patched {s_PatchedClasses} Harmony classes; "
                + (s_Failed.Count == 0 ? "no part failed" : $"{s_Failed.Count} part(s) OFF after a failure (each its own ERROR above): {string.Join(", ", s_Failed)}; everything else is on"));
            Log.Info("[RevivalGuard] loaded: movement (and the moveguard path check), vendor, fellowship, flood and chat guards, "
                + "the ticket desk (@tickets / @ticket), presence (@who / @stuck), the seasonal "
                + "calendar (@festival) and its seasonal drops (SeasonalDrops), the teleport watchdog and completion rescue, chess "
                + "checkmate, the stuck-chest healer, the academy exit amnesty and give-after-yes "
                + "(a Luminance token is taken only when the player answers Yes), @create placed "
                + "inside a cell (CreateInCell), one landblock back from the database (@reloadblock), one weenie back from the database without a global cache clear (@reloadweenie), "
                + "the world broadcast with a look first (@broadcast), and the blue rim on magic items "
                + "whose weenie never carried UiEffects (MagicRim), and retail's PK death loot: wielded "
                + "gear at any level and every rare (PkDeathLoot), and keyrings that open their chests and "
                + "spend a key per unlock (Keyrings), the audit trail on disk and in game (@audit), and character "
                + "snapshots with a confirmed restore (@snapshot), and bonded items into an offline "
                + "character's pack (@bestow), and the looking-for-group board (@lfgboard), and the feature request board (@features), and locked items (@lockitem / @unlockitem), and cell furniture taken out of the world (HiddenCellStatics), and no house purchase waits for dev accounts (DevHousing), and the allegiance hall: bank, hall board, hall portal, lawn corpses, kill board and raid alarm (/mansion), and ragdoll corpses where the body lands (@ragdollcorpse), and the login screen's population count on the login port (PopulationQuery), and combat pets that only pick targets in line of sight (PetLos, pet_los_targets), and dyes that take only on clothing whose art has the colour (DyeArt, dye_by_art), and hovering NPCs reported lower than their physics (HoverDrop, hover_drop), and generator spawns placed in the air dropped to the ground (SpawnGround, rg_spawn_ground), and monsters ACE sends home sent as a teleport (ForceHomeStamp, rg_forcehome_stamp), and town NPCs that keep one face across landblock reloads (NpcFaceStable, rg_npc_face_stable), and Azimar greeting players by name, level, hour and visit, with paced blessings (AzimarVoice, azimar_voice), and every new character starting in Holtburg whatever town the client asked for (HoltburgOnly, rg_holtburg_only), and Holtburg's Royal Guards talking, patrolling and keeping the town (HoltburgGuards, rg_holtburg_guards), and Holtburg's townsfolk going about their day, mostly in silence, under one speech budget (TownLife, rg_town_life), and the chest beside the Holtburg grocer keeping what players leave in it (ForeverChest, rg_forever_chest " + (ForeverChest.On ? "ON" : "off") + "), and the Pyreal Target Drudge as a damage meter (TargetDummy, rg_target_dummy " + (TargetDummy.On ? "ON" : "off") + "), and one object's or player's tick exception contained instead of stopping the server (TickContainment, rg_tick_containment " + (TickContainment.On ? "ON" : "off") + ", @tickfaults), and Hollowtide Marionette essences in the normal essence loot (HollowEssences, rg_hollow_essences " + (HollowEssences.On ? "ON" : "off") + ", @hollowessences)");
        }

        // one failure loses one feature, not the mod
        static readonly List<string> s_Failed = new List<string>();
        static int s_PatchedClasses;

        /// <summary>PatchAll, one [HarmonyPatch] class at a time.</summary>
        void PatchEach()
        {
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Mod).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try { m_Harmony.CreateClassProcessor(type).Patch(); s_PatchedClasses++; }
                catch (Exception e)
                {
                    int undone = UnpatchClass(type);
                    s_Failed.Add(type.Name);
                    Log.Error($"[RevivalGuard] patch class {type.FullName} FAILED and is OFF ({undone} partial patch(es) undone; the rest of "
                        + $"RevivalGuard loads; a staged mod is rolled back on this line): {(e.InnerException ?? e).Message}");
                }
            }
        }

        int UnpatchClass(Type type)
        {
            int n = 0;
            try
            {
                foreach (var original in m_Harmony.GetPatchedMethods().ToList())
                {
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                        if (p.owner == ID && p.PatchMethod.DeclaringType == type) { m_Harmony.Unpatch(original, p.PatchMethod); n++; }
                }
            }
            catch (Exception e) { Log.Warn($"[RevivalGuard] undoing {type.Name}'s partial patches: {e.Message}"); }
            return n;
        }

        /// <summary>One Register()/setup call: a throw is one ERROR naming the part, and the rest carries on.</summary>
        static void Try(string part, Action register)
        {
            try { register(); }
            catch (Exception e)
            {
                s_Failed.Add(part);
                Log.Error($"[RevivalGuard] {part} failed to register and is OFF (the rest of RevivalGuard loads; a staged mod is rolled back on this line): {e.Message}");
            }
        }

        public void Dispose()
        {
            DerethChat.Bridge.Stop();
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

    /// <summary>Movement.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    static class MovementGuard
    {
        const float BASE_RUN = 4.0f;      // m/s at run rate 1 (retail RunForward)
        // The live values are in tunables.json on the server (Tunables): a published limit is one a cheater stays just under.
        static float SLACK => (float)Tunables.D("movement.slack", 2.0);           // jump arcs, slopes, rounding
        static float MARGIN => (float)Tunables.D("movement.margin", 8.0);         // metres always allowed on top of the budget
        static double MAX_DT => Tunables.D("movement.max_dt", 3.0);               // a longer silence earns no more than this: a lag switch gains little
        static double BURST_SECONDS => Tunables.D("movement.burst_seconds", 3.0); // the budget holds up to this much running

        // Measured from the client's own last report, not from the server's copy of the player.
        internal sealed class State
        {
            public DateTime At; public ACE.Entity.Position Pos; public float Budget = float.NaN; public ACE.Entity.Position StaleFrom; public DateTime TeleportedAt;
            public ushort SnapStamp; public DateTime SnapAt; public int StaleDrops;   // the last snap-back's ForcePosition sequence
            public ACE.Entity.Position ReqPos; public ushort ReqStamp;               // the newest AutonomousPosition and its echoed sequence
            public int ForceBefore = -1;   // ACE's ForcePosition sequence when a report was let through to it (-1: none)
            public int Unheard; public DateTime UnheardSince; public bool DesyncLogged;
        }
        internal static readonly ConditionalWeakTable<Player, State> s_State = new ConditionalWeakTable<Player, State>();

        internal static void Rebaseline(Player p) { MoveGuard.Restart(p); if (s_State.TryGetValue(p, out var st)) st.Pos = null; }

        /// <summary>A teleport is starting: remember where from, so a report still carrying the old spot is recognised as stale</summary>
        internal static void TeleportFrom(Player p)
        {
            var st = s_State.GetOrCreateValue(p);
            MoveGuard.Restart(p);
            st.Pos = null;
            st.StaleFrom = p.Location != null ? new ACE.Entity.Position(p.Location) : null;
            st.TeleportedAt = DateTime.UtcNow;
        }

        const double SILENCE_MAX = 30.0;   // seconds of silence a report can be credited with as plain running

        /// <summary>Whether plain running for the real seconds since the baseline, at most SILENCE_MAX, covers moved.</summary>
        internal static bool SilenceAllows(State st, DateTime now, float rate, float moved, out double silent, out float runAllowed)
        {
            var since = st.TeleportedAt > st.At ? st.TeleportedAt : st.At;
            silent = Math.Max(0.0, (now - since).TotalSeconds);
            runAllowed = (float)(BASE_RUN * rate * Math.Min(silent, SILENCE_MAX));
            return silent > MAX_DT && moved <= runAllowed;
        }

        const double STALE_WINDOW = 3.0;   // seconds after a snap a report from before it can still be in flight
        const double STALE_TELEPORT_MAX = 900.0;   // how long a report from the spot a teleport left counts as stale

        static int ForceSeq(Player p)
        {
            try { var b = p.Sequences.GetCurrentSequence(SequenceType.ObjectForcePosition); return b != null && b.Length >= 2 ? BitConverter.ToUInt16(b, 0) : -1; }
            catch { return -1; }
        }

        /// <summary>A report the client sent before our snap-back reached it: dropped quietly.</summary>
        static int ReportStamp(Player p, State st, ACE.Entity.Position pos)
        {
            if (ReferenceEquals(st.ReqPos, pos)) return st.ReqStamp;
            if (p.CurrentMoveToState != null && ReferenceEquals(p.CurrentMoveToState.Position, pos)) return p.CurrentMoveToState.ForcePositionSequence;
            return -1;
        }

        static bool StaleAfterSnap(Player p, State st, ACE.Entity.Position pos, DateTime now)
        {
            if (st.SnapAt == default) return false;
            int stamp = ReportStamp(p, st, pos);
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
            st.ForceBefore = -1;
            if (forceUpdate || p.Teleporting || p.Location == null || newPosition == null)
            { st.Pos = null; st.At = now; MoveGuard.Restart(p); return true; }
            // A report that echoes our newest snap (or later) shows the client heard it: the unheard count starts again.
            if (st.Unheard > 0)
            {
                int heard = ReportStamp(p, st, newPosition);
                if (heard >= 0 && !MovementBudget.StaleAfterSnap((ushort)heard, st.SnapStamp, 0, 1)) { st.Unheard = 0; st.DesyncLogged = false; }
            }
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

            // A long honest silence.
            st.Budget = budget;
            if (!fits && SilenceAllows(st, now, rate, moved, out double silent, out float runAllowed))
            {
                Mod.Log.Info($"[RevivalGuard] movement silence: {p.Name} (0x{p.Guid.Full:X8}) moved {moved:F1} m after {silent:F1} s without a report " +
                             $"(run allowance {runAllowed:F1} m at run rate {rate:F2}, budget {allowed:F1} m); accepted");
                if (!MoveGuard.Allow(p, newPosition, now)) { SnapBack(p, st, now); __result = false; return false; }
                st.Pos = new ACE.Entity.Position(newPosition);
                st.At = now;
                st.ForceBefore = ForceSeq(p);
                return true;
            }

            if (!fits)
            {
                // The budget is kept.
                bool stale = st.StaleFrom != null && (now - st.TeleportedAt).TotalSeconds < STALE_TELEPORT_MAX
                             && st.StaleFrom.Distance2D(newPosition) < 50f;
                // The baseline and the server's own copy are named too
                if (!stale)
                Offences.Add(p, "movement", $"moved {moved:F1} m in {dt:F2} s (budget allowed {allowed:F1} m at run rate {rate:F2}); snapped back to {p.Location.ToLOCString()}"
                    + $" (measured from {st.Pos.ToLOCString()}, {(now - st.At).TotalSeconds:F1} s old; the server's copy {p.Location.DistanceTo(newPosition):F1} m from the report)");
                SnapBack(p, st, now);
                __result = false;
                return false;   // ACE's UpdatePlayerPosition is skipped: the server keeps its position
            }
            // MoveGuard (MoveGuard.cs): what fits this budget is checked against the last 30 s of reports
            if (!MoveGuard.Allow(p, newPosition, now)) { SnapBack(p, st, now); __result = false; return false; }
            st.Pos = new ACE.Entity.Position(newPosition);
            st.At = now;
            st.ForceBefore = ForceSeq(p);
            return true;
        }

        /// <summary>Refuses the report: the client is snapped back to the server's position, which stands as of now.</summary>
        static void SnapBack(Player p, State st, DateTime now)
        {
            MoveGuard.Restart(p);
            var seq = p.Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
            if (seq != null && seq.Length >= 2) { st.SnapStamp = BitConverter.ToUInt16(seq, 0); st.SnapAt = now; st.StaleDrops = 0; }
            p.SendUpdatePosition();
            if (st.Unheard++ == 0) st.UnheardSince = now;
            if (MovementBudget.Desynced(st.Unheard, (now - st.UnheardSince).TotalSeconds) && !st.DesyncLogged)
            {
                st.DesyncLogged = true;
                Mod.Log.Warn($"[RevivalGuard] movement desync: {p.Name} (0x{p.Guid.Full:X8}) echoed none of {st.Unheard} snap-backs in "
                    + $"{(now - st.UnheardSince).TotalSeconds:F0} s (its client is not applying the server's positions); forcing a relog at {p.Location.ToLOCString()}");
                if (p.Session != null) p.Session.LogOffPlayer(true); else p.LogOut();
            }
            // The server's position stands as of now
            st.Pos = new ACE.Entity.Position(p.Location);
            st.At = now;
        }

        /// <summary>Ace's own snap-back counts as a snap.</summary>
        static void Postfix(Player __instance, bool __result)
        {
            try
            {
                var p = __instance;
                if (__result || !s_State.TryGetValue(p, out var st) || st.ForceBefore < 0) return;
                int after = ForceSeq(p);
                if (after < 0 || after == st.ForceBefore) return;
                st.SnapStamp = (ushort)after; st.SnapAt = DateTime.UtcNow; st.StaleDrops = 0;
                MoveGuard.Restart(p);
                st.Pos = p.Location != null ? new ACE.Entity.Position(p.Location) : null;
                st.At = st.SnapAt;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] movement: {e.Message}"); }
            finally { if (s_State.TryGetValue(__instance, out var s2)) s2.ForceBefore = -1; }
        }
    }

    /// <summary>The movement budget's arithmetic, pure so tools/modcheck can test it without a server.</summary>
    public static class MovementBudget
    {
        public const float BASE_RUN = 4.0f;   // m/s at run rate 1 (retail RunForward)

        /// <summary>Refill budget by dt seconds at run speed x slack, capped at burstSeconds of it; a move fits within budget + margin.</summary>
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

        /// <summary>A position report sent before the client saw the snap-back</summary>
        public const int DESYNC_SNAPS = 10;
        public const double DESYNC_SECONDS = 30.0;
        public static bool Desynced(int unheardSnaps, double secondsSinceFirst) => unheardSnaps >= DESYNC_SNAPS && secondsSinceFirst >= DESYNC_SECONDS;

        public static bool StaleAfterSnap(ushort reportStamp, ushort snapStamp, double sinceSnap, double window)
        {
            if (sinceSnap < 0 || sinceSnap > window) return false;
            ushort behind = (ushort)(snapStamp - reportStamp);
            return behind != 0 && behind < 0x8000;
        }
    }

    /// <summary>In-flight reports after a snap-back.</summary>
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

        // The stamp is kept beside the very Position object the handler stored as RequestedLocation
        static void Postfix(Session session)
        {
            var p = session?.Player;
            if (p == null || t_Stamp == 0) return;
            var st = MovementGuard.s_State.GetOrCreateValue(p);
            st.ReqPos = p.RequestedLocation; st.ReqStamp = (ushort)(t_Stamp - 1);
        }

        static Exception Finalizer(Exception __exception) { t_Stamp = 0; return __exception; }
    }

    /// <summary>A teleport moves the player without a client report</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Teleport))]
    static class TeleportRebaseline
    {
        static void Prefix(Player __instance) => MovementGuard.TeleportFrom(__instance);
        static void Postfix(Player __instance) => MovementGuard.Rebaseline(__instance);
    }

    /// <summary>Vendor price overflow.</summary>
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

    /// <summary>Fellowship size.</summary>
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

    /// <summary>Action floods.</summary>
    [HarmonyPatch(typeof(InboundMessageManager), nameof(InboundMessageManager.HandleGameAction))]
    static class FloodGuard
    {
        const int PER_SECOND = 30, QUERIES_PER_SECOND = 150;
        sealed class Bucket { public long Second; public int Count; public int Queries; }
        static readonly ConditionalWeakTable<Session, Bucket> s_Buckets = new ConditionalWeakTable<Session, Bucket>();

        /// <summary>Asks for information only: nothing moves, so no dupe can be raced with it.</summary>
        internal static bool IsQuery(GameActionType op) => op == GameActionType.IdentifyObject || op == GameActionType.QueryItemMana
            || op == GameActionType.QueryHealth || op == GameActionType.QueryAge || op == GameActionType.QueryBirth || op == GameActionType.PingRequest;

        /// <summary>Retail's client records these as a pending request and waits for the server's answer; a silent drop leaves it waiting.</summary>
        internal static bool AwaitsAnswer(GameActionType op) => op == GameActionType.Buy || op == GameActionType.Sell
            || op == GameActionType.PutItemInContainer || op == GameActionType.GetAndWieldItem || op == GameActionType.DropItem
            || op == GameActionType.StackableMerge || op == GameActionType.StackableSplitToContainer || op == GameActionType.StackableSplitTo3D
            || op == GameActionType.StackableSplitToWield || op == GameActionType.GiveObjectRequest;

        static void Refuse(GameActionType op, Session session)
        {
            var p = session.Player;
            session.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventInventoryServerSaveFailed(session, p.Guid.Full));
            if (op == GameActionType.Buy || op == GameActionType.Sell) p.SendUseDoneEvent(WeenieError.YoureTooBusy);
        }

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
            if (!SocialGuard.Allow(opcode, message, session)) return false;
            var b = s_Buckets.GetOrCreateValue(session);
            long sec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            bool query = IsQuery(opcode);
            lock (b)
            {
                if (b.Second != sec) { b.Second = sec; b.Count = 0; b.Queries = 0; }
                if (query)
                {
                    if (++b.Queries <= QUERIES_PER_SECOND) return true;
                    if (b.Queries == QUERIES_PER_SECOND + 1)
                        Offences.Add(session.Player, "flood", $"more than {QUERIES_PER_SECOND} queries in one second (last {opcode}); the excess is dropped");
                    return false;
                }
                if (++b.Count <= PER_SECOND) return true;
                if (b.Count == PER_SECOND + 1)
                    Offences.Add(session.Player, "flood", $"more than {PER_SECOND} game actions in one second (last {opcode}); the excess is dropped");
                // answer a bounded number, so a real flood cannot turn the refusals into an amplifier
                if (b.Count <= PER_SECOND * 2 && AwaitsAnswer(opcode)) Refuse(opcode, session);
                return false;
            }
        }
    }

    /// <summary>Chat spam.</summary>
    static class ChatGuard
    {
        const int MAX_MSGS = 6; const double WINDOW = 10;
        const int MAX_REPEAT = 2; const double REPEAT_WINDOW = 30;

        // The client's own queries.
        static readonly HashSet<string> s_Auto = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "@revivalclient 1", "@questlog", "@rgexpansion 0", "@rgexpansion 1", "@ragdollcorpse 0",
            "@ragdollcorpse 1", "@time", "@pop", "@myquests", "@gps",
            "@features",   // the feature request window asks when it opens and once a minute while open
            "@recruit board",   // the Allegiance page asks when it comes into view and every 30 s while shown (AcAllegianceFinder)
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
                case (GameActionType)0x01E1: break;   // SoulEmote: a *wave* typed in chat, a text line to everyone near
                default: return true;
            }
            if (session.AccessLevel >= AccessLevel.Admin) return true;

            string text = null;
            try
            {
                var st = message.Payload.BaseStream; long pos = st.Position;
                try { text = ChatRepeatKey.Of((uint)opcode, message.Payload); }   // ChatChannel: channel first
                finally { st.Position = pos; }   // the real handler reads it again
            }
            catch { }

            // The staff Dressing Room's panel speaks one @dress line per click
            if (opcode == GameActionType.Talk && session.AccessLevel >= AccessLevel.Sentinel && text != null
                && text.TrimStart().StartsWith("@dress ", StringComparison.OrdinalIgnoreCase)) return true;
            // The Decorate panel speaks one @decor line per gesture; @decor needs Developer on a test shard and Admin on live.
            if (opcode == GameActionType.Talk && session.AccessLevel >= AccessLevel.Developer && text != null
                && text.TrimStart().StartsWith("@decor ", StringComparison.OrdinalIgnoreCase)) return true;

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
                if (text != null && session.AccessLevel < AccessLevel.Sentinel && PkCommon.On("rg_macro_guards"))
                {
                    string said = text.StartsWith("#") && text.IndexOf(':') > 0 ? text.Substring(text.IndexOf(':') + 1) : text;
                    var c = s_Cadence.Check(session.Player.Guid.Full, said, now);
                    if (c.Drop)
                    {
                        if (c.Verdict != ChatCadenceRule.Verdict.Muted || (now - h.NoticeAt).TotalSeconds > 10)
                        { h.NoticeAt = now; session.Player.SendTransientError(ChatCadenceRule.Notice(c, now)); }
                        if (c.Verdict == ChatCadenceRule.Verdict.Muted) Offences.Add(session.Player, "chat", $"{opcode} dropped: muted until {c.Until:HH:mm} UTC");
                        else Mod.Log.Warn($"[RevivalGuard] chat-macro: {session.Player.Name} (0x{session.Player.Guid.Full:X8}) strike {c.Strike}, "
                            + (c.Verdict == ChatCadenceRule.Verdict.Warn ? "warned" : $"muted until {c.Until:HH:mm} UTC") + $": {c.Why} (\"{Clip(said)}\")");
                        return false;
                    }
                }
                h.Times.Enqueue(now);
                if (text != null) h.Texts.Enqueue((now, text));
                return true;
            }
        }

        static readonly ChatCadenceRule s_Cadence = new ChatCadenceRule();
        static string Clip(string t) => t.Length <= 80 ? t : t.Substring(0, 80) + "...";
    }

    /// <summary>Trade requests, fellowship invites, allegiance requests each pop up on another player's screen</summary>
    static class SocialGuard
    {
        static readonly SocialRequestRule s_Rule = new SocialRequestRule();

        public static bool Allow(GameActionType opcode, ClientMessage message, Session session)
        {
            if (!PkCommon.On("rg_macro_guards")) return true;
            if (!SocialRequestRule.Covers((uint)opcode) || session.AccessLevel >= AccessLevel.Sentinel) return true;
            uint to;
            try
            {
                var st = message.Payload.BaseStream; long pos = st.Position;
                try { to = message.Payload.ReadUInt32(); }
                finally { st.Position = pos; }   // the real handler reads it again
            }
            catch { return true; }
            string why = s_Rule.Check(session.Player.Guid.Full, (uint)opcode, to, DateTime.UtcNow);
            if (why == null) return true;
            session.Player.SendTransientError(why);
            Offences.Add(session.Player, "request", $"{SocialRequestRule.What((uint)opcode)} to 0x{to:X8} dropped: {why}");
            return false;
        }
    }

}
