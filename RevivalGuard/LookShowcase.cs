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
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.TryEquipObject))]
    public static class LookShowcase
    {
        internal const string P_ON = "rg_look_showcase";
        sealed class Fx { public uint Script; public bool OnItem; public double Every; }
        static readonly Dictionary<uint, Fx> s_Fx = new Dictionary<uint, Fx>();
        static readonly ConcurrentDictionary<uint, WeakReference<Creature>> s_Wearers = new ConcurrentDictionary<uint, WeakReference<Creature>>();
        static readonly Dictionary<(uint, uint), DateTime> s_Next = new Dictionary<(uint, uint), DateTime>();   // world thread only
        static Timer s_Timer;
        static bool s_WasOn;

        internal static void Register()
        {
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard LookShowcase: the Staff Base look showcase's tables (0x1000F200+) and effects (staff review; test shard only)");
            try
            {
                using var rs = typeof(LookShowcase).Assembly.GetManifestResourceStream("RevivalGuard.LookShowcase.json");
                using var doc = JsonDocument.Parse(rs);
                foreach (var e in doc.RootElement.GetProperty("items").EnumerateObject())
                {
                    var v = e.Value;
                    s_Fx[uint.Parse(e.Name)] = new Fx
                    {
                        Script = CustomClothingRule.Num(v.GetProperty("script")),
                        OnItem = v.GetProperty("on").GetString() == "item",
                        Every = v.TryGetProperty("every", out var ev) ? Math.Max(1.0, ev.GetDouble()) : 0,
                    };
                }
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: effects not loaded: " + e.Message); }
            s_Timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
            Mod.Log.Info($"[RevivalGuard] LookShowcase: {s_Fx.Count} effect(s) ({P_ON}, default off)");
        }

        static void Prefix(WorldObject worldObject)
        {
            try
            {
                if (worldObject != null && s_Fx.TryGetValue(worldObject.WeenieClassId, out var fx) && fx.OnItem)
                    worldObject.DefaultScriptId = PkCommon.On(P_ON) ? fx.Script : (uint?)null;
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
                if (fx.OnItem && __instance is Player p && PkCommon.On(P_ON)) p.Session?.Network.EnqueueSend(new GameMessageUpdateObject(worldObject));
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }

        static void Tick()
        {
            try
            {
                if (s_Fx.Count == 0) return;
                bool on = PkCommon.On(P_ON);
                if (!on && !s_WasOn) return;
                s_WasOn = on;
                WorldManager.EnqueueAction(new ActionEventDelegate(() => Apply(on)));
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }

        /// <summary>World thread: every wearer's showcase items, scripts set or cleared, auras re-played when due.</summary>
        static void Apply(bool on)
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
                        if (!s_Fx.TryGetValue(item.WeenieClassId, out var fx)) continue;
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
                if (!on) s_Next.Clear();
            }
            catch (Exception e) { Mod.Log.Warn("[RevivalGuard] LookShowcase: " + e.Message); }
        }
    }
}
