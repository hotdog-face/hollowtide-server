using System.Collections.Concurrent;
using System.Numerics;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;
using Position = ACE.Entity.Position;

namespace RevivalGuard
{
    /// <summary>
    /// MANSION FURNITURE THAT SHOWS WHAT IS ON IT (owner, 2026-09-27: mansions are "mostly useless
    /// inside"; approved sculpted furniture, "useful pieces plus trophy displays"). docs/MANSION-FURNITURE.md.
    ///
    /// Four hook items, weenies 900500-900503 (tools/gpubox-ace/mansion-furniture.sql), each a Hooker
    /// so ACE's own hook rules apply (hung by the owner, used through the hook with hooks off):
    ///
    ///   Weapon Rack       wall  4 weapons, bows, wands or shields, hung flat side by side
    ///   Armor Stand       floor a wooden figure (weenie 900510) wearing up to 9 pieces of armour
    ///   Portal Gem Shelf  wall  8 portal gems on two shelves; anyone let into the house can use them
    ///   Trophy Wall       wall  4 trophies (the trophy mounts, mounted heads, fish) two by two
    ///
    /// HOW AN ITEM GOES ON: drag it from your pack onto the piece (our client sends GiveObjectRequest
    /// for a drop on any world object, AcNpcInteraction.Give). Anyone with the house's STORAGE
    /// permission may hang things (the owner, their account, the allegiance when storage is granted
    /// to it). HOW IT COMES OFF: double-click it (Use) or pick it up (F, PutItemInContainer); the
    /// one who hung it, or the house owner for anything not attuned. On the gem shelf a double-click
    /// USES a gem instead, and picking it up takes the stack back.
    ///
    /// WHERE THE ITEMS LIVE. The real item stays a real biota with ContainerId = the furniture, like
    /// an item in a pack: ACE saves it, never loads it on its own (the furniture is not a Container,
    /// so nothing asks for its inventory) and never decays it. This file loads it when the furniture
    /// is hung (Hook.OnAddItem, which also runs when the hook loads from the shard) and keeps it in
    /// memory for the life of the server. The furniture cannot leave its hook while it holds
    /// anything, so an item can never be carried off inside it.
    ///
    /// WHAT THE WORLD SEES is a COPY per item: a new object of the same weenie with every property
    /// and spell of the real one, Stuck, ethereal, never rotting, and never saved
    /// (IsDynamicThatShouldPersistToShard says no). The client draws it exactly as it draws the item
    /// anywhere (own model, colours, expansion art) and appraising it shows the real item's
    /// qualities, which is the showing off. Copies are made again whenever the hook loads.
    /// The Armor Stand's figure (weenie 900510) is an object on the human setup and the statue
    /// NO motion table (2026-09-30: 0x090000F3, the statue table, is the full human table and our
    /// client played its breathing idle; with none the client draws the part-node export
    /// 0x02000001_0x00000000, still); FigureDress gives it the look of a body wearing the
    /// copies, with the wooden parts in expansion/models/0x01FF0510-0x01FF0520 wherever nothing
    /// covers it, and its appraisal lists what it wears.
    ///
    /// PLACEMENT. A copy hangs in its own Hook placement (103), as it would on a real wall hook, at
    /// a slot offset in the hook's frame (SLOTS; the geometry in expansion/housing/build_furniture.py
    /// is built around the same numbers, DAT offset = (-x, -y, z) of Blender's). Its hook-pose bounds
    /// are read from the DAT (setup parts, placement frames, gfxobj vertices): a weapon that hangs
    /// across is turned upright about the wall normal, and anything too big for its slot is scaled
    /// down with ObjScale. Gems sit on the shelves in their Resting placement (101).
    /// Custom properties (ours, never sent to a client): PropertyBool 29120 (a copy),
    /// PropertyInstanceId 29121 (who hung it), PropertyInt 29122 (its slot).
    /// </summary>
    [HarmonyPatch]
    static class MansionFurniture
    {
        internal const uint RACK = 900500, STAND = 900501, SHELF = 900502, TROPHY = 900503, FIGURE = 900510;
        internal const PropertyBool DISPLAY_COPY = (PropertyBool)29120;
        internal const PropertyInstanceId HUNG_BY = (PropertyInstanceId)29121;
        internal const PropertyInt SLOT = (PropertyInt)29122;

        /// <summary>The trophy mounts (TrophyMounts.cs) draw as a kit-bashed board about 0.9 x 0.75 m
        /// that their Plaque setup does not describe, so their bounds are given here.</summary>
        static readonly HashSet<uint> s_ExpansionTrophies = new HashSet<uint> { 900060, 900061, 900062, 900063, 900205 };

        sealed class Spec
        {
            public string Name, Noun, Holds, Takes;
            public Vector3[] Slots;          // DAT offsets in the hook's frame
            public float MaxW, MaxH;          // the room a slot gives (metres across, up)
        }

        // Wall pieces: the wall is 0.085 m behind the hook's origin (DAT +y); the backboard's face
        // is where an item's back rests. Slot y is that face.
        static readonly Dictionary<uint, Spec> s_Spec = new Dictionary<uint, Spec>
        {
            [RACK] = new Spec
            {
                Name = "Weapon Rack", Noun = "weapon", Holds = "weapons, bows, wands and shields",
                Takes = "Drag a weapon, bow, wand or shield from your pack onto the rack to hang it.",
                Slots = new[] { new Vector3(0.60f, 0.047f, 0f), new Vector3(0.20f, 0.047f, 0f), new Vector3(-0.20f, 0.047f, 0f), new Vector3(-0.60f, 0.047f, 0f) },
                MaxW = 0.34f, MaxH = 1.30f,
            },
            [STAND] = new Spec
            {
                Name = "Armor Stand", Noun = "piece", Holds = "armor and clothing, plus a weapon and a shield",
                Takes = "Drag armor, clothing, a weapon or a shield from your pack onto the stand to dress the figure.",
                Slots = new Vector3[9],   // one figure; nine pieces (and its two hands, HAND_WEAPON/HAND_SHIELD)
            },
            [SHELF] = new Spec
            {
                Name = "Portal Gem Shelf", Noun = "gem", Holds = "portal gems",
                Takes = "Drag a portal gem from your pack onto the shelf to set it out.",
                // two shelves (tops at z -0.46 and 0.02), four gems each, centred in the shelf's depth
                Slots = new[]
                {
                    new Vector3(0.45f, -0.065f, 0.02f), new Vector3(0.15f, -0.065f, 0.02f), new Vector3(-0.15f, -0.065f, 0.02f), new Vector3(-0.45f, -0.065f, 0.02f),
                    new Vector3(0.45f, -0.065f, -0.46f), new Vector3(0.15f, -0.065f, -0.46f), new Vector3(-0.15f, -0.065f, -0.46f), new Vector3(-0.45f, -0.065f, -0.46f),
                },
                MaxW = 0.26f, MaxH = 0.40f,
            },
            [TROPHY] = new Spec
            {
                Name = "Trophy Wall", Noun = "trophy", Holds = "trophies",
                Takes = "Drag a trophy from your pack onto the wall to hang it.",
                Slots = new[] { new Vector3(0.55f, 0.060f, 0.42f), new Vector3(-0.55f, 0.060f, 0.42f), new Vector3(0.55f, 0.060f, -0.42f), new Vector3(-0.55f, 0.060f, -0.42f) },
                MaxW = 1.02f, MaxH = 0.80f,
            },
        };

        sealed class Display
        {
            public readonly object Lock = new object();
            public uint FurnitureGuid, Wcid;
            public Hook Hook;
            public WorldObject Furniture;
            public readonly List<WorldObject> Items = new List<WorldObject>();   // the real items
            public readonly List<WorldObject> Copies = new List<WorldObject>();  // copies and the figure
            public List<WorldObject> Wear = new List<WorldObject>();             // what the figure wears
            public List<WorldObject> Held = new List<WorldObject>();             // what the figure holds (weapon, shield)
            public bool Loaded, Loading;
            public int Retries;
        }

        static readonly ConcurrentDictionary<uint, Display> s_Displays = new ConcurrentDictionary<uint, Display>();
        static readonly ConcurrentDictionary<uint, (Display d, uint real)> s_Copies = new ConcurrentDictionary<uint, (Display, uint)>();

        static bool IsFurniture(WorldObject wo) => wo != null && s_Spec.ContainsKey(wo.WeenieClassId);

        // ------------------------------------------------------------------ banner

        static bool Prepare(System.Reflection.MethodBase original)
        {
            if (original == null)
            {
                Mod.Log.Info("[RevivalGuard] MansionFurniture: weapon rack, armor stand, portal gem shelf and trophy wall (900500-900503)");
                try
                {
                    ACE.Server.Command.CommandManager.TryAddCommand(FurnitureCommand, "furniture", AccessLevel.Developer,
                        ACE.Server.Command.CommandHandlerFlag.RequiresWorld,
                        "The mansion furniture on this landblock: what each piece holds and whether its display is up.", "[refresh]");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionFurniture: @furniture not registered: {e.Message}"); }
            }
            return true;
        }

        /// <summary>@furniture [refresh] (staff): every piece on this landblock, what it holds, and
        /// each copy's state; `refresh` makes the copies again.</summary>
        static void FurnitureCommand(ACE.Server.Network.Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p?.CurrentLandblock == null) return;
            bool refresh = parameters?.Length > 0 && parameters[0].Equals("refresh", StringComparison.OrdinalIgnoreCase);
            int n = 0;
            foreach (var d in s_Displays.Values)
            {
                if (d.Hook?.CurrentLandblock != p.CurrentLandblock) continue;
                n++;
                lock (d.Lock)
                {
                    Say(p, $"{s_Spec[d.Wcid].Name} 0x{d.FurnitureGuid:X8} on hook 0x{d.Hook.Guid.Full:X8}: {d.Items.Count} item(s){(d.Loaded ? "" : " (loading)")}, {d.Copies.Count} shown");
                    foreach (var c in d.Copies)
                        Say(p, $"  0x{c.Guid.Full:X8} {c.Name}{(c.IsDestroyed ? " DESTROYED" : "")} {(c.CurrentLandblock == null ? "not in world" : c.Location?.ToLOCString())}");
                }
                if (refresh) d.Hook.EnqueueAction(new ActionEventDelegate(() => Refresh(d)));
            }
            if (n == 0) Say(p, "No mansion furniture is hung on this landblock.");
        }

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            // a no-op anchor so Prepare runs once at PatchAll; the real patches are the nested classes
            yield return AccessTools.Method(typeof(WorldObject), nameof(WorldObject.IsDynamicThatShouldPersistToShard));
        }

        /// <summary>A copy is never saved: it is made again from the real item whenever it is needed.</summary>
        static void Postfix(WorldObject __instance, ref bool __result)
        {
            if (__result && s_Copies.ContainsKey(__instance.Guid.Full)) __result = false;
        }

        // ------------------------------------------------------------------ hanging and loading

        [HarmonyPatch(typeof(Hook), "OnAddItem")]
        static class Hung
        {
            static void Postfix(Hook __instance)
            {
                try
                {
                    var item = __instance.Item;
                    if (!IsFurniture(item)) return;
                    var d = s_Displays.GetOrAdd(item.Guid.Full, g => new Display { FurnitureGuid = g, Wcid = item.WeenieClassId });
                    lock (d.Lock) { d.Hook = __instance; d.Furniture = item; d.Retries = 0; }
                    Load(d);
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: hung: {e}"); }
            }
        }

        [HarmonyPatch(typeof(Hook), "OnRemoveItem")]
        static class TakenDown
        {
            static void Postfix(WorldObject removedItem)
            {
                try
                {
                    if (!IsFurniture(removedItem) || !s_Displays.TryGetValue(removedItem.Guid.Full, out var d)) return;
                    lock (d.Lock) { ClearCopies(d); d.Hook = null; }
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: taken down: {e}"); }
            }
        }

        /// <summary>The real items are loaded once per server run; after that the in-memory list is
        /// the truth (every change is saved as it happens), so a landblock reload only makes the
        /// copies again. Two loads of one biota would be two live objects for one item.</summary>
        static void Load(Display d)
        {
            lock (d.Lock)
            {
                if (d.Loaded) { d.Hook?.EnqueueAction(new ActionEventDelegate(() => Refresh(d))); return; }
                if (d.Loading) return;
                d.Loading = true;
            }
            DatabaseManager.Shard.GetInventoryInParallel(d.FurnitureGuid, false, biotas =>
            {
                var hook = d.Hook;
                void Apply()
                {
                    lock (d.Lock)
                    {
                        if (!d.Loaded)
                        {
                            foreach (var b in biotas)
                            {
                                var wo = WorldObjectFactory.CreateWorldObject(b);
                                if (wo != null) d.Items.Add(wo);
                            }
                            d.Items.Sort((a, b) => (a.GetProperty(SLOT) ?? 99).CompareTo(b.GetProperty(SLOT) ?? 99));
                            d.Loaded = true;
                            d.Loading = false;
                            if (d.Items.Count > 0)
                                Mod.Log.Info($"[RevivalGuard] MansionFurniture: {s_Spec[d.Wcid].Name} 0x{d.FurnitureGuid:X8} holds {d.Items.Count}: {string.Join(", ", d.Items.Select(i => i.Name))}");
                        }
                    }
                    Refresh(d);
                }
                if (hook != null) hook.EnqueueAction(new ActionEventDelegate(Apply));
                else Apply();
            });
        }

        // ------------------------------------------------------------------ the copies

        static void ClearCopies(Display d)
        {
            // the figure's held copies first, while the figure is still in the world to broadcast from
            foreach (var h in d.Held)
            {
                if (s_Copies.TryRemove(h.Guid.Full, out _)) s_Retired[h.Guid.Full] = (d, DateTime.UtcNow);
                var fig = d.Copies.FirstOrDefault(c => c.Guid.Full == (h.WielderId ?? 0));
                try { if (fig?.CurrentLandblock != null) fig.EnqueueBroadcast(new GameMessageDeleteObject(h)); } catch { }
                try { if (!h.IsDestroyed) h.Destroy(false); } catch { }
            }
            d.Held = new List<WorldObject>();
            foreach (var c in d.Copies)
            {
                if (s_Copies.TryRemove(c.Guid.Full, out _)) s_Retired[c.Guid.Full] = (d, DateTime.UtcNow);
                s_Figures.TryRemove(c.Guid.Full, out _);
                s_FigureHeld.TryRemove(c.Guid.Full, out _);
                try { if (!c.IsDestroyed) c.Destroy(false); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionFurniture: clearing a copy: {e.Message}"); }
            }
            d.Copies.Clear();
            foreach (var w in d.Wear)
                try { if (!w.IsDestroyed) w.Destroy(false); } catch { }   // recycles the guid; nothing was saved
            d.Wear = new List<WorldObject>();
        }

        /// <summary>Make every copy again from the real items. Runs on the hook's landblock.</summary>
        static void Refresh(Display d)
        {
            lock (d.Lock)
            {
                ClearCopies(d);
                var hook = d.Hook;
                if (hook == null || hook.Item?.Guid.Full != d.FurnitureGuid) return;
                if (hook.CurrentLandblock == null || hook.Location == null)
                {
                    // the hook's inventory can finish loading before the hook is in the world
                    if (d.Retries++ < 10)
                    {
                        var chain = new ActionChain();
                        chain.AddDelaySeconds(3);
                        chain.AddAction(hook, () => Refresh(d));
                        chain.EnqueueChain();
                    }
                    return;
                }
                try
                {
                    if (d.Wcid == STAND) SpawnFigure(d);
                    else
                        foreach (var real in d.Items)
                        {
                            var c = SpawnCopy(d, real);
                            if (c != null) d.Copies.Add(c);
                        }
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: refresh {s_Spec[d.Wcid].Name} 0x{d.FurnitureGuid:X8}: {e}"); }
            }
        }

        static readonly HashSet<PropertyInt> s_SkipInt = new HashSet<PropertyInt>
        { PropertyInt.CurrentWieldedLocation, PropertyInt.PlacementPosition, SLOT, PropertyInt.Placement };

        /// <summary>A new object of the same weenie carrying every property and spell of the real one
        /// (so appraisal shows its real qualities), with no container, wielder or owner.</summary>
        static WorldObject Clone(WorldObject real)
        {
            var c = WorldObjectFactory.CreateNewWorldObject(real.WeenieClassId);
            if (c == null) return null;
            foreach (var kv in real.GetAllPropertyInt()) if (!s_SkipInt.Contains(kv.Key)) c.SetProperty(kv.Key, kv.Value);
            foreach (var kv in real.GetAllPropertyInt64()) c.SetProperty(kv.Key, kv.Value);
            foreach (var kv in real.GetAllPropertyBools()) c.SetProperty(kv.Key, kv.Value);
            foreach (var kv in real.GetAllPropertyFloat()) c.SetProperty(kv.Key, kv.Value);
            foreach (var kv in real.GetAllPropertyString()) c.SetProperty(kv.Key, kv.Value);
            foreach (var kv in real.GetAllPropertyDataId()) c.SetProperty(kv.Key, kv.Value);
            foreach (var sp in real.Biota.GetKnownSpellsIds(real.BiotaDatabaseLock))
                c.Biota.GetOrAddKnownSpell(sp, c.BiotaDatabaseLock, out _);
            c.SetProperty(DISPLAY_COPY, true);
            return c;
        }

        static WorldObject SpawnCopy(Display d, WorldObject real)
        {
            var spec = s_Spec[d.Wcid];
            int slot = real.GetProperty(SLOT) ?? 0;
            if (slot < 0 || slot >= spec.Slots.Length) slot = 0;
            var s0 = spec.Slots[slot];
            bool shelf = d.Wcid == SHELF;
            var pose = shelf ? Placement.Resting : (Placement)(real.HookPlacement ?? (int)Placement.Hook);
            float baseScale = real.ObjScale ?? 1f;

            Vector3 lo, hi;
            if (s_ExpansionTrophies.Contains(real.WeenieClassId)) { lo = new Vector3(-0.46f, -0.35f, -0.38f); hi = new Vector3(0.46f, 0.058f, 0.38f); }
            else if (!PoseBounds(real.SetupTableId, (int)pose, out lo, out hi)) { lo = new Vector3(-0.1f); hi = new Vector3(0.1f); }
            lo *= baseScale; hi *= baseScale;

            // A rack shows every weapon upright: one that hangs across the wall is turned 90 degrees
            // about the wall's normal (DAT y), about its own centre.
            var extra = Quaternion.Identity;
            if (d.Wcid == RACK && hi.X - lo.X > hi.Z - lo.Z)
            {
                extra = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
                Rotated(ref lo, ref hi, extra);
            }
            var size = hi - lo;
            float fit = shelf ? Math.Min(spec.MaxW / Math.Max(0.01f, Math.Max(size.X, size.Y)), spec.MaxH / Math.Max(0.01f, size.Z))
                              : Math.Min(spec.MaxW / Math.Max(0.01f, size.X), spec.MaxH / Math.Max(0.01f, size.Z));
            // A WEAPON ON THE RACK IS SHOWN AT ITS TRUE SIZE (owner, 2026-09-29: "crossbow was small on
            // the rack"). Fitting it into the 0.34 x 1.30 m slot shrank everything wider or longer than
            // that: a crossbow's limbs (Heavy Crossbow at about half size), a spear, a staff, a
            // two-hander. A rack now never scales: a weapon taller than the slot stands on the rack's
            // bottom rail and rises above it, and a wide one may overlap its neighbour, as on a real
            // rack. The shelf and the trophy wall still fit their items to their places.
            float s = d.Wcid == RACK ? 1f : Math.Min(1f, fit);
            var centre = (lo + hi) * 0.5f * s;
            float rackZ = d.Wcid == RACK && size.Z > spec.MaxH ? s0.Z - spec.MaxH * 0.5f - lo.Z : s0.Z - centre.Z;
            // wall pieces: the item's back (its most wall-ward point) on the backboard; shelf: its
            // bottom on the shelf, its middle over the slot
            var offset = shelf
                ? new Vector3(s0.X - centre.X, s0.Y - centre.Y, s0.Z - lo.Z * s)
                : new Vector3(s0.X - centre.X, s0.Y - hi.Y * s, rackZ);

            var c = Clone(real);
            if (c == null) return null;
            c.Stuck = true;
            c.Ethereal = true;
            c.IgnoreCollisions = true;
            c.GravityStatus = false;
            c.TimeToRot = -1;
            c.UseRadius = ReachOf(d);
            c.Placement = pose;
            c.ObjScale = baseScale * s;
            c.Location = At(d.Hook.Location, offset, extra);
            s_Copies[c.Guid.Full] = (d, real.Guid.Full);
            if (!c.EnterWorld())
            {
                s_Copies.TryRemove(c.Guid.Full, out _);
                c.Destroy(false);
                Mod.Log.Warn($"[RevivalGuard] MansionFurniture: {real.Name} would not enter the world at {c.Location?.ToLOCString()}");
                return null;
            }
            return c;
        }

        /// <summary>THE FIGURE IS AN OBJECT, NOT A CREATURE. The first cut spawned a statue creature and
        /// equipped copies on it; ACE's physics walked it a metre off its plinth within a minute
        /// and, after the next teleport, stopped sending it to the client at all. As a Generic
        /// object (the human setup with no motion table, so it never animates) it stays
        /// exactly where it is put, like every other copy, and appraising it shows its LongDesc:
        /// the list of what it wears. Its dressed look is built by FigureDress below.</summary>
        static void SpawnFigure(Display d)
        {
            var fig = WorldObjectFactory.CreateNewWorldObject(FIGURE);
            if (fig == null) { Mod.Log.Warn("[RevivalGuard] MansionFurniture: weenie 900510 (the figure) is missing"); return; }
            fig.SetProperty(DISPLAY_COPY, true);
            var hungBy = d.Items.Select(i => i.GetProperty(HUNG_BY) ?? 0).Distinct().ToList();
            string who = hungBy.Count == 1 ? PlayerManager.FindByGuid(hungBy[0])?.Name : null;
            fig.Name = d.Items.Count == 0 ? "Wooden Figure" : who != null ? $"{who}'s Armor" : "Armor on Display";
            fig.LongDesc = d.Items.Count == 0
                ? "A wooden figure on an armor stand, waiting to be dressed."
                : (d.Items.Any(i => !InHand(i)) ? "It wears:\n" + string.Join("\n", d.Items.Where(i => !InHand(i)).Select(i => "  " + Label(i))) : "")
                  + (d.Items.Any(InHand) ? (d.Items.Any(i => !InHand(i)) ? "\n" : "") + "It holds:\n" + string.Join("\n", d.Items.Where(InHand).Select(i => "  " + Label(i))) : "");
            var wear = new List<WorldObject>();
            var held = new List<WorldObject>();
            var want = At(d.Hook.Location, new Vector3(0f, 0f, 0.075f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));
            foreach (var real in d.Items)
            {
                var c = Clone(real);
                if (c == null) continue;
                if (InHand(real))
                {
                    // HELD AS A CHARACTER HOLDS IT (owner 2026-11-06, "weapon in hand and the shield on the arm"): the copy is
                    // sent as the figure's child, as ACE sends a creature's wielded item (Creature.TryWieldObject: wielder,
                    // parent location, placement; the figure's Children list), so the client hangs it on the hand part.
                    var (loc, pose, parent) = HandPose(real);
                    c.CurrentWieldedLocation = loc;
                    c.WielderId = fig.Guid.Full;
                    c.ParentLocation = parent;
                    c.Placement = pose;
                    c.Location = new Position(want);
                    fig.Children.Add(new HeldItem(c.Guid.Full, (int)parent, loc));
                    held.Add(c);
                    s_Copies[c.Guid.Full] = (d, real.Guid.Full);
                    continue;
                }
                c.CurrentWieldedLocation = real.ValidLocations;   // what the clothing sort reads
                wear.Add(c);
            }
            d.Wear = wear;
            d.Held = held;
            fig.Stuck = true;
            // solid on the Staff Base (owner M5 261103-0060 "armor stands need collision"): the client blocks on a Stuck,
            // non-Ethereal object's DAT volume, here the human setup's two 0.48 m spheres (the plinth's setup 0x02000B0E
            // has none). A house's stand stays walk-through, as retail furniture is. IgnoreCollisions keeps the server's
            // physics off it either way.
            fig.Ethereal = !StaffBaseHooks.Is(d.Hook);
            fig.IgnoreCollisions = true;
            fig.GravityStatus = false;
            fig.TimeToRot = -1;
            fig.UseRadius = ReachOf(d);
            // on the plinth's top, turned to face out the way the plinth's nameplate does (a body
            // faces its own +y; the furniture's face is -y)
            fig.Location = new Position(want);
            s_Figures[fig.Guid.Full] = wear;
            s_FigureHeld[fig.Guid.Full] = held;
            s_Copies[fig.Guid.Full] = (d, 0);
            if (!fig.EnterWorld())
            {
                s_Copies.TryRemove(fig.Guid.Full, out _);
                s_Figures.TryRemove(fig.Guid.Full, out _);
                s_FigureHeld.TryRemove(fig.Guid.Full, out _);
                foreach (var h in held) { s_Copies.TryRemove(h.Guid.Full, out _); h.Destroy(false); }
                d.Held = new List<WorldObject>();
                fig.Destroy(false);
                Mod.Log.Warn($"[RevivalGuard] MansionFurniture: the figure would not enter the world at {fig.Location?.ToLOCString()}");
                return;
            }
            // ACE's placement stands a human-setup object on the floor whatever it is told (its
            // step-down), which sank the figure's feet into the plinth. It never moves, so the
            // position it is SENT at is the one that matters: put that back on the plinth's top.
            if (fig.Location != null && Math.Abs(fig.Location.PositionZ - want.PositionZ) > 0.01f)
            {
                fig.Location = new Position(want);
                fig.SendUpdatePosition(true);
            }
            d.Copies.Add(fig);
        }

        /// <summary>HOW FAR A COPY CAN BE USED FROM: its hook's own UseRadius (ACE authors 10 m on
        /// wall, floor, ceiling and yard hooks, 50 m on roof hooks), never less than 2.5. A fixed 2.5
        /// m left a rack on a high cottage wall hook (3.5 m up, 2026-09-30 sweep) out of reach: a
        /// double-click walked, never arrived, and nothing could be taken back down.</summary>
        static float ReachOf(Display d) => Math.Max(2.5f, d.Hook?.UseRadius ?? 2.5f);

        /// <summary>Figure guid -> the copies it wears (never in the world themselves).</summary>
        static readonly ConcurrentDictionary<uint, List<WorldObject>> s_Figures = new ConcurrentDictionary<uint, List<WorldObject>>();
        /// <summary>Figure guid -> the copies in its hands, sent to each player who sees the figure (FigureSeen).</summary>
        static readonly ConcurrentDictionary<uint, List<WorldObject>> s_FigureHeld = new ConcurrentDictionary<uint, List<WorldObject>>();

        /// <summary>A player who starts seeing a figure is sent what it holds, after the figure itself, as ACE's
        /// TrackObject sends a creature's wielded items (TrackEquippedObject).</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.TrackObject))]
        static class FigureSeen
        {
            static void Postfix(Player __instance, WorldObject worldObject)
            {
                try
                {
                    if (worldObject == null || worldObject.WeenieClassId != FIGURE || __instance?.Session == null) return;
                    if (worldObject.Visibility && !__instance.Adminvision) return;
                    if (!s_FigureHeld.TryGetValue(worldObject.Guid.Full, out var held)) return;
                    foreach (var h in held.ToList())
                        if (!h.IsDestroyed) __instance.Session.Network.EnqueueSend(new GameMessageCreateObject(h));
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionFurniture: sending what a figure holds: {e.Message}"); }
            }
        }

        /// <summary>Appraising the weapon or shield in a figure's hand shows the real item's qualities (ACE finds only objects
        /// in a landblock or held by a creature, and the figure is neither).</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionIdentifyObject))]
        static class AppraiseHeld
        {
            static bool Prefix(Player __instance, uint objectGuid)
            {
                try
                {
                    if (objectGuid == 0 || __instance?.Session == null || !s_Copies.TryGetValue(objectGuid, out var hit)) return true;
                    WorldObject h;
                    lock (hit.d.Lock) h = hit.d.Held.FirstOrDefault(x => x.Guid.Full == objectGuid);
                    if (h == null) return true;
                    __instance.RequestedAppraisalTarget = objectGuid;
                    __instance.CurrentAppraisalTarget = objectGuid;
                    __instance.AppraisalRequestedTimestamp = ACE.Common.Time.GetUnixTime();
                    __instance.Session.Network.EnqueueSend(new GameEventIdentifyObjectResponse(__instance.Session, h, true));
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionFurniture: appraising a held copy: {e.Message}"); return true; }
            }
        }

        /// <summary>What a player walks to and acts on for a guid: the copy itself, or, for the weapon or shield in a figure's
        /// hand (never in the world on its own), the figure.</summary>
        static WorldObject Shown(Display d, uint guid)
        {
            var c = d.Copies.FirstOrDefault(x => x.Guid.Full == guid);
            if (c != null) return c;
            var h = d.Held.FirstOrDefault(x => x.Guid.Full == guid);
            return h == null ? null : d.Copies.FirstOrDefault(x => x.Guid.Full == (h.WielderId ?? 0));
        }

        static Position At(Position hookLoc, Vector3 offset, Quaternion extra)
        {
            var rot = hookLoc.Rotation;
            var pos = new Position(hookLoc);
            var world = hookLoc.Pos + Vector3.Transform(offset, rot);
            if (!hookLoc.Indoors)
            {
                pos.Pos = world;    // outdoors: SetPosition re-derives the land cell
                pos.LandblockId = new LandblockId(pos.GetCell());
            }
            else
            {
                pos.PositionX = world.X; pos.PositionY = world.Y; pos.PositionZ = world.Z;
            }
            pos.Rotation = Quaternion.Normalize(rot * extra);
            return pos;
        }

        static void Rotated(ref Vector3 lo, ref Vector3 hi, Quaternion q)
        {
            var nlo = new Vector3(float.MaxValue); var nhi = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) != 0 ? hi.X : lo.X, (i & 2) != 0 ? hi.Y : lo.Y, (i & 4) != 0 ? hi.Z : lo.Z);
                p = Vector3.Transform(p, q);
                nlo = Vector3.Min(nlo, p); nhi = Vector3.Max(nhi, p);
            }
            lo = nlo; hi = nhi;
        }

        static readonly ConcurrentDictionary<(uint, int), (Vector3, Vector3)?> s_Bounds = new ConcurrentDictionary<(uint, int), (Vector3, Vector3)?>();

        /// <summary>The object's extent in a placement, the way the client draws it: each part's
        /// gfxobj vertices, scaled by the setup's default scale, through that placement's frame
        /// (the default frame when the setup has none for it).</summary>
        static bool PoseBounds(uint setupId, int placement, out Vector3 lo, out Vector3 hi)
        {
            var r = s_Bounds.GetOrAdd((setupId, placement), key =>
            {
                try
                {
                    var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(key.Item1);
                    if (setup == null || setup.Parts.Count == 0) return null;
                    if (!setup.PlacementFrames.TryGetValue(key.Item2, out var pt) && !setup.PlacementFrames.TryGetValue(0, out pt)) pt = null;
                    var l = new Vector3(float.MaxValue); var h = new Vector3(float.MinValue);
                    for (int i = 0; i < setup.Parts.Count; i++)
                    {
                        var gfx = DatManager.PortalDat.ReadFromDat<GfxObj>(setup.Parts[i]);
                        if (gfx == null || gfx.VertexArray.Vertices.Count == 0) continue;
                        var sc = setup.DefaultScale.Count > i ? setup.DefaultScale[i] : Vector3.One;
                        var f = pt != null && pt.AnimFrame.Frames.Count > i ? pt.AnimFrame.Frames[i] : null;
                        foreach (var v in gfx.VertexArray.Vertices.Values)
                        {
                            var p = v.Origin * sc;
                            if (f != null) p = Vector3.Transform(p, f.Orientation) + f.Origin;
                            l = Vector3.Min(l, p); h = Vector3.Max(h, p);
                        }
                    }
                    return l.X <= h.X ? (l, h) : ((Vector3, Vector3)?)null;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionFurniture: bounds of 0x{key.Item1:X8}: {e.Message}"); return null; }
            });
            lo = r?.Item1 ?? Vector3.Zero; hi = r?.Item2 ?? Vector3.Zero;
            return r != null;
        }

        // ------------------------------------------------------------------ who may do what

        static House RootHouseOf(Display d) => d.Hook?.House?.RootHouse;

        /// <summary>Hang things: the house's storage permission (owner, their account, storage
        /// guests, the allegiance when it has storage), or an admin who walks through barriers.</summary>
        static bool MayHang(Player p, Display d) =>
            p.IgnoreHouseBarriers || StaffBaseHooks.Is(d.Hook) || (RootHouseOf(d)?.HasPermission(p, true) ?? false);

        static bool IsHouseOwner(Player p, Display d)
        {
            var rh = RootHouseOf(d);
            if (rh?.HouseOwner == null) return false;
            if (rh.HouseOwner == p.Guid.Full) return true;
            var owner = PlayerManager.FindByGuid(rh.HouseOwner.Value);
            return owner?.Account != null && owner.Account.AccountId == p.Account.AccountId;
        }

        /// <summary>Take something down: the one who hung it; the house owner, unless it is
        /// attuned (an attuned item goes back only to the one it is bound to).</summary>
        static bool MayTake(Player p, Display d, WorldObject real) =>
            (real.GetProperty(HUNG_BY) ?? 0) == p.Guid.Full
            || (!real.IsAttunedOrContainsAttuned && (IsHouseOwner(p, d) || p.IgnoreHouseBarriers
                                                     || (StaffBaseHooks.Is(d.Hook) && p.IsAdmin)));   // the Staff Base: an admin

        static bool MayUse(Player p, Display d)
        {
            var rh = RootHouseOf(d);
            return p.IgnoreHouseBarriers || rh == null || rh.OpenStatus || rh.HasPermission(p, false);
        }

        static void Say(Player p, string line, ChatMessageType type = ChatMessageType.Broadcast) =>
            p?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, type));

        // ------------------------------------------------------------------ what fits where

        static string WhyNot(Display d, WorldObject item)
        {
            var spec = s_Spec[d.Wcid];
            var it = item.ItemType;
            var loc = item.ValidLocations ?? EquipMask.None;
            switch (d.Wcid)
            {
                case RACK:
                    bool weapon = (it & (ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster)) != 0 && item.WeenieType != WeenieType.Ammunition;
                    bool shield = (loc & EquipMask.Shield) != 0;
                    if (!weapon && !shield) return "Only weapons, bows, wands and shields go on the weapon rack.";
                    break;
                case STAND:
                    if (HandSlot(item) is int hand)
                    {
                        // one weapon and one shield, as a character holds them (owner 2026-11-06)
                        var same = d.Items.FirstOrDefault(o => (o.GetProperty(SLOT) ?? -1) == hand);
                        if (same != null) return $"The figure already holds the {same.Name}. Take it off first.";
                        var other = d.Items.FirstOrDefault(o => (o.GetProperty(SLOT) ?? -1) == (hand == HAND_WEAPON ? HAND_SHIELD : HAND_WEAPON));
                        var held = hand == HAND_WEAPON ? item : other;
                        if (other != null && held != null && NoShieldWith(held))
                            return $"The figure cannot hold a shield with the {held.Name}.";
                        break;
                    }
                    if (!StandPiece(item))
                        return "Only armor, clothing, cloaks, a weapon and a shield go on the armor stand.";
                    foreach (var o in d.Items)
                        if (!InHand(o) && ((o.ValidLocations ?? EquipMask.None) & loc) != 0)
                            return $"The figure already wears {o.Name} there. Take it off first.";
                    break;
                case SHELF:
                    if (!IsPortalGem(item)) return "Only portal gems go on the portal gem shelf.";
                    break;
                case TROPHY:
                    // Generic wall hangings, and books that hang (the retail Mounted Fish is a Book);
                    // never the Allegiance Hall Board, which only works on its own hook.
                    bool trophy = s_ExpansionTrophies.Contains(item.WeenieClassId)
                        || ((item.WeenieType == WeenieType.Generic || item.WeenieType == WeenieType.Book)
                            && ((item.HookType ?? 0) & (int)HookType.Wall) != 0 && !(item.GetProperty(Mansions.BOARD) ?? false));
                    if (!trophy) return "Only trophies and other wall hangings go on the trophy wall.";
                    break;
            }
            return null;
        }

        static bool IsPortalGem(WorldObject item)
        {
            if (item.WeenieType != WeenieType.Gem || item.SpellDID == null) return false;
            var sp = new Spell(item.SpellDID.Value);
            if (sp.NotFound) return false;
            switch (sp.MetaSpellType)
            {
                case SpellType.PortalLink: case SpellType.PortalRecall: case SpellType.PortalSummon:
                case SpellType.PortalSending: case SpellType.FellowPortalSending:
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ putting something on

        /// <summary>A drop on the furniture (the hook the client sees) or on anything shown on it.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
        static class GiveTo
        {
            [HarmonyPriority(Priority.Low)]     // after ItemLock's refusal of a locked item (HouseRentNotes' rule)
            static bool Prefix(Player __instance, uint targetGuid, uint itemGuid, int amount, bool __runOriginal)
            {
                try
                {
                    if (!__runOriginal) return false;   // an earlier prefix (ItemLockGive) already refused it
                    var d = DisplayAt(__instance, targetGuid, out var target);
                    if (d == null) return true;
                    __instance.CreateMoveToChain(target, ok =>
                    {
                        try { if (ok) Put(__instance, d, itemGuid, amount); else Refuse(__instance, itemGuid, null); }
                        catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: put: {e}"); Refuse(__instance, itemGuid, "That did not work. It has been logged for staff."); }
                    });
                    return false;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: give: {e}"); return true; }
            }
        }

        /// <summary>The display a guid belongs to: a hook holding furniture, or one of its copies.</summary>
        /// <summary>COPIES REPLACED IN THE LAST MINUTE, by their old guid. Every piece put up rebuilds the copies (the figure too)
        /// under new guids, so a selection dropped on a figure (owner 261102-0045, "dragging and dropping multiple items at once
        /// doesnt work"), given one item at a time by the client, aimed every item after the first at a figure that was gone.
        /// Such a give lands on the furniture's hook instead. (A destroyed copy's guid is recycled only much later.)</summary>
        static readonly ConcurrentDictionary<uint, (Display d, DateTime at)> s_Retired = new ConcurrentDictionary<uint, (Display, DateTime)>();

        static Display DisplayAt(Player p, uint guid, out WorldObject target)
        {
            target = null;
            if (!s_Copies.ContainsKey(guid) && s_Retired.TryGetValue(guid, out var old))
            {
                if (DateTime.UtcNow - old.at < TimeSpan.FromMinutes(1) && old.d.Hook != null && !old.d.Hook.IsDestroyed)
                {
                    target = old.d.Hook;
                    return old.d;
                }
                s_Retired.TryRemove(guid, out _);
            }
            if (s_Copies.TryGetValue(guid, out var hit))
            {
                target = Shown(hit.d, guid);
                return target != null ? hit.d : null;
            }
            if (p.CurrentLandblock?.GetObject(guid) is Hook hook && IsFurniture(hook.Item)
                && s_Displays.TryGetValue(hook.Item.Guid.Full, out var d))
            {
                target = hook;
                return d;
            }
            return null;
        }

        /// <summary>A drop on a Staff Base hook sent as PutItemInContainer (StaffBaseHooks.PutIn): put it on
        /// the furniture the hook holds, as a drop on the furniture is. False when the hook holds none.</summary>
        internal static bool PutFromContainer(Player p, Hook hook, uint itemGuid)
        {
            if (!IsFurniture(hook.Item) || !s_Displays.TryGetValue(hook.Item.Guid.Full, out var d)) return false;
            p.CreateMoveToChain(hook, ok =>
            {
                try { if (ok) Put(p, d, itemGuid, int.MaxValue); else Refuse(p, itemGuid, null); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: put from container: {e}"); Refuse(p, itemGuid, "That did not work. It has been logged for staff."); }
            });
            return true;
        }

        static void Refuse(Player p, uint itemGuid, string why)
        {
            if (why != null) p.SendTransientError(why);
            p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, itemGuid, WeenieError.None));
        }

        static void Put(Player p, Display d, uint itemGuid, int amount)
        {
            var spec = s_Spec[d.Wcid];
            var item = p.FindObject(itemGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems, out _, out _, out bool equipped);
            if (item == null) { Refuse(p, itemGuid, null); return; }
            // A WORN PIECE dragged from the paper doll (owner 261102-0045, "dragging items from equipped to the wooden figure
            // doesnt work"): on a Staff Base piece it comes off you and goes up, the double-click store's rule (TradeSuits);
            // a house's furniture still asks you to take it off first.
            if (equipped && !StaffBaseHooks.Is(d.Hook)) { Refuse(p, itemGuid, $"Take off the {item.Name} first."); return; }
            if (!MayHang(p, d)) { Refuse(p, itemGuid, $"Only someone with storage rights in this house can put things on the {spec.Name.ToLowerInvariant()}."); return; }
            if (item is Container) { Refuse(p, itemGuid, $"Empty packs and chests do not go on the {spec.Name.ToLowerInvariant()}."); return; }
            if (p.IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(p.ItemsInTradeWindow)) { Refuse(p, itemGuid, $"The {item.Name} is being traded."); return; }

            lock (d.Lock)
            {
                if (!d.Loaded) { Refuse(p, itemGuid, $"The {spec.Name.ToLowerInvariant()} is still being set up. Try again in a moment."); return; }
                var why = WhyNot(d, item);
                if (why != null) { Refuse(p, itemGuid, why); return; }

                int stack = item.StackSize ?? 1;
                int take = Math.Clamp(amount, 1, stack);

                // a gem of a kind already on the shelf joins that stack
                if (d.Wcid == SHELF)
                {
                    var same = d.Items.FirstOrDefault(o => o.WeenieClassId == item.WeenieClassId && (o.StackSize ?? 1) + take <= (o.MaxStackSize ?? 1));
                    if (same != null)
                    {
                        if (!p.TryConsumeFromInventoryWithNetworking(item, take)) { Refuse(p, itemGuid, null); return; }
                        same.SetStackSize((same.StackSize ?? 1) + take);
                        same.SaveBiotaToDatabase();
                        Done(p, d, $"You add {take} {(take == 1 ? item.Name : item.GetPluralName())} to the {spec.Name.ToLowerInvariant()}.", same);
                        return;
                    }
                }

                int limit = Limit(d);
                int? hand = d.Wcid == STAND ? HandSlot(item) : null;   // a weapon or shield takes a hand, not one of the nine
                if (hand == null && d.Items.Count(o => !InHand(o)) >= limit) { Refuse(p, itemGuid, $"The {spec.Name.ToLowerInvariant()} is full. Take something off first."); return; }
                int slot = hand ?? Enumerable.Range(0, limit).First(i => !d.Items.Any(o => (o.GetProperty(SLOT) ?? -1) == i));

                WorldObject real;
                if (equipped)
                {
                    if (!p.TryDequipObjectWithNetworking(item.Guid, out real, Player.DequipObjectAction.TradeItem)) { Refuse(p, itemGuid, null); return; }
                }
                else if (take < stack)
                {
                    // a split: the stack left in the pack shrinks and a new stack of `take` goes up
                    real = WorldObjectFactory.CreateNewWorldObject(item.WeenieClassId);
                    if (real == null) { Refuse(p, itemGuid, null); return; }
                    real.SetStackSize(take);
                    if (!p.TryConsumeFromInventoryWithNetworking(item, take)) { real.Destroy(); Refuse(p, itemGuid, null); return; }
                }
                else
                {
                    if (!p.TryRemoveFromInventoryWithNetworking(item.Guid, out real, Player.RemoveFromInventoryAction.GiveItem)) { Refuse(p, itemGuid, null); return; }
                }
                real.Location = null;
                real.ContainerId = d.FurnitureGuid;
                real.PlacementPosition = null;
                real.SetProperty(HUNG_BY, p.Guid.Full);
                real.SetProperty(SLOT, slot);
                real.SaveBiotaToDatabase();
                d.Items.Add(real);
                d.Items.Sort((a, b) => (a.GetProperty(SLOT) ?? 99).CompareTo(b.GetProperty(SLOT) ?? 99));
                Mod.Log.Info($"[RevivalGuard] MansionFurniture: {p.Name} put {real.Name} 0x{real.Guid.Full:X8} on {spec.Name} 0x{d.FurnitureGuid:X8} (slot {slot})");
                Done(p, d, d.Wcid == STAND ? (hand != null ? $"You put the {real.Name} in the figure's hand{(hand == HAND_SHIELD ? " (on its arm)" : "")}." : $"You dress the figure in the {real.Name}.") : $"You put the {real.Name} on the {spec.Name.ToLowerInvariant()}.", real);
            }
        }

        static void Done(Player p, Display d, string line, WorldObject _)
        {
            Say(p, line);
            Refresh(d);
        }

        // ------------------------------------------------------------------ taking off, and using

        /// <summary>Double-click on a copy or the figure (Use), or on the furniture itself.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseItem))]
        static class UseCopy
        {
            static bool Prefix(Player __instance, uint itemGuid)
            {
                try
                {
                    if (!s_Copies.TryGetValue(itemGuid, out var hit)) return true;
                    var d = hit.d;
                    var copy = Shown(d, itemGuid);
                    // EVERY USE ENDS WITH UseDone, as ACE's own HandleActionUseItem does. Without it the
                    // client's use stays busy: the owner's 2026-09-29 log, "the server never finished
                    // use 0x8000114B Wooden Figure (12 s, no UseDone)".
                    if (copy == null) { __instance.SendUseDoneEvent(); return false; }
                    __instance.CreateMoveToChain(copy, ok =>
                    {
                        try
                        {
                            if (!ok) return;
                            if (d.Wcid == SHELF) UseGem(__instance, d, hit.real);
                            else if (d.Wcid == STAND && hit.real == 0 && StaffBaseHooks.Is(d.Hook)) TradeSuits(__instance, d);
                            else TakeDown(__instance, d, hit.real);
                        }
                        catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: use: {e}"); }
                        finally { __instance.SendUseDoneEvent(); }
                    });
                    return false;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: use copy: {e}"); return true; }
            }
        }

        /// <summary>F / drag into a pack on a copy: take it back (every piece, gems included).</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionPutItemInContainer))]
        static class PickUp
        {
            static bool Prefix(Player __instance, uint itemGuid, uint containerGuid)
            {
                try
                {
                    if (s_Copies.TryGetValue(itemGuid, out var hit))
                    {
                        var copy = Shown(hit.d, itemGuid);
                        if (copy == null) { Refuse(__instance, itemGuid, null); return false; }
                        __instance.CreateMoveToChain(copy, ok =>
                        {
                            try { if (ok) TakeDown(__instance, hit.d, hit.real); }
                            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: pick up: {e}"); }
                        });
                        return false;
                    }
                    // the furniture itself off its hook: not while it holds anything
                    if (s_Displays.TryGetValue(itemGuid, out var d) && d.Hook != null)
                    {
                        bool busy;
                        lock (d.Lock) busy = !d.Loaded || d.Items.Count > 0;
                        if (busy)
                        {
                            Refuse(__instance, itemGuid, $"Take everything off the {s_Spec[d.Wcid].Name.ToLowerInvariant()} before you take it down.");
                            return false;
                        }
                    }
                    return true;
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: put in container: {e}"); return true; }
            }
        }

        /// <summary>No tinkering, mana stones or keys on a copy, and no copy used on anything.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionUseWithTarget))]
        static class NoUseWith
        {
            static bool Prefix(Player __instance, uint sourceObjectGuid, uint targetObjectGuid)
            {
                if (!s_Copies.ContainsKey(sourceObjectGuid) && !s_Copies.ContainsKey(targetObjectGuid)) return true;
                __instance.SendTransientError("That is on display. Take it down first.");
                __instance.SendUseDoneEvent();
                return false;
            }
        }

        static void TakeDown(Player p, Display d, uint realGuid)
        {
            var spec = s_Spec[d.Wcid];
            lock (d.Lock)
            {
                var list = realGuid != 0 ? d.Items.Where(i => i.Guid.Full == realGuid).ToList() : d.Items.ToList();
                // A BARE FIGURE HAS NOTHING TO GIVE BACK. This used to Refresh, which destroys the
                // figure and makes a new one (a new guid on every double-click), and said nothing; the
                // owner double-clicked it expecting to dress it (2026-09-29). Say how instead.
                if (list.Count == 0 && realGuid == 0 && d.Wcid == STAND)
                {
                    Say(p, StaffBaseHooks.Is(d.Hook)
                        ? "The figure is bare. Double-click it while wearing a suit to put the suit on it, or drag pieces onto it."
                        : "Drag armor, clothing, a weapon or a shield from your pack onto the figure to dress it, one piece at a time. Double-click it to take everything back.");
                    return;
                }
                if (list.Count == 0) { Refresh(d); return; }
                var mine = list.Where(i => MayTake(p, d, i)).ToList();
                if (mine.Count == 0)
                {
                    var by = list[0].GetProperty(HUNG_BY) is uint g ? PlayerManager.FindByGuid(g)?.Name : null;
                    Say(p, realGuid != 0
                        ? $"{(by != null ? by + " hung" : "Someone hung")} the {list[0].Name} here. "
                          + (list[0].IsAttunedOrContainsAttuned ? "It is attuned to them, so only they can take it down." : "Only they or the owner of the house can take it down.")
                          + " Appraise it to see its qualities."
                        : "Only the one who dressed the figure, or the owner of the house, can take its armor off. Appraise the figure to see what it wears.");
                    return;
                }
                int moved = 0;
                foreach (var real in mine)
                {
                    real.RemoveProperty(HUNG_BY);
                    real.RemoveProperty(SLOT);
                    real.ContainerId = null;
                    if (!p.TryCreateInInventoryWithNetworking(real))
                    {
                        real.ContainerId = d.FurnitureGuid;   // back on display, nothing lost
                        real.SetProperty(HUNG_BY, p.Guid.Full);
                        real.SetProperty(SLOT, NextFreeSlot(d, real));
                        real.SaveBiotaToDatabase();
                        Say(p, $"You have no room for the {real.Name}.");
                        continue;
                    }
                    d.Items.Remove(real);
                    moved++;
                    Mod.Log.Info($"[RevivalGuard] MansionFurniture: {p.Name} took {real.Name} 0x{real.Guid.Full:X8} off {spec.Name} 0x{d.FurnitureGuid:X8}");
                }
                if (moved > 0)
                    Say(p, moved == 1 ? $"You take the {mine[0].Name} off the {spec.Name.ToLowerInvariant()}." : $"You take {moved} pieces off the {spec.Name.ToLowerInvariant()}.");
                Refresh(d);
            }
        }

        /// <summary>How many pieces the furniture holds. An armor stand's figure takes 9 (a suit of armor); a Staff Base
        /// stand takes 16, so a whole suit with its underclothes fits for TradeSuits.</summary>
        static int Limit(Display d) => d.Wcid == STAND ? (StaffBaseHooks.Is(d.Hook) ? 16 : 9) : s_Spec[d.Wcid].Slots.Length;

        // ------------------------------------------------------------------ trading suits (the Staff Base's stands)

        static readonly System.Reflection.MethodInfo s_WieldCheck = AccessTools.Method(typeof(Player), "CheckWieldRequirements");

        // THE FIGURE'S HANDS (owner 2026-11-06: stands "also hold weapons and a shield, so a stand can show a full outfit
        // with the weapon in hand and the shield on the arm"). Their own two places, apart from the 9 (house) or 16 (Staff
        // Base) pieces of armor and clothing, kept in SLOT as 100 and 101.
        const int HAND_WEAPON = 100, HAND_SHIELD = 101;

        static bool IsShieldItem(WorldObject i) => i.ItemType == ItemType.Armor && ((i.ValidLocations ?? EquipMask.None) & EquipMask.Shield) != 0;

        static bool IsWeaponItem(WorldObject i) =>
            (i.ItemType & (ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster)) != 0 && i.WeenieType != WeenieType.Ammunition
            && ((i.ValidLocations ?? EquipMask.None) & (EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.TwoHanded)) != 0;

        static int? HandSlot(WorldObject i) => IsWeaponItem(i) ? HAND_WEAPON : IsShieldItem(i) ? HAND_SHIELD : (int?)null;

        static bool InHand(WorldObject real) => (real.GetProperty(SLOT) ?? -1) >= HAND_WEAPON;

        static bool IsBow(WorldObject i) => i.DefaultCombatStyle == CombatStyle.Bow || i.DefaultCombatStyle == CombatStyle.Crossbow;

        /// <summary>A bow or crossbow (held in the left hand) or a two-handed weapon leaves no arm for a shield, as on a character.</summary>
        static bool NoShieldWith(WorldObject weapon) =>
            ((weapon.ValidLocations ?? EquipMask.None) & EquipMask.TwoHanded) != 0 || (((weapon.ValidLocations ?? EquipMask.None) & EquipMask.MissileWeapon) != 0 && IsBow(weapon));

        /// <summary>How the figure holds it: ACE's Creature.GetPlacementLocation for the slot a character would wield it in.</summary>
        static (EquipMask loc, Placement pose, ParentLocation parent) HandPose(WorldObject real)
        {
            var v = real.ValidLocations ?? EquipMask.None;
            if (IsShieldItem(real)) return (EquipMask.Shield, Placement.Shield, ParentLocation.Shield);
            if ((v & EquipMask.TwoHanded) != 0) return (EquipMask.TwoHanded, Placement.RightHandCombat, ParentLocation.RightHand);
            if ((v & EquipMask.MissileWeapon) != 0)
                return IsBow(real) ? (EquipMask.MissileWeapon, Placement.LeftHand, ParentLocation.LeftHand)
                                   : (EquipMask.MissileWeapon, Placement.RightHandCombat, ParentLocation.RightHand);
            if ((v & EquipMask.Held) != 0 && (v & EquipMask.MeleeWeapon) == 0) return (EquipMask.Held, Placement.RightHandCombat, ParentLocation.RightHand);
            return (EquipMask.MeleeWeapon, Placement.RightHandCombat, ParentLocation.RightHand);
        }

        /// <summary>Would this worn item go on an armor stand (WhyNot's STAND rule, without the one-per-slot check).</summary>
        static bool StandPiece(WorldObject item)
        {
            const EquipMask worn = EquipMask.Armor | EquipMask.Clothing | EquipMask.HeadWear | EquipMask.HandWear | EquipMask.FootWear
                        | EquipMask.Cloak;   // cloaks too (owner M5 261103-0003, "capes are also not able to be worn on armor stands")
            var loc = item.ValidLocations ?? EquipMask.None;
            return (item.ItemType & (ItemType.Armor | ItemType.Clothing)) != 0 && (loc & worn) != 0 && (loc & EquipMask.Shield) == 0;
        }

        /// <summary>THE OWNER'S WARDROBE (owner 2026-11-02: "give me armor housing items so I can line up my suits of armor and
        /// choose them from the mannequin as i see fit"). On a Staff Base armor stand a double-click on the figure TRADES SUITS:
        /// the armor and clothing you wear go up on the figure, and the figure's suit comes to you and is put on (ACE's own
        /// wield, with its requirements; a piece that cannot be worn stays in the pack). An empty figure just takes your suit
        /// (store it); wearing nothing, you just take the figure's suit back. The main-hand weapon and the shield go with the suit
        /// (owner 2026-11-06); jewelry and a dual-wielded off-hand weapon stay where they are.
        /// The pieces are the real items throughout (enchantments, attunement and ownership go with them, as on any stand),
        /// saved at each step. Player houses' stands keep the double-click take-down (TakeDown).</summary>
        static void TradeSuits(Player p, Display d)
        {
            lock (d.Lock)
            {
                if (!d.Loaded) { Say(p, "The armor stand is still being set up. Try again in a moment."); return; }
                var theirs = d.Items.ToList();
                var notMine = theirs.Where(i => !MayTake(p, d, i)).ToList();
                if (notMine.Count > 0) { Say(p, $"The figure wears {Label(notMine[0])}, which is not yours to take."); return; }
                var mine = p.EquippedObjects.Values.Where(StandPiece).ToList();
                // and what is in your hands: the main-hand weapon (a dual-wielded off-hand weapon stays) and the shield
                var weapon = p.EquippedObjects.Values.FirstOrDefault(o => IsWeaponItem(o) && o.CurrentWieldedLocation != EquipMask.Shield
                                                                       && (o.CurrentWieldedLocation & (EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.TwoHanded)) != 0);
                var shield = p.EquippedObjects.Values.FirstOrDefault(o => IsShieldItem(o) && o.CurrentWieldedLocation == EquipMask.Shield);
                int armorCount = mine.Count;
                if (weapon != null) mine.Add(weapon);
                if (shield != null) mine.Add(shield);
                if (theirs.Count == 0 && mine.Count == 0)
                {
                    Say(p, "The figure is bare and you wear no armor. Double-click it while wearing a suit to put the suit on it.");
                    return;
                }
                if (armorCount > Limit(d)) { Say(p, $"You wear {armorCount} pieces; the figure holds {Limit(d)}."); return; }
                if (theirs.Count > 0 && p.GetFreeInventorySlots(true) < theirs.Count)
                {
                    Say(p, $"Make room in your main pack for {theirs.Count} pieces first: the figure's suit passes through it.");
                    return;
                }

                // 1. the figure's suit comes off it (still saved as on the figure until it reaches the pack)
                foreach (var real in theirs) d.Items.Remove(real);

                // 2. what you wear goes up
                int up = 0, slot = 0;
                foreach (var w in mine)
                {
                    if (!p.TryDequipObjectWithNetworking(w.Guid, out var real, Player.DequipObjectAction.TradeItem)) continue;
                    real.Location = null;
                    real.ContainerId = d.FurnitureGuid;
                    real.PlacementPosition = null;
                    real.SetProperty(HUNG_BY, p.Guid.Full);
                    real.SetProperty(SLOT, HandSlot(real) is int hs && (real == weapon || real == shield) ? hs : slot++);
                    real.SaveBiotaToDatabase();
                    d.Items.Add(real);
                    up++;
                    Mod.Log.Info($"[RevivalGuard] MansionFurniture: {p.Name} put {real.Name} 0x{real.Guid.Full:X8} on the figure 0x{d.FurnitureGuid:X8} (trade)");
                }

                // 3. the figure's suit comes to your pack now, and is put on a second later in its own step: the client
                //    takes a wield only for an item it has already been sent (box 2026-11-02: wielded in the same tick as
                //    its create, the server had it worn and the client still listed it in the pack)
                var packed = new List<WorldObject>();
                foreach (var real in theirs)
                {
                    real.RemoveProperty(HUNG_BY);
                    real.RemoveProperty(SLOT);
                    real.ContainerId = null;
                    if (!p.TryCreateInInventoryWithNetworking(real))
                    {
                        real.ContainerId = d.FurnitureGuid;   // back on the figure, nothing lost
                        real.SetProperty(HUNG_BY, p.Guid.Full);
                        real.SetProperty(SLOT, NextFreeSlot(d, real));
                        real.SaveBiotaToDatabase();
                        d.Items.Add(real);
                        Say(p, $"You have no room for the {real.Name}; it stays on the figure.");
                        continue;
                    }
                    packed.Add(real);
                    Mod.Log.Info($"[RevivalGuard] MansionFurniture: {p.Name} took {real.Name} 0x{real.Guid.Full:X8} off the figure 0x{d.FurnitureGuid:X8} (trade)");
                }
                string Pieces(int n) => n == 1 ? "1 piece" : $"{n} pieces";
                if (packed.Count == 0)
                {
                    Say(p, theirs.Count == 0 ? $"You put your suit on the figure ({Pieces(up)})." : $"You put {Pieces(up)} on the figure.");
                }
                else
                {
                    var wear = new ActionChain();
                    wear.AddDelaySeconds(1.0);
                    wear.AddAction(p, () =>
                    {
                        int worn = 0;
                        foreach (var real in packed)
                        {
                            try
                            {
                                if (p.FindObject(real.Guid.Full, Player.SearchLocations.MyInventory, out _, out _, out bool on) == null || on) continue;   // moved or worn since
                                var why = s_WieldCheck?.Invoke(p, new object[] { real }) is WeenieError we ? we : WeenieError.None;
                                var wantLoc = HandSlot(real) != null ? HandPose(real).loc : real.ValidLocations;
                                if (why == WeenieError.None && wantLoc is EquipMask loc && loc != EquipMask.None
                                    && !p.EquippedObjects.Values.Any(o => ((o.CurrentWieldedLocation ?? EquipMask.None) & loc) != 0))
                                    p.HandleActionGetAndWieldItem(real.Guid.Full, loc);
                                if (real.WielderId == p.Guid.Full) worn++;
                            }
                            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: trade wield {real.Name}: {e}"); }
                        }
                        int left = packed.Count - worn;
                        string rest = left > 0 ? $"; {left} left in your pack (you cannot wear {(left == 1 ? "it" : "them")} now)" : "";
                        Say(p, up == 0 ? $"You take the figure's suit and put it on ({Pieces(worn)}{rest})."
                                       : $"You trade suits with the figure: {Pieces(up)} up, {worn} put on{rest}.");
                    });
                    wear.EnqueueChain();
                }
                Refresh(d);
            }
        }

        static int NextFreeSlot(Display d, WorldObject self)
        {
            if (d.Wcid == STAND && HandSlot(self) is int hand) return hand;
            int limit = Limit(d);
            for (int i = 0; i < limit; i++)
                if (!d.Items.Any(o => o != self && (o.GetProperty(SLOT) ?? -1) == i)) return i;
            return 0;
        }

        /// <summary>A gem on the shelf is used as it would be from a pack (Gem.UseGem): a summoning
        /// gem opens its portal in front of the user, a sending gem sends them, and one is used up.
        /// Anyone let into the house may use it; that is what the shelf is for.</summary>
        static void UseGem(Player p, Display d, uint realGuid)
        {
            WorldObject gem;
            lock (d.Lock) gem = d.Items.FirstOrDefault(i => i.Guid.Full == realGuid);
            if (gem == null) { Refresh(d); return; }
            if (!MayUse(p, d)) { Say(p, "You are not welcome to use the gems in this house."); return; }
            if (p.IsBusy || p.Teleporting || p.IsDead) { p.SendWeenieError(WeenieError.YoureTooBusy); return; }
            var req = gem.CheckUseRequirements(p);
            if (!req.Success) { if (req.Message != null) p.Session.Network.EnqueueSend(req.Message); return; }
            var spell = new Spell(gem.SpellDID ?? 0);
            if (spell.NotFound) { Say(p, $"The {gem.Name} has no magic left in it."); return; }

            Mod.Log.Info($"[RevivalGuard] MansionFurniture: {p.Name} used {gem.Name} 0x{gem.Guid.Full:X8} from the shelf 0x{d.FurnitureGuid:X8} ({gem.StackSize ?? 1} before)");
            if (spell.MetaSpellType == SpellType.PortalSummon)
                gem.TryCastSpell(spell, p, gem, tryResist: false);
            else
                p.TryCastSpell(spell, p, gem, tryResist: false);
            if (gem.UseSound > 0) p.Session.Network.EnqueueSend(new GameMessageSound(p.Guid, gem.UseSound));

            lock (d.Lock)
            {
                if ((gem.GetProperty(PropertyBool.UnlimitedUse) ?? false) == false)
                {
                    int left = (gem.StackSize ?? 1) - 1;
                    if (left > 0) { gem.SetStackSize(left); gem.SaveBiotaToDatabase(); }
                    else { d.Items.Remove(gem); gem.Destroy(); }
                }
                Refresh(d);
            }
        }

        /// <summary>Double-click on the furniture with hooks off: what it holds, and how to use it.
        /// (Hook.ActOnUse redirects to the item's OnActivate after Hooker's permission checks.)</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.OnActivate))]
        static class Status
        {
            static bool Prefix(WorldObject __instance, WorldObject activator)
            {
                if (!(__instance is Hooker) || !IsFurniture(__instance) || !(activator is Player p)) return true;
                try
                {
                    if (!s_Displays.TryGetValue(__instance.Guid.Full, out var d)) { Say(p, $"Hang the {__instance.Name} on a hook in your house first."); return false; }
                    var spec = s_Spec[d.Wcid];
                    int limit = Limit(d);
                    List<WorldObject> items;
                    lock (d.Lock) items = d.Items.ToList();
                    int count = d.Wcid == STAND ? items.Count(i => !InHand(i)) : items.Count;
                    Say(p, items.Count == 0 ? (d.Wcid == STAND ? $"The armor stand is empty ({limit} places for armor and clothing, and a weapon and a shield in its hands)."
                                                               : $"The {spec.Name.ToLowerInvariant()} is empty ({limit} places for {spec.Holds}).")
                                            : $"The {spec.Name.ToLowerInvariant()} holds {count} of {limit}{(count == items.Count ? "" : $" pieces and {items.Count - count} in hand")}: {string.Join(", ", items.Select(i => Label(i)))}.");
                    if (MayHang(p, d)) Say(p, spec.Takes);
                    Say(p, d.Wcid == SHELF ? "Double-click a gem to use it. Pick a stack up (F) to take it back."
                         : d.Wcid == STAND && StaffBaseHooks.Is(d.Hook) ? "Double-click the figure to trade suits: it takes the armor, clothing, cloak, weapon and shield you wear and dresses you in its suit. Bare, it just takes yours; wearing nothing, you just put its suit on. Shift and double-click to take its suit into your pack without wearing it."
                         : d.Wcid == STAND ? "Double-click the figure to take back what you dressed it in, or its weapon or shield to take just that. Appraise it to see what it wears."
                         : $"Double-click a {spec.Noun} or pick it up (F) to take it back. Appraise one to see its qualities.");
                }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] MansionFurniture: status: {e}"); }
                return false;
            }
        }

        static string Label(WorldObject i)
        {
            int n = i.StackSize ?? 1;
            var by = i.GetProperty(HUNG_BY) is uint g ? PlayerManager.FindByGuid(g)?.Name : null;
            return (n > 1 ? $"{n} {i.GetPluralName()}" : i.Name) + (by != null ? $" ({by})" : "");
        }

        // ------------------------------------------------------------------ the figure, dressed

        const uint HUMAN = 0x02000001;       // the figure's setup, which every ClothingTable keys on
        const uint WOOD_BASE = 0x01FF0510;   // wooden stand-ins for human parts 0-16

        /// <summary>THE FIGURE'S LOOK: Creature.CalculateObjDesc's clothing pass, run on the copies
        /// it wears. Clothes first, then armour bottom layer to top, each ClothingTable's parts,
        /// textures and palettes for the human setup; then any expansion item's own part rows (as
        /// ACE's ApplyOwnAnimParts does for a wearer); then WOOD on every part 0-16 nothing covers,
        /// the head included (a helm's part replaces the head, so a helmed figure has the helm's).</summary>
        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.CalculateObjDesc))]
        static class FigureDress
        {
            static void Postfix(WorldObject __instance, ref ACE.Entity.ObjDesc __result)
            {
                if (__instance == null || __instance.WeenieClassId != FIGURE || __result == null) return;
                if (!s_Figures.TryGetValue(__instance.Guid.Full, out var wear)) wear = new List<WorldObject>();
                try { Dress(__result, wear); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] MansionFurniture: dressing the figure: {e.Message}"); }
            }
        }

        static void Dress(ACE.Entity.ObjDesc objDesc, List<WorldObject> wear)
        {
            var coverage = new HashSet<uint>();
            var armor = wear.Where(x => x.ItemType == ItemType.Armor || ((x.CurrentWieldedLocation ?? 0) & (EquipMask.Armor | EquipMask.Extremity)) != 0).ToList();
            foreach (var w in armor) w.setVisualClothingPriority();
            var sorted = armor.Where(x => x.TopLayerPriority == false).OrderBy(x => x.VisualClothingPriority)
                .Concat(armor.Where(x => x.TopLayerPriority == null).OrderBy(x => x.VisualClothingPriority))
                .Concat(armor.Where(x => x.TopLayerPriority == true).OrderBy(x => x.VisualClothingPriority));
            var clothes = wear.Where(x => x.ItemType == ItemType.Clothing && ((x.CurrentWieldedLocation ?? 0) & (EquipMask.Armor | EquipMask.Extremity)) == 0)
                .OrderBy(x => x.ClothingPriority);

            foreach (var w in clothes.Concat(sorted))
            {
                if (w.ClothingBase == null) continue;
                var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(w.ClothingBase.Value);
                if (table == null || !table.ClothingBaseEffects.TryGetValue(HUMAN, out var effect)) continue;
                foreach (var t in effect.CloObjectEffects)
                {
                    coverage.Add(t.Index);
                    objDesc.AddAnimPartChange(new PropertiesAnimPart { Index = (byte)t.Index, AnimationId = t.ModelId });
                    foreach (var t1 in t.CloTextureEffects)
                        objDesc.AddTextureChange(new PropertiesTextureMap { PartIndex = (byte)t.Index, OldTexture = t1.OldTexture, NewTexture = t1.NewTexture });
                }
                if (table.ClothingSubPalEffects.Count == 0) continue;
                int option = w.PaletteTemplate ?? 0;
                var sub = table.ClothingSubPalEffects.TryGetValue((uint)option, out var s) ? s : table.ClothingSubPalEffects.Values.First();
                float shade = (float)(w.Shade ?? 0);
                foreach (var cs in sub.CloSubPalettes)
                {
                    var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(cs.PaletteSet);
                    ushort pal = (ushort)set.GetPaletteID(shade);
                    foreach (var r in cs.Ranges)
                        objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = pal, Offset = (ushort)(r.Offset / 8), Length = (ushort)(r.NumColors / 8) });
                }
            }

            // an expansion item's own parts (Sentinel's Helm) replace, by index, what its table put there
            foreach (var w in wear)
            {
                var own = new List<PropertiesAnimPart>();
                w.Biota.PropertiesAnimPart.CopyTo(own, w.BiotaDatabaseLock);
                foreach (var a in own)
                {
                    if (!coverage.Contains(a.Index)) continue;
                    objDesc.AnimPartChanges.RemoveAll(x => x.Index == a.Index);
                    objDesc.AnimPartChanges.Add(new PropertiesAnimPart { Index = a.Index, AnimationId = a.AnimationId });
                }
            }

            for (byte i = 0; i <= 16; i++)
                if (!coverage.Contains(i))
                {
                    objDesc.AnimPartChanges.RemoveAll(x => x.Index == i);
                    objDesc.AnimPartChanges.Add(new PropertiesAnimPart { Index = i, AnimationId = WOOD_BASE + i });
                }
        }
    }
}
