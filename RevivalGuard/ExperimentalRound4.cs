using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>
    /// ROUND 4 (owner's picks, docs/content-review/experimental-spells/ROUND4-PICKS.md): ten more staff spells and one
    /// player-facing one. Drafts, test shard only, behind rg_experimental_spells like the rest.
    ///   spectacle   60118 Olthoi Eruption, 60119 Lightning Storm, 60120 Tidal Wave, 60121 Dragon Flyover
    ///   jokes       60122 Big Head Mode, 60123 Moon Gravity, 60124 Penguin Parade: a look on our client only, sent on the
    ///               mod channel ("explook"), timed there, kept nowhere on the server, and cleared when its wearer logs off
    ///   event tools 60125 Arena Wall, 60126 Boss Summon, 60127 Treasure Rain
    ///   player      60128 Void Chains: a monster cannot move for six seconds; never a boss
    /// They keep to what the earlier ones keep to: outdoors where the effect needs sky or ground, nothing spawned can be
    /// attacked or attacks a player, nothing harms a character or an item, everything ends by itself.
    /// </summary>
    static partial class ExperimentalSpells
    {
        const uint ERUPTION = 60118, STORM = 60119, WAVE = 60120, FLYOVER = 60121, BIGHEAD = 60122, MOON = 60123, GUISE = 60124,
                   ARENA = 60125, BOSS = 60126, TREASURE = 60127, CHAINS = 60128;
        const uint W_OLTHOI = 214, W_DRAGON = 60121, W_SACK = 36668, W_PYREAL = 273, W_NOTE = 2622;
        // our own PlayScripts (expansion/vfx, tools/vfx/build_vfx.py): a retail client has none of them and plays nothing
        const uint FX_ERUPTION = 0x10003, FX_STORM = 0x10004, FX_BOLT = 0x10005, FX_WAVE = 0x10006, FX_CHAINS = 0x10007, FX_TREASURE = 0x10008, FX_ARENA = 0x10009;
        const float STORM_R = 25f, WAVE_R = 36f, WAVE_SPEED = 12f, FLY_BACK = 75f, FLY_HEIGHT = 9f, FLY_SPEED = 22f, BURN_R = 16f;
        const float LOOK_R = 40f; const int LOOK_S = 60;
        const float ARENA_R = 15f; const double ARENA_S = 60;   // owner round 5: "make it 1 minute"
        const double CHAIN_S = 6, CHAIN_REST = 20; const uint CHAIN_HEALTH = 20000;
        // the admin panel's "Big ones" page and its "Event boss" (tools/admin/build_admin_layout.py BIG_SPAWNS, EVENT_BOSS)
        static readonly uint[] s_BossPool = { 38824, 22002, 22900, 31418, 38830, 72786, 46408, 28661, 38012, 46928, 88240, 72426, 51727, EventBoss.WCID };
        static readonly uint[] s_Gems = { 2413, 2415, 2416, 2419, 2426, 2428, 2429, 2432 };   // agate, lapis, malachite, turquoise, amber, carnelian, citrine, onyx

        internal static bool HookRoot, HookBag, HookWall;

        /// <summary>The refusal under a roof, in plain words. True = open sky.</summary>
        static bool Sky(Player p, string name, string why)
        {
            if (!p.Location.Indoors) return true;
            Say(p, $"{name} only works outdoors: {why} Step outside under open sky and cast it again.");
            return false;
        }

        static Vector3 Flat(Position from, Position to)
        {
            var o = from.GetOffset(to);
            return new Vector3(o.X, o.Y, 0f);
        }

        /// <summary>Living monsters within <paramref name="r"/> m of <paramref name="centre"/>: attackable creatures, never
        /// players, pets or anything a staff spell put there.</summary>
        static List<Creature> Monsters(Player p, Position centre, float r)
        {
            var found = new List<Creature>();
            if (p.PhysicsObj == null) return found;
            foreach (var c in p.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeCreature())
            {
                if (c == null || c is Player || c is CombatPet || !c.IsAlive || !c.Attackable || c.Location == null || s_Birds.ContainsKey(c.Guid.Full)) continue;
                if (Flat(centre, c.Location).Length() <= r) found.Add(c);
            }
            return found;
        }

        static void Fx(WorldObject on, uint script) => on.EnqueueBroadcast(new GameMessageScript(on.Guid, (PlayScript)script, 1f));

        // ------------------------------------------------------------------ 60118 Olthoi Eruption

        static readonly ConcurrentDictionary<uint, Flock> s_Swarms = new();   // caster -> their Olthoi (one ring each)

        /// <summary>The ground cracks round the caster and eight Olthoi burst up in a ring and go for the monsters near
        /// them (or whatever the caster selects; a player only after "@expspells players on"). They are Fowl Weather's
        /// flock with another body: unattackable, driven from FowlTick, gone in 45 s.</summary>
        static void Eruption(Player p)
        {
            if (!Sky(p, "Olthoi Eruption", "the Olthoi burst up through open ground.")) return;
            if (s_Swarms.TryRemove(p.Guid.Full, out var old)) Scatter(p, old, null);
            Fx(p, FX_ERUPTION);
            Thunder(p, EnvironChangeType.Thunder2Sound);
            var centre = new Position(p.Location);
            var f = new Flock { Until = DateTime.UtcNow.AddSeconds(45), Wcid = W_OLTHOI, Auto = true, Stay = true,
                Goes = "The Olthoi go for {0}!", Hits = "The Olthoi tear at {0}.", Killed = "The Olthoi have torn {0} apart.", Coming = "{0}'s Olthoi are coming for you!" };
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.9);   // the cracks first, then the bodies
            chain.AddAction(WorldManager.ActionQueue, () =>
            {
                if (p.Location == null || p.PhysicsObj == null) return;
                for (int i = 0; i < 8; i++)
                {
                    double a = 2 * Math.PI * i / 8;
                    var at = Spot(centre, (float)Math.Cos(a) * 7f, (float)Math.Sin(a) * 7f);
                    at.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(a - Math.PI / 2));   // facing outward
                    var c = Hatch(p, 1.25f, 50, W_OLTHOI, at);
                    if (c == null) continue;
                    f.Birds.Add(c); f.Scale.Add(1.25f); f.Home.Add(new Position(at));
                    c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.SplatterUpLeftFront));
                }
                if (f.Birds.Count == 0) { Say(p, "Olthoi Eruption found no open ground around you. Move to open, level ground and cast it again."); return; }
                s_Swarms[p.Guid.Full] = f;
                Thunder(p, EnvironChangeType.RoarSound);
                Drive(p, f, 45, "The Olthoi burrow back into the ground.");
            });
            chain.EnqueueChain();
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"The ground splits open around {p.Name}!");
        }

        /// <summary>FowlTick for <paramref name="seconds"/>, then the flock goes.</summary>
        static void Drive(Player p, Flock f, double seconds, string end)
        {
            int ticks = (int)(seconds / FOWL_TICK);
            var chain = new ActionChain();
            for (int t = 0; t < ticks; t++)
            {
                int n = t;
                chain.AddDelaySeconds(FOWL_TICK);
                chain.AddAction(WorldManager.ActionQueue, () => { try { FowlTick(p, f, n); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: swarm: {e.Message}"); } });
            }
            chain.AddAction(WorldManager.ActionQueue, () => Scatter(p, f, end));
            chain.EnqueueChain();
        }

        /// <summary>What a flock that hunts by itself goes for: the living monster nearest its first body, within 30 m.</summary>
        static Creature? Prey(Player p, Flock f)
        {
            Position? from = null;
            foreach (var b in f.Birds) if (!b.IsDestroyed && b.Location != null) { from = b.Location; break; }
            if (from == null) return null;
            Creature? best = null; float near = 30f;
            foreach (var c in Monsters(p, from, 30f))
            {
                float d = Flat(from, c.Location).Length();
                if (d < near) { near = d; best = c; }
            }
            return best;
        }

        // ------------------------------------------------------------------ 60119 Lightning Storm

        /// <summary>Twenty seconds of lightning over 25 m round where the caster stood (our effect, lightning_storm). Twice a
        /// second up to two monsters in it are struck (storm_bolt on each) and thrown; none is hurt. A monster is struck
        /// again only after three seconds, when it has landed.</summary>
        static void Storm(Player p)
        {
            if (!Sky(p, "Lightning Storm", "the bolts come down from the sky.")) return;
            var centre = new Position(p.Location);
            Fx(p, FX_STORM);
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"A lightning storm breaks over {p.Name}!");
            var struck = new Dictionary<uint, DateTime>();
            var sounds = new[] { EnvironChangeType.Thunder1Sound, EnvironChangeType.Thunder4Sound, EnvironChangeType.Thunder2Sound, EnvironChangeType.Thunder6Sound, EnvironChangeType.Thunder3Sound, EnvironChangeType.Thunder5Sound };
            int thrown = 0;
            var chain = new ActionChain();
            for (int i = 0; i < 40; i++)
            {
                int n = i;
                chain.AddAction(WorldManager.ActionQueue, () =>
                {
                    if (p.Location == null || p.PhysicsObj == null) return;
                    if (n % 3 == 0) Thunder(p, sounds[n / 3 % sounds.Length]);
                    int hits = 0;
                    foreach (var c in Monsters(p, centre, STORM_R))
                    {
                        if (hits >= 2) break;
                        if (struck.TryGetValue(c.Guid.Full, out var at) && (DateTime.UtcNow - at).TotalSeconds < 3) continue;
                        struck[c.Guid.Full] = DateTime.UtcNow;
                        Fx(c, FX_BOLT);
                        double a = Rnd(0f, (float)(2 * Math.PI));
                        Hurl(c, new Vector3((float)Math.Cos(a), (float)Math.Sin(a), 0f), p, 14f, 9f);
                        hits++; thrown++;
                    }
                });
                chain.AddDelaySeconds(0.5);
            }
            chain.AddAction(WorldManager.ActionQueue, () => { if (p.Session != null) Say(p, $"The storm passes. Its bolts threw monsters {thrown} {(thrown == 1 ? "time" : "times")}."); });
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ 60120 Tidal Wave

        /// <summary>A ring of water sweeps out from the caster at 12 m/s to 36 m (our effect, tidal_wave). Each monster is
        /// knocked over as the wave reaches it: a low throw 10 m outward, no damage. Players only after "@expspells players on".</summary>
        static void Wave(Player p)
        {
            if (!Sky(p, "Tidal Wave", "the wave needs open ground to sweep over.")) return;
            var centre = new Position(p.Location);
            Fx(p, FX_WAVE);
            Thunder(p, EnvironChangeType.DarkWindSound);
            Thunder(p, EnvironChangeType.Thunder5Sound);
            int n = 0, soaked = 0;
            foreach (var c in Monsters(p, centre, WAVE_R))
            {
                var d = Flat(centre, c.Location);
                var dir = d.LengthSquared() < 0.01f ? Vector3.UnitY : Vector3.Normalize(d);
                var hit = c;
                var chain = new ActionChain();
                chain.AddDelaySeconds(Math.Max(0.05, d.Length() / WAVE_SPEED));
                chain.AddAction(WorldManager.ActionQueue, () => { if (hit.IsAlive && hit.Location != null && p.Location != null) Hurl(hit, dir, p, 10f, 5.5f); });
                chain.EnqueueChain();
                n++;
            }
            if (s_GalePlayers.ContainsKey(p.Guid.Full))
                foreach (var o in PlayersNear(p, WAVE_R, false))
                {
                    var who = o;
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(Math.Max(0.05, Flat(centre, o.Location).Length() / WAVE_SPEED));
                    chain.AddAction(WorldManager.ActionQueue, () =>
                    {
                        if (who.Session == null || who.Location == null || p.Location == null) return;
                        if (Knockback.Push(who, p, 9f, 5f)) { SoftLanding(who); Say(who, $"{p.Name}'s wave knocks you off your feet! You are not harmed."); }
                    });
                    chain.EnqueueChain();
                    soaked++;
                }
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"A wall of water bursts out from {p.Name}!");
            Say(p, $"Your wave sweeps over {n} {(n == 1 ? "monster" : "monsters")}{(soaked > 0 ? $" and {soaked} {(soaked == 1 ? "player" : "players")}" : "")}.");
        }

        // ------------------------------------------------------------------ 60121 Dragon Flyover

        /// <summary>A giant Mukkir (retail's largest winged flyer, wings beating; retail has no dragon: weenie 60121, a body that flies as a
        /// spell's does) comes in from ahead of the caster 9 m up, passes over them at 22 m/s and is gone behind. It roars, fire rains
        /// under it as it crosses, and every monster within 16 m of where the caster stood burns and dies. Players, pets
        /// and NPCs are not touched.</summary>
        static void Flyover(Player p)
        {
            if (!Sky(p, "Dragon Flyover", "the dragon needs open sky to fly in.")) return;
            if (DatabaseManager.World.GetCachedWeenie(W_DRAGON) == null) { Say(p, $"The dragon's body (weenie {W_DRAGON}) is missing on this shard: the -draft datafix has not been applied."); return; }
            var f = Vector3.Transform(Vector3.UnitY, p.Location.Rotation); f.Z = 0f;
            f = f.LengthSquared() < 0.01f ? Vector3.UnitY : Vector3.Normalize(f);
            var centre = new Position(p.Location);
            var made = new List<SpellProjectile>();
            // from AHEAD of the caster, toward and over them: a chase camera looks the way the caster faces, so the whole
            // approach is in view (the first build came from behind, and was past and gone before anyone saw it)
            var from = Spot(centre, f.X * FLY_BACK, f.Y * FLY_BACK);
            from.PositionZ = Math.Max(from.PositionZ + 6f, centre.PositionZ + FLY_HEIGHT);   // level flight, 9 m over the caster
            var dragon = Fly(p, Variant(FIRESTORM, W_DRAGON), from, -f * FLY_SPEED);
            if (dragon != null) made.Add(dragon);
            if (made.Count == 0) { Say(p, "The dragon found no room in the sky ahead of you. Stand on open ground and cast it again."); return; }
            foreach (var sp in made)
            {
                var body = sp;
                var gone = new ActionChain();
                gone.AddDelaySeconds(2 * FLY_BACK / FLY_SPEED);   // as far past the caster as it came from
                gone.AddAction(WorldManager.ActionQueue, () => { try { if (!body.IsDestroyed) body.Destroy(); } catch { } });
                gone.EnqueueChain();
            }
            Thunder(p, EnvironChangeType.RoarSound);
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, "A vast shadow falls over you. Something enormous is coming in low!");
            var chain = new ActionChain();
            chain.AddDelaySeconds(FLY_BACK / FLY_SPEED - 1.9);   // the fire starts while it is still 40 m out and in everyone's view
            chain.AddAction(WorldManager.ActionQueue, () =>
            {
                if (p.Location == null || p.PhysicsObj == null) return;
                Thunder(p, EnvironChangeType.RoarSound);
                foreach (var sp in made) if (!sp.IsDestroyed) sp.EnqueueBroadcast(new GameMessageScript(sp.Guid, PlayScript.BreatheFlame));
                var at = Flat(p.Location, centre);
                Rain(p, FIRESTORM, 14, 5, 0.12, 1f, 14f, 11f, 16f, at + new Vector3(0.01f, 0f, 0f));
            });
            chain.AddDelaySeconds(1.6);
            chain.AddAction(WorldManager.ActionQueue, () =>
            {
                if (p.Location == null || p.PhysicsObj == null) return;
                int burned = 0;
                foreach (var c in Monsters(p, centre, BURN_R))
                {
                    c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.Explode));
                    try { c.Smite(p); burned++; } catch { }
                }
                Thunder(p, EnvironChangeType.Thunder3Sound);
                Say(p, $"The dragon's fire burns {burned} {(burned == 1 ? "monster" : "monsters")}.");
            });
            chain.EnqueueChain();
        }

        /// <summary>A spell's body put in the sky by hand, for show: ACE's own launch (LaunchSpellProjectiles) keeps the
        /// caster's landblock and refuses a start the caster cannot see, so a body 75 m behind them was never made. This one
        /// starts at a real position in whatever landblock that is, touches nothing (no collisions at all) and flies level.</summary>
        static SpellProjectile? Fly(Player p, Spell body, Position at, Vector3 velocity)
        {
            SpellProjectile? sp = null;
            try
            {
                sp = WorldObjectFactory.CreateNewWorldObject(body.Wcid) as SpellProjectile;
                if (sp == null) return null;
                sp.Setup(body, ProjectileSpellType.Bolt);
                sp.Ethereal = true; sp.IgnoreCollisions = true; sp.ReportCollisions = false;
                sp.Location = new Position(at);
                sp.PhysicsObj.Velocity = velocity;
                sp.PhysicsObj.Position.Frame.set_vector_heading(Vector3.Normalize(velocity));
                sp.Location.Rotation = sp.PhysicsObj.Position.Frame.Orientation;
                sp.ProjectileSource = p;
                sp.SetProjectilePhysicsState(null, false);
                sp.SpawnPos = new Position(sp.Location);
                if (s_Harmless.Count > 4000) s_Harmless.Clear();
                s_Harmless[sp.Guid.Full] = 1;
                if (LandblockManager.AddObject(sp)) return sp;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: fly {body.Id}: {e.Message}"); }
            try { sp?.Destroy(); } catch { }
            return null;
        }

        // ------------------------------------------------------------------ 60122-60124 looks on our client

        /// <summary>A timed look for every player within 40 m of the caster, shown by every client of ours within 96 m:
        /// "explook" on the mod channel, "<kind> <guid> <seconds> [setup mtable scale]". The client times it and undoes it;
        /// nothing is kept on the server or the character, so a relog is a clean body. For the others' sake a wearer who
        /// logs off (or comes back as a new session) inside the minute is cleared on every viewer.</summary>
        static void Look(Player p, string kind, string extra, string told, string done)
        {
            var subjects = PlayersNear(p, LOOK_R, true).ToList();
            var viewers = PlayersNear(p, SHOW_RANGE, true).Where(o => ModChannel.Capable(o.Session)).Select(o => o.Session).ToList();
            foreach (var s in subjects)
            {
                foreach (var v in viewers) ModChannel.Send(v, "explook", $"{kind} {s.Guid.Full} {LOOK_S}{extra}");
                s.EnqueueBroadcast(new GameMessageScript(s.Guid, PlayScript.EnchantUpYellow));
                Say(s, told);
                if (kind == "moon") SoftLanding(s, LOOK_S + 20);
            }
            var worn = subjects.Select(s => (id: s.Guid.Full, session: s.Session)).ToList();
            var chain = new ActionChain();
            for (int i = 0; i < LOOK_S; i++)
            {
                chain.AddDelaySeconds(1);
                chain.AddAction(WorldManager.ActionQueue, () =>
                {
                    for (int k = worn.Count - 1; k >= 0; k--)
                    {
                        var now = PlayerManager.GetOnlinePlayer(worn[k].id);
                        if (now != null && now.Session == worn[k].session) continue;
                        foreach (var v in viewers) if (v?.Player != null) ModChannel.Send(v, "explook", $"clear {worn[k].id}");
                        Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: look '{kind}' cleared on {viewers.Count} client(s): its wearer 0x{worn[k].id:X8} left the world");
                        worn.RemoveAt(k);
                    }
                });
            }
            chain.EnqueueChain();
            Say(p, $"{done} {subjects.Count} {(subjects.Count == 1 ? "player wears" : "players wear")} it for a minute; {viewers.Count} {(viewers.Count == 1 ? "client" : "clients")} here can show it.");
        }

        static void BigHead(Player p) => Look(p, "head", "", "Your head swells to a ridiculous size for a minute. It is only a look.", "Big Head Mode.");
        static void Moon(Player p) => Look(p, "moon", "", "You feel as light as on the moon for a minute: jump!", "Moon Gravity.");
        // a penguin (retail's, setup 0x02001252 with its own waddle 0x0900017B), a little under knee height
        static void Guise(Player p) => Look(p, "guise", " 0x02001252 0x0900017B 0.8", "You are a penguin for a minute. It is only a look: you fight, cast and carry as yourself.", "Penguin Parade.");

        // ------------------------------------------------------------------ 60125 Arena Wall

        sealed class Ring
        {
            public uint Caster; public Position Centre = null!; public DateTime Until; public bool Done;
            public List<WorldObject> Walls = new(); public Dictionary<uint, bool> Inside = new(); public Dictionary<uint, DateTime> Told = new();
            public int Ticks; public Dictionary<uint, DateTime> Pushed = new();
        }
        static readonly ConcurrentDictionary<uint, Ring> s_Rings = new();   // caster -> their ring (one each)

        /// <summary>A glowing ring (our effect, arena_ring) 15 m round where the caster stands, for one minute. Nobody walks, runs or
        /// jumps through it either way: a player's step across it is refused and their client pushed back (WallPrefix), a
        /// monster that crosses is set back on its own side (ArenaTick). The caster alone passes.
        /// IT CANNOT HOLD ANYONE FOR GOOD: recalls, portals and death leave it as always; it ends at one minute on the
        /// world's own queue whoever is online; it ends the moment its caster is no longer in the world; the caster ends
        /// it by casting again; and a restart of the shard forgets it (nothing is saved).</summary>
        static void Arena(Player p)
        {
            if (s_Rings.TryRemove(p.Guid.Full, out var old)) { Lift(old, $"{p.Name} lowers the arena wall."); return; }
            if (!Sky(p, "Arena Wall", "the ring stands on open ground.")) return;
            var r = new Ring { Caster = p.Guid.Full, Centre = new Position(p.Location), Until = DateTime.UtcNow.AddSeconds(ARENA_S) };
            // What is seen is our effect (arena_ring), played on one unseen marker at the centre and renewed while the wall
            // stands (ArenaTick). The marker is Fowl Weather's body a hundredth of its size: nothing to see, nothing to
            // bump into (a mana barrier's body, tried first, is a glass box our client would not let a player walk out of).
            var mark = WorldObjectFactory.CreateNewWorldObject(W_CHICKEN);
            if (mark != null)
            {
                mark.Name = "Arena Wall";
                mark.ObjScale = 0.01f;
                if (mark is Creature wall) wall.Invincible = true;
                mark.SetProperty(PropertyInt.ShowableOnRadar, (int)RadarBehavior.ShowNever);
                if (Show(mark, new Position(r.Centre), ARENA_S)) { r.Walls.Add(mark); s_Birds[mark.Guid.Full] = 1; }
            }
            if (r.Walls.Count == 0) { Say(p, "Arena Wall found no open ground to stand on here. Move to open, level ground and cast it again."); return; }
            s_Rings[p.Guid.Full] = r;
            Thunder(p, EnvironChangeType.BellSound);
            foreach (var o in PlayersNear(p, SHOW_RANGE, true))
                Say(o, $"{p.Name} raises an arena wall. Nobody passes it for one minute. Recalls and portals still work.");
            Say(p, "The wall stands for one minute, or until you cast Arena Wall again or leave the world. You alone can walk through it.");
            ArenaTick(r);
        }

        static void Lift(Ring r, string why)
        {
            if (r.Done) return;
            r.Done = true;
            s_Rings.TryRemove(new KeyValuePair<uint, Ring>(r.Caster, r));
            foreach (var wo in r.Walls)
            {
                s_Birds.TryRemove(wo.Guid.Full, out _);
                try { if (!wo.IsDestroyed) wo.Destroy(); } catch { }
            }
            foreach (var o in PlayerManager.GetAllOnline())
                if (o?.Session != null && o.Location != null && o.Location.DistanceTo(r.Centre) <= SHOW_RANGE) Say(o, why);
        }

        /// <summary>Twice a second: the ends (time, the caster gone), and the monsters. A monster's side is the side it
        /// was first seen on; one found across the line is set back 3 m inside its own side.</summary>
        static void ArenaTick(Ring r)
        {
            if (r.Done) return;
            try
            {
                var caster = PlayerManager.GetOnlinePlayer(r.Caster);
                if (DateTime.UtcNow >= r.Until) { Lift(r, "The arena wall fades away."); return; }
                if (caster == null || caster.Session == null || caster.Location == null) { Lift(r, "The arena wall falls: its caster has left."); return; }
                // the ring of light: ten seconds each time, played again every 8.5 s
                if (r.Ticks++ % 17 == 0) foreach (var wo in r.Walls) if (!wo.IsDestroyed) Fx(wo, FX_ARENA);
                if (caster.PhysicsObj != null && caster.Location.DistanceTo(r.Centre) < 150f)
                    foreach (var c in Monsters(caster, r.Centre, ARENA_R + 25f))
                    {
                        var d = Flat(r.Centre, c.Location);
                        float dist = d.Length();
                        uint id = c.Guid.Full;
                        if (!r.Inside.TryGetValue(id, out bool inside)) { r.Inside[id] = dist < ARENA_R; continue; }
                        if (inside ? dist <= ARENA_R - 0.5f : dist >= ARENA_R + 0.5f) continue;
                        var dir = dist < 0.05f ? Vector3.UnitY : d / dist;
                        var to = Spot(r.Centre, dir.X * (inside ? ARENA_R - 3f : ARENA_R + 3f), dir.Y * (inside ? ARENA_R - 3f : ARENA_R + 3f));
                        to.Rotation = c.Location.Rotation;
                        if (to.Landblock != c.Location.Landblock) continue;   // ACE moves a monster only inside its own landblock
                        try { c.FakeTeleport(to); } catch { }
                    }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: arena: {e.Message}"); }
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.5);
            chain.AddAction(WorldManager.ActionQueue, () => ArenaTick(r));
            chain.EnqueueChain();
        }

        /// <summary>Player.UpdatePlayerPosition prefix: a client's step from one side of a standing ring to the other is
        /// refused (the server keeps its position) and the client is sent back to its own side.
        /// Measured flat, so a jump does not clear it. Teleports do not come through here. False = refused.</summary>
        internal static bool WallPrefix(Player p, Position to, bool forceUpdate, ref bool __result)
        {
            if (s_Rings.IsEmpty || forceUpdate || p == null || to == null || p.Location == null || p.Teleporting) return true;
            foreach (var r in s_Rings.Values)
            {
                if (r.Done || r.Caster == p.Guid.Full) continue;
                if (p.Location.DistanceTo(r.Centre) > ARENA_R + 60f) continue;
                var reach = Flat(r.Centre, to);
                float dist = reach.Length();
                bool was = Flat(r.Centre, p.Location).Length() < ARENA_R, now = dist < ARENA_R;
                if (was == now) continue;
                // OUR CLIENT IS PUSHED BACK, NOT PUT BACK. A forced position reads there as a teleport (tested: every bump
                // was a moment of portal space), so a client on the mod channel gets the knockback push toward its own
                // side instead, and walks back into agreement with the server. A client that has no push (retail's), or
                // one that kept going 4 m past the line anyway, is put back the old way.
                var radial = dist < 0.05f ? Vector3.UnitY : reach / dist;
                bool deep = was ? dist > ARENA_R + 4f : dist < ARENA_R - 4f;
                bool pushed = false;
                if (!deep)
                {
                    if (r.Pushed.TryGetValue(p.Guid.Full, out var last) && (DateTime.UtcNow - last).TotalSeconds < 0.4) pushed = true;   // one is on its way
                    else if (Knockback.PushAlong(p, was ? -radial : radial, 8f, 2f)) { r.Pushed[p.Guid.Full] = DateTime.UtcNow; pushed = true; }
                }
                if (!pushed)
                {
                    p.Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
                    p.SendUpdatePosition();
                    MovementGuard.Rebaseline(p);
                }
                if (!r.Told.TryGetValue(p.Guid.Full, out var at) || (DateTime.UtcNow - at).TotalSeconds > 4)
                {
                    r.Told[p.Guid.Full] = DateTime.UtcNow;
                    Say(p, $"The arena wall holds. It stands for {Math.Max(1, (int)Math.Ceiling((r.Until - DateTime.UtcNow).TotalMinutes))} more {((r.Until - DateTime.UtcNow).TotalMinutes > 1 ? "minutes" : "minute")}; a recall or a portal still takes you out.");
                }
                __result = false;
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ 60126 Boss Summon

        /// <summary>One of the event bosses at random (the admin panel's Big ones page and its Event boss), 8 m in front
        /// of the caster: the sky tears, lightning strikes the spot, a rift opens, and the boss steps out of it two
        /// seconds later. It is the real monster, spawned by the same code as the panel's buttons.</summary>
        static void Boss(Player p)
        {
            if (!Sky(p, "Boss Summon", "a boss needs open ground to arrive on.")) return;
            uint wcid = 0;
            for (int i = 0; i < 12 && wcid == 0; i++)
            {
                uint pick = s_BossPool[ThreadSafeRandom.Next(0, s_BossPool.Length - 1)];
                if (DatabaseManager.World.GetCachedWeenie(pick) != null) wcid = pick;
            }
            if (wcid == 0) { Say(p, "No event boss is in this shard's world data."); return; }
            var f = Vector3.Transform(Vector3.UnitY, p.Location.Rotation);
            var gate = Gate(Spot(p, f.X * 8f, f.Y * 8f), 6);
            if (gate != null)
            {
                foreach (var fx in new[] { PlayScript.PortalStorm, PlayScript.BaelZharonSmite }) gate.EnqueueBroadcast(new GameMessageScript(gate.Guid, fx));
                Fx(gate, STRIKE_SCRIPT);
            }
            Thunder(p, EnvironChangeType.Thunder1Sound);
            Thunder(p, EnvironChangeType.DarkLaughSound);
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"The sky tears open before {p.Name}. Something answers the summons, and it is no ally: it will attack anyone near, {p.Name} too.");
            var chain = new ActionChain();
            chain.AddDelaySeconds(2.2);
            chain.AddAction(p, () =>
            {
                if (p.Location == null || p.Session == null) return;
                if (p.Location.Indoors) { Say(p, "The boss will not come under a roof."); return; }
                if (gate != null && !gate.IsDestroyed) Fx(gate, STRIKE_SCRIPT);
                Thunder(p, EnvironChangeType.RoarSound);
                Thunder(p, EnvironChangeType.Thunder3Sound);
                if (wcid == EventBoss.WCID) Say(p, EventBoss.Spawn(p));
                else AdminSpawn.Cmd(p.Session, wcid.ToString(CultureInfo.InvariantCulture));
                Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {p.Name} cast Boss Summon: weenie {wcid}");
            });
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ 60127 Treasure Rain

        sealed class Hoard { public HashSet<uint> Took = new(); }
        static readonly ConcurrentDictionary<uint, Hoard> s_Bags = new();   // a bag on the ground -> the cast it fell in

        /// <summary>Gold rains round the caster (our effect, treasure_rain) and bags land on the ground: one for each
        /// player within 40 m and three spare, 30 at most. A bag is opened by using it and holds one modest prize; a
        /// player may open ONE bag of a cast, whoever they are. Bags left over are gone in two minutes.</summary>
        static void Treasure(Player p)
        {
            Fx(p, FX_TREASURE);
            Thunder(p, EnvironChangeType.BellSound);
            int n = Math.Min(30, PlayersNear(p, LOOK_R, true).Count() + 3);
            var hoard = new Hoard();
            var centre = new Position(p.Location);
            bool roof = centre.Indoors;
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.0);
            for (int i = 0; i < n; i++)
            {
                chain.AddAction(WorldManager.ActionQueue, () =>
                {
                    var wo = WorldObjectFactory.CreateNewWorldObject(W_SACK);
                    if (wo == null) return;
                    double a = Rnd(0f, (float)(2 * Math.PI)); float d = roof ? Rnd(1f, 3f) : Rnd(2.5f, 11f);
                    wo.Name = "Treasure Bag";
                    wo.SetProperty(PropertyString.LongDesc, "A bag from a treasure rain. Use it to take the prize inside. One bag for each player.");
                    wo.SetProperty(PropertyBool.Stuck, true);
                    wo.SetProperty(PropertyInt.ItemUseable, (int)Usable.Remote);
                    wo.SetProperty(PropertyFloat.UseRadius, 2.0);
                    wo.ObjScale = 1.6f;
                    wo.TimeToRot = -1; wo.Lifespan = 135;
                    wo.Biota.PropertiesEmote?.Clear();
                    wo.Location = Spot(centre, (float)Math.Cos(a) * d, (float)Math.Sin(a) * d);
                    bool ok; try { ok = wo.EnterWorld(); } catch { ok = false; }
                    if (!ok) { try { wo.Destroy(); } catch { } return; }
                    s_Bags[wo.Guid.Full] = hoard;
                    wo.EnqueueBroadcast(new GameMessageScript(wo.Guid, PlayScript.EnchantUpYellow));
                    var end = new ActionChain();
                    end.AddDelaySeconds(120);
                    end.AddAction(WorldManager.ActionQueue, () => { s_Bags.TryRemove(wo.Guid.Full, out _); try { if (!wo.IsDestroyed) wo.Destroy(); } catch { } });
                    end.EnqueueChain();
                });
                chain.AddDelaySeconds(0.25);
            }
            chain.EnqueueChain();
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"Treasure rains down around {p.Name}! Use a bag to take your prize: one bag each.");
        }

        /// <summary>WorldObject.OnActivate prefix. False = it was one of our bags (opened, or refused).</summary>
        internal static bool BagPrefix(WorldObject bag, WorldObject activator)
        {
            if (s_Bags.IsEmpty || bag == null || !s_Bags.TryGetValue(bag.Guid.Full, out var hoard)) return true;
            if (!(activator is Player pl) || pl.Session == null) return false;
            try
            {
                lock (hoard)
                {
                    if (hoard.Took.Contains(pl.Guid.Full)) { Say(pl, "You have already taken your prize from this treasure rain. The other bags are for the other players."); return false; }
                    if (bag.IsDestroyed || !s_Bags.ContainsKey(bag.Guid.Full)) return false;
                    // modest: mostly pyreals, sometimes a small trade note or a common gem
                    int roll = ThreadSafeRandom.Next(1, 100);
                    uint wcid = roll <= 55 ? W_PYREAL : roll <= 80 ? W_NOTE : s_Gems[ThreadSafeRandom.Next(0, s_Gems.Length - 1)];
                    var prize = WorldObjectFactory.CreateNewWorldObject(wcid);
                    if (prize == null) { Say(pl, "The bag is empty (its prize is missing on this shard)."); return false; }
                    int coins = 0;
                    if (wcid == W_PYREAL) { coins = ThreadSafeRandom.Next(250, 1500); prize.SetStackSize(coins); }
                    string what = coins > 0 ? $"{coins:N0} pyreals" : prize.Name;
                    if (!pl.TryCreateInInventoryWithNetworking(prize))
                    {
                        try { prize.Destroy(); } catch { }
                        Say(pl, "Your pack has no room for the prize. Make room and use the bag again.");
                        return false;
                    }
                    hoard.Took.Add(pl.Guid.Full);
                    s_Bags.TryRemove(bag.Guid.Full, out _);
                    bag.EnqueueBroadcast(new GameMessageScript(bag.Guid, PlayScript.EnchantUpYellow));
                    try { bag.Destroy(); } catch { }
                    Say(pl, $"You open the bag and find {what}.");
                    Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: Treasure Rain: {pl.Name} took {what}");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: bag: {e.Message}"); }
            finally { try { pl.SendUseDoneEvent(); } catch { } }
            return false;
        }

        // ------------------------------------------------------------------ 60128 Void Chains (player-facing)

        static readonly ConcurrentDictionary<uint, DateTime> s_Rooted = new();     // monster -> held until
        static readonly ConcurrentDictionary<uint, DateTime> s_RootRest = new();   // monster -> cannot be held again before

        /// <summary>What Void Chains never takes: the event bosses, the world bosses, the raid commanders, and anything
        /// with 20,000 health or more.</summary>
        static bool BossLike(Creature c) =>
            Array.IndexOf(s_BossPool, c.WeenieClassId) >= 0 || c.WeenieClassId == EventBoss.SHADOW || WorldBoss.Bosses.ContainsKey(c.WeenieClassId)
            || TthuunRaidsRule.IsBoss(c.WeenieClassId) || c.Health.MaxValue >= CHAIN_HEALTH;

        static bool ChainsOk(Player? p, WorldObject? target)
        {
            if (!(target is Creature c) || c is Player || c is CombatPet || !c.IsAlive || !c.Attackable || c.Location == null)
            { if (p != null) Say(p, "Select a target first. Void Chains needs a living monster selected."); return false; }
            if (BossLike(c)) { if (p != null) Say(p, $"{c.Name} is too mighty to be chained."); return false; }
            if (s_RootRest.TryGetValue(c.Guid.Full, out var rest) && DateTime.UtcNow < rest)
            {
                if (p != null) Say(p, s_Rooted.ContainsKey(c.Guid.Full) ? $"{c.Name} is already chained." : $"{c.Name} has just broken free and cannot be chained again so soon.");
                return false;
            }
            return true;
        }

        /// <summary>The enchantment landed (ACE's own resist roll is behind us): the monster stands where it is for six
        /// seconds. It still turns nowhere and walks nowhere, but it attacks what is in reach and casts as before. Then
        /// twenty seconds in which it cannot be chained again.</summary>
        static void ChainsLand(Player? p, Creature c)
        {
            if (s_Rooted.Count > 2000) s_Rooted.Clear();
            if (s_RootRest.Count > 4000) s_RootRest.Clear();
            var until = DateTime.UtcNow.AddSeconds(CHAIN_S);
            s_Rooted[c.Guid.Full] = until;
            s_RootRest[c.Guid.Full] = until.AddSeconds(CHAIN_REST);
            Fx(c, FX_CHAINS);
            try
            {
                if (c.IsMoving || c.IsTurning)
                {
                    c.PhysicsObj?.MovementManager?.MoveToManager?.CancelMoveTo(WeenieError.ActionCancelled);
                    c.IsMoving = false; c.IsTurning = false;
                    c.EnqueueBroadcastMotion(new Motion(c.CurrentMotionState.Stance, MotionCommand.Ready));
                    c.SendUpdatePosition();
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: chains halt {c.Name}: {e.Message}"); }
            if (p != null) Say(p, $"Void chains hold {c.Name} where it stands for six seconds.");
        }

        /// <summary>Creature.StartTurn / Movement prefix. False = the monster is chained and does not move.</summary>
        internal static bool Rooted(Creature c)
        {
            if (s_Rooted.IsEmpty || c == null || !s_Rooted.TryGetValue(c.Guid.Full, out var until)) return false;
            if (DateTime.UtcNow < until && c.IsAlive) return true;
            s_Rooted.TryRemove(c.Guid.Full, out _);
            return false;
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsRootTurn
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Creature), nameof(Creature.StartTurn), Type.EmptyTypes);
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookRoot = TargetMethod() != null);
        static bool Prefix(Creature __instance) { try { return !ExperimentalSpells.Rooted(__instance); } catch { return true; } }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsRootMove
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Creature), nameof(Creature.Movement), Type.EmptyTypes);
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static bool Prefix(Creature __instance) { try { return !ExperimentalSpells.Rooted(__instance); } catch { return true; } }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsBag
    {
        static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(WorldObject), nameof(WorldObject.OnActivate), new[] { typeof(WorldObject) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookBag = TargetMethod() != null);
        static bool Prefix(WorldObject __instance, WorldObject activator)
        {
            try { return ExperimentalSpells.BagPrefix(__instance, activator); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: bag: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsWall
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.UpdatePlayerPosition), new[] { typeof(Position), typeof(bool) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookWall = TargetMethod() != null);
        static bool Prefix(Player __instance, Position newPosition, bool forceUpdate, ref bool __result)
        {
            try { return ExperimentalSpells.WallPrefix(__instance, newPosition, forceUpdate, ref __result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: wall: {e.Message}"); return true; }
        }
    }
}
