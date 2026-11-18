using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Entity.Mutations;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// "FEELS LIKE RETAIL DARKTIDE" (owner's yes 2026-11-18 to build; each switch DEFAULT OFF until his separate yes):
    /// docs/proposals/darktide-feel-2026-11-18.md. Pure rules: DarktideFeelRule.cs.
    ///
    ///   rg_signed_loot              corpse-capsule and house-capsule items carry retail makers' names: a looted item a
    ///                               player could have tinkered is tinkered for real (ACE's own mutation scripts, the
    ///                               same NumTimesTinkered, TinkerLog and numbers), "Last tinkered by", "Imbued by" (with
    ///                               the imbue's icon underlay) and "Created by" on a recipe's crafted result.
    ///   rg_signed_loot_inscriptions a few weapons and armour carry their owner's own inscription.
    ///   rg_house_last_owner         an empty capsule house's covenant crystal reads "&lt;last owner&gt;'s Cottage" while his
    ///                               pieces hang there; the stash note is signed by the same name; gone at purchase.
    ///   rg_onthisday                twice a day a world line of dated real Asheron's Call history, Darktide first.
    ///
    /// NAMES are real retail characters and never in git or this source: read from the server-only names file the corpse
    /// capsules use (~/ace/corpse-capsules/names.json, RG_CORPSE_NAMES; tools/corpse-capsules/names.py): people (Darktide
    /// flagged), dtTinkers, dtImbuers. No file: nothing is signed and the signs stay as ACE has them.
    /// </summary>
    static class DarktideFeel
    {
        internal const string P_LOOT = "rg_signed_loot", P_INSCRIBE = "rg_signed_loot_inscriptions", P_SIGN = "rg_house_last_owner", P_DAY = "rg_onthisday";
        internal static bool Loot => PkCommon.On(P_LOOT);
        internal static bool Inscribe => PkCommon.On(P_INSCRIBE);
        internal static bool Sign => PkCommon.On(P_SIGN);
        internal static bool Day => PkCommon.On(P_DAY);
        static bool s_Registered;

        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            PkCommon.RegisterBool(P_LOOT, false, "RevivalGuard SignedLoot: capsule items carry retail tinker, imbuer and maker names (needs the owner's yes)");
            PkCommon.RegisterBool(P_INSCRIBE, false, "RevivalGuard SignedLoot: a few capsule weapons and armour carry their owner's inscription (needs the owner's yes)");
            PkCommon.RegisterBool(P_SIGN, false, "RevivalGuard house capsules: an empty capsule house's crystal names its last owner (needs the owner's yes)");
            PkCommon.RegisterBool(P_DAY, false, "RevivalGuard OnThisDay: twice a day a line of real Asheron's Call history (needs the owner's yes)");
            CommandManager.TryAddCommand(Cmd, "darktidefeel", AccessLevel.Developer, CommandHandlerFlag.None,
                "Signed loot, capsule house signs, On this day: status, roll <n>, day [MM-dd], say, loadlb <landblock hex> (works from the console).",
                "[status | roll [n] | day [MM-dd] | say | loadlb <hex>]");
            OnThisDay.Load();
            var n = Names;
            Mod.Log.Info($"[RevivalGuard] DarktideFeel: loot {(Loot ? "ON" : "off")}, inscriptions {(Inscribe ? "ON" : "off")}, house signs {(Sign ? "ON" : "off")}, on this day {(Day ? "ON" : "off")}; " +
                         (n == null ? "no server names file: nothing signed" : $"names {n.Darktide.Count} Darktide, {n.Other.Count} other, {n.Tinkers.Count} tinkers, {n.Imbuers.Count} imbuers") +
                         $"; {OnThisDay.Count} dated lines (@darktidefeel)");
        }

        // ---------------------------------------------------------------- names (server-only file)
        sealed class NameLists { public List<string> Darktide = new(), Other = new(), Tinkers = new(), Imbuers = new(); }
        static NameLists s_Names; static bool s_NamesTried;
        static readonly object s_Lock = new object();

        static string NamesPath => Environment.GetEnvironmentVariable("RG_CORPSE_NAMES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ace", "corpse-capsules", "names.json");

        static NameLists Names
        {
            get
            {
                lock (s_Lock)
                {
                    if (s_NamesTried) return s_Names;
                    s_NamesTried = true;
                    try
                    {
                        if (!File.Exists(NamesPath)) return null;
                        using var doc = JsonDocument.Parse(File.ReadAllText(NamesPath));
                        var r = doc.RootElement; var n = new NameLists();
                        foreach (var x in r.GetProperty("people").EnumerateArray())
                        {
                            var name = x[0].GetString();
                            if (!TimeCapsuleRule.NameOk(name)) continue;
                            ((x[2].GetInt32() & 1) != 0 ? n.Darktide : n.Other).Add(name);
                        }
                        void Read(string key, List<string> into)
                        {
                            if (r.TryGetProperty(key, out var a)) foreach (var x in a.EnumerateArray()) { var s = x.GetString(); if (TimeCapsuleRule.NameOk(s)) into.Add(s); }
                        }
                        Read("dtTinkers", n.Tinkers); Read("dtImbuers", n.Imbuers);
                        s_Names = n.Darktide.Count + n.Other.Count > 0 ? n : null;
                    }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DarktideFeel: names file {NamesPath} not read ({e.Message}); nothing signed"); }
                    return s_Names;
                }
            }
        }

        static bool InUse(string n) { try { return PlayerManager.FindByName(n) != null; } catch { return false; } }

        static SignedLootRule.Pools Pools()
        {
            var n = Names;
            return n == null ? new SignedLootRule.Pools() : new SignedLootRule.Pools { Tinkers = n.Tinkers, Imbuers = n.Imbuers, Darktide = n.Darktide, Other = n.Other, InUse = InUse };
        }

        /// <summary>The house's last owner (Darktide first), else null: then TimeCapsule signs as it always did.</summary>
        internal static string LastOwner(ulong roll)
        {
            var n = Names;
            if (n == null) return null;
            var name = HouseSignRule.LastOwner(n.Darktide, n.Other, roll, InUse, TimeCapsuleRule.NameOk);
            return name == TimeCapsuleRule.FallbackNames[0] ? null : name;
        }

        // ---------------------------------------------------------------- signed loot
        static readonly Random s_Rng = new Random();

        static SignedLootRule.Facts FactsOf(WorldObject wo)
        {
            var kind = wo is MeleeWeapon ? SignedLootRule.Kind.Melee : wo is MissileLauncher ? SignedLootRule.Kind.Missile : wo is Caster ? SignedLootRule.Kind.Caster
                     : (wo.ItemType & ItemType.Armor) != 0 && (wo.ArmorLevel ?? 0) > 0 ? SignedLootRule.Kind.Armor : SignedLootRule.Kind.Other;
            return new SignedLootRule.Facts
            {
                Kind = kind, Workmanship = wo.ItemWorkmanship ?? 0, Tinkered = wo.NumTimesTinkered, Imbued = wo.ImbuedEffect != ImbuedEffectType.Undef,
                Salvage = (wo.ItemType & ItemType.TinkeringMaterial) != 0,
                Inscribable = wo.Inscribable, Inscribed = wo.ScribeName != null, Crafted = SignedLootRule.Crafted(wo.WeenieClassId),
                HasCraftsman = wo.GetProperty(PropertyString.CraftsmanName) != null, Elemental = (wo.ElementalDamageMod ?? 0) > 0,
                Signed = wo.GetProperty(PropertyString.TinkerName) != null || wo.GetProperty(PropertyString.ImbuerName) != null,
            };
        }

        static MaterialType MaterialOf(uint mutation) => mutation switch
        {
            SignedLootRule.STEEL => MaterialType.Steel, SignedLootRule.IRON => MaterialType.Iron, SignedLootRule.MAHOGANY => MaterialType.Mahogany,
            SignedLootRule.GRANITE => MaterialType.Granite, SignedLootRule.BRASS => MaterialType.Brass, SignedLootRule.GREEN_GARNET => MaterialType.GreenGarnet,
            SignedLootRule.SUNSTONE => MaterialType.Sunstone, SignedLootRule.BLACK_OPAL => MaterialType.BlackOpal, _ => MaterialType.FireOpal,
        };

        /// <summary>One tinker as ACE applies it (RecipeManager.TryMutate + HandleTinkerLog): the mutation script, and the
        /// salvage's material appended to TinkerLog when NumTimesTinkered went up.</summary>
        static bool Apply(WorldObject wo, uint mutation)
        {
            var script = MutationCache.GetMutation(mutation);
            if (script == null) return false;
            int before = wo.NumTimesTinkered;
            script.TryMutate(wo);
            if (wo.NumTimesTinkered == before) return false;
            wo.TinkerLog = (wo.TinkerLog != null ? wo.TinkerLog + "," : "") + (uint)MaterialOf(mutation);
            return true;
        }

        /// <summary>Signs what a capsule holds, as its owner and the tinkers of his day left it. Returns how many got a name.</summary>
        internal static int SignAll(IEnumerable<WorldObject> items, string owner)
        {
            if (!Loot || items == null) return 0;
            var pools = Pools();
            if (pools.Empty) return 0;
            int n = 0;
            foreach (var wo in items.Where(i => i != null).ToList())
                try
                {
                    var line = SignOne(wo, owner, pools, s_Rng, Inscribe);
                    if (line == null) continue;
                    n++;
                    Mod.Log.Info($"[RevivalGuard] SignedLoot: {owner ?? "?"}'s {line}");   // server log only (staff)
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SignedLoot {wo.Name}: {e.Message}"); }
            return n;
        }

        internal static string SignOne(WorldObject wo, string owner, SignedLootRule.Pools pools, Random rng, bool inscriptions)
        {
            var f = FactsOf(wo);
            var plan = SignedLootRule.Decide(f, owner, pools, rng, inscriptions);
            if (!plan.Any || !SignedLootRule.Consistent(f, plan)) return null;
            int applied = 0;
            if (plan.Imbue != 0 && Apply(wo, plan.Imbue))
            {
                applied++;
                if (RecipeManager.IconUnderlay.TryGetValue(wo.ImbuedEffect, out var underlay)) wo.IconUnderlayId = underlay;
                wo.SetProperty(PropertyString.ImbuerName, plan.Imbuer);
            }
            int tinks = 0;
            foreach (var m in plan.Tinkers) if (Apply(wo, m)) { applied++; tinks++; }
            if (tinks > 0) wo.SetProperty(PropertyString.TinkerName, plan.Tinker);
            if (plan.Craftsman != null) wo.SetProperty(PropertyString.CraftsmanName, plan.Craftsman);
            if (plan.Inscription != null)
            {
                wo.Inscription = plan.Inscription; wo.ScribeName = plan.Scribe;
                wo.ScribeIID = 0x5F000000u | (uint)(Math.Abs(plan.Scribe.GetHashCode()) & 0xFFFFF);   // a player-range id, as the notes
            }
            return $"{wo.Name}: tinkered {wo.NumTimesTinkered}{(tinks > 0 ? " by " + plan.Tinker : "")}{(plan.Imbuer != null && applied > tinks ? $", imbued {wo.ImbuedEffect} by {plan.Imbuer}" : "")}" +
                   $"{(plan.Craftsman != null ? ", created by " + plan.Craftsman : "")}{(plan.Inscription != null ? $", inscribed \"{plan.Inscription}\"" : "")}";
        }

        // ---------------------------------------------------------------- the capsule house's crystal
        internal const PropertyBool SIGN_SET = (PropertyBool)29156;          // on the slumlord: its name is ours
        internal const PropertyString LAST_OWNER = (PropertyString)29158;    // on the house: this vacancy's last owner

        static string BaseName(SlumLord sl) => DatabaseManager.World.GetCachedWeenie(sl.WeenieClassId)?.GetName() ?? sl.House?.HouseType.ToString();

        /// <summary>Puts the last owner's name on an empty capsule house's crystal, or takes ours off. Never touches a
        /// name ACE set (an owned house).</summary>
        internal static void UpdateSign(House house, bool piecesUp, string owner)
        {
            var sl = house?.SlumLord;
            if (sl == null) return;
            bool owned = (house.HouseOwner ?? 0) != 0;
            bool show = HouseSignRule.Show(Sign, owned, piecesUp, owner);
            bool ours = sl.GetProperty(SIGN_SET) ?? false;
            if (show)
            {
                var want = HouseSignRule.Signed(owner, BaseName(sl));
                if (sl.Name == want && ours) return;
                sl.Name = BaseName(sl);
                sl.SetAndBroadcastName(owner);
                sl.SetProperty(SIGN_SET, true);
                sl.SaveBiotaToDatabase();
                Mod.Log.Info($"[RevivalGuard] DarktideFeel: 0x{house.Guid.Full:X8} crystal reads \"{sl.Name}\"");
            }
            else if (ours) ClearSign(sl, owned);
        }

        /// <summary>Ours off: back to the weenie's name (ACE then names an owner's house as it always does).</summary>
        internal static void ClearSign(SlumLord sl, bool owned)
        {
            if (sl == null || !(sl.GetProperty(SIGN_SET) ?? false)) return;
            sl.RemoveProperty(SIGN_SET);
            if (!owned) sl.SetAndBroadcastName();
            else sl.Name = BaseName(sl);   // a purchase: ACE appends the buyer's name right after (SetHouseOwner)
            sl.SaveBiotaToDatabase();
        }

        // ---------------------------------------------------------------- @darktidefeel
        static void Say(Session s, string t)
        {
            if (s?.Network != null) s.Network.EnqueueSend(new GameMessageSystemChat(t, ChatMessageType.Broadcast));
            else { Console.WriteLine(t); Mod.Log.Info(t); }   // the server console (testshard.sh console)
        }

        static void Cmd(Session session, params string[] a)
        {
            var verb = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
            var n = Names;
            switch (verb)
            {
                case "roll":
                {
                    // Signs fresh loot rolls without touching the world: the odds and names as a corpse would get them.
                    int count = a.Length > 1 && int.TryParse(a[1], out var c) ? Math.Clamp(c, 1, 200) : 20;
                    var pools = Pools();
                    if (pools.Empty) { Say(session, "[DarktideFeel] No server names file: nothing would be signed."); return; }
                    int signed = 0, shown = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var wo = CorpseCapsules.LootForTest(5 + i % 3);
                        if (wo == null) continue;
                        string Stats() => $"AL {wo.ArmorLevel ?? 0}, dmg {wo.Damage ?? 0}, var {wo.DamageVariance ?? 0:0.###}, dmod {wo.DamageMod ?? 0:0.###}, def {wo.WeaponDefense ?? 0:0.###}, elem {wo.ElementalDamageMod ?? 0:0.###}";
                        var before = Stats();
                        var line = SignOne(wo, n.Darktide.Count > 0 ? n.Darktide[s_Rng.Next(n.Darktide.Count)] : null, pools, s_Rng, true);
                        if (line != null)
                        {
                            signed++;
                            bool imb = wo.GetProperty(PropertyString.ImbuerName) != null;
                            if (shown++ < 10 || imb) Say(session, $"[DarktideFeel] {line} | wk {wo.ItemWorkmanship} | {before} -> {Stats()} | log {wo.TinkerLog}{(imb ? $" | underlay 0x{wo.IconUnderlayId:X8}" : "")}");
                        }
                        wo.Destroy();
                    }
                    Say(session, $"[DarktideFeel] {signed} of {count} loot rolls signed (inscriptions on for this test).");
                    return;
                }
                case "day":
                {
                    var when = DateTime.UtcNow;
                    if (a.Length > 1 && DateTime.TryParseExact($"{when.Year}-{a[1]}", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)) when = d;
                    for (int i = 0; i < 3; i++) { var e = OnThisDay.Entry(when, i); if (e != null) Say(session, $"[DarktideFeel] {when:MM-dd} #{i + 1}: {OnThisDayRule.Line(e, when)} ({e.Url})"); }
                    return;
                }
                case "say": Say(session, OnThisDay.SayNow(force: true) ?? "[DarktideFeel] Nothing for today."); return;
                case "loadlb":
                {
                    // Loads a landblock with nobody in it, so its houses (hook pieces, crystal) and corpses are made as a
                    // visitor would find them. Test shards only; it unloads by itself like any other.
                    if (a.Length < 2 || !uint.TryParse(a[1].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var lb)) { Say(session, "[DarktideFeel] loadlb <landblock hex, e.g. C7D2>"); return; }
                    var id = new ACE.Entity.LandblockId((lb & 0xFFFF) << 16 | 0xFFFF);
                    LandblockManager.GetLandblock(id, false, false);
                    Say(session, $"[DarktideFeel] Loading landblock {lb & 0xFFFF:X4}: its houses and corpses are made as it loads (see the log).");
                    return;
                }
            }
            Say(session, $"[DarktideFeel] signed loot {(Loot ? "ON" : "off")} (inscriptions {(Inscribe ? "ON" : "off")}), house signs {(Sign ? "ON" : "off")}, on this day {(Day ? "ON" : "off")}.");
            Say(session, n == null ? $"[DarktideFeel] No names file at {NamesPath}: nothing is signed, crystals stay as ACE has them."
                                   : $"[DarktideFeel] Names: {n.Darktide.Count} Darktide, {n.Other.Count} other, {n.Tinkers.Count} tinkers by trade, {n.Imbuers.Count} imbuers by trade.");
            Say(session, $"[DarktideFeel] On this day: {OnThisDay.Count} lines; {OnThisDay.Status()}");
        }
    }

    /// <summary>ON THIS DAY: the dated lines (embedded OnThisDay.json) said at OnThisDayRule.HOURS, at most twice a day,
    /// only with someone online. What was said is kept in Mods/RevivalGuard/OnThisDay/state.json across restarts.</summary>
    static class OnThisDay
    {
        static List<OnThisDayRule.Entry> s_All = new List<OnThisDayRule.Entry>();
        static string s_Day; static int s_Said; static double s_Next;
        static readonly object s_Lock = new object();
        internal static int Count => s_All.Count;
        static string StatePath => Path.Combine(ModFolder.Sub("OnThisDay"), "state.json");

        internal static void Load()
        {
            try
            {
                using var rs = typeof(OnThisDay).Assembly.GetManifestResourceStream("RevivalGuard.OnThisDay.json");
                using var doc = JsonDocument.Parse(rs);
                foreach (var x in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    var e = new OnThisDayRule.Entry
                    {
                        Md = x.GetProperty("md").GetString(), Year = x.GetProperty("year").GetInt32(), Darktide = x.GetProperty("darktide").GetBoolean(),
                        Text = x.GetProperty("text").GetString(), Title = x.GetProperty("title").GetString(), Url = x.GetProperty("source_url").GetString(),
                    };
                    if (OnThisDayRule.Problem(e) == null) s_All.Add(e);
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] OnThisDay: data not loaded ({e.Message})"); }
            try
            {
                if (File.Exists(StatePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                    s_Day = doc.RootElement.GetProperty("day").GetString(); s_Said = doc.RootElement.GetProperty("said").GetInt32();
                }
            }
            catch { }
        }

        internal static OnThisDayRule.Entry Entry(DateTime utc, int i) => OnThisDayRule.Pick(s_All, utc, i);
        internal static string Status() { lock (s_Lock) return $"today {s_Day ?? "-"}: {s_Said} said"; }

        /// <summary>From PlayerManager.Tick; looks every 30 s.</summary>
        internal static void Tick()
        {
            double now = ACE.Common.Time.GetUnixTime();
            if (now < s_Next) return;
            s_Next = now + 30;
            if (!DarktideFeel.Day) return;
            SayNow(force: false);
        }

        internal static string SayNow(bool force)
        {
            var utc = DateTime.UtcNow;
            string line; OnThisDayRule.Entry e;
            lock (s_Lock)
            {
                var day = utc.ToString("yyyy-MM-dd");
                if (s_Day != day) { s_Day = day; s_Said = 0; }
                if (!force && (OnThisDayRule.Due(utc, s_Said) < 0 || PlayerManager.GetAllOnline().Count == 0)) return null;
                e = OnThisDayRule.Pick(s_All, utc, s_Said);
                if (e == null) { if (!force) { s_Said = OnThisDayRule.HOURS.Length; Save(); } return null; }
                s_Said++;
                Save();
                line = OnThisDayRule.Line(e, utc);
            }
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(line, ChatMessageType.WorldBroadcast));
            Mod.Log.Info($"[RevivalGuard] OnThisDay: said {e.Md} {e.Year}{(e.Darktide ? " (Darktide)" : "")}: {line}");
            return line;
        }

        static void Save()
        {
            try { File.WriteAllText(StatePath, JsonSerializer.Serialize(new { day = s_Day, said = s_Said })); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] OnThisDay: state not saved ({e.Message})"); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
    static class DarktideFeelTick
    {
        static bool Prepare() { try { DarktideFeel.Register(); } catch (Exception e) { Mod.Log.Error($"[RevivalGuard] DarktideFeel: register: {e.Message}"); } return true; }
        static void Postfix()
        {
            try { OnThisDay.Tick(); }
            catch (Exception e) { TickGuard.Report("OnThisDay", e); }
        }
    }

    /// <summary>Before ACE names the crystal for its buyer ("&lt;buyer&gt;'s Cottage" from the current name), our last-owner
    /// name comes off, so the buyer's name is put on the plain one.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetHouseOwner))]
    static class DarktideFeelOnPurchase
    {
        static void Prefix(SlumLord slumlord)
        {
            try
            {
                DarktideFeel.ClearSign(slumlord, owned: true);   // LAST_OWNER stays: the stash note (and a basement chest) still read it
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] DarktideFeel purchase: {e.Message}"); }
        }
    }
}
