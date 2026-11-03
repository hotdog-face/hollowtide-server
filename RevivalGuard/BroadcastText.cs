using System.Reflection;
using System.Reflection.Emit;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// RETAIL'S WORDING ON FEAT BROADCASTS (owner 2026-11-02: "bring back retail's server-wide announcements of player
    /// feats"; inventory in docs/findings/feat-broadcasts-2026-11-02.md). ACE already sends every retail feat line the
    /// world data holds (Gauntlet and Colosseum Champion, the "Attention, all!" titles, Queenslayer, boss kills) and the
    /// server-wide PK kill line; two of them print differently from retail's screenshots:
    ///   - the PK kill line says "The kill occured at"; retail "The kill occurred at" (BroadcastTextRule.RetailKillAt);
    ///   - Aerbax's Shadow's "%tn 's Fellowship has defeated Aerbax's Shadow!" prints "X 's"; retail "X's".
    ///   @modifybool rg_broadcast_retail_text false   ACE's text for both
    /// </summary>
    static class BroadcastText
    {
        internal const string P_ON = "rg_broadcast_retail_text";
        internal static bool KillLineHooked;

        internal static bool On => PropertyManager.GetBool(P_ON, true).Item;

        static bool s_Registered;
        internal static void Register()
        {
            if (s_Registered) return;
            s_Registered = true;
            try
            {
                var b = MonsterAi.Inner(DefaultPropertyManager.DefaultBooleanProperties);
                if (!b.ContainsKey(P_ON)) b[P_ON] = new Property<bool>(true, "RevivalGuard BroadcastText: the PK kill and fellowship boss lines in retail's wording (false: ACE's text)");
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BroadcastText: switch not registered with @modifybool ({e.Message}); still read at its default"); }
        }

        /// <summary>Called in place of ACE's ldstr " The kill occured at ".</summary>
        public static string KillAt() => On ? BroadcastTextRule.RetailKillAt : BroadcastTextRule.AceKillAt;

        [HarmonyPatch(typeof(Player), nameof(Player.HandlePKDeathBroadcast))]
        static class KillLine
        {
            static bool Prepare() { Register(); return true; }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                foreach (var c in code)
                {
                    if (c.opcode != OpCodes.Ldstr || !(c.operand is string s) || s != BroadcastTextRule.AceKillAt) continue;
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(BroadcastText), nameof(KillAt));
                    KillLineHooked = true;
                }
                if (KillLineHooked) Mod.Log.Info($"[RevivalGuard] BroadcastText: PK kill line says \"occurred\" as retail ({P_ON})");
                else Mod.Log.Error("[RevivalGuard] BroadcastText: no \" The kill occured at \" in HandlePKDeathBroadcast; the PK line keeps ACE's text");
                return code;
            }
        }

        [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.Replace))]
        static class Possessive
        {
            static bool Prepare() { Register(); return true; }

            static void Prefix(ref string message)
            {
                try { if (message != null && On) message = BroadcastTextRule.JoinPossessive(message); }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] BroadcastText: {e.GetType().Name} ({e.Message}); emote text left as ACE's"); }
            }
        }
    }
}
