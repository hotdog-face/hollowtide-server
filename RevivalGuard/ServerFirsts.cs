using System.Reflection;
using System.Text.Json;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// BRONZE STATUE SERVER-FIRSTS (owner-approved 2026-10-20, docs/HALF-FINISHED-CONTENT.md item 7). Retail's 2002
    /// "Bronze Statues Attack" event: whoever destroyed a statue type's native cast got a Unique Bronze part, which Bhab,
    /// Arcanum Tinker (19354, the Consortium near Xarabydun) turns into a Commemorative Bronze Statue if his
    /// Event...Killer flag says it was you; one per server. ACE has the Tinker, the parts and the statues, but nothing sets
    /// the flags or makes the parts.
    ///
    /// OURS: the FIRST player on this server to kill each Bronze Statue creature type (17 types, as the Tinker's data has
    /// them) gets that type's flag and Unique part (WorldBossSchedule.Grant: a full pack keeps it owed), and the world
    /// hears it. The claim is written to ServerFirsts.json beside the dll BEFORE anything is given, so a type is only
    /// ever claimed once, across restarts and mod updates (the file is not part of a deploy). A kill with no online
    /// player behind it claims nothing. Players: @serverfirsts lists who holds which.
    /// </summary>
    static class ServerFirsts
    {
        internal sealed class Spec { public string Key, Label, Flag; public uint Part; }

        // creature wcid -> the Tinker's flag and the Unique part his Give emote for that flag takes (ace_world 19354)
        internal static readonly Dictionary<uint, Spec> Specs = new Dictionary<uint, Spec>
        {
            [19288] = S("Drudge", "a Bronze Statue of a Drudge", "EventDrudgeKiller", 19734),
            [19294] = S("Mosswart", "a Bronze Statue of a Mosswart", "EventMosswartKiller", 19736),
            [19297] = S("Reedshark", "a Bronze Statue of a Reedshark", "EventReedsharkKiller", 19737),
            [19291] = S("Gromnie", "a Bronze Statue of a Gromnie", "EventGromnieKiller", 19735),
            [19300] = S("Banderling", "a Bronze Statue of a Banderling", "EventBanderlingKiller", 19728),
            [19303] = S("BenTen", "the Bronze Statue of Ben Ten", "EventBenTenKiller", 19729),
            [19312] = S("Thorsten", "the Bronze Statue of Thorsten Cragstone", "EventThorstenKiller", 19732),
            [19315] = S("Zharalim", "a Bronze Statue of a Zharalim", "EventZharalimKiller", 19733),
            [19306] = S("MidSclavus", "a Bronze Statue of a Sclavus", "EventMidSclavusKiller", 19730),
            [19309] = S("MidSkeleton", "a Bronze Statue of a Skeleton", "EventMidSkeletonKiller", 19731),
            [19276] = S("Golem", "a Bronze Statue of a Golem", "EventGolemKiller", 19758),
            [19285] = S("Tumerok", "a Bronze Statue of a Tumerok", "EventTumerokKiller", 19761),
            [19279] = S("HighSclavus", "a great Bronze Statue of a Sclavus", "EventHighSclavusKiller", 19759),
            [19282] = S("HighSkeleton", "a great Bronze Statue of a Skeleton", "EventHighSkeletonKiller", 19760),
            [19267] = S("Grievver", "a Bronze Statue of a Grievver", "EventGrievverKiller", 19755),
            [19270] = S("Shadow", "a Bronze Statue of a Shadow", "EventShadowKiller", 19756),
            [19273] = S("Virindi", "a Bronze Statue of a Virindi", "EventVirindiKiller", 19757),
        };
        static Spec S(string key, string label, string flag, uint part) => new Spec { Key = key, Label = label, Flag = flag, Part = part };

        sealed class Claim { public string Name { get; set; } public uint Guid { get; set; } public double At { get; set; } }

        const string STATE_FILE = "ServerFirsts.json";
        static readonly object s_Lock = new object();
        static Dictionary<string, Claim> s_Claims;
        static readonly HashSet<uint> s_Seen = new HashSet<uint>();

        internal static void Register()
        {
            lock (s_Lock) Load();
            CommandManager.TryAddCommand(ListCmd, "serverfirsts", AccessLevel.Player, CommandHandlerFlag.None,
                "Who on this server first destroyed each kind of Bronze Statue.", "");
            Mod.Log.Info($"[RevivalGuard] ServerFirsts: {Specs.Count} bronze statue types, {s_Claims.Count} claimed (@serverfirsts)");
        }

        internal static void OnDeath(Creature dead, DamageHistoryInfo lastDamager)
        {
            if (dead == null || !Specs.TryGetValue(dead.WeenieClassId, out var s)) return;
            lock (s_Seen) { if (!s_Seen.Add(dead.Guid.Full)) return; }   // ACE may enter OnDeath twice
            var killer = (lastDamager ?? dead.DamageHistory?.LastDamager)?.TryGetPetOwnerOrAttacker() as Player;
            if (killer == null || PlayerManager.GetOnlinePlayer(killer.Guid.Full) == null) return;

            lock (s_Lock)
            {
                Load();
                if (s_Claims.ContainsKey(s.Key)) return;
                s_Claims[s.Key] = new Claim { Name = killer.Name, Guid = killer.Guid.Full, At = ACE.Common.Time.GetUnixTime() };
                if (!Save()) { s_Claims.Remove(s.Key); return; }   // never give what could be given twice
            }
            killer.QuestManager.Stamp(s.Flag);
            WorldBossSchedule.Grant(killer.Guid.Full, s.Part, $"for being the first on this server to destroy {s.Label}");
            killer.SendMessage($"You are the first in all of Dereth to destroy {s.Label}! Take the Unique Bronze part to Bhab, "
                + "Arcanum Tinker, in the Consortium near Xarabydun. The statue he makes weighs 5,000; if you cannot carry "
                + "it, he sets it down at your feet.", ChatMessageType.Broadcast);
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(
                $"{killer.Name} is the first in Dereth to destroy {s.Label}! The Arcanum Tinker near Xarabydun will cast a "
                + "commemorative statue for this deed, and only one will ever be made.", ChatMessageType.WorldBroadcast));
            Mod.Log.Info($"[RevivalGuard] ServerFirsts: {s.Key} first claimed by {killer.Name} 0x{killer.Guid.Full:X8} ({s.Flag}, part {s.Part})");
        }

        static void ListCmd(Session session, params string[] parameters)
        {
            List<string> lines;
            lock (s_Lock)
            {
                Load();
                lines = Specs.Values.Select(s => s_Claims.TryGetValue(s.Key, out var c)
                    ? $"  {Cap(s.Label)}: {c.Name}" : $"  {Cap(s.Label)}: not yet destroyed").ToList();
            }
            var text = $"Bronze Statue server-firsts ({lines.Count(l => !l.EndsWith("not yet destroyed"))} of {Specs.Count} claimed):\n" + string.Join("\n", lines);
            if (session?.Network != null) session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            else Console.WriteLine(text);
        }

        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string StatePath()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) folder = Path.Combine(ModManager.ModPath, "RevivalGuard");
            return Path.Combine(folder, STATE_FILE);
        }

        static void Load()   // under s_Lock; once (the file only changes through Save)
        {
            if (s_Claims != null) return;
            s_Claims = new Dictionary<string, Claim>(StringComparer.OrdinalIgnoreCase);
            var path = StatePath();
            try
            {
                if (File.Exists(path))
                    s_Claims = JsonSerializer.Deserialize<Dictionary<string, Claim>>(File.ReadAllText(path))
                        is { } d ? new Dictionary<string, Claim>(d, StringComparer.OrdinalIgnoreCase) : s_Claims;
            }
            catch (Exception e)
            {
                // An unreadable file must not reopen claimed types: refuse every claim until it is fixed by hand.
                Mod.Log.Error($"[RevivalGuard] ServerFirsts: could not read {path}: {e.Message}; no new claims until it is fixed");
                foreach (var s in Specs.Values) s_Claims[s.Key] = new Claim { Name = "(unreadable state)" };
            }
        }

        static bool Save()   // under s_Lock
        {
            var path = StatePath();
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s_Claims, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ServerFirsts: could not write {path}: {e.Message}; claim not given"); return false; }
        }
    }

    /// <summary>
    /// The Tinker's statue never lost (owner 2026-10-20). ACE's Player.GiveFromEmote returns early with only "tries to give
    /// you" when ItemsToReceive says the pack is full or too heavy, and TryCreateForGive drops the item on a failed add:
    /// either way the part is already taken. For Bhab (19354) giving a Commemorative Bronze Statue (19738-19754) this
    /// prefix does the give itself: ACE's own TryCreateForGive first (same pack rule and messages), and if that fails the
    /// statue goes on the ground at the player's feet (ACE's drop path), never rotting, saved at once. Exactly one statue
    /// per call; every other emote give runs ACE's code untouched.
    /// </summary>
    [HarmonyPatch]
    static class ServerFirstsStatueDrop
    {
        const uint TINKER = 19354;
        internal static bool IsStatue(uint wcid) => wcid >= 19738 && wcid <= 19754;   // housestatue*-event, ace_world
        static readonly MethodInfo s_TryDropItem = AccessTools.Method(typeof(Player), "TryDropItem", new[] { typeof(WorldObject) });

        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Player), nameof(Player.GiveFromEmote), new[] { typeof(WorldObject), typeof(uint), typeof(int), typeof(int), typeof(float) });
            if (m == null) Mod.Log.Error("[RevivalGuard] ServerFirsts: Player.GiveFromEmote not found; a statue that does not fit is lost");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;

        static bool Prefix(Player __instance, WorldObject emoter, uint weenieClassId, int amount, int palette, float shade)
        {
            if (__instance == null || emoter == null || emoter.WeenieClassId != TINKER || !IsStatue(weenieClassId)) return true;
            for (int i = 0; i < Math.Max(1, amount); i++)
            {
                WorldObject item;
                try { item = WorldObjectFactory.CreateNewWorldObject(weenieClassId); }
                catch (Exception e) { Mod.Log.Error($"[RevivalGuard] ServerFirsts: statue {weenieClassId} create failed: {e.Message}"); item = null; }
                if (item == null) { if (i == 0) return true; break; }   // nothing made yet: ACE logs and handles it
                if (palette > 0) item.PaletteTemplate = palette;
                if (shade > 0) item.Shade = shade;
                bool inPack = false;
                try { inPack = __instance.Session != null && __instance.TryCreateForGive(emoter, item); }
                catch (Exception e)
                {
                    inPack = item.ContainerId.HasValue;   // added before the throw: it is in the pack, do not drop a second
                    Mod.Log.Warn($"[RevivalGuard] ServerFirsts: TryCreateForGive {item.Name} for {__instance.Name}: {e.Message}");
                }
                if (inPack) Mod.Log.Info($"[RevivalGuard] ServerFirsts: {item.Name} 0x{item.Guid.Full:X8} given to {__instance.Name} 0x{__instance.Guid.Full:X8}");
                else Drop(__instance, emoter, item);
            }
            return false;
        }

        static void Drop(Player p, WorldObject emoter, WorldObject item)
        {
            item.TimeToRot = -1;   // a one-per-server trophy must not rot away on the ground
            bool placed = false;
            try
            {
                if (p.CurrentLandblock != null && p.Location != null && s_TryDropItem != null)
                    placed = (bool)s_TryDropItem.Invoke(p, new object[] { item });
            }
            catch (Exception e)
            {
                placed = item.CurrentLandblock != null;   // thrown after it reached the ground: do not add a second time
                Mod.Log.Warn($"[RevivalGuard] ServerFirsts: TryDropItem {item.Name}: {e.InnerException?.Message ?? e.Message}");
            }
            foreach (var at in new[] { p.Location, emoter.Location })   // no landblock (logged out mid-emote): beside the Tinker
            {
                if (placed || at == null) continue;
                try
                {
                    item.Location = new Position(at);
                    item.Placement = Placement.Resting;
                    placed = LandblockManager.AddObject(item);
                }
                catch (Exception e) { placed = item.CurrentLandblock != null; Mod.Log.Warn($"[RevivalGuard] ServerFirsts: AddObject {item.Name}: {e.Message}"); }
            }
            if (placed)
            {
                try { item.SaveBiotaToDatabase(); } catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ServerFirsts: save {item.Name}: {e.Message}"); }
                p.Session?.Network?.EnqueueSend(new GameMessageSystemChat($"{emoter.Name} cannot fit the {item.Name} in your pack, "
                    + "so he sets it down at your feet. It will wait there until you pick it up.", ChatMessageType.Broadcast));
                Mod.Log.Info($"[RevivalGuard] ServerFirsts: {item.Name} 0x{item.Guid.Full:X8} for {p.Name} 0x{p.Guid.Full:X8} did not fit; "
                    + $"placed on the ground at {item.Location?.ToLOCString()} (never rots)");
                return;
            }
            // No ground either: owe it through the world-boss mailbox (delivered when there is room), never lose it.
            Mod.Log.Error($"[RevivalGuard] ServerFirsts: {item.Name} for {p.Name} 0x{p.Guid.Full:X8} could not be placed at "
                + $"{p.Location?.ToLOCString()}; owed instead");
            var wcid = item.WeenieClassId;
            item.Destroy();
            WorldBossSchedule.Grant(p.Guid.Full, wcid, "from Bhab, Arcanum Tinker, who could not hand it to you");
        }
    }

    [HarmonyPatch]
    static class ServerFirstsOnDeath
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Creature), nameof(Creature.OnDeath), new[] { typeof(DamageHistoryInfo), typeof(DamageType), typeof(bool) });
            if (m == null) Mod.Log.Error("[RevivalGuard] ServerFirsts: Creature.OnDeath(DamageHistoryInfo, DamageType, bool) not found; no server-firsts");
            return m;
        }
        static bool Prepare(MethodBase original) => original != null || TargetMethod() != null;
        static void Prefix(Creature __instance, DamageHistoryInfo lastDamager)
        {
            if (__instance == null || !ServerFirsts.Specs.ContainsKey(__instance.WeenieClassId)) return;
            try { ServerFirsts.OnDeath(__instance, lastDamager); }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] ServerFirsts: OnDeath: {e.Message}"); }
        }
    }
}
