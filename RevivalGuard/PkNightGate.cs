using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// PK NIGHT'S EVENT PORTALS AND QUIET GROUND (owner 2026-11-20, "Yes, this Sunday": the Dark Isle). For a spot with
    /// HasGate (PkNightRules), from an hour before the start to the end of the round (GateSpot):
    ///   * a "Dark Isle Portal" stands in Holtburg's lower town (never the lifestone square where players arrive) and lands
    ///     players on the beach by the spot, no flagging quest asked; a "Holtburg Portal" on the beach takes them back;
    ///   * creature generators within the spot's ClearMetres of the centre or the landing do not generate (a prefix on
    ///     Generator_Generate, so pods, their bosses and generators loaded during the window all wait), and hostile
    ///     generator-spawned creatures within half that are removed every SWEEP_S, so the circle and the landing are a PK
    ///     fight, not a level 150 slaughter. Everything further out is retail. At the end the prefix lets go: the
    ///     generators fill again on their own regeneration (retail delays, 5 minutes on the Dark Isle).
    /// The portals are Gateways (weenie 1955: never saved to the shard, use only, no recall tie) made again if a landblock
    /// unload took one; a restart mid-window makes them again on the next tick.
    /// </summary>
    public static partial class PkNight
    {
        const uint GATEWAY_WCID = 1955;
        const double SWEEP_S = 10, REMAKE_AFTER_S = 30;

        static string s_GateKey;                       // the spot whose portals stand now (null: none)
        static volatile Spot s_PauseSpot;              // read by the generator prefix on landblock threads
        static Portal s_GateOut, s_GateHome;
        static double s_GateOutAt, s_GateHomeAt, s_GateEnds, s_SweepAt;
        static int s_Swept, s_Held;

        /// <summary>Every tick, with or without a round: open, keep or close the portals of the spot GateSpot names.</summary>
        static void GateStep(double now, DateTime utc, Live live)
        {
            Spot want; double ends;
            if (live != null) { want = GateSpot(live, null, false, utc); ends = live.EndsAt; }
            else
            {
                var next = Next(utc);
                want = GateSpot(null, next, next != null && Played(next.Key), utc);
                ends = next != null ? ToUnix(next.EndUtc) : 0;
            }
            if (want == null) { if (s_GateKey != null) CloseGate("the night is over"); return; }
            if (s_GateKey != want.Key)
            {
                if (s_GateKey != null) CloseGate("another spot");
                s_GateKey = want.Key; s_PauseSpot = want; s_GateEnds = ends; s_Swept = 0; s_Held = 0;
                Mod.Log.Info($"[RevivalGuard] PkNight: portals to {want.Name} open until {DateTimeOffset.FromUnixTimeSeconds((long)ends):u}; "
                    + $"generators within {want.ClearMetres:0} m of the spot and the landing paused");
                World(GateOpenLine(want));
            }
            s_GateEnds = ends;
            EnsureGates(want, now);
            if (now >= s_SweepAt) { s_SweepAt = now + SWEEP_S; Sweep(want); }
        }

        static void EnsureGates(Spot spot, double now)
        {
            if (Gone(s_GateOut, s_GateOutAt, now, HOLTBURG_GATE_NS, HOLTBURG_GATE_EW))
            {
                s_GateOut = MakeGate(GateName(spot), HOLTBURG_GATE_NS, HOLTBURG_GATE_EW, spot.LandNs, spot.LandEw, now);
                s_GateOutAt = now;
            }
            if (Gone(s_GateHome, s_GateHomeAt, now, spot.HomeNs, spot.HomeEw))
            {
                s_GateHome = MakeGate(HOME_NAME, spot.HomeNs, spot.HomeEw, HOLTBURG_LAND_NS, HOLTBURG_LAND_EW, now);
                s_GateHomeAt = now;
            }
        }

        /// <summary>A portal to (re)make: never made, destroyed, or out of the world for a while (a landblock unload took
        /// it). Remade only where its landblock is loaded again (a player there), except the first time, so an empty isle
        /// is not kept awake.</summary>
        static bool Gone(Portal p, double madeAt, double now, double ns, double ew)
        {
            if (p == null) return true;
            bool gone = p.IsDestroyed || (p.CurrentLandblock == null && now - madeAt > REMAKE_AFTER_S);
            if (!gone) return false;
            var (cell, _, _) = CellOfMap(ns, ew);
            return LandblockManager.IsLoaded(new LandblockId(cell | 0xFFFF));
        }

        static Portal MakeGate(string name, double ns, double ew, double toNs, double toEw, double now)
        {
            try
            {
                if (!(WorldObjectFactory.CreateNewWorldObject(GATEWAY_WCID) is Portal gate)) { Mod.Log.Warn($"[RevivalGuard] PkNight: weenie {GATEWAY_WCID} is not a portal"); return null; }
                gate.Location = Ground(ns, ew, 0.0f);
                gate.Name = name;
                gate.PortalRestrictions = PortalBitmask.Unrestricted | PortalBitmask.NoRecall;   // no tie, no summon from it
                gate.UpdatePortalDestination(Ground(toNs, toEw, 0.05f));
                gate.Lifespan = (int)Math.Max(600, s_GateEnds - now + 300);   // gone by itself if a close is ever missed
                WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (!gate.EnterWorld()) { Mod.Log.Warn($"[RevivalGuard] PkNight: {name} could not enter at {gate.Location}"); return; }
                        gate.EnqueueBroadcast(new ACE.Server.Network.GameMessages.Messages.GameMessageScript(gate.Guid, PlayScript.PortalStorm));
                        Mod.Log.Info($"[RevivalGuard] PkNight: {name} 0x{gate.Guid.Full:X8} at {Mansions.Where(gate.Location)} (0x{gate.Location.Cell:X8}), to {Mansions.Where(gate.Destination)}");
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] PkNight: {name}: {e.Message}"); }
                }));
                return gate;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] PkNight: making {name}: {e.Message}"); return null; }
        }

        static Position Ground(double ns, double ew, float lift)
        {
            var (cell, x, y) = CellOfMap(ns, ew);
            var at = new Position(cell, x, y, 0f, 0f, 0f, 0f, 1f);
            try { at.PositionZ = at.GetTerrainZ() + lift; } catch { }
            return at;
        }

        static void CloseGate(string why)
        {
            var key = s_GateKey;
            s_GateKey = null; s_PauseSpot = null;
            foreach (var g in new[] { s_GateOut, s_GateHome })
            {
                if (g == null || g.IsDestroyed) continue;
                if (g.CurrentLandblock == null) { WorldManager.EnqueueAction(new ActionEventDelegate(() => { try { if (!g.IsDestroyed) g.Destroy(); } catch { } })); continue; }
                var chain = new ActionChain();
                chain.AddAction(g, () => { try { if (!g.IsDestroyed) g.Destroy(); } catch { } });
                chain.EnqueueChain();
            }
            s_GateOut = s_GateHome = null;
            Mod.Log.Info($"[RevivalGuard] PkNight: portals to {key} closed ({why}); generators there let go "
                + $"({s_Held} generator runs held, {s_Swept} creatures kept off the ground)");
        }

        /// <summary>Hostile creatures a generator made within half the clear radius: removed (no corpse, no loot).</summary>
        static void Sweep(Spot spot)
        {
            double r = spot.ClearMetres / 2;
            var (cell, _, _) = CellOfMap(spot.Ns, spot.Ew);
            uint bx = cell >> 24, by = (cell >> 16) & 0xFF;
            int span = (int)Math.Ceiling((spot.ClearMetres + 60) / 192.0);
            int n = 0;
            for (int dx = -span; dx <= span; dx++)
                for (int dy = -span; dy <= span; dy++)
                {
                    long x = bx + dx, y = by + dy;
                    if (x < 0 || y < 0 || x > 0xFE || y > 0xFE) continue;
                    var id = new LandblockId((uint)((x << 24) | (y << 16) | 0xFFFF));
                    if (!LandblockManager.IsLoaded(id)) continue;
                    var lb = LandblockManager.GetLandblock(id, false);
                    if (lb == null) continue;
                    foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                    {
                        if (!(wo is Creature c) || c is Player || c is Pet || c.IsDead || c.IsDestroyed || !c.Attackable || c.Generator == null || c.Location == null) continue;
                        var g = c.Location.ToGlobal();
                        if (!InClearZone(spot, g.X, g.Y, r)) continue;
                        n++;
                        var chain = new ActionChain();
                        chain.AddAction(c, () =>
                        {
                            try { if (!c.IsDestroyed && !c.IsDead) { c.EnqueueBroadcast(new ACE.Server.Network.GameMessages.Messages.GameMessageScript(c.Guid, PlayScript.Destroy)); c.Destroy(); } }
                            catch { }
                        });
                        chain.EnqueueChain();
                    }
                }
            if (n > 0) { s_Swept += n; Mod.Log.Info($"[RevivalGuard] PkNight: {n} creature(s) kept off {spot.Name} ({s_Swept} this window)"); }
        }

        /// <summary>While a gate spot's window runs, its nearby generators make nothing (their queues wait too).</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Generator_Generate))]
        static class HoldGenerators
        {
            static bool Prefix(WorldObject __instance)
            {
                var spot = s_PauseSpot;
                if (spot == null) return true;
                try
                {
                    var loc = __instance.Location;
                    if (loc == null || loc.Indoors) return true;
                    var g = loc.ToGlobal();
                    if (!InClearZone(spot, g.X, g.Y, spot.ClearMetres)) return true;
                    if (__instance is Container && !(__instance is Creature)) return true;   // chests keep their contents
                    System.Threading.Interlocked.Increment(ref s_Held);
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>For @pknight: the portals now, in a line.</summary>
        internal static string GateStatus()
        {
            if (s_GateKey == null) return "PK Night portals: none open.";
            string P(Portal p) => p == null ? "not made" : p.IsDestroyed ? "gone" : p.CurrentLandblock == null ? "waiting to enter" : Mansions.Where(p.Location);
            return $"PK Night portals ({s_GateKey}): out {P(s_GateOut)}, home {P(s_GateHome)}; generator runs held {s_Held}, creatures cleared {s_Swept}.";
        }
    }
}
