using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Small hitbox.</summary>
    [HarmonyPatch]
    static class SmallHitbox
    {
        internal const string P_ON = "azimar_small_hitbox";

        // weenie class id -> collision radius scale
        static readonly Dictionary<uint, float> s_Scale = new()
        {
            { 901631, 0.2f },   // Azimar the Gleaming: spheres r 0.5 x 1.8 = 0.9 -> 0.18 m
            // his Hotel Swank review copies, one per festival look (AzimarThemeRule.Fixed)
            { 901633, 0.2f }, { 901634, 0.2f }, { 901635, 0.2f }, { 901636, 0.2f }, { 901637, 0.2f },
        };

        static readonly ConditionalWeakTable<PhysicsObj, Setup> s_Original = new();
        static readonly MethodInfo s_Clone = AccessTools.Method(typeof(object), "MemberwiseClone");

        static IEnumerable<MethodBase> TargetMethods()
        {
            var a = AccessTools.DeclaredMethod(typeof(WorldObject), nameof(WorldObject.SyncLocation), Type.EmptyTypes);
            var b = AccessTools.DeclaredMethod(typeof(Creature), nameof(Creature.UpdatePosition_SyncLocation), Type.EmptyTypes);
            if (a == null) Mod.Log.Error("[RevivalGuard] SmallHitbox: WorldObject.SyncLocation() not found; skipped");
            if (b == null) Mod.Log.Error("[RevivalGuard] SmallHitbox: Creature.UpdatePosition_SyncLocation() not found; skipped");
            if (a != null) yield return a;
            if (b != null) yield return b;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (!TargetMethods().Any()) return false;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard SmallHitbox: Azimar collides at his body, not his scaled spheres");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SmallHitbox: switch not registered with @modifybool ({e.Message}); still read at its default"); }
            Mod.Log.Info($"[RevivalGuard] SmallHitbox: {s_Scale.Count} weenie(s) with smaller collision ({P_ON})");
            return true;
        }

        static bool On => PropertyManager.GetBool(P_ON, true).Item;

        static void Postfix(WorldObject __instance)
        {
            try
            {
                if (__instance == null || !s_Scale.TryGetValue(__instance.WeenieClassId, out float k)) return;
                var pa = __instance.PhysicsObj?.PartArray;
                if (pa?.Setup == null) return;
                var po = __instance.PhysicsObj;
                bool shrunk = s_Original.TryGetValue(po, out var orig);
                if (On && !shrunk)
                {
                    var st = pa.Setup;
                    var c = (Setup)s_Clone.Invoke(st, null);
                    c.Sphere = st.Sphere?.Select(s => new Sphere(s.Center, s.Radius * k)).ToList();
                    c.CylSphere = st.CylSphere?.Select(s => new CylSphere(s.LowPoint, s.Height, s.Radius * k)).ToList();
                    s_Original.Add(po, st);
                    pa.Setup = c;
                    Mod.Log.Info($"[RevivalGuard] SmallHitbox: {__instance.Name} 0x{__instance.Guid.Full:X8} collision radii x{k} ({st.Sphere?.Count ?? 0} spheres, {st.CylSphere?.Count ?? 0} cylinders)");
                }
                else if (!On && shrunk)
                {
                    pa.Setup = orig;
                    s_Original.Remove(po);
                    Mod.Log.Info($"[RevivalGuard] SmallHitbox: {__instance.Name} 0x{__instance.Guid.Full:X8} retail collision again");
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SmallHitbox: {e.Message}"); }
        }

        /// <summary>After the mod channel's hello: which weenies collide smaller, so our client agrees ("" when off).</summary>
        internal static void Hello(Session session)
        {
            try
            {
                string payload = On ? string.Join(" ", s_Scale.Select(kv => kv.Key + ":" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))) : "";
                ModChannel.Send(session, "collide", payload);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] SmallHitbox hello: {e.Message}"); }
        }
    }
}
