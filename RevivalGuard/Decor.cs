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
    /// removed), so undo is just another edit and the file is the net result.
    ///   s:&lt;guid&gt;      a placed server object (landblock_instance row, link children included)
    ///   a:&lt;n&gt;         a server object added from the palette (a Stuck copy of a retail weenie)
    ///   t:&lt;blk&gt;:&lt;i&gt;  a DAT static of block blk, i-th entry of its object list
    ///   n:&lt;n&gt;         a DAT static added from the palette
    /// Poses are AC landblock-local: cell, x, y, z, qw, qx, qy, qz.
    /// </summary>
    internal static class Decor
    {
        static readonly object s_Lock = new object();
        static string Dir => Path.Combine(ModFolder.Path(), "decor");
        static bool Enabled => File.Exists(Path.Combine(Dir, "enabled"));
        static string s_Area = "holtburg";
        static JsonObject s_Layer;
        internal static volatile bool On;

        internal static void Register()
        {
            CommandManager.TryAddCommand(Cmd, "decor", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld,
                "World decorator (test shard only; sent by the client's Decorate mode).",
                "state | move <guid> <pose> | remove <guid> | restore <guid> | add <wcid> <pose> | unadd <id> | static <blk> <i> <model> <pose|gone> | staticadd <id> <model> <pose|gone>");
            On = Enabled;
            Mod.Log.Info("[RevivalGuard] Decor: @decor (world decorator, test shard only: " + (Enabled ? "ENABLED here" : "off here") + ")");
        }

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
                    switch (args[0].ToLowerInvariant())
                    {
                        case "state": SendState(s); return;
                        case "info": Info(s, me, args); return;
                        case "audit": Audit(me, Reply); break;
                        case "scale": Scale(s, me, args, Reply); break;
                        case "piecescale": PieceScale(args); break;
                        case "move": Move(s, me, args, Reply); break;
                        case "remove": Remove(me, args, Reply); break;
                        case "restore": Restore(me, args, Reply); break;
                        case "add": Add(s, me, args, Reply); break;
                        case "unadd": Unadd(me, args, Reply); break;
                        case "static": Static(args, Reply); break;
                        case "staticadd": StaticAdd(args, Reply); break;
                        default: Reply("Unknown decor verb."); return;
                    }
                    Save();
                }
            }
            catch (Exception e) { Reply("That edit failed: " + e.Message); Mod.Log.Warn($"[RevivalGuard] Decor {string.Join(' ', args)}: {e}"); }
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
                    return !(s_Layer["edits"]![$"s:{p.Generator.Guid.Full:X8}"] is JsonObject g && g["spawn0"] != null && g.ContainsKey("after") && g["after"] == null);
                if (!(s_Layer["edits"]![$"s:{p.Id:X8}"] is JsonObject e) || !e.ContainsKey("after")) return true;
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
                    if (s_Layer["edits"]![$"s:{p.Generator.Guid.Full:X8}"] is JsonObject g && g["spawn"] is JsonObject at)
                        foreach (var wo in spawned) Place(wo, PosFrom(at), _ => { });
                    return;
                }
                if (!(s_Layer["edits"]![$"s:{p.Id:X8}"] is JsonObject e) || e["scale"] == null) return;
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
            if (wo == null) { o["why"] = "That object is not here any more."; Send(s, o); return; }
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
            }
            Send(s, o);
        }

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
            string before = s_Layer["edits"]!.ToJsonString();
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
                bool had = s_Layer["edits"]![key] != null;
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
                    if (s_Layer["edits"]![key] != null) ((JsonObject)s_Layer["edits"]!).Remove(key);
                }
                catch (Exception e) { o["error"] = e.GetType().Name + ": " + e.Message; if (s_Layer["edits"]![key] != null) ((JsonObject)s_Layer["edits"]!).Remove(key); }
            }
            File.WriteAllText(Path.Combine(Dir, "audit.json"), rows.ToJsonString());
            reply($"audit: {rows.Count} objects; layout unchanged: {before == s_Layer["edits"]!.ToJsonString()}");
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
            if (s_Layer["edits"]![a[1]] is JsonObject e) e["scale"] = R(Mathf01(F(a[2])));
        }

        static void Move(Session s, Player me, string[] a, Action<string> reply)
        {
            uint guid = Hex(a[1]);
            var pose = Pose(a, 2);
            var wo = SpawnOf(Find(me, guid));
            if (wo == null) { reply("That object is not here any more."); return; }
            string why = WhyNot(wo);
            if (why != null) { reply(why); return; }
            string key = KeyOf(wo, out bool placed);
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
                ((JsonObject)s_Layer["edits"]!).Remove(key);
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
            if (to.PositionX < 0f || to.PositionX >= 192f || to.PositionY < 0f || to.PositionY >= 192f)
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
                ((JsonObject)s_Layer["edits"]!).Remove(key);
        }

        static void Remove(Player me, string[] a, Action<string> reply)
        {
            uint guid = Hex(a[1]);
            var wo = SpawnOf(Find(me, guid));
            if (wo == null) { reply("That object is not here any more."); return; }
            string why = WhyNot(wo);
            if (why != null) { reply(why); return; }
            string key = KeyOf(wo, out bool placed);
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
            if (!(s_Layer["edits"]![key] is JsonObject e)) { reply("Nothing to restore."); return; }
            var before = (JsonObject)e["before"]!;
            uint row = Hex(key.Substring(2));
            var wo = named;
            var profile = ProfileOfRow(row);
            if (profile != null)
            {
                ((JsonObject)s_Layer["edits"]!).Remove(key);   // first: its spawn must no longer be held back
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
                ((JsonObject)s_Layer["edits"]!).Remove(key);
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
                ((JsonObject)s_Layer["edits"]!).Remove(key);   // first: a generator's own first spawn must not be held back
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
                        s_Layer["edits"]![key] = JsonNode.Parse(e.ToJsonString());
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
            ((JsonObject)s_Layer["edits"]!).Remove(key);
            reply($"{wo.Name} is back where it was.");
        }

        static void Add(Session s, Player me, string[] a, Action<string> reply)
        {
            uint wcid = uint.Parse(a[1], CultureInfo.InvariantCulture);
            var pose = Pose(a, 2);
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) { reply($"No such thing ({wcid})."); return; }
            if (wo is Creature) { wo.Destroy(); reply("New NPCs and monsters are not in the palette."); return; }
            wo.SetProperty(PropertyBool.Stuck, true);   // as the datafix clones it: nobody picks it up
            wo.TimeToRot = -1;
            wo.Location = pose;
            wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
            if (!wo.EnterWorld()) { wo.Destroy(); reply($"{wo.Name} could not be placed there."); return; }
            int n = (int)(s_Layer["next"]?.GetValue<int>() ?? 1);
            s_Layer["next"] = n + 1;
            string key = $"a:{n}";
            ((JsonObject)s_Layer["edits"]!)[key] = new JsonObject
            {
                ["kind"] = "add", ["wcid"] = wcid, ["name"] = wo.Name, ["guid"] = $"0x{wo.Guid.Full:X8}", ["after"] = PoseOf(wo.Location),
            };
            s_Added[wo.Guid.Full] = key;
            Send(s, new JsonObject { ["k"] = "added", ["key"] = key, ["guid"] = $"0x{wo.Guid.Full:X8}", ["name"] = wo.Name });
        }

        static void Unadd(Player me, string[] a, Action<string> reply)
        {
            string key = a[1].StartsWith("a:") ? a[1] : "a:" + a[1];
            if (!(s_Layer["edits"]![key] is JsonObject e)) { reply("Nothing to take away."); return; }
            uint guid = Hex((string)e["guid"]!);
            var wo = Find(me, guid);
            // Not found = not in any loaded landblock; an added prop is never saved (DecorNoSave), so it is gone for good
            // and the entry can go too. Found: destroyed first, then forgotten.
            wo?.Destroy();
            ((JsonObject)s_Layer["edits"]!).Remove(key);
            s_Added.Remove(guid);
            reply(wo != null ? $"{wo.Name} taken away." : "Taken away (it was no longer in the world).");
        }

        // ---- DAT statics: the client draws them; the server only keeps the layer ----

        static void Static(string[] a, Action<string> reply)
        {
            string key = $"t:{a[1].ToUpperInvariant()}:{a[2]}";
            var edits = (JsonObject)s_Layer["edits"]!;
            if (a[4] == "reset") { edits.Remove(key); return; }
            var e = Entry(key, () => new JsonObject { ["kind"] = "static", ["block"] = a[1].ToUpperInvariant(), ["id"] = a[2], ["model"] = a[3] });
            int from;
            if (a[4] == "gone") { e["after"] = null; from = 5; }
            else { e["after"] = PoseOf(Pose(a, 4)); from = 12; }
            if (e["before"] == null && a.Length > from + 8 && a[from] == "from") e["before"] = PoseOf(Pose(a, from + 1));
        }

        static void StaticAdd(string[] a, Action<string> reply)
        {
            string key = "n:" + a[1];
            var edits = (JsonObject)s_Layer["edits"]!;
            if (a[3] == "gone") { edits.Remove(key); return; }
            edits[key] = new JsonObject { ["kind"] = "staticadd", ["model"] = a[2], ["after"] = PoseOf(Pose(a, 3)) };
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
            var cell = phys.AdjustPosition(target, System.Numerics.Vector3.Zero, false, true);
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
            var edits = (JsonObject)s_Layer["edits"]!;
            if (edits[key] is JsonObject e) return e;
            e = make();
            edits[key] = e;
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

        static string LayerPath => Path.Combine(Dir, s_Area + ".json");

        static void Load()
        {
            if (s_Layer != null) return;
            try { if (File.Exists(LayerPath)) s_Layer = JsonNode.Parse(File.ReadAllText(LayerPath)) as JsonObject; }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] Decor: {LayerPath} unreadable, starting a new layer: {e.Message}"); }
            s_Layer ??= new JsonObject { ["area"] = s_Area, ["next"] = 1, ["edits"] = new JsonObject() };
            // added objects do not survive a restart (the database has no row for them): their entries stay, and
            // the client re-adds what is missing when it asks for the state.
        }

        static void Save()
        {
            s_Layer["saved"] = DateTime.UtcNow.ToString("o");
            var tmp = LayerPath + ".tmp";
            File.WriteAllText(tmp, s_Layer.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, LayerPath, true);
        }

        static void SendState(Session s)
        {
            Reapply(s.Player);
            Save();
            Send(s, new JsonObject { ["k"] = "state", ["layer"] = JsonNode.Parse(s_Layer.ToJsonString()) });
        }

        /// <summary>After a restart the test shard's rows are back and added objects are gone: put the layer back on the
        /// world (moves, removals, additions; the additions get new guids).</summary>
        static void Reapply(Player me)
        {
            foreach (var kv in ((JsonObject)s_Layer["edits"]!).ToList())
            {
                if (!(kv.Value is JsonObject e)) continue;
                string kind = (string)e["kind"]!;
                try
                {
                    if (kind == "server")
                    {
                        var wo = Find(me, Hex((string)e["guid"]!));
                        if (wo == null) continue;
                        if (!e.ContainsKey("after")) continue;   // never a removal by omission
                        if (e["spawn0"] != null)
                        {
                            var child = SpawnOf(wo);
                            if (e["after"] == null) { if (child != wo) child.Destroy(); wo.Destroy(); continue; }
                            Place(wo, PosFrom((JsonObject)e["after"]!), _ => { });
                            if (child != wo && e["spawn"] is JsonObject sp) Place(child, PosFrom(sp), _ => { });
                            continue;
                        }
                        if (e["after"] == null) wo.Destroy();
                        else
                        {
                            if (wo.Location.DistanceTo(PosFrom((JsonObject)e["after"]!)) > 0.01f) Place(wo, PosFrom((JsonObject)e["after"]!), _ => { });
                            if (e["scale"] != null && Math.Abs((wo.ObjScale ?? 1f) - N(e["scale"])) > 0.001f) ApplyScale(wo, N(e["scale"]));
                        }
                    }
                    else if (kind == "add" && e["after"] is JsonObject after)
                    {
                        uint g = Hex((string)e["guid"]!);
                        if (s_Added.ContainsKey(g) && Find(me, g) != null) continue;
                        var wo = WorldObjectFactory.CreateNewWorldObject((uint)D(e["wcid"]));
                        if (wo == null) continue;
                        wo.SetProperty(PropertyBool.Stuck, true);
                        wo.TimeToRot = -1;
                        if (e["scale"] != null) wo.ObjScale = N(e["scale"]);
                        wo.Location = PosFrom(after);
                        wo.Location.LandblockId = new LandblockId(wo.Location.GetCell());
                        if (!wo.EnterWorld()) { wo.Destroy(); continue; }
                        s_Added.Remove(g);
                        s_Added[wo.Guid.Full] = kv.Key;
                        e["guid"] = $"0x{wo.Guid.Full:X8}";
                    }
                }
                catch (Exception x) { Mod.Log.Warn($"[RevivalGuard] Decor reapply {kv.Key}: {x.Message}"); }
            }
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
}
