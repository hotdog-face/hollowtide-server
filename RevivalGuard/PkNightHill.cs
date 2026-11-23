using System.Collections.Concurrent;
using System.Text;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PK NIGHT v2: THE MOVING HILL ON THE GROUND (owner 2026-11-22, docs/proposals/pk-night-v2.md; the rules in
    /// PkNightHillRules.cs). While a round runs:
    ///   * the hill's edge is a ring of torches about every 8 m (pk_night_marker, default the retail Tumerok Torch 22858) with
    ///     a Gout of Elemental Flame (pk_night_marker_centre, 20636: a tall red column seen from far off) in the middle.
    ///     They are ordinary retail objects, so every client draws them, retail too. The Gateway pattern (PkNightGate.cs):
    ///     made by the mod, never saved to the shard (HillNoSave), gone at the end or a move, made again where a landblock
    ///     unload took one, and a Lifespan in case a removal is ever missed. Stuck, ethereal (no one is blocked or hides
    ///     behind them), not attackable or usable.
    ///   * every BOARD_LINE_SECONDS each player within the radius plus BOARD_LINE_NEAR metres of the hill is told who holds it
    ///     (HillLine); the hill's place and that line go in the board JSON too (AppendHillJson).
    /// </summary>
    public static partial class PkNight
    {
        internal const string P_MARKER = "pk_night_marker", P_MARKER_CENTRE = "pk_night_marker_centre";
        internal const uint MARKER_WCID = 22858, MARKER_CENTRE_WCID = 20636;   // Tumerok Torch; Gout of Elemental Flame
        const float MARKER_SCALE = 1.6f;   // a torch is a slim 2 m pole; half again reads from across the field
        const double MARKER_REMAKE_S = 30;

        sealed class Marker { public volatile WorldObject Wo; public double Ns, Ew, MadeAt; public bool Centre; public volatile bool Failed; public int Remakes; }

        static readonly List<Marker> s_Markers = new List<Marker>();
        static readonly ConcurrentDictionary<uint, byte> s_MarkerGuids = new ConcurrentDictionary<uint, byte>();
        static string s_MarkKey;           // spot/point/radius the markers stand for (null: none)
        static double s_LineAt;
        static int s_MarkersMade;

        /// <summary>Every tick of a running round: the hill's notes, its markers, and the battlefield line.</summary>
        static void HillStep(Live live, Spot spot, ACE.Entity.Position centre, double now)
        {
            (HillNote note, int step)? due;
            lock (s_Lock) { due = HillDue(live, spot, now); if (due != null) Save(); }
            if (due?.note == HillNote.Warn) World(HillWarnLine(spot, due.Value.step));
            else if (due?.note == HillNote.Move) World(HillMoveLine(spot, due.Value.step));
            try { EnsureMarkers(live, spot, now); }
            catch (Exception e) { TickGuard.Report("PkNight.Markers", e); }
            if (now >= s_LineAt)
            {
                s_LineAt = now + BOARD_LINE_SECONDS;
                try { SendHillLines(live, spot, centre, now); }
                catch (Exception e) { TickGuard.Report("PkNight.HillLine", e); }
            }
        }

        /// <summary>The side a player near the hill reads the line as: 0 for one who cannot hold it (not a player killer, a
        /// Hollow Company mercenary, or benched).</summary>
        static uint ReaderSide(Player p, Live live)
        {
            if (p == null || !p.IsPK || Mercenaries.IsHollowCompany(p)) return 0;
            uint side = NightSide(p, live, live.ByFellowship).Side;
            lock (s_Lock) if (live.ByFellowship && (live.Benched.Contains(p.Guid.Full) || (live.Locked.TryGetValue(p.Guid.Full, out var k) && k != side))) return 0;
            return side;
        }

        static void SendHillLines(Live live, Spot spot, ACE.Entity.Position centre, double now)
        {
            double near = Radius + BOARD_LINE_NEAR;
            int n = 0;
            foreach (var p in PlayerManager.GetAllOnline())
            {
                if (p?.Location == null || p.Teleporting || Mercenaries.IsHollowCompany(p) || Dist(p.Location, centre) > near) continue;
                uint side = ReaderSide(p, live);   // outside s_Lock: NightSide takes s_KeyLock, which takes s_Lock
                string line;
                lock (s_Lock) line = HillLine(live, spot, side, now);
                PkCommon.Say(p, line);
                if (now >= s_LineLogAt) { s_LineLogAt = now + 300; Mod.Log.Info($"[RevivalGuard] PkNight: hill line to {p.Name}: {line}"); }
                n++;
            }
        }

        static double s_LineLogAt;

        // ------------------------------------------------------------------ the markers

        static uint MarkerWcid(bool centre) => (uint)Math.Max(0, PkCommon.Long(centre ? P_MARKER_CENTRE : P_MARKER, centre ? MARKER_CENTRE_WCID : MARKER_WCID));

        static void EnsureMarkers(Live live, Spot spot, double now)
        {
            string key = MarkerKey(live, spot, now, Radius, $"{MarkerWcid(false)}/{MarkerWcid(true)}");
            if (s_MarkKey != key)
            {
                if (s_MarkKey != null) ClearMarkers(key == null ? "the night is over" : "the hill moved");
                if (key == null) return;
                s_MarkKey = key;
                foreach (var (ns, ew, centre) in MarkerPoints(live, spot, now, Radius))
                    if (MarkerWcid(centre) != 0) s_Markers.Add(new Marker { Ns = ns, Ew = ew, Centre = centre });
                s_MarkersMade = 0;
                Mod.Log.Info($"[RevivalGuard] PkNight: hill {HillIndex(live, spot, now) + 1} of {HillPoints(spot).Length} at {MapWords(HillAt(live, spot, now))}, {s_Markers.Count} markers");
            }
            foreach (var m in s_Markers)
            {
                if (m.Failed) continue;
                var wo = m.Wo;
                bool loaded = LandblockManager.IsLoaded(new ACE.Entity.LandblockId(CellOfMap(m.Ns, m.Ew).cell | 0xFFFF));
                if (MarkerCheck(wo != null, wo != null && wo.IsDestroyed, wo?.CurrentLandblock != null, now - m.MadeAt, loaded, MARKER_REMAKE_S) != MarkerDo.Make) continue;
                m.Wo = MakeMarker(m, live.EndsAt, now);
                m.MadeAt = now;
                m.Remakes++;
            }
        }

        /// <summary>Where a marker may stand if its own point is refused (a wall or a building there: box 2026-11-23, Zombie
        /// Castle's centre and three edge points sit in the castle ruin), in metres east and north, nearest first.</summary>
        static readonly (double e, double n)[] NUDGE =
            { (0, 0), (0, -3), (0, 3), (-3, 0), (3, 0), (0, -6), (0, 6), (-6, 0), (6, 0), (-6, -6), (6, -6), (-6, 6), (6, 6), (0, -10), (0, 10), (-10, 0), (10, 0) };

        static WorldObject NewMarker(Marker m, double ns, double ew, double endsAt, double now)
        {
            uint wcid = MarkerWcid(m.Centre);
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] PkNight: marker weenie {wcid} does not exist"); return null; }
            wo.Location = Ground(ns, ew, 0.05f);
            wo.Name = m.Centre ? "The Hill" : "Hill Torch";
            wo.Stuck = true;
            wo.Attackable = false;
            wo.Ethereal = true;
            wo.ItemUseable = Usable.No;
            wo.Value = 0;
            if (!m.Centre) wo.ObjScale = (wo.ObjScale ?? 1f) * MARKER_SCALE;
            wo.Lifespan = (int)Math.Max(300, endsAt - now + 300);   // gone by itself if a removal is ever missed
            s_MarkerGuids[wo.Guid.Full] = 0;
            return wo;
        }

        /// <summary>A marker, entering the world on the world thread at its point or the nearest NUDGE that takes it. Returns
        /// the first object made at once (so the tick does not make another while it waits); a nudged one replaces it in
        /// m.Wo. None taking: m.Failed, not tried again for this hill.</summary>
        static WorldObject MakeMarker(Marker m, double endsAt, double now)
        {
            try
            {
                var first = NewMarker(m, m.Ns, m.Ew, endsAt, now);
                if (first == null) { m.Failed = true; return null; }
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        var wo = first;
                        for (int i = 0; i < NUDGE.Length; i++)
                        {
                            if (i > 0) wo = NewMarker(m, m.Ns + NUDGE[i].n / 240, m.Ew + NUDGE[i].e / 240, endsAt, now);
                            if (wo == null) break;
                            if (wo.EnterWorld())
                            {
                                m.Wo = wo;
                                if (++s_MarkersMade == 1 || m.Centre || i > 0)
                                    Mod.Log.Info($"[RevivalGuard] PkNight: {wo.Name} 0x{wo.Guid.Full:X8} at {Mansions.Where(wo.Location)} (0x{wo.Location.Cell:X8})"
                                        + (i > 0 ? $", {NUDGE[i].e:0} m east {NUDGE[i].n:0} m north of its point (a wall there)" : ""));
                                return;
                            }
                            s_MarkerGuids.TryRemove(wo.Guid.Full, out _);
                        }
                        m.Failed = true;
                        Mod.Log.Warn($"[RevivalGuard] PkNight: {(m.Centre ? "the centre marker" : "an edge marker")} could not stand within 10 m of {MapWords((m.Ns, m.Ew))}");
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkNight: marker: {e.Message}"); }
                }));
                return first;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: making a marker: {e.Message}"); m.Failed = true; return null; }
        }

        static void ClearMarkers(string why)
        {
            if (s_MarkKey == null && s_Markers.Count == 0) return;
            int n = 0;
            foreach (var m in s_Markers)
            {
                var g = m.Wo;
                if (g == null || g.IsDestroyed) continue;
                n++;
                if (g.CurrentLandblock == null) { WorldManager.EnqueueAction(new ActionEventDelegate(() => { try { if (!g.IsDestroyed) g.Destroy(); } catch { } })); continue; }
                var chain = new ActionChain();
                chain.AddAction(g, () => { try { if (!g.IsDestroyed) g.Destroy(); } catch { } });
                chain.EnqueueChain();
            }
            s_Markers.Clear();
            s_MarkKey = null;
            Mod.Log.Info($"[RevivalGuard] PkNight: hill markers removed ({why}): {n}");
        }

        /// <summary>The markers are the mod's alone: never written to the shard database.</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard))]
        static class HillNoSave
        {
            static void Postfix(WorldObject __instance, ref bool __result)
            {
                if (__result && __instance != null && s_MarkerGuids.ContainsKey(__instance.Guid.Full)) __result = false;
            }
        }

        /// <summary>For @pknight gate: the markers now, in a line.</summary>
        internal static string MarkerStatus()
        {
            if (s_MarkKey == null) return "PK Night hill markers: none.";
            int up = s_Markers.Count(m => m.Wo != null && !m.Wo.IsDestroyed && m.Wo.CurrentLandblock != null);
            return $"PK Night hill markers ({s_MarkKey}): {up} of {s_Markers.Count} standing, {s_Markers.Count(m => m.Failed)} with no room, "
                + $"{s_Markers.Sum(m => Math.Max(0, m.Remakes - 1))} made again after an unload.";
        }

        // ------------------------------------------------------------------ the board's data

        /// <summary>The hill in the "night" JSON (live rounds): where it is, which point, when it moves and to where, and the
        /// battlefield line as the asker would read it.</summary>
        static void AppendHillJson(StringBuilder sb, Live live, Spot spot, Player asker, double now)
        {
            var pts = HillPoints(spot);
            int idx = HillIndex(live, spot, now);
            double move = NextMoveAt(live, spot, now);
            var at = pts[idx];
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append(",\"hill\":{\"n\":").Append(idx + 1).Append(",\"of\":").Append(pts.Length)
              .Append(",\"ns\":").Append(at.Ns.ToString("0.0000", inv)).Append(",\"ew\":").Append(at.Ew.ToString("0.0000", inv))
              .Append(",\"where\":").Append(PkCommon.Q(MapWords(at)))
              .Append(",\"moves\":").Append((long)move).Append(",\"every\":").Append(HILL_MOVE_MINUTES * 60);
            if (move > 0)
            {
                var nx = pts[(idx + 1) % pts.Length];
                sb.Append(",\"next\":{\"ns\":").Append(nx.Ns.ToString("0.0000", inv)).Append(",\"ew\":").Append(nx.Ew.ToString("0.0000", inv))
                  .Append(",\"where\":").Append(PkCommon.Q(MapWords(nx))).Append('}');
            }
            string line;
            uint side = 0;
            try { side = ReaderSide(asker, live); } catch { }
            lock (s_Lock) line = HillLine(live, spot, side, now);
            sb.Append(",\"line\":").Append(PkCommon.Q(line)).Append('}');
        }
    }
}
