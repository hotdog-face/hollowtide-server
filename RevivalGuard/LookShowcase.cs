using System.Collections.Concurrent;
using System.Text.Json;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE LOOK SHOWCASE'S EFFECTS (owner 2026-11-24, after the rune-skin suits: "stage a bunch in the base for me to review";
    /// docs/findings/look-showcase-2026-11-24.md). Retail PhysicsScripts where retail never put them, named per weenie in
    /// LookShowcase.json (embedded; tools/gpubox-ace/gen_look_showcase.py):
    ///   on=item    the wielded item's DefaultScript is that 0x33 script while wielded (as BorgShield): our client's
    ///              AcWielded plays a 0x33 value in place of the setup's own effect. Set at equip, and on each tick for a
    ///              wielder who logged in with it (equip at login does not go through TryEquipObject).
    ///   on=wearer  the script is re-played on the wearer every N s while worn (a finite retail script: an Aetheria surge,
    ///              Black Madness). PlayEffect with a 0x33 id; ServerLink.PlayEffectOn plays it as is, a retail client
    ///              looks it up as a type and plays nothing.
    /// The showcase's looks (ClothingTables 0x1000F200-0x1000F2FF) are CustomClothing's, behind the same switch.
    /// Switch rg_look_showcase, default OFF: nothing is set or played, a set item script is cleared on the next tick.
    /// Wearers are online players and any creature seen equipping a showcase item (the Staff Base mannequins).
    /// LOOK LAB ITEMS (docs/proposals/look-lab.md): LookLab.json (embedded; tools/looklab/package.py) names the packaged
    /// looks' effects the same way, behind their own switch rg_look_lab (default OFF; the package's datafix turns it on).
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TryEquipObject))]
    public static class LookShowcase
    {
        internal const string P_ON = "rg_look_showcase", P_LAB = "rg_look_lab";
        sealed class Fx { public uint Script; public bool OnItem, Lab; public double Every; }
        static readonly Dictionary<uint, Fx> s_Fx = new Dictionary<uint, Fx>();
        static readonly ConcurrentDictionary<uint, WeakReference<Creature>> s_Wearers = new ConcurrentDictionary<uint, WeakReference<Creature>>();
        static readonly Dictionary<(uint, uint), DateTime> s_Next = new Dictionary<(uint, uint), DateTime>();   // world thread only
        static Timer s_Timer;
        static bool s_WasOn;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard LookShowcase: the Staff Base look showcase's tables (0x1000F200+) and effects (staff review; test shard only)");
            PkCommon.RegisterBool(P_LAB, false, "RevivalGuard LookShowcase: the Look Lab's packaged items' effects (LookLab.json, tools/looklab/package.py)");
            Load("RevivalGuard.LookShowcase.json", false);
            Load("RevivalGuard.LookLab.json", true);
            s_Timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
            Mod.Log.Info($"[RevivalGuard] LookShowcase: {s_Fx.Count} effect(s) ({P_ON}, default off)");
        }

        static void Load(string res, bool lab)
        {
            try
            {
                using var rs = typeof(LookShowcase).Assembly.GetManifestResourceStream(res);
                if (rs == null) return;
                using var doc = JsonDocument.Parse(rs);
                foreach (var e in doc.RootElement.GetProperty("items").EnumerateObject())
                {
                    var v = e.Value;
                    s_Fx[uint.Parse(e.Name)] = new Fx
                    {
                        Script = CustomClothingRule.Num(v.GetProperty("script")),
                        OnItem = v.GetProperty("on").GetString() == "item",
                        Every = v.TryGetProperty("every", out var ev) ? Math.Max(1.0, ev.GetDouble()) : 0,
                        Lab = lab,
                    };
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] LookShowcase: {res} not loaded: " + e.Message); }
        }

        static bool On(Fx fx) => PkCommon.On(fx.Lab ? P_LAB : P_ON);

        static void Prefix(WorldObject worldObject)
        {
            try
            {
                if (worldObject != null && s_Fx.TryGetValue(worldObject.WeenieClassId, out var fx) && fx.OnItem)
                    worldObject.DefaultScriptId = On(fx) ? fx.Script : (uint?)null;
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }

        static void Postfix(Creature __instance, WorldObject worldObject, bool __result)
        {
            try
            {
                if (!__result || worldObject == null || !s_Fx.TryGetValue(worldObject.WeenieClassId, out var fx)) return;
                s_Wearers[__instance.Guid.Full] = new WeakReference<Creature>(__instance);
                // the wielder's own client already holds the item: tell it the script (observers get it with the wield)
                if (fx.OnItem && __instance is Player p && On(fx)) p.Session?.Network.EnqueueSend(new GameMessageUpdateObject(worldObject));
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }

        static void Tick()
        {
            try
            {
                bool on = PkCommon.On(P_ON) || PkCommon.On(P_LAB) || PkCommon.On(LookLabMake.P_ON);
                if (!on && !s_WasOn) return;
                s_WasOn = on;
                WorldManager.EnqueueAction(new ActionEventDelegate(() => Apply()));
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }

        /// <summary>World thread: every wearer's showcase items, scripts set or cleared, auras re-played when due.</summary>
        static void Apply()
        {
            try
            {
                var now = DateTime.UtcNow;
                var wearers = new List<Creature>();
                foreach (var p in PlayerManager.GetAllOnline()) wearers.Add(p);
                foreach (var (guid, wr) in s_Wearers)
                {
                    if (!wr.TryGetTarget(out var c) || c.IsDestroyed) { s_Wearers.TryRemove(guid, out _); continue; }
                    if (!(c is Player)) wearers.Add(c);
                }
                foreach (var c in wearers)
                {
                    foreach (var item in c.EquippedObjects.Values.ToList())
                    {
                        if (!s_Fx.TryGetValue(item.WeenieClassId, out var fx))
                        {
                            // a Look Lab "Make it now" item worn: its own DefaultScript re-played on the wearer (LookLabMake.P_EVERY)
                            double every = item.GetProperty(LookLabMake.P_EVERY) ?? 0;
                            uint ds = item.DefaultScriptId ?? 0;
                            if (every <= 0 || ds >> 24 != 0x33 || !PkCommon.On(LookLabMake.P_ON)) continue;
                            var mk = (c.Guid.Full, item.Guid.Full);
                            if (s_Next.TryGetValue(mk, out var mdue) && now < mdue) continue;
                            s_Next[mk] = now.AddSeconds(every);
                            c.EnqueueBroadcast(new GameMessageScript(c.Guid, (PlayScript)ds));
                            continue;
                        }
                        bool on = On(fx);
                        if (fx.OnItem)
                        {
                            uint? want = on ? fx.Script : (uint?)null;
                            if (item.DefaultScriptId == want) continue;
                            item.DefaultScriptId = want;
                            c.EnqueueBroadcast(new GameMessageUpdateObject(item));
                        }
                        else if (on)
                        {
                            var key = (c.Guid.Full, item.WeenieClassId);
                            if (s_Next.TryGetValue(key, out var due) && now < due) continue;
                            s_Next[key] = now.AddSeconds(fx.Every);
                            c.EnqueueBroadcast(new GameMessageScript(c.Guid, (PlayScript)fx.Script));
                        }
                    }
                }
                if (!PkCommon.On(P_ON) && !PkCommon.On(P_LAB) && !PkCommon.On(LookLabMake.P_ON)) s_Next.Clear();
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }
    }
}
