using System.Text;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE SHADOW SHRINES: RETAIL'S LAND CONTROL, SMALLER (docs/PK-EXPANSION.md, idea 1). Ours, on
    /// retail's pattern. Off until `pk_shrines_enabled` is true, and inert without its world rows
    /// (tools/gpubox-ace/pk-shrines.sql places three shrines, weenies 900900-900902, in the Altar of
    /// Bael'Zharon's own model).
    ///
    /// Retail (wiki "Land Control", Ancient Powers 2008): player killers of a society took a keep by
    /// holding its banners, and the holders got a boon; the designers wanted "fewer battles that are
    /// more epic", on a schedule people could plan around. Turbine's own 2006 diagnosis of PvP was
    /// that "PvP players want something meaningful to fight over". So:
    ///
    ///   * A player killer uses a shrine and must stay within ATTUNE_RANGE of it for
    ///     pk_shrine_attune_seconds (60) while no ENEMY player killer (another allegiance, another
    ///     account) stands within CONTEST_RANGE. An enemy near freezes the count; the attuner dying,
    ///     leaving or teleporting breaks it. Whoever holds the shrine is told the moment someone
    ///     starts, so defenders can ride out.
    ///   * Taken, the shrine is held by the taker's allegiance (the monarch; a lone player holds it
    ///     for themselves) for pk_shrine_hold_hours (12) unless renewed the same way, and cannot be
    ///     taken again for pk_shrine_lock_minutes (30). A world line names the new holder.
    ///   * THE BOON is small and not combat power: pk_shrine_xp_bonus (5%) more experience from
    ///     creature kills per shrine held, to every member of the holding allegiance, PK or not.
    ///   * pk_shrine_hours (UTC, e.g. "18-23") limits when shrines can be taken; empty = always.
    /// Non-PKs and newcomers are never involved: only a PK can attune, and the shrines stand far from
    /// towns and starter areas.
    ///
    ///     @shrines        who holds what (our client: the PK board's Shrines tab)
    ///
    /// State: PkLedger/shrines.json beside the dll.
    /// </summary>
    internal static class PkShrines
    {
        internal const string P_ATTUNE = "pk_shrine_attune_seconds", P_BONUS = "pk_shrine_xp_bonus",
            P_HOLD_HOURS = "pk_shrine_hold_hours", P_LOCK_MINUTES = "pk_shrine_lock_minutes", P_HOURS = "pk_shrine_hours";
        const float ATTUNE_RANGE = 15f, CONTEST_RANGE = 35f, NEWS_RANGE = 300f;
        const double TICK = 2.0, CONTEST_NOTE = 20.0;

        /// <summary>The shrines: weenie class, name, and where (for the board and the world lines).
        /// Must match tools/gpubox-ace/pk-shrines.sql.</summary>
        internal static readonly (uint wcid, string name, string place)[] SHRINES =
        {
            (900900, "Shadow Shrine of the Empyrean Ruin", "the Empyrean ruin, 81.4N 32.9W"),
            (900901, "Shadow Shrine of Zombie Castle", "Zombie Castle, 45.2S 39.1E"),
            (900902, "Shadow Shrine of the Tumerok Fort", "the Tumerok fort, 50.7S 81.8W"),
        };

        internal sealed class Hold
        {
            public uint Side { get; set; }
            public string Label { get; set; }
            public string By { get; set; }
            public long Taken { get; set; }
            public long Expires { get; set; }
            public long LockedUntil { get; set; }
        }

        sealed class Attempt
        {
            public uint Wcid, Player, Side, Account; public string Name, Label, Of;
            public ACE.Entity.Position At;
            public double Progress, LastNote; public bool HalfSaid, Contested;
            public WorldObject Shrine;   // for the effects (2026-09-29, owner: "looks awesome but does nothing so far")
        }

        static readonly object s_Lock = new object();
        static Dictionary<uint, Hold> s_Held = new Dictionary<uint, Hold>();
        static readonly Dictionary<uint, Attempt> s_Attempts = new Dictionary<uint, Attempt>();
        static string s_Path;
        static double s_Next;

        internal static bool On => PkCommon.On(PkCommon.P_SHRINES);

        static (uint wcid, string name, string place) Spec(uint wcid) => SHRINES.FirstOrDefault(s => s.wcid == wcid);
        static bool IsShrine(uint wcid) => SHRINES.Any(s => s.wcid == wcid);

        internal static void Register()
        {
            Load();
            CommandManager.TryAddCommand(Command, "shrines", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Who holds the Shadow Shrines.", "");
        }

        // ------------------------------------------------------------------ using a shrine

        /// <summary>A use of a shrine: walk to it (ACE's own move-to), then attune. With the feature
        /// off ACE handles the use as it would any Generic, which does nothing.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseItem))]
        static class Use
        {
            static bool Prefix(Player __instance, uint itemGuid)
            {
                try
                {
                    if (!On) return true;
                    var wo = __instance.FindObject(itemGuid, Player.SearchLocations.Landblock, out _, out _, out _);
                    if (wo == null || !IsShrine(wo.WeenieClassId)) return true;
                    __instance.CreateMoveToChain(wo, ok =>
                    {
                        try { if (ok) Attune(__instance, wo); }
                        catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkShrines: use by {__instance.Name}: {e}"); }
                        __instance.SendUseDoneEvent();
                    });
                    return false;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkShrines: use: {e}"); return true; }
            }
        }

        static void Attune(Player p, WorldObject shrine)
        {
            var spec = Spec(shrine.WeenieClassId);
            long now = PkCommon.Now();
            if (!p.IsPK) { PkCommon.Say(p, "The shrine does not answer you. Only a player killer may attune it."); return; }
            if (!WindowOpen(out string opens)) { PkCommon.Say(p, $"The shrine sleeps. It wakes at {opens} UTC."); return; }
            uint side = PkCommon.Side(p);
            string label = PkCommon.SideLabel(p);
            Hold held;
            lock (s_Lock)
            {
                s_Held.TryGetValue(spec.wcid, out held);
                if (s_Attempts.TryGetValue(spec.wcid, out var a))
                {
                    PkCommon.Say(p, a.Side == side ? $"{a.Name} of your allegiance is already attuning the shrine." : $"{a.Name} is attuning the shrine. Drive them off first.");
                    return;
                }
                if (held != null && held.Side != side && held.LockedUntil > now)
                {
                    PkCommon.Say(p, $"The shrine is still bound to {held.Label} for {Math.Max(1, (held.LockedUntil - now + 59) / 60)} more minutes.");
                    return;
                }
                s_Attempts[spec.wcid] = new Attempt
                {
                    Wcid = spec.wcid, Player = p.Guid.Full, Side = side, Account = p.Account?.AccountId ?? 0, Name = p.Name, Label = label, Of = PkCommon.Of(p),
                    At = new ACE.Entity.Position(shrine.Location), LastNote = ACE.Common.Time.GetUnixTime(), Shrine = shrine,
                };
            }
            long secs = PkCommon.Long(P_ATTUNE, 60);
            bool renew = held != null && held.Side == side;
            PkCommon.Say(p, renew
                ? $"You renew your allegiance's hold on the {spec.name}. Stay within {ATTUNE_RANGE:0} metres for {secs} seconds with no enemy near."
                : $"You lay your hands on the {spec.name}. Stay within {ATTUNE_RANGE:0} metres for {secs} seconds with no enemy near, and it is yours.");
            Fx(shrine, PlayScript.EnchantUpPurple);
            Fx(p, PlayScript.SkillDownVoid);
            Near(spec.wcid, $"{p.Name}{PkCommon.Of(p)} is attuning the {spec.name}.", p);
            if (held != null && !renew)
                foreach (var m in Members(held.Side))
                    PkCommon.Say(m, $"{p.Name}{PkCommon.Of(p)} is attuning your allegiance's {spec.name} at {spec.place}!", ChatMessageType.Allegiance);
            Mod.Log.Info($"[RevivalGuard] PkShrines: {p.Name} ({label}) began attuning {spec.name}{(held != null ? $", held by {held.Label}" : "")}");
        }

        // ------------------------------------------------------------------ the count

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        static class Tick
        {
            static void Postfix()
            {
                var now = ACE.Common.Time.GetUnixTime();
                if (now < s_Next) return;
                s_Next = now + TICK;
                if (!On) { lock (s_Lock) s_Attempts.Clear(); return; }
                try { Step(now); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkShrines tick: {e.Message}"); }
            }
        }

        static void Step(double now)
        {
            List<Attempt> attempts;
            lock (s_Lock) attempts = s_Attempts.Values.ToList();
            List<Player> online = attempts.Count > 0 ? PlayerManager.GetAllOnline() : null;
            foreach (var a in attempts)
            {
                var spec = Spec(a.Wcid);
                var p = PlayerManager.GetOnlinePlayer(a.Player);
                string broken = null;
                if (p == null) broken = "gone";
                else if (p.IsDead) broken = "fell";
                else if (p.Teleporting || p.Location == null || !p.IsPK) broken = "left";
                else if (Dist(p.Location, a.At) > ATTUNE_RANGE) broken = "left";
                if (broken != null)
                {
                    lock (s_Lock) s_Attempts.Remove(a.Wcid);
                    if (p != null) PkCommon.Say(p, broken == "fell" ? $"You fell, and the {spec.name} forgets you." : $"You left the {spec.name}, and the attunement is broken.");
                    Near(a.Wcid, $"{a.Name}'s attunement of the {spec.name} is broken.", p);
                    continue;
                }
                var enemy = online.FirstOrDefault(o => o != p && o.IsPK && !o.IsDead && o.Location != null && o.CloakStatus != CloakStatus.On
                    && PkCommon.Side(o) != a.Side && (o.Account?.AccountId ?? 0) != a.Account && Dist(o.Location, a.At) <= CONTEST_RANGE);
                if (enemy != null)
                {
                    if (!a.Contested || now - a.LastNote >= CONTEST_NOTE)
                    {
                        a.LastNote = now;
                        PkCommon.Say(p, $"{enemy.Name} contests the {spec.name}. The shrine will not answer while an enemy stands near.");
                        PkCommon.Say(enemy, $"You contest {p.Name}'s claim on the {spec.name}. Hold your ground or drive them off.");
                    }
                    a.Contested = true;
                    Fx(a.Shrine, PlayScript.SkillDownBlack);
                    continue;
                }
                a.Contested = false;
                a.Progress += TICK;
                Fx(a.Shrine, a.Progress % 4 < TICK ? PlayScript.SpecialStatePurple : PlayScript.AttribUpPurple);
                long need = Math.Max(5, PkCommon.Long(P_ATTUNE, 60));
                if (!a.HalfSaid && a.Progress >= need / 2.0) { a.HalfSaid = true; PkCommon.Say(p, $"The {spec.name} stirs. Hold on."); }
                if (a.Progress >= need) Capture(a, p);
            }
            Lapse((long)now);
        }

        static void Capture(Attempt a, Player p)
        {
            var spec = Spec(a.Wcid);
            long now = PkCommon.Now();
            Hold before;
            var hold = new Hold
            {
                Side = a.Side, Label = a.Label, By = a.Name, Taken = now,
                Expires = now + (long)(PkCommon.Dbl(P_HOLD_HOURS, 12) * 3600),
                LockedUntil = now + (long)(PkCommon.Dbl(P_LOCK_MINUTES, 30) * 60),
            };
            int count;
            lock (s_Lock)
            {
                s_Attempts.Remove(a.Wcid);
                s_Held.TryGetValue(a.Wcid, out before);
                s_Held[a.Wcid] = hold;
                count = s_Held.Values.Count(h => h.Side == a.Side);
                Save();
            }
            double bonus = PkCommon.Dbl(P_BONUS, 0.05) * count;
            if (before != null && before.Side == a.Side)
            {
                PkCommon.Say(p, $"Your allegiance's hold on the {spec.name} is renewed for {PkCommon.Dbl(P_HOLD_HOURS, 12):0.#} hours.");
                Mod.Log.Info($"[RevivalGuard] PkShrines: {a.Name} renewed {spec.name} for {a.Label}");
                return;
            }
            Fx(a.Shrine, PlayScript.BlackMadness);
            Fx(p, PlayScript.EnchantUpPurple);
            PkCommon.World($"{a.Name}{a.Of} has taken the {spec.name} at {spec.place}.");
            foreach (var m in Members(a.Side))
                PkCommon.Say(m, $"Your {(m.Allegiance != null ? "allegiance holds" : "hold is")} {count} Shadow {(count == 1 ? "Shrine" : "Shrines")}: {bonus * 100:0.#}% more experience from every creature you kill, for {PkCommon.Dbl(P_HOLD_HOURS, 12):0.#} hours. @shrines shows the holds.", m.Allegiance != null ? ChatMessageType.Allegiance : ChatMessageType.Broadcast);
            if (before != null)
                foreach (var m in Members(before.Side))
                    PkCommon.Say(m, $"Your allegiance has lost the {spec.name} to {a.Label}.", ChatMessageType.Allegiance);
            Mod.Log.Info($"[RevivalGuard] PkShrines: {a.Name} took {spec.name} for {a.Label} (0x{a.Side:X8}){(before != null ? $" from {before.Label}" : "")}");
        }

        /// <summary>Holds past their time end, and the holders are told.</summary>
        static void Lapse(long now)
        {
            List<(uint wcid, Hold h)> gone;
            lock (s_Lock)
            {
                gone = s_Held.Where(kv => kv.Value.Expires <= now).Select(kv => (kv.Key, kv.Value)).ToList();
                if (gone.Count == 0) return;
                foreach (var (w, _) in gone) s_Held.Remove(w);
                Save();
            }
            foreach (var (w, h) in gone)
            {
                foreach (var m in Members(h.Side))
                    PkCommon.Say(m, $"Your allegiance's hold on the {Spec(w).name} has lapsed.", ChatMessageType.Allegiance);
                Mod.Log.Info($"[RevivalGuard] PkShrines: {h.Label}'s hold on {Spec(w).name} lapsed");
            }
        }

        // ------------------------------------------------------------------ the boon

        /// <summary>Creature-kill XP for members of an allegiance that holds shrines.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.EarnXP))]
        static class Boon
        {
            static void Prefix(Player __instance, ref long amount, XpType xpType)
            {
                try
                {
                    if (xpType != XpType.Kill || amount <= 0 || !On) return;
                    int n = HeldBy(PkCommon.Side(__instance));
                    if (n == 0) return;
                    long before = amount;
                    amount = (long)Math.Round(amount * (1 + PkCommon.Dbl(P_BONUS, 0.05) * n));
                    // one line per player per ten minutes, so staff can see the boon at work
                    double now = ACE.Common.Time.GetUnixTime();
                    if (!s_BoonLogged.TryGetValue(__instance.Guid.Full, out var at) || now - at > 600)
                    {
                        s_BoonLogged[__instance.Guid.Full] = now;
                        Mod.Log.Info($"[RevivalGuard] PkShrines: shrine boon for {__instance.Name} ({n} held): creature XP {before} -> {amount}");
                    }
                }
                catch { }
            }
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, double> s_BoonLogged = new System.Collections.Concurrent.ConcurrentDictionary<uint, double>();

        internal static int HeldBy(uint side)
        {
            long now = PkCommon.Now();
            lock (s_Lock) return s_Held.Values.Count(h => h.Side == side && h.Expires > now);
        }

        // ------------------------------------------------------------------ helpers

        static float Dist(ACE.Entity.Position a, ACE.Entity.Position b)
        {
            if (a == null || b == null) return float.MaxValue;
            if (a.Indoors != b.Indoors && (a.Cell >> 16) != (b.Cell >> 16)) return float.MaxValue;
            return a.DistanceTo(b);
        }

        static void Near(uint wcid, string line, Player except)
        {
            ACE.Entity.Position at;
            lock (s_Lock) at = s_Attempts.TryGetValue(wcid, out var a) ? a.At : null;
            if (at == null) return;
            foreach (var o in PlayerManager.GetAllOnline())
                if (o != except && o.Location != null && Dist(o.Location, at) <= NEWS_RANGE) PkCommon.Say(o, line);
        }

        /// <summary>Every online member of the allegiance whose monarch is `side` (or that lone player).</summary>
        /// <summary>A client effect on the shrine or a player (retail PlayScripts from the DATs). Never throws.</summary>
        static void Fx(WorldObject wo, PlayScript script)
        {
            try { if (wo?.Location != null && !wo.IsDestroyed) wo.EnqueueBroadcast(new GameMessageScript(wo.Guid, script)); }
            catch { }
        }

        static List<Player> Members(uint side)
        {
            var list = new List<Player>();
            var a = Mansions.AllegianceOfOwner(side, out _);
            if (a != null) list.AddRange(a.OnlinePlayers);
            var o = PlayerManager.GetOnlinePlayer(side);
            if (o != null && !list.Contains(o)) list.Add(o);
            return list;
        }

        /// <summary>Is a shrine takeable now (pk_shrine_hours, UTC ranges like "18-23,2-4")?</summary>
        internal static bool WindowOpen(out string opens)
        {
            opens = null;
            string spec = PkCommon.Str(P_HOURS, "").Trim();
            if (spec.Length == 0) return true;
            int h = DateTime.UtcNow.Hour;
            int? first = null;
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ab = part.Split('-');
                if (ab.Length != 2 || !int.TryParse(ab[0], out int from) || !int.TryParse(ab[1], out int to)) continue;
                bool inside = from <= to ? h >= from && h < to : h >= from || h < to;
                if (inside) return true;
                int wait = (from - h + 24) % 24;
                if (first == null || wait < (first.Value - h + 24) % 24) first = from;
            }
            if (first == null) return true;   // an unreadable setting never locks the shrines
            opens = $"{first.Value:00}:00";
            return false;
        }

        // ------------------------------------------------------------------ the command and the board

        static void Command(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            if (!On) { PkCommon.Say(p, "The Shadow Shrines are silent on this world."); return; }
            if (ModChannel.Capable(session)) { PkBoard.Send(session, true, "shrines"); return; }
            long now = PkCommon.Now();
            PkCommon.Say(p, "--- The Shadow Shrines ---");
            foreach (var s in SHRINES) PkCommon.Say(p, $"{s.name} ({s.place}): {State(s.wcid, now)}");
            int n = HeldBy(PkCommon.Side(p));
            if (n > 0) PkCommon.Say(p, $"Your allegiance holds {n}: {PkCommon.Dbl(P_BONUS, 0.05) * n * 100:0.#}% more experience from creatures.");
            if (!WindowOpen(out var opens)) PkCommon.Say(p, $"The shrines sleep until {opens} UTC.");
        }

        static string State(uint wcid, long now)
        {
            lock (s_Lock)
            {
                string s = s_Held.TryGetValue(wcid, out var h) && h.Expires > now
                    ? $"held by {h.Label} for {Math.Max(1, (h.Expires - now) / 3600)} more hours" : "unclaimed";
                if (s_Attempts.TryGetValue(wcid, out var a)) s += $"; {a.Name} is attuning it{(a.Contested ? " (contested)" : "")}";
                return s;
            }
        }

        internal static void AppendJson(StringBuilder sb, Player asker)
        {
            long now = PkCommon.Now();
            uint side = PkCommon.Side(asker);
            bool open = WindowOpen(out var opens);
            long need = PkCommon.Long(P_ATTUNE, 60);
            sb.Append(",\"shrines\":[");
            int n = 0;
            lock (s_Lock)
                foreach (var s in SHRINES)
                {
                    if (n++ > 0) sb.Append(',');
                    s_Held.TryGetValue(s.wcid, out var h);
                    if (h != null && h.Expires <= now) h = null;
                    s_Attempts.TryGetValue(s.wcid, out var a);
                    sb.Append("{\"name\":").Append(PkCommon.Q(s.name)).Append(",\"where\":").Append(PkCommon.Q(s.place))
                      .Append(",\"holder\":").Append(PkCommon.Q(h?.Label ?? "")).Append(",\"mine\":").Append(h != null && h.Side == side ? 1 : 0)
                      .Append(",\"left\":").Append(h != null ? h.Expires - now : 0).Append(",\"lock\":").Append(h != null ? Math.Max(0, h.LockedUntil - now) : 0)
                      .Append(",\"by\":").Append(PkCommon.Q(a?.Name ?? "")).Append(",\"contested\":").Append(a != null && a.Contested ? 1 : 0)
                      .Append(",\"progress\":").Append(a != null ? (int)a.Progress : 0).Append('}');
                }
            sb.Append("],\"need\":").Append(need).Append(",\"sopen\":").Append(open ? 1 : 0).Append(",\"swakes\":").Append(PkCommon.Q(opens ?? ""))
              .Append(",\"held\":").Append(HeldBy(side))
              .Append(",\"bonus\":").Append(PkCommon.Dbl(P_BONUS, 0.05).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ the file

        static void Load()
        {
            try
            {
                var dir = ModFolder.Sub("PkLedger");
                if (dir == null) return;
                s_Path = Path.Combine(dir, "shrines.json");
                if (File.Exists(s_Path)) s_Held = JsonSerializer.Deserialize<Dictionary<uint, Hold>>(File.ReadAllText(s_Path)) ?? new Dictionary<uint, Hold>();
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkShrines: cannot read {s_Path}: {e.Message}; starting empty"); }
        }

        static void Save()
        {
            if (s_Path == null) return;
            try
            {
                var tmp = s_Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Held));
                File.Move(tmp, s_Path, true);
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkShrines: cannot write {s_Path}: {e.Message}"); }
        }
    }
}
