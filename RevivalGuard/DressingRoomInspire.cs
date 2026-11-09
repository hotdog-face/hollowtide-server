using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>
    /// INSPIRE and REMIX (owner 2026-11-08: "the random button works well and creates some pretty wild results. but they
    /// are ugly. i wonder is it possible to get a button that gets me more usable tailors like players would make with a
    /// button press? we have pcap data of old player tailors to push off of").
    ///
    /// RetailOutfits.json (tools/dressing-room/outfits_extract.py) holds every worn look the retail captures saw on a
    /// player, as the ObjDesc the server drew: part swaps, texture swaps and sub-palettes, with no name. Each is read back
    /// into wardrobe pieces once, against the ClothingTables of our own catalogue, for the body it was seen on:
    ///  - pieces: the looks whose parts (and texture swaps, which tell Celdon from leather on one mesh) that body shows,
    ///    best explained first, never two that clash, each part explained once;
    ///  - colours: for each piece, the colour template whose palette sets hold the palettes seen at its colour ranges, and
    ///    the shade that picks that palette from its set (PaletteSet.GetPaletteID run backwards).
    /// Inspire wears one of them (only pieces that draw on the viewer's body). Remix keeps a real outfit's colours and
    /// swaps each piece for another look in the same slots that has the same colour template. Each is one Undo step.
    /// </summary>
    public static partial class DressingRoom
    {
        sealed class RealOutfit { public List<Piece> P = new List<Piece>(); public int Seen; }
        static List<RealOutfit> s_Real;
        static System.Threading.Tasks.Task s_RealLoading;
        static string s_RealStats = "";

        const uint NULL_PART = 0x010001EC;   // the empty model: on a player it fills the spare attachment parts
        sealed class Cand { public Look L; public List<(uint I, uint M, HashSet<(uint, uint)> T)> Fx; }

        /// <summary>Starts reading the real outfits (after the catalogue). True when they are ready.</summary>
        static bool RealReady()
        {
            lock (s_Lock)
            {
                if (s_Real != null) return true;
                if (s_RealLoading != null || s_Catalog == null) return false;
                var cat = s_Catalog;
                s_RealLoading = System.Threading.Tasks.Task.Run(() =>
                {
                    var res = new List<RealOutfit>();
                    try { res = ReadReal(cat); }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DressingRoom: real outfits: {e.Message}"); }
                    lock (s_Lock) { s_Real = res; s_RealLoading = null; }
                });
                return false;
            }
        }

        static List<RealOutfit> ReadReal(List<Look> cat)
        {
            JsonElement root;
            using (var rs = typeof(DressingRoom).Assembly.GetManifestResourceStream("RevivalGuard.RetailOutfits.json"))
            {
                if (rs == null) return new List<RealOutfit>();
                root = JsonDocument.Parse(rs).RootElement.Clone();
            }
            // index: (body, part index, model) -> the looks whose table puts that model on that part of that body
            var index = new Dictionary<(uint, uint, uint), List<Cand>>();
            foreach (var look in cat)
                foreach (var st in look.Styles.Count > 0 ? look.Styles : new List<Look> { look })
                {
                    if (st.Cb == 0) continue;
                    ClothingTable t;
                    try { t = DatManager.PortalDat.ReadFromDat<ClothingTable>(st.Cb); } catch { continue; }
                    foreach (var be in t.ClothingBaseEffects)
                    {
                        if (be.Value.CloObjectEffects.Count == 0) continue;
                        var c = new Cand
                        {
                            L = st,
                            Fx = be.Value.CloObjectEffects.Select(o => (o.Index, o.ModelId,
                                new HashSet<(uint, uint)>(o.CloTextureEffects.Select(x => (x.OldTexture, x.NewTexture))))).ToList(),
                        };
                        foreach (var o in c.Fx)
                        {
                            var k = (be.Key, o.I, o.M);
                            if (!index.TryGetValue(k, out var l)) index[k] = l = new List<Cand>();
                            l.Add(c);
                        }
                    }
                }
            var outs = new Dictionary<string, RealOutfit>();
            int read = 0, none = 0, covN = 0, cov80 = 0; double covSum = 0;
            foreach (var o in root.GetProperty("outfits").EnumerateArray())
            {
                read++;
                uint body = TableSetup(o.GetProperty("s").GetUInt32());
                var parts = new Dictionary<uint, uint>();
                foreach (var p in o.GetProperty("p").EnumerateArray()) parts[p[0].GetUInt32()] = p[1].GetUInt32();
                var tex = new HashSet<(uint, uint, uint)>();
                foreach (var x in o.GetProperty("t").EnumerateArray()) tex.Add((x[0].GetUInt32(), x[1].GetUInt32(), x[2].GetUInt32()));
                var subs = o.GetProperty("c").EnumerateArray().Select(x => (Pal: x[0].GetUInt32(), Off: x[1].GetUInt32(), Len: x[2].GetUInt32())).ToList();
                int seen = o.TryGetProperty("n", out var nn) ? nn.GetInt32() : 1;

                var cands = new HashSet<Cand>();
                foreach (var kv in parts)
                    if (kv.Value != NULL_PART && index.TryGetValue((body, kv.Key, kv.Value), out var l)) cands.UnionWith(l);
                var done = new HashSet<uint>();
                var chosen = new List<Piece>();
                while (true)
                {
                    Cand best = null; int bestScore = 0;
                    foreach (var c in cands)
                    {
                        int hit = 0, miss = 0, th = 0, tm = 0;
                        foreach (var e in c.Fx)
                        {
                            if (e.M == NULL_PART) continue;   // the invisible placeholder every player carries on parts 17-33
                            bool on = parts.TryGetValue(e.I, out var m) && m == e.M;
                            if (!on) { miss++; continue; }
                            foreach (var tt in e.T) { if (tex.Contains((e.I, tt.Item1, tt.Item2))) th++; else tm++; }
                            if (!done.Contains(e.I)) hit++;
                        }
                        if (hit == 0 || hit < miss) continue;   // most of what it would draw is not on this body
                        int score = hit * 10 + th * 3 - tm * 4 - miss * 3;
                        if (score < bestScore || score == 0 || (score == bestScore && best != null && c.L.W >= best.L.W)) continue;   // a tie goes to retail's own (lowest) weenie
                        var x = FromLook(c.L);
                        if (chosen.Any(q => Clash(q, x))) continue;
                        best = c; bestScore = score;
                    }
                    if (best == null) break;
                    cands.Remove(best);
                    foreach (var e in best.Fx) if (parts.TryGetValue(e.I, out var m) && m == e.M) done.Add(e.I);
                    var piece = FromLook(best.L);
                    Colour(piece, subs);
                    chosen.Add(piece);
                }
                // how much of what the player wore we could name: the parts that differ from the bare body (hair and
                // face swaps count too, so this is a floor)
                try
                {
                    var bare = DatManager.PortalDat.ReadFromDat<SetupModel>(o.GetProperty("s").GetUInt32()).Parts;
                    var worn = parts.Where(kv => kv.Value != NULL_PART && kv.Key < bare.Count && bare[(int)kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
                    if (worn.Count > 0) { double cov = (double)worn.Count(i => done.Contains(i)) / worn.Count; covSum += cov; covN++; if (cov >= 0.8) cov80++; }
                }
                catch { }
                if (chosen.Count < 2) { none++; continue; }
                chosen.Sort((a, b) => ELoc(a).CompareTo(ELoc(b)));
                string key = string.Join(";", chosen.Select(x => FormattableString.Invariant($"{x.W},{x.Pal},{x.Sh:0.##}")));
                if (outs.TryGetValue(key, out var had)) had.Seen += seen;
                else { var r = new RealOutfit { Seen = seen }; r.P.AddRange(chosen); outs[key] = r; }
            }
            var list = outs.Values.OrderByDescending(r => r.Seen).ToList();
            s_RealStats = $"{list.Count} real outfits ({read} retail looks read, {none} with under two pieces we could name); "
                + $"worn parts named: {(covN == 0 ? 0 : 100 * covSum / covN):0}% on average, {cov80} of {covN} looks at 80% or better";
            Mod.Log.Info($"[RevivalGuard] DressingRoom: {s_RealStats}");
            return list;
        }

        /// <summary>The colour template and shade that give the palettes seen at this piece's colour ranges.</summary>
        static void Colour(Piece x, List<(uint Pal, uint Off, uint Len)> subs)
        {
            if (x.Cb == 0) return;
            try
            {
                var t = DatManager.PortalDat.ReadFromDat<ClothingTable>(x.Cb);
                long bestK = -1; double bestFit = 0, bestShade = 0;
                foreach (var kv in t.ClothingSubPalEffects)
                {
                    int want = 0, got = 0; double shade = -1;
                    foreach (var sp in kv.Value.CloSubPalettes)
                    {
                        PaletteSet set;
                        try { set = DatManager.PortalDat.ReadFromDat<PaletteSet>(sp.PaletteSet); } catch { continue; }
                        foreach (var rg in sp.Ranges)
                        {
                            want++;
                            var hit = subs.Where(s => s.Off == rg.Offset && s.Len == rg.NumColors && set.PaletteList.Contains(s.Pal)).ToList();
                            if (hit.Count == 0) continue;
                            got++;
                            int n = set.PaletteList.Count;
                            if (shade < 0 && n > 1) shade = (set.PaletteList.IndexOf(hit[hit.Count - 1].Pal) + 0.5) / n;
                        }
                    }
                    if (want == 0) continue;
                    double fit = (double)got / want;
                    if (fit > bestFit) { bestFit = fit; bestK = kv.Key; bestShade = Math.Max(0, shade); }
                }
                if (bestK >= 0 && bestFit >= 0.5) { x.Pal = (int)bestK; x.Sh = Math.Round(bestShade, 3); }
            }
            catch { }
        }

        /// <summary>A real retail outfit's pieces that draw on this body, as new pieces.</summary>
        static List<Piece> OnBody(Player p, RealOutfit r)
        {
            uint s = p.SetupTableId;
            return r.P.Where(x => { var set = x.Cb == 0 ? null : BodiesOf(x.Cb); return set == null || set.Contains(s) || set.Contains(TableSetup(s)); })
                .Select(Copy).ToList();
        }

        static RealOutfit PickReal(Player p, out List<Piece> pieces)
        {
            pieces = null;
            List<RealOutfit> all; lock (s_Lock) all = s_Real;
            if (all == null || all.Count == 0) return null;
            for (int tries = 0; tries < 40; tries++)
            {
                var r = all[s_Rng.Next(all.Count)];
                var on = OnBody(p, r);
                if (on.Count >= 2) { pieces = on; return r; }
            }
            return null;
        }

        static string NotReady()
        {
            StartCatalog();   // a verb before the panel ever opened starts the reading too
            return s_Catalog == null ? "The wardrobe is still being read. Try again in a moment."
                : "Reading the real outfits from the retail captures. Try again in a moment.";
        }

        /// <summary>Inspire: wear a look a retail player wore.</summary>
        static void Inspire(Player p, Fit f)
        {
            if (!RealReady()) { f.Msg = NotReady(); return; }
            var r = PickReal(p, out var pieces);
            if (r == null) { f.Msg = "No real outfit fits this body."; return; }
            f.Pieces.Clear();
            foreach (var x in pieces) Put(f, x);
            int total; lock (s_Lock) total = s_Real.Count;
            Mod.Log.Info($"[RevivalGuard] DressingRoom: inspire {p.Name}: " + string.Join(", ", f.Pieces.Select(x => $"{x.N} ({x.W}) pal {x.Pal} sh {x.Sh:0.##}")));
            f.Msg = $"An outfit a retail player wore{(r.Seen > 1 ? $" (seen {r.Seen} times)" : "")}, one of {total}. Inspire again for another; Undo goes back.";
        }

        /// <summary>Remix: a real outfit's colours, each piece swapped for another look in the same slots that has the same
        /// colour template.</summary>
        static void Remix(Player p, Fit f)
        {
            if (!RealReady()) { f.Msg = NotReady(); return; }
            var r = PickReal(p, out var pieces);
            if (r == null) { f.Msg = "No real outfit fits this body."; return; }
            var cat = Drawable(p);
            f.Pieces.Clear();
            int swapped = 0;
            foreach (var x in pieces)
            {
                var others = cat.Where(l => l.Loc == x.Loc && l.W != x.W && l.Cb != 0 && l.Cb != x.Cb
                                            && PalettesOf(l.Cb).Any(q => (uint)q[0] == (uint)x.Pal)).ToList();
                var y = x;
                if (others.Count > 0)
                {
                    y = FromLook(others[s_Rng.Next(others.Count)]);
                    y.Pal = x.Pal; y.Sh = x.Sh;
                    if (f.Pieces.Any(o => Clash(o, y))) y = x; else swapped++;
                }
                Put(f, y);
            }
            Mod.Log.Info($"[RevivalGuard] DressingRoom: remix {p.Name}: " + string.Join(", ", f.Pieces.Select(x => $"{x.N} ({x.W}) pal {x.Pal} sh {x.Sh:0.##}")));
            f.Msg = $"A real outfit's colours on {swapped} other piece{(swapped == 1 ? "" : "s")}. Remix again for another; Undo goes back.";
        }
    }
}
