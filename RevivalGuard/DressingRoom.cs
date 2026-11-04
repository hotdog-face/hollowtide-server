using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.Sequence;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE STAFF DRESSING ROOM (owner 2026-11-03: "a dressing room ... an npc i talk to that opens a ui that allows me to
    /// select any armor look, and any color show as it would look ... players cant see this. a staff only tool. id use it
    /// to make npcs, monsters, and my own armor tailors"). Ours. docs/DRESSING-ROOM.md.
    ///
    /// The Tailor in the Staff Base (weenie TAILOR) opens our client's Dressing Room panel ("dress" on the mod channel),
    /// for Sentinel and up only. Every "@dress" verb is a Sentinel command, so the gate is ACE's own access check, not the
    /// panel. The shard keeps the outfit being built (one per character, lost on logout) and answers each change with:
    ///  - the state ("dress" {"st":..}) for the panel, and
    ///  - a PREVIEW: an ObjDescEvent for the staff member's own character, sent to that one connection only. Nobody else
    ///    sees it, and nothing is saved. The look is ACE's own Creature.CalculateObjDesc (with every patch on it) run on
    ///    a stand-in creature (weenie MANNEQUIN) that copies the character's body and wears the outfit as real items, so
    ///    the preview is what the same items would really look like. "close" sends the real look back.
    /// Apply: "worn" tailors each piece onto the worn item in the same slots (ACE's Tailoring.UpdateArmorProps, the kit's
    /// own copy), "new" creates the pieces in the pack, "npc" dresses the selected non-player creature with real items
    /// (not saved: a respawn drops it). "template" writes a create-list datafix snippet beside the mod
    /// (dressing-room/&lt;name&gt;.sql). Outfits are saved for all staff in dressing-room/outfits.json.
    /// </summary>
    public static class DressingRoom
    {
        public const uint TAILOR = 908200, MANNEQUIN = 908201, TRUNK = 908206;
        static bool IsMannequin(uint wcid) => wcid >= 908202 && wcid <= 908204;   // the room's dressed mannequins
        const AccessLevel STAFF = AccessLevel.Sentinel;
        const uint LOOK_MASK = 0x7FFF | (uint)EquipMask.Cloak;   // the clothing and armour slots, and the cloak
        const int PAGE = 40;

        public sealed class Piece
        {
            public uint W { get; set; }       // the weenie the look comes from
            public string N { get; set; }
            public uint Loc { get; set; }     // its ValidLocations
            public int Cov { get; set; }      // its ClothingPriority (the body it covers): what ACE checks for a clash
            public uint Cb { get; set; }      // its ClothingBase
            public int Pal { get; set; }      // PaletteTemplate
            public double Sh { get; set; }    // Shade
            public int Ly { get; set; }       // TopLayerPriority: 1 top, 0 unset, -1 bottom
        }

        sealed class Look { public uint W; public string N; public uint Loc; public uint Cb; public int Pal; public double Sh; public int Cov; }

        sealed class Fit
        {
            public readonly List<Piece> Pieces = new List<Piece>();
            public readonly Dictionary<uint, WorldObject> Items = new Dictionary<uint, WorldObject>();
            public Creature Mannequin;
            public string Msg;
        }

        static readonly Dictionary<uint, Fit> s_Fits = new Dictionary<uint, Fit>();
        static readonly object s_Lock = new object();
        static List<Look> s_Catalog;
        static System.Threading.Tasks.Task s_Loading;
        static readonly List<Session> s_Waiting = new List<Session>();

        internal static void Register()
        {
            CommandManager.TryAddCommand(Handle, "dress", STAFF, CommandHandlerFlag.RequiresWorld,
                "Staff Dressing Room (the panel's own verbs).", "open | list <slot mask> <page> [words] | pick <wcid> | pal <i> <n> | shade <i> <0-1> | layer <i> <1|0|-1> | drop <i> | clear | worn | save <name> | load <name> | delete <name> | apply worn|new|npc <guid> | template <name> | close");
        }

        static bool Staff(Player p) => p?.Session != null && p.Session.AccessLevel >= STAFF;

        // ------------------------------------------------------------------ the Tailor

        /// <summary>Creature.ActOnUse postfix: staff get the panel; anyone else gets a polite no. A mannequin (owner M5
        /// 261103-0094: "same with these mannequins") opens it with the mannequin's outfit loaded, ready to change and
        /// wear or put back on it.</summary>
        internal static void TailorUse(Creature npc, WorldObject activator)
        {
            if (npc == null || activator is not Player p || p.Session == null) return;
            bool tailor = npc.WeenieClassId == TAILOR, mannequin = IsMannequin(npc.WeenieClassId);
            if (!tailor && !mannequin) return;
            if (!Staff(p) || !ModChannel.Capable(p.Session))
            {
                if (tailor) p.Session.Network.EnqueueSend(new GameEventTell(npc, "My fitting room is closed today.", p, ChatMessageType.Tell));
                return;
            }
            if (mannequin)
            {
                var f = FitOf(p);
                LoadWorn(npc, f);
                Open(p);
                f.Msg = $"Loaded the {npc.Name}'s outfit. Dress selected NPC puts changes back on it.";
                SendState(p, f);
                return;
            }
            Open(p);
        }

        /// <summary>Storage.CheckUseRequirements prefix for the room's Wardrobe Trunks (owner M5 261103-0093: "chests are
        /// locked"): staff storage with no house, so ACE's own check (house permission) would always refuse; Sentinel and
        /// up open them. Their contents keep like any Storage chest's (saved with the chest).</summary>
        internal static bool TrunkCheck(Storage s, WorldObject activator, ref ActivationResult result)
        {
            if (s == null || s.WeenieClassId != TRUNK) return true;
            if (activator is Player p && Staff(p)) result = new ActivationResult(true);
            else
            {
                if (activator is Player q) q.Session?.Network.EnqueueSend(new GameEventCommunicationTransientString(q.Session, $"The {s.Name} is for staff only."));
                result = new ActivationResult(false);
            }
            return false;
        }

        static void Open(Player p)
        {
            StartCatalog();
            var f = FitOf(p);
            f.Msg = null;
            if (f.Pieces.Count == 0) LoadWorn(p, f);   // the preview starts as the real look, never a bare body
            SendState(p, f, open: true);
            Preview(p, f);
        }

        static Fit FitOf(Player p)
        {
            lock (s_Lock)
            {
                if (!s_Fits.TryGetValue(p.Guid.Full, out var f)) s_Fits[p.Guid.Full] = f = new Fit();
                return f;
            }
        }

        // ------------------------------------------------------------------ the catalogue

        /// <summary>Every weenie in the world database with a ClothingBase in a clothing, armour or cloak slot, one row per
        /// (ClothingBase, slots): the same look under twenty names shows once, under its lowest weenie id.</summary>
        static void StartCatalog()
        {
            lock (s_Lock)
            {
                if (s_Catalog != null || s_Loading != null) return;
                s_Loading = System.Threading.Tasks.Task.Run(() =>
                {
                    var list = new List<Look>();
                    try
                    {
                        using var ctx = new ACE.Database.Models.World.WorldDbContext();
                        var cbs = ctx.WeeniePropertiesDID.Where(d => d.Type == (ushort)PropertyDataId.ClothingBase).Select(d => new { d.ObjectId, d.Value }).ToList()
                            .GroupBy(d => d.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var locs = ctx.WeeniePropertiesInt.Where(i => i.Type == (ushort)PropertyInt.ValidLocations).Select(i => new { i.ObjectId, i.Value }).ToList()
                            .Where(i => cbs.ContainsKey(i.ObjectId)).GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => (uint)g.First().Value);
                        var pals = ctx.WeeniePropertiesInt.Where(i => i.Type == (ushort)PropertyInt.PaletteTemplate).Select(i => new { i.ObjectId, i.Value }).ToList()
                            .Where(i => cbs.ContainsKey(i.ObjectId)).GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var covs = ctx.WeeniePropertiesInt.Where(i => i.Type == (ushort)PropertyInt.ClothingPriority).Select(i => new { i.ObjectId, i.Value }).ToList()
                            .Where(i => cbs.ContainsKey(i.ObjectId)).GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var shades = ctx.WeeniePropertiesFloat.Where(x => x.Type == (ushort)PropertyFloat.Shade).Select(x => new { x.ObjectId, x.Value }).ToList()
                            .Where(x => cbs.ContainsKey(x.ObjectId)).GroupBy(x => x.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var names = ctx.WeeniePropertiesString.Where(s => s.Type == (ushort)PropertyString.Name).Select(s => new { s.ObjectId, s.Value }).ToList()
                            .Where(s => cbs.ContainsKey(s.ObjectId)).GroupBy(s => s.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var seen = new HashSet<(uint, uint)>();
                        foreach (var kv in cbs.OrderBy(k => k.Key))
                        {
                            if (!locs.TryGetValue(kv.Key, out var loc) || (loc & LOOK_MASK) == 0 || (loc & ~LOOK_MASK & 0x7FFFFFFF) != 0) continue;
                            if (!seen.Add((kv.Value, loc))) continue;
                            list.Add(new Look
                            {
                                W = kv.Key, Cb = kv.Value, Loc = loc,
                                N = names.TryGetValue(kv.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "#" + kv.Key,
                                Pal = pals.TryGetValue(kv.Key, out var pt) ? pt : 0,
                                Sh = shades.TryGetValue(kv.Key, out var sh) ? sh : 0,
                                Cov = covs.TryGetValue(kv.Key, out var cv) ? cv : 0,
                            });
                        }
                        list.Sort((a, b) => string.Compare(a.N, b.N, StringComparison.OrdinalIgnoreCase));
                        Mod.Log.Info($"[RevivalGuard] DressingRoom: {list.Count} looks from {cbs.Count} weenies with a ClothingBase");
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DressingRoom: catalogue: {e.Message}"); }
                    List<Session> wake;
                    lock (s_Lock) { s_Catalog = list; s_Loading = null; wake = s_Waiting.ToList(); s_Waiting.Clear(); }
                    foreach (var s in wake) ModChannel.Send(s, "dress", "{\"ready\":1}");
                });
            }
        }

        static void SendList(Player p, uint slot, int page, string words)
        {
            List<Look> cat;
            lock (s_Lock)
            {
                cat = s_Catalog;
                if (cat == null) { if (!s_Waiting.Contains(p.Session)) s_Waiting.Add(p.Session); }
            }
            if (cat == null) { StartCatalog(); ModChannel.Send(p.Session, "dress", "{\"loading\":1}"); return; }
            var terms = (words ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = cat.Where(l => (slot == 0 || (l.Loc & slot) != 0) && terms.All(t => l.N.ToLowerInvariant().Contains(t) || l.W.ToString() == t)).ToList();
            int pages = Math.Max(1, (hits.Count + PAGE - 1) / PAGE);
            page = Math.Clamp(page, 0, pages - 1);
            var rows = hits.Skip(page * PAGE).Take(PAGE).Select(l => new object[] { l.W, l.N, l.Loc });
            ModChannel.Send(p.Session, "dress", JsonSerializer.Serialize(new { list = new { slot, page, pages, total = hits.Count, q = words ?? "", rows } }));
        }

        static Look LookOf(uint wcid)
        {
            List<Look> cat; lock (s_Lock) cat = s_Catalog;
            var hit = cat?.FirstOrDefault(l => l.W == wcid);
            if (hit != null) return hit;
            var w = ACE.Database.DatabaseManager.World.GetCachedWeenie(wcid);
            if (w == null) return null;
            w.PropertiesDID.TryGetValue(PropertyDataId.ClothingBase, out var cb);
            w.PropertiesInt.TryGetValue(PropertyInt.ValidLocations, out var loc);
            w.PropertiesInt.TryGetValue(PropertyInt.PaletteTemplate, out var pal);
            w.PropertiesFloat.TryGetValue(PropertyFloat.Shade, out var sh);
            w.PropertiesString.TryGetValue(PropertyString.Name, out var n);
            w.PropertiesInt.TryGetValue(PropertyInt.ClothingPriority, out var cov);
            if (((uint)loc & LOOK_MASK) == 0) return null;
            return new Look { W = wcid, N = n ?? "#" + wcid, Loc = (uint)loc, Cb = cb, Pal = pal, Sh = sh, Cov = cov };
        }

        /// <summary>The palette templates this look's ClothingTable has (the only ones it can show), with ACE's names.</summary>
        static List<object[]> PalettesOf(uint cb)
        {
            var r = new List<object[]>();
            if (cb == 0) return r;
            try
            {
                var t = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb);
                foreach (var k in t.ClothingSubPalEffects.Keys.OrderBy(k => k))
                    r.Add(new object[] { k, Enum.IsDefined(typeof(PaletteTemplate), (int)k) ? Spaced(((PaletteTemplate)(int)k).ToString()) : "Template " + k });
            }
            catch { }
            return r;
        }

        static string Spaced(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the outfit

        static Piece FromLook(Look l) => new Piece { W = l.W, N = l.N, Loc = l.Loc, Cov = l.Cov, Cb = l.Cb, Pal = l.Pal, Sh = l.Sh, Ly = 0 };

        /// <summary>A new piece takes the place of any piece it clashes with, by ACE's rule for putting it on
        /// (Creature.GetEquippedItems): clothing and armour clash where they cover the same body (ClothingPriority), so a
        /// shirt and breeches that both list the abdomen as a slot are worn together; anything else by its slots.</summary>
        static bool Clash(Piece a, Piece b)
        {
            const uint CLOAK = (uint)EquipMask.Cloak;   // a cloak is worn over everything; it clashes only with a cloak
            if (((a.Loc | b.Loc) & CLOAK) != 0) return (a.Loc & b.Loc & CLOAK) != 0;
            return a.Cov != 0 && b.Cov != 0 ? (a.Cov & b.Cov) != 0 : (a.Loc & b.Loc) != 0;
        }

        static void Put(Fit f, Piece x)
        {
            f.Pieces.RemoveAll(o => Clash(o, x));
            f.Pieces.Add(x);
            f.Pieces.Sort((a, b) => a.Loc.CompareTo(b.Loc));
        }

        static IEnumerable<WorldObject> WornLooks(Creature c) =>
            c.EquippedObjects.Values.Where(w => (((uint)(w.CurrentWieldedLocation ?? 0)) & LOOK_MASK) != 0).ToList();

        static void SendState(Player p, Fit f, bool open = false)
        {
            string target = "";
            uint tg = p.CurrentAppraisalTarget ?? 0;
            var two = tg != 0 ? p.CurrentLandblock?.GetObject(new ObjectGuid(tg)) : null;
            if (two is Creature c && c is not Player) target = c.Name;
            var st = new
            {
                open = open ? 1 : 0,
                pieces = f.Pieces.Select(x => new { w = x.W, n = x.N, loc = x.Loc, pal = x.Pal, sh = Math.Round(x.Sh, 3), ly = x.Ly, pals = PalettesOf(x.Cb) }),
                outfits = Outfits().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                msg = f.Msg ?? "",
                target,
            };
            ModChannel.Send(p.Session, "dress", JsonSerializer.Serialize(new { st }));
        }

        static void Handle(Session session, params string[] a)
        {
            var p = session?.Player;
            if (p == null || !Staff(p)) return;
            if (a == null || a.Length == 0) { Open(p); return; }
            var f = FitOf(p);
            string verb = a[0].ToLowerInvariant();
            string rest = string.Join(" ", a.Skip(1));
            int I(int k) => a.Length > k && int.TryParse(a[k], out var v) ? v : -1;
            Piece At(int k) { int i = I(k); return i >= 0 && i < f.Pieces.Count ? f.Pieces[i] : null; }
            f.Msg = null;
            bool look = true;
            try
            {
                switch (verb)
                {
                    case "open": Open(p); return;
                    case "list":
                        uint.TryParse(a.Length > 1 ? a[1] : "0", out var slot);
                        SendList(p, slot, I(2) < 0 ? 0 : I(2), string.Join(" ", a.Skip(3)));
                        return;
                    case "pick":
                        {
                            uint.TryParse(a.Length > 1 ? a[1] : "", out var w);
                            var l = LookOf(w);
                            if (l == null) f.Msg = "That is not something you can wear.";
                            else Put(f, FromLook(l));
                            break;
                        }
                    case "pal": { var x = At(1); if (x != null && a.Length > 2 && int.TryParse(a[2], out var v)) x.Pal = v; break; }
                    case "shade": { var x = At(1); if (x != null && a.Length > 2 && double.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) x.Sh = Math.Clamp(v, 0, 1); break; }
                    case "layer": { var x = At(1); if (x != null && a.Length > 2 && int.TryParse(a[2], out var v)) x.Ly = Math.Sign(v); break; }
                    case "drop": { var x = At(1); if (x != null) f.Pieces.Remove(x); break; }
                    case "clear": f.Pieces.Clear(); break;
                    case "worn": LoadWorn(p, f); break;
                    case "save":
                        {
                            var name = CleanName(rest);
                            if (name.Length == 0 || f.Pieces.Count == 0) { f.Msg = "Name the outfit, and put something in it."; break; }
                            var all = Outfits(); all[name] = f.Pieces.Select(Copy).ToList(); SaveOutfits(all);
                            f.Msg = $"Saved \"{name}\".";
                            look = false;
                            break;
                        }
                    case "load":
                        {
                            var name = CleanName(rest);
                            if (!Outfits().TryGetValue(name, out var list)) { f.Msg = "No outfit by that name."; break; }
                            f.Pieces.Clear(); foreach (var x in list) Put(f, Copy(x));
                            f.Msg = $"Loaded \"{name}\".";
                            break;
                        }
                    case "delete":
                        {
                            var name = CleanName(rest);
                            var all = Outfits();
                            f.Msg = all.Remove(name) ? $"Deleted \"{name}\"." : "No outfit by that name.";
                            SaveOutfits(all);
                            look = false;
                            break;
                        }
                    case "apply":
                        {
                            string how = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                            if (how == "worn") ApplyWorn(p, f);
                            else if (how == "new") ApplyNew(p, f);
                            else if (how == "npc") { uint.TryParse((a.Length > 2 ? a[2] : "").Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var g); ApplyNpc(p, f, g); look = false; }
                            else f.Msg = "Apply worn, new or npc.";
                            if (how == "worn" || how == "new") look = false;   // worn: the real look was just sent to all
                            break;
                        }
                    case "template": Template(p, f, CleanName(rest)); look = false; break;
                    case "close": Close(p); return;
                    default: f.Msg = "Unknown dressing room verb."; look = false; break;
                }
            }
            catch (Exception e)
            {
                f.Msg = "That did not work: " + e.Message;
                Mod.Log.Warn($"[RevivalGuard] DressingRoom: {verb}: {e}");
            }
            SendState(p, f);
            if (look) Preview(p, f);
        }

        static void LoadWorn(Creature c, Fit f)
        {
            f.Pieces.Clear();
            foreach (var w in WornLooks(c))
                Put(f, new Piece { W = w.WeenieClassId, N = w.Name, Loc = (uint)(w.ValidLocations ?? 0), Cov = (int)(w.ClothingPriority ?? 0), Cb = w.ClothingBase ?? 0, Pal = w.PaletteTemplate ?? 0, Sh = w.Shade ?? 0, Ly = w.TopLayerPriority == true ? 1 : w.TopLayerPriority == false ? -1 : 0 });
        }

        static Piece Copy(Piece x) => new Piece { W = x.W, N = x.N, Loc = x.Loc, Cov = x.Cov, Cb = x.Cb, Pal = x.Pal, Sh = x.Sh, Ly = x.Ly };

        static string CleanName(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in (s ?? "").Trim())
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '\'') sb.Append(c);
            var r = sb.ToString().Trim();
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }

        // ------------------------------------------------------------------ saved outfits

        static string Dir() => ModFolder.Sub("dressing-room");

        static Dictionary<string, List<Piece>> Outfits()
        {
            try
            {
                var file = Path.Combine(Dir() ?? "", "outfits.json");
                if (File.Exists(file))
                    return new Dictionary<string, List<Piece>>(JsonSerializer.Deserialize<Dictionary<string, List<Piece>>>(File.ReadAllText(file)) ?? new(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DressingRoom: outfits.json: {e.Message}"); }
            return new Dictionary<string, List<Piece>>(StringComparer.OrdinalIgnoreCase);
        }

        static void SaveOutfits(Dictionary<string, List<Piece>> all)
        {
            var dir = Dir(); if (dir == null) return;
            var file = Path.Combine(dir, "outfits.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, true);
        }

        // ------------------------------------------------------------------ the preview

        /// <summary>A real item for this piece, as the piece would make it (the cached one is re-coloured each time).</summary>
        static WorldObject Item(Fit f, Piece x)
        {
            if (!f.Items.TryGetValue(x.W, out var wo) || wo == null)
            {
                wo = WorldObjectFactory.CreateNewWorldObject(x.W);
                if (wo == null) return null;
                f.Items[x.W] = wo;
            }
            Colour(wo, x);
            return wo;
        }

        static void Colour(WorldObject wo, Piece x)
        {
            if (x.Cb != 0) wo.ClothingBase = x.Cb;   // a worn piece loaded with "worn" may be tailored already
            if (x.Pal > 0) wo.PaletteTemplate = x.Pal;
            wo.Shade = x.Sh;
            wo.TopLayerPriority = x.Ly > 0 ? true : x.Ly < 0 ? false : (bool?)null;
        }

        static Creature Stand(Fit f, Player p)
        {
            if (f.Mannequin == null)
                f.Mannequin = WorldObjectFactory.CreateNewWorldObject(MANNEQUIN) as Creature;
            var m = f.Mannequin;
            if (m == null) return null;
            // the body: what AddBaseModelData reads
            m.SetupTableId = p.SetupTableId;
            m.Heritage = p.Heritage; m.Gender = p.Gender; m.HairStyle = p.HairStyle;
            m.HeadObjectDID = p.HeadObjectDID; m.HairPaletteDID = p.HairPaletteDID; m.SkinPaletteDID = p.SkinPaletteDID;
            m.EyesPaletteDID = p.EyesPaletteDID; m.PaletteBaseDID = p.PaletteBaseDID;
            m.EyesTextureDID = p.EyesTextureDID; m.DefaultEyesTextureDID = p.DefaultEyesTextureDID;
            m.NoseTextureDID = p.NoseTextureDID; m.DefaultNoseTextureDID = p.DefaultNoseTextureDID;
            m.MouthTextureDID = p.MouthTextureDID; m.DefaultMouthTextureDID = p.DefaultMouthTextureDID;
            m.EquippedObjects.Clear();
            foreach (var x in f.Pieces)
            {
                var wo = Item(f, x);
                if (wo == null) continue;
                wo.CurrentWieldedLocation = (EquipMask)x.Loc;
                m.EquippedObjects[wo.Guid] = wo;
            }
            return m;
        }

        static void Preview(Player p, Fit f)
        {
            var m = Stand(f, p);
            if (m == null) { f.Msg = "The preview needs the Dressing Room mannequin (weenie " + MANNEQUIN + ")."; SendState(p, f); return; }
            var desc = m.CalculateObjDesc();
            // the one line of AddBaseModelData that only a Player gets: the hair texture, after the body-style textures
            int at = 0;
            if (p.HairStyle.HasValue && p.Heritage.HasValue && p.Gender.HasValue)
                try
                {
                    var sex = DatManager.PortalDat.CharGen.HeritageGroups[(uint)p.Heritage].Genders[(int)p.Gender];
                    if (sex.HairStyleList.Count > p.HairStyle.Value) at = sex.HairStyleList[p.HairStyle.Value].ObjDesc.TextureChanges.Count;
                }
                catch { }
            if (p.Character != null)
                desc.TextureChanges.Insert(Math.Min(at, desc.TextureChanges.Count), new ACE.Entity.Models.PropertiesTextureMap { PartIndex = 0x10, OldTexture = p.Character.DefaultHairTexture, NewTexture = p.Character.HairTexture });
            p.Session.Network.EnqueueSend(new LookOnly(p, desc));
        }

        /// <summary>The panel closed: the character's real look again, to that connection (the only one that saw the preview).</summary>
        static void Close(Player p)
        {
            // always, not only after a preview: a stale preview left on screen would pass for the real look
            p.Session.Network.EnqueueSend(new GameMessageObjDescEvent(p));
            ModChannel.Send(p.Session, "dress", "{\"closed\":1}");
        }

        /// <summary>ACE's ObjDescEvent for the player, with a look worked out elsewhere (WorldObject.SerializeModelData's layout).</summary>
        sealed class LookOnly : GameMessage
        {
            public LookOnly(Player p, ACE.Entity.ObjDesc d) : base(GameMessageOpcode.ObjDescEvent, GameMessageGroup.SmartboxQueue)
            {
                var w = Writer;
                w.WriteGuid(p.Guid);
                w.Write((byte)0x11);
                w.Write((byte)d.SubPalettes.Count);
                w.Write((byte)d.TextureChanges.Count);
                w.Write((byte)d.AnimPartChanges.Count);
                if (d.SubPalettes.Count > 0) w.WritePackedDwordOfKnownType(d.PaletteID, 0x4000000);
                foreach (var s in d.SubPalettes) { w.WritePackedDwordOfKnownType(s.SubPaletteId, 0x4000000); w.Write((byte)s.Offset); w.Write((byte)s.Length); }
                foreach (var t in d.TextureChanges) { w.Write(t.PartIndex); w.WritePackedDwordOfKnownType(t.OldTexture, 0x5000000); w.WritePackedDwordOfKnownType(t.NewTexture, 0x5000000); }
                foreach (var m in d.AnimPartChanges) { w.Write(m.Index); w.WritePackedDwordOfKnownType(m.AnimationId, 0x1000000); }
                w.Align();
                w.Write(p.Sequences.GetCurrentSequence(SequenceType.ObjectInstance));
                w.Write(p.Sequences.GetNextSequence(SequenceType.ObjectVisualDesc));
            }
        }

        // ------------------------------------------------------------------ apply

        /// <summary>Each piece onto the worn item in exactly its slots, as a tailoring kit would (name, look, colours, icon).</summary>
        static void ApplyWorn(Player p, Fit f)
        {
            var worn = WornLooks(p).ToList();
            int done = 0; var missed = new List<string>();
            foreach (var x in f.Pieces)
            {
                var target = worn.FirstOrDefault(w => (uint)(w.ValidLocations ?? 0) == x.Loc);
                var src = WorldObjectFactory.CreateNewWorldObject(x.W);
                if (target == null || src == null) { missed.Add(x.N); continue; }
                Colour(src, x);
                Tailoring.UpdateArmorProps(p, src, target);
                p.UpdateProperty(target, PropertyBool.TopLayerPriority, src.TopLayerPriority);
                p.EnqueueBroadcast(new GameMessageUpdateObject(target));
                target.SaveBiotaToDatabase();
                src.Destroy();
                done++;
            }
            p.EnqueueBroadcast(new GameMessageObjDescEvent(p));
            f.Msg = $"Tailored {done} worn piece{(done == 1 ? "" : "s")}." + (missed.Count > 0 ? " Nothing worn in the slots of: " + string.Join(", ", missed) + "." : "");
        }

        static void ApplyNew(Player p, Fit f)
        {
            int done = 0;
            foreach (var x in f.Pieces)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(x.W);
                if (wo == null) continue;
                Colour(wo, x);
                if (p.TryCreateInInventoryWithNetworking(wo)) done++;
                else wo.Destroy();
            }
            f.Msg = $"Made {done} of {f.Pieces.Count} piece{(f.Pieces.Count == 1 ? "" : "s")} in your pack."
                + (done < f.Pieces.Count ? " The rest did not fit: your pack is full or you carry too much." : "");
        }

        /// <summary>The selected creature (never a player) takes off its clothing and armour and wears the outfit as real
        /// items. Not saved: it is a look for staff content work; "template" is the saved form.</summary>
        static void ApplyNpc(Player p, Fit f, uint guid)
        {
            var c = guid != 0 ? p.CurrentLandblock?.GetObject(new ObjectGuid(guid)) as Creature : null;
            if (c == null || c is Player) { f.Msg = "Select a creature or NPC first (not a player)."; return; }
            foreach (var w in WornLooks(c))
                if (c.TryDequipObject(w.Guid, out var off, out _)) off.Destroy();
            int done = 0;
            foreach (var x in f.Pieces)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(x.W);
                if (wo == null) continue;
                Colour(wo, x);
                if (c.TryEquipObject(wo, (EquipMask)x.Loc)) done++;
                else wo.Destroy();
            }
            c.EnqueueBroadcast(new GameMessageObjDescEvent(c));
            f.Msg = $"Dressed {c.Name} in {done} piece{(done == 1 ? "" : "s")}. A respawn undresses it; save a template to keep it.";
        }

        /// <summary>dressing-room/&lt;name&gt;.sql: the outfit as create-list rows (destination Wield, palette, shade) for a
        /// weenie id the content author sets, the way retail NPCs are dressed.</summary>
        static void Template(Player p, Fit f, string name)
        {
            if (name.Length == 0 || f.Pieces.Count == 0) { f.Msg = "Name the template, and put something in the outfit."; return; }
            var dir = Dir(); if (dir == null) { f.Msg = "No folder for templates."; return; }
            var file = Path.Combine(dir, name.Replace(' ', '-').Replace("'", "") + ".sql");
            var sb = new StringBuilder();
            sb.AppendLine($"-- Dressing Room outfit \"{name}\", saved by {p.Name} {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.");
            sb.AppendLine("-- Set @wcid to the NPC or monster weenie. Create-list rows dress it at each spawn (destination 2 = Wield).");
            sb.AppendLine("-- Create lists carry palette and shade only: a piece with a layering choice notes it below.");
            sb.AppendLine("SET @wcid = 0;");
            sb.AppendLine("INSERT INTO ace_world.weenie_properties_create_list (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond) VALUES");
            for (int i = 0; i < f.Pieces.Count; i++)
            {
                var x = f.Pieces[i];
                var own = LookOf(x.W);
                string note = x.N.Replace("\n", " ").Replace("--", "-")
                    + (x.Ly != 0 ? (x.Ly > 0 ? ", layered on top" : ", layered underneath") : "")
                    + (own != null && x.Cb != 0 && own.Cb != x.Cb ? $", tailored: its look is ClothingBase 0x{x.Cb:X8}, not this weenie's own" : "");
                sb.AppendLine(FormattableString.Invariant($"  (@wcid, 2, {x.W}, 1, {x.Pal}, {x.Sh:0.####}, 0){(i < f.Pieces.Count - 1 ? "," : ";")}   -- {note}"));
            }
            File.WriteAllText(file, sb.ToString());
            f.Msg = $"Template saved as {Path.GetFileName(file)} in the shard's dressing-room folder.";
        }

        internal static void Forget(Player p) { lock (s_Lock) s_Fits.Remove(p.Guid.Full); }
    }

    [HarmonyPatch]
    static class DressingRoomTailor
    {
        // Creature DECLARES its ActOnUse override (see NetherRiftTrainer); the wcid filter is inside.
        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.ActOnUse), new[] { typeof(WorldObject) });
            if (m == null) Mod.Log.Error("[RevivalGuard] DressingRoom: Creature.ActOnUse(WorldObject) not found; the Tailor is silent");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Postfix(Creature __instance, WorldObject worldObject)
        {
            try { DressingRoom.TailorUse(__instance, worldObject); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DressingRoom: tailor: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Storage), nameof(Storage.CheckUseRequirements))]
    static class DressingRoomTrunk
    {
        static bool Prefix(Storage __instance, WorldObject activator, ref ActivationResult __result)
        {
            try { return DressingRoom.TrunkCheck(__instance, activator, ref __result); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DressingRoom: trunk: {e.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.LogOut_Inner))]
    static class DressingRoomLogout
    {
        static void Prefix(Player __instance) { try { DressingRoom.Forget(__instance); } catch { } }
    }
}
