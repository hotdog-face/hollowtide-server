using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Your fellows' damage, for the floating numbers.</summary>
    [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
    static class FellowDamage
    {
        const float RANGE = 60f;

        // Runs on landblock threads, where an escaped exception stops the world loop
        static void Postfix(DamageHistory __instance, WorldObject attacker, DamageType damageType, uint amount)
        {
            try { Share(__instance, attacker, damageType, amount); }
            catch (System.Exception e) { TickGuard.Report("FellowDamage", e); }
        }

        static void Share(DamageHistory __instance, WorldObject attacker, DamageType damageType, uint amount)
        {
            if (amount == 0 || !(attacker is Player p) || p.Fellowship == null) return;
            var target = __instance.Creature;
            if (target == null || target is Player || target.Location == null) return;
            string json = null;
            foreach (var f in p.Fellowship.GetFellowshipMembers().Values)
            {
                if (f == null || f == p || f.Session == null || !ModChannel.Capable(f.Session)) continue;
                if (f.Location == null || f.Location.DistanceTo(target.Location) > RANGE) continue;
                json ??= "{\"a\":" + p.Guid.Full + ",\"t\":" + target.Guid.Full + ",\"n\":" + amount + ",\"k\":" + (uint)damageType + "}";
                ModChannel.Send(f.Session, "fdmg", json);
            }
        }
    }
}
