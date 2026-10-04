using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// YOUR FELLOWS' DAMAGE, FOR THE FLOATING NUMBERS (owner, 2026-09-25: "I think seeing damage done
    /// by your fellowship would be an amazing addition"). Ours. ACE tells only the attacker about a
    /// hit (GameEventAttackerNotification), so a client has no way to see anyone else's numbers.
    ///
    /// Every damage a creature records passes through DamageHistory.Add: melee, missiles, war spells
    /// and damage over time alike. When the attacker is a player in a fellowship, each other member
    /// whose client reads the mod channel, and who is within 60 m of the target, gets "fdmg":
    /// {"a":attacker,"t":target,"n":amount,"k":damageType}. Only damage to non-players is shared.
    /// Retail clients never receive it.
    /// </summary>
    [HarmonyPatch(typeof(DamageHistory), nameof(DamageHistory.Add))]
    static class FellowDamage
    {
        const float RANGE = 60f;

        static void Postfix(DamageHistory __instance, WorldObject attacker, DamageType damageType, uint amount)
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
