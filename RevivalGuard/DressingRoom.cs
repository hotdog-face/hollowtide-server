using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
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
        const uint OURS = 900000;   // our own weenies (masks, costume pieces, suits) start here
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
            public uint Ic { get; set; }      // its icon, ItemType (the icon's underlay) and UiEffects, for the visual panel
            public int It { get; set; }
            public int Fx { get; set; }
            public uint Rd { get; set; }      // reduced to these slots (ValidLocations), as a reduction tool would; 0 = full
            // A worn piece's own look rows when they are not its weenie's (a mask tailored onto a helm carries the mask's head
            // part): anim parts (index, model), texture maps (part, old, new), palettes (id, offset, length). Null = the weenie's.
            public List<uint[]> Ap { get; set; }
            public List<uint[]> Tm { get; set; }
            public List<uint[]> Pl { get; set; }
        }

        sealed class Look { public uint W; public string N; public uint Loc; public uint Cb; public int Pal; public double Sh; public int Cov; public uint Ic; public int It; public int Fx; }

        sealed class Fit
        {
            public readonly List<Piece> Pieces = new List<Piece>();
            public readonly Dictionary<uint, WorldObject> Items = new Dictionary<uint, WorldObject>();
            public Creature Mannequin;
            public string Msg;
            public bool Open;   // the panel is up (Open .. Close): a Tailor use while it is up keeps the outfit being built
            // Undo / Redo (owner ticket 261108-0005): the outfit before each change, newest last, HISTORY deep. A shade
            // drag on one piece is one step (LastOp).
            public readonly List<List<Piece>> Undo = new List<List<Piece>>(), Redo = new List<List<Piece>>();
            public string LastOp;
            public DateTime LastOpAt;
        }
        const int HISTORY = 50;

        /// <summary>Everything that changes how the outfit looks, for "did this verb change it".</summary>
        static string Key(List<Piece> l) => string.Join(";", l.Select(x => FormattableString.Invariant($"{x.W},{x.Loc},{x.Rd},{x.Cov},{x.Cb},{x.Pal},{x.Sh:0.###},{x.Ly},{Rows(x.Ap)}/{Rows(x.Tm)}/{Rows(x.Pl)}")));
        static List<Piece> Snap(Fit f) => f.Pieces.Select(Copy).ToList();
        static void Restore(Fit f, List<Piece> to) { f.Pieces.Clear(); f.Pieces.AddRange(to.Select(Copy)); }
        static void Push(List<List<Piece>> stack, List<Piece> snap) { stack.Add(snap); if (stack.Count > HISTORY) stack.RemoveAt(0); }

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
            Open(p, fromGear: true);
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

        /// <summary>fromGear (the Tailor, owner ticket 261108-0001 "doesnt match how i look in the 3d world. pants are
        /// different"): a closed panel opens on the gear worn NOW. The outfit is kept per character until logout, so a
        /// reopen showed the last session's outfit (or the mannequin's) after the gear had changed. A typed "@dress" and a
        /// Tailor use while the panel is up keep the outfit being built; saved outfits are what persists.</summary>
        static void Open(Player p, bool fromGear = false)
        {
            StartCatalog();
            var f = FitOf(p);
            f.Msg = null;
            if ((fromGear && !f.Open) || f.Pieces.Count == 0) { LoadWorn(p, f); f.Undo.Clear(); f.Redo.Clear(); f.LastOp = null; }   // the preview starts as the real look, never a bare body
            f.Open = true;
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
                        var icons = ctx.WeeniePropertiesDID.Where(d => d.Type == (ushort)PropertyDataId.Icon).Select(d => new { d.ObjectId, d.Value }).ToList()
                            .Where(d => cbs.ContainsKey(d.ObjectId)).GroupBy(d => d.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var types = ctx.WeeniePropertiesInt.Where(i => i.Type == (ushort)PropertyInt.ItemType).Select(i => new { i.ObjectId, i.Value }).ToList()
                            .Where(i => cbs.ContainsKey(i.ObjectId)).GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var fxs = ctx.WeeniePropertiesInt.Where(i => i.Type == (ushort)PropertyInt.UiEffects).Select(i => new { i.ObjectId, i.Value }).ToList()
                            .Where(i => cbs.ContainsKey(i.ObjectId)).GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        var names = ctx.WeeniePropertiesString.Where(s => s.Type == (ushort)PropertyString.Name).Select(s => new { s.ObjectId, s.Value }).ToList()
                            .Where(s => cbs.ContainsKey(s.ObjectId)).GroupBy(s => s.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        // Our own wearables (owner 2026-11-07: "not just masks but all new tailor items"): a 900000+ weenie's look
                        // can come from its own anim-part, texture and palette rows (the creature-head masks share one
                        // ClothingBase and differ only by an anim-part row), or from its setup with no ClothingBase at all.
                        var aps = ctx.WeeniePropertiesAnimPart.Select(x => new { x.ObjectId, x.Index, x.AnimationId }).ToList()
                            .GroupBy(x => x.ObjectId).ToDictionary(g => g.Key, g => string.Join(",", g.OrderBy(x => x.Index).Select(x => $"{x.Index}:{x.AnimationId:X}")));
                        var tms = ctx.WeeniePropertiesTextureMap.Select(x => new { x.ObjectId, x.Index, x.OldId, x.NewId }).ToList()
                            .GroupBy(x => x.ObjectId).ToDictionary(g => g.Key, g => string.Join(",", g.OrderBy(x => x.Index).ThenBy(x => x.OldId).Select(x => $"{x.Index}:{x.OldId:X}>{x.NewId:X}")));
                        var pls = ctx.WeeniePropertiesPalette.Select(x => new { x.ObjectId, x.SubPaletteId, x.Offset, x.Length }).ToList()
                            .GroupBy(x => x.ObjectId).ToDictionary(g => g.Key, g => string.Join(",", g.OrderBy(x => x.Offset).Select(x => $"{x.SubPaletteId:X}@{x.Offset}+{x.Length}")));
                        var setups = ctx.WeeniePropertiesDID.Where(d => d.Type == (ushort)PropertyDataId.Setup).Select(d => new { d.ObjectId, d.Value }).ToList()
                            .GroupBy(d => d.ObjectId).ToDictionary(g => g.Key, g => g.First().Value);
                        // ours with no ClothingBase but parts of their own: worn as their setup (ACE's AddSetupAsClothingBase)
                        var own = ctx.WeeniePropertiesInt.Where(i => i.Type == (ushort)PropertyInt.ValidLocations && i.ObjectId >= OURS).Select(i => new { i.ObjectId, i.Value }).ToList();
                        foreach (var o in own)
                            if (!cbs.ContainsKey(o.ObjectId) && aps.ContainsKey(o.ObjectId) && ((uint)o.Value & LOOK_MASK) != 0) cbs[o.ObjectId] = 0;
                        var more = ctx.WeeniePropertiesInt.Where(i => i.ObjectId >= OURS && (i.Type == (ushort)PropertyInt.ValidLocations || i.Type == (ushort)PropertyInt.PaletteTemplate || i.Type == (ushort)PropertyInt.ClothingPriority || i.Type == (ushort)PropertyInt.ItemType || i.Type == (ushort)PropertyInt.UiEffects)).ToList();
                        foreach (var m in more.Where(m => cbs.ContainsKey(m.ObjectId) && cbs[m.ObjectId] == 0))
                        {
                            if (m.Type == (ushort)PropertyInt.ValidLocations) locs[m.ObjectId] = (uint)m.Value;
                            else if (m.Type == (ushort)PropertyInt.PaletteTemplate) pals[m.ObjectId] = m.Value;
                            else if (m.Type == (ushort)PropertyInt.ClothingPriority) covs[m.ObjectId] = m.Value;
                            else if (m.Type == (ushort)PropertyInt.ItemType) types[m.ObjectId] = m.Value;
                            else fxs[m.ObjectId] = m.Value;
                        }
                        foreach (var d in ctx.WeeniePropertiesDID.Where(d => d.ObjectId >= OURS && d.Type == (ushort)PropertyDataId.Icon).ToList())
                            if (cbs.TryGetValue(d.ObjectId, out var c0) && c0 == 0) icons[d.ObjectId] = d.Value;
                        foreach (var n0 in ctx.WeeniePropertiesString.Where(x => x.ObjectId >= OURS && x.Type == (ushort)PropertyString.Name).ToList())
                            if (cbs.TryGetValue(n0.ObjectId, out var c1) && c1 == 0) names[n0.ObjectId] = n0.Value;
                        // one row per LOOK, not per ClothingBase: the same retail look under twenty names shows once (under its
                        // lowest weenie id), but pieces that differ by setup, own parts, textures, palettes, colour template or
                        // icon are different looks and each shows
                        var seen = new HashSet<string>();
                        foreach (var kv in cbs.OrderBy(k => k.Key))
                        {
                            if (!locs.TryGetValue(kv.Key, out var loc) || (loc & LOOK_MASK) == 0 || (loc & ~LOOK_MASK & 0x7FFFFFFF) != 0) continue;
                            string sig = string.Join("|", kv.Value, loc, pals.TryGetValue(kv.Key, out var p0) ? p0 : 0, icons.TryGetValue(kv.Key, out var i0) ? i0 : 0,
                                setups.TryGetValue(kv.Key, out var s0) ? s0 : 0, aps.TryGetValue(kv.Key, out var a0) ? a0 : "",
                                tms.TryGetValue(kv.Key, out var t0) ? t0 : "", pls.TryGetValue(kv.Key, out var q0) ? q0 : "");
                            // ours always show under their own names (masks, costume pieces, showcase and owner suits), even
                            // when one looks exactly like the retail piece it was cloned from
                            if (!seen.Add(kv.Key >= OURS ? "#" + kv.Key : sig)) continue;
                            list.Add(new Look
                            {
                                W = kv.Key, Cb = kv.Value, Loc = loc,
                                N = names.TryGetValue(kv.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "#" + kv.Key,
                                Pal = pals.TryGetValue(kv.Key, out var pt) ? pt : 0,
                                Sh = shades.TryGetValue(kv.Key, out var sh) ? sh : 0,
                                Cov = covs.TryGetValue(kv.Key, out var cv) ? cv : 0,
                                Ic = icons.TryGetValue(kv.Key, out var ic) ? ic : 0,
                                It = types.TryGetValue(kv.Key, out var it) ? it : 0,
                                Fx = fxs.TryGetValue(kv.Key, out var fx) ? fx : 0,
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

        static void SendList(Player p, uint slot, int page, string words, int per = PAGE)
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
            per = Math.Clamp(per, 8, 80);
            int pages = Math.Max(1, (hits.Count + per - 1) / per);
            page = Math.Clamp(page, 0, pages - 1);
            var rows = hits.Skip(page * per).Take(per).Select(l => new object[] { l.W, l.N, l.Loc, l.Ic, l.It, l.Fx });
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
            w.PropertiesDID.TryGetValue(PropertyDataId.Icon, out var ic);
            w.PropertiesInt.TryGetValue(PropertyInt.ItemType, out var it);
            w.PropertiesInt.TryGetValue(PropertyInt.UiEffects, out var fx);
            if (((uint)loc & LOOK_MASK) == 0) return null;
            return new Look { W = wcid, N = n ?? "#" + wcid, Loc = (uint)loc, Cb = cb, Pal = pal, Sh = sh, Cov = cov, Ic = ic, It = it, Fx = fx };
        }

        /// <summary>The palette templates this look's ClothingTable has (the only ones it can show), with ACE's names and a
        /// swatch: the colour that template gives the look's first sub-palette at this shade, read the way CalculateObjDesc
        /// picks it (the PaletteSet's palette for the shade), at the middle of that sub-palette's colour range.</summary>
        static List<object[]> PalettesOf(uint cb, double shade = 0)
        {
            var r = new List<object[]>();
            if (cb == 0) return r;
            try
            {
                var t = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb);
                // The swatch shows the sub-palette that changes from template to template. Many armours list a trim or
                // leather range first that every template shares, and that range made all their swatches the same tan.
                int best = 0, bestDistinct = -1; long bestColors = -1;
                int most = t.ClothingSubPalEffects.Values.Select(e => e.CloSubPalettes.Count).DefaultIfEmpty(0).Max();
                for (int j = 0; j < most; j++)
                {
                    var subs = t.ClothingSubPalEffects.Values.Where(e => e.CloSubPalettes.Count > j).Select(e => e.CloSubPalettes[j]).ToList();
                    int distinct = subs.Select(c => c.PaletteSet).Distinct().Count();
                    long colors = subs.Count == 0 ? 0 : subs[0].Ranges.Sum(g => (long)g.NumColors);
                    if (distinct > bestDistinct || (distinct == bestDistinct && colors > bestColors)) { best = j; bestDistinct = distinct; bestColors = colors; }
                }
                foreach (var k in t.ClothingSubPalEffects.Keys.OrderBy(k => k))
                    r.Add(new object[] { k, Enum.IsDefined(typeof(PaletteTemplate), (int)k) ? Spaced(((PaletteTemplate)(int)k).ToString()) : "Template " + k,
                                         Swatch(t.ClothingSubPalEffects[k], shade, best) });
            }
            catch { }
            return r;
        }

        static string Swatch(CloSubPalEffect e, double shade, int pick = 0)
        {
            try
            {
                var sub = pick < e.CloSubPalettes.Count && e.CloSubPalettes[pick].Ranges.Count > 0
                    ? e.CloSubPalettes[pick] : e.CloSubPalettes.FirstOrDefault(c => c.Ranges.Count > 0);
                if (sub == null) return "";
                var set = DatManager.PortalDat.ReadFromDat<PaletteSet>(sub.PaletteSet);
                var pal = DatManager.PortalDat.ReadFromDat<Palette>(set.GetPaletteID(shade));
                var rg = sub.Ranges[0];
                int i = (int)Math.Min(rg.Offset + rg.NumColors / 2, (uint)Math.Max(0, pal.Colors.Count - 1));
                return pal.Colors.Count == 0 ? "" : "#" + (pal.Colors[i] & 0xFFFFFF).ToString("X6");
            }
            catch { return ""; }
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

        // ------------------------------------------------------------------ reduction (retail's three tools)

        /// <summary>The slots a piece is worn in: its reduced slots, else its own.</summary>
        static uint ELoc(Piece x) => x.Rd != 0 ? x.Rd : x.Loc;
        /// <summary>The body it covers (ClothingPriority): what Tailoring.TailorReduceArmor sets for the reduced slot.</summary>
        static int ECov(Piece x) => x.Rd != 0 ? (int)CoverOf(x.Rd) : x.Cov;

        static CoverageMask CoverOf(uint loc) => (EquipMask)loc switch
        {
            EquipMask.ChestArmor => CoverageMask.OuterwearChest,
            EquipMask.UpperArmArmor => CoverageMask.OuterwearUpperArms,
            EquipMask.LowerArmArmor => CoverageMask.OuterwearLowerArms,
            EquipMask.AbdomenArmor => CoverageMask.OuterwearAbdomen,
            EquipMask.UpperLegArmor => CoverageMask.OuterwearUpperLegs,
            EquipMask.LowerLegArmor => CoverageMask.OuterwearLowerLegs,
            EquipMask.FootWear => CoverageMask.Feet,
            _ => CoverageMask.Unknown,
        };

        const uint REDUCIBLE = (uint)(EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor | EquipMask.LowerArmArmor
                                      | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor | EquipMask.FootWear);

        /// <summary>What the Main, Lower and Middle reduction tools would leave of a piece in these slots, exactly as
        /// ACE's Tailoring.TailorReduceArmor picks (docs/findings/tailoring-reduction-2026-11-06.md). A piece in one
        /// slot has no choice (TailoringParity refuses the tools on it). Each choice: (slots, button, tooltip).</summary>
        static List<(uint Loc, string Short, string Long)> ReduceChoices(uint loc)
        {
            var r = new List<(uint, string, string)>();
            if (System.Numerics.BitOperations.PopCount(loc & REDUCIBLE) < 2) return r;
            // full robes and guises (every body armour slot) cannot be tailored or reduced (RobeTailoring, retail T9):
            // a hooded robe keeps its head, so it takes a helm off
            const uint FULL = (uint)(EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor | EquipMask.LowerArmArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor);
            if ((loc & FULL) == FULL) return r;
            bool Has(EquipMask m) => ((EquipMask)loc & m) == m;
            // Main: chest, else upper arms, else abdomen
            if (Has(EquipMask.ChestArmor)) r.Add(((uint)EquipMask.ChestArmor, "Chest", "Chest only (an Armor Main Reduction Tool)"));
            else if (Has(EquipMask.UpperArmArmor)) r.Add(((uint)EquipMask.UpperArmArmor, "Upper", "Upper arms only (an Armor Main Reduction Tool)"));
            else if (Has(EquipMask.AbdomenArmor)) r.Add(((uint)EquipMask.AbdomenArmor, "Girth", "Abdomen only (an Armor Main Reduction Tool)"));
            // Middle: upper legs
            if (Has(EquipMask.UpperLegArmor)) r.Add(((uint)EquipMask.UpperLegArmor, "Upper", "Upper legs only (an Armor Middle Reduction Tool)"));
            // Lower: never a chest piece; lower arms, else lower legs, else feet
            if (!Has(EquipMask.ChestArmor))
            {
                if (Has(EquipMask.UpperArmArmor)) r.Add(((uint)EquipMask.LowerArmArmor, "Lower", "Lower arms only (an Armor Lower Reduction Tool)"));
                else if (Has(EquipMask.UpperLegArmor)) r.Add(((uint)EquipMask.LowerLegArmor, "Lower", "Lower legs only (an Armor Lower Reduction Tool)"));
                else if (Has(EquipMask.LowerLegArmor | EquipMask.FootWear)) r.Add(((uint)EquipMask.FootWear, "Feet", "Feet only (an Armor Lower Reduction Tool)"));
            }
            // a choice that is the whole piece is no choice (two slots where a tool keeps both cannot happen, but be safe)
            r.RemoveAll(c => c.Item1 == (loc & REDUCIBLE));
            return r;
        }

        static Piece FromLook(Look l) => new Piece { W = l.W, N = l.N, Loc = l.Loc, Cov = l.Cov, Cb = l.Cb, Pal = l.Pal, Sh = l.Sh, Ly = 0 };

        /// <summary>A new piece takes the place of any piece it clashes with, by ACE's rule for putting it on
        /// (Creature.GetEquippedItems): clothing and armour clash where they cover the same body (ClothingPriority), so a
        /// shirt and breeches that both list the abdomen as a slot are worn together; anything else by its slots.</summary>
        static bool Clash(Piece a, Piece b)
        {
            const uint CLOAK = (uint)EquipMask.Cloak;   // a cloak is worn over everything; it clashes only with a cloak
            uint al = ELoc(a), bl = ELoc(b); int ac = ECov(a), bc = ECov(b);
            if (((al | bl) & CLOAK) != 0) return (al & bl & CLOAK) != 0;
            return ac != 0 && bc != 0 ? (ac & bc) != 0 : (al & bl) != 0;
        }

        static void Put(Fit f, Piece x)
        {
            f.Pieces.RemoveAll(o => Clash(o, x));
            f.Pieces.Add(x);
            f.Pieces.Sort((a, b) => ELoc(a).CompareTo(ELoc(b)));
        }

        /// <summary>Owner ticket 261108-0006 ("changing chest armor that covers head wiped out ... the helm i chose and wanted
        /// to build around"). Retail wears one piece per body part: a piece that covers the head (a hooded robe, the
        /// Doomshark Hide Coat, the Cowl of the Sand) cannot be worn with a helm, and putting it on takes the helm off. But
        /// retail's reduction tools cut a multi-slot ARMOUR piece down to one slot (Main: chest), and a coat cut to its
        /// chest leaves the head free. So: when the new piece would take off pieces already chosen and one of its
        /// reductions still covers the slot being dressed (sel; any, from an older panel) and takes off fewer, wear it
        /// reduced and keep them (Covers > Full wears the whole piece). Whatever still comes off is named, and Undo puts
        /// it back.</summary>
        static void Pick(Fit f, Piece x, uint sel)
        {
            // a piece in the slot being dressed is simply replaced; the others are the chosen pieces this would wipe
            bool Replaced(Piece o) => sel != 0 ? (ELoc(o) & sel) != 0 : ELoc(o) == ELoc(x);
            List<Piece> Lost(Piece y) => f.Pieces.Where(o => Clash(o, y) && !Replaced(o)).ToList();
            var lost = Lost(x);
            var kept = new List<Piece>();
            if (lost.Count > 0)
            {
                Piece best = null; var bestLost = lost;
                foreach (var c in ReduceChoices(x.Loc))
                {
                    if (sel != 0 && (c.Loc & sel) == 0) continue;
                    var y = Copy(x); y.Rd = c.Loc;
                    var l2 = Lost(y);
                    if (l2.Count < bestLost.Count) { best = y; bestLost = l2; }
                }
                if (best != null) { kept = lost.Where(o => !bestLost.Contains(o)).ToList(); x = best; lost = bestLost; }
            }
            Put(f, x);
            string Names(List<Piece> l) => l.Count <= 2 ? string.Join(" and ", l.Select(o => o.N)) : $"{l[0].N}, {l[1].N} and {l.Count - 2} more";
            var msg = new List<string>();
            if (kept.Count > 0) msg.Add($"Kept your {Names(kept)}: the {x.N} is reduced to {SlotName(x.Rd)} (Covers, Full for all of it).");
            if (lost.Count > 0) msg.Add($"Took off the {Names(lost)}: the {x.N} covers {BodyOf(lost, x)} too. Undo puts {(lost.Count == 1 ? "it" : "them")} back.");
            if (msg.Count > 0) f.Msg = string.Join(" ", msg);
        }

        /// <summary>The body part a lost piece shares with the new one, in words (the first of them).</summary>
        static string BodyOf(List<Piece> lost, Piece x)
        {
            int both = 0; foreach (var o in lost) both |= ECov(o) & ECov(x);
            var c = (CoverageMask)both;
            if (c.HasFlag(CoverageMask.Head)) return "the head";
            if (c.HasFlag(CoverageMask.Hands)) return "the hands";
            if (c.HasFlag(CoverageMask.Feet)) return "the feet";
            if ((both & (int)(CoverageMask.OuterwearChest | CoverageMask.UnderwearChest)) != 0) return "the chest";
            if ((both & (int)(CoverageMask.OuterwearAbdomen | CoverageMask.UnderwearAbdomen)) != 0) return "the abdomen";
            if ((both & (int)(CoverageMask.OuterwearUpperArms | CoverageMask.OuterwearLowerArms | CoverageMask.UnderwearUpperArms | CoverageMask.UnderwearLowerArms)) != 0) return "the arms";
            if ((both & (int)(CoverageMask.OuterwearUpperLegs | CoverageMask.OuterwearLowerLegs | CoverageMask.UnderwearUpperLegs | CoverageMask.UnderwearLowerLegs)) != 0) return "the legs";
            return "the same slots";
        }

        static string SlotName(uint loc) => (EquipMask)loc switch
        {
            EquipMask.ChestArmor => "the chest",
            EquipMask.UpperArmArmor => "the upper arms",
            EquipMask.LowerArmArmor => "the lower arms",
            EquipMask.AbdomenArmor => "the abdomen",
            EquipMask.UpperLegArmor => "the upper legs",
            EquipMask.LowerLegArmor => "the lower legs",
            EquipMask.FootWear => "the feet",
            _ => "one slot",
        };

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
                pieces = f.Pieces.Select(x =>
                {
                    Iconify(x);
                    return new
                    {
                        w = x.W, n = x.N, loc = ELoc(x), full = x.Loc, rd = x.Rd, pal = x.Pal, sh = Math.Round(x.Sh, 3), ly = x.Ly, ic = IconFor(x), it = x.It, fx = x.Fx,
                        pals = PalettesOf(x.Cb, x.Sh),
                        cover = ReduceChoices(x.Loc).Select(c => new object[] { c.Loc, c.Short, c.Long }),
                    };
                }),
                // each saved outfit as a tile: its name and the icon of its chest piece (else its first)
                outfits = Outfits().OrderBy(k => OutfitOrder(k.Key)).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k =>
                {
                    var face = k.Value.FirstOrDefault(x => (x.Loc & (uint)EquipMask.ChestArmor) != 0) ?? k.Value.FirstOrDefault(x => (x.Loc & (uint)EquipMask.ChestWear) != 0) ?? k.Value.FirstOrDefault();
                    if (face != null) Iconify(face);
                    return new object[] { k.Key, face?.Ic ?? 0, face?.It ?? 0 };
                }),
                msg = f.Msg ?? "",
                target,
                undo = f.Undo.Count,
                redo = f.Redo.Count,
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
            var before = Snap(f);
            string beforeKey = Key(before);
            try
            {
                switch (verb)
                {
                    case "open": Open(p); return;
                    case "list":
                        // list <slot mask> <page> [per <n>] [words]: the visual panel asks for its grid's size, no words
                        uint.TryParse(a.Length > 1 ? a[1] : "0", out var slot);
                        int per = PAGE, skip = 3;
                        if (a.Length > 4 && a[3] == "per" && int.TryParse(a[4], out var pn)) { per = pn; skip = 5; }
                        SendList(p, slot, I(2) < 0 ? 0 : I(2), string.Join(" ", a.Skip(skip)), per);
                        return;
                    case "pick":
                        {
                            // pick <wcid> [slot mask]: the slot the panel is dressing (a newer panel sends it)
                            uint.TryParse(a.Length > 1 ? a[1] : "", out var w);
                            uint.TryParse(a.Length > 2 ? a[2] : "", out var sel);
                            var l = LookOf(w);
                            if (l == null) f.Msg = "That is not something you can wear.";
                            else Pick(f, FromLook(l), sel);
                            break;
                        }
                    case "undo":
                    case "redo":
                        {
                            var from = verb == "undo" ? f.Undo : f.Redo;
                            var to = verb == "undo" ? f.Redo : f.Undo;
                            if (from.Count == 0) { f.Msg = verb == "undo" ? "Nothing to undo." : "Nothing to redo."; look = false; break; }
                            Push(to, Snap(f));
                            Restore(f, from[from.Count - 1]); from.RemoveAt(from.Count - 1);
                            f.LastOp = null;
                            f.Msg = verb == "undo" ? "Undone." : "Redone.";
                            break;
                        }
                    case "pal": { var x = At(1); if (x != null && a.Length > 2 && int.TryParse(a[2], out var v)) x.Pal = v; break; }
                    case "shade": { var x = At(1); if (x != null && a.Length > 2 && double.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) x.Sh = Math.Clamp(v, 0, 1); break; }
                    case "layer": { var x = At(1); if (x != null && a.Length > 2 && int.TryParse(a[2], out var v)) x.Ly = Math.Sign(v); break; }
                    case "reduce":
                        {
                            // reduce <piece> <slots>: 0 = the whole piece again
                            var x = At(1);
                            if (x == null || a.Length < 3 || !uint.TryParse(a[2], out var to)) break;
                            if (to != 0 && !ReduceChoices(x.Loc).Any(c => c.Loc == to)) { f.Msg = "That piece cannot be reduced to those slots."; break; }
                            x.Rd = to;
                            f.Pieces.Remove(x); Put(f, x);   // the whole piece again takes back slots another piece may hold
                            break;
                        }
                    case "drop": { var x = At(1); if (x != null) f.Pieces.Remove(x); break; }
                    case "clear": f.Pieces.Clear(); break;
                    case "worn": LoadWorn(p, f); break;
                    case "save":
                        {
                            if (f.Pieces.Count == 0) { f.Msg = "Put something in the outfit first."; break; }
                            var all = Outfits();
                            var name = CleanName(rest);
                            if (name.Length == 0) { int k = 1; while (all.ContainsKey("Outfit " + k)) k++; name = "Outfit " + k; }   // the visual panel types nothing
                            all[name] = f.Pieces.Select(Copy).ToList(); SaveOutfits(all);
                            Template(p, f, name);   // the content template beside it (dressing-room/<name>.sql)
                            f.Msg = $"Saved as {name}, with its template.";
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
            // history: any verb that changed the outfit is one step (undo/redo keep their own stacks); a run of shade moves
            // on the same piece is one step, so a drag is undone in one go
            if (verb != "undo" && verb != "redo" && Key(f.Pieces) != beforeKey)
            {
                string op = verb == "shade" ? "shade " + (a.Length > 1 ? a[1] : "") : verb;
                // one drag sends a shade every 0.12 s; a pause of 1.5 s starts a new step
                if (!(verb == "shade" && f.LastOp == op && (DateTime.UtcNow - f.LastOpAt).TotalSeconds < 1.5)) Push(f.Undo, before);
                f.Redo.Clear();
                f.LastOp = op; f.LastOpAt = DateTime.UtcNow;
            }
            SendState(p, f);
            if (look) Preview(p, f);
        }

        static void LoadWorn(Creature c, Fit f)
        {
            f.Pieces.Clear();
            foreach (var w in WornLooks(c))
            {
                var x = new Piece { W = w.WeenieClassId, N = w.Name, Loc = (uint)(w.ValidLocations ?? 0), Cov = (int)(w.ClothingPriority ?? 0), Cb = w.ClothingBase ?? 0, Pal = w.PaletteTemplate ?? 0, Sh = w.Shade ?? 0, Ly = w.TopLayerPriority == true ? 1 : w.TopLayerPriority == false ? -1 : 0, Ic = w.IconId, It = (int)w.ItemType, Fx = (int)(w.UiEffects ?? 0) };
                // a reduced piece: its weenie's own slots, reduced to the ones it is worn in
                var own = LookOf(w.WeenieClassId);
                if (own != null && own.Loc != x.Loc && ReduceChoices(own.Loc).Any(r => r.Loc == x.Loc)) { x.Rd = x.Loc; x.Loc = own.Loc; x.Cov = own.Cov; }
                ReadRows(w, x);
                Put(f, x);
            }
        }

        /// <summary>Fill in a piece's icon from its weenie (outfits saved before icons were kept have none).</summary>
        static void Iconify(Piece x)
        {
            if (x.Ic != 0) return;
            var l = LookOf(x.W);
            if (l != null) { x.Ic = l.Ic; x.It = l.It; x.Fx = l.Fx; }
        }

        /// <summary>Owner ticket 261108-0008 ("icons dont look like they look in game ... kojis beast helm looks like something
        /// else"). The icon a piece made or tailored from this look should carry: its colour template's own icon in its
        /// ClothingTable (what retail's tailored and dyed clothing shows), else the look weenie's icon. ACE instead takes the
        /// table's FIRST template's icon when the item's template is not in the table (WorldObject.CalculateObjDesc), and
        /// Koji's Beast (template 4, table 0x10000541 has no 4) became another helm's icon 0x06003166. The made item gets
        /// this icon and IgnoreCloIcons so ACE leaves it.</summary>
        static uint IconFor(Piece x)
        {
            var l = LookOf(x.W);
            uint own = l?.Ic ?? x.Ic;
            if (x.Cb == 0) return own;
            try
            {
                var w = ACE.Database.DatabaseManager.World.GetCachedWeenie(x.W);
                if (w != null && w.PropertiesBool.TryGetValue(PropertyBool.IgnoreCloIcons, out var ign) && ign) return own;
                var t = DatManager.PortalDat.ReadFromDat<ClothingTable>(x.Cb);
                if (t != null && t.ClothingSubPalEffects.TryGetValue((uint)Math.Max(0, x.Pal), out var e) && e.Icon != 0) return e.Icon;
            }
            catch { }
            return own;
        }

        /// <summary>A made or tailored piece shows the icon of the look it carries (IconFor), and ACE keeps it.</summary>
        static void SetIcon(WorldObject wo, Piece x)
        {
            uint ic = IconFor(x);
            if (ic == 0) return;
            wo.IconId = ic;
            wo.IgnoreCloIcons = true;
        }

        /// <summary>The look's own rows (a mask's head part, a costume's texture or palette rows) onto a worn piece being
        /// tailored: ACE's UpdateArmorProps copies the ClothingBase, setup and colours, not these, so a mask tailored onto a
        /// helm wore the plain table's look. Anim parts apply on any item (ApplyOwnAnimParts); texture rows only on our
        /// own 900000+ weenies (ItemTextureMaps).</summary>
        static void CopyOwnRows(WorldObject src, WorldObject target)
        {
            var aps = src.Biota.PropertiesAnimPart?.Clone(src.BiotaDatabaseLock) ?? new List<ACE.Entity.Models.PropertiesAnimPart>();
            var tms = new List<ACE.Entity.Models.PropertiesTextureMap>();
            src.Biota.PropertiesTextureMap?.CopyTo(tms, src.BiotaDatabaseLock);
            var pls = new List<ACE.Entity.Models.PropertiesPalette>();
            src.Biota.PropertiesPalette?.CopyTo(pls, src.BiotaDatabaseLock);
            target.BiotaDatabaseLock.EnterWriteLock();
            try
            {
                target.Biota.PropertiesAnimPart = aps.Select(a => new ACE.Entity.Models.PropertiesAnimPart { Index = a.Index, AnimationId = a.AnimationId }).ToList();
                target.Biota.PropertiesTextureMap = tms.Select(t => new ACE.Entity.Models.PropertiesTextureMap { PartIndex = t.PartIndex, OldTexture = t.OldTexture, NewTexture = t.NewTexture }).ToList();
                target.Biota.PropertiesPalette = pls.Select(q => new ACE.Entity.Models.PropertiesPalette { SubPaletteId = q.SubPaletteId, Offset = q.Offset, Length = q.Length }).ToList();
            }
            finally { target.BiotaDatabaseLock.ExitWriteLock(); }
            target.ChangesDetected = true;
        }

        /// <summary>"Outfit 2" before "Outfit 10".</summary>
        static int OutfitOrder(string name) =>
            name.StartsWith("Outfit ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.Substring(7), out var n) ? n : int.MaxValue;

        static Piece Copy(Piece x) => new Piece { W = x.W, N = x.N, Loc = x.Loc, Cov = x.Cov, Ic = x.Ic, It = x.It, Fx = x.Fx, Cb = x.Cb, Pal = x.Pal, Sh = x.Sh, Ly = x.Ly, Rd = x.Rd,
            Ap = x.Ap?.Select(r => (uint[])r.Clone()).ToList(), Tm = x.Tm?.Select(r => (uint[])r.Clone()).ToList(), Pl = x.Pl?.Select(r => (uint[])r.Clone()).ToList() };

        static string Rows(List<uint[]> l) => l == null ? "" : string.Join(",", l.Select(r => string.Join(":", r)));

        /// <summary>A worn item's own look rows, when they differ from its weenie's (tailoring copied another look's).</summary>
        static void ReadRows(WorldObject w, Piece x)
        {
            var ap = new List<PropertiesAnimPart>(); w.Biota.PropertiesAnimPart?.CopyTo(ap, w.BiotaDatabaseLock);
            var tm = new List<PropertiesTextureMap>(); w.Biota.PropertiesTextureMap?.CopyTo(tm, w.BiotaDatabaseLock);
            var pl = new List<PropertiesPalette>(); w.Biota.PropertiesPalette?.CopyTo(pl, w.BiotaDatabaseLock);
            var wee = ACE.Database.DatabaseManager.World.GetCachedWeenie(w.WeenieClassId);
            var a = ap.Select(r => new uint[] { r.Index, r.AnimationId }).ToList();
            var t = tm.Select(r => new uint[] { r.PartIndex, r.OldTexture, r.NewTexture }).ToList();
            var q = pl.Select(r => new uint[] { r.SubPaletteId, r.Offset, r.Length }).ToList();
            var wa = wee?.PropertiesAnimPart?.Select(r => new uint[] { r.Index, r.AnimationId }).ToList() ?? new List<uint[]>();
            var wt = wee?.PropertiesTextureMap?.Select(r => new uint[] { r.PartIndex, r.OldTexture, r.NewTexture }).ToList() ?? new List<uint[]>();
            var wq = wee?.PropertiesPalette?.Select(r => new uint[] { r.SubPaletteId, r.Offset, r.Length }).ToList() ?? new List<uint[]>();
            if (Rows(a) != Rows(wa) || Rows(t) != Rows(wt) || Rows(q) != Rows(wq)) { x.Ap = a; x.Tm = t; x.Pl = q; }
        }

        /// <summary>The piece's own rows onto an item made from its weenie (preview, made, tailored, NPC).</summary>
        static void WriteRows(WorldObject wo, Piece x)
        {
            if (x.Ap == null && x.Tm == null && x.Pl == null) return;
            wo.BiotaDatabaseLock.EnterWriteLock();
            try
            {
                wo.Biota.PropertiesAnimPart = (x.Ap ?? new List<uint[]>()).Select(r => new PropertiesAnimPart { Index = (byte)r[0], AnimationId = r[1] }).ToList();
                wo.Biota.PropertiesTextureMap = (x.Tm ?? new List<uint[]>()).Select(r => new PropertiesTextureMap { PartIndex = (byte)r[0], OldTexture = r[1], NewTexture = r[2] }).ToList();
                wo.Biota.PropertiesPalette = (x.Pl ?? new List<uint[]>()).Select(r => new PropertiesPalette { SubPaletteId = r[0], Offset = (ushort)r[1], Length = (ushort)r[2] }).ToList();
            }
            finally { wo.BiotaDatabaseLock.ExitWriteLock(); }
            wo.ChangesDetected = true;
        }

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
            if (x.Ap != null || x.Tm != null || x.Pl != null)
            {   // its own rows: a fresh item each time (the cached one has the weenie's)
                var own = WorldObjectFactory.CreateNewWorldObject(x.W);
                if (own == null) return null;
                Colour(own, x); WriteRows(own, x);
                return own;
            }
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
            // Always, so the preview item carries the worn piece's own colour: a worn piece with no PaletteTemplate drew the
            // weenie's default template in the preview (and a cached item kept the last piece's). Catalogue looks carry
            // their weenie's own (LookOf), so this changes nothing for them. CalculateObjDesc reads null as 0.
            wo.PaletteTemplate = x.Pal > 0 ? x.Pal : (int?)null;
            wo.Shade = x.Sh;
            wo.TopLayerPriority = x.Ly > 0 ? true : x.Ly < 0 ? false : (bool?)null;
            // its slots and the body it covers, reduced or whole (the cached preview item may have been reduced before)
            wo.ValidLocations = (EquipMask)ELoc(x);
            if (ECov(x) != 0) wo.ClothingPriority = (CoverageMask)ECov(x);
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
                wo.CurrentWieldedLocation = (EquipMask)ELoc(x);
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
            FitOf(p).Open = false;
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
                var target = worn.FirstOrDefault(w => (uint)(w.ValidLocations ?? 0) == ELoc(x));
                var src = WorldObjectFactory.CreateNewWorldObject(x.W);
                if (target == null || src == null) { missed.Add(x.N); continue; }
                Colour(src, x);
                WriteRows(src, x);
                SetIcon(src, x);
                Tailoring.UpdateArmorProps(p, src, target);
                CopyOwnRows(src, target);
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
                WriteRows(wo, x);
                SetIcon(wo, x);
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
                WriteRows(wo, x);
                SetIcon(wo, x);
                if (c.TryEquipObject(wo, (EquipMask)ELoc(x))) done++;
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
                    + (x.Rd != 0 ? $", reduced to slots 0x{x.Rd:X} (a create list cannot carry a reduction)" : "")
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
