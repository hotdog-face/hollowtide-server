using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>Look lab "make it now".</summary>
    static class LookLabMake
    {
        internal const string P_ON = "rg_look_lab_make";
        /// <summary>On a made item: seconds between re-plays of its DefaultScript on the wearer (a worn aura).</summary>
        internal const PropertyFloat P_EVERY = (PropertyFloat)29200;
        /// <summary>On a made item: "Look Lab, made by &lt;name&gt; &lt;utc&gt;".</summary>
        internal const PropertyString P_MADE = (PropertyString)29201;
        static readonly Dictionary<string, string[]> s_Parts = new Dictionary<string, string[]>();

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard LookLabMake: admins may make a Look Lab look as a one-off item in their pack (@looklab)");
            CommandManager.TryAddCommand(Cmd, "looklab", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld,
                "Look Lab: make the look you are building as a one-off item in your pack (sent by the client's Make it now).",
                "part <id> <i> <n> <base64 chunk>");
            Mod.Log.Info($"[RevivalGuard] LookLabMake: @looklab ({P_ON}, default off)");
        }

        static void Cmd(Session s, params string[] args)
        {
            var me = s?.Player;
            if (me == null) return;
            void Reply(string t) => me.Session?.Network?.EnqueueSend(new GameMessageSystemChat("[Look Lab] " + t, ChatMessageType.Broadcast));
            if (!PkCommon.On(P_ON)) { Reply("Make it now is switched off on this server."); return; }
            if (args.Length != 5 || args[0] != "part" || !int.TryParse(args[2], out int i) || !int.TryParse(args[3], out int n) || n < 1 || n > 64 || i < 0 || i >= n)
            { Reply("Use the Look Lab's Make it now button."); return; }
            string key = me.Guid.Full + ":" + args[1];
            string json;
            lock (s_Parts)
            {
                if (!s_Parts.TryGetValue(key, out var parts) || parts.Length != n) s_Parts[key] = parts = new string[n];
                parts[i] = args[4];
                if (parts.Any(p => p == null)) return;
                s_Parts.Remove(key);
                try { json = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(parts))); }
                catch { Reply("The look arrived garbled; press Make it now again."); return; }
            }
            try { Reply(Make(me, json)); }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookLabMake: " + e); Reply("Could not make it: " + e.Message); }
        }

        static uint Hex(JsonNode n) => n == null ? 0u : CustomClothingRule.Num(n.GetValue<string>());

        /// <summary>A recipe's look, checked: what Make it now and the Dressing Room's Lab drawer both build from.</summary>
        internal sealed class Look
        {
            public readonly Dictionary<uint, uint> Tex = new Dictionary<uint, uint>();
            public readonly HashSet<uint> Bare = new HashSet<uint>();
            public readonly List<(uint Set, List<(uint Off, uint N)> Ranges)> Dyes = new List<(uint, List<(uint, uint)>)>();
            public uint Spectral, Script, Pal;
            public double Every;
            public string Err;
            public bool Table => Tex.Count > 0 || Bare.Count > 0 || Spectral != 0 || Dyes.Count > 0;
        }

        internal static Look Read(JsonObject r)
        {
            var o = new Look();
            if (r["textures"] is JsonArray ta)
                foreach (var x in ta)
                {
                    uint a = Hex(x["shows"]), b = Hex(x["becomes"]);
                    if (a >> 24 != 0x05 || b >> 24 != 0x05 || (!DatManager.PortalDat.AllFiles.ContainsKey(b) && (b & 0xFFF00000u) != 0x05E00000u)) { o.Err = $"Texture 0x{b:X8} is not in the DAT."; return o; }
                    o.Tex[a] = b;
                }
            // Our materials: every surface shows a self-describing id 0x05E00000-0x05EFFFFF, no file in the DAT
            o.Spectral = r["spectral"]?["texture"] is JsonNode sp ? Hex(sp) : 0u;
            if (o.Spectral != 0 && (o.Spectral & 0xFFF00000u) != 0x05E00000u) { o.Err = "Its material is not one this server knows."; return o; }
            if (r["bareParts"] is JsonArray bp) foreach (var x in bp) o.Bare.Add((uint)x.GetValue<int>());
            o.Script = r["effect"] is JsonNode fx && fx.GetValue<string>() != "none" ? Hex(fx) : 0u;
            if (o.Script != 0 && o.Script >> 24 != 0x33) { o.Err = "Its effect is not a PhysicsScript."; return o; }
            o.Every = Math.Clamp(r["effectEvery"]?.GetValue<double>() ?? 10.0, 1.0, 60.0);
            o.Pal = Hex(r["paletteBase"]);
            if (o.Pal != 0 && o.Pal >> 24 != 0x04) { o.Err = "Its palette is not a palette."; return o; }
            // free colours (the Dye page's "palettes"): read here, built into a table only by the Dressing Room's drawer
            if (r["palettes"] is JsonArray pa)
                foreach (var x in pa)
                {
                    uint set = Hex(x["set"]);
                    if (set >> 24 != 0x0F || !DatManager.PortalDat.AllFiles.ContainsKey(set)) { o.Err = $"Colour 0x{set:X8} is not a retail palette set."; return o; }
                    var rg = new List<(uint, uint)>();
                    if (x["ranges"] is JsonArray ra) foreach (var q in ra) rg.Add((q[0].GetValue<uint>(), q[1].GetValue<uint>()));
                    if (rg.Count > 0) o.Dyes.Add((set, rg));
                }
            return o;
        }

        /// <summary>One item from the recipe, in the player's pack.</summary>
        internal static string Make(Player me, string json)
        {
            var r = JsonNode.Parse(json).AsObject();
            uint wcid = (uint)(r["base"]?.GetValue<int>() ?? 0);
            var weenie = wcid != 0 ? DatabaseManager.World.GetCachedWeenie(wcid) : null;
            if (weenie == null) return $"Its base item {wcid} is not in this world.";
            string name = (r["name"]?.GetValue<string>() ?? "").Trim();
            if (name.Length == 0 || name.Length > 64) return "Give it a name (64 letters at most) first.";
            string desc = (r["description"]?.GetValue<string>() ?? "").Trim();
            var look = Read(r);
            if (look.Err != null) return look.Err;
            var tex = look.Tex; var bare = look.Bare; uint spectral = look.Spectral, script = look.Script, pal = look.Pal;

            uint setup = weenie.GetProperty(PropertyDataId.Setup) ?? 0;
            uint cb = weenie.GetProperty(PropertyDataId.ClothingBase) ?? 0;
            uint? table = null;
            if (tex.Count > 0 || bare.Count > 0 || spectral != 0)
            {
                table = CustomClothing.AddRuntime(id => TableJson(id, setup, cb, tex, bare, spectral));
                if (table == null) return "The Look Lab's runtime table range is full (256 looks): package the keepers.";
            }

            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (wo == null) return "Its base item could not be made.";
            wo.Name = name;
            if (desc.Length > 0) wo.LongDesc = desc;
            if (table.HasValue) wo.ClothingBase = table.Value;
            if (pal != 0) wo.PaletteBaseId = pal;
            if (r["paletteTemplate"] != null) wo.PaletteTemplate = r["paletteTemplate"].GetValue<int>();
            if (r["shade"] != null) wo.Shade = r["shade"].GetValue<double>();
            if (r["scale"] != null) wo.ObjScale = (float)Math.Clamp(r["scale"].GetValue<double>() * (wo.ObjScale ?? 1f), 0.2, 5.0);
            if (r["translucency"] != null) wo.Translucency = (float)Math.Clamp(r["translucency"].GetValue<double>(), 0.0, 0.95);
            if (script != 0)
            {
                wo.DefaultScriptId = script;
                bool worn = ((int)(wo.ValidLocations ?? 0) & (0x7FFF | 0x08000000)) != 0;
                if (worn) wo.SetProperty(P_EVERY, look.Every);
            }
            // its own item, not an upgrade: no spells, procs, mana, rare stamp, spellcraft, difficulty or imbue
            wo.Biota.ClearSpells(wo.BiotaDatabaseLock);
            wo.RemoveProperty(PropertyDataId.Spell); wo.RemoveProperty(PropertyDataId.ProcSpell);
            foreach (var p in new[] { PropertyInt.RareId, PropertyInt.ItemSpellcraft, PropertyInt.ItemCurMana, PropertyInt.ItemMaxMana,
                                      PropertyInt.ItemDifficulty, PropertyInt.ItemAllegianceRankLimit, PropertyInt.ImbuedEffect })
                wo.RemoveProperty(p);
            wo.SetProperty(P_MADE, $"Look Lab, made by {me.Name} {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            if (!me.TryCreateInInventoryWithNetworking(wo)) { wo.Destroy(); return "Your pack is full."; }
            Mod.Log.Info($"[RevivalGuard] LookLabMake: {me.Name} made \"{name}\" from {wcid}" + (table.HasValue ? $", table 0x{table:X8}" : "") + (script != 0 ? $", effect 0x{script:X8}" : ""));
            return $"Made \"{name}\" in your pack (a one-off: its look lives on this item alone).";
        }

        /// <summary>package.py's look_table: the base's retail table, what each surface SHOWS replaced</summary>
        internal const int DYE_TEMPLATE = 9990;

        internal static string TableJson(uint id, uint setup, uint cb, Dictionary<uint, uint> tex, HashSet<uint> bare, uint spectral = 0,
                                         List<(uint Set, List<(uint Off, uint N)> Ranges)> dyes = null)
        {
            var retail = cb != 0 ? DatManager.PortalDat.ReadFromDat<ClothingTable>(cb) : null;
            var entries = new Dictionary<uint, List<(uint idx, uint model, Dictionary<uint, uint> tx)>>();
            if (retail != null)
                foreach (var (s, eff) in retail.ClothingBaseEffects)
                    entries[s] = eff.CloObjectEffects.Select(o => (o.Index, o.ModelId, o.CloTextureEffects.ToDictionary(t => t.OldTexture, t => t.NewTexture))).ToList();
            if (setup != 0 && !entries.ContainsKey(setup))
            {
                var sm = DatManager.PortalDat.ReadFromDat<SetupModel>(setup);
                entries[setup] = sm.Parts.Select((m, i) => ((uint)i, m, new Dictionary<uint, uint>())).ToList();
            }
            string H(uint v) => $"0x{v:X8}";
            var bases = new JsonObject();
            foreach (var (s, fx) in entries.OrderBy(e => e.Key))
            {
                var objs = new JsonArray();
                foreach (var (idx, model, tx) in fx)
                {
                    if (s != setup && bare.Contains(idx)) continue;
                    var texs = new JsonArray();
                    if (model >> 24 == 0x01)
                    {
                        var gfx = DatManager.PortalDat.ReadFromDat<GfxObj>(model);
                        var seen = new HashSet<uint>();
                        foreach (var sid in gfx.Surfaces)
                        {
                            var surf = DatManager.PortalDat.ReadFromDat<Surface>(sid);
                            if (surf == null || ((uint)surf.Type & 6) == 0 || surf.OrigTextureId == 0 || !seen.Add(surf.OrigTextureId)) continue;
                            uint orig = surf.OrigTextureId;
                            uint shows = tx.TryGetValue(orig, out var t) ? t : orig;
                            uint now = tex.TryGetValue(shows, out var n2) ? n2 : shows;
                            if (spectral != 0 && (now & 0xFFF00000u) != 0x05E00000u) now = spectral;   // a surface's own material wins
                            if (now != orig) texs.Add(new JsonObject { ["OldTexture"] = H(orig), ["NewTexture"] = H(now) });
                        }
                    }
                    objs.Add(new JsonObject { ["Index"] = (int)idx, ["ModelId"] = H(model), ["CloTextureEffects"] = texs });
                }
                bases[H(s)] = new JsonObject { ["CloObjectEffects"] = objs };
            }
            var subs = new JsonObject();
            if (retail != null)
                foreach (var (k, sp) in retail.ClothingSubPalEffects.OrderBy(e => e.Key))
                    subs[k.ToString()] = new JsonObject
                    {
                        ["Icon"] = H(sp.Icon),
                        ["CloSubPalettes"] = new JsonArray(sp.CloSubPalettes.Select(c => (JsonNode)new JsonObject
                        {
                            ["Ranges"] = new JsonArray(c.Ranges.Select(rg => (JsonNode)new JsonObject { ["Offset"] = rg.Offset, ["NumColors"] = rg.NumColors }).ToArray()),
                            ["PaletteSet"] = H(c.PaletteSet),
                        }).ToArray()),
                    };
            if (dyes != null && dyes.Count > 0)
                subs[DYE_TEMPLATE.ToString()] = new JsonObject
                {
                    ["Icon"] = H(0),
                    ["CloSubPalettes"] = new JsonArray(dyes.Select(d => (JsonNode)new JsonObject
                    {
                        ["Ranges"] = new JsonArray(d.Ranges.Select(rg => (JsonNode)new JsonObject { ["Offset"] = rg.Off, ["NumColors"] = rg.N }).ToArray()),
                        ["PaletteSet"] = H(d.Set),
                    }).ToArray()),
                };
            return new JsonObject { ["Id"] = H(id), ["ClothingBaseEffects"] = bases, ["ClothingSubPalEffects"] = subs }.ToJsonString();
        }
    }
}
