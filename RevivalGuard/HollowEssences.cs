using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using ACE.Common;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables.Wcids;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using HarmonyLib;
using WeenieClassName = ACE.Server.Factories.Enum.WeenieClassName;

namespace RevivalGuard
{
    /// <summary>
    /// HOLLOWTIDE ESSENCES IN THE NORMAL ESSENCE LOOT (owner 2026-11-24, docs/proposals/summoning-plan.md; the pure rule
    /// is HollowEssenceRule.cs). A postfix on ACE's PetDeviceWcids.Roll(TreasureDeath), the one place loot picks an
    /// essence: with rg_hollow_essences on, about 1 in 10 rolls at a level we cover (every retail level, 50 to 200) becomes
    /// one of our families' essences of that same level (Marionette, Drudge, Olthoi ...; HollowEssenceRule.Essences),
    /// picked evenly, so any creature of the right tier can drop one, like retail's. The level comes from
    /// ACE's own tables (petDevices, petLevelIndexes), read once. Ours are also added to the set behind
    /// PetDeviceWcids.Contains, so LootGenerationFactory.MutateItem (@lootgen) rolls their ratings like retail's; the main
    /// loot path already does (TreasureRoll.ItemType is PetDevice). Off: ACE as is.
    ///   @hollowessences [tier] [rolls]   switch, counters; with a tier, rolls that many essences through ACE's RollWcid
    /// </summary>
    [HarmonyPatch]
    static class HollowEssences
    {
        internal const string P_ON = "rg_hollow_essences";

        internal static bool On
        {
            get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } }
        }

        static Dictionary<WeenieClassName, int> s_Level;   // retail essence -> its pet level
        static long s_Rolls, s_Swaps;

        static MethodBase TargetMethod()
        {
            var m = AccessTools.DeclaredMethod(typeof(PetDeviceWcids), nameof(PetDeviceWcids.Roll), new[] { typeof(TreasureDeath) });
            if (m == null) Mod.Log.Error("[RevivalGuard] HollowEssences: PetDeviceWcids.Roll(TreasureDeath) not found; essence loot stays ACE's");
            return m;
        }

        static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            if (TargetMethod() == null) return false;
            try { s_Level = ReadLevels(); }
            catch (Exception e) { s_Level = null; Mod.Log.Error($"[RevivalGuard] HollowEssences: ACE's pet tables unreadable ({e.Message}); essence loot stays ACE's"); return false; }
            int added = AddToContains();
            PkCommon.RegisterBool(P_ON, false, "RevivalGuard HollowEssences: about 1 in 10 essence drops become a Hollowtide family essence (Marionette, Drudge, Olthoi ...) of the same level (false: ACE's 36 retail families only)");
            CommandManager.TryAddCommand(Command, "hollowessences", AccessLevel.Admin, CommandHandlerFlag.None,
                "Hollowtide essences in essence loot: switch, counters; with a tier, roll essences through ACE's loot to check the share.", "[tier 1-8] [rolls]");
            Mod.Log.Info($"[RevivalGuard] HollowEssences: {s_Level.Count} retail essences by level, {added} of ours in ACE's essence set ({P_ON}, @hollowessences)");
            return true;
        }

        /// <summary>ACE's petDevices (36 lists, one per family, index = level slot) and petLevelIndexes (level -> slot).</summary>
        static Dictionary<WeenieClassName, int> ReadLevels()
        {
            var lists = AccessTools.Field(typeof(PetDeviceWcids), "petDevices")?.GetValue(null) as List<List<WeenieClassName>>
                ?? throw new MissingFieldException("PetDeviceWcids.petDevices");
            var idx = AccessTools.Field(typeof(PetDeviceWcids), "petLevelIndexes")?.GetValue(null) as Dictionary<int, int>
                ?? throw new MissingFieldException("PetDeviceWcids.petLevelIndexes");
            var d = new Dictionary<WeenieClassName, int>();
            foreach (var family in lists)
                foreach (var kv in idx)
                    if (kv.Value >= 0 && kv.Value < family.Count) d[family[kv.Value]] = kv.Key;
            if (d.Count == 0) throw new InvalidOperationException("no essences read");
            return d;
        }

        /// <summary>PetDeviceWcids.Contains reads one private static HashSet; ours go in it (a set, not a patch on a
        /// one-line method the JIT may inline).</summary>
        static int AddToContains()
        {
            try
            {
                var set = AccessTools.Field(typeof(PetDeviceWcids), "_combined")?.GetValue(null) as HashSet<WeenieClassName>;
                if (set == null) { Mod.Log.Warn("[RevivalGuard] HollowEssences: PetDeviceWcids._combined not found; @lootgen of ours skips ratings"); return 0; }
                int n = 0;
                foreach (var w in HollowEssenceRule.All) if (set.Add((WeenieClassName)w)) n++;
                return n;
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] HollowEssences: essence set not extended ({e.Message})"); return 0; }
        }

        static void Postfix(ref WeenieClassName __result)
        {
            try
            {
                if (!On || s_Level == null) return;
                Interlocked.Increment(ref s_Rolls);
                if (!s_Level.TryGetValue(__result, out var level)) return;
                uint ours = HollowEssenceRule.Pick(level, ThreadSafeRandom.Next(0.0f, 1.0f), ThreadSafeRandom.Next(0.0f, 1.0f));
                if (ours == 0) return;
                __result = (WeenieClassName)ours;
                Interlocked.Increment(ref s_Swaps);
            }
            catch { }   // never cost a drop: ACE's pick stands
        }

        static void Say(Session s, string text)
        {
            if (s == null) Console.WriteLine(text);
            else s.Network?.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        static void Command(Session session, params string[] p)
        {
            Say(session, $"HollowEssences: {P_ON} {(On ? "ON" : "off")}; since start {Interlocked.Read(ref s_Swaps)} of {Interlocked.Read(ref s_Rolls)} essence rolls became ours.");
            if (p == null || p.Length == 0 || !int.TryParse(p[0], out int tier)) return;
            if (tier < 1 || tier > 8) { Say(session, "Tier is 1 to 8."); return; }
            int n = p.Length > 1 && int.TryParse(p[1], out var k) ? Math.Clamp(k, 1, 100000) : 10000;
            var roll = AccessTools.Method(typeof(LootGenerationFactory), "RollWcid");
            if (roll == null) { Say(session, "LootGenerationFactory.RollWcid not found."); return; }
            var td = new TreasureDeath { Tier = tier, LootQualityMod = 0 };
            var all = new SortedDictionary<int, int>();
            var mine = new SortedDictionary<int, int>();
            var fams = new SortedDictionary<string, int>();
            int unknown = 0;
            for (int i = 0; i < n; i++)
            {
                var tr = roll.Invoke(null, new object[] { td, TreasureItemCategory.Item, TreasureItemType.PetDevice });
                var w = (WeenieClassName)AccessTools.Field(tr.GetType(), "Wcid").GetValue(tr);
                int lvl = HollowEssenceRule.LevelOf((uint)w);
                bool isOurs = lvl != 0;
                if (!isOurs && (s_Level == null || !s_Level.TryGetValue(w, out lvl))) { unknown++; continue; }
                all[lvl] = all.GetValueOrDefault(lvl) + 1;
                if (isOurs)
                {
                    mine[lvl] = mine.GetValueOrDefault(lvl) + 1;
                    var key = $"{HollowEssenceRule.FamilyOf((uint)w)} {lvl}";
                    fams[key] = fams.GetValueOrDefault(key) + 1;
                }
            }
            var sb = new StringBuilder($"Tier {tier}, {n} essence rolls through ACE's RollWcid ({(On ? "switch ON" : "switch off")}): ");
            sb.Append(string.Join(", ", all.Select(kv => $"level {kv.Key}: {kv.Value} ({mine.GetValueOrDefault(kv.Key)} ours, {100.0 * mine.GetValueOrDefault(kv.Key) / kv.Value:F1}%)")));
            sb.Append($"; ours in all {mine.Values.Sum()} ({100.0 * mine.Values.Sum() / n:F1}%)");
            if (unknown > 0) sb.Append($"; {unknown} not essences");
            Say(session, sb.ToString());
            if (fams.Count > 0) Say(session, "Ours by family: " + string.Join(", ", fams.Select(kv => $"{kv.Key}: {kv.Value}")));
        }
    }
}
