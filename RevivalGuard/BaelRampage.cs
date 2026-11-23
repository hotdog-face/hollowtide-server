using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BAEL'ZHARON'S RAMPAGE, REVIVED (retail Dec 2000, Fourth Sending; owner 2026-11-21 "Yes, build it"; docs/EVENT-TESTS/
    /// bz-rampage.md). Nov 24 to 30, leading into Lightbringer's Week. Retail's was a developer at the keyboard; ours is
    /// scheduled. What he says and casts is only what Turbine's own video of his client shows
    /// (docs/findings/event-video-transcripts-2026-11-21.md section 2).
    ///
    ///   WHEN    the Event Director runs one "bz-&lt;town&gt;" entry (EventDirector catalogue, festival "bzrampage") at most
    ///           once every 10 hours (EventDirectorRules.BZ_APART_S; about two a day), never while the world boss Bael'Zharon is up.
    ///   ARRIVE  he appears in the town's square (8503's retail body, level 899, XP 0) in a portal storm, says his line
    ///           ("My master has eaten worlds...") and his darkness falls on the town.
    ///   IDLE    nobody hitting him: retail's joke emotes (rest, teapot, YMCA, snow angel, curtsey, bow) now and then, his
    ///           line again every few minutes. He never starts a fight and never moves.
    ///   STRUCK  he cannot be hurt (Invincible: blows are evaded, spells resisted, as in the video). Whoever strikes him is
    ///           cursed in turn: Demon Fists, Sparking Fury, Electric Blaze, Electric Discharge, Fuming Acid, Weight of
    ///           Eternity (not Portal to Teth: see CURSES); "Face me, weaklings!" when a fight starts. His spells kill, as
    ///           retail's did (rg_bz_rampage_lethal, default on; off leaves a struck player at 1 health).
    ///   LEAVE   the director's time is up (30 minutes) or it is stopped: he vanishes in black madness.
    /// No loot, no corpse, no XP, nothing saved. Off by default: rg_bz_rampage (the admin panel's Events buttons) and the
    /// datafix's rows (2026-11-24-bz-rampage-1-rows.sql) both needed.
    ///   @rampage [status | on | off | visit [town] | leave]     (Admin)
    /// </summary>
    static class BaelRampage
    {
        internal const string FESTIVAL = "bzrampage", P_ON = "rg_bz_rampage", P_LETHAL = "rg_bz_rampage_lethal", KEY_PREFIX = "bz-";
        const uint BODY = 8503;            // retail Bael'Zharon, never placed: level 899, war magic 900, XP 0, his spellbook
        const float RANGE = 60f;           // who hears him and who may be cursed
        const double TICK_S = 0.5, FOE_FORGET_S = 30, CAST_S = 4.0, CAST_WIND_S = 1.2, LINE_EVERY_S = 6 * 60,
                     TAUNT_EVERY_S = 120, JOKE_MIN_S = 50, JOKE_MAX_S = 90, RESPAWN_TRY_S = 20, MUSE_MIN_S = 100, MUSE_MAX_S = 160;

        // the video's lines (transcript section 2): the monologue verbatim; the taunt's start had scrolled off ("...face me,
        // weaklings!"); the green line only its first sentence (its middle is unreadable)
        internal const string MONOLOGUE = "My master has eaten worlds. It has sucked the light from stars. Its power is in me. You have no chance, children.";
        internal const string TAUNT = "Face me, weaklings!";

        // OURS, NOT RETAIL (owner 2026-11-21 "Fill it out"; he reviews every one before live): more of him, in the voice of
        // the recorded lines (the video's comic, condescending Hopeslayer), so a 30-minute visit does not repeat one line.
        internal static readonly string[] MUSINGS =
        {
            "So this is where the little ones hide. How small you are.",
            "I have walked between the stars. Your walls do not impress me.",
            "The Shadows sing of this place. They are hungry.",
            "Run to your lifestones, children. I will still be here.",
            "Such a pleasant town. It smells of fear, mostly.",
            "Asheron cannot save you. He could not even save his own world.",
            "Kneel, or do not. It changes nothing.",
            "I came to make friends. Why does everyone run?",
        };
        internal static readonly string[] STRUCK =
        {
            "You strike at the Hopeslayer? Bold. Foolish, but bold.",
            "Is that all? My master's crumbs bite harder.",
            "Another one. Come, then.",
        };
        internal const string FAREWELL = "Enough. I have other children to visit.";
        internal const string DARKNESS = "Bael'Zharon's whispering darkness falls upon you.";

        // his joke emotes, retail's first-person lines in his client turned to what onlookers read
        static readonly (MotionCommand motion, string text, bool state)[] JOKES =
        {
            (MotionCommand.SitState, "decides to rest for a while.", true),        // "You decide to rest for a while."
            (MotionCommand.Teapot, "gestures, 'I'm a little teapot!'", false),     // "You gesture, 'I'm a little teapot!'"
            (MotionCommand.YMCA, "cheers, 'YMCA!'", false),                         // "You cheer, 'YMCA!'"
            (MotionCommand.SnowAngelState, "makes a snow angel.", true),           // "You make a snow angel."
            (MotionCommand.CurtseyState, "curtseys.", true),                       // "You curtsey."
            (MotionCommand.BowDeep, "bows deeply.", false),                         // "You bow deeply."
        };

        // his curses, weighted: the video's six (cast-button labels)
        static readonly (uint id, int weight)[] CURSES =
        {
            (2045, 4),   // Demon Fists
            (2039, 4),   // Sparking Fury
            (2032, 2),   // Electric Blaze
            (2035, 2),   // Electric Discharge
            (2036, 2),   // Fuming Acid
            (2043, 2),   // Weight of Eternity
            // Portal to Teth (2046, the wiki's) is left out: retail's landing point is 770 m up, and with FallGuard off on live
            // (checked 2026-11-21) the drop cost a test character 180 health, enough to kill a new one
        };

        internal sealed class Town { public string Key, Name; public float Ns, Ew; }
        // Fort Tethana, Kara and Arwic are the wiki's; the other thirteen are ours (retail said "and more"). Map points:
        // Assets/StreamingAssets/map-places.json "town" entries.
        internal static readonly Town[] TOWNS =
        {
            new Town { Key = "fort-tethana", Name = "Fort Tethana", Ns = 1.6f,   Ew = -71.8f },
            new Town { Key = "kara",         Name = "Kara",         Ns = -83.3f, Ew = 47.1f },
            new Town { Key = "arwic",        Name = "Arwic",        Ns = 33.6f,  Ew = 56.8f },
            new Town { Key = "cragstone",    Name = "Cragstone",    Ns = 25.6f,  Ew = 48.8f },
            new Town { Key = "zaikhal",      Name = "Zaikhal",      Ns = 13.7f,  Ew = 0.6f },
            new Town { Key = "shoushi",      Name = "Shoushi",      Ns = -33.5f, Ew = 72.8f },
            new Town { Key = "yaraq",        Name = "Yaraq",        Ns = -21.5f, Ew = -1.6f },
            // ours too (owner 2026-11-21 "Fill it out"): more of Dereth's towns, all levels, Holtburg (owner 2026-11-21) away from its lifestone and raid points
            new Town { Key = "hebian-to",    Name = "Hebian-To",    Ns = -39.1f, Ew = 83.2f },
            new Town { Key = "rithwic",      Name = "Rithwic",      Ns = 10.6f,  Ew = 58.3f },
            new Town { Key = "al-arqas",     Name = "Al-Arqas",     Ns = -31.3f, Ew = 13.6f },
            new Town { Key = "glenden-wood", Name = "Glenden Wood", Ns = 29.7f,  Ew = 27.2f },
            new Town { Key = "sawato",       Name = "Sawato",       Ns = -28.7f, Ew = 59.3f },
            new Town { Key = "tufa",         Name = "Tufa",         Ns = -14.2f, Ew = 5.4f },
            new Town { Key = "lytelthorpe",  Name = "Lytelthorpe",  Ns = 0.6f,   Ew = 51.2f },
            new Town { Key = "samsur",       Name = "Samsur",       Ns = -2.9f,  Ew = 19.5f },
            // owner 2026-11-21 "add holtberg": (84, 75) in A9B4 metres, 63 m north of the lifestone where new players arrive
            // (TownRaid.json noSpawnNearLifestone 60) and 60 m or more from every raid side's point in town; a visit and a
            // Holtburg raid never overlap (EventDirector.Blocked waits on TownRaid.Running; TownRaid pauses on "bz-holtburg")
            new Town { Key = "holtburg",     Name = "Holtburg",     Ns = 42.3625f, Ew = 33.6f },
        };
        internal static Town TownOf(string directorKey) =>
            directorKey != null && directorKey.StartsWith(KEY_PREFIX, StringComparison.OrdinalIgnoreCase)
                ? TOWNS.FirstOrDefault(t => t.Key.Equals(directorKey.Substring(KEY_PREFIX.Length), StringComparison.OrdinalIgnoreCase)) : null;

        internal static bool SwitchOn { get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } } }
        static bool Lethal { get { try { return PropertyManager.GetBool(P_LETHAL, true).Item; } catch { return true; } } }
        static double Now => ACE.Common.Time.GetUnixTime();

        sealed class Visit
        {
            public string Key; public Town Town;
            public Creature Bael;
            public bool Greeted;
            public double NextLineAt, NextJokeAt, NextCastAt, NextSpawnTry, LastTauntAt = -1e9, DarknessAt, ReadyAt;
            public Spell PendingSpell; public Player PendingTarget; public double PendingAt;
            public int CastRr, Casts, Jokes, Spawns, Muses, Taunts;
            public double NextMuseAt;
        }
        static Visit s_Visit;
        static int s_Muse = Random.Shared.Next(64);   // carries on across visits, from a different line after each restart
        static readonly List<(Creature c, double fxAt, double goneAt)> s_Exits = new List<(Creature, double, double)>();
        static volatile uint s_BaelGuid;   // the one Bael'Zharon this module put in the world (0: none)
        static double s_NextTick;
        static readonly ConcurrentDictionary<uint, (double at, WeakReference<Player> who)> s_Foes = new ConcurrentDictionary<uint, (double, WeakReference<Player>)>();
        static int s_Struck;

        internal static bool IsOurs(WorldObject wo) => wo != null && s_BaelGuid != 0 && wo.Guid.Full == s_BaelGuid;

        // ---------------------------------------------------------------- start up

        internal static void Register()
        {
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(false, "Bael'Zharon's Rampage (Nov 24 to 30): his town visits; off until staff switch it on (admin panel, Events)");
                if (!b.ContainsKey(P_LETHAL)) b[P_LETHAL] = new Property<bool>(true, "Bael'Zharon's Rampage: his curses on those who strike him can kill (retail); off leaves them at 1 health");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BaelRampage: switches not registered with @modifybool ({e.Message}); they are still read"); }
            CommandManager.TryAddCommand(Cmd, "rampage", AccessLevel.Admin, CommandHandlerFlag.None,
                "Bael'Zharon's Rampage (Nov 24 to 30): status; on/off (the season's switch); visit [town] starts a visit now; leave ends it.",
                "[status | on | off | visit [town] | leave | curse]");
            Mod.Log.Info($"[RevivalGuard] BaelRampage: {TOWNS.Length} towns, switch {P_ON} {(SwitchOn ? "ON" : "off")}; @rampage");
        }

        // ---------------------------------------------------------------- the tick (world thread)

        internal static void Tick()
        {
            var now = Now;
            if (now < s_NextTick) return;
            s_NextTick = now + TICK_S;
            if (s_Exits.Count > 0) Exits(now);

            var cur = EventDirector.CurrentEvent();
            var town = cur.HasValue ? TownOf(cur.Value.key) : null;
            // a shutdown countdown: he goes quietly (nothing of his is saved), and comes back if it is cancelled
            if (ServerManager.ShutdownInitiated) town = null;

            if (s_Visit != null && (town == null || !s_Visit.Key.Equals(cur.Value.key, StringComparison.OrdinalIgnoreCase)))
            {
                Leave(s_Visit, quiet: ServerManager.ShutdownInitiated);
                s_Visit = null;
            }
            if (town == null) return;
            if (s_Visit == null)
            {
                s_Visit = new Visit { Key = cur.Value.key, Town = town };
                s_Foes.Clear();
                Mod.Log.Info($"[RevivalGuard] BaelRampage: visit {town.Name} until {DateTimeOffset.FromUnixTimeSeconds((long)cur.Value.endsAt).UtcDateTime:HH:mm} UTC");
            }
            Run(s_Visit, now);
        }

        static void Run(Visit v, double now)
        {
            var c = v.Bael;
            if (c == null || c.IsDestroyed || c.IsDead || c.CurrentLandblock == null)
            {
                if (c != null && c.CurrentLandblock == null && !c.IsDestroyed && now < v.NextSpawnTry) return;   // entering
                v.Bael = null; s_BaelGuid = 0;
                // gone (his landblock unloaded with nobody there): back when someone comes near the town
                if (now < v.NextSpawnTry) return;
                v.NextSpawnTry = now + RESPAWN_TRY_S;
                // only into a landblock that is loaded with someone near: a spawn at boot into an unloaded block hung a
                // landblock worker at 100% on shard2 (2026-11-21), so he waits for the first player the broadcast brings
                if (AnyoneNear(v.Town) && LandblockManager.IsLoaded(new LandblockId(Ground(v.Town.Ns, v.Town.Ew).LandblockId.Raw))) Arrive(v, now);
                return;
            }

            // the darkness line follows his first words
            if (v.DarknessAt > 0 && now >= v.DarknessAt) { v.DarknessAt = 0; Emote(c, DARKNESS); }
            // a held pose (snow angel, curtsey, rest) ends
            if (v.ReadyAt > 0 && now >= v.ReadyAt) { v.ReadyAt = 0; c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.Ready)); }

            // a curse wound up a moment ago lands now
            if (v.PendingSpell != null && now >= v.PendingAt)
            {
                var sp = v.PendingSpell; var t = v.PendingTarget;
                v.PendingSpell = null; v.PendingTarget = null;
                if (Cursable(c, t))
                {
                    try { c.TryCastSpell(sp, t); v.Casts++; }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BaelRampage: {sp.Name} on {t.Name}: {e.Message}"); }
                    Mod.Log.Info($"[RevivalGuard] BaelRampage: {sp.Name} on {t.Name}");
                }
            }

            var foes = Foes(c, now);
            if (foes.Count > 0)
            {
                if (now - v.LastTauntAt >= TAUNT_EVERY_S)
                {
                    v.LastTauntAt = now;
                    // the recorded taunt first in each visit, then ours in turn
                    Speak(c, v.Taunts == 0 ? TAUNT : STRUCK[(v.Taunts - 1) % STRUCK.Length]);
                    v.Taunts++;
                }
                if (now >= v.NextCastAt && v.PendingSpell == null)
                {
                    var t = foes[v.CastRr++ % foes.Count];
                    var sp = PickCurse();
                    if (v.ReadyAt > 0) { v.ReadyAt = 0; c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.Ready)); }
                    try { c.Rotate(t); } catch { }
                    c.EnqueueBroadcastMotion(new Motion(c, MotionCommand.CastSpell));
                    v.PendingSpell = sp; v.PendingTarget = t; v.PendingAt = now + CAST_WIND_S;
                    v.NextCastAt = now + CAST_S;
                }
                v.NextJokeAt = Math.Max(v.NextJokeAt, now + JOKE_MIN_S / 2);
                return;
            }

            // quiet: his line now and then, and the jokes between
            if (now >= v.NextLineAt) { v.NextLineAt = now + LINE_EVERY_S; v.NextMuseAt = Math.Max(v.NextMuseAt, now + MUSE_MIN_S); Speak(c, MONOLOGUE); return; }
            if (v.NextMuseAt > 0 && now >= v.NextMuseAt)
            {
                v.NextMuseAt = now + MUSE_MIN_S + Random.Shared.NextDouble() * (MUSE_MAX_S - MUSE_MIN_S);
                v.NextJokeAt = Math.Max(v.NextJokeAt, now + 20);
                v.Muses++; Speak(c, MUSINGS[s_Muse++ % MUSINGS.Length]);
                return;
            }
            if (now >= v.NextJokeAt)
            {
                v.NextJokeAt = now + JOKE_MIN_S + Random.Shared.NextDouble() * (JOKE_MAX_S - JOKE_MIN_S);
                var j = JOKES[v.Jokes++ % JOKES.Length];
                c.EnqueueBroadcastMotion(new Motion(c, j.motion));
                c.EnqueueBroadcast(new GameMessageEmoteText(c.Guid.Full, c.Name, j.text), RANGE);
                if (j.state) v.ReadyAt = now + 6;
            }
        }

        // ---------------------------------------------------------------- arrive, leave

        /// <summary>Outdoor ground at a map point (NS/EW, 240 m a unit, landblocks of 192 m from 101.95S/W).</summary>
        internal static Position Ground(float ns, float ew, float dx = 0, float dy = 0)
        {
            float gx = (ew + 101.95f) * 240f + dx, gy = (ns + 101.95f) * 240f + dy;
            int bx = (int)Math.Floor(gx / 192f), by = (int)Math.Floor(gy / 192f);
            float lx = gx - bx * 192f, ly = gy - by * 192f;
            uint cell = (uint)((int)(lx / 24) * 8 + (int)(ly / 24) + 1);
            var p = new Position((uint)(bx << 24 | by << 16) | cell, lx, ly, 0, 0, 0, 0, 1);
            try { p.PositionZ = p.GetTerrainZ(); } catch { }
            return p;
        }

        static void Arrive(Visit v, double now)
        {
            if (!(WorldObjectFactory.CreateNewWorldObject(BODY) is Creature c)) { Mod.Log.Warn($"[RevivalGuard] BaelRampage: weenie {BODY} missing"); return; }
            // a visitor, not a monster: he can be struck (and evades it), never hurt, never starts a fight, never rots or saves
            c.SetProperty(PropertyBool.Attackable, true);
            c.Invincible = true;
            c.SetProperty(PropertyInt.Tolerance, (int)Tolerance.NoAttack);
            c.SetProperty(PropertyBool.NoCorpse, true);
            c.TimeToRot = -1;
            c.Lifespan = 3 * 3600;   // gone by himself if a leave is ever missed
            try { c.EquipInventoryItems(); } catch { }
            var at = Ground(v.Town.Ns, v.Town.Ew);
            at.Rotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, (float)(Random.Shared.NextDouble() * Math.PI * 2));
            c.Location = new Position(at);
            c.Location.PositionZ += 0.05f;
            bool ok;
            try { ok = c.EnterWorld(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BaelRampage: enter: {e.Message}"); ok = false; }
            if (!ok) { try { c.Destroy(); } catch { } Mod.Log.Warn($"[RevivalGuard] BaelRampage: could not enter {v.Town.Name} at {at}"); return; }
            v.Bael = c; v.Spawns++;
            s_BaelGuid = c.Guid.Full;
            c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.PortalStorm));
            Mod.Log.Info($"[RevivalGuard] BaelRampage: arrived in {v.Town.Name} at {c.Location} (indoors {c.Location.Indoors}){(v.Greeted ? " (back: someone came near)" : "")}");
            if (!v.Greeted)
            {
                v.Greeted = true;
                v.NextLineAt = now + 4;     // his words a moment after the storm
                v.DarknessAt = now + 10;
                v.NextJokeAt = now + 40;
                v.NextMuseAt = now + 75;
            }
            else v.NextJokeAt = now + 20;
        }

        static void Leave(Visit v, bool quiet)
        {
            var c = v.Bael;
            s_BaelGuid = 0;
            s_Foes.Clear();
            Mod.Log.Info($"[RevivalGuard] BaelRampage: left {v.Town.Name} ({v.Casts} curses, {v.Spawns} arrivals){(quiet ? " (shutdown)" : "")}");
            if (c == null || c.IsDestroyed) return;
            if (quiet || c.CurrentLandblock == null) { try { c.Destroy(); } catch { } return; }
            Speak(c, FAREWELL);
            var now = Now;
            s_Exits.Add((c, now + 2.5, now + 4.0));   // run by the tick (Exits), not an ActionChain on him
        }

        /// <summary>His exits in progress: black madness, then gone.</summary>
        static void Exits(double now)
        {
            for (int i = s_Exits.Count - 1; i >= 0; i--)
            {
                var (c, fxAt, goneAt) = s_Exits[i];
                try
                {
                    if (c.IsDestroyed) { s_Exits.RemoveAt(i); continue; }
                    if (fxAt > 0 && now >= fxAt)
                    {
                        c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.BlackMadness));
                        c.EnqueueBroadcast(new GameMessageScript(c.Guid, PlayScript.DisappearDestroy));
                        s_Exits[i] = (c, 0, goneAt);
                    }
                    else if (now >= goneAt) { c.Destroy(); s_Exits.RemoveAt(i); }
                }
                catch { s_Exits.RemoveAt(i); }
            }
        }

        // ---------------------------------------------------------------- who struck him

        /// <summary>A player's blow or spell reached him (DamageEvent, OnAttackMonster, TryResistSpell).</summary>
        internal static void Struck(Player p)
        {
            if (p == null || p.IsDead) return;
            s_Foes[p.Guid.Full] = (Now, new WeakReference<Player>(p));
            System.Threading.Interlocked.Increment(ref s_Struck);
        }

        static List<Player> Foes(Creature c, double now)
        {
            var list = new List<Player>();
            foreach (var kv in s_Foes)
            {
                if (now - kv.Value.at > FOE_FORGET_S || !kv.Value.who.TryGetTarget(out var p) || !Cursable(c, p)) { s_Foes.TryRemove(kv.Key, out _); continue; }
                list.Add(p);
            }
            return list;
        }

        static bool Cursable(Creature c, Player p) =>
            p != null && !p.IsDead && p.Session != null && p.Location != null && c?.Location != null
            && p.CurrentLandblock != null && p.Location.DistanceTo(c.Location) <= RANGE;

        static readonly ConcurrentDictionary<uint, Spell> s_Spells = new ConcurrentDictionary<uint, Spell>();
        static Spell PickCurse()
        {
            int total = CURSES.Sum(x => x.weight), roll = Random.Shared.Next(total);
            foreach (var (id, w) in CURSES) { if (roll < w) return s_Spells.GetOrAdd(id, i => new Spell(i)); roll -= w; }
            return s_Spells.GetOrAdd(CURSES[0].id, i => new Spell(i));
        }

        /// <summary>His spells kill, as retail's did; with rg_bz_rampage_lethal off a struck player keeps 1 health.</summary>
        internal static void CapSpell(Player target, ref float damage)
        {
            if (Lethal || target == null) return;
            float room = Math.Max(0, target.Health.Current - 1);
            if (damage > room) damage = room;
        }

        // ---------------------------------------------------------------- words

        static IEnumerable<Player> Near(WorldObject c) =>
            PlayerManager.GetAllOnline().Where(p => p?.Location != null && c.Location != null && p.CurrentLandblock != null
                && p.Location.DistanceTo(c.Location) <= RANGE * 2);

        static bool AnyoneNear(Town t)
        {
            var at = Ground(t.Ns, t.Ew);
            int x = (int)at.LandblockX, y = (int)at.LandblockY;
            return PlayerManager.GetAllOnline().Any(p => p?.Location != null && !p.Location.Indoors
                && Math.Abs((int)p.Location.LandblockX - x) <= 1 && Math.Abs((int)p.Location.LandblockY - y) <= 1);
        }

        static void Speak(Creature c, string text)
        {
            c.EnqueueBroadcast(new GameMessageHearSpeech(text, c.Name, c.Guid.Full, ChatMessageType.Speech), RANGE * 2);
            Mod.Log.Info($"[RevivalGuard] BaelRampage says: {text}");
        }

        static void Emote(Creature c, string text)
        {
            foreach (var p in Near(c)) p.Session?.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Emote));
            Mod.Log.Info($"[RevivalGuard] BaelRampage emote: {text}");
        }

        // ---------------------------------------------------------------- @rampage

        static void Say(Session s, string text)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static void Cmd(Session session, params string[] parameters)
        {
            string verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            string who = session?.Player?.Name ?? "console";
            switch (verb)
            {
                case "on":
                case "off":
                    PropertyManager.ModifyBool(P_ON, verb == "on");
                    Mod.Log.Info($"[RevivalGuard] BaelRampage: switched {verb} by {who}");
                    Say(session, verb == "on"
                        ? "[Rampage] on. While the calendar has it (Nov 24 to 30) and its rows are in ace_world, the Event Director sends Bael'Zharon to a town about twice a day; 'visit' sends him now."
                        : "[Rampage] off. A visit running now ends within a minute (the festival closes; the director withdraws him).");
                    return;
                case "visit":
                    {
                        Town t = parameters.Length > 1 ? TOWNS.FirstOrDefault(x => x.Key.StartsWith(parameters[1], StringComparison.OrdinalIgnoreCase) || x.Name.StartsWith(parameters[1], StringComparison.OrdinalIgnoreCase)) : null;
                        if (parameters.Length > 1 && t == null) { Say(session, "[Rampage] towns: " + string.Join(", ", TOWNS.Select(x => x.Key))); return; }
                        t ??= TOWNS[Random.Shared.Next(TOWNS.Length)];
                        void go() => EventDirector.StartEntry(KEY_PREFIX + t.Key, who, session);
                        if (session != null) go(); else WorldManager.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(go));
                        return;
                    }
                case "curse":
                    // staff test: as if you had struck him (his curses follow you for FOE_FORGET_S)
                    if (session?.Player != null) { Struck(session.Player); Say(session, "[Rampage] you are marked as having struck him."); }
                    return;
                case "leave":
                    {
                        void stop() => EventDirector.StopEntryIf(KEY_PREFIX, who, session);
                        if (session != null) stop(); else WorldManager.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(stop));
                        return;
                    }
            }
            var v = s_Visit; var c = v?.Bael;
            bool rows = EventManager.IsEventAvailable("BaelZharonRampage") && EventManager.IsEventAvailable("BaelZharonRampageVisit");
            Say(session, $"[Rampage] (retired event, ours to run) switch {P_ON} {(SwitchOn ? "ON" : "off")}; rows {(rows ? "installed" : "NOT in ace_world (datafix 2026-11-24-bz-rampage-1-rows.sql)")}; season {(SeasonalCalendar.IsFestivalActive(FESTIVAL) ? "running" : "not running")} (Nov 24 to 30); his spells {(Lethal ? "can kill" : "leave 1 health")} ({P_LETHAL})");
            if (v == null) Say(session, "[Rampage] no visit now. The Event Director sends him (@eventdirector list shows the bz- entries and why each waits).");
            else Say(session, $"[Rampage] visiting {v.Town.Name}: {(c == null ? "not in the world now (nobody near; he comes back when someone is)" : $"at {c.Location}")}; foes {s_Foes.Count}, curses {v.Casts}, arrivals {v.Spawns}, struck {s_Struck} times since the restart");
            Say(session, "[Rampage] @rampage on | off | visit [town] | leave; towns: " + string.Join(", ", TOWNS.Select(x => x.Key)));
        }
    }

    // ================= hooks =================

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class BaelRampageTick
    {
        static bool s_Done;
        static bool Prepare()
        {
            if (!s_Done) { s_Done = true; try { BaelRampage.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] BaelRampage: register: {e.Message}"); } }
            return true;
        }
        static void Postfix()
        {
            try { BaelRampage.Tick(); }
            catch (Exception e) { TickGuard.Report("BaelRampage", e); }
        }
    }

    /// <summary>A blow or arrow on him: evaded ("Bael'Zharon evaded your attack", as players read in 2000), and the striker
    /// is marked for a curse.</summary>
    [HarmonyPatch(typeof(DamageEvent), nameof(DamageEvent.CalculateDamage))]
    static class BaelRampageEvade
    {
        static void Postfix(Creature attacker, Creature defender, DamageEvent __result)
        {
            try
            {
                if (__result == null || !BaelRampage.IsOurs(defender)) return;
                __result.Evaded = true;
                if (attacker is Player p) BaelRampage.Struck(p);
            }
            catch { }
        }
    }

    /// <summary>A war spell's projectile reaching him: marked, and ACE's monster wake-up (which would set him on the caster)
    /// is skipped.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnAttackMonster))]
    static class BaelRampageAttacked
    {
        static bool Prefix(Player __instance, Creature monster)
        {
            try { if (!BaelRampage.IsOurs(monster)) return true; BaelRampage.Struck(__instance); return false; }
            catch { return true; }
        }
    }

    /// <summary>Any spell cast at him (Invincible: ACE resists it, "Bael'Zharon resists your spell"): the caster is marked.</summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.TryResistSpell))]
    static class BaelRampageResist
    {
        static void Postfix(WorldObject __instance, WorldObject target, Spell spell)
        {
            try { if (BaelRampage.IsOurs(target) && __instance is Player p && spell != null && spell.IsHarmful) BaelRampage.Struck(p); }
            catch { }
        }
    }

    /// <summary>No monster AI: he never chases, swings or goes home; BaelRampage acts for him.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.Monster_Tick))]
    static class BaelRampageNoAi
    {
        static bool Prefix(Creature __instance) => !BaelRampage.IsOurs(__instance);
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.AlertMonster))]
    static class BaelRampageNoAlert
    {
        static bool Prefix(Creature __instance, ref bool __result)
        {
            if (!BaelRampage.IsOurs(__instance)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard))]
    static class BaelRampageNoSave
    {
        static void Postfix(WorldObject __instance, ref bool __result)
        {
            if (__result && BaelRampage.IsOurs(__instance)) __result = false;
        }
    }

    /// <summary>rg_bz_rampage_lethal off: his spells leave a player at 1 health.</summary>
    [HarmonyPatch(typeof(SpellProjectile), nameof(SpellProjectile.DamageTarget))]
    static class BaelRampageSpellCap
    {
        static void Prefix(SpellProjectile __instance, Creature target, ref float damage)
        {
            try
            {
                if (target is Player p && BaelRampage.IsOurs(__instance.ProjectileSource))
                {
                    BaelRampage.CapSpell(p, ref damage);
                }
            }
            catch { }
        }
    }
}

