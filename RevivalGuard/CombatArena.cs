using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE DERETHIAN COMBAT ARENA (docs/COMBAT-ARENA.md). Retail's PK arena under Baishi, revived.
    ///
    /// ALMOST ALL OF IT IS ALREADY IN ACE'S WORLD DATA, AS EMOTES. The Arena Master (40772) and the
    /// entrance (44823, "Derethian Combat Arena") stand at Baishi (0xCD41); both arenas are placed
    /// (the Arena, landblock 0x00AB, and the Pit, 0x00AC, retail's September 2011 alternate); every
    /// statue sits behind a linkable generator whose GeneratorEvent is one of the DCA* events; the
    /// entrance's emotes check level 150, the 4 MMD toll (quest DCAAccess), tier 1/2 rare gems and PK
    /// status, strip the Marks you carry, clear your statue timers, give you one Mark and teleport you
    /// into whichever arena's event is on. The Statue of Death takes 10 Marks for a Durable Legendary
    /// Key, 2 MMD and 1,000 luminance and marks you every 5 minutes for a 5% chance at a key; the
    /// Statues of Illuminating Death pay 250 (High 584, Extreme 834) to the one player who holds each.
    /// Marks are Slippery (Bonded -1), so ACE drops every one on death. ACE lists 0x00AB/0x00AC as
    /// no-log landblocks (Player.NoLog_Landblocks): relog there and you wake at your lifestone.
    ///
    /// WHAT WAS MISSING, AND IS HERE:
    ///  1. NOTHING TURNS THE EVENTS ON. Retail's Master rolled the week's arena from his Generation
    ///     emote, which ACE fires only for a generator-spawned object, and he is a plain placement;
    ///     the statue events (DCALum2..5, the bonus DCAExLum1) had a server-side schedule the data
    ///     does not carry. So on this shard the entrance has always said "not open at this time".
    ///     The tick below asserts the schedule every 10 s from the world loop (SeasonalCalendar's
    ///     way; EventManager state is in memory only, so a restart re-derives it within seconds).
    ///  2. RE-ENTRY PAYS FOR THE MARKS IT STRIPS (wiki: "any marks you have on you will be stripped and
    ///     you'll be given 2 MMD for each one"); ACE's PreTeleport takes them for nothing. A player
    ///     whose pack cannot take the notes is stopped at the entrance instead of losing them.
    ///  3. THE MASTER TAKES ALL YOUR MARKS AT ONCE (wiki item interactions: "You hand over all of your
    ///     Marks of a Kill."; ACE's TakeMarkLoop takes one per give), 2 MMD each, and counts them
    ///     toward the four titles.
    ///  4. A LUMINANCE HINT (ours): a statue's award is silently nothing to a character who has not
    ///     done Nalicana's Test (MaximumLuminance unset), which reads as broken.
    ///
    /// Switch `rg_combat_arena`, default OFF: with it off every hook returns at once and no event is
    /// touched (ACE's own behaviour, the arena closed). Turning it off after it was on stops every
    /// DCA/DCP event, which despawns the statues and closes the entrance.
    /// </summary>
    public static class CombatArena
    {
        public const string P_ON = "rg_combat_arena";
        public const string P_BONUS = "rg_arena_bonus_chance";

        internal const uint MASTER = 40772, ENTRANCE = 44823, MARK = 80101, MMD = 20630;
        const int PAY_PER_MARK = 2;     // MMD notes per Mark, the Master's own rate ("2 MMDs each")

        static readonly HashSet<uint> LUM_STATUES = new HashSet<uint> { 80103, 80104, 80105, 80135, 80136, 80137, 80138, 80139 };

        internal const string Q_TURNED_IN = "DCAMarksTurnedIn";
        static readonly string[] Q_ALL = { "DCAAccess", "DCADeathStatueTimeout", "DCADeathStatueCooldown",
            "DCALumStatueTimeout", "DCALumStatueCooldown", "DCALumStatueBitfield", Q_TURNED_IN };

        /// <summary>Retail's titles for Marks handed in, from the Master's and the Statue's own
        /// TitleRewards sets (AddCharacterTitle 677..680 at 100/200/300/400).</summary>
        static readonly (int at, uint title, string name)[] TITLES =
        {
            (100, 677, "Player Slayer"), (200, 678, "Bathed in Blood"), (300, 679, "I Am Darktide"), (400, 680, "Darkness in the Light"),
        };

        // ------------------------------------------------------------------ the schedule

        const string EV_DCA = "DCAActive", EV_DCP = "DCPActive", EV_ALWAYS = "DCALum1Active", EV_BONUS = "DCAExLum1Active";
        static readonly string[] EV_NIGHT = { "DCALum2Active", "DCALum3Active" };
        static readonly string[] EV_WEEKEND = { "DCALum4Active", "DCALum5Active" };
        static readonly string[] EV_HILUM = { "DCAHiLum1Active", "DCAHiLum2Active" };
        static readonly string[] EV_ALL = new[] { EV_DCA, EV_DCP, EV_ALWAYS, EV_BONUS }.Concat(EV_NIGHT).Concat(EV_WEEKEND).Concat(EV_HILUM).ToArray();

        /// <summary>The wiki gives the statue hours in EST ("3pm est to 3am est"); like SeasonalCalendar,
        /// a fixed UTC-5 reproduces that without drifting an hour twice a year.</summary>
        const int UTC_OFFSET_HOURS = -5;
        const int NIGHT_FROM = 15, NIGHT_TO = 3;
        const double TICK = 10.0;

        internal enum Force { Auto, On, Off }
        static Force s_Night, s_Weekend, s_Bonus, s_HiLum;
        static string s_Pit;            // null = the calendar; else EV_DCA or EV_DCP
        static bool s_Managing;         // the schedule has been asserted since the switch went on
        static double s_Next;
        static readonly object s_Lock = new object();
        static bool s_Registered;

        public static bool On => PropertyManager.GetBool(P_ON, false).Item;
        static double BonusChance => Math.Clamp(PropertyManager.GetDouble(P_BONUS, 0.25).Item, 0, 1);

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "RevivalGuard CombatArena: open retail's Derethian Combat Arena at Baishi and run its statue schedule");
                var d = Inner(DefaultPropertyManager.DefaultDoubleProperties);
                if (!d.ContainsKey(P_BONUS)) d[P_BONUS] = new Property<double>(0.25, "RevivalGuard CombatArena: chance per night hour that the bonus Statue of Extreme Illumination (834 luminance) stands");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CombatArena: properties not registered ({e.Message}); read with defaults"); }
            CommandManager.TryAddCommand(Command, "arena", AccessLevel.Admin, CommandHandlerFlag.None,
                "The Derethian Combat Arena: schedule, statues and test verbs (RevivalGuard).",
                "[pit dca|dcp|auto] [night|weekend|bonus|hilum on|off|auto] [plan <date> <time>] [marks <n>] [turnins [n]] [reset] [tick]");
            Mod.Log.Info($"[RevivalGuard] CombatArena: {(On ? "ON" : "off")} ({P_ON}), @arena");
        }

        internal sealed class Plan
        {
            public DateTime Local;
            public bool Night, Weekend, Bonus, HiLum;
            public string Pit;
            public readonly HashSet<string> Want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Which events should be on at this moment. The wiki: one statue always, two more at
        /// night (3 pm to 3 am EST), two more on weekends (its examples put Friday 1 pm in the weekend
        /// and Thursday 10 pm out, so a day runs 3 am to 3 am and Friday..Sunday count), a bonus 834
        /// statue that "has a chance to spawn whenever the nighttime statues are active" (rolled once
        /// an hour, seeded by the hour so a restart keeps the same answer), and "some weeks the
        /// current arena will be used and other weeks the new arena" (ISO week parity: even weeks the
        /// Arena, odd weeks the Pit). The two Statues of High Illumination (584) have no rule in any
        /// source, so they stand only when an admin forces them.</summary>
        internal static Plan Compute(DateTime utc)
        {
            var pl = new Plan { Local = utc.AddHours(UTC_OFFSET_HOURS) };
            var day = pl.Local.AddHours(-NIGHT_TO);   // the arena's day starts at 3 am
            pl.Night = Apply(s_Night, pl.Local.Hour >= NIGHT_FROM || pl.Local.Hour < NIGHT_TO);
            pl.Weekend = Apply(s_Weekend, day.DayOfWeek == DayOfWeek.Friday || day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday);
            pl.Bonus = Apply(s_Bonus, pl.Night && Roll(pl.Local));
            pl.HiLum = Apply(s_HiLum, false);
            pl.Pit = s_Pit ?? (ISOWeek.GetWeekOfYear(day) % 2 == 0 ? EV_DCA : EV_DCP);

            pl.Want.Add(pl.Pit);
            pl.Want.Add(EV_ALWAYS);
            if (pl.Night) foreach (var e in EV_NIGHT) pl.Want.Add(e);
            if (pl.Weekend) foreach (var e in EV_WEEKEND) pl.Want.Add(e);
            if (pl.Bonus) pl.Want.Add(EV_BONUS);
            if (pl.HiLum) foreach (var e in EV_HILUM) pl.Want.Add(e);
            return pl;
        }

        static bool Apply(Force f, bool auto) => f == Force.On || (f == Force.Auto && auto);

        static bool Roll(DateTime local)
        {
            int seed = local.Year * 1000000 + local.Month * 10000 + local.Day * 100 + local.Hour;
            return new Random(seed).NextDouble() < BonusChance;
        }

        static bool IsOn(string ev) => EventManager.GetEventStatus(ev) == GameEventState.On;

        /// <summary>Bring the event table to the plan. Returns the changes made.</summary>
        internal static List<string> Assert()
        {
            var changes = new List<string>();
            lock (s_Lock)
            {
                if (!On)
                {
                    if (!s_Managing) return changes;
                    foreach (var ev in EV_ALL)
                        if (IsOn(ev) && EventManager.StopEvent(ev, null, null)) changes.Add("-" + ev);
                    s_Managing = false;
                    if (changes.Count > 0) Mod.Log.Info($"[RevivalGuard] CombatArena: switched off, closed {string.Join(" ", changes)}");
                    return changes;
                }
                var pl = Compute(DateTime.UtcNow);
                foreach (var ev in EV_ALL)
                {
                    bool want = pl.Want.Contains(ev), cur = IsOn(ev);
                    if (want && !cur && EventManager.StartEvent(ev, null, null)) changes.Add("+" + ev);
                    else if (!want && cur && EventManager.StopEvent(ev, null, null)) changes.Add("-" + ev);
                }
                s_Managing = true;
                if (changes.Count > 0)
                    Mod.Log.Info($"[RevivalGuard] CombatArena: {pl.Local:ddd HH:mm} UTC-5, {(pl.Pit == EV_DCA ? "Arena" : "Pit")} week, night {pl.Night}, weekend {pl.Weekend}, bonus {pl.Bonus}: {string.Join(" ", changes)}");
            }
            return changes;
        }

        internal static void Tick()
        {
            var now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + TICK;
            if (!On && !s_Managing) return;
            try { Assert(); }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] CombatArena tick: {e.Message}"); }
        }

        // ------------------------------------------------------------------ marks

        /// <summary>At the entrance, before its emotes run: a player carrying Marks must have room for
        /// the notes the strip pays, or they would be taken for nothing. False stops the use.</summary>
        internal static bool EntranceGate(EmoteManager em, Creature activator)
        {
            if (!(activator is Player p) || p.Session == null) return true;
            if (em?.WorldObject == null || em.WorldObject.WeenieClassId != ENTRANCE || !On) return true;
            int n = p.GetNumInventoryItemsOfWCID(MARK);
            if (n <= 0 || CanReceivePay(p, n)) return true;
            Say(p, $"The portal will take your {Marks(n)} and pay you {n * PAY_PER_MARK} Trade Notes (250,000) for them, but your pack has no room. Make room and try again.");
            return false;
        }

        static bool CanReceivePay(Player p, int marks)
        {
            try
            {
                var r = new ItemsToReceive(p);
                r.Remove(MARK, marks);
                r.Add(MMD, marks * PAY_PER_MARK);
                return !r.PlayerExceedsLimits;
            }
            catch (Exception) { return true; }   // cannot tell: GiveFromEmote still refuses a gift that does not fit
        }

        /// <summary>After the entrance's TakeItems: pay for what it took.</summary>
        internal static void PayStrip(Player p, WorldObject entrance, int taken)
        {
            if (taken <= 0) return;
            p.GiveFromEmote(entrance, MMD, taken * PAY_PER_MARK);
            Mod.Log.Info($"[RevivalGuard] CombatArena: {p.Name} re-entered carrying {Marks(taken)}: stripped, paid {taken * PAY_PER_MARK} MMD");
        }

        /// <summary>A Mark given to the Arena Master: all of them go, 2 MMD each, and they count toward
        /// the titles. False means handled (the original give does not run).</summary>
        internal static bool MasterTurnIn(Player p, WorldObject target, WorldObject item)
        {
            if (p?.Session == null || target == null || item == null) return true;
            if (target.WeenieClassId != MASTER || item.WeenieClassId != MARK || !On) return true;
            if (target.EmoteManager.IsBusy) return true;   // ACE's own "too busy" refusal
            int n = p.GetNumInventoryItemsOfWCID(MARK);
            if (n <= 0) return true;

            // Retail's line for an item shown, not taken, and the event that keeps the client's drag
            // honest (the same pair ACE sends for a Refuse emote); the take below removes the Marks.
            p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You allow {target.Name} to examine your {item.NameWithMaterial}.", ChatMessageType.Broadcast));
            p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, item.Guid.Full, WeenieError.TradeAiRefuseEmote));

            if (!CanReceivePay(p, n))
            {
                Say(p, $"{target.Name} would pay you {n * PAY_PER_MARK} Trade Notes (250,000) for your {Marks(n)}, but your pack has no room.");
                return false;
            }
            if (!p.TryConsumeFromInventoryWithNetworking(MARK, n))
            {
                Mod.Log.Warn($"[RevivalGuard] CombatArena: could not take {n} Marks from {p.Name}");
                return false;
            }
            Say(p, "You hand over all of your Marks of a Kill.");
            p.GiveFromEmote(target, MMD, n * PAY_PER_MARK);
            int total = AddTurnIns(p, n);
            Mod.Log.Info($"[RevivalGuard] CombatArena: {p.Name} handed the Master {Marks(n)} for {n * PAY_PER_MARK} MMD ({total} turned in)");
            return false;
        }

        /// <summary>Count Marks toward the titles, awarding each threshold crossed.</summary>
        internal static int AddTurnIns(Player p, int n)
        {
            int before = p.QuestManager.GetCurrentSolves(Q_TURNED_IN);
            p.QuestManager.Increment(Q_TURNED_IN, n);
            int after = p.QuestManager.GetCurrentSolves(Q_TURNED_IN);
            foreach (var (at, title, name) in TITLES)
                if (before < at && after >= at)
                {
                    p.AddTitle(title);
                    Say(p, $"You have been awarded the title of \"{name}\"");
                }
            return after;
        }

        /// <summary>THE ENTRANCE'S "Nullify All Rares" (5582) CANNOT BE RESISTED. ACE casts an emote's
        /// CastSpellInstant with the emoter as caster, and the entrance is a creature with no magic
        /// skill, so every player resisted it (staging, 2026-09-30: "You resist the spell cast by
        /// Derethian Combat Arena") and walked in with their rare-gem buffs, the one thing the rare
        /// check in front of it exists to stop. Cast without the resist roll. False = handled.</summary>
        internal static bool EntranceSpell(WorldObject entrance, PropertiesEmoteAction emote, WorldObject target, ref float result)
        {
            try
            {
                var spell = new Spell((uint)(emote.SpellId ?? 0));
                if (spell.NotFound) return true;
                entrance.TryCastSpell_WithRedirects(spell, target, entrance, null, false, false, false);
                result = 0f;
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] CombatArena: entrance spell: {e.Message}"); return true; }
        }

        // ------------------------------------------------------------------ luminance hint

        static readonly ConditionalWeakTable<Player, object> s_Hinted = new ConditionalWeakTable<Player, object>();

        /// <summary>Ours: a statue's luminance is silently nothing to a character who cannot earn
        /// luminance yet, or whose luminance is full. Say so, once a minute at most.</summary>
        internal static void LumHint(Player p, WorldObject statue)
        {
            if (p?.Session == null || statue == null || !LUM_STATUES.Contains(statue.WeenieClassId) || !On) return;
            long max = p.MaximumLuminance ?? 0, have = p.AvailableLuminance ?? 0;
            string line = max <= 0 ? "You cannot earn luminance yet, so the statue's reward is lost on you. Nalicana's Test opens luminance."
                : have >= max ? "Your luminance is full, so the statue's reward is lost on you." : null;
            if (line == null) return;
            var now = ACE.Common.Time.GetUnixTime();
            if (s_Hinted.TryGetValue(p, out var o) && o is double last && now - last < 60) return;
            s_Hinted.AddOrUpdate(p, now);
            Say(p, line);
        }

        // ------------------------------------------------------------------ @arena

        static void Command(Session session, params string[] args)
        {
            var p = session?.Player;
            var a = (args ?? Array.Empty<string>()).Select(s => s.ToLowerInvariant()).ToArray();
            string reply;
            try { reply = Run(p, a); }
            catch (Exception e) { reply = $"[Arena] {e.GetType().Name}: {e.Message}"; }
            foreach (var line in reply.Split('\n'))
                if (session != null) session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
                else Console.WriteLine(line);
        }

        static string Run(Player p, string[] a)
        {
            string verb = a.Length > 0 ? a[0] : "";
            switch (verb)
            {
                case "":
                case "status":
                    return Status();
                case "plan":
                    {
                        // @arena plan 2026-10-02 13:00: the schedule at a UTC-5 wall time (forces apply)
                        if (a.Length < 3 || !DateTime.TryParse(a[1] + " " + a[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                            return "[Arena] @arena plan <yyyy-mm-dd> <hh:mm> (UTC-5)";
                        var pl = Compute(DateTime.SpecifyKind(local.AddHours(-UTC_OFFSET_HOURS), DateTimeKind.Utc));
                        int statues = pl.Want.Count(e => e.StartsWith("DCALum") || e.StartsWith("DCAHiLum") || e == EV_BONUS);
                        return $"[Arena] {pl.Local:ddd yyyy-MM-dd HH:mm} UTC-5: {(pl.Pit == EV_DCA ? "the Arena" : "the Pit")}, night {pl.Night}, weekend {pl.Weekend}, bonus {pl.Bonus}; " +
                               $"{statues} luminance event(s): {string.Join(" ", pl.Want.Where(e => e != pl.Pit))}";
                    }
                case "tick":
                    { var ch = Assert(); return ch.Count == 0 ? "[Arena] schedule asserted: no change" : "[Arena] " + string.Join(" ", ch); }
                case "pit":
                    if (a.Length < 2) return "[Arena] @arena pit dca|dcp|auto";
                    s_Pit = a[1] == "dca" || a[1] == "arena" ? EV_DCA : a[1] == "dcp" ? EV_DCP : null;
                    Assert();
                    return $"[Arena] pit forced to {(s_Pit == null ? "the calendar" : s_Pit)}\n" + Status();
                case "night":
                case "weekend":
                case "bonus":
                case "hilum":
                    {
                        if (a.Length < 2) return $"[Arena] @arena {verb} on|off|auto";
                        var f = a[1] == "on" ? Force.On : a[1] == "off" ? Force.Off : Force.Auto;
                        if (verb == "night") s_Night = f; else if (verb == "weekend") s_Weekend = f; else if (verb == "bonus") s_Bonus = f; else s_HiLum = f;
                        Assert();
                        return $"[Arena] {verb} forced {f}\n" + Status();
                    }
                case "marks":
                    {
                        if (p == null) return "[Arena] in game only";
                        int n = a.Length > 1 && int.TryParse(a[1], out var v) ? Math.Clamp(v, 1, 1000) : 1;
                        p.GiveFromEmote(p, MARK, n);
                        return $"[Arena] gave you {Marks(n)} (you carry {p.GetNumInventoryItemsOfWCID(MARK)})";
                    }
                case "turnins":
                    {
                        if (p == null) return "[Arena] in game only";
                        if (a.Length < 2 || !int.TryParse(a[1], out var v))
                            return $"[Arena] {Q_TURNED_IN} is {p.QuestManager.GetCurrentSolves(Q_TURNED_IN)}; @arena turnins <n> sets it";
                        p.QuestManager.SetQuestCompletions(Q_TURNED_IN, Math.Max(0, v));
                        return $"[Arena] {Q_TURNED_IN} set to {p.QuestManager.GetCurrentSolves(Q_TURNED_IN)}";
                    }
                case "reset":
                    {
                        if (p == null) return "[Arena] in game only";
                        foreach (var q in Q_ALL) p.QuestManager.Erase(q);
                        int n = p.GetNumInventoryItemsOfWCID(MARK);
                        if (n > 0) p.TryConsumeFromInventoryWithNetworking(MARK, n);
                        return $"[Arena] your arena quests are erased and {Marks(n)} removed";
                    }
                default:
                    return "[Arena] @arena [status] | tick | pit dca|dcp|auto | night|weekend|bonus|hilum on|off|auto | plan <yyyy-mm-dd> <hh:mm> | marks <n> | turnins [n] | reset";
            }
        }

        static string Status()
        {
            var pl = Compute(DateTime.UtcNow);
            var sb = new StringBuilder();
            sb.Append($"[Arena] {P_ON} {(On ? "ON" : "off")}; {pl.Local:ddd HH:mm} UTC-5; this week: {(pl.Pit == EV_DCA ? "the Arena (0x00AB)" : "the Pit (0x00AC)")}{(s_Pit != null ? " (forced)" : "")}\n");
            sb.Append($"[Arena] night {pl.Night}{Tag(s_Night)}, weekend {pl.Weekend}{Tag(s_Weekend)}, bonus {pl.Bonus}{Tag(s_Bonus)} (chance {BonusChance:0.##}/hour), high {pl.HiLum}{Tag(s_HiLum)}\n");
            sb.Append("[Arena] events: " + string.Join(", ", EV_ALL.Select(e => $"{e} {(IsOn(e) ? "ON" : "off")}{(On && pl.Want.Contains(e) != IsOn(e) ? "*" : "")}")));
            return sb.ToString();
        }

        static string Tag(Force f) => f == Force.Auto ? "" : $" (forced {f})";

        static string Marks(int n) => n == 1 ? "1 Mark of a Kill" : $"{n} Marks of a Kill";

        static void Say(Player p, string line) =>
            p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        static IDictionary<string, Property<T>> Inner<T>(ReadOnlyDictionary<string, Property<T>> ro)
        {
            var inner = typeof(ReadOnlyDictionary<string, Property<T>>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(ro)).OfType<IDictionary<string, Property<T>>>().FirstOrDefault();
            return inner ?? throw new MissingFieldException("ReadOnlyDictionary", "inner dictionary");
        }
    }

    /// <summary>The schedule, from the world loop. Its Prepare registers the feature, so Mod.cs needs
    /// no line for it.</summary>
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class CombatArenaTick
    {
        static bool Prepare() { CombatArena.Register(); return true; }
        static void Postfix() => CombatArena.Tick();
    }

    /// <summary>The entrance's use: room for the strip's payment first.</summary>
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.OnUse))]
    static class CombatArenaEntranceGate
    {
        static bool Prefix(EmoteManager __instance, Creature activator) => CombatArena.EntranceGate(__instance, activator);
    }

    /// <summary>Two emote actions: the entrance's TakeItems of Marks (paid for after), and a
    /// statue's AwardLuminance (the hint). Every other action passes at the cost of two compares.</summary>
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.ExecuteEmote))]
    static class CombatArenaEmote
    {
        static bool Prefix(EmoteManager __instance, PropertiesEmoteAction emote, WorldObject targetObject, out int __state, ref float __result)
        {
            __state = -1;
            if (emote == null) return true;
            if (emote.Type == (uint)EmoteType.TakeItems)
            {
                if (emote.WeenieClassId == CombatArena.MARK && targetObject is Player p
                    && __instance.WorldObject?.WeenieClassId == CombatArena.ENTRANCE && CombatArena.On)
                    __state = p.GetNumInventoryItemsOfWCID(CombatArena.MARK);
                return true;
            }
            if (emote.Type == (uint)EmoteType.CastSpellInstant && __instance.WorldObject?.WeenieClassId == CombatArena.ENTRANCE
                && targetObject is Player && CombatArena.On)
                return CombatArena.EntranceSpell(__instance.WorldObject, emote, targetObject, ref __result);
            return true;
        }

        static void Postfix(EmoteManager __instance, PropertiesEmoteAction emote, WorldObject targetObject, int __state)
        {
            if (emote == null || !(targetObject is Player p)) return;
            try
            {
                if (__state > 0) CombatArena.PayStrip(p, __instance.WorldObject, __state - p.GetNumInventoryItemsOfWCID(CombatArena.MARK));
                else if (emote.Type == (uint)EmoteType.AwardLuminance) CombatArena.LumHint(p, __instance.WorldObject);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] CombatArena emote: {e.Message}"); }
        }
    }

    /// <summary>Marks given to the Arena Master.</summary>
    [HarmonyPatch(typeof(Player), "GiveObjectToNPC")]
    static class CombatArenaMasterTurnIn
    {
        static bool Prefix(Player __instance, WorldObject target, WorldObject item) => CombatArena.MasterTurnIn(__instance, target, item);
    }
}
