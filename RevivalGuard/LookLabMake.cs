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
    /// <summary>
    /// LOOK LAB "MAKE IT NOW" (owner 261125-0009: "any way to take that item right then and there and wear it on my character
    /// in game? as a 1 off"). The client's Look Lab sends its recipe (@looklab part ... base64 JSON, the Keep file's own
    /// fields); this builds the look's ClothingTable the way tools/looklab/package.py does (the base item's retail table, or its
    /// setup's parts, with every surface showing X now showing Y, bare body parts left out), registers it at runtime
    /// (CustomClothing.AddRuntime: kept as clothing/looklabrun-*.json, served to every client in the ObjDesc like the rune
    /// suits), and makes one item from the BASE weenie in the admin's pack with the look's name, description, table,
    /// colours, size, see-through and effect, its spells, mana, rare stamp and imbue removed. No new weenie, no datafix, no
    /// stage per item: the item lives in the biota alone. Its effect: a held item's own DefaultScript (sent with the object;
    /// our client plays a 0x33 value); a worn one carries P_EVERY and LookShowcase re-plays its script on the wearer.
    /// Admin only (AccessLevel.Admin), and only while rg_look_lab_make is on (default OFF; the switch-on datafix is staff's).
    /// </summary>
    static class LookLabMake
    {
        internal const string P_ON = "rg_look_lab_make";
        /// <summary>On a made item: seconds between re-plays of its DefaultScript on the wearer (a worn aura).</summary>
        internal const PropertyFloat P_EVERY = (PropertyFloat)29200;
        /// <summary>On a made item: "Look Lab, made by &lt;name&gt; &lt;utc&gt;" (what it is, for an audit).</summary>
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

        /// <summary>One item from the recipe, in the player's pack. Returns what to tell them.</summary>
        internal static string Make(Player me, string json)
        {
            var r = JsonNode.Parse(json).AsObject();
            uint wcid = (uint)(r["base"]?.GetValue<int>() ?? 0);
            var weenie = wcid != 0 ? DatabaseManager.World.GetCachedWeenie(wcid) : null;
            if (weenie == null) return $"Its base item {wcid} is not in this world.";
            string name = (r["name"]?.GetValue<string>() ?? "").Trim();
            if (name.Length == 0 || name.Length > 64) return "Give it a name (64 letters at most) first.";
            string desc = (r["description"]?.GetValue<string>() ?? "").Trim();
            var tex = new Dictionary<uint, uint>();
            if (r["textures"] is JsonArray ta)
                foreach (var x in ta)
                {
                    uint a = Hex(x["shows"]), b = Hex(x["becomes"]);
                    if (a >> 24 != 0x05 || b >> 24 != 0x05 || !DatManager.PortalDat.AllFiles.ContainsKey(b)) return $"Texture 0x{b:X8} is not in the DAT.";
                    tex[a] = b;
                }
            var bare = new HashSet<uint>();
            if (r["bareParts"] is JsonArray bp) foreach (var x in bp) bare.Add((uint)x.GetValue<int>());
            uint script = r["effect"] is JsonNode fx && fx.GetValue<string>() != "none" ? Hex(fx) : 0u;
            if (script != 0 && script >> 24 != 0x33) return "Its effect is not a PhysicsScript.";
            uint pal = Hex(r["paletteBase"]);
            if (pal != 0 && pal >> 24 != 0x04) return "Its palette is not a palette.";

            uint setup = weenie.GetProperty(PropertyDataId.Setup) ?? 0;
            uint cb = weenie.GetProperty(PropertyDataId.ClothingBase) ?? 0;
            uint? table = null;
            if (tex.Count > 0 || bare.Count > 0)
            {
                table = CustomClothing.AddRuntime(id => TableJson(id, setup, cb, tex, bare));
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
                if (worn) wo.SetProperty(P_EVERY, Math.Clamp(r["effectEvery"]?.GetValue<double>() ?? 10.0, 1.0, 60.0));
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

        /// <summary>package.py's look_table: the base's retail table (or its setup's parts), what each surface SHOWS replaced,
        /// bare body parts left off every player body, the retail palette templates unchanged.</summary>
        static string TableJson(uint id, uint setup, uint cb, Dictionary<uint, uint> tex, HashSet<uint> bare)
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
            return new JsonObject { ["Id"] = H(id), ["ClothingBaseEffects"] = bases, ["ClothingSubPalEffects"] = subs }.ToJsonString();
        }
    }
}
