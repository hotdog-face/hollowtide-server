using System.Collections.ObjectModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// RETAIL'S DEATH-TO-RESPAWN DELAY. ACE's Player.Die waits the Dead animation plus one more second
    /// before the corpse and the death teleport (Player_Death.cs: AddDelaySeconds(animLength + 1.0f)).
    /// Retail did not: in pkt_2017-1-30_1485769877 Health = 0 reaches the client 1.09 s and 1.17 s before
    /// PlayerTeleport, the Dead animation's own length (1.067 s, 0x030000C6) plus a tick. Audit round 2
    /// (docs/findings/accuracy-2026-10-20/round2/session-death-travel.md item 3).
    ///
    /// One transpiler anchor: the first "ldc.r4 1" followed by "add" within six instructions after the call
    /// to GetAnimationLength in Player.Die. It becomes Extra(), 0 with the switch on and 1.0 with it off.
    /// A missing anchor is logged and ACE's delay stays. Switch `retail_death_delay`, default on.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Die", new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) })]
    static class RetailDeathDelay
    {
        const string P_ON = "retail_death_delay";
        const float ACE_EXTRA = 1.0f;
        static int s_Registered;
        internal static bool Hooked;

        static bool On => PropertyManager.GetBool(P_ON, true).Item;

        /// <summary>The seconds added after the Dead animation.</summary>
        public static float Extra() => On ? 0f : ACE_EXTRA;

        static bool Prepare()
        {
            if (Interlocked.Exchange(ref s_Registered, 1) == 0)
            {
                try
                {
                    var inner = typeof(ReadOnlyDictionary<string, Property<bool>>)
                        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                        .Select(f => f.GetValue(DefaultPropertyManager.DefaultBooleanProperties))
                        .OfType<IDictionary<string, Property<bool>>>().FirstOrDefault();
                    if (inner != null && !inner.ContainsKey(P_ON))
                        inner[P_ON] = new Property<bool>(true, "RetailDeathDelay: the death teleport follows the Dead animation with no extra second (retail); off = ACE's +1.0 s");
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] RetailDeathDelay: switch not registered with @modifybool ({e.Message}); it reads its default (on)"); }
            }
            return true;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            try
            {
                int at = -1;
                for (int i = 0; i < code.Count && at < 0; i++)
                {
                    if (!(code[i].operand is MethodInfo mi && mi.Name == "GetAnimationLength")) continue;
                    for (int j = i + 1; j < Math.Min(code.Count - 1, i + 7); j++)
                        if (code[j].opcode == OpCodes.Ldc_R4 && Convert.ToSingle(code[j].operand) == ACE_EXTRA && code[j + 1].opcode == OpCodes.Add)
                        { at = j; break; }
                }
                Hooked = at >= 0;
                if (!Hooked)
                {
                    Mod.Log.Error($"[RevivalGuard] RetailDeathDelay: no 'GetAnimationLength ... ldc.r4 1; add' in {original?.Name}; deaths keep ACE's +1.0 s");
                    return code;
                }
                var call = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RetailDeathDelay), nameof(Extra)));
                call.labels.AddRange(code[at].labels);
                call.blocks.AddRange(code[at].blocks);
                code[at] = call;
                Mod.Log.Info($"[RevivalGuard] RetailDeathDelay: death teleport after the Dead animation alone ({P_ON}, default on)");
                return code;
            }
            catch (Exception e)
            {
                Hooked = false;
                Mod.Log.Error($"[RevivalGuard] RetailDeathDelay: {e.GetType().Name} rewriting {original?.Name}: {e.Message}; deaths keep ACE's +1.0 s");
                return code;
            }
        }
    }
}
