using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
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
    /// THE WORLD DECORATOR'S SERVER END (docs/proposals/world-decorator.md, owner 2026-11-21). The client's Decorate mode
    /// (AcDecorMode) sends these; nobody types them. TEST SHARD ONLY: every verb refuses unless the file
    /// Mods/RevivalGuard/decor/enabled exists, which only the box/M5 test shard has; live never gets it. Edits change the
    /// running world at once and are kept in Mods/RevivalGuard/decor/&lt;area&gt;.json, the layer that
    /// tools/decor/decor.py turns into a datafix (server rows) and a cell-overlay record (DAT statics). The database is
    /// never written here: a restart puts the test shard's rows back until a datafix is applied.
    /// Layer: one entry per thing touched, keyed, holding its first-seen pose ("before") and its latest ("after", null =
    /// removed), so the file is the net result.
    /// V2 (owner 2026-11-23, "decorate the whole world of dereth easily in game"): one layer per AREA (&lt;area&gt;.json, with
    /// the landblocks it covers, its name and its spot), padlocks ("locks"), undo kept per area on disk (&lt;area&gt;.undo.json:
    /// each command's edits before and after), batches (one message, one undo step), kits (decor/kits/), and each landblock
    /// gets its edits when it loads (DecorBlockLoad), not only when Decorate opens.
    ///   s:&lt;guid&gt;     a placed server object (landblock_instance row, link children included)
    ///   a:&lt;n&gt;         a server object added from the palette (a Stuck copy of a retail weenie)
    ///   t:&lt;blk&gt;:&lt;i&gt;  a DAT static of block blk, i-th entry of its object list
    ///   n:&lt;n&gt;         a DAT static added from the palette
    /// Poses are AC landblock-local: cell, x, y, z, qw, qx, qy, qz.
    /// </summary>
    internal static partial class Decor
    {
        static readonly object s_Lock = new object();
        static string Dir => Path.Combine(ModFolder.Path(), "decor");
        static bool Enabled => File.Exists(Path.Combine(Dir, "enabled"));
        internal static volatile bool On;

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "decor", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "World decorator (test shard only; sent by the client's Decorate mode).",
                "state | move <guid> <pose> | remove <guid> | restore <guid> | add <wcid> <pose> [size] | unadd <id> | static <blk> <i> <model> <pose|gone> | "
                + "staticadd <id> <model> <pose|gone> [size] | lock <guid|key> on|off | undo | redo | revert | area <id> | newarea | goto [id] | "
                + "batch <label> <verb args>;<verb args>... | part <id> <i> <n> <text> | kitsave <name> <items> | publish <area> [props rows pieces] | pub");
            On = Enabled;
            Mod.Log.Info("[RevivalGuard] Decor: @decor (world decorator, test shard only: " + (Enabled ? "ENABLED here" : "off here") + ")");
        }

        // a long batch arrives as several chat lines ("part"), put together before it runs
        static readonly Dictionary<string, string[]> s_Parts = new Dictionary<string, string[]>();

        /// <summary>Verbs that change the layout: each run is ONE undo step (a batch of fifty is one step too).</summary>
        static readonly HashSet<string> s_Editing = new HashSet<string>
            { "scale", "piecescale", "move", "remove", "restore", "add", "unadd", "static", "staticadd", "batch", "revert", "way" };

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            void Reply(string t) => me.Session?.Network?.EnqueueSend(new GameMessageSystemChat("[Decor] " + t, ChatMessageType.Broadcast));
            On = Enabled;
            if (!On) { Reply("Decorating works only on the test shard."); Send(s, new JsonObject { ["k"] = "off" }); return; }
            if (args.Length == 0) { Reply("Use the Decorate button."); return; }
            try
            {
                lock (s_Lock)
                {
                    Load();
                    string verb = args[0].ToLowerInvariant();
                    if (verb == "part")
                    {
                        // part <id> <i> <n> <text>: kept until all n are in, then run as one command
                        string pid = me.Guid.Full.ToString("X8") + ":" + args[1];
                        int i = int.Parse(args[2], CultureInfo.InvariantCulture), n = int.Parse(args[3], CultureInfo.InvariantCulture);
                        if (n < 1 || n > 64 || i < 0 || i >= n) return;
                        if (!s_Parts.TryGetValue(pid, out var got) || got.Length != n) s_Parts[pid] = got = new string[n];
                        got[i] = string.Join(" ", args.Skip(4));
                        if (got.Any(x => x == null)) return;
                        s_Parts.Remove(pid);
                        args = string.Join(" ", got).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (args.Length == 0) return;
                        verb = args[0].ToLowerInvariant();
                    }
                    s_Cur = ActiveOf(me);
                    if (s_Editing.Contains(verb))
                    {
                        var before = Snapshot();
                        string label = verb == "batch" ? string.Join(" ", args[1].Split('_')) : verb;
                        if (verb == "batch") Batch(s, me, args, Reply);
                        else if (verb == "revert") Revert(me, Reply);
                        else Dispatch(s, me, args, Reply);
                        EndStep(label, before, s_Cur);
                        Save();
                        if (verb == "revert") SendState(s, me); else SendCounts(s, me);
                        return;
                    }
                    switch (verb)
                    {
                        case "state": SendState(s, me); return;
                        case "info": Info(s, me, args); return;
                        case "infos":   // a box selection: one answer for all of them
                        {
                            var list = new JsonArray();
                            s_InfoInto = list;
                            try { foreach (var g in args.Skip(1).Take(200)) Info(s, me, new[] { "info", g }); }
                            finally { s_InfoInto = null; }
                            Send(s, new JsonObject { ["k"] = "infos", ["list"] = list });
                            return;
                        }
                        case "audit": Audit(me, Reply); Save(); return;
                        case "undo": case "redo": UndoRedo(me, verb == "undo", Reply); Save(); SendState(s, me); return;
                        case "lock": Lock(me, args, Reply); Save(); SendState(s, me); return;
                        case "kitsave": KitSave(args, Reply); SendState(s, me); return;
                        case "area":
                            if (args.Length < 2 || !s_Areas.ContainsKey(args[1])) { Reply("No such area."); return; }
                            s_Active[me.Guid.Full] = args[1];
                            SendState(s, me);
                            return;
                        case "newarea": NewArea(me, Reply, args.Length > 1 ? string.Join(" ", args.Skip(1)) : null); Save(); SendState(s, me); return;
                        case "goto": GoTo(me, args.Length > 1 ? args[1] : null, Reply); return;
                        case "publish": Publish(s, me, args, Reply); SendPub(s); return;
                        case "pub": SendPub(s); return;
                        default: if (WorldVerb(s, me, verb, args, Reply)) return; Reply("Unknown decor verb."); return;
                    }
                }
            }
            catch (Exception e) { Reply("That edit failed: " + e.Message); Mod.Log.Warn($"[RevivalGuard] Decor {string.Join(' ', args)}: {e}"); }
        }

        static void Dispatch(Session s, Player me, string[] args, Action<string> reply)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "scale": Scale(s, me, args, reply); break;
                case "piecescale": PieceScale(args); break;
                case "move": Move(s, me, args, reply); break;
                case "remove": Remove(me, args, reply); break;
                case "restore": Restore(me, args, reply); break;
                case "add": Add(s, me, args, reply); break;
                case "unadd": Unadd(me, args, reply); break;
                case "static": Static(args, reply); break;
                case "staticadd": StaticAdd(args, reply); break;
                case "way": Way(me, args, reply); break;
                default: reply("Unknown decor verb in a batch: " + args[0]); break;
            }
        }

        /// <summary>batch &lt;label_words&gt; &lt;verb args&gt;;&lt;verb args&gt;;...: a kit, line, ring, brush stroke or group edit as one
        /// message, one undo step and one answer (the props it added, in order, and what was refused).</summary>
        static void Batch(Session s, Player me, string[] args, Action<string> reply)
        {
            var ops = string.Join(" ", args.Skip(2)).Split(';').Select(x => x.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(x => x.Length > 0).ToList();
            var said = new List<string>();
            s_BatchAdded = new List<JsonObject>();
            int failed = 0;
            string label = string.Join(" ", args[1].Split('_'));
            try
            {
                foreach (var op in ops)
                {
                    string first = null;
                    int added0 = s_BatchAdded.Count;
                    try { Dispatch(null, me, op, t => first ??= t); }
                    catch (Exception e) { first = "failed: " + e.Message; }
                    bool refused = op[0] == "add" ? s_BatchAdded.Count == added0
                        : first != null && !(op[0] == "remove" && first.Contains("removed")) && !(op[0] == "restore" && first.Contains("back where"));
                    if (refused) { failed++; if (said.Count < 3 && first != null) said.Add(first); }
                }
                var added = new JsonArray(s_BatchAdded.Select(x => (JsonNode)x).ToArray());
                Send(s, new JsonObject { ["k"] = "batch", ["label"] = label, ["ops"] = ops.Count, ["added"] = added, ["failed"] = failed });
                reply(failed == 0 ? $"{label}: {ops.Count} done" : $"{label}: {ops.Count - failed} of {ops.Count} done. {string.Join(" ", said)}");
            }
            finally { s_BatchAdded = null; }
        }

        /// <summary>newarea: an area around where you stand, named by its map spot; it becomes the one you decorate.</summary>
        static void NewArea(Player me, Action<string> reply, string name = null)
        {
            var at = new ACE.Entity.Position(me.Location);
            uint blk = at.LandblockId.Landblock;
            int n = 1;
            string id;
            while (s_Areas.ContainsKey(id = $"area-{blk:x4}-{n}")) n++;
            string where;
            try { where = at.GetMapCoordStr() ?? $"block {blk:X4}"; } catch { where = $"block {blk:X4}"; }
            // indoors (a dungeon or a building's room, decor-world): the client names the place; map coordinates mean little there
            if (at.Indoors && !string.IsNullOrWhiteSpace(name)) where = CleanName(name);
            var ar = new Area
            {
                Id = id,
                Layer = new JsonObject
                {
                    ["area"] = id, ["name"] = $"New area {s_Areas.Count + 1} ({where})", ["blocks"] = new JsonArray($"{blk:X4}"),
                    ["home"] = PoseOf(at), ["next"] = 1, ["edits"] = new JsonObject(), ["locks"] = new JsonObject(),
                },
            };
            s_Areas[id] = ar;
            s_Active[me.Guid.Full] = id;
            reply($"{ar.Layer["name"]}: made. Your changes go in it now.");
        }

        /// <summary>goto [id]: the decorator to an area's spot (where it was made, or Holtburg's lifestone).</summary>
        static void GoTo(Player me, string id, Action<string> reply)
        {
            var ar = id != null && s_Areas.TryGetValue(id, out var x) ? x : ActiveOf(me);
            if (!(ar.Layer["home"] is JsonObject home)) { reply("That area has no spot to go to."); return; }
            var to = PosFrom(home);
            to.LandblockId = new LandblockId(to.GetCell());
            me.Teleport(to);
        }

        // ---- server objects ----

        static WorldObject Find(Player me, uint guid)
        {
            WorldObject wo;
            foreach (var l in LandblockManager.GetLoadedLandblocks())
                if ((wo = l.GetObject(new ObjectGuid(guid))) != null) return wo;
            var p = ProfileOfRow(guid);
            if (p == null) return null;
            foreach (var info in p.Spawned.Values)
                if ((wo = info.TryGetWorldObject()) != null) return wo;
            return null;
        }

        // ---- a generator's linked row: most of a town (NPCs, forges, seasonal decor) ----
        // Its landblock_instance row is a link child: ACE makes a profile on the parent generator with Id = the row's guid and
        // spawns it with a NEW dynamic guid (GeneratorProfile.Spawn). So a live object is traced to its row here, and the profile
        // is what keeps a move or a removal true when it respawns (DecorSpawn).

        static GeneratorProfile ProfileOf(WorldObject wo) =>
            wo.Generator?.GeneratorProfiles?.FirstOrDefault(p => p.Spawned.ContainsKey(wo.Guid.Full));

        static GeneratorProfile ProfileOfRow(uint row)
        {
            if ((row & 0xF0000000u) != 0x70000000u) return null;
            uint block = (row >> 12) & 0xFFFF;
            foreach (var l in LandblockManager.GetLoadedLandblocks())
            {
                if (l.Id.Landblock != block) continue;
                foreach (var g in l.GetAllWorldObjectsForDiagnostics())
                {
                    if (!g.IsGenerator) continue;
                    var hit = g.GeneratorProfiles.FirstOrDefault(p => p.Id == row);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        /// <summary>A placed generator that makes exactly one kind of thing around itself (the Collector, Azimar, the town cow, a
        /// costumer): moving what it made means moving its row. The weenie's profile table is shared by every copy, so it is never
        /// edited, and one that names an absolute spot can't follow.</summary>
        static WorldObject GenRowOf(WorldObject wo)
        {
            var g = wo.Generator;
            if (g == null || (g.Guid.Full & 0xF0000000u) != 0x70000000u || g.Generator != null || g.GeneratorProfiles == null) return null;
            var real = g.GeneratorProfiles.Where(p => !p.IsPlaceholder).ToList();
            // exactly one at a time: an apple tree's generator (max -1) keeps several, and one apple can't carry the tree (audit 11-23)
            if (real.Count != 1 || real[0].Biota.MaxCreate != 1 || (real[0].Id & 0xF0000000u) == 0x70000000u || (real[0].Biota.ObjCellId ?? 0) > 0) return null;
            return g;
        }

        /// <summary>A row guid sent for a one-thing generator means what it made.</summary>
        static WorldObject SpawnOf(WorldObject wo)
        {
            if (wo == null || !wo.IsGenerator) return wo;
            var real = wo.GeneratorProfiles.Where(p => !p.IsPlaceholder).ToList();
            if (real.Count != 1 || real[0].Biota.MaxCreate != 1 || (real[0].Id & 0xF0000000u) == 0x70000000u) return wo;
            foreach (var info in real[0].Spawned.Values)
            {
                var child = info.TryGetWorldObject();
                if (child != null) return child;
            }
            return wo;
        }

        static void SetProfilePose(GeneratorProfile p, ACE.Entity.Position at)
        {
            p.Biota.ObjCellId = at.Cell;
            p.Biota.OriginX = at.PositionX; p.Biota.OriginY = at.PositionY; p.Biota.OriginZ = at.PositionZ;
            p.Biota.AnglesW = at.RotationW; p.Biota.AnglesX = at.RotationX; p.Biota.AnglesY = at.RotationY; p.Biota.AnglesZ = at.RotationZ;
        }

        static ACE.Entity.Position ProfilePose(GeneratorProfile p) => new ACE.Entity.Position(p.Biota.ObjCellId ?? 0,
            p.Biota.OriginX ?? 0f, p.Biota.OriginY ?? 0f, p.Biota.OriginZ ?? 0f,
            p.Biota.AnglesX ?? 0f, p.Biota.AnglesY ?? 0f, p.Biota.AnglesZ ?? 0f, p.Biota.AnglesW ?? 1f);

        /// <summary>DecorSpawn, before a linked row spawns: false = the layout removed it. A moved row spawns where it was put.</summary>
        internal static bool RowMaySpawn(GeneratorProfile p)
        {
            lock (s_Lock)
            {
                Load();
                if ((p.Id & 0xF0000000u) != 0x70000000u)
                    return !(Get($"s:{p.Generator.Guid.Full:X8}") is JsonObject g && g["spawn0"] != null && g.ContainsKey("after") && g["after"] == null);
                if (!(Get($"s:{p.Id:X8}") is JsonObject e) || !e.ContainsKey("after")) return true;
                if (e["after"] == null) return false;
                SetProfilePose(p, PosFrom((JsonObject)e["after"]!));
                return true;
            }
        }

        /// <summary>DecorSpawn, after: the layout's size on what just spawned.</summary>
        internal static void RowSpawned(GeneratorProfile p, List<WorldObject> spawned)
        {
            if (spawned == null) return;
            lock (s_Lock)
            {
                Load();
                if ((p.Id & 0xF0000000u) != 0x70000000u)
                {
                    if (Get($"s:{p.Generator.Guid.Full:X8}") is JsonObject g && g["spawn"] is JsonObject at)
                        foreach (var wo in spawned) Place(wo, PosFrom(at), _ => { });
                    return;
                }
                if (!(Get($"s:{p.Id:X8}") is JsonObject e) || e["scale"] == null) return;
                foreach (var wo in spawned) ApplyScale(wo, N(e["scale"]));
            }
        }

        static void ApplyScale(WorldObject wo, float scale)
        {
            wo.ObjScale = scale;
            wo.PhysicsObj?.SetScaleStatic(scale);
            if (wo.CurrentLandblock != null) wo.EnqueueBroadcast(new GameMessageUpdateObject(wo));
        }

        /// <summary>Why a live object can't be decorated, for the panel at the click; null = it can.</summary>
        static string WhyNot(WorldObject wo)
        {
            if (wo is Player) return "Players can't be decorated.";
            if (KeyOf(wo, out _) != null) return null;
            if (wo is Creature c && c.Attackable) return $"{wo.Name} is a wandering monster from a spawn point: there is no fixed one to move or remove.";
            if (wo.Generator != null) return $"{wo.Name} is picked at random by a generator, so there is no fixed one to move or remove.";
            return $"{wo.Name} isn't part of the town's layout (something dropped or spawned).";
        }

        /// <summary>A size is a weenie property, so a scaled row publishes as a copy of its weenie, and that copy has no emotes:
        /// anything that talks, opens or teleports keeps its size.</summary>
        static string WhyNoScale(WorldObject wo)
        {
            if (s_Added.ContainsKey(wo.Guid.Full)) return null;
            if (wo is Creature) return "NPCs and creatures keep their size.";
            if (GenRowOf(wo) != null) return $"{wo.Name} keeps its size: a spawn point makes it fresh each time.";
            if (wo is Door || wo is Portal || wo is Lifestone || wo is Container) return $"{wo.Name} keeps its size: it does something when used.";
            if (wo.Biota?.PropertiesEmote?.Count > 0) return $"{wo.Name} keeps its size: it reacts to players.";
            return null;
        }

        static void Info(Session s, Player me, string[] a)
        {
            uint guid = Hex(a[1]);
            var wo = Find(me, guid);
            var o = new JsonObject { ["k"] = "info", ["guid"] = $"0x{guid:X8}" };
            if (wo == null) { o["why"] = "That object is not here any more."; Answer(s, o); return; }
            string why = WhyNot(wo);
            if (why != null) o["why"] = why;
            else
            {
                string key = KeyOf(wo, out _);
                o["key"] = key;
                if (key.StartsWith("s:")) o["row"] = "0x" + key.Substring(2);
                string noScale = WhyNoScale(wo);
                if (noScale != null) o["noscale"] = noScale;
                o["scale"] = R(wo.ObjScale ?? 1f);
                if (LockedName(key) != null) o["locked"] = true;
            }
            o["wcid"] = wo.WeenieClassId;
            // its drawing, so a kit of Turbine's things can show them while placing (F7 261124-0004)
            if (wo.SetupTableId != 0) o["setup"] = $"0x{wo.SetupTableId:X8}";
            string noCopy = WhyNoCopy(wo);
            if (noCopy != null) o["nocopy"] = noCopy;
            Answer(s, o);
        }

        static JsonArray s_InfoInto;

        static void Answer(Session s, JsonObject o) { if (s_InfoInto != null) s_InfoInto.Add(o); else Send(s, o); }

        /// <summary>@decor audit (test shard): every object in the landblocks around you is really moved, moved back, removed and
        /// restored through the same verbs the panel sends, and the result written to decor/audit.json. The layer ends as it began.</summary>
        static void Audit(Player me, Action<string> reply)
        {
            uint bx = (uint)me.Location.LandblockId.Landblock >> 8 & 0xFFu, by = (uint)me.Location.LandblockId.Landblock & 0xFFu;
            var all = new List<WorldObject>();
            foreach (var l in LandblockManager.GetLoadedLandblocks())
            {
                uint id = (uint)l.Id.Landblock;
                if (Math.Abs((int)(id >> 8 & 0xFF) - (int)bx) > 1 || Math.Abs((int)(id & 0xFF) - (int)by) > 1) continue;
                all.AddRange(l.GetAllWorldObjectsForDiagnostics().Where(w => !(w is Player)));
            }
            string before = AllEditsJson();
            var rows = new JsonArray();
            string P(ACE.Entity.Position q, float dx) => string.Format(CultureInfo.InvariantCulture, "0x{0:X8} {1} {2} {3} {4} {5} {6} {7}",
                q.Cell, q.PositionX + dx, q.PositionY, q.PositionZ, q.RotationW, q.RotationX, q.RotationY, q.RotationZ);
            foreach (var wo in all)
            {
                string said = null;
                void Note(string t) => said = t;
                bool hidden = wo.Visibility || wo.IsGenerator || (wo.PhysicsObj != null && wo.PhysicsObj.State.HasFlag(PhysicsState.Hidden));
                string key = KeyOf(wo, out bool placed), why = WhyNot(wo);
                var o = new JsonObject
                {
                    ["guid"] = $"0x{wo.Guid.Full:X8}", ["wcid"] = wo.WeenieClassId, ["name"] = wo.Name, ["type"] = wo.WeenieType.ToString(),
                    ["hidden"] = hidden, ["generator"] = wo.IsGenerator, ["spawned"] = wo.Generator != null, ["key"] = key, ["why"] = why,
                };
                rows.Add(o);
                if (hidden || why != null || key == null || !placed) continue;
                uint guid = wo.Guid.Full, row = Hex(key.Substring(2));
                bool had = Get(key) != null;
                if (had) { o["skipped"] = "already in the layout"; continue; }
                var home = new ACE.Entity.Position(wo.Location);
                var rowHome = ProfileOf(wo) is GeneratorProfile rp && (rp.Id & 0xF0000000u) == 0x70000000u ? ProfilePose(rp) : home;   // where its row says
                try
                {
                    Move(null, me, ("move 0x" + guid.ToString("X8") + " " + P(home, 0.2f)).Split(' '), Note);
                    o["moved"] = Math.Abs(wo.Location.PositionX - home.PositionX - 0.2f) < 0.02f;
                    if (said != null) o["moveSaid"] = said;
                    said = null;
                    Move(null, me, ("move 0x" + guid.ToString("X8") + " " + P(home, 0f)).Split(' '), Note);
                    Remove(me, new[] { "remove", "0x" + guid.ToString("X8") }, Note);
                    bool gone = Find(me, guid) == null && (row == guid || Find(me, row) == null);
                    o["removed"] = gone;
                    Restore(me, new[] { "restore", "0x" + row.ToString("X8") }, Note);
                    var back = Find(me, row);
                    o["restored"] = back != null && (back.IsGenerator || back.Location.DistanceTo(rowHome) < 0.6f);
                    if (back == null) o["restoreSaid"] = said;
                    Del(key);
                }
                catch (Exception e) { o["error"] = e.GetType().Name + ": " + e.Message; Del(key); }
            }
            File.WriteAllText(Path.Combine(Dir, "audit.json"), rows.ToJsonString());
            reply($"audit: {rows.Count} objects; layout unchanged: {before == AllEditsJson()}");
        }

        static void Scale(Session s, Player me, string[] a, Action<string> reply)
        {
            uint guid = Hex(a[1]);
            float size = Mathf01(F(a[2]));
            var wo = Find(me, guid);
            if (wo == null) { reply("That object is not here any more."); return; }
            string why = WhyNot(wo) ?? WhyNoScale(wo);
            if (why != null) { reply(why); return; }
            string key = KeyOf(wo, out bool placed);
            if (Locked(key, reply)) return;
            float was = wo.ObjScale ?? 1f;
            var here = PoseOf(wo.Location);
            var e = Entry(key, () => new JsonObject
            {
                ["kind"] = placed ? "server" : "add", ["guid"] = placed ? "0x" + key.Substring(2) : $"0x{guid:X8}", ["wcid"] = wo.WeenieClassId,
                ["name"] = wo.Name, ["before"] = here, ["after"] = JsonNode.Parse(here.ToJsonString()),
            });
            if (placed && !e.ContainsKey("scale0")) e["scale0"] = R(was);
            e["scale"] = R(size);
            ApplyScale(wo, size);
        }

        static float Mathf01(float v) => Math.Clamp(v, 0.1f, 10f);

        /// <summary>A map piece's size: the client draws it; the layer keeps it (decor.py publishes a sized piece as a prop).</summary>
        static void PieceScale(string[] a)
        {
            if (Locked(a[1], null)) return;
            if (Get(a[1]) is JsonObject e) e["scale"] = R(Mathf01(F(a[2])));
        }

        static void Move(Session s, Player me, string[] a, Action<string> reply)
        {
            uint guid = Hex(a[1]);
            var pose = Pose(a, 2);
            if (WhyNotCell(pose, reply)) return;
            var wo = SpawnOf(Find(me, guid));
            if (wo == null) { reply("That object is not here any more."); return; }
            string why = WhyNot(wo);
            if (why != null) { reply(why); return; }
            string key = KeyOf(wo, out bool placed);
            if (Locked(key, reply)) return;
            var gen = GenRowOf(wo);
            if (gen != null && key == $"s:{gen.Guid.Full:X8}") { MoveWithGenerator(wo, gen, pose, key, reply); return; }
            var profile = placed ? ProfileOf(wo) : null;
            // the row's own pose for a spawn (it may have scattered); written only once the move worked: no "after" must never exist
            var first = PoseOf(profile != null ? ProfilePose(profile) : wo.Location);
            if (!Place(wo, pose, reply)) return;
            var e = Entry(key, () => new JsonObject
            {
                ["kind"] = placed ? "server" : "add", ["guid"] = placed ? "0x" + key.Substring(2) : $"0x{guid:X8}", ["wcid"] = wo.WeenieClassId,
                ["name"] = wo.Name, ["before"] = first,
            });
            e["after"] = PoseOf(wo.Location);
            if (profile != null) SetProfilePose(profile, wo.Location);
            // back on its original spot with its own size: no longer an edit (an undo must not leave a row in the datafix)
            if (placed && e["scale"] == null && e["before"] is JsonObject b && wo.Location.DistanceTo(PosFrom(b)) < 0.005f
                && Math.Abs(Math.Abs(wo.Location.RotationW * N(b["qw"]) + wo.Location.RotationZ * N(b["qz"])) - 1f) < 0.0005f)
                Del(key);
            if (s != null) Send(s, new JsonObject { ["k"] = "moved", ["key"] = key, ["guid"] = $"0x{guid:X8}" });
        }

        /// <summary>What a one-thing generator made moves; its row follows by the same step, so a respawn lands in the same place.</summary>
        static void MoveWithGenerator(WorldObject wo, WorldObject gen, ACE.Entity.Position pose, string key, Action<string> reply)
        {
            var was = new ACE.Entity.Position(wo.Location);
            var genFirst = PoseOf(gen.Location);
            var spawnFirst = PoseOf(wo.Location);
            if (!Place(wo, pose, reply)) return;
            var global = wo.Location.ToGlobal() - was.ToGlobal();
            var to = new ACE.Entity.Position(gen.Location);
            to.PositionX += global.X; to.PositionY += global.Y; to.PositionZ += global.Z;
            if (!to.Indoors && (to.PositionX < 0f || to.PositionX >= 192f || to.PositionY < 0f || to.PositionY >= 192f))
            {
                Place(wo, was, _ => { });
                reply($"{wo.Name} can't leave its landblock: its spawn point stays in this one.");
                return;
            }
            if (!Place(gen, to, reply)) { Place(wo, was, _ => { }); return; }
            var e = Entry(key, () => new JsonObject
            {
                ["kind"] = "server", ["guid"] = "0x" + key.Substring(2), ["wcid"] = gen.WeenieClassId, ["name"] = wo.Name,
                ["before"] = genFirst, ["spawn0"] = spawnFirst,
            });
            e["after"] = PoseOf(gen.Location);
            e["spawn"] = PoseOf(wo.Location);
            if (e["spawn0"] is JsonObject s0 && wo.Location.DistanceTo(PosFrom(s0)) < 0.005f
                && Math.Abs(Math.Abs(wo.Location.RotationW * N(s0["qw"]) + wo.Location.RotationZ * N(s0["qz"])) - 1f) < 0.0005f)
                Del(key);
        }

        static void Remove(Player me, string[] a, Action<string> reply)
        {
            uint guid = Hex(a[1]);
            var wo = SpawnOf(Find(me, guid));
            if (wo == null) { reply("That object is not here any more."); return; }
            string why = WhyNot(wo);
            if (why != null) { reply(why); return; }
            string key = KeyOf(wo, out bool placed);
            if (Locked(key, reply)) return;
            if (!placed) { Unadd(me, new[] { "unadd", key.Substring(2) }, reply); return; }
            var gen = GenRowOf(wo);
            if (gen != null && key == $"s:{gen.Guid.Full:X8}")
            {
                var g = Entry(key, () => new JsonObject
                {
                    ["kind"] = "server", ["guid"] = "0x" + key.Substring(2), ["wcid"] = gen.WeenieClassId, ["name"] = wo.Name,
                    ["before"] = PoseOf(gen.Location), ["spawn0"] = PoseOf(wo.Location),
                });
                g["after"] = null;
                string name = wo.Name;
                wo.Destroy();
                gen.Destroy();
                reply($"{name} removed (restore brings it back).");
                return;
            }
            var profile = ProfileOf(wo);
            var e = Entry(key, () => new JsonObject
            {
                ["kind"] = "server", ["guid"] = "0x" + key.Substring(2), ["wcid"] = wo.WeenieClassId, ["name"] = wo.Name,
                ["before"] = PoseOf(profile != null ? ProfilePose(profile) : wo.Location),
            });
            e["after"] = null;   // first: the generator's respawn asks DecorSpawn, which reads this
            wo.Destroy();
            reply($"{wo.Name} removed (restore brings it back).");
        }

        /// <summary>A removed placed object back where it was first seen (undo of remove); a moved one back too.</summary>
        static void Restore(Player me, string[] a, Action<string> reply)
        {
            uint guid = Hex(a[1]);
            var named = SpawnOf(Find(me, guid));   // a live spawn's guid or a row's
            string key = named != null ? KeyOf(named, out _) ?? $"s:{guid:X8}" : $"s:{guid:X8}";
            if (Locked(key, reply)) return;
            if (!(Get(key) is JsonObject e)) { reply("Nothing to restore."); return; }
            var before = (JsonObject)e["before"]!;
            uint row = Hex(key.Substring(2));
            var wo = named;
            var profile = ProfileOfRow(row);
            if (profile != null)
            {
                Del(key);   // first: its spawn must no longer be held back
                SetProfilePose(profile, PosFrom(before));
                if (wo == null)
                {
                    profile.SpawnQueue.Add(DateTime.UtcNow);
                    profile.ProcessQueue();
                    wo = Find(me, row);
                }
                if (wo != null) Place(wo, PosFrom(before), reply);   // a scatter spawn lands near: this puts it on the row's spot
                if (wo != null && e["scale0"] != null) ApplyScale(wo, N(e["scale0"]));
                reply($"{wo?.Name ?? (string)e["name"]} is back where it was.");
                return;
            }
            if (e["spawn0"] != null && Find(me, row) is WorldObject liveGen)
            {
                // moved, not removed: the generator and what it made go back
                if (!Place(liveGen, PosFrom(before), reply)) return;
                if (wo != null && wo != liveGen) Place(wo, PosFrom((JsonObject)e["spawn0"]!), reply);
                Del(key);
                reply($"{(string)e["name"]} is back where it was.");
                return;
            }
            if (wo == null)
            {
                wo = WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie((uint)D(e["wcid"])), new ObjectGuid(row));
                if (wo == null) { reply("Could not make it again."); return; }
                wo.Location = PosFrom(before);
                wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
                // it stood here before: nothing nearby may refuse it (a portal beside a wall came back as "could not place")
                bool wasEthereal = wo.Ethereal ?? false;
                wo.Ethereal = true;
                Del(key);   // first: a generator's own first spawn must not be held back
                if (!wo.EnterWorld())
                {
                    // ACE's placement refuses a spot in the air or against a wall that the world's own load accepts (a portal above
                    // its steps). So it enters at your feet, where placement always works, and goes straight back into its cell.
                    wo.Destroy();
                    wo = WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie((uint)D(e["wcid"])), new ObjectGuid(row));
                    wo.Ethereal = true;
                    wo.Location = new ACE.Entity.Position(me.Location);
                    if (wo == null || !wo.EnterWorld() || !Place(wo, PosFrom(before), reply))
                    {
                        Put(key, (JsonObject)JsonNode.Parse(e.ToJsonString())!);
                        wo?.Destroy();
                        reply("Could not place it again.");
                        return;
                    }
                }
                wo.Ethereal = wasEthereal;
                wo.EnqueueBroadcastPhysicsState();
                reply($"{(string)e["name"]} is back where it was.");
                return;
            }
            else if (!Place(wo, PosFrom(before), reply)) return;
            if (e["scale0"] != null) ApplyScale(wo, N(e["scale0"]));
            Del(key);
            reply($"{wo.Name} is back where it was.");
        }

        /// <summary>add &lt;wcid&gt; &lt;pose&gt; [size]: a Stuck copy of a retail weenie (the palette, a copy of something in the world, a
        /// kit, a line, a ring or a brush stroke).</summary>
        static void Add(Session s, Player me, string[] a, Action<string> reply)
        {
            uint wcid = uint.Parse(a[1], CultureInfo.InvariantCulture);
            var pose = Pose(a, 2);
            string badCell = null;
            if (WhyNotCell(pose, t => badCell = t))
            {
                // answered as a refusal: the client queued this add and pairs the next answer with it
                reply(badCell);
                if (s_BatchAdded == null && s != null) Send(s, new JsonObject { ["k"] = "refused", ["wcid"] = wcid, ["why"] = badCell });
                return;
            }
            float? size = a.Length > 10 ? Mathf01(F(a[10])) : (float?)null;
            string why = null;
            var wo = MakeAdd(wcid, pose, size, t => { why = t; reply(t); });
            if (wo == null)
            {
                // the client queued this add: say so, or its next "added" answer was taken for this one. A hidden trigger's
                // look can still go down as a map piece (F7 261123-0014): "piece" asks the client to do that instead.
                var no = new JsonObject { ["k"] = "refused", ["wcid"] = wcid, ["why"] = why };
                if (why != null && why.Contains(HIDDEN_TRIGGER)) no["piece"] = true;
                if (s_BatchAdded == null && s != null) Send(s, no);
                return;
            }
            string key = $"a:{NextAdd()}";
            var e = new JsonObject
            {
                ["kind"] = "add", ["wcid"] = wcid, ["name"] = wo.Name, ["guid"] = $"0x{wo.Guid.Full:X8}", ["after"] = PoseOf(wo.Location),
            };
            if (size.HasValue) e["scale"] = R(size.Value);
            Put(key, e);
            s_Added[wo.Guid.Full] = key;
            var msg = new JsonObject { ["k"] = "added", ["key"] = key, ["guid"] = $"0x{wo.Guid.Full:X8}", ["name"] = wo.Name };
            if (s_BatchAdded != null) s_BatchAdded.Add(msg);
            else if (s != null) Send(s, msg);
        }

        /// <summary>Things a copy must never be: anything that walks, talks back as a creature, teleports or spawns.</summary>
        static string WhyNoCopy(WorldObject wo)
        {
            if (wo is Creature) return "New NPCs and monsters can't be placed: copy props and map pieces.";
            // the "flat dirt floor patch" props (Tusker's Friend, The Tunnel, Scold ...) are invisible pressure plates: no client
            // is sent one, so a click placed nothing anyone could see, and stepping on one fires its trap (F7 261123-0014)
            if (wo is PressurePlate || wo.Visibility)
                return $"{wo.Name} is {HIDDEN_TRIGGER} (a trap that fires when stepped on), so it can't be placed as a prop.";
            if (wo is Portal || wo is Door || wo is Lifestone || wo.IsGenerator || wo is House || wo is Hook || wo is Storage)
                return $"{wo.Name} does something when used, so it can't be copied.";
            return null;
        }

        const string HIDDEN_TRIGGER = "an invisible trigger";

        static WorldObject MakeAdd(uint wcid, ACE.Entity.Position pose, float? size, Action<string> reply)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { reply($"No such thing ({wcid})."); return null; }
            string no = WhyNoCopy(wo);
            if (no != null) { wo.Destroy(); reply(no); return null; }
            wo.SetProperty(PropertyBool.Stuck, true);   // as the datafix clones it: nobody picks it up
            wo.TimeToRot = -1;
            if (size.HasValue) wo.ObjScale = size.Value;
            wo.Location = new ACE.Entity.Position(pose);
            wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
            if (!wo.EnterWorld()) { wo.Destroy(); reply($"{wo.Name} could not be placed there."); return null; }
            return wo;
        }

        /// <summary>a:n keys are unique over every area (decor.py and the client look them up by key alone).</summary>
        static int NextAdd()
        {
            int n = 1;
            foreach (var ar in s_Areas.Values)
            {
                n = Math.Max(n, (int)(ar.Layer["next"]?.GetValue<int>() ?? 1));
                // a stale "next" (a hand-edited or old file) never hands out a key an edit already has (Codex review 11-23)
                if (ar.Layer["edits"] is JsonObject ed)
                    foreach (var kv in ed)
                        if (kv.Key.StartsWith("a:") && int.TryParse(kv.Key.Substring(2), out var k)) n = Math.Max(n, k + 1);
            }
            s_Cur.Layer["next"] = n + 1;
            return n;
        }

        static void Unadd(Player me, string[] a, Action<string> reply)
        {
            string key = a[1].StartsWith("a:") ? a[1] : "a:" + a[1];
            if (Locked(key, reply)) return;
            if (!(Get(key) is JsonObject e)) { reply("Nothing to take away."); return; }
            uint guid = Hex((string)e["guid"]!);
            var wo = s_Added.ContainsKey(guid) ? Find(me, guid) : null;
            // Not found = not in any loaded landblock; an added prop is never saved (DecorNoSave), so it is gone for good
            // and the entry can go too. Found: destroyed first, then forgotten.
            wo?.Destroy();
            Del(key);
            s_Added.Remove(guid);
            if (s_BatchAdded == null) reply(wo != null ? $"{wo.Name} taken away." : "Taken away (it was no longer in the world).");
        }

        // ---- DAT statics: the client draws them; the server only keeps the layer ----

        static void Static(string[] a, Action<string> reply)
        {
            string key = $"t:{a[1].ToUpperInvariant()}:{a[2]}";
            if (Locked(key, reply)) return;
            if (a[4] == "reset") { Del(key); return; }
            // a ROOM's object (decor-world): <blk> is then its cell (more than four hex digits), the entry names the cell, and the
            // pose must be inside the rooms (decor.py rewrites that cell's object list in the overlay)
            uint where = Hex(a[1]);
            bool room = where > 0xFFFF;
            if (room && WhyNotRoom(where, reply)) return;
            if (a[4] != "gone" && WhyNotCell(Pose(a, 4), reply)) return;
            var e = Entry(key, () => new JsonObject { ["kind"] = "static", ["block"] = a[1].ToUpperInvariant(), ["id"] = a[2], ["model"] = a[3] });
            if (room) e["cell"] = $"0x{where:X8}";
            int from;
            if (a[4] == "gone") { e["after"] = null; from = 5; }
            else { e["after"] = PoseOf(Pose(a, 4)); from = 12; }
            if (e["before"] == null && a.Length > from + 8 && a[from] == "from") e["before"] = PoseOf(Pose(a, from + 1));
        }

        /// <summary>staticadd &lt;id&gt; &lt;model&gt; &lt;pose|gone&gt; [size]</summary>
        static void StaticAdd(string[] a, Action<string> reply)
        {
            string key = "n:" + a[1];
            if (Locked(key, reply)) return;
            if (a[3] == "gone") { Del(key); return; }
            if (WhyNotCell(Pose(a, 3), reply)) return;
            var e = new JsonObject { ["kind"] = "staticadd", ["model"] = a[2], ["after"] = PoseOf(Pose(a, 3)) };
            if (a.Length > 11) e["scale"] = R(Mathf01(F(a[11])));
            else if (Get(key) is JsonObject old && old["scale"] != null) e["scale"] = old["scale"]!.DeepClone();
            Put(key, e);
        }

        // ---- helpers ----

        static readonly Dictionary<uint, string> s_Added = new Dictionary<uint, string>();

        /// <summary>A prop this tool added (this server run). ACE saves every dynamic object lying on a landblock to the
        /// shard database (WorldObject.IsDynamicThatShouldPersistToShard), so without <see cref="DecorNoSave"/> an added
        /// prop outlived its layout entry: three test Wells stayed on the box shard and blocked 0.1.125's regression set
        /// (2026-11-22). The layout is the only record; Reapply puts the additions back after a restart.</summary>
        internal static bool IsAdded(WorldObject wo)
        {
            if (wo == null) return false;
            lock (s_Lock) return s_Added.ContainsKey(wo.Guid.Full);
        }

        /// <summary>The layer key of a live object: its row (a placed object's own guid, or a generator's linked row it was spawned
        /// from), or an object this tool added. Null for a generator's random pick or something loose in the world.</summary>
        static string KeyOf(WorldObject wo, out bool placed)
        {
            uint g = wo.Guid.Full;
            placed = (g & 0xF0000000u) == 0x70000000u;
            if (placed) return $"s:{g:X8}";
            if (s_Added.TryGetValue(g, out var k)) return k;
            var p = ProfileOf(wo);
            if (p != null && (p.Id & 0xF0000000u) == 0x70000000u) { placed = true; return $"s:{p.Id:X8}"; }
            var gen = GenRowOf(wo);
            if (gen == null) return null;
            placed = true;
            return $"s:{gen.Guid.Full:X8}";
        }

        static bool Place(WorldObject wo, ACE.Entity.Position pos, Action<string> reply)
        {
            pos.LandblockId = new LandblockId(pos.GetCell());
            if (wo.PhysicsObj == null) { reply($"{wo.Name} has no body in the world."); return false; }
            // A decorator puts a thing where it is told: straight into the cell, no walking transition. SetPosition's transition
            // refused a painting flush on a wall or an NPC beside a table ("Collided", "NoValidPosition") while the client showed
            // it moved (F7 261122-0027, 0030).
            var phys = wo.PhysicsObj;
            var target = pos.PhysPosition();
            // indoors the origin sits ON the floor, the room's boundary: ask for the room a hand above it (decor-world)
            var cell = phys.AdjustPosition(target, pos.Indoors ? new System.Numerics.Vector3(0f, 0f, 0.1f) : System.Numerics.Vector3.Zero, false, true);
            var r = phys.ForceIntoCell(cell, target);
            if (r != ACE.Server.Physics.Common.SetPositionError.OK) { reply($"{wo.Name} can't go there ({r})."); return false; }
            phys.Position.ObjCellID = cell.ID;
            var prev = new ACE.Entity.Position(wo.Location);
            wo.Location = wo.PhysicsObj.Position.ACEPosition();
            wo.Location.Rotation = pos.Rotation;   // SetPosition keeps the frame's turn, but be exact
            if (prev.Landblock != wo.Location.Landblock) LandblockManager.RelocateObjectForPhysics(wo, true);
            wo.SendUpdatePosition(true);
            return true;
        }

        static JsonObject Entry(string key, Func<JsonObject> make)
        {
            if (Get(key) is JsonObject e) return e;
            e = make();
            Put(key, e);
            return e;
        }

        static uint Hex(string s) => uint.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s, NumberStyles.HexNumber);

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>cell x y z qw qx qy qz from a[i..i+7].</summary>
        static ACE.Entity.Position Pose(string[] a, int i) =>
            new ACE.Entity.Position(Hex(a[i]), F(a[i + 1]), F(a[i + 2]), F(a[i + 3]), F(a[i + 5]), F(a[i + 6]), F(a[i + 7]), F(a[i + 4]));

        static JsonObject PoseOf(ACE.Entity.Position p) => new JsonObject
        {
            ["cell"] = $"0x{p.Cell:X8}", ["x"] = R(p.PositionX), ["y"] = R(p.PositionY), ["z"] = R(p.PositionZ),
            ["qw"] = R(p.RotationW), ["qx"] = R(p.RotationX), ["qy"] = R(p.RotationY), ["qz"] = R(p.RotationZ),
        };

        /// <summary>A number from the layer as float: written as double, read back from the file as any JSON number.</summary>
        static float N(JsonNode n) => (float)D(n);

        /// <summary>Any JSON number, whatever type it was stored as in memory (uint, double) or parsed from the file.</summary>
        static double D(JsonNode n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);

        static ACE.Entity.Position PosFrom(JsonObject j) => new ACE.Entity.Position(Hex((string)j["cell"]!),
            N(j["x"]), N(j["y"]), N(j["z"]), N(j["qx"]), N(j["qy"]), N(j["qz"]), N(j["qw"]));

        static double R(float v) => Math.Round(v, 4);

        // ---- areas (decor v2): one layer file per area, each listing its landblocks ----

        sealed class Area
        {
            public string Id;
            public JsonObject Layer;
            public JsonArray Undo = new JsonArray(), Redo = new JsonArray();
            public JsonObject Edits => (JsonObject)Layer["edits"]!;
            public JsonObject Locks => Layer["locks"] as JsonObject ?? (JsonObject)(Layer["locks"] = new JsonObject());
            public string LayerPath => Path.Combine(Dir, Id + ".json");
            public string UndoPath => Path.Combine(Dir, Id + ".undo.json");
        }

        const int UNDO_KEEP = 100;
        const string FIRST_AREA = "holtburg";
        static Dictionary<string, Area> s_Areas;
        static Area s_Cur;                                                     // the area this command edits (under s_Lock)
        static readonly Dictionary<uint, string> s_Active = new Dictionary<uint, string>();   // player -> area id
        static List<JsonObject> s_BatchAdded;                                  // set while a batch runs: one answer for all

        static bool ValidId(string id) => id.Length > 0 && id.Length <= 40 && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

        /// <summary>Every area on this shard. holtburg.json from before areas loads untouched (its edits as they were; "blocks",
        /// "name" and "home" are added beside them). The drudge range is made once, its three target drudges locked.</summary>
        static void Load()
        {
            if (s_Areas != null) return;
            s_Areas = new Dictionary<string, Area>();
            Directory.CreateDirectory(Dir);
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (id.EndsWith(".undo", StringComparison.Ordinal) || !ValidId(id)) continue;
                try
                {
                    if (!(JsonNode.Parse(File.ReadAllText(f)) is JsonObject layer) || !(layer["edits"] is JsonObject)) continue;
                    var ar = new Area { Id = id, Layer = layer };
                    if (File.Exists(ar.UndoPath) && JsonNode.Parse(File.ReadAllText(ar.UndoPath)) is JsonObject u)
                    {
                        if (u["undo"] is JsonArray ua) ar.Undo = (JsonArray)ua.DeepClone();
                        if (u["redo"] is JsonArray ra) ar.Redo = (JsonArray)ra.DeepClone();
                    }
                    s_Areas[id] = ar;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Decor: {f} unreadable, left alone: {e.Message}"); }
            }
            if (!s_Areas.ContainsKey(FIRST_AREA))
                s_Areas[FIRST_AREA] = new Area { Id = FIRST_AREA, Layer = new JsonObject { ["area"] = FIRST_AREA, ["next"] = 1, ["edits"] = new JsonObject() } };
            if (!s_Areas.ContainsKey("drudge-range") && !File.Exists(Path.Combine(Dir, "drudge-range.json")))
            {
                // owner 2026-11-23: "decorate the drudge target practice area first". The three Pyreal Target Drudges
                // (tools/public-shard/datafixes/20261123-3-holtburg-dummies-move.sql) start locked.
                var locks = new JsonObject();
                foreach (var g in new[] { "7A9B4E70", "7A9B4E71", "7A9B4E72" }) locks["s:" + g] = "Pyreal Target Drudge";
                s_Areas["drudge-range"] = new Area
                {
                    Id = "drudge-range",
                    Layer = new JsonObject
                    {
                        ["area"] = "drudge-range", ["name"] = "Drudge range (42.2N 33.8E)", ["blocks"] = new JsonArray("A9B4"),
                        ["home"] = PoseOf(new ACE.Entity.Position(0xA9B4002Au, 132f, 28f, 94.005f, 0f, 0f, 0f, 1f)),
                        ["next"] = 1, ["edits"] = new JsonObject(), ["locks"] = locks,
                    },
                };
            }
            foreach (var ar in s_Areas.Values) Migrate(ar);
        }

        /// <summary>Fields an area has always had since v2, filled in for a layer from before (its edits are not touched).</summary>
        static void Migrate(Area ar)
        {
            var l = ar.Layer;
            l["area"] ??= ar.Id;
            if (l["name"] == null) l["name"] = ar.Id == FIRST_AREA ? "Holtburg" : ar.Id;
            if (l["home"] == null && ar.Id == FIRST_AREA)   // Holtburg's lifestone
                l["home"] = PoseOf(new ACE.Entity.Position(0xA9B40019u, 86f, 4f, 94.005f, 0f, 0f, 0.707107f, 0.707107f));
            var blocks = l["blocks"] as JsonArray ?? new JsonArray();
            var have = new HashSet<string>(blocks.Select(x => (string)x!));
            if (ar.Id == FIRST_AREA) have.Add("A9B4");
            foreach (var kv in ar.Edits) { uint b = BlockOfEdit(kv.Key, kv.Value as JsonObject); if (b != 0) have.Add($"{b:X4}"); }
            if (have.Count != blocks.Count) l["blocks"] = new JsonArray(have.OrderBy(x => x).Select(x => (JsonNode)x).ToArray());
        }

        /// <summary>The landblock an edit is applied in: a row's own block (where the world loads it), else where it stands.</summary>
        static uint BlockOfEdit(string key, JsonObject e)
        {
            try
            {
                if (key.StartsWith("s:")) return (Hex(key.Substring(2)) >> 12) & 0xFFFF;
                if (key.StartsWith("t:")) { uint w = Hex(key.Split(':')[1]); return w > 0xFFFF ? w >> 16 : w; }   // a room's: its cell
                if (e?["after"] is JsonObject a) return Hex((string)a["cell"]!) >> 16;
                if (e?["before"] is JsonObject b) return Hex((string)b["cell"]!) >> 16;
            }
            catch { }
            return 0;
        }

        static Area AreaOfKey(string key)
        {
            foreach (var ar in s_Areas.Values) if (ar.Edits.ContainsKey(key)) return ar;
            return null;
        }

        /// <summary>An edit wherever it lives (every area: an object belongs to the area that first touched it).</summary>
        static JsonObject Get(string key) => AreaOfKey(key)?.Edits[key] as JsonObject;

        static void Del(string key) { foreach (var ar in s_Areas.Values) ar.Edits.Remove(key); }

        /// <summary>Into the area that already holds it, else the area being decorated (which then covers its landblock).</summary>
        static void Put(string key, JsonObject e, Area into = null)
        {
            var ar = AreaOfKey(key) ?? into ?? s_Cur ?? s_Areas[FIRST_AREA];
            if (into != null && ar != into) { ar.Edits.Remove(key); ar = into; }
            ar.Edits[key] = e;
            uint b = BlockOfEdit(key, e);
            var blocks = ar.Layer["blocks"] as JsonArray;
            if (b != 0 && blocks != null && !blocks.Any(x => (string)x! == $"{b:X4}")) blocks.Add($"{b:X4}");
        }

        static string AllEditsJson() => string.Join("|", s_Areas.Values.OrderBy(a => a.Id).Select(a => a.Edits.ToJsonString()));

        static Area ActiveOf(Player me)
        {
            if (me != null && s_Active.TryGetValue(me.Guid.Full, out var id) && s_Areas.TryGetValue(id, out var ar)) return ar;
            return s_Areas[FIRST_AREA];
        }

        // ---- locks: a padlocked thing refuses move, turn, size and remove ----

        static string LockedName(string key)
        {
            if (key == null) return null;
            foreach (var ar in s_Areas.Values) if (ar.Locks[key] is JsonNode n) return (string)n ?? "It";
            return null;
        }

        static bool Locked(string key, Action<string> reply)
        {
            string n = LockedName(key);
            if (n == null) return false;
            reply?.Invoke($"{n} is locked. Press Unlock first.");
            return true;
        }

        /// <summary>lock on|off &lt;0xguid | key&gt;... (or lock &lt;target&gt; on|off)</summary>
        static void Lock(Player me, string[] a, Action<string> reply)
        {
            if (a.Length > 2 && (a[1] == "on" || a[1] == "off"))
            {
                foreach (var t in a.Skip(2)) Lock(me, new[] { "lock", t, a[1] }, reply);
                return;
            }
            if (a.Length < 2) return;
            string key = a[1], name = null;
            if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var wo = SpawnOf(Find(me, Hex(key)));
                if (wo == null) { reply("That object is not here any more."); return; }
                key = KeyOf(wo, out _);
                if (key == null) { reply(WhyNot(wo) ?? "That can't be locked."); return; }
                name = wo.Name;
            }
            bool on = a.Length < 3 || a[2] != "off";
            foreach (var ar in s_Areas.Values) ar.Locks.Remove(key);
            if (on) (AreaOfKey(key) ?? s_Cur).Locks[key] = name ?? (string)Get(key)?["name"] ?? "This map piece";
        }

        // ---- undo: each command is one step, kept per area on disk (survives closing Decorate, a relog, a restart) ----

        static Dictionary<string, (string area, string json)> Snapshot()
        {
            var d = new Dictionary<string, (string, string)>();
            foreach (var ar in s_Areas.Values) foreach (var kv in ar.Edits) d[kv.Key] = (ar.Id, kv.Value?.ToJsonString());
            return d;
        }

        /// <summary>What one command changed, as each edit's state before and after: undo puts the before back.</summary>
        static bool EndStep(string label, Dictionary<string, (string area, string json)> before, Area into)
        {
            var after = Snapshot();
            var list = new JsonArray();
            foreach (var key in before.Keys.Union(after.Keys))
            {
                before.TryGetValue(key, out var b); after.TryGetValue(key, out var f);
                if (b.json == f.json) continue;
                list.Add(new JsonObject
                {
                    ["k"] = key, ["a"] = f.area ?? b.area,
                    ["b"] = b.json == null ? null : JsonNode.Parse(b.json), ["f"] = f.json == null ? null : JsonNode.Parse(f.json),
                });
            }
            if (list.Count == 0) return false;
            into.Undo.Add(new JsonObject { ["label"] = label, ["at"] = DateTime.UtcNow.ToString("o"), ["edits"] = list });
            while (into.Undo.Count > UNDO_KEEP) into.Undo.RemoveAt(0);
            into.Redo.Clear();
            return true;
        }

        /// <summary>undo | redo: the active area's last step, every edit in it back to its state before (after).</summary>
        static void UndoRedo(Player me, bool undo, Action<string> reply)
        {
            var from = undo ? s_Cur.Undo : s_Cur.Redo;
            var to = undo ? s_Cur.Redo : s_Cur.Undo;
            if (from.Count == 0) { reply(undo ? "Nothing to undo." : "Nothing to redo."); return; }
            var step = (JsonObject)from[from.Count - 1]!;
            from.RemoveAt(from.Count - 1);
            var edits = ((JsonArray)step["edits"]!).Select(x => (JsonObject)x!).ToList();
            if (undo) edits.Reverse();
            int skipped = 0;
            foreach (var ed in edits)
            {
                string key = (string)ed["k"]!;
                if (LockedName(key) != null) { skipped++; continue; }
                s_Areas.TryGetValue((string)ed["a"] ?? s_Cur.Id, out var ar);
                Reconcile(me, key, (undo ? ed["b"] : ed["f"]) as JsonObject, ar ?? s_Cur, reply);
            }
            to.Add(step);
            reply($"{(undo ? "Undid" : "Redid")} {(string)step["label"]}" + (skipped > 0 ? $" ({skipped} locked thing(s) left as they are)" : ""));
        }

        /// <summary>revert: every unlocked edit of the area taken away (one step, so Undo brings it all back).</summary>
        static void Revert(Player me, Action<string> reply)
        {
            int n = 0, kept = 0;
            foreach (var key in s_Cur.Edits.Select(kv => kv.Key).ToList())
            {
                if (LockedName(key) != null) { kept++; continue; }
                Reconcile(me, key, null, s_Cur, _ => { });
                n++;
            }
            reply($"{s_Cur.Layer["name"]}: {n} change(s) taken back" + (kept > 0 ? $", {kept} locked kept" : "") + ". Undo brings them back.");
        }

        static string[] PoseArgs(JsonObject p) => new[]
        {
            (string)p["cell"]!, Num(p["x"]), Num(p["y"]), Num(p["z"]), Num(p["qw"]), Num(p["qx"]), Num(p["qy"]), Num(p["qz"]),
        };

        static string Num(JsonNode n) => D(n).ToString("0.#####", CultureInfo.InvariantCulture);

        /// <summary>Make the world and the layer match <paramref name="want"/> for one key (null = no edit: as retail has it).
        /// Undo, redo and Revert all come through here.</summary>
        static void Reconcile(Player me, string key, JsonObject want, Area into, Action<string> reply)
        {
            var cur = Get(key);
            if (cur?.ToJsonString() == want?.ToJsonString()) return;
            string kind = (string)(want ?? cur)?["kind"];
            want = want == null ? null : (JsonObject)want.DeepClone();
            if (kind == "server")
            {
                string row = "0x" + key.Substring(2);
                if (cur != null) Restore(me, new[] { "restore", row }, _ => { });
                Del(key);
                if (want == null) return;
                if (want.ContainsKey("after") && want["after"] == null) Remove(me, new[] { "remove", row }, reply);
                else if ((want["spawn"] ?? want["after"]) is JsonObject to)
                {
                    Move(null, me, new[] { "move", row }.Concat(PoseArgs(to)).ToArray(), reply);
                    if (want["scale"] != null) Scale(null, me, new[] { "scale", row, Num(want["scale"]) }, _ => { });
                }
                Put(key, want, into);
            }
            else if (kind == "add")
            {
                // moved or turned only: the same object goes there (it keeps its guid, so the client's selection holds)
                if (cur != null && want?["after"] is JsonObject wa && cur["after"] is JsonObject
                    && (want["scale"]?.ToJsonString() ?? "") == (cur["scale"]?.ToJsonString() ?? "")
                    && s_Added.ContainsKey(Hex((string)cur["guid"]!)) && Find(me, Hex((string)cur["guid"]!)) is WorldObject same
                    && Place(same, PosFrom(wa), reply))
                {
                    want["guid"] = cur["guid"]!.DeepClone();
                    Del(key);
                    Put(key, want, into);
                    return;
                }
                if (cur != null)
                {
                    uint g = Hex((string)cur["guid"]!);
                    if (s_Added.ContainsKey(g)) Find(me, g)?.Destroy();
                    s_Added.Remove(g);
                    Del(key);
                }
                if (want == null || !(want["after"] is JsonObject after)) return;
                var wo = MakeAdd((uint)D(want["wcid"]), PosFrom(after), want["scale"] != null ? N(want["scale"]) : (float?)null, reply);
                if (wo == null) return;
                want["guid"] = $"0x{wo.Guid.Full:X8}";
                s_Added[wo.Guid.Full] = key;
                Put(key, want, into);
            }
            else if (kind == "way") ReconcileWay(key, cur, want, into);
            else
            {
                Del(key);
                if (want != null) Put(key, want, into);
            }
        }

        // ---- saving ----

        static void Save()
        {
            var opt = new JsonSerializerOptions { WriteIndented = true };
            foreach (var ar in s_Areas.Values)
            {
                ar.Layer["saved"] = DateTime.UtcNow.ToString("o");
                Write(ar.LayerPath, ar.Layer.ToJsonString(opt));
                if (ar.Undo.Count > 0 || ar.Redo.Count > 0 || File.Exists(ar.UndoPath))
                    Write(ar.UndoPath, new JsonObject { ["undo"] = ar.Undo.DeepClone(), ["redo"] = ar.Redo.DeepClone() }.ToJsonString());
            }
        }

        static void Write(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }

        // ---- what the client is told ----

        static JsonObject AreaInfo(Area ar) => new JsonObject
        {
            ["id"] = ar.Id, ["name"] = (string)ar.Layer["name"] ?? ar.Id, ["blocks"] = ar.Layer["blocks"]?.DeepClone(),
            ["home"] = ar.Layer["home"]?.DeepClone(), ["edits"] = ar.Edits.Count, ["locks"] = ar.Locks.Count,
            ["undo"] = ar.Undo.Count, ["redo"] = ar.Redo.Count, ["world"] = ar.Layer["world"]?.DeepClone(), ["pub"] = PubStatus(ar),
            ["keys"] = new JsonArray(ar.Edits.Select(kv => (JsonNode)kv.Key).ToArray()),   // the panel's publish summary sorts by area
        };

        /// <summary>Every area's edits and locks together (the client draws every map-piece edit in view), the area list, the
        /// active one's undo and redo, and the kits.</summary>
        static void SendState(Session s, Player me)
        {
            Reapply(me);
            Save();
            var edits = new JsonObject();
            var locks = new JsonObject();
            foreach (var ar in s_Areas.Values)
            {
                foreach (var kv in ar.Edits) edits[kv.Key] = kv.Value?.DeepClone();
                foreach (var kv in ar.Locks) locks[kv.Key] = kv.Value?.DeepClone();
            }
            var cur = ActiveOf(me);
            Send(s, new JsonObject
            {
                ["k"] = "state", ["layer"] = new JsonObject { ["edits"] = edits, ["locks"] = locks },
                ["areas"] = new JsonArray(s_Areas.Values.OrderBy(a => a.Id == FIRST_AREA ? "" : a.Id).Select(a => (JsonNode)AreaInfo(a)).ToArray()),
                ["active"] = cur.Id, ["undo"] = cur.Undo.Count, ["redo"] = cur.Redo.Count,
                ["undoLabel"] = cur.Undo.Count > 0 ? cur.Undo[cur.Undo.Count - 1]!["label"]?.DeepClone() : null,
                ["kits"] = Kits(),
            });
        }

        static void SendCounts(Session s, Player me)
        {
            var cur = ActiveOf(me);
            Send(s, new JsonObject { ["k"] = "counts", ["active"] = cur.Id, ["undo"] = cur.Undo.Count, ["redo"] = cur.Redo.Count });
        }

        // ---- self-restoring: each landblock gets its edits when it loads, without anyone pressing Decorate ----

        /// <summary>DecorBlockLoad, after a landblock's own objects are in: its moves, removals and sizes, and the props added
        /// in it (each exactly once: an added prop is never saved, and one still in the world is left alone).</summary>
        internal static void ApplyBlock(Landblock lb)
        {
            if (!On) return;
            lock (s_Lock)
            {
                try
                {
                    Load();
                    uint blk = lb.Id.Landblock;
                    int n = 0;
                    foreach (var ar in s_Areas.Values)
                        foreach (var kv in ar.Edits.ToList())
                            if (kv.Value is JsonObject e && BlockOfEdit(kv.Key, e) == blk && ReapplyOne(null, kv.Key, e)) n++;
                    if (n > 0) { Save(); Mod.Log.Info($"[RevivalGuard] Decor: landblock {blk:X4} loaded, {n} edit(s) applied"); }
                }
                catch (Exception x) { Mod.Log.Warn($"[RevivalGuard] Decor block load: {x.Message}"); }
            }
        }

        /// <summary>The whole layer back on the loaded world (Decorate opened): the same per-edit rule as a block load.</summary>
        static void Reapply(Player me)
        {
            foreach (var ar in s_Areas.Values)
                foreach (var kv in ar.Edits.ToList())
                    if (kv.Value is JsonObject e) ReapplyOne(me, kv.Key, e);
        }

        /// <summary>One edit onto the world if its object is loaded and not yet as the layer says; true = something changed.</summary>
        static bool ReapplyOne(Player me, string key, JsonObject e)
        {
            string kind = (string)e["kind"]!;
            try
            {
                if (kind == "server")
                {
                    var wo = Find(me, Hex((string)e["guid"]!));
                    if (wo == null) return false;
                    if (!e.ContainsKey("after")) return false;   // never a removal by omission
                    if (e["spawn0"] != null)
                    {
                        var child = SpawnOf(wo);
                        if (e["after"] == null) { if (child != wo) child.Destroy(); wo.Destroy(); return true; }
                        if (wo.Location.DistanceTo(PosFrom((JsonObject)e["after"]!)) < 0.01f) return false;
                        Place(wo, PosFrom((JsonObject)e["after"]!), _ => { });
                        if (child != wo && e["spawn"] is JsonObject sp) Place(child, PosFrom(sp), _ => { });
                        return true;
                    }
                    if (e["after"] == null) { wo.Destroy(); return true; }
                    bool did = false;
                    if (wo.Location.DistanceTo(PosFrom((JsonObject)e["after"]!)) > 0.01f) { Place(wo, PosFrom((JsonObject)e["after"]!), _ => { }); did = true; }
                    if (e["scale"] != null && Math.Abs((wo.ObjScale ?? 1f) - N(e["scale"])) > 0.001f) { ApplyScale(wo, N(e["scale"])); did = true; }
                    return did;
                }
                if (kind == "way") return ReapplyWay(key, e);
                if (kind == "add" && e["after"] is JsonObject after)
                {
                    uint g = Hex((string)e["guid"]!);
                    if (s_Added.ContainsKey(g) && Find(me, g) != null) return false;
                    // its landblock must be loaded, or EnterWorld would load it just to hold a prop
                    uint blk = Hex((string)after["cell"]!) >> 16;
                    if (!LandblockManager.GetLoadedLandblocks().Any(l => l.Id.Landblock == blk)) return false;
                    var wo = MakeAdd((uint)D(e["wcid"]), PosFrom(after), e["scale"] != null ? N(e["scale"]) : (float?)null, _ => { });
                    if (wo == null) return false;
                    s_Added.Remove(g);
                    s_Added[wo.Guid.Full] = key;
                    e["guid"] = $"0x{wo.Guid.Full:X8}";
                    return true;
                }
            }
            catch (Exception x) { Mod.Log.Warn($"[RevivalGuard] Decor reapply {key}: {x.Message}"); }
            return false;
        }

        // ---- one-click publish (owner 2026-11-24: "Publish this area", his in-game "Yes, publish" is his yes) ----
        // The mod only WRITES A REQUEST: decor/publish/requests/<area>.json, the approved layer as it was at the click plus the
        // approval record (who, when, which area). tools/decor/publish_job.py on the GPU box (run by publish-watch.sh) picks it
        // up, builds the datafix and undo with decor.py into its outbox, and writes decor/publish/status/<area>.json back,
        // which the panel shows. Nothing here runs a program or reaches live; the coordinator forwards approved packages.

        static string PubDir => Path.Combine(Dir, "publish");

        static string EditsSha(Area ar)
        {
            using var h = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ar.Edits.ToJsonString()))).ToLowerInvariant();
        }

        /// <summary>publish &lt;area&gt; [props rows pieces]: the counts are the summary the panel showed before "Yes, publish".</summary>
        static void Publish(Session s, Player me, string[] a, Action<string> reply)
        {
            if (s == null || s.AccessLevel < AccessLevel.Admin) { reply("Only an Admin can publish an area."); return; }
            string id = a.Length > 1 ? a[1] : ActiveOf(me).Id;
            if (!ValidId(id) || !s_Areas.TryGetValue(id, out var ar)) { reply("No such area."); return; }
            if (ar.Edits.Count == 0) { reply($"{(string)ar.Layer["name"] ?? id} has no changes to publish."); return; }
            int N(int i) => a.Length > i && int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
            string when = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string sha = EditsSha(ar);
            var req = new JsonObject
            {
                ["area"] = id, ["name"] = (string)ar.Layer["name"] ?? id, ["edits_sha"] = sha, ["layer"] = ar.Layer.DeepClone(),
                ["approval"] = new JsonObject
                {
                    ["said"] = "Yes, publish", ["what"] = "approved by the owner in game", ["time"] = when, ["area"] = id,
                    ["character"] = me.Name, ["account"] = s.Account, ["access"] = s.AccessLevel.ToString(),
                    ["shown"] = new JsonObject { ["props"] = N(2), ["rows"] = N(3), ["pieces"] = N(4) },
                },
            };
            Directory.CreateDirectory(Path.Combine(PubDir, "requests"));
            Directory.CreateDirectory(Path.Combine(PubDir, "status"));
            Write(Path.Combine(PubDir, "requests", id + ".json"), req.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Write(Path.Combine(PubDir, "status", id + ".json"), new JsonObject { ["state"] = "queued", ["time"] = when, ["edits_sha"] = sha }.ToJsonString());
            Mod.Log.Info($"[RevivalGuard] Decor: publish {id} approved in game by {me.Name} ({s.Account}) at {when}");
            reply($"{req["name"]}: publishing. The package is built on the box in a minute or so.");
        }

        /// <summary>The area's publish state as the box's job last wrote it, and whether it changed since.</summary>
        static JsonObject PubStatus(Area ar)
        {
            try
            {
                var f = Path.Combine(PubDir, "status", ar.Id + ".json");
                if (!File.Exists(f) || !(JsonNode.Parse(File.ReadAllText(f)) is JsonObject st)) return null;
                st["changed"] = (string)st["edits_sha"] != EditsSha(ar);
                return st;
            }
            catch { return null; }
        }

        static void SendPub(Session s)
        {
            var o = new JsonObject();
            foreach (var ar in s_Areas.Values) if (PubStatus(ar) is JsonObject st) o[ar.Id] = st;
            Send(s, new JsonObject { ["k"] = "pub", ["areas"] = o });
        }

        // ---- kits: a group saved as poses relative to its centre (Mods/RevivalGuard/decor/kits; decor.py pull keeps them in git) ----

        static string KitDir => Path.Combine(Dir, "kits");

        /// <summary>kitsave &lt;Name_With_Underscores&gt; w|m &lt;wcid|model&gt; &lt;x y z qw qx qy qz size&gt; [name words_ [look]];...
        /// Any weenie and any model goes in (Turbine's own things too, F7 261124-0004): a w item is placed later through add,
        /// which refuses NPCs, portals, doors and generators (WhyNoCopy) there as well. look: a w item's setup, for the ghost.</summary>
        static void KitSave(string[] a, Action<string> reply)
        {
            string name = string.Join(" ", a[1].Split('_', StringSplitOptions.RemoveEmptyEntries)).Trim();
            string id = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            while (id.Contains("--")) id = id.Replace("--", "-");
            if (id.Length == 0 || id.Length > 40) { reply("A kit needs a short name."); return; }
            var items = new JsonArray();
            foreach (var part in string.Join(" ", a.Skip(2)).Split(';'))
            {
                var f = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10) continue;
                var it = new JsonObject();
                if (f[0] == "w") it["wcid"] = int.Parse(f[1], CultureInfo.InvariantCulture); else it["model"] = f[1];
                it["x"] = R(F(f[2])); it["y"] = R(F(f[3])); it["z"] = R(F(f[4]));
                it["qw"] = R(F(f[5])); it["qx"] = R(F(f[6])); it["qy"] = R(F(f[7])); it["qz"] = R(F(f[8]));
                it["scale"] = R(Mathf01(F(f[9])));
                if (f.Length > 10) it["name"] = string.Join(" ", f[10].Split('_'));
                if (f.Length > 11 && f[0] == "w" && f[11].StartsWith("0x") && f[11].Length <= 10) it["look"] = f[11];
                items.Add(it);
            }
            if (items.Count == 0) { reply("Select some things first."); return; }
            Directory.CreateDirectory(KitDir);
            Write(Path.Combine(KitDir, id + ".json"), new JsonObject { ["name"] = name, ["items"] = items }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            reply($"Kit \"{name}\" saved ({items.Count} thing(s)).");
        }

        static JsonArray Kits()
        {
            var o = new JsonArray();
            if (!Directory.Exists(KitDir)) return o;
            foreach (var f in Directory.GetFiles(KitDir, "*.json").OrderBy(x => x))
                try { if (JsonNode.Parse(File.ReadAllText(f)) is JsonObject k) { k["id"] = Path.GetFileNameWithoutExtension(f); o.Add(k); } }
                catch { }
            return o;
        }

        static void Send(Session s, JsonObject o)
        {
            if (ModChannel.Capable(s)) ModChannel.Send(s, "decor", o.ToJsonString());
        }
    }

    /// <summary>Props added by the decorator are never written to the shard database (see Decor.IsAdded).</summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard))]
    static class DecorNoSave
    {
        static bool Prefix(WorldObject __instance, ref bool __result)
        {
            if (!Decor.IsAdded(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>A linked row the layout removed stays gone, one it moved respawns there, at its size. Off unless this shard has
    /// decor/enabled, and only for link profiles: anything else passes straight through.</summary>
    [HarmonyPatch(typeof(GeneratorProfile), nameof(GeneratorProfile.Spawn))]
    static class DecorSpawn
    {
        static bool Prefix(GeneratorProfile __instance, ref List<WorldObject> __result)
        {
            if (!Decor.On) return true;
            try { if (Decor.RowMaySpawn(__instance)) return true; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DecorSpawn: {e.Message}"); return true; }
            __result = null;
            return false;
        }

        static void Postfix(GeneratorProfile __instance, List<WorldObject> __result)
        {
            if (!Decor.On) return;
            try { Decor.RowSpawned(__instance, __result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DecorSpawn: {e.Message}"); }
        }
    }

    /// <summary>Self-restoring layouts (decor v2): when a landblock has made its own objects, its decor edits go on (queued
    /// behind CreateWorldObjects' own action, so the objects are there). Off unless this shard has decor/enabled.</summary>
    [HarmonyPatch]
    static class DecorBlockLoad
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(Landblock), "CreateWorldObjects");

        static bool Prepare(System.Reflection.MethodBase original)
        {
            if (original != null) return true;
            bool ok = AccessTools.Method(typeof(Landblock), "CreateWorldObjects") != null;
            if (!ok) Mod.Log.Warn("[RevivalGuard] DecorBlockLoad: Landblock.CreateWorldObjects not found; layouts apply only when Decorate opens");
            return ok;
        }

        static void Postfix(Landblock __instance)
        {
            if (!Decor.On) return;
            var lb = __instance;
            lb.EnqueueAction(new ActionEventDelegate(() => Decor.ApplyBlock(lb)));
        }
    }
}
