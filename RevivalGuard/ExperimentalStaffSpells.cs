using System.Collections.Concurrent;
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
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Sequence;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>
    /// THE STAFF-ONLY EXPERIMENTAL SPELLS 60109-60117 (owner 2026-11-27: "more staff only spells for myself to use. one for
    /// knockback. a brutally killing one. a raven fury style but extremely deadly. and others that make spectacular visuals
    /// ... more joke spells"; and "a spell that sends a victims ragdol body flying"). Drafts, test shard only, behind
    /// rg_experimental_spells like the rest; ExperimentalSpells.CastPrefix refuses every one of them below Sentinel.
    ///
    /// What they keep to: the killing ones take monsters (attackable creatures) and, only when the staff member targets
    /// one, a player; a player killed by one drops nothing (NoDrop). Everything that falls or flies for show is marked
    /// harmless (the bolt bursts and nothing else). Everything spawned is unattackable, never attacks (Fowl Weather's flock
    /// pecks only what its caster selected, FowlTick), can be walked
    /// through, and is gone by itself (Lifespan) even if the caster logs off. Retail models and effects only.
    /// </summary>
    static partial class ExperimentalSpells
    {
        const uint GALE = 60109, UNMAKING = 60110, RAVEN = 60111, SENDOFF = 60112, STARFALL = 60113, FINALE = 60114,
                   VISITATION = 60115, FOWL = 60116, COWS = 60117;
        const uint FIRESTORM = 1834, BOVINE = 2366, FORCE_RING = 3807, LIGHTNING_RING = 3808, RAVEN_FURY = 3818;   // retail spells, for their look
        const uint FIREWORK_FIRST = 2871, FIREWORK_LAST = 2926;                            // the 56 "Firework UP" spells
        const uint W_PORTAL = 1955, W_ASHERON = 36091, W_BAEL = 8503, W_CHICKEN = 35499;
        // owner 11-27 "doesnt knock back enough": a monster is THROWN now (a real arc every client flies, 15 m/s of lift,
        // about 3 s in the air, 11 m high) up to 48 m, not blinked 18 m. Players (when allowed): 22 m/s out, 12 up.
        const float GALE_RANGE = 20f, GALE_THROW = 48f, GALE_UP = 15f, GALE_THROW_INDOORS = 6f, GALE_PLAYER_H = 22f, GALE_PLAYER_UP = 12f;
        // owner 11-27 "doesnt send them flying ... with extreme enough force and distance": 90 m at 45 degrees (21 m/s up,
        // 21 m/s out: 30 m/s off the ground, 4.3 s in the air, 22 m high). It was 45 m at 72 degrees, which read as "up".
        const float LAUNCH_DIST = 90f, LAUNCH_UP = 21f, LAUNCH_SPIN = 4f;                  // metres, m/s, x the usual tumble
        const float G = 9.8f;
        // Round 3 (owner's F7 notes after the shard4 playtest, 11-27).
        const uint W_STAR = 60113, W_MOOCOW = 60117;   // our copies of the Firestorm fire and the Bovine cow (the -draft datafix): a star's body, a cow with a cow's voice
        const uint FADE_TABLE = 0x3400002B;            // a small retail effect table with Create 0x58 and Destroy 0x59: retail's own 1 s fade in and out
        const uint SHOCK_WAVE = 2144;                  // Crushing Shame, for its look: Send-Off's bolt and Unmaking's ring
        const uint STRIKE_SCRIPT = 0x10002;            // our own PlayScript: expansion/vfx/unmaking_strike (a retail client has no such script and plays nothing)
        const float BOLT_SPEED = 16f;                  // Send-Off's bolt, m/s: a run is about 4, so a watched bolt can be sidestepped
        const float LAUNCH_PLAYER_H = 21f, LAUNCH_PLAYER_UP = 21f;   // a player the bolt hits: the same throw as a body (90 m, 22 m high)
        static readonly ConcurrentDictionary<uint, Vector3> s_Bolts = new();   // Send-Off's bolts in flight -> their flat heading
        static readonly ConcurrentDictionary<(uint, uint), Spell> s_Variants = new();
        static readonly ConcurrentDictionary<uint, DateTime> s_SoftLanding = new();   // players a staff spell threw -> until when a fall does not hurt

        internal static bool HookCollide, HookDrop;
        [ThreadStatic] static bool t_Harmless, t_Raven;
        static readonly ConcurrentDictionary<uint, byte> s_Raven = new();        // Raven's Reckoning's bolts
        static readonly ConcurrentDictionary<uint, byte> s_Harmless = new();     // projectiles that only burst
        static readonly ConcurrentDictionary<uint, byte> s_NoDrop = new();       // players a staff spell is killing
        static readonly ConcurrentDictionary<uint, byte> s_GalePlayers = new();  // casters whose Gale knocks players too
        static int s_Visits;

        static float Rnd(float a, float b) => (float)ThreadSafeRandom.Next(a, b);
        static void Thunder(Player p, EnvironChangeType snd)
        {
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) o.Session.Network.EnqueueSend(new GameMessageAdminEnvirons(o.Session, snd));
        }

        /// <summary>A spot <paramref name="east"/>/<paramref name="north"/> metres from the caster, on the ground outdoors.</summary>
        static Position Spot(Player p, float east, float north) => Spot(p.Location, east, north);
        static Position Spot(Position from, float east, float north)
        {
            Position at;
            if (from.Indoors) { at = new Position(from); at.PositionX += east; at.PositionY += north; }
            else { at = from.FromGlobal(from.ToGlobal() + new Vector3(east, north, 0f)); at.PositionZ = at.GetTerrainZ() + 0.05f; }
            return at;
        }

        /// <summary>A retail projectile spell with another body (<paramref name="wcid"/>, one of our draft weenies): the
        /// spell's own row copied once with that weenie in it. Without the weenie (datafix not applied) it is the retail spell.</summary>
        static Spell Variant(uint spellId, uint wcid) => s_Variants.GetOrAdd((spellId, wcid), _ =>
        {
            var spell = new Spell(spellId);
            try
            {
                if (spell.NotFound || spell._spell == null || DatabaseManager.World.GetCachedWeenie(wcid) == null) return spell;
                var row = (ACE.Database.Models.World.Spell)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(spell._spell, null)!;
                row.Wcid = wcid;
                spell._spell = row;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: variant {spellId}/{wcid}: {e.Message}"); }
            return spell;
        });

        /// <summary>Something for show: cannot be attacked, never attacks, can be walked through, drops and says nothing, gone
        /// after <paramref name="life"/> seconds by itself.</summary>
        static bool Show(WorldObject wo, Position at, double life, bool fade = false)
        {
            if (fade) wo.SetProperty(PropertyDataId.PhysicsEffectTable, FADE_TABLE);   // it fades in and out instead of popping
            wo.SetProperty(PropertyBool.Attackable, false);
            wo.SetProperty(PropertyInt.Tolerance, (int)Tolerance.NoAttack);
            wo.SetProperty(PropertyBool.NoCorpse, true);
            wo.Ethereal = true;
            wo.TimeToRot = -1;
            wo.Lifespan = (int)life + 15;   // the safety net; the chain below is the normal end
            wo.Biota.PropertiesEmote?.Clear();
            wo.Location = at;
            bool ok;
            try { ok = wo.EnterWorld(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: {wo.Name} enter: {e.Message}"); ok = false; }
            if (!ok) { try { wo.Destroy(); } catch { } return false; }
            var chain = new ActionChain();
            if (fade)
            {
                wo.EnqueueBroadcast(new GameMessageScript(wo.Guid, PlayScript.Create));
                chain.AddDelaySeconds(Math.Max(0.1, life - 1.1));
                chain.AddAction(WorldManager.ActionQueue, () => { try { if (!wo.IsDestroyed) wo.EnqueueBroadcast(new GameMessageScript(wo.Guid, PlayScript.Destroy)); } catch { } });
                chain.AddDelaySeconds(1.1);
            }
            else chain.AddDelaySeconds(life);
            chain.AddAction(WorldManager.ActionQueue, () => { try { if (!wo.IsDestroyed) wo.Destroy(); } catch { } });
            chain.EnqueueChain();
            return true;
        }

        /// <summary>A real portal body that is only a picture (owner 11-27 round 3: "make sure players can't use the portals
        /// that spell makes and no weird text in chat if used": using one said "Portal destination for portal ID 1955 not
        /// yet implemented!"). Not Active, so ACE's OnActivate returns before it looks for a destination, whether the
        /// player used it or walked into it; not usable; reports no collision.</summary>
        static WorldObject? Picture(string name)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(W_PORTAL);
            if (wo == null) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: no weenie {W_PORTAL} for {name}"); return null; }
            wo.Name = name;
            wo.SetProperty(PropertyInt.Active, 0);
            wo.SetProperty(PropertyBool.ReportCollisions, false);
            wo.SetProperty(PropertyInt.ItemUseable, (int)Usable.No);
            if (wo.PhysicsObj != null) wo.PhysicsObj.State &= ~PhysicsState.ReportCollisions;
            return wo;
        }

        // ------------------------------------------------------------------ 60106's portals

        /// <summary>Eight portals in a ring round where the caster stood, for <paramref name="seconds"/>: they open one after
        /// another over the first three seconds and close one after another over the last five, each fading in and out.
        /// Real portal bodies (retail's gateway, so every client draws its swirl) that lead nowhere (Picture).</summary>
        static void StormPortals(Player p, double seconds)
        {
            const int N = 8;
            var centre = new Position(p.Location);
            var chain = new ActionChain();
            for (int i = 0; i < N; i++)
            {
                int k = (i * 3) % N;   // not round the ring in order
                double a = 2 * Math.PI * k / N;
                float r = 6f + (k % 2) * 3f;
                double life = seconds - i * 0.4 - (N - 1 - i) * 0.7;
                chain.AddAction(WorldManager.ActionQueue, () =>
                {
                    var wo = Picture("Portal Storm");
                    if (wo != null) Show(wo, Spot(centre, (float)Math.Cos(a) * r, (float)Math.Sin(a) * r), life, true);
                });
                chain.AddDelaySeconds(0.4);
            }
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ harmless things that fall and fly

        static List<SpellProjectile> LaunchHarmless(Player p, Spell spell, Vector3 worldOffset, Vector3 velocity)
        {
            var none = new List<SpellProjectile>();
            if (p.Location == null || p.PhysicsObj == null || spell.NotFound) return none;
            // LaunchSpellProjectiles turns an origin by the caster's heading: give it the world offset turned back
            var local = Vector3.Transform(worldOffset, Quaternion.Inverse(p.PhysicsObj.Position.Frame.Orientation));
            if (s_Harmless.Count > 4000) s_Harmless.Clear();
            t_Harmless = true;   // a bolt that hits something as it enters the world is harmless too
            try
            {
                var made = p.LaunchSpellProjectiles(spell, null, SpellProjectile.GetProjectileSpellType(spell.Id), null, false, false,
                    new List<Vector3> { local }, velocity, 0) ?? none;
                foreach (var sp in made) s_Harmless[sp.Guid.Full] = 1;
                return made;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: launch {spell.Id}: {e.Message}"); return none; }
            finally { t_Harmless = false; }
        }

        /// <summary><paramref name="count"/> volleys of <paramref name="each"/> of a retail falling spell's bodies, <paramref name="every"/> seconds apart,
        /// from <paramref name="height"/> m up at random spots <paramref name="rMin"/>-<paramref name="rMax"/> m from the caster
        /// (or from <paramref name="centre"/>, a world offset from the caster).</summary>
        static void Rain(Player p, uint spellId, int count, int each, double every, float rMin, float rMax, float height, float fall, Vector3 centre = default,
            Spell? body = null, Action<SpellProjectile>? born = null)
        {
            var spell = body ?? new Spell(spellId);
            if (spell.NotFound) { Say(p, $"Spell {spellId} is missing on this shard."); return; }
            // Under a roof (owner 11-27, Starfall in a dungeon: "did nothing when used": a body put 18 m over the caster is
            // outside every room, so ACE refused all but a stray one): they start just under the ceiling, close in, and fall slowly.
            bool roof = p.Location.Indoors;
            if (roof) { height = 1.9f; rMin = Math.Min(rMin, 0.8f); rMax = Math.Min(rMax, 4.5f); fall = Math.Min(fall, 2.5f); }
            int made = 0;
            var chain = new ActionChain();
            for (int i = 0; i < count; i++)
            {
                chain.AddAction(p, () =>
                {
                    for (int k = 0; k < each; k++)
                    {
                        double a = Rnd(0f, (float)(2 * Math.PI)); float r = Rnd(rMin, rMax);
                        foreach (var sp in LaunchHarmless(p, spell, centre + new Vector3((float)Math.Cos(a) * r, (float)Math.Sin(a) * r, height + Rnd(0f, roof ? 0.5f : 4f)), new Vector3(Rnd(-3f, 3f), Rnd(-3f, 3f) , -fall) * (roof ? new Vector3(0.3f, 0.3f, 1f) : Vector3.One)))
                        {
                            made++;
                            try { born?.Invoke(sp); } catch { }
                        }
                    }
                });
                chain.AddDelaySeconds(every);
            }
            chain.AddAction(p, () =>
            {
                Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {p.Name} rain of {spellId}: {made} of {count * each} bodies entered the world{(roof ? " (indoors)" : "")}");
                if (made * 2 < count * each && centre == default)
                    Say(p, $"Only {made} of {count * each} found room to fall here{(roof ? ": the ceiling is in the way. This spell is at its best outdoors, under open sky" : "")}.");
            });
            chain.EnqueueChain();
        }

        static void Starfall(Player p)
        {
            // owner round 3: "might it have star 3d objects. right now it looks like fireballs not stars": the falling body
            // is our weenie 60113 (retail's lightning orb, white, for a client without the enhanced effects), and the client
            // draws a bright spinning star with a trail of sparkles on it (expansion/vfx/starfall_star, starfall_burst).
            Rain(p, FIRESTORM, 75, 3, 0.2, 3f, 16f, 26f, 9f, default, Variant(FIRESTORM, W_STAR));   // 26 m at 9 m/s: three seconds in the sky, slow enough to read as stars
            Thunder(p, EnvironChangeType.Thunder4Sound);
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"The stars fall at {p.Name}'s call!");
        }

        static void Cows(Player p)
        {
            // owner round 3: "make sure they moo while falling". ACE's cow body has a sound table of two waves the retail
            // DATs do not hold, so it was silent; ours (60117) is the same body with the retail cow's voice, and each one
            // is told to cry out as it starts to fall (the cow's long moo, Wound1; one in three its Death1 a second later).
            int cow = 0;
            Rain(p, BOVINE, 30, 3, 0.4, 2f, 10f, 20f, 9f, default, Variant(BOVINE, W_MOOCOW), sp =>
            {
                bool also = ++cow % 3 == 0;
                var moo = new ActionChain();
                moo.AddDelaySeconds(0.25);   // after the client has the body
                moo.AddAction(sp, () => { if (!sp.IsDestroyed) sp.EnqueueBroadcast(new GameMessageSound(sp.Guid, Sound.Wound1, 1f)); });
                if (also)
                {
                    moo.AddDelaySeconds(1.0);
                    moo.AddAction(sp, () => { if (!sp.IsDestroyed) sp.EnqueueBroadcast(new GameMessageSound(sp.Guid, Sound.Death1, 1f)); });
                }
                moo.EnqueueChain();
            });
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, "The forecast called for cows.");
        }

        static void Finale(Player p)
        {
            var chain = new ActionChain();
            for (int i = 0; i < 120; i++)
            {
                chain.AddAction(p, () =>
                {
                    var spell = new Spell((uint)ThreadSafeRandom.Next((int)FIREWORK_FIRST, (int)FIREWORK_LAST));
                    var made = LaunchHarmless(p, spell, new Vector3(Rnd(-6f, 6f), Rnd(-6f, 6f), 1.5f), new Vector3(Rnd(-5f, 5f), Rnd(-5f, 5f), Rnd(12f, 20f)));
                    foreach (var sp in made)
                    {
                        var fuse = new ActionChain();
                        fuse.AddDelaySeconds(Rnd(0.9f, 1.5f));
                        fuse.AddAction(sp, () => { if (!sp.IsDestroyed && sp.PhysicsObj != null && sp.ReportCollisions == true) sp.ProjectileImpact(); });
                        fuse.EnqueueChain();
                    }
                });
                chain.AddDelaySeconds(0.125);
            }
            chain.EnqueueChain();
            p.EnqueueBroadcast(new GameMessageScript(p.Guid, PlayScript.WeddingBliss));
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, $"{p.Name} lights up the sky!");
        }

        /// <summary>A staff spell never fizzles for staff, whatever their magic skills (a staff character has none).</summary>
        /// <summary>A staff caster never fizzles one of ours, the player-facing ones too (owner round 5: Void Chains "fizzes
        /// every time": his staff character is untrained in Void Magic, so the rare's +250 was all the skill it had against
        /// difficulty 300, 3 casts in 100). A player's cast keeps ACE's own roll.</summary>
        internal static bool SureCast(Player p, Spell spell) => spell != null && Ours(spell.Id) && Live && Staff(p);

        /// <summary>SpellProjectile.OnCollideObject prefix. False = ACE's damage is skipped.</summary>
        internal static bool CollidePrefix(SpellProjectile sp, WorldObject target)
        {
            if (sp == null) return true;
            if (s_Bolts.TryRemove(sp.Guid.Full, out var heading))
            {
                sp.ProjectileImpact();
                BoltHit(sp, heading, target);
                return false;
            }
            bool raven = t_Raven || s_Raven.ContainsKey(sp.Guid.Full);
            if (!raven && !t_Harmless && !s_Harmless.ContainsKey(sp.Guid.Full)) return true;
            sp.ProjectileImpact();   // the burst, and nothing else: no damage, no aggro
            // Raven's Reckoning: a monster the bolt touches dies. Never a player, a pet, or anything unattackable.
            if (raven && Live && sp.ProjectileSource is Player caster && Staff(caster)
                && target is Creature c && !(c is Player) && !(c is CombatPet) && c.Attackable && c.IsAlive)
            {
                c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.BaelZharonSmite));
                c.Smite(caster);
            }
            return false;
        }

        // ------------------------------------------------------------------ the killing ones

        /// <summary>May a staff kill spell take this target? A living attackable monster, or a player the staff member
        /// targeted (a targeted cast is the explicit aim). Never the caster, an NPC, a vendor or a pet.</summary>
        static bool Slayable(Player p, WorldObject target, out Creature c)
        {
            c = (target as Creature)!;
            if (c == null || c == p || !c.IsAlive || c.Location == null || c is CombatPet) return false;
            return c is Player || c.Attackable;
        }

        /// <summary>The line for a targeted staff spell cast at nothing it can take (owner round 3: "unmaking says casting
        /// unmaking in chat. with no enemy selected. need better message there. more clear").</summary>
        static string NoTarget(uint spellId) => $"Select a target first. {DefOf(spellId)!.Name} needs a living monster, or a player, selected.";

        /// <summary>Player.HandleActionCastTargetedSpell prefix: Unmaking and Skyward Send-Off aimed at nothing (no
        /// selection, a body already dead, an NPC, a pet, the caster) are refused in plain words before the windup starts.
        /// False = the cast is dropped.</summary>
        internal static bool AimPrefix(Player p, uint targetGuid, uint spellId)
        {
            if (spellId == CHAINS && Live)
            {
                WorldObject? held = null;
                try { held = p.CurrentLandblock?.GetObject(targetGuid); } catch { }
                // untrained, ACE lets the cast start and it fizzles every time without a word: say why instead
                if (!Staff(p) && p.GetCreatureSkill(Skill.VoidMagic).AdvancementClass < SkillAdvancementClass.Trained)
                {
                    Say(p, "You are not trained in Void Magic. Void Chains needs the skill trained, and about 350 of it to land reliably.");
                    try { p.SendUseDoneEvent(); } catch { }
                    return false;
                }
                if (ChainsOk(p, held)) return true;
                try { p.SendUseDoneEvent(); } catch { }
                return false;
            }
            if ((spellId != UNMAKING && spellId != SENDOFF) || !Live || !Staff(p)) return true;
            WorldObject? target = null;
            try { target = p.CurrentLandblock?.GetObject(targetGuid); } catch { }
            if (target != null && Slayable(p, target, out _)) return true;
            Say(p, NoTarget(spellId));
            try { p.SendUseDoneEvent(); } catch { }
            return false;
        }

        /// <summary>Unmaking: the target is struck down and dies, whatever its health. Owner round 3: "needs more intense
        /// strike down vfx. they all happen too slow". What was slow: the shadow funnel (BaelZharonSmite) takes 1.7 s to
        /// come down, the fire fell for a second, and the death came at 1.1 s. Now our own strike effect
        /// (expansion/vfx/unmaking_strike, PlayScript 0x10002: lightning from the sky, a flash, a ground-shaking shock
        /// ring, thrown rock and embers, all in the first tenth of a second, with a camera shake) plays on the target at
        /// once, fire lands on it 0.4 s later, and it dies at 0.3 s in blood with a ring of shock waves bursting outward.
        /// A client without our effects still sees the fire, the blood and the ring. A player killed this way drops nothing.</summary>
        static void Unmake(Player p, WorldObject target)
        {
            if (!Slayable(p, target, out var c)) { Say(p, NoTarget(UNMAKING)); return; }
            var off = p.Location.GetOffset(c.Location);
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, (PlayScript)STRIKE_SCRIPT, 1f));
            Thunder(p, EnvironChangeType.Thunder1Sound);
            bool roof = c.Location.Indoors || p.Location.Indoors;
            float top = roof ? 1.8f : 10f, step = roof ? 0.03f : 0.4f;
            var fire = new Spell(FIRESTORM);
            int fires = 0, waves = 0;
            // a body that lives under 0.4 s is gone before a client has drawn it, so that is as fast as the fire falls
            for (int i = 0; i < 10; i++)
                fires += LaunchHarmless(p, fire, new Vector3(off.X + Rnd(-2.2f, 2.2f), off.Y + Rnd(-2.2f, 2.2f), off.Z + top + i * step), new Vector3(Rnd(-1.5f, 1.5f), Rnd(-1.5f, 1.5f), roof ? -4f : -26f)).Count;
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.3);
            chain.AddAction(c, () =>
            {
                if (!c.IsAlive || c.Location == null || p.Location == null) return;
                foreach (var s in new[] { PlayScript.SplatterUpLeftFront, PlayScript.SplatterUpRightBack, PlayScript.SplatterMidLeftBack, PlayScript.SplatterMidRightFront, PlayScript.SplatterLowLeftFront, PlayScript.SplatterLowRightBack })
                    c.EnqueueBroadcast(new GameMessageScript(c.Guid, s));
                Thunder(p, EnvironChangeType.Thunder3Sound);
                // the impact: twelve shock waves burst out from the body and are spent ten metres on
                var wave = new Spell(SHOCK_WAVE);
                var at = p.Location.GetOffset(c.Location);
                for (int i = 0; i < 12; i++)
                {
                    double a = 2 * Math.PI * i / 12;
                    var dir = new Vector3((float)Math.Cos(a), (float)Math.Sin(a), 0f);
                    foreach (var sp in LaunchHarmless(p, wave, new Vector3(at.X, at.Y, at.Z + 0.9f) + dir * 1.2f, dir * 18f))
                    {
                        waves++;
                        var fuse = new ActionChain();
                        fuse.AddDelaySeconds(0.6);
                        fuse.AddAction(sp, () => { if (!sp.IsDestroyed && sp.PhysicsObj != null && sp.ReportCollisions == true) sp.ProjectileImpact(); });
                        fuse.EnqueueChain();
                    }
                }
                Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: Unmaking on {c.Name}: {fires} of 10 fires, {waves} of 12 shock waves entered the world");
                uint id = c.Guid.Full;
                if (c is Player) s_NoDrop[id] = 1;
                try { c.Smite(p); }
                finally
                {
                    if (c is Player)
                    {
                        var later = new ActionChain();   // the corpse is made a few seconds after the death
                        later.AddDelaySeconds(30);
                        later.AddAction(WorldManager.ActionQueue, () => s_NoDrop.TryRemove(id, out _));
                        later.EnqueueChain();
                    }
                }
            });
            chain.EnqueueChain();
            Say(p, $"You unmake {c.Name}.");
            if (c is Player victim) Say(victim, $"{p.Name} unmakes you. You lose nothing.");
            Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {p.Name} cast Unmaking on {c.Name} (0x{c.Guid.Full:X8})");
        }

        /// <summary>Skyward Send-Off. Owner round 3: "might it be a bolt that players can try to dodge so i can have fun
        /// with it. if they are hit by it they go flying in the direction of the spell." It is a bolt now (retail's shock
        /// wave, 16 m/s), thrown at where the target stands and never steering: what it hits is thrown along its path
        /// (BoltHit), and a miss bursts on the ground and does nothing. Players are aimed at only after
        /// "@expspells players on", and are thrown, never killed: it is a game to play with them.</summary>
        static void SendOff(Player p, WorldObject target)
        {
            if (!Slayable(p, target, out var c)) { Say(p, NoTarget(SENDOFF)); return; }
            if (c is Player && !s_GalePlayers.ContainsKey(p.Guid.Full))
            {
                Say(p, $"Skyward Send-Off leaves players alone until you type @expspells players on. Then its bolt throws {c.Name} and does no harm.");
                return;
            }
            // owner 11-27 cast it in a dungeon and the body only dropped: a throw needs somewhere to land. Say so.
            if (c.Location.Indoors || p.Location.Indoors)
            {
                Say(p, "Skyward Send-Off only works outdoors: a roof is in the way of the throw. Take the target out under open sky (Unmaking kills indoors).");
                return;
            }
            var off = p.Location.GetOffset(c.Location);
            var flat = new Vector3(off.X, off.Y, 0f);
            flat = flat.LengthSquared() < 0.01f ? Vector3.Transform(Vector3.UnitY, p.Location.Rotation) : Vector3.Normalize(flat);
            var from = flat * 1.2f + new Vector3(0f, 0f, 1.1f);
            float chest = Math.Clamp((c.PhysicsObj?.GetHeight() ?? 1.6f) * 0.5f, 0.4f, 2.5f);
            var aim = new Vector3(off.X, off.Y, off.Z + chest) - from;
            if (aim.LengthSquared() < 0.01f) aim = flat;
            var made = LaunchHarmless(p, new Spell(SHOCK_WAVE), from, Vector3.Normalize(aim) * BOLT_SPEED);
            if (made.Count == 0) { Say(p, "The bolt found no room to fly from where you stand."); return; }
            if (s_Bolts.Count > 500) s_Bolts.Clear();
            foreach (var sp in made) { s_Harmless.TryRemove(sp.Guid.Full, out _); s_Bolts[sp.Guid.Full] = flat; }
            Say(p, $"You hurl a bolt at {c.Name}.");
            if (c is Player victim) Say(victim, $"{p.Name} hurls a bolt at you. Dodge it!");
            Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {p.Name} cast Skyward Send-Off at {c.Name} (0x{c.Guid.Full:X8})");
        }

        /// <summary>A player a staff spell throws lands unhurt (tested round 3: a 22 m high throw down a hillside cost the
        /// victim 35 health, enough to kill a new character; "launched, not killed"). Twenty seconds covers the flight.</summary>
        static void SoftLanding(Player victim, double seconds = 20)
        {
            if (s_SoftLanding.Count > 200) s_SoftLanding.Clear();
            s_SoftLanding[victim.Guid.Full] = DateTime.UtcNow.AddSeconds(seconds);
        }

        /// <summary>Player.TakeDamage_Falling prefix. False = no fall damage.</summary>
        internal static bool FallPrefix(Player victim)
        {
            if (victim == null || !s_SoftLanding.TryGetValue(victim.Guid.Full, out var until)) return true;
            if (DateTime.UtcNow <= until) return false;
            s_SoftLanding.TryRemove(victim.Guid.Full, out _);
            return true;
        }

        /// <summary>Send-Off's bolt touched something. A monster dies and its body is thrown LAUNCH_DIST along the bolt's
        /// heading (where there is ground for it; else it dies where it stood). A player is thrown the same way by the
        /// client's knockback push, unharmed, and only if the caster has players on. Anything else: only the burst.</summary>
        static void BoltHit(SpellProjectile sp, Vector3 heading, WorldObject target)
        {
            if (!Live || !(sp.ProjectileSource is Player caster) || !Staff(caster) || caster.Location == null) return;
            if (!(target is Creature c) || c == caster || !c.IsAlive || c.Location == null || c is CombatPet || s_Birds.ContainsKey(c.Guid.Full)) return;
            if (c is Player victim)
            {
                if (!s_GalePlayers.ContainsKey(caster.Guid.Full)) return;
                SoftLanding(victim);
                if (!Knockback.PushAlong(victim, heading, LAUNCH_PLAYER_H, LAUNCH_PLAYER_UP)) { Say(caster, $"Your bolt hits {victim.Name}, but their client cannot be thrown."); return; }
                victim.EnqueueBroadcast(new GameMessageScript(victim.Guid, PlayScript.WeddingBliss));
                Thunder(caster, EnvironChangeType.Thunder2Sound);
                Say(victim, $"{caster.Name}'s bolt sends you flying! You are not harmed.");
                Say(caster, $"Your bolt sends {victim.Name} flying.");
                Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {caster.Name}'s Send-Off bolt threw player {victim.Name}");
                return;
            }
            if (!c.Attackable) return;
            if (RagdollCorpse.CanLaunch(caster.Location, c.Location, LAUNCH_DIST, heading)) RagdollCorpse.Launch(c.Guid.Full, LAUNCH_DIST, LAUNCH_UP, LAUNCH_SPIN, heading);
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.WeddingBliss));
            Thunder(caster, EnvironChangeType.Thunder2Sound);
            c.Smite(caster);
            Say(caster, $"Your bolt sends {c.Name} skyward.");
            Mod.Log.Info($"[RevivalGuard] ExperimentalSpells: {caster.Name}'s Send-Off bolt killed {c.Name} (0x{c.Guid.Full:X8})");
        }

        /// <summary>Player.CalculateDeathItems prefix: a player a staff spell killed drops nothing.</summary>
        internal static bool DropPrefix(Player victim, ref List<WorldObject> __result)
        {
            if (victim == null || !s_NoDrop.ContainsKey(victim.Guid.Full)) return true;
            __result = new List<WorldObject>();
            return false;
        }

        // ------------------------------------------------------------------ Banishing Gale

        static void Gale(Player p)
        {
            LaunchRing(p, FORCE_RING); LaunchRing(p, LIGHTNING_RING);
            Thunder(p, EnvironChangeType.DarkWindSound);
            Thunder(p, EnvironChangeType.Thunder1Sound);
            int moved = 0;
            foreach (var c in p.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeCreature())
            {
                if (c == null || c is Player || c is CombatPet || !c.IsAlive || !c.Attackable || c.Location == null || s_Birds.ContainsKey(c.Guid.Full)) continue;
                var off = p.Location.GetOffset(c.Location);
                var dir = new Vector3(off.X, off.Y, 0f);
                if (dir.Length() > GALE_RANGE) continue;
                dir = dir.LengthSquared() < 0.01f ? Vector3.UnitY : Vector3.Normalize(dir);
                Hurl(c, dir, p);
                moved++;
            }
            int knocked = 0;
            if (s_GalePlayers.ContainsKey(p.Guid.Full))
                foreach (var o in PlayersNear(p, GALE_RANGE, false))
                    if (Knockback.Push(o, p, GALE_PLAYER_H, GALE_PLAYER_UP)) { SoftLanding(o); knocked++; Say(o, $"{p.Name}'s gale throws you back!"); }
            Say(p, $"Your gale hurls {moved} {(moved == 1 ? "monster" : "monsters")}{(knocked > 0 ? $" and {knocked} {(knocked == 1 ? "player" : "players")}" : "")} away.");
        }

        /// <summary>A VectorUpdate with a velocity of our choosing (ACE's own message reads the server body's, which a
        /// monster does not have): every client, retail's too, flies the body along it under gravity, as it does a jump.</summary>
        sealed class Thrown : GameMessage
        {
            public Thrown(WorldObject wo, Vector3 velocity, Vector3 omega) : base(GameMessageOpcode.VectorUpdate, GameMessageGroup.SmartboxQueue, 36)
            {
                Writer.WriteGuid(wo.Guid);
                Writer.Write(velocity.X); Writer.Write(velocity.Y); Writer.Write(velocity.Z);
                Writer.Write(omega.X); Writer.Write(omega.Y); Writer.Write(omega.Z);
                Writer.Write(wo.Sequences.GetCurrentSequence(SequenceType.ObjectInstance));
                Writer.Write(wo.Sequences.GetNextSequence(SequenceType.ObjectVector));
            }
        }

        /// <summary>A monster thrown along <paramref name="dir"/>. Outdoors it flies: clients are sent the launch velocity,
        /// the monster is held asleep while it is in the air, and the server sets it down on the landing spot as the arc
        /// ends (ACE moves a monster only inside its own landblock, so the throw is the farthest dry-land spot up to
        /// GALE_THROW inside it). Then it wakes and comes back for the caster. Indoors it is a short shove.</summary>
        static void Hurl(Creature c, Vector3 dir, Player by, float far = GALE_THROW, float lift = GALE_UP)
        {
            var start = new Position(c.Location);
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.SplatterMidLeftFront));
            Position? to = null;
            float up = lift;
            if (start.Indoors)
            {
                // under a roof: as far down the room or corridor as it can be seen to go (never through a wall), on a low arc
                up = 4.5f;
                foreach (float d in new[] { 16f, 13f, 10f, 7f, 4f })
                {
                    var t = IndoorSpot(start, dir * d);
                    bool clear; try { clear = t != null && c.IsDirectVisible(t); } catch { clear = false; }
                    if (clear) { to = t; break; }
                }
            }
            else
                for (float d = far; d >= Math.Min(6f, far * 0.4f) && to == null; d -= Math.Min(3f, far * 0.2f))
                {
                    var t = start.FromGlobal(start.ToGlobal() + dir * d);
                    if (t.Indoors || t.Landblock != start.Landblock) continue;
                    t.PositionZ = t.GetTerrainZ();
                    t.Rotation = start.Rotation;
                    to = t;
                }
            if (to == null) { Shove(c, dir, start); return; }
            var flat = start.Indoors ? new Vector3(to.PositionX - start.PositionX, to.PositionY - start.PositionY, 0f) : to.ToGlobal() - start.ToGlobal();
            flat.Z = 0f;
            float air = 2f * up / G;
            var v = flat / air + new Vector3(0f, 0f, up);
            var target = c.AttackTarget ?? by;
            try { c.Sleep(); } catch { }   // nothing bites, walks or casts from mid-air
            c.EnqueueBroadcast(new Thrown(c, v, new Vector3(0f, 0f, 7f)));
            var chain = new ActionChain();
            chain.AddDelaySeconds(air);
            chain.AddAction(c, () =>
            {
                if (!c.IsAlive || c.Location == null || c.PhysicsObj == null) return;
                try { c.FakeTeleport(to); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: hurl {c.Name}: {e.Message}"); }
                c.EnqueueBroadcast(new Thrown(c, Vector3.Zero, Vector3.Zero));
                c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.SplatterLowLeftFront));
                if (target is Creature t && t.IsAlive && !c.IsAwake) { c.AttackTarget = t; c.WakeUp(false); }
            });
            chain.EnqueueChain();
        }

        /// <summary><paramref name="start"/> moved by <paramref name="d"/> inside a dungeon or building, in the room that
        /// holds that point (a position that keeps the old room's cell id is refused by ACE: the indoor gale moved nothing);
        /// null when the point is in no room the start can see.</summary>
        static Position? IndoorSpot(Position start, Vector3 d)
        {
            try
            {
                if (!(ACE.Server.Physics.Common.LScape.get_landcell(start.Cell) is ACE.Server.Physics.Common.EnvCell cell)) return null;
                var pt = new Vector3(start.PositionX + d.X, start.PositionY + d.Y, start.PositionZ + 0.3f);
                ACE.Server.Physics.Common.ObjCell? found = cell.point_in_cell(pt) ? cell : cell.find_visible_child_cell(pt, true);
                if (found == null) return null;
                var to = new Position(start);
                to.PositionX += d.X; to.PositionY += d.Y;
                to.LandblockId = new ACE.Entity.LandblockId(found.ID);
                return to;
            }
            catch { return null; }
        }

        /// <summary>The fallback (no clear way indoors, a landblock edge outdoors): a short blink along <paramref name="dir"/>; a spot ACE cannot place it on (another
        /// body, a wall) is tried again nearer and a little to the side.</summary>
        static void Shove(Creature c, Vector3 dir, Position start)
        {
            bool indoors = start.Indoors;
            float total = indoors ? GALE_THROW_INDOORS : 12f;
            var chain = new ActionChain();
            foreach (float share in new[] { 1f, 0.75f, 0.5f, 0.3f })
            {
                chain.AddAction(c, () =>
                {
                    if (!c.IsAlive || c.Location == null || c.PhysicsObj == null) return;
                    if (c.Location.DistanceTo(start) > total * 0.25f) return;   // already thrown
                    var d = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, share < 1f ? Rnd(-0.5f, 0.5f) : 0f)) * total * share;
                    Position to;
                    if (indoors) { var spot = IndoorSpot(start, d); if (spot == null) return; to = spot; }
                    else
                    {
                        to = start.FromGlobal(start.ToGlobal() + d);
                        if (to.Indoors || to.Landblock != start.Landblock) return;
                        to.PositionZ = to.GetTerrainZ();
                    }
                    to.Rotation = start.Rotation;
                    try { c.FakeTeleport(to); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: hurl {c.Name}: {e.Message}"); }
                });
                chain.AddDelaySeconds(0.15);
            }
            chain.EnqueueChain();
        }

        /// <summary>A retail ring spell's bolts from the caster, for the look only; <paramref name="raven"/>: they kill
        /// the monsters they touch (CollidePrefix).</summary>
        static void LaunchRing(Player p, uint spellId, bool raven = false)
        {
            var spell = new Spell(spellId);
            if (spell.NotFound || p.Location == null || p.PhysicsObj == null) return;
            var set = raven ? s_Raven : s_Harmless;
            if (set.Count > 4000) set.Clear();
            t_Harmless = !raven; t_Raven = raven;
            try { foreach (var sp in p.CreateSpellProjectiles(spell, null, null) ?? new List<SpellProjectile>()) set[sp.Guid.Full] = 1; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: ring {spellId}: {e.Message}"); }
            finally { t_Harmless = false; t_Raven = false; }
        }

        /// <summary>Raven's Reckoning: Curse of Raven Fury's own ring (3818's bolts), three waves; nothing is drained from
        /// the caster and the bolts do no damage of their own. A monster one touches dies.</summary>
        static void Raven(Player p)
        {
            var chain = new ActionChain();
            for (int i = 0; i < 3; i++)
            {
                chain.AddAction(p, () => LaunchRing(p, RAVEN_FURY, true));
                chain.AddDelaySeconds(0.5);
            }
            chain.EnqueueChain();
            Thunder(p, EnvironChangeType.DarkLaughSound);
        }

        // ------------------------------------------------------------------ Visitation, Fowl Weather

        /// <summary>The first of <paramref name="tries"/> the creature's motion table has in <paramref name="stance"/> (or the
        /// stance it is in), played for everyone; its length in seconds, 0 if it has none of them.</summary>
        static float Act(Creature c, MotionStance? stance, params MotionCommand[] tries)
        {
            try
            {
                if (c.IsDestroyed || c.Location == null) return 0f;
                var st = stance ?? c.CurrentMotionState?.Stance ?? MotionStance.NonCombat;
                foreach (var m in tries)
                {
                    float len = MotionTable.GetAnimationLength(c.MotionTableId, st, m);
                    if (len <= 0f) continue;
                    c.EnqueueBroadcastMotion(new Motion(st, m));
                    return len;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: {c.Name} motion: {e.Message}"); }
            return 0f;
        }

        static void Speak(Creature c, string line)
        {
            if (c.IsDestroyed || c.Location == null) return;
            c.EnqueueBroadcast(new GameMessageHearRangedSpeech(line, c.Name, c.Guid.Full, SHOW_RANGE, ChatMessageType.Speech), SHOW_RANGE, ChatMessageType.Speech);
        }

        // ------------------------------------------------------------------ Visitation

        const int VISIT_LIFE = 45;
        static readonly ConcurrentDictionary<uint, Creature> s_Images = new();   // caster -> the image standing for them

        /// <summary>The image's place as a world offset from the caster now (for things launched round it).</summary>
        static Vector3 Offset(Player p, WorldObject wo)
        {
            var o = p.Location.GetOffset(wo.Location);
            return new Vector3(o.X, o.Y, 0f);
        }

        /// <summary>Fire or fireworks bursting round the image (harmless, like everything launched here).</summary>
        static void Flourish(Player p, Creature c, bool bael, int waves)
        {
            if (c.IsDestroyed || c.Location == null || p.Location == null) return;
            var at = Offset(p, c);
            if (at.Length() > 80f) return;
            if (bael) Rain(p, FIRESTORM, waves, 4, 0.25, 2f, 9f, 22f, 16f, at);
            else
            {
                var chain = new ActionChain();
                for (int i = 0; i < waves * 3; i++)
                {
                    chain.AddAction(p, () =>
                    {
                        var spell = new Spell((uint)ThreadSafeRandom.Next((int)FIREWORK_FIRST, (int)FIREWORK_LAST));
                        foreach (var sp in LaunchHarmless(p, spell, at + new Vector3(Rnd(-7f, 7f), Rnd(-7f, 7f), 2f), new Vector3(Rnd(-3f, 3f), Rnd(-3f, 3f), Rnd(14f, 22f))))
                        {
                            var fuse = new ActionChain();
                            fuse.AddDelaySeconds(Rnd(0.8f, 1.3f));
                            fuse.AddAction(sp, () => { if (!sp.IsDestroyed && sp.PhysicsObj != null && sp.ReportCollisions == true) sp.ProjectileImpact(); });
                            fuse.EnqueueChain();
                        }
                    });
                    chain.AddDelaySeconds(0.1);
                }
                chain.EnqueueChain();
            }
        }

        /// <summary>A ring of a retail ring spell's bolts flying out from the image at <paramref name="height"/> m: the
        /// "something showy" it casts. Harmless.</summary>
        static void ImageRing(Player p, Creature c, uint spellId, float height)
        {
            if (c.IsDestroyed || c.Location == null || p.Location == null) return;
            var spell = new Spell(spellId);
            if (spell.NotFound) return;
            var at = Offset(p, c);
            for (int i = 0; i < 16; i++)
            {
                double a = 2 * Math.PI * i / 16;
                var dir = new Vector3((float)Math.Cos(a), (float)Math.Sin(a), 0f);
                LaunchHarmless(p, spell, at + dir * 3f + new Vector3(0f, 0f, height), dir * 14f);
            }
        }

        /// <summary>A real portal (unscaled, so retail's and our effects play on it at their own size) where the image
        /// stands: what it arrives through and leaves by. Leads nowhere, cannot be used.</summary>
        static WorldObject? Gate(Position at, int life)
        {
            var wo = Picture("Rift");
            return wo != null && Show(wo, new Position(at), life, true) ? wo : null;
        }

        /// <summary>WHY THE IMAGE "DOESNT WORK RELAIBLY" (owner 11-27; his editor log and the box client agree): the first
        /// time a client meets Asheron's or Bael'Zharon's body it has to build the model and its animation library, 15 s
        /// on his Mac for Asheron and over 20 s on the box, more for Bael'Zharon, and the image lived 45 s: it showed up
        /// late or not at all. After that first time it is drawn at once. So a staff member who knows the spell is shown
        /// both bodies once at login and at "@expspells teach", a twentieth of life size in the grass at their feet for
        /// 75 seconds, and the client has them ready before the first cast.</summary>
        internal static void WarmImages(Player p)
        {
            try
            {
                if (!Live || !Staff(p) || p.Location == null || !p.QuestManager.HasQuest(Stamp(VISITATION))) return;
                foreach (uint wcid in new[] { W_ASHERON, W_BAEL })
                {
                    if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature c)) continue;
                    c.Name = "Image";
                    c.ObjScale = (c.ObjScale ?? 1f) * 0.02f;
                    c.Invincible = true;
                    c.SetProperty(PropertyInt.RadarBlipColor, 0); c.SetProperty(PropertyInt.ShowableOnRadar, (int)RadarBehavior.ShowNever);
                    Show(c, Spot(p, wcid == W_BAEL ? 1.5f : -1.5f, 1.5f), 75);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: warm images: {e.Message}"); }
        }

        /// <summary>The image goes: its effect and sound now, the body a moment later.</summary>
        static void Depart(Player p, Creature c, bool bael)
        {
            if (c.IsDestroyed || c.Location == null) return;
            s_Images.TryRemove(new KeyValuePair<uint, Creature>(p.Guid.Full, c));
            // on the towering body only effects that scale well (the shadow funnel becomes house-sized sheets at x7): that one plays on the gate
            foreach (var fx in bael ? new[] { PlayScript.PortalStorm } : new[] { PlayScript.LevelUp, PlayScript.PortalStorm, PlayScript.AetheriaLevelUp })
                c.EnqueueBroadcast(new GameMessageScript(c.Guid, fx));
            var gate = Gate(c.Location, 4);
            if (gate != null) foreach (var fx in bael ? new[] { PlayScript.BaelZharonSmite, PlayScript.PortalStorm } : new[] { PlayScript.PortalStorm, PlayScript.LevelUp })
                gate.EnqueueBroadcast(new GameMessageScript(gate.Guid, fx));
            if (p.Location != null)
            {
                Flourish(p, c, bael, 6);
                Thunder(p, bael ? EnvironChangeType.Thunder3Sound : EnvironChangeType.BellSound);
            }
            var chain = new ActionChain();
            chain.AddDelaySeconds(1.6);
            chain.AddAction(WorldManager.ActionQueue, () => { try { if (!c.IsDestroyed) c.Destroy(); } catch { } });
            chain.EnqueueChain();
        }

        /// <summary>Owner 11-27: "when they spawn in they need an effect to appear. and they just stand there ... the spell
        /// doesnt work relaibly. and they also do nothing when they go." The image now arrives in an effect, speaks,
        /// gestures, casts something (Asheron) or menaces (Bael'Zharon), and leaves in an effect. Open sky only, said
        /// plainly; eight spots are tried so a tree, a wall or a slope in front of the caster does not refuse it; a new
        /// cast sends the standing image away first.</summary>
        static void Visitation(Player p)
        {
            if (p.Location.Indoors)
            {
                Say(p, "Visitation only works outdoors: the image is far too tall for a roof. Step outside under open sky and cast it again.");
                return;
            }
            bool bael = (Interlocked.Increment(ref s_Visits) & 1) == 0;   // Asheron first, then Bael'Zharon, and so on
            if (s_Images.TryRemove(p.Guid.Full, out var old)) Depart(p, old, old.WeenieClassId == W_BAEL);
            Creature? c = null;
            var f = Vector3.Transform(Vector3.UnitY, p.Location.Rotation);
            foreach (var (dist, turn) in new[] { (16f, 0f), (12f, 0f), (16f, 0.6f), (16f, -0.6f), (9f, 0f), (12f, 1.2f), (12f, -1.2f), (6f, 0f) })
            {
                if (!(WorldObjectFactory.CreateNewWorldObject(bael ? W_BAEL : W_ASHERON) is Creature made)) { Say(p, "The image will not come (its weenie is missing on this shard)."); return; }
                var d = Vector3.Transform(f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn));
                var at = Spot(p, d.X * dist, d.Y * dist);
                if (at.Indoors) { try { made.Destroy(); } catch { } continue; }
                var back = p.Location.ToGlobal() - at.ToGlobal();
                at.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Atan2(-back.X, back.Y));   // facing the caster
                made.Name = bael ? "Bael'Zharon" : "Asheron Realaidain";
                made.ObjScale = (made.ObjScale ?? 1f) * (bael ? 2.5f : 6f);
                made.Invincible = true;
                if (Show(made, at, VISIT_LIFE + 4)) { c = made; break; }
            }
            if (c == null)
            {
                Say(p, "Visitation found no open ground within 16 meters of you. Stand on open, level ground away from walls and water and cast it again.");
                Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: Visitation by {p.Name} at {p.Location}: no spot took the image");
                return;
            }
            s_Images[p.Guid.Full] = c;
            var image = c;

            // the arrival: a rift opens where it will stand and the storm turns over it, then the burst
            var gate = Gate(image.Location, 7);
            if (gate != null) foreach (var fx in bael ? new[] { PlayScript.PortalStorm, PlayScript.BaelZharonSmite } : new[] { PlayScript.PortalStorm, PlayScript.LevelUp })
                gate.EnqueueBroadcast(new GameMessageScript(gate.Guid, fx));
            foreach (var fx in bael ? new[] { PlayScript.PortalStorm } : new[] { PlayScript.PortalStorm, PlayScript.LevelUp, PlayScript.AetheriaLevelUp })
                image.EnqueueBroadcast(new GameMessageScript(image.Guid, fx));
            Thunder(p, EnvironChangeType.Thunder1Sound);
            Thunder(p, bael ? EnvironChangeType.DarkLaughSound : EnvironChangeType.BellSound);
            Flourish(p, image, bael, 8);
            foreach (var o in PlayersNear(p, SHOW_RANGE, true))
                Say(o, bael ? "The sky tears open, and the shadow of Bael'Zharon rises over you." : "The sky tears open in light, and a vast image of Asheron stands over you.");
            if (bael) { try { image.SetCombatMode(CombatMode.Melee); } catch { } }

            // what it does while it stands there (seconds after arriving)
            var chain = new ActionChain();
            double t = 0;
            void At(double when, Action what)
            {
                chain.AddDelaySeconds(when - t); t = when;
                chain.AddAction(WorldManager.ActionQueue, () => { try { if (!image.IsDestroyed && image.Location != null && p.Location != null) what(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: image: {e.Message}"); } });
            }
            if (bael)
            {
                At(2.5, () => { Speak(image, "You called, and the Hopeslayer answers."); Act(image, null, MotionCommand.AttackHigh1, MotionCommand.AttackMed1, MotionCommand.AttackLow1); });
                At(8, () => { Act(image, null, MotionCommand.AttackMed1, MotionCommand.AttackHigh1); Thunder(p, EnvironChangeType.RoarSound); ImageRing(p, image, RAVEN_FURY, 3f); });
                At(13, () =>
                {
                    Speak(image, "Kneel, or be unmade.");
                    Act(image, null, MotionCommand.AttackLow1, MotionCommand.AttackHigh1);
                    foreach (var o in PlayersNear(p, SHOW_RANGE, true)) o.EnqueueBroadcast(new GameMessageScript(o.Guid, PlayScript.BaelZharonSmite));   // the shadow funnel on each of them, for the look
                    Thunder(p, EnvironChangeType.Thunder2Sound);
                });
                At(19, () => { Act(image, null, MotionCommand.AttackHigh1, MotionCommand.AttackMed1); Flourish(p, image, true, 10); Thunder(p, EnvironChangeType.DarkLaughSound); });
                At(26, () => { Act(image, null, MotionCommand.AttackMed1, MotionCommand.AttackLow1); ImageRing(p, image, RAVEN_FURY, 3f); Thunder(p, EnvironChangeType.RoarSound); });
                At(33, () => { Speak(image, "Your light is a candle. I am the wind."); Act(image, null, MotionCommand.AttackHigh1, MotionCommand.AttackLow1); Flourish(p, image, true, 8); });
                At(VISIT_LIFE - 4, () => { Speak(image, "I leave you your lives. For now."); Thunder(p, EnvironChangeType.DarkLaughSound); });
            }
            else
            {
                At(2.5, () => { Speak(image, "Be at ease, children of Ispar. I am here."); Act(image, null, MotionCommand.Wave, MotionCommand.BowDeep); });
                At(8, () => { image.EnqueueBroadcastMotion(new Motion(MotionStance.Magic)); });
                At(9, () => { Act(image, MotionStance.Magic, MotionCommand.MagicPowerUp08Purple, MotionCommand.MagicPowerUp04, MotionCommand.MagicPowerUp02); image.EnqueueBroadcast(new GameMessageScript(image.Guid, PlayScript.EnchantUpWhite)); });
                At(11.5, () =>
                {
                    Speak(image, "Watch closely. This is how the old magic was meant to look.");
                    Act(image, MotionStance.Magic, MotionCommand.MagicBlast, MotionCommand.MagicPray, MotionCommand.MagicHeal);
                    ImageRing(p, image, LIGHTNING_RING, 8f); ImageRing(p, image, FORCE_RING, 5f);
                    Flourish(p, image, false, 10);
                    Thunder(p, EnvironChangeType.Thunder4Sound);
                });
                At(17, () => { Act(image, MotionStance.Magic, MotionCommand.MagicPowerUp08Purple, MotionCommand.MagicPowerUp04); });
                At(19, () => { Act(image, MotionStance.Magic, MotionCommand.MagicPray, MotionCommand.MagicBlast); ImageRing(p, image, LIGHTNING_RING, 6f); Flourish(p, image, false, 10); Thunder(p, EnvironChangeType.BellSound); });
                At(24, () => { image.EnqueueBroadcastMotion(new Motion(MotionStance.NonCombat)); });
                At(26, () => { Act(image, MotionStance.NonCombat, MotionCommand.Cheer, MotionCommand.ClapHands, MotionCommand.Wave); foreach (var o in PlayersNear(p, SHOW_RANGE, true)) o.EnqueueBroadcast(new GameMessageScript(o.Guid, PlayScript.EnchantUpWhite)); });
                At(33, () => { Act(image, MotionStance.NonCombat, MotionCommand.Salute, MotionCommand.BowDeep); Flourish(p, image, false, 6); });
                At(VISIT_LIFE - 4, () => { Speak(image, "Walk in the light until I return."); Act(image, MotionStance.NonCombat, MotionCommand.BowDeep, MotionCommand.Wave); });
            }
            At(VISIT_LIFE - 1.6, () => Depart(p, image, bael));
            chain.EnqueueChain();
        }

        // ------------------------------------------------------------------ Fowl Weather

        const int FOWL_COUNT = 14; const double FOWL_LIFE = 60, FOWL_TICK = 0.75;
        const float FOWL_LEASH = 30f, FOWL_HUNT = 60f;

        sealed class Flock
        {
            public List<Creature> Birds = new(); public List<float> Scale = new(); public HashSet<uint> Pecked = new();
            public bool Done; public uint Target; public DateTime Until;
            // round 4 (Olthoi Eruption): another body, one that hunts the nearest monster by itself and stays where it rose
            public uint Wcid = W_CHICKEN; public bool Auto, Stay;
            public List<Position> Home = new();   // a staying flock's own places: each goes back to its own between kills, so they do not stand in a heap
            public string Goes = "Your flock goes for {0}!", Hits = "Your flock pecks {0}.", Killed = "Your flock has pecked {0} to death.", Coming = "{0}'s chickens are coming for you!";
        }
        static readonly ConcurrentDictionary<uint, Flock> s_Flocks = new();   // caster -> their flock (one each)
        static readonly ConcurrentDictionary<uint, byte> s_Birds = new();     // every chicken of every flock
        static readonly MethodInfo? s_Selected = AccessTools.PropertyGetter(typeof(Player), "selectedTarget");

        /// <summary>One chicken beside the caster. Unattackable, walked through, and its own AI never runs (NoAttack): the
        /// flock is driven from FowlTick, so it can only ever go for what the caster picked.</summary>
        static Creature? Hatch(Player p, float scale, int life, uint wcid = W_CHICKEN, Position? where = null)
        {
            if (!(WorldObjectFactory.CreateNewWorldObject(wcid) is Creature c)) return null;
            double a = Rnd(0f, (float)(2 * Math.PI)); float r = Rnd(2f, 7f);
            var at = where ?? Spot(p, (float)Math.Cos(a) * r, (float)Math.Sin(a) * r);
            if (where == null) at.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Rnd(0f, (float)(2 * Math.PI)));
            c.ObjScale = (c.ObjScale ?? 1f) * scale;
            c.Invincible = true;
            if (!Show(c, at, life)) return null;
            s_Birds[c.Guid.Full] = 1;
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.Create));
            return c;
        }

        /// <summary>What the flock goes for: the creature the caster has selected (or is attacking). A monster that can be
        /// attacked; a player only after the caster typed "@expspells players on"; never the caster, an NPC, a pet or a chicken.</summary>
        static Creature? FowlTarget(Player p)
        {
            WorldObject? wo = null;
            try { wo = (s_Selected?.Invoke(p, null) as WorldObjectInfo)?.TryGetWorldObject(); } catch { }
            if (!(wo is Creature)) wo = p.AttackTarget;
            if (!(wo is Creature c) || c == p || !c.IsAlive || c.Location == null || c is CombatPet || s_Birds.ContainsKey(c.Guid.Full)) return null;
            if (c.Location.DistanceTo(p.Location) > FOWL_HUNT) return null;
            if (c is Player) return s_GalePlayers.ContainsKey(p.Guid.Full) ? c : null;
            return c.Attackable ? c : null;
        }

        static void RunTo(Creature c, Position centre, float rMin, float rMax)
        {
            double a = Rnd(0f, (float)(2 * Math.PI)); float r = Rnd(rMin, rMax);
            var to = centre.FromGlobal(centre.ToGlobal() + new Vector3((float)Math.Cos(a) * r, (float)Math.Sin(a) * r, 0f));
            if (!to.Indoors) to.PositionZ = to.GetTerrainZ();
            try { c.MoveTo(to, 4.0f, true, null, 2.0f); } catch { }
        }

        static void Scatter(Player p, Flock f, string? why)
        {
            if (f.Done) return;
            f.Done = true;
            s_Flocks.TryRemove(new KeyValuePair<uint, Flock>(p.Guid.Full, f));
            s_Swarms.TryRemove(new KeyValuePair<uint, Flock>(p.Guid.Full, f));
            foreach (var c in f.Birds)
            {
                s_Birds.TryRemove(c.Guid.Full, out _);
                if (c.IsDestroyed) continue;
                try { c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.Destroy)); } catch { }
            }
            var chain = new ActionChain();
            chain.AddDelaySeconds(0.7);
            chain.AddAction(WorldManager.ActionQueue, () => { foreach (var c in f.Birds) { try { if (!c.IsDestroyed) c.Destroy(); } catch { } } });
            chain.AddDelaySeconds(30);   // a player they killed drops nothing; the corpse is made a few seconds after the death
            chain.AddAction(WorldManager.ActionQueue, () => { foreach (var id in f.Pecked) s_NoDrop.TryRemove(id, out _); });
            chain.EnqueueChain();
            if (why != null) Say(p, why);
        }

        /// <summary>Owner 11-27: "says they run around me but when i moved they didnt follow. id rather they attack for me.
        /// players and monsters." The flock follows the caster and pecks whatever the caster has selected.</summary>
        static void Fowl(Player p)
        {
            if (p.Location.Indoors) { Say(p, "Fowl Weather only works outdoors. Step outside under open sky and cast it again."); return; }
            if (s_Flocks.TryRemove(p.Guid.Full, out var old)) Scatter(p, old, null);
            var f = new Flock { Until = DateTime.UtcNow.AddSeconds(FOWL_LIFE) };
            for (int i = 0; i < FOWL_COUNT; i++)
            {
                float scale = i == 0 ? 6f : Rnd(0.6f, 1.8f);   // one of them is enormous
                var c = Hatch(p, scale, (int)FOWL_LIFE + 5);
                if (c == null) continue;
                f.Birds.Add(c); f.Scale.Add(scale);
            }
            if (f.Birds.Count == 0) { Say(p, "Fowl Weather found no open ground around you for the chickens. Move to open, level ground and cast it again."); return; }
            s_Flocks[p.Guid.Full] = f;
            int ticks = (int)(FOWL_LIFE / FOWL_TICK);
            var chain = new ActionChain();
            for (int t = 0; t < ticks; t++)
            {
                int n = t;
                chain.AddDelaySeconds(FOWL_TICK);
                chain.AddAction(WorldManager.ActionQueue, () => { try { FowlTick(p, f, n); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: flock: {e.Message}"); } });
            }
            chain.AddAction(WorldManager.ActionQueue, () => Scatter(p, f, "The flock scatters."));
            chain.EnqueueChain();
            foreach (var o in PlayersNear(p, SHOW_RANGE, true)) Say(o, "Fowl weather rolls in.");
            Say(p, $"The flock follows you for a minute. Select a monster{(s_GalePlayers.ContainsKey(p.Guid.Full) ? " or a player" : "")} and they attack it.");
        }

        static void FowlTick(Player p, Flock f, int n)
        {
            if (f.Done) return;
            if (p.Session == null || p.Location == null || p.PhysicsObj == null || !p.IsAlive) { Scatter(p, f, null); return; }
            if (p.Location.Indoors || p.Teleporting) return;   // they wait outside, and catch up when the caster is back under the sky
            var target = FowlTarget(p) ?? (f.Auto ? Prey(p, f) : null);
            if (target != null && target.Guid.Full != f.Target)
            {
                f.Target = target.Guid.Full;
                Say(p, string.Format(f.Goes, target.Name));
                if (target is Player tp) Say(tp, string.Format(f.Coming, p.Name));
            }
            else if (target == null) f.Target = 0;
            int life = Math.Max(5, (int)(f.Until - DateTime.UtcNow).TotalSeconds + 5);
            uint dealt = 0;
            for (int i = 0; i < f.Birds.Count; i++)
            {
                var c = f.Birds[i];
                bool lost = c.IsDestroyed || c.Location == null || c.PhysicsObj == null;
                // the caster ran, recalled or was moved too far for a chicken to catch up: it comes in again beside them
                if (f.Stay) { if (lost) continue; }
                else if (lost || c.Location!.Landblock != p.Location.Landblock && c.Location.DistanceTo(p.Location) > FOWL_LEASH || c.Location.DistanceTo(p.Location) > FOWL_LEASH * 2f)
                {
                    s_Birds.TryRemove(c.Guid.Full, out _);
                    try { if (!c.IsDestroyed) c.Destroy(); } catch { }
                    var again = Hatch(p, f.Scale[i], life, f.Wcid);
                    if (again != null) f.Birds[i] = again;
                    continue;
                }
                bool turn = (n + i) % 2 == 0;   // each chicken acts every other tick, half the flock at a time
                if (target != null && target.IsAlive && target.Location != null)
                {
                    float reach = 1.6f + 0.5f * f.Scale[i] + (target.PhysicsObj?.GetPhysicsRadius() ?? 0.5f);
                    float gap = c.Location.DistanceTo(target.Location);
                    if (gap > reach) { if (turn) RunTo(c, target.Location, 0.3f, Math.Max(0.6f, reach - 0.8f)); continue; }
                    if (!turn) continue;
                    Act(c, null, MotionCommand.AttackHigh1, MotionCommand.AttackMed1, MotionCommand.AttackLow1, MotionCommand.Twitch1, MotionCommand.Twitch2);
                    // a peck is a share of the target's health, so the flock fells a drudge and a tusker alike in a few seconds
                    float share = (f.Scale[i] > 3f ? 0.08f : Rnd(0.02f, 0.04f)) * (target is Player ? 0.5f : 1f);
                    float amount = Math.Max(target is Player ? 4f : 15f, target.Health.MaxValue * share);
                    if (target is Player victim)
                    {
                        s_NoDrop[victim.Guid.Full] = 1; f.Pecked.Add(victim.Guid.Full);
                        dealt += (uint)Math.Max(0, victim.TakeDamage(c, DamageType.Pierce, amount, BodyPart.Chest));
                    }
                    else dealt += target.TakeDamage(p, DamageType.Pierce, amount);
                    if (!target.IsAlive) { Say(p, string.Format(f.Killed, target.Name)); break; }
                }
                else if (turn && !f.Stay && c.Location.DistanceTo(p.Location) > 5f) RunTo(c, p.Location, 1.5f, 4.5f);
                else if (turn && f.Stay && i < f.Home.Count && c.Location.DistanceTo(f.Home[i]) > 2.5f) { try { c.MoveTo(f.Home[i], 4.0f, true, null, 2.0f); } catch { } }
            }
            if (dealt > 0 && n % 4 == 0 && target != null && target.IsAlive) Say(p, string.Format(f.Hits, target.Name));
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsCollide
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(SpellProjectile), nameof(SpellProjectile.OnCollideObject), new[] { typeof(WorldObject) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookCollide = TargetMethod() != null);
        static bool Prefix(SpellProjectile __instance, WorldObject target)
        {
            try { return ExperimentalSpells.CollidePrefix(__instance, target); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: collide: {e.Message}"); return false; }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsSureCast
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.GetCastingPreCheckStatus), new[] { typeof(Spell), typeof(uint), typeof(bool) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static bool Prefix(Player __instance, Spell spell, ref Player.CastingPreCheckStatus __result)
        {
            try
            {
                if (!ExperimentalSpells.SureCast(__instance, spell)) return true;
                __result = Player.CastingPreCheckStatus.Success;
                return false;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: sure cast: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsAnyTarget
    {
        // ACE refuses a helpful spell (the targeted staff spells wear Heal Other's row) on a monster; the staff ones go through
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "IsInvalidTarget", new[] { typeof(Spell), typeof(WorldObject) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Player __instance, Spell spell, WorldObject target, ref bool __result)
        {
            try { if (__result && target is Creature && ExperimentalSpells.SureCast(__instance, spell)) __result = false; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: target: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsAim
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.HandleActionCastTargetedSpell), new[] { typeof(uint), typeof(uint), typeof(WorldObject) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static bool Prefix(Player __instance, uint targetGuid, uint spellId)
        {
            try { return ExperimentalSpells.AimPrefix(__instance, targetGuid, spellId); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: aim: {e.Message}"); return true; }
        }
    }

    /// <summary>ACE refuses a helpful spell (the targeted staff spells wear Heal Other's row) between players of different
    /// player-killer kinds ("You fail to affect X because you are not the same sort of player killer"): staff are not
    /// PKs, so a PK or PK-lite player could not be aimed at. The staff spells go through.</summary>
    [HarmonyPatch]
    static class ExperimentalSpellsAnyKiller
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.CheckPKStatusVsTarget), new[] { typeof(WorldObject), typeof(Spell) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Player __instance, Spell spell, ref List<WeenieErrorWithString> __result)
        {
            try { if (__result != null && ExperimentalSpells.SureCast(__instance, spell)) __result = null!; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: killer kind: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsSoftLanding
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.TakeDamage_Falling), new[] { typeof(float) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static bool Prefix(Player __instance)
        {
            try { return ExperimentalSpells.FallPrefix(__instance); } catch { return true; }
        }
    }

    [HarmonyPatch]
    static class ExperimentalSpellsNoDrop
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.CalculateDeathItems), new[] { typeof(Corpse) });
        static bool Prepare(MethodBase original) => original != null || (ExperimentalSpells.HookDrop = TargetMethod() != null);
        static bool Prefix(Player __instance, ref List<WorldObject> __result)
        {
            try { return ExperimentalSpells.DropPrefix(__instance, ref __result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ExperimentalSpells: drop: {e.Message}"); return true; }
        }
    }

    /// <summary>A staff spell takes a retail level VII formula for its gestures; staff never need those components...</summary>
    [HarmonyPatch]
    static class ExperimentalSpellsNoComps
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.HasComponentsForSpell), new[] { typeof(Spell) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Player __instance, Spell spell, ref bool __result)
        {
            try { if (!__result && ExperimentalSpells.SureCast(__instance, spell)) __result = true; } catch { }
        }
    }

    /// <summary>...and never burn them.</summary>
    [HarmonyPatch]
    static class ExperimentalSpellsNoBurn
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.TryBurnComponents), new[] { typeof(Spell) });
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static bool Prefix(Player __instance, Spell spell)
        {
            try { return !ExperimentalSpells.SureCast(__instance, spell); } catch { return true; }
        }
    }
}
