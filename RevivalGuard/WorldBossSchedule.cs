using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    /// THE WORLD BOSS SCHEDULE (owner, 2026-09-30: "similar to diablo's where its timed, lets players
    /// know when its happening, and is shown on the map"). Ours, not retail. Design and the Diablo IV
    /// research it follows: docs/WORLD-BOSS-SYSTEM.md.
    ///
    ///   schedule     every boss rises on a fixed rhythm from an anchor in UTC-5 (Bael'Zharon: every
    ///                7 hours, so each hour of the day gets one rising a week, as Diablo's 3.5 h does)
    ///   warnings     world broadcasts at the boss's lead times (30, 10, 2 minutes), then one when he rises
    ///   window       he stays for his window (30 minutes); unbeaten at its end he withdraws (despawned)
    ///   rewards      every player in his damage history gets a personal roll from his table, and the
    ///                first victory of each ISO week (UTC-5) per character adds a bonus roll from the
    ///                rare end; both go into the pack, and what does not fit waits (@worldboss claim)
    ///   the map      a "worldboss" message on the mod channel on every change and at hello; our client
    ///                draws the marker and the countdown on the world map (AcWorldMapHud.WorldBoss.cs).
    ///                Since 2026-10-01 he stands outdoors and nothing gives a route: chat names only the
    ///                region (Where), and the marker is the only exact place.
    ///
    /// Spawning is still the boss's own generator (900202 for Bael'Zharon): outside a window every
    /// profile of his is held a day ahead, and when the window opens it is released and the generator
    /// asked to generate. The generator's own Delay is 7 days (world-boss-schedule.sql), so nothing but
    /// this schedule, or WorldBoss's 24 h mode, brings him back.
    ///
    /// Switch `rg_world_boss_schedule` (default off, tools/gpubox-ace/live-switch.sh). Off, WorldBoss.cs
    /// runs the old rule: 24 h after his last death.
    /// </summary>
    static class WorldBossSchedule
    {
        public const string P_ON = "rg_world_boss_schedule";
        internal const int UTC_OFFSET_HOURS = -5;
        const double TICK = 5, SPAWN_RETRY = 15;

        public static bool On => PropertyManager.GetBool(P_ON, false).Item;

        // ------------------------------------------------------------------ loot data
        internal sealed class Tier
        {
            public string Name; public int Weight; public uint[] Items;
            public Tier(string name, int weight, params uint[] items) { Name = name; Weight = weight; Items = items; }
        }

        static uint[] Range(uint a, uint b) { var l = new List<uint>(); for (var i = a; i <= b; i++) l.Add(i); return l.ToArray(); }

        // The held-back items of docs/OBTAINABLE-ITEMS-2026-09-30.md section 5 and the Zefir Charms.
        static readonly uint[] SOUL_CRYSTAL = Range(7998, 8009).Concat(Range(8022, 8030)).Concat(Range(23529, 23531)).ToArray();
        static readonly uint[] ZEFIR = Range(900760, 900768);
        static readonly uint[] KEEPSAKE = { 9173, 9175, 52699, 22556, 22550, 7400 };   // Pack Ursuin, Pack Cow, Wooden Top, Mace Tattoo, Tusker Paws, Heaume of the Inscrutable Mind
        static readonly uint[] RELIC = { 33026, 31838, 35395, 52193, 41915, 40443 };   // Souldrinker, Hammer of Discipline, House Mhoire Shield, Mukkir Wings, Weapon and Armor Upgrade Kits
        static readonly uint[] LEGEND = { 12269 };                                      // Shroud of Levistras

        internal static readonly Tier[] BAEL_KILL =
        {
            new Tier("Soul Crystal", 55, SOUL_CRYSTAL),
            new Tier("Zefir Charm", 15, ZEFIR),
            new Tier("Keepsake", 15, KEEPSAKE),
            new Tier("Relic", 12, RELIC),
            new Tier("Legend", 3, LEGEND),
        };
        internal static readonly Tier[] BAEL_WEEKLY =
        {
            new Tier("Relic", 85, RELIC),
            new Tier("Legend", 15, LEGEND),
        };

        // ------------------------------------------------------------------ state (persisted)
        internal sealed class BossState
        {
            public double ForcedStart, ForcedEnd, ForcedLead;
            public List<double> Skipped = new List<double>();
            public double ActiveStart, ActiveEnd;          // the window he was raised for; 0 = none open
            public double LastStart, LastEnd;              // the last window settled
            public string LastOutcome;                     // "defeated" | "escaped"
            public double AnnouncedFor;
            public List<int> Announced = new List<int>();
        }
        internal sealed class Owe { public uint Wcid; public string Why; }
        sealed class State
        {
            public Dictionary<string, BossState> Bosses = new Dictionary<string, BossState>();
            public Dictionary<string, List<Owe>> Owed = new Dictionary<string, List<Owe>>();                   // character guid (hex)
            public Dictionary<string, Dictionary<string, string>> Weekly = new Dictionary<string, Dictionary<string, string>>();   // guid -> boss key -> ISO week
        }

        static readonly JsonSerializerOptions JSON = new JsonSerializerOptions { IncludeFields = true };
        static readonly JsonSerializerOptions WIRE = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        static readonly object s_Lock = new object();
        static State s_State;
        static string s_Path;
        static double s_Next;
        static bool? s_WasOn;
        static readonly Dictionary<string, string> s_Sig = new Dictionary<string, string>();
        static readonly Dictionary<string, double> s_SpawnTry = new Dictionary<string, double>();
        static readonly Dictionary<string, double> s_Despawned = new Dictionary<string, double>();
        static readonly Random s_Rng = new Random();

        /// <summary>A short test rhythm set by an admin (@worldboss testschedule), memory only.</summary>
        sealed class TestRhythm { public double Anchor, Every, Lead, Window; }
        static TestRhythm s_Test;

        static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        static DateTime Utc(double unix) => DateTime.UnixEpoch.AddSeconds(unix);

        internal static void Register()
        {
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard WorldBossSchedule: world bosses rise on a fixed schedule with warnings, a map marker, a timed window and personal rewards (off: Bael'Zharon's 24 h after death)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBossSchedule: property default not registered: {e.Message}"); }
            Load();
            Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: {(On ? "ON" : "off")} ({P_ON}); {WorldBoss.Bosses.Count} boss(es)");
        }

        static BossState St(WorldBoss.Spec s)
        {
            if (s_State == null) Load();   // a lair can load (HoldNewProfile) before Register
            if (!s_State.Bosses.TryGetValue(s.Key, out var st)) s_State.Bosses[s.Key] = st = new BossState();
            return st;
        }

        // ------------------------------------------------------------------ the rhythm
        internal struct Ev { public double Start, End, Lead; public bool Forced; public bool Valid => End > 0; }

        static double AnchorUnix(WorldBoss.Spec s) =>
            (DateTime.SpecifyKind(s.AnchorLocal.AddHours(-UTC_OFFSET_HOURS), DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

        /// <summary>The window now open, or the next one: a forced one first, then the rhythm, skipping
        /// skipped risings and any that would overlap a forced window.</summary>
        static Ev Next(WorldBoss.Spec s, BossState st, double now)
        {
            if (st.ForcedEnd > now) return new Ev { Start = st.ForcedStart, End = st.ForcedEnd, Lead = st.ForcedLead, Forced = true };
            var t = s_Test;
            double every = t?.Every ?? s.Interval.TotalSeconds, win = t?.Window ?? s.Window.TotalSeconds,
                   lead = t?.Lead ?? s.Lead.TotalSeconds, anchor = t?.Anchor ?? AnchorUnix(s);
            long k = (long)Math.Floor((now - anchor) / every);
            for (int i = 0; i < 500; i++, k++)
            {
                double start = anchor + k * every, end = start + win;
                if (end <= now || st.Skipped.Contains(start)) continue;
                if (start < st.ForcedEnd && end > st.ForcedStart) continue;
                return new Ev { Start = start, End = end, Lead = lead };
            }
            return default;
        }

        enum Phase { Idle, Upcoming, Active, Defeated }

        static Phase PhaseOf(Ev e, BossState st, double now) =>
            !e.Valid || now < e.Start - e.Lead ? Phase.Idle
            : now < e.Start ? Phase.Upcoming
            : st.LastOutcome == "defeated" && st.LastStart == e.Start ? Phase.Defeated
            : Phase.Active;

        // ------------------------------------------------------------------ the tick (world loop)
        internal static void Tick()
        {
            double now = Now();
            if (now < s_Next) return;
            s_Next = now + TICK;
            bool on = On;
            if (s_WasOn != on)
            {
                bool first = s_WasOn == null;
                s_WasOn = on;
                if (!first) Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: switched {(on ? "ON" : "off")}");
                s_Sig.Clear();
                if (!on && !first) PushAll();   // the markers go away
            }
            if (!on) return;
            foreach (var s in WorldBoss.Bosses.Values)
            {
                try { TickBoss(s, now); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] WorldBossSchedule: {s.Name}: {e}"); }
            }
        }

        static void TickBoss(WorldBoss.Spec s, double now)
        {
            Ev e; Phase ph; BossState st;
            string rise = null;
            lock (s_Lock)
            {
                st = St(s);
                e = Next(s, st, now);
                // A window he was raised for has closed: time is up, or it was skipped or reset.
                if (st.ActiveStart != 0 && (e.Start != st.ActiveStart || now >= st.ActiveEnd))
                    CloseWindow(s, st, now);
                e = Next(s, st, now);
                ph = PhaseOf(e, st, now);
                if (ph == Phase.Upcoming) Announce(s, st, e, now);
                if (ph == Phase.Active && st.ActiveStart != e.Start)
                {
                    st.ActiveStart = e.Start; st.ActiveEnd = e.End;
                    Save();
                    rise = $"{s.Name} has risen somewhere in {s.Where}! {s.He} withdraws in {Minutes(e.End - now)}. Every hand that wounds {s.Him} is rewarded.";
                    Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: {s.Name} window open {Stamp(e.Start)} to {Stamp(e.End)}{(e.Forced ? " (forced)" : "")}");
                }
            }
            if (rise != null) World(rise);
            if (ph == Phase.Active) EnsureSpawned(s, now);
            else Hold(s);
            PushIfChanged(s, now);
        }

        static void CloseWindow(WorldBoss.Spec s, BossState st, double now)
        {
            bool defeated = st.LastOutcome == "defeated" && st.LastStart == st.ActiveStart;
            if (!defeated)
            {
                int gone = Despawn(s, "his window closed");
                st.LastStart = st.ActiveStart; st.LastEnd = Math.Min(now, st.ActiveEnd); st.LastOutcome = "escaped";
                st.ActiveStart = st.ActiveEnd = 0;
                var n = Next(s, st, now);
                World($"{s.Escaped} {s.He} returns {When(n.Start)}.");
                Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: {s.Name} escaped ({gone} despawned); next {Stamp(n.Start)}");
            }
            st.ActiveStart = st.ActiveEnd = 0;
            Save();
        }

        /// <summary>One broadcast per lead time crossed; after a restart, one with the true minutes.</summary>
        static void Announce(WorldBoss.Spec s, BossState st, Ev e, double now)
        {
            if (st.AnnouncedFor != e.Start) { st.AnnouncedFor = e.Start; st.Announced.Clear(); }
            double rem = e.Start - now;
            var warn = s_Test != null ? new[] { (int)Math.Ceiling(e.Lead / 60), 1 } : s.Warn;
            var crossed = warn.Where(m => m * 60 >= rem - 0.5 && !st.Announced.Contains(m)).ToList();
            if (crossed.Count == 0) return;
            st.Announced.AddRange(crossed);
            Save();
            World($"{s.Stir} {s.He} rises in {Minutes(rem)}.");   // no place beyond the region: the way is for players to find
        }

        // ------------------------------------------------------------------ spawning and holding
        static void EnsureSpawned(WorldBoss.Spec s, double now)
        {
            if (WorldBoss.FindAlive(s) != null) return;
            if (s_SpawnTry.TryGetValue(s.Key, out var last) && now - last < SPAWN_RETRY) return;
            s_SpawnTry[s.Key] = now;
            if (WorldBoss.LoadedLair(s) == null)
            {
                LandblockManager.GetLandblock(new LandblockId((uint)s.Lair << 16 | 0xFFFF), false);
                Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: loading lair 0x{s.Lair:X4} for {s.Name}");
                return;   // the generator comes up with the block; next try releases it
            }
            int n = 0;
            foreach (var prof in WorldBoss.LairProfiles(s).ToList())
            {
                prof.NextAvailable = DateTime.UtcNow.AddSeconds(-1);
                var gen = prof.Generator;
                if (gen == null) continue;
                var chain = new ActionChain();
                chain.AddAction(gen, () => { try { gen.Generator_Generate(); } catch (Exception ex) { Mod.Log.Warn($"[RevivalGuard] WorldBossSchedule: generate: {ex.Message}"); } });
                chain.EnqueueChain();
                n++;
            }
            if (n == 0) Mod.Log.Warn($"[RevivalGuard] WorldBossSchedule: no generator profile for {s.Name} (wcid {s.Wcid}) in 0x{s.Lair:X4}; he cannot rise");
            else Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: released {n} generator profile(s) for {s.Name}");
        }

        /// <summary>Outside a window: every profile of his held a day ahead, and a boss standing
        /// outside any window (the 24 h rule raised him, an admin made one) sent away once nobody
        /// is fighting him.</summary>
        static void Hold(WorldBoss.Spec s)
        {
            var until = DateTime.UtcNow.AddDays(1);
            foreach (var prof in WorldBoss.LairProfiles(s))
                if (prof.NextAvailable < until.AddHours(-1)) prof.NextAvailable = until;
            var c = WorldBoss.FindAlive(s);
            if (c != null && !(s_Despawned.TryGetValue(s.Key, out var at) && Now() - at < 10) && (c.DamageHistory == null || c.DamageHistory.TotalDamage.Count == 0 || c.Health.Current >= c.Health.MaxValue))
                Despawn(s, "outside any window, nobody fighting");
        }

        static int Despawn(WorldBoss.Spec s, string why)
        {
            int n = 0;
            foreach (var c in WorldBoss.AllAlive(s).ToList())
            {
                var chain = new ActionChain();
                chain.AddAction(c, () => { if (!c.IsDestroyed && !c.IsDead) c.Destroy(); });
                chain.EnqueueChain();
                n++;
            }
            if (n > 0) { s_Despawned[s.Key] = Now(); Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: {s.Name} despawned x{n} ({why})"); }
            return n;
        }

        /// <summary>For WorldBoss's generator-profile postfix: a profile made while no window is open
        /// (a restart, a reload) starts held.</summary>
        internal static bool HoldNewProfile(WorldBoss.Spec s, GeneratorProfile profile)
        {
            if (!On) return false;
            double now = Now();
            Phase ph;
            lock (s_Lock) { var st = St(s); ph = PhaseOf(Next(s, st, now), st, now); }
            if (ph != Phase.Active) profile.NextAvailable = DateTime.UtcNow.AddDays(1);
            return true;
        }

        // ------------------------------------------------------------------ death and rewards
        /// <summary>From WorldBoss's Creature.OnDeath postfix (the boss's landblock thread).</summary>
        internal static void OnBossDeath(WorldBoss.Spec s, Creature boss)
        {
            if (!On) return;
            double now = Now();
            var hands = new HashSet<uint>();
            if (boss.DamageHistory != null)
                foreach (var info in boss.DamageHistory.TotalDamage.Values.ToList())
                {
                    if (info == null || info.TotalDamage <= 0) continue;
                    if (info.IsPlayer) hands.Add(info.Guid.Full);
                    else if (info.PetOwner != null && info.TryGetPetOwner() is Player owner) hands.Add(owner.Guid.Full);
                }
            string week = WeekKey(DateTime.UtcNow);
            lock (s_Lock)
            {
                var st = St(s);
                if (st.ActiveStart != 0) { st.LastStart = st.ActiveStart; st.LastEnd = now; st.LastOutcome = "defeated"; }
                foreach (var g in hands)
                {
                    var key = g.ToString("X8");
                    Add(key, Roll(s.Kill), $"your share of {s.Name}'s hoard");
                    if (!s_State.Weekly.TryGetValue(key, out var w)) s_State.Weekly[key] = w = new Dictionary<string, string>();
                    if (!w.TryGetValue(s.Key, out var had) || had != week)
                    {
                        w[s.Key] = week;
                        Add(key, Roll(s.Weekly), $"your first victory over {s.Name} this week");
                    }
                }
                Save();
            }
            World(hands.Count == 0
                ? $"{s.Name} is defeated. {s.He} returns {When(NextAfterNow(s, now))}."
                : $"{s.Name} is defeated! {hands.Count} {(hands.Count == 1 ? "champion shares" : "champions share")} the spoils. {s.He} returns {When(NextAfterNow(s, now))}.");
            Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: {s.Name} defeated; rewards for {hands.Count} player(s) in his damage history");
            foreach (var g in hands)
            {
                var p = PlayerManager.GetOnlinePlayer(g);
                if (p != null) DeliverLater(p, false);
            }
            PushIfChanged(s, now);
        }

        static double NextAfterNow(WorldBoss.Spec s, double now)
        {
            lock (s_Lock)
            {
                var st = St(s);
                var e = Next(s, st, now);
                if (e.Valid && e.Start <= now) e = Next(s, st, e.End + 1);
                return e.Start;
            }
        }

        static uint Roll(Tier[] table)
        {
            int total = table.Sum(t => t.Weight), r;
            lock (s_Rng) r = s_Rng.Next(total);
            foreach (var t in table)
            {
                if (r < t.Weight) { lock (s_Rng) return t.Items[s_Rng.Next(t.Items.Length)]; }
                r -= t.Weight;
            }
            return table[0].Items[0];
        }

        static void Add(string key, uint wcid, string why)
        {
            if (!s_State.Owed.TryGetValue(key, out var l)) s_State.Owed[key] = l = new List<Owe>();
            l.Add(new Owe { Wcid = wcid, Why = why });
        }

        /// <summary>Personal loot for another boss (BossTrophies): owed to the character, then into the pack
        /// if online; a full pack keeps it owed for the next login or @worldboss claim.</summary>
        internal static void Grant(uint guid, uint wcid, string why)
        {
            Load();
            lock (s_Lock) { Add(guid.ToString("X8"), wcid, why); Save(); }
            var p = PlayerManager.GetOnlinePlayer(guid);
            if (p != null) DeliverLater(p, true);
        }

        internal static void DeliverLater(Player p, bool quietIfNone)
        {
            var chain = new ActionChain();
            chain.AddAction(p, () => Deliver(p, quietIfNone));
            chain.EnqueueChain();
        }

        /// <summary>Into the pack; what does not fit stays owed (PkBounty.Collect's rule).</summary>
        static void Deliver(Player p, bool quietIfNone)
        {
            if (p?.Session == null) return;
            var key = p.Guid.Full.ToString("X8");
            List<Owe> take;
            lock (s_Lock)
            {
                if (!s_State.Owed.TryGetValue(key, out take) || take.Count == 0)
                {
                    if (!quietIfNone) Say(p, "You are owed nothing from the world bosses.");
                    return;
                }
                s_State.Owed.Remove(key);
                Save();
            }
            var left = new List<Owe>();
            bool heavy = false;
            foreach (var o in take)
            {
                WorldObject wo = null;
                try
                {
                    wo = WorldObjectFactory.CreateNewWorldObject(o.Wcid);
                    if (wo == null) { Mod.Log.Warn($"[RevivalGuard] WorldBossSchedule: wcid {o.Wcid} could not be made for {p.Name}; dropped"); continue; }
                    if (p.TryCreateInInventoryWithNetworking(wo)) { Say(p, $"You receive {wo.Name}, {o.Why}."); Mod.Log.Info($"[RevivalGuard] WorldBossSchedule: {p.Name} received {wo.Name} ({o.Wcid}), {o.Why}"); }
                    else { heavy |= !p.HasEnoughBurdenToAddToInventory(wo); wo.Destroy(); left.Add(o); }
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] WorldBossSchedule: delivering {o.Wcid} to {p.Name}: {e.Message}"); left.Add(o); }
            }
            if (left.Count > 0)
            {
                lock (s_Lock)
                {
                    if (!s_State.Owed.TryGetValue(key, out var l)) s_State.Owed[key] = l = new List<Owe>();
                    l.InsertRange(0, left);
                    Save();
                }
                Say(p, $"{left.Count} {(left.Count == 1 ? "reward" : "rewards")} ({string.Join(", ", left.Select(o => o.Why.Length > 0 ? o.Why : o.Wcid.ToString()))}) {(heavy ? (left.Count == 1 ? "is" : "are") + " too heavy for you to carry now" : "did not fit in your pack")}. {(left.Count == 1 ? "It is" : "They are")} kept for you: {(heavy ? "lighten your load" : "make room")}, then type @worldboss claim (or log in again).");
            }
        }

        internal static void AtLogin(Player p)
        {
            lock (s_Lock) { if (!s_State.Owed.TryGetValue(p.Guid.Full.ToString("X8"), out var l) || l.Count == 0) return; }
            DeliverLater(p, true);
        }

        // ------------------------------------------------------------------ the mod channel
        internal static void Hello(Session session)
        {
            if (!On || session == null) return;
            ModChannel.Send(session, "worldboss", Payload(Now()));
        }

        static void PushIfChanged(WorldBoss.Spec s, double now)
        {
            string sig;
            lock (s_Lock) sig = Describe(s, now, out _);
            if (s_Sig.TryGetValue(s.Key, out var had) && had == sig) return;
            s_Sig[s.Key] = sig;
            PushAll();
        }

        static void PushAll()
        {
            var payload = Payload(Now());
            foreach (var p in PlayerManager.GetAllOnline())
                if (p?.Session != null && ModChannel.Capable(p.Session)) ModChannel.Send(p.Session, "worldboss", payload);
        }

        sealed class Wire
        {
            public string key { get; set; } public string name { get; set; } public string where { get; set; }
            public double ns { get; set; } public double ew { get; set; } public string state { get; set; }
            public long start { get; set; } public long end { get; set; } public long next { get; set; }
        }

        /// <summary>What the client draws: state, the window it refers to, and the next rising.</summary>
        static string Describe(WorldBoss.Spec s, double now, out Wire w)
        {
            var st = St(s);
            var e = Next(s, st, now);
            var ph = PhaseOf(e, st, now);
            w = new Wire { key = s.Key, name = s.Name, where = s.Where, ns = s.NS, ew = s.EW };
            switch (ph)
            {
                case Phase.Upcoming: w.state = "upcoming"; w.start = (long)e.Start; w.end = (long)e.End; w.next = (long)e.Start; break;
                case Phase.Active: w.state = "active"; w.start = (long)e.Start; w.end = (long)e.End; w.next = (long)Next(s, st, e.End + 1).Start; break;
                case Phase.Defeated: w.state = "defeated"; w.start = (long)e.Start; w.end = (long)e.End; w.next = (long)Next(s, st, e.End + 1).Start; break;
                default:
                    bool recent = st.LastOutcome != null && now - st.LastEnd < 6 * 3600;
                    w.state = recent ? st.LastOutcome : "idle";
                    w.start = recent ? (long)st.LastStart : 0; w.end = recent ? (long)st.LastEnd : 0; w.next = (long)e.Start;
                    break;
            }
            return $"{w.state}|{w.start}|{w.end}|{w.next}";
        }

        static string Payload(double now)
        {
            var list = new List<Wire>();
            if (On)
                lock (s_Lock)
                    foreach (var s in WorldBoss.Bosses.Values) { Describe(s, now, out var w); list.Add(w); }
            return JsonSerializer.Serialize(new { now = (long)now, bosses = list }, WIRE);
        }

        // ------------------------------------------------------------------ @worldboss
        internal static void Command(Session session, string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            bool admin = session == null || session.AccessLevel >= AccessLevel.Admin;
            var player = session?.Player;
            if (verb == "claim")
            {
                if (player == null) { Reply(session, "Claim is for a character in the world."); return; }
                DeliverLater(player, false);
                return;
            }
            if (verb.Length > 0 && verb != "status")
            {
                if (!admin) { Reply(session, "@worldboss shows when the world bosses rise. @worldboss claim hands over rewards that did not fit in your pack."); return; }
                Reply(session, Admin(verb, args, session));
                return;
            }
            Reply(session, Status(player, admin));
        }

        static string Status(Player p, bool admin)
        {
            double now = Now();
            var sb = new StringBuilder();
            if (!On) sb.Append("World bosses are not on a schedule right now.\n");
            foreach (var s in WorldBoss.Bosses.Values)
            {
                if (!On) break;
                lock (s_Lock)
                {
                    var st = St(s);
                    var e = Next(s, st, now);
                    var ph = PhaseOf(e, st, now);
                    string at = $"{s.He} rises somewhere in {s.Where}; finding the way is up to you.";
                    switch (ph)
                    {
                        case Phase.Active: sb.Append($"{s.Name} is abroad now! {s.He} withdraws in {Minutes(e.End - now)}. {at}\n"); break;
                        case Phase.Defeated: sb.Append($"{s.Name} has been defeated. {s.He} returns {When(Next(s, st, e.End + 1).Start)}.\n"); break;
                        default: sb.Append($"{s.Name} rises {When(e.Start)}, in {Span(e.Start - now)}, and stays {Minutes(e.End - e.Start)}. {at}\n"); break;
                    }
                    var later = new List<string>();
                    var probe = e;
                    for (int i = 0; i < 3 && probe.Valid; i++) { probe = Next(s, st, probe.End + 1); if (probe.Valid) later.Add(LocalShort(probe.Start)); }
                    if (later.Count > 0) sb.Append($"{(ph == Phase.Idle || ph == Phase.Upcoming ? "After that" : "Next risings")}: {string.Join(", ", later)} (server time, UTC-5).\n");
                    if (p != null)
                    {
                        var key = p.Guid.Full.ToString("X8");
                        bool claimed = s_State.Weekly.TryGetValue(key, out var w) && w.TryGetValue(s.Key, out var wk) && wk == WeekKey(DateTime.UtcNow);
                        sb.Append(claimed ? $"You have had this week's first victory bonus from {s.Name}.\n" : $"Your first victory over {s.Name} this week earns a bonus reward.\n");
                    }
                    if (admin)
                        sb.Append($"[WorldBoss] admin: site {Coords(s)} (0x{s.Lair:X4}), {(e.Forced ? "forced " : "")}window {Stamp(e.Start)} to {Stamp(e.End)} UTC, phase {ph}, raised-for {(st.ActiveStart == 0 ? "none" : Stamp(st.ActiveStart))}, last {st.LastOutcome ?? "none"}, skipped {st.Skipped.Count}{(s_Test != null ? ", TEST RHYTHM" : "")}, alive {(WorldBoss.FindAlive(s) is Creature b ? $"yes, {b.Health.Current:N0}/{b.Health.MaxValue:N0} health" : "no")}\n");
                }
            }
            if (p != null)
                lock (s_Lock)
                    if (s_State.Owed.TryGetValue(p.Guid.Full.ToString("X8"), out var l) && l.Count > 0)
                        sb.Append($"{l.Count} reward(s) wait for room in your pack: @worldboss claim.\n");
            if (admin) sb.Append("[WorldBoss] admin verbs: spawn [minutes] [window minutes], respawn, skip, reset, testschedule <every> <lead> <window> (minutes), weeklyreset [name]");
            return sb.ToString().TrimEnd();
        }

        static string Admin(string verb, string[] args, Session session)
        {
            double now = Now();
            if (!On && verb != "respawn") return $"[WorldBoss] the schedule is off ({P_ON}); only 'respawn' (the 24 h rule's) applies.";
            int Arg(int i, int d) => args.Length > i && int.TryParse(args[i], out var v) ? v : d;
            var sb = new StringBuilder();
            foreach (var s in WorldBoss.Bosses.Values)
            {
                lock (s_Lock)
                {
                    var st = St(s);
                    switch (verb)
                    {
                        case "spawn":
                        case "respawn":
                        {
                            int lead = Math.Max(0, Arg(1, 0)), win = Math.Max(1, Arg(2, (int)s.Window.TotalMinutes));
                            st.ForcedStart = now + lead * 60; st.ForcedEnd = st.ForcedStart + win * 60; st.ForcedLead = lead * 60;
                            bool beaten = st.LastOutcome == "defeated" && st.LastStart == st.ActiveStart;
                            if (st.ActiveStart != 0 && !beaten && st.ActiveStart != st.ForcedStart) { st.ActiveStart = st.ForcedStart; st.ActiveEnd = st.ForcedEnd; }   // he is up: the forced window takes over
                            Save();
                            sb.Append($"[WorldBoss] {s.Name}: forced window {(lead == 0 ? "now" : $"in {lead} min")} for {win} min.\n");
                            break;
                        }
                        case "skip":
                        {
                            var e = Next(s, st, now);
                            if (PhaseOf(e, st, now) == Phase.Upcoming && st.AnnouncedFor == e.Start && st.Announced.Count > 0)
                                World($"{s.Name} will not rise this time.");
                            if (e.Forced) { st.ForcedEnd = Math.Min(st.ForcedEnd, now); st.ForcedStart = Math.Min(st.ForcedStart, now); }
                            else st.Skipped.Add(e.Start);
                            Save();
                            sb.Append($"[WorldBoss] {s.Name}: skipped the window at {Stamp(e.Start)} UTC{(st.ActiveStart != 0 ? "; he withdraws on the next tick" : "")}.\n");
                            break;
                        }
                        case "reset":
                            st.ForcedStart = st.ForcedEnd = st.ForcedLead = 0; st.Skipped.Clear(); st.AnnouncedFor = 0; st.Announced.Clear();
                            s_Test = null;
                            Save();
                            sb.Append($"[WorldBoss] {s.Name}: back to the plain schedule (forced, skipped and test rhythm cleared).\n");
                            break;
                        case "testschedule":
                        {
                            int every = Math.Max(2, Arg(1, 10)), lead = Math.Max(1, Arg(2, 2)), win = Math.Max(1, Arg(3, 3));
                            if (lead + win >= every) return "[WorldBoss] testschedule: every must be more than lead + window.";
                            s_Test = new TestRhythm { Every = every * 60, Lead = lead * 60, Window = win * 60, Anchor = Math.Ceiling(now) + lead * 60 + 5 };
                            st.Skipped.Clear();
                            sb.Append($"[WorldBoss] TEST rhythm (memory only, @worldboss reset clears it): every {every} min, warned {lead} min ahead, window {win} min; first rising in {lead} min.\n");
                            break;
                        }
                        case "weeklyreset":
                        {
                            string name = args.Length > 1 ? string.Join(" ", args.Skip(1)) : session?.Player?.Name;
                            var who = name == null ? null : PlayerManager.FindByName(name);
                            if (who == null) return $"[WorldBoss] weeklyreset: no character named {name}.";
                            s_State.Weekly.Remove(who.Guid.Full.ToString("X8"));
                            Save();
                            return $"[WorldBoss] {who.Name}'s weekly bonus is available again.";
                        }
                        default:
                            return "[WorldBoss] unknown verb. spawn [minutes] [window], respawn, skip, reset, testschedule <every> <lead> <window>, weeklyreset [name]";
                    }
                }
            }
            s_Next = 0;   // act on the next world tick
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ text helpers
        static string Coords(WorldBoss.Spec s) =>
            $"{Math.Abs(s.NS).ToString("0.0", CultureInfo.InvariantCulture)}{(s.NS >= 0 ? "N" : "S")} {Math.Abs(s.EW).ToString("0.0", CultureInfo.InvariantCulture)}{(s.EW >= 0 ? "E" : "W")}";

        static string Minutes(double seconds)
        {
            int m = Math.Max(1, (int)Math.Ceiling(seconds / 60 - 0.01));
            return m == 1 ? "1 minute" : $"{m} minutes";
        }

        static string Span(double seconds)
        {
            if (seconds < 3600) return Minutes(seconds);
            int h = (int)(seconds / 3600), m = (int)(seconds % 3600 / 60);
            return m == 0 ? $"{h} h" : $"{h} h {m} min";
        }

        static DateTime Local(double unix) => Utc(unix).AddHours(UTC_OFFSET_HOURS);
        static string LocalShort(double unix) => Local(unix).ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        static string When(double unix) => $"{Local(unix).ToString("dddd 'at' HH:mm", CultureInfo.InvariantCulture)} server time (UTC-5)";
        static string Stamp(double unix) => Utc(unix).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        internal static string WeekKey(DateTime utc)
        {
            var d = utc.AddHours(UTC_OFFSET_HOURS);
            return $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";
        }

        static void World(string text) =>
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(text, ChatMessageType.WorldBroadcast));

        static void Say(Player p, string text) =>
            p?.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

        static void Reply(Session s, string text)
        {
            if (s?.Network == null) { Console.WriteLine(text); return; }
            foreach (var line in text.Split('\n'))
                s.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        // ------------------------------------------------------------------ persistence
        static void Load()
        {
            lock (s_Lock)
            {
                if (s_State != null) return;
                var dir = ModFolder.Sub("WorldBoss");
                s_Path = dir == null ? null : Path.Combine(dir, "schedule.json");
                try
                {
                    if (s_Path != null && File.Exists(s_Path)) s_State = JsonSerializer.Deserialize<State>(File.ReadAllText(s_Path), JSON);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] WorldBossSchedule: cannot read {s_Path}: {e.Message}; starting empty"); }
                s_State ??= new State();
            }
        }

        static void Save()
        {
            if (s_Path == null) return;
            try
            {
                double cut = Now() - 86400;
                foreach (var st in s_State.Bosses.Values) st.Skipped.RemoveAll(x => x < cut);
                var tmp = s_Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_State, JSON));
                File.Move(tmp, s_Path, true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] WorldBossSchedule: cannot write {s_Path}: {e.Message}"); }
        }

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }
    }

    /// <summary>Owed world boss rewards at login.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    static class WorldBossAtLogin
    {
        static void Postfix(Player __instance)
        {
            try { WorldBossSchedule.AtLogin(__instance); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] WorldBossSchedule: login of {__instance?.Name}: {e.Message}"); }
        }
    }
}
