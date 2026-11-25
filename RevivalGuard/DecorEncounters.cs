using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE DECORATOR'S ENCOUNTERS (owner 2026-11-23: "a highly curated and good looking boss dungeon"; the Encounters page of
    /// the Decorate panel, AcDecorMode.Encounters.cs). Spawn points, a boss, patrol paths and loot chests are layer entries
    /// (key e:&lt;n&gt;, kind spawn | boss | chest) beside the props, with the same undo, areas, Clear area and self-restore on a
    /// landblock load. On the test shard they are made live in memory (never written to the database, as props); the numbers
    /// come from DecorEncounterRule and go into the entry as its "plan", which tools/decor/decor.py publishes as retail-shaped
    /// rows (a generator copy and its profile, creature copies with HeartBeat emotes, a chest copy, a key copy, a quest row).
    ///   encspawn &lt;wcid&gt; &lt;count&gt; &lt;respawn s&gt; &lt;radius m&gt; &lt;wander 0|1&gt; &lt;pose&gt;
    ///   encboss  &lt;wcid&gt; &lt;respawn s&gt; &lt;lockout h&gt; &lt;pose&gt;
    ///   encchest &lt;tier 0-8&gt; &lt;none|pick|key&gt; &lt;reset s&gt; &lt;boss key|-&gt; &lt;item wcids a,b|-&gt; &lt;pose&gt;   (tier 0 = the items)
    ///   encset &lt;key&gt; &lt;field&gt; &lt;value&gt; | encmove &lt;key&gt; &lt;pose&gt; | encpatrol &lt;key&gt; none | &lt;cell x y z&gt;... | encremove &lt;key&gt;
    ///   enctest now | clear   (not an edit: every encounter of the area spawned fresh and chests refilled, or its monsters gone)
    /// </summary>
    internal static partial class Decor
    {
        static readonly HashSet<string> s_EncKinds = new HashSet<string> { "spawn", "boss", "chest" };
        static bool IsEnc(string kind) => kind != null && s_EncKinds.Contains(kind);

        static readonly Dictionary<string, WorldObject> s_EncLive = new Dictionary<string, WorldObject>();   // key -> generator or chest
        static readonly Dictionary<uint, string> s_EncOf = new Dictionary<uint, string>();                   // its guid -> key

        static string NextEnc()
        {
            int n = 1;
            foreach (var ar in s_Areas.Values)
                foreach (var kv in ar.Edits)
                    if (kv.Key.StartsWith("e:") && int.TryParse(kv.Key.Substring(2), out var k)) n = Math.Max(n, k + 1);
            return "e:" + n;
        }

        static string EncLabel(string verb) => verb switch
        {
            "encspawn" => "place spawn point", "encboss" => "place boss", "encchest" => "place chest", "encset" => "change encounter",
            "encmove" => "move encounter", "encpatrol" => "patrol path", "encremove" => "remove encounter", _ => null,
        };

        static void EncDispatch(Session s, Player me, string[] a, Action<string> reply)
        {
            switch (a[0])
            {
                case "encspawn":
                {
                    var e = new JsonObject
                    {
                        ["kind"] = "spawn", ["wcid"] = uint.Parse(a[1], CultureInfo.InvariantCulture),
                        ["count"] = DecorEncounterRule.Count(int.Parse(a[2], CultureInfo.InvariantCulture)),
                        ["respawn"] = R(DecorEncounterRule.Respawn(F(a[3]))), ["radius"] = R(DecorEncounterRule.Radius(F(a[4]))),
                        ["wander"] = a[5] == "1", ["after"] = PoseOf(Room(me, Pose(a, 6))),
                    };
                    EncNew(s, me, e, reply);
                    break;
                }
                case "encboss":
                {
                    var e = new JsonObject
                    {
                        ["kind"] = "boss", ["wcid"] = uint.Parse(a[1], CultureInfo.InvariantCulture), ["count"] = 1,
                        ["respawn"] = R(DecorEncounterRule.Respawn(F(a[2]))), ["radius"] = 0,
                        ["lockout"] = R(DecorEncounterRule.LockoutHours(F(a[3]))), ["wander"] = false, ["after"] = PoseOf(Room(me, Pose(a, 4))),
                    };
                    EncNew(s, me, e, reply);
                    break;
                }
                case "encchest":
                {
                    var e = new JsonObject
                    {
                        ["kind"] = "chest", ["wcid"] = DecorEncounterRule.CHEST_TEMPLATE, ["tier"] = Math.Clamp(int.Parse(a[1], CultureInfo.InvariantCulture), 0, 8),
                        ["lock"] = a[2] == "pick" || a[2] == "key" ? a[2] : "none", ["reset"] = R(DecorEncounterRule.ChestReset(F(a[3]))),
                        ["boss"] = a[4] == "-" ? null : a[4], ["items"] = Items(a[5]), ["after"] = PoseOf(Room(me, Pose(a, 6))),
                    };
                    EncNew(s, me, e, reply);
                    break;
                }
                case "encset": EncSet(me, a, reply); break;
                case "encmove":
                {
                    if (!(Get(a[1]) is JsonObject cur) || !IsEnc((string)cur["kind"])) { reply("No such encounter."); return; }
                    if (Locked(a[1], reply)) return;
                    var e = (JsonObject)cur.DeepClone();
                    e["after"] = PoseOf(Room(me, Pose(a, 2)));
                    EncReplace(me, a[1], e, reply);
                    break;
                }
                case "encpatrol": EncPatrol(me, a, reply); break;
                case "encremove":
                {
                    if (!(Get(a[1]) is JsonObject cur) || !IsEnc((string)cur["kind"])) { reply("No such encounter."); return; }
                    if (Locked(a[1], reply)) return;
                    EncTeardown(a[1]);
                    Del(a[1]);
                    reply($"{(string)cur["name"] ?? "It"} removed (Undo brings it back).");
                    break;
                }
            }
        }

        /// <summary>The client names an outdoor cell for any point (it knows no rooms); in a dungeon or a building the decorator
        /// stands in, the room that holds the point is found from the decorator's own cell (EnvCell.find_visible_child_cell).</summary>
        static ACE.Entity.Position Room(Player me, ACE.Entity.Position p)
        {
            try
            {
                if (me?.Location == null || !me.Location.Indoors || (me.Location.Cell >> 16) != (p.Cell >> 16)) return p;
                if (!(ACE.Server.Physics.Common.LScape.get_landcell(me.Location.Cell) is ACE.Server.Physics.Common.EnvCell here)) return p;
                foreach (float up in new[] { 0.5f, 0.05f, 1.2f })
                {
                    var found = here.find_visible_child_cell(new System.Numerics.Vector3(p.PositionX, p.PositionY, p.PositionZ + up), true);
                    if (found != null)
                        return new ACE.Entity.Position(found.ID, p.PositionX, p.PositionY, p.PositionZ, p.RotationX, p.RotationY, p.RotationZ, p.RotationW);
                }
            }
            catch (Exception x) { Mod.Log.Warn($"[RevivalGuard] Decor encounter room lookup: {x.Message}"); }
            return p;
        }

        static JsonArray Items(string csv)
        {
            var o = new JsonArray();
            if (csv == "-" || string.IsNullOrEmpty(csv)) return o;
            foreach (var p in csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(12))
                if (uint.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) o.Add(w);
            return o;
        }

        /// <summary>A new encounter: checked, planned, made live, put in the area being decorated, and the client told its key
        /// (it selects it, so the patrol button and the settings act on it at once).</summary>
        static void EncNew(Session s, Player me, JsonObject e, Action<string> reply)
        {
            string why = EncCheck(e);
            if (why != null) { reply(why); return; }
            string key = NextEnc();
            e["area"] = s_Cur.Id;
            EncPlan(key, e);
            Put(key, e);
            EncBuild(me, key, e, reply);
            reply($"{(string)e["name"]} placed: {EncSays(e)}");
            if (s != null) Send(s, new JsonObject { ["k"] = "enc", ["key"] = key, ["name"] = (string)e["name"] });
        }

        /// <summary>The creature (or item) really exists and is the right kind; its name goes in the entry.</summary>
        static string EncCheck(JsonObject e)
        {
            string kind = (string)e["kind"];
            if (kind == "chest")
            {
                foreach (var w in (JsonArray)e["items"]!)
                {
                    var iw = DatabaseManager.World.GetCachedWeenie((uint)D(w));
                    if (iw == null) return $"No such item ({(uint)D(w)}).";
                    if (iw.WeenieType == WeenieType.Creature) return "A chest holds items, not creatures.";
                }
                if ((int)D(e["tier"]) == 0 && ((JsonArray)e["items"]!).Count == 0) return "A chest needs a loot tier or some items.";
                if (e["boss"] != null && !(Get((string)e["boss"]) is JsonObject b && (string)b["kind"] == "boss")) return "That boss isn't placed.";
                e["name"] = e["boss"] != null ? $"{(string)((JsonObject)Get((string)e["boss"])!)["name"]}'s Chest" : "Chest";
                return null;
            }
            var w0 = DatabaseManager.World.GetCachedWeenie((uint)D(e["wcid"]));
            if (w0 == null) return $"No such creature ({(uint)D(e["wcid"])}).";
            string wn = w0.GetProperty(PropertyString.Name) ?? "It";
            if (w0.WeenieType != WeenieType.Creature) return $"{wn} isn't a creature.";
            e["name"] = wn;
            return null;
        }

        /// <summary>The entry's plan: what decor.py publishes and what the live copy is given, the same numbers both ways.</summary>
        static void EncPlan(string key, JsonObject e)
        {
            string kind = (string)e["kind"], area = (string)e["area"] ?? s_Cur.Id;
            var plan = new JsonObject();
            if (kind == "chest")
            {
                int tier = (int)D(e["tier"]);
                if (tier > 0) plan["treasure"] = DecorEncounterRule.TreasureFor(tier);
                plan["lockcode"] = DecorEncounterRule.LockCode(area, key);
                string lk = (string)e["lock"];
                plan["locked"] = lk != "none";
                plan["resist"] = lk == "pick" ? 300 : lk == "key" ? 5000 : 0;   // 5000: retail's "nearly impossible to pick" (3969)
                if (e["boss"] is JsonNode bn && Get((string)bn) is JsonObject boss && N(boss["lockout"]) > 0f)
                {
                    string q = DecorEncounterRule.QuestName((string)boss["area"] ?? area, (string)bn);
                    plan["quest"] = q;
                    plan["mindelta"] = (int)Math.Round(N(boss["lockout"]) * 3600f);
                    plan["emotes"] = SetsJson(new[] { DecorEncounterRule.Lockout(q) });
                }
                e["plan"] = plan;
                return;
            }
            float respawn = N(e["respawn"]), radius = N(e["radius"]);
            int count = (int)D(e["count"]);
            plan["regen"] = R(DecorEncounterRule.RegenInterval(respawn));
            plan["specific"] = DecorEncounterRule.Specific(count, radius);
            var sets = new List<DecorEncounterRule.Set>();
            var pts = PatrolOffsets(e);
            var patrol = DecorEncounterRule.Patrol(pts);
            if (patrol != null) sets.Add(patrol);
            else if (e["wander"]?.GetValue<bool>() == true) sets.AddRange(DecorEncounterRule.Wander(radius, (int)(Hash(key) % 360)));
            plan["emotes"] = SetsJson(sets);
            e["plan"] = plan;
        }

        static uint Hash(string s) { uint h = 2166136261; foreach (char c in s) h = (h ^ c) * 16777619; return h; }

        static List<(float x, float y, float z)> PatrolOffsets(JsonObject e)
        {
            var o = new List<(float, float, float)>();
            if (e["patrol"] is JsonArray pa)
                foreach (var p in pa.OfType<JsonObject>()) o.Add((N(p["x"]), N(p["y"]), N(p["z"])));
            return o;
        }

        static JsonArray SetsJson(IEnumerable<DecorEncounterRule.Set> sets)
        {
            var arr = new JsonArray();
            foreach (var st in sets)
            {
                var acts = new JsonArray();
                foreach (var ac in st.Acts)
                {
                    var j = new JsonObject { ["t"] = ac.Type, ["d"] = R(ac.Delay) };
                    if (ac.Type == DecorEncounterRule.MOVE) { j["x"] = R(ac.X); j["y"] = R(ac.Y); j["z"] = R(ac.Z); }
                    if (ac.Message != null) j["m"] = ac.Message;
                    acts.Add(j);
                }
                var o = new JsonObject { ["cat"] = st.Category, ["p"] = R(st.Probability), ["acts"] = acts };
                if (st.Quest != null) o["quest"] = st.Quest;
                arr.Add(o);
            }
            return arr;
        }

        static List<PropertiesEmote> EmotesFrom(JsonObject plan)
        {
            var list = new List<PropertiesEmote>();
            if (!(plan?["emotes"] is JsonArray arr)) return list;
            foreach (var so in arr.OfType<JsonObject>())
            {
                var em = new PropertiesEmote { Category = (EmoteCategory)(int)D(so["cat"]), Probability = N(so["p"]), Quest = (string)so["quest"] };
                foreach (var ao in ((JsonArray)so["acts"]!).OfType<JsonObject>())
                {
                    var act = new PropertiesEmoteAction { Type = (uint)D(ao["t"]), Delay = N(ao["d"]), Extent = 1f, Message = (string)ao["m"] };
                    if (ao["x"] != null) { act.OriginX = N(ao["x"]); act.OriginY = N(ao["y"]); act.OriginZ = N(ao["z"]); }
                    em.PropertiesEmoteAction.Add(act);
                }
                list.Add(em);
            }
            return list;
        }

        static string EncSays(JsonObject e)
        {
            string kind = (string)e["kind"];
            if (kind == "chest")
            {
                int tier = (int)D(e["tier"]);
                string what = tier > 0 ? $"loot tier {tier}" : $"{((JsonArray)e["items"]!).Count} item(s)";
                string lk = (string)e["lock"] == "key" ? (e["boss"] != null ? ", locked: the boss drops its key" : ", locked: no key drops (add a boss)")
                          : (string)e["lock"] == "pick" ? ", locked (pickable)" : "";
                return $"{what}{lk}, refills {DecorEncounterRule.Span(N(e["reset"]))} after it is opened";
            }
            string pat = e["patrol"] is JsonArray pa && pa.Count > 0 ? $", patrols {pa.Count} point(s)" : e["wander"]?.GetValue<bool>() == true ? ", wanders" : "";
            string lockout = kind == "boss" && N(e["lockout"]) > 0f ? $", chest lockout {DecorEncounterRule.Span(N(e["lockout"]) * 3600f)}" : "";
            return $"{(int)D(e["count"])} at a time, respawn {DecorEncounterRule.Span(N(e["respawn"]))}"
                 + (N(e["radius"]) > 0f ? $", {N(e["radius"]):0.#} m round" : "") + pat + lockout;
        }

        static void EncSet(Player me, string[] a, Action<string> reply)
        {
            if (a.Length < 4 || !(Get(a[1]) is JsonObject cur) || !IsEnc((string)cur["kind"])) { reply("No such encounter."); return; }
            if (Locked(a[1], reply)) return;
            var e = (JsonObject)cur.DeepClone();
            string f = a[2], v = a[3];
            switch (f)
            {
                case "count": if ((string)e["kind"] != "spawn") { reply("A boss is one."); return; } e["count"] = DecorEncounterRule.Count(int.Parse(v, CultureInfo.InvariantCulture)); break;
                case "respawn": e["respawn"] = R(DecorEncounterRule.Respawn(F(v))); break;
                case "radius": if ((string)e["kind"] != "spawn") { reply("A boss stands on its spot."); return; } e["radius"] = R(DecorEncounterRule.Radius(F(v))); break;
                case "wander": e["wander"] = v == "1"; break;
                case "lockout": e["lockout"] = R(DecorEncounterRule.LockoutHours(F(v))); break;
                case "wcid": e["wcid"] = uint.Parse(v, CultureInfo.InvariantCulture); break;
                case "tier": e["tier"] = Math.Clamp(int.Parse(v, CultureInfo.InvariantCulture), 0, 8); break;
                case "lock": e["lock"] = v == "pick" || v == "key" ? v : "none"; break;
                case "reset": e["reset"] = R(DecorEncounterRule.ChestReset(F(v))); break;
                case "boss": e["boss"] = v == "-" ? null : v; break;
                case "items": e["items"] = Items(v); break;
                default: reply("Unknown setting."); return;
            }
            string why = EncCheck(e);
            if (why != null) { reply(why); return; }
            EncReplace(me, a[1], e, reply);
            reply($"{(string)e["name"]}: {EncSays(e)}");
        }

        /// <summary>encpatrol &lt;key&gt; none | &lt;cell x y z&gt;...: the points, kept as offsets from the spawn point (ACE's Move
        /// emote is relative to the creature's home).</summary>
        static void EncPatrol(Player me, string[] a, Action<string> reply)
        {
            if (!(Get(a[1]) is JsonObject cur) || (string)cur["kind"] == "chest" || !IsEnc((string)cur["kind"])) { reply("Pick a spawn point or a boss first."); return; }
            if (Locked(a[1], reply)) return;
            var e = (JsonObject)cur.DeepClone();
            if (a.Length < 3 || a[2] == "none") e.Remove("patrol");
            else
            {
                var homePos = PosFrom((JsonObject)e["after"]!);
                var home = homePos.ToGlobal();
                var pts = new JsonArray();
                var offs = new List<(float, float, float)>();
                for (int i = 2; i + 3 < a.Length; i += 4)
                {
                    var at = Room(me, new ACE.Entity.Position(Hex(a[i]), F(a[i + 1]), F(a[i + 2]), F(a[i + 3]), 0f, 0f, 0f, 1f));
                    // in one landblock the offset is the local difference (a room's ToGlobal is not the land's)
                    var p = (at.Cell >> 16) == (homePos.Cell >> 16)
                        ? new System.Numerics.Vector3(at.PositionX - homePos.PositionX, at.PositionY - homePos.PositionY, at.PositionZ - homePos.PositionZ)
                        : at.ToGlobal() - home;
                    pts.Add(new JsonObject { ["x"] = R(p.X), ["y"] = R(p.Y), ["z"] = R(p.Z) });
                    offs.Add((p.X, p.Y, p.Z));
                }
                string why = DecorEncounterRule.WhyNotPatrol(offs);
                if (why != null) { reply(why); return; }
                e["patrol"] = pts;
            }
            EncReplace(me, a[1], e, reply);
            reply(e["patrol"] is JsonArray pa ? $"{(string)e["name"]} patrols {pa.Count} point(s) and back, in a loop." : $"{(string)e["name"]}: no patrol.");
        }

        /// <summary>The same encounter changed: planned again and made again (its monsters spawn fresh).</summary>
        static void EncReplace(Player me, string key, JsonObject e, Action<string> reply)
        {
            EncPlan(key, e);
            EncTeardown(key);
            Put(key, e);
            EncBuild(me, key, e, reply);
            // a boss's lockout lives on the chests keyed to it
            if ((string)e["kind"] == "boss")
                foreach (var ar in s_Areas.Values)
                    foreach (var kv in ar.Edits.ToList())
                        if (kv.Value is JsonObject c && (string)c["kind"] == "chest" && (string)c["boss"] == key)
                        { EncPlan(kv.Key, c); c["name"] = $"{(string)e["name"]}'s Chest"; EncTeardown(kv.Key); EncBuild(me, kv.Key, c, _ => { }); }
        }

        // ---- live: made in memory on the test shard, never saved (generators and what they make are never saved by ACE) ----

        static bool EncAlive(string key) => s_EncLive.TryGetValue(key, out var wo) && wo != null && !wo.IsDestroyed && wo.CurrentLandblock != null;

        static void EncTeardown(string key)
        {
            if (!s_EncLive.TryGetValue(key, out var wo)) return;
            s_EncLive.Remove(key);
            if (wo == null) return;
            s_EncOf.Remove(wo.Guid.Full);
            try
            {
                if (wo.GeneratorProfiles != null && !(wo is Chest))
                    foreach (var p in wo.GeneratorProfiles)
                        foreach (var info in p.Spawned.Values.ToList()) info.TryGetWorldObject()?.Destroy();
                if (!wo.IsDestroyed) wo.Destroy();
            }
            catch (Exception x) { Mod.Log.Warn($"[RevivalGuard] Decor encounter {key} teardown: {x.Message}"); }
        }

        /// <summary>One encounter made live where its landblock is loaded (else it waits for DecorBlockLoad). True = made.</summary>
        static bool EncBuild(Player me, string key, JsonObject e, Action<string> reply)
        {
            var pose = PosFrom((JsonObject)e["after"]!);
            uint blk = pose.Cell >> 16;
            if (!LandblockManager.GetLoadedLandblocks().Any(l => l.Id.Landblock == blk)) return false;
            var plan = e["plan"] as JsonObject;
            WorldObject wo;
            if ((string)e["kind"] == "chest")
            {
                wo = WorldObjectFactory.CreateNewWorldObject(DecorEncounterRule.CHEST_TEMPLATE);
                if (wo == null) { reply("No chest weenie on this shard."); return false; }
                wo.SetProperty(PropertyString.Name, (string)e["name"]);
                bool locked = plan?["locked"]?.GetValue<bool>() == true;
                wo.IsLocked = locked;
                if (locked) wo.SetProperty(PropertyBool.DefaultLocked, true); else wo.RemoveProperty(PropertyBool.DefaultLocked);
                wo.SetProperty(PropertyInt.ResistLockpick, (int)D(plan!["resist"]));
                wo.SetProperty(PropertyString.LockCode, (string)plan["lockcode"]);
                wo.SetProperty(PropertyFloat.ResetInterval, N(e["reset"]));
                var gens = new List<PropertiesGenerator>();
                if (plan["treasure"] != null)
                    gens.Add(new PropertiesGenerator { Probability = 1f, WeenieClassId = (uint)D(plan["treasure"]), Delay = 0f, InitCreate = 1, MaxCreate = 1,
                        WhenCreate = RegenerationType.Destruction | RegenerationType.PickUp, WhereCreate = RegenLocationType.ContainTreasure, StackSize = 0, AnglesW = 1f });
                foreach (var it in (JsonArray)e["items"]!)
                    gens.Add(new PropertiesGenerator { Probability = -1f, WeenieClassId = (uint)D(it), Delay = 0f, InitCreate = 1, MaxCreate = 1,
                        WhenCreate = RegenerationType.Destruction | RegenerationType.PickUp, WhereCreate = RegenLocationType.Contain, StackSize = -1, AnglesW = 1f });
                wo.SetProperty(PropertyInt.InitGeneratedObjects, gens.Count);
                wo.SetProperty(PropertyInt.MaxGeneratedObjects, gens.Count);
                wo.Biota.PropertiesGenerator = gens;
                wo.InitializeGenerator();
                if (plan["quest"] != null)
                {
                    wo.SetProperty(PropertyString.Quest, (string)plan["quest"]);
                    wo.Biota.PropertiesEmote = EmotesFrom(plan);
                    InjectQuest((string)plan["quest"], (uint)D(plan["mindelta"]));
                }
            }
            else
            {
                wo = WorldObjectFactory.CreateNewWorldObject(DecorEncounterRule.GEN_TEMPLATE);
                if (wo == null) { reply("No generator weenie on this shard."); return false; }
                int count = (int)D(e["count"]);
                bool specific = plan?["specific"]?.GetValue<bool>() == true;
                wo.SetProperty(PropertyFloat.RegenerationInterval, N(plan!["regen"]));
                wo.SetProperty(PropertyFloat.GeneratorRadius, Math.Max(0.5f, N(e["radius"])));
                wo.SetProperty(PropertyInt.InitGeneratedObjects, count);
                wo.SetProperty(PropertyInt.MaxGeneratedObjects, count);
                wo.Biota.PropertiesGenerator = new List<PropertiesGenerator>
                {
                    new PropertiesGenerator
                    {
                        Probability = -1f, WeenieClassId = (uint)D(e["wcid"]), Delay = N(e["respawn"]), InitCreate = count, MaxCreate = count,
                        WhenCreate = RegenerationType.Destruction, WhereCreate = specific ? RegenLocationType.Specific : RegenLocationType.Scatter,
                        StackSize = -1, PaletteId = 0, Shade = 0f, ObjCellId = 0, OriginX = 0f, OriginY = 0f, OriginZ = 0f,
                        AnglesW = pose.RotationW, AnglesX = pose.RotationX, AnglesY = pose.RotationY, AnglesZ = pose.RotationZ,
                    },
                };
                wo.InitializeGenerator();
            }
            wo.Location = new ACE.Entity.Position(pose);
            wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
            if (!wo.EnterWorld())
            {
                // the generator is invisible and has no body to collide; a chest may sit against a wall: put it at your feet and
                // straight into its cell, as Restore does
                if (me == null || (string)e["kind"] != "chest") { wo.Destroy(); reply($"{(string)e["name"]} could not be placed there."); return false; }
                wo.Location = new ACE.Entity.Position(me.Location);
                if (!wo.EnterWorld() || !Place(wo, pose, reply)) { wo.Destroy(); reply($"{(string)e["name"]} could not be placed there."); return false; }
            }
            s_EncLive[key] = wo;
            s_EncOf[wo.Guid.Full] = key;
            Mod.Log.Info($"[RevivalGuard] Decor encounter {key} ({(string)e["kind"]} {(string)e["name"]}) live as 0x{wo.Guid.Full:X8} at {wo.Location.ToLOCString()}");
            return true;
        }

        /// <summary>A boss chest's lockout quest, known to ACE's quest cache on the test shard without a database row (decor.py
        /// writes the row when it publishes).</summary>
        static void InjectQuest(string name, uint minDelta)
        {
            try
            {
                var f = AccessTools.Field(DatabaseManager.World.GetType(), "cachedQuest");
                if (f?.GetValue(DatabaseManager.World) is ConcurrentDictionary<string, ACE.Database.Models.World.Quest> cache)
                    cache[name] = new ACE.Database.Models.World.Quest { Name = name, MinDelta = minDelta, MaxSolves = -1, Message = name, LastModified = DateTime.UtcNow };
                else Mod.Log.Warn("[RevivalGuard] Decor: ACE's quest cache not found; a boss chest's lockout is not kept on this shard");
            }
            catch (Exception x) { Mod.Log.Warn($"[RevivalGuard] Decor quest {name}: {x.Message}"); }
        }

        /// <summary>DecorSpawn's postfix for an encounter generator: the creature gets its wander or patrol (HeartBeat emotes,
        /// on its own copy of the list: ACE shares the weenie's list between every copy). True = it was ours.</summary>
        static bool EncSpawned(GeneratorProfile p, List<WorldObject> spawned)
        {
            if (p.Generator == null || !s_EncOf.TryGetValue(p.Generator.Guid.Full, out var key) || !(Get(key) is JsonObject e)) return false;
            var mine = EmotesFrom(e["plan"] as JsonObject);
            foreach (var wo in spawned)
            {
                if (mine.Count > 0)
                {
                    var list = new List<PropertiesEmote>();
                    if (wo.Biota.PropertiesEmote != null) list.AddRange(wo.Biota.PropertiesEmote.Where(x => x.Category != EmoteCategory.HeartBeat).Select(x => x.Clone()));
                    list.AddRange(EmotesFrom(e["plan"] as JsonObject));
                    wo.Biota.PropertiesEmote = list;
                }
                Mod.Log.Info($"[RevivalGuard] Decor encounter {key} spawned {wo.Name} 0x{wo.Guid.Full:X8} at {wo.Location?.ToLOCString()}");
            }
            return true;
        }

        /// <summary>DecorBossLoot: a boss dies; each chest keyed to it gets its key on the corpse (retail: the key is in the boss's
        /// create list as ContainTreasure at chance 1, which decor.py publishes on its creature copy).</summary>
        internal static void BossLoot(Creature c, Corpse corpse)
        {
            if (!On || c?.Generator == null || corpse == null) return;
            lock (s_Lock)
            {
                if (!s_EncOf.TryGetValue(c.Generator.Guid.Full, out var key) || !(Get(key) is JsonObject e)) return;
                Mod.Log.Info($"[RevivalGuard] Decor encounter {key} {c.Name} died");
                if ((string)e["kind"] != "boss") return;
                foreach (var ar in s_Areas.Values)
                    foreach (var kv in ar.Edits)
                        if (kv.Value is JsonObject ch && (string)ch["kind"] == "chest" && (string)ch["boss"] == key && (string)ch["lock"] == "key"
                            && ch["plan"]?["lockcode"] is JsonNode code)
                        {
                            var k = WorldObjectFactory.CreateNewWorldObject(DecorEncounterRule.KEY_TEMPLATE);
                            if (k == null) continue;
                            k.SetProperty(PropertyString.KeyCode, (string)code);
                            k.SetProperty(PropertyString.Name, $"{(string)ch["name"]} Key");
                            if (corpse.TryAddToInventory(k)) Mod.Log.Info($"[RevivalGuard] Decor encounter {key}: {k.Name} on the corpse");
                            else k.Destroy();
                        }
            }
        }

        /// <summary>enctest now | clear: every encounter of the area being decorated made fresh (monsters at full count, chests
        /// refilled and locked again), or its monsters taken away (they come back on their respawn time).</summary>
        static void EncTest(Player me, string[] a, Action<string> reply)
        {
            bool now = a.Length > 1 && a[1] == "now";
            int n = 0, gone = 0;
            foreach (var kv in s_Cur.Edits.ToList())
            {
                if (!(kv.Value is JsonObject e) || !IsEnc((string)e["kind"])) continue;
                if (now) { EncTeardown(kv.Key); if (EncBuild(me, kv.Key, e, reply)) n++; continue; }
                if (!s_EncLive.TryGetValue(kv.Key, out var wo) || wo?.GeneratorProfiles == null || wo is Chest) continue;
                foreach (var p in wo.GeneratorProfiles)
                    foreach (var info in p.Spawned.Values.ToList()) { var c = info.TryGetWorldObject(); if (c != null) { c.Destroy(); gone++; } }
            }
            reply(now ? $"{n} encounter(s) spawned fresh, chests refilled." : $"{gone} monster(s) cleared; they come back on their respawn time.");
        }

        /// <summary>ReapplyOne for an encounter: made when its landblock is loaded and it isn't live yet.</summary>
        static bool EncReapply(Player me, string key, JsonObject e)
        {
            if (EncAlive(key)) return false;
            EncTeardown(key);
            return EncBuild(me, key, e, _ => { });
        }

        /// <summary>Reconcile for an encounter (undo, redo, Clear area): the old one taken away, the wanted one made.</summary>
        static void EncReconcile(Player me, string key, JsonObject want, Area into)
        {
            EncTeardown(key);
            Del(key);
            if (want == null) return;
            Put(key, want, into);
            EncBuild(me, key, want, _ => { });
        }
    }

    /// <summary>A decor boss's chest key onto its corpse (Creature.GenerateTreasure is private; off unless decor/enabled).</summary>
    [HarmonyPatch]
    static class DecorBossLoot
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(Creature), "GenerateTreasure");

        static bool Prepare(System.Reflection.MethodBase original)
        {
            if (original != null) return true;
            bool ok = AccessTools.Method(typeof(Creature), "GenerateTreasure") != null;
            if (!ok) Mod.Log.Warn("[RevivalGuard] DecorBossLoot: Creature.GenerateTreasure not found; decor boss keys don't drop on the test shard");
            return ok;
        }

        static void Postfix(Creature __instance, Corpse corpse)
        {
            if (!Decor.On) return;
            try { Decor.BossLoot(__instance, corpse); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DecorBossLoot: {e.Message}"); }
        }
    }
}
