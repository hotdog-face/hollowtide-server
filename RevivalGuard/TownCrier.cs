using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>
    /// THE HOLTBURG TOWN CRIER (owner 2026-11-09: "yes do the Holtburg Town Crier"). A retail-looking Town Crier (weenie
    /// 903150, a clone of retail's Aluvian crier 5773, placed by the datafix tools/gpubox-ace/gen_town_crier.py writes)
    /// who answers players' questions about the game in character: where to hunt at a level, how to reach a place, what
    /// a thing does, what level a quest is. The answers come from TownCrierBrain, a deterministic search over our own
    /// data (the client's wiki pack and Journal data, copied into `crier/` beside this dll by tools/crier/sync-knowledge.sh);
    /// no model, no network call, nothing made up: what he does not know he says so and points at the Journal's Wiki tab.
    /// docs/TOWN-CRIER.md has the design and the test sheet.
    ///
    /// How players ask (all three answer privately, as his tells):
    ///   - say it near him (local chat within SAY_RANGE), as to any NPC;
    ///   - /tell Town Crier, &lt;question&gt; from anywhere in the world (GameActionTell is patched: a tell to his name goes to
    ///     him instead of "that character is not online");
    ///   - speak to him as the selected target (TalkDirect: EmoteManager.OnTalkDirect).
    ///   Using him (double-click) gets the greeting. Off unless `@modifybool rg_crier true` (new content: owner's yes).
    /// Staff: @crier status | ask &lt;question&gt; | reload.
    /// </summary>
    public static class TownCrier
    {
        internal const string P_ON = "rg_crier";
        internal static uint CRIER_WCID = 903150;
        const double SAY_RANGE = 12.0;        // metres: a word said this near him is for him
        const double ANSWER_GAP_S = 1.5;      // one answer per player per this many seconds (chat flood guard, our side)
        const string LOG = "crier-log.txt";
        // Retail's own criers (every heritage, the Marae Lassel and Olthoi ones): they answer too when crier.all_criers is 1.
        static readonly HashSet<uint> RETAIL_CRIERS = new HashSet<uint> { 5772, 5773, 5774, 5775, 5776, 5777, 11938, 11939, 11940, 22643, 30063, 30064 };

        static readonly object s_Lock = new object();
        static TownCrierBrain s_Brain;
        static Task s_Loading;
        static bool s_AllCriers;
        static readonly Dictionary<uint, double> s_LastAnswer = new Dictionary<uint, double>();
        // landblock id -> (crier guid or 0, when looked up): a say in a block with no crier costs one dictionary read
        static readonly Dictionary<uint, (uint Guid, double At)> s_CrierIn = new Dictionary<uint, (uint, double)>();

        internal static bool On
        {
            get { try { return PropertyManager.GetBool(P_ON, false).Item; } catch { return false; } }
        }

        public static void Register()
        {
            CRIER_WCID = (uint)Tunables.L("crier.wcid", CRIER_WCID);
            s_AllCriers = Tunables.L("crier.all_criers", 0) == 1;
            PkCommon.RegisterBool(P_ON, false, "The Holtburg Town Crier answers players' questions (RevivalGuard TownCrier)");
            CommandManager.TryAddCommand(HandleAdmin, "crier", AccessLevel.Admin, CommandHandlerFlag.None,
                "The Holtburg Town Crier: status, a dry run of a question, reload his knowledge.", "[status | ask <question> | reload]");
            if (On) StartLoading();
            Mod.Log.Info($"[RevivalGuard] TownCrier: {(On ? "ON" : "off")} ({P_ON}); weenie {CRIER_WCID}{(s_AllCriers ? ", every retail crier answers" : "")}; knowledge {Folder()}; @crier");
        }

        static string Folder() => Path.Combine(ModFolder.Path(), "crier");

        static void StartLoading()
        {
            lock (s_Lock)
            {
                if (s_Loading != null && !s_Loading.IsCompleted) return;
                s_Loading = Task.Run(() =>
                {
                    try
                    {
                        var b = TownCrierBrain.Load(Folder(), s => Mod.Log.Warn("[RevivalGuard] TownCrier: " + s));
                        lock (s_Lock) s_Brain = b;
                        Mod.Log.Info($"[RevivalGuard] TownCrier knowledge: {b.Status}");
                    }
                    catch (Exception e) { Mod.Log.Error($"[RevivalGuard] TownCrier load: {e}"); }
                });
            }
        }

        static TownCrierBrain Brain()
        {
            lock (s_Lock) if (s_Brain != null) return s_Brain;
            StartLoading();
            return null;
        }

        internal static bool IsCrier(WorldObject wo) =>
            wo is Creature && !(wo is Player) && (wo.WeenieClassId == CRIER_WCID || (s_AllCriers && RETAIL_CRIERS.Contains(wo.WeenieClassId)));

        // every crier guid ever seen this run: a tell from afar names him by the guid the client remembers, and his
        // landblock may not be the asker's (or may be unloaded)
        static readonly HashSet<uint> s_CrierGuids = new HashSet<uint>();

        static void Remember(WorldObject wo) { if (wo != null) lock (s_Lock) s_CrierGuids.Add(wo.Guid.Full); }

        /// <summary>The crier this guid names, wherever he stands (the asker's landblock, any loaded landblock), or null; a
        /// guid seen before counts as his even while his landblock is unloaded (the object is then null).</summary>
        static bool IsCrierGuid(uint guid, Player p, out WorldObject npc)
        {
            npc = p?.CurrentLandblock?.GetObject(guid);
            if (npc != null) { if (!IsCrier(npc)) { npc = null; return false; } Remember(npc); return true; }
            lock (s_Lock) if (s_CrierGuids.Contains(guid)) { npc = FindLoaded(guid); return true; }
            npc = FindLoaded(guid);
            if (npc == null || !IsCrier(npc)) { npc = null; return false; }
            Remember(npc);
            return true;
        }

        static WorldObject FindLoaded(uint guid)
        {
            try
            {
                foreach (var lb in LandblockManager.GetLoadedLandblocks())
                {
                    var wo = lb.GetObject(guid);
                    if (wo != null) return IsCrier(wo) ? wo : null;
                }
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownCrier find: {e.Message}"); }
            return null;
        }

        // ------------------------------------------------------------------ answering
        static void Tell(Player p, WorldObject npc, string text)
        {
            if (p?.Session == null || string.IsNullOrEmpty(text)) return;
            if (npc != null) p.Session.Network.EnqueueSend(new GameEventTell(npc, text, p, ChatMessageType.Tell));
            else p.Session.Network.EnqueueSend(new GameMessageSystemChat($"{TownCrierBrain.WHO} tells you, \"{text}\"", ChatMessageType.Tell));
        }

        static TownCrierBrain.Asker AskerOf(Player p, WorldObject npc)
        {
            var a = new TownCrierBrain.Asker { Name = p.Name, Level = p.Level ?? 1 };
            try
            {
                var m = p.Location?.GetMapCoords();
                if (m.HasValue) { a.NS = m.Value.Y; a.EW = m.Value.X; a.Outdoors = true; } else a.Outdoors = false;
                var c = npc?.Location?.GetMapCoords();
                if (c.HasValue) { a.CrierNS = c.Value.Y; a.CrierEW = c.Value.X; }
                if (!a.Outdoors) { a.NS = a.CrierNS; a.EW = a.CrierEW; }
            }
            catch { }
            return a;
        }

        /// <summary>Answer one question. npc may be null (a tell from afar): the lines then come as system chat in his name.</summary>
        internal static void Ask(Player p, WorldObject npc, string question)
        {
            if (p == null || string.IsNullOrWhiteSpace(question)) return;
            string q = question.Trim();
            if (q.Length < 2 || q.StartsWith("/") || q.StartsWith("@")) return;
            double now = ACE.Common.Time.GetUnixTime();
            lock (s_Lock)
            {
                if (s_LastAnswer.TryGetValue(p.Guid.Full, out var last) && now - last < ANSWER_GAP_S) return;
                s_LastAnswer[p.Guid.Full] = now;
            }
            var brain = Brain();
            if (brain == null) { Tell(p, npc, "Hear ye! Give me a moment to gather my wits, then ask again."); return; }
            var lines = brain.Answer(q, AskerOf(p, npc));
            foreach (var l in lines) Tell(p, npc, l);
            Log(p.Name, q, lines);
        }

        static void Log(string who, string q, List<string> lines)
        {
            try
            {
                var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\t{who}\t{q.Replace('\t', ' ')}\t{string.Join(" | ", lines).Replace('\t', ' ')}";
                File.AppendAllText(Path.Combine(ModFolder.Path(), LOG), line + Environment.NewLine);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownCrier log: {e.Message}"); }
        }

        /// <summary>The crier in the player's landblock, within range, or null. The block is scanned once a minute at most.</summary>
        static WorldObject CrierNear(Player p)
        {
            var lb = p?.CurrentLandblock;
            if (lb == null || p.Location == null) return null;
            uint key = p.Location.LandblockId.Raw;
            double now = ACE.Common.Time.GetUnixTime();
            uint guid;
            lock (s_Lock)
            {
                if (!s_CrierIn.TryGetValue(key, out var c) || now - c.At > 60)
                {
                    guid = 0;
                    try { foreach (var wo in lb.GetAllWorldObjectsForDiagnostics()) if (IsCrier(wo)) { guid = wo.Guid.Full; break; } }
                    catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownCrier scan: {e.Message}"); }
                    s_CrierIn[key] = (guid, now);
                }
                else guid = c.Guid;
            }
            if (guid == 0) return null;
            var npc = lb.GetObject(guid);
            if (npc == null || npc.Location == null) { lock (s_Lock) s_CrierIn.Remove(key); return null; }
            Remember(npc);
            return p.Location.DistanceTo(npc.Location) <= SAY_RANGE ? npc : null;
        }

        // ------------------------------------------------------------------ staff
        static void HandleAdmin(Session session, params string[] args)
        {
            void Say(string s)
            {
                if (session?.Player != null) session.Network.EnqueueSend(new GameMessageSystemChat(s, ChatMessageType.Broadcast));
                else Console.WriteLine(s);
            }
            string verb = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (verb == "reload") { lock (s_Lock) s_Brain = null; StartLoading(); Say("Town Crier: reloading his knowledge from " + Folder()); return; }
            if (verb == "ask")
            {
                string q = string.Join(" ", args.Skip(1));
                var brain = Brain();
                if (brain == null) { Say("Town Crier: knowledge still loading; ask again in a moment."); return; }
                var who = session?.Player != null ? AskerOf(session.Player, null) : new TownCrierBrain.Asker { Name = "Console", Level = 10 };
                foreach (var l in brain.Answer(q, who)) Say($"{TownCrierBrain.WHO} tells you, \"{l}\"");
                return;
            }
            var b = Brain();
            Say($"Town Crier: {(On ? "ON" : "off")} ({P_ON}); weenie {CRIER_WCID}; knowledge {(b == null ? "loading" : b.Status)}; asked {(b?.Asked ?? 0)} this run; folder {Folder()}. @crier ask <question> | reload");
        }

        // ================================================================== patches
        /// <summary>A word said near him is for him.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.HandleActionTalk))]
        static class HeardNearby
        {
            static void Postfix(Player __instance, string message)
            {
                try
                {
                    if (!On || __instance == null || string.IsNullOrWhiteSpace(message)) return;
                    var npc = CrierNear(__instance);
                    if (npc != null) Ask(__instance, npc, message);
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownCrier heard: {e.Message}"); }
            }
        }

        /// <summary>Spoken to as the selected target (the client's TalkDirect): ours instead of an emote set he has not got.</summary>
        [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.OnTalkDirect))]
        static class SpokenTo
        {
            static bool Prefix(EmoteManager __instance, Player player, string message)
            {
                try
                {
                    if (!On || !IsCrier(__instance?.WorldObject)) return true;
                    Remember(__instance.WorldObject);
                    Ask(player, __instance.WorldObject, message);
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownCrier talk direct: {e.Message}"); return true; }
            }
        }

        /// <summary>Double-clicked: the greeting, in place of retail's "give me a pyreal" emotes he does not carry.</summary>
        [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.OnUse))]
        static class Used
        {
            static bool Prefix(EmoteManager __instance, Creature activator)
            {
                try
                {
                    if (!On || !IsCrier(__instance?.WorldObject) || !(activator is Player p)) return true;
                    Remember(__instance.WorldObject);
                    Ask(p, __instance.WorldObject, "hello");
                    return false;
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] TownCrier use: {e.Message}"); return true; }
            }
        }

        /// <summary>"/tell Town Crier, where is the Mana Forge" from afar. Our client sends a tell to a name it has seen as
        /// TalkDirect by that object's guid (ChatHud's speakable target), and ACE answers "not available" when the guid is
        /// not in the asker's landblock. A guid that is a crier's, anywhere, is answered; the payload is put back for ACE
        /// when it is anyone else's.</summary>
        [HarmonyPatch(typeof(GameActionTalkDirect), nameof(GameActionTalkDirect.Handle))]
        static class ToldByGuid
        {
            static bool Prefix(ClientMessage clientMessage, Session session)
            {
                if (!On || clientMessage?.Payload == null || session?.Player == null) return true;
                long pos = clientMessage.Payload.BaseStream.Position;
                try
                {
                    string message = clientMessage.Payload.ReadString16L();
                    uint guid = clientMessage.Payload.ReadUInt32();
                    var p = session.Player;
                    // in the asker's own landblock ACE's path runs as usual (EmoteManager.OnTalkDirect, patched above)
                    if (p.CurrentLandblock?.GetObject(guid) != null || !IsCrierGuid(guid, p, out var npc))
                    { clientMessage.Payload.BaseStream.Position = pos; return true; }
                    if (p.IsGagged) { p.SendGagError(); return false; }
                    p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You tell {TownCrierBrain.WHO}, \"{message}\"", ChatMessageType.OutgoingTell));
                    Ask(p, npc, message);
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"[RevivalGuard] TownCrier talk direct afar: {e.Message}");
                    try { clientMessage.Payload.BaseStream.Position = pos; } catch { }
                    return true;
                }
            }
        }

        /// <summary>"/tell Town Crier, where do I level at 30" by name (a retail client, or ours before it has seen him): ACE
        /// would answer "not online". The payload is read here and put back for ACE when the tell is for someone else.</summary>
        [HarmonyPatch(typeof(GameActionTell), nameof(GameActionTell.Handle))]
        static class ToldFromAfar
        {
            static bool Prefix(ClientMessage clientMessage, Session session)
            {
                if (!On || clientMessage?.Payload == null || session?.Player == null) return true;
                long pos = clientMessage.Payload.BaseStream.Position;
                try
                {
                    string message = clientMessage.Payload.ReadString16L();
                    string target = clientMessage.Payload.ReadString16L().Trim();
                    if (!target.Equals(TownCrierBrain.WHO, StringComparison.OrdinalIgnoreCase) && !target.Equals("Town Crier of Holtburg", StringComparison.OrdinalIgnoreCase)
                        && !target.Equals("Holtburg Herald", StringComparison.OrdinalIgnoreCase) && !target.Equals("Herald", StringComparison.OrdinalIgnoreCase))
                    { clientMessage.Payload.BaseStream.Position = pos; return true; }
                    var p = session.Player;
                    if (p.IsGagged) { p.SendGagError(); return false; }
                    p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You tell {TownCrierBrain.WHO}, \"{message}\"", ChatMessageType.OutgoingTell));
                    Ask(p, CrierNear(p), message);
                    return false;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"[RevivalGuard] TownCrier tell: {e.Message}");
                    try { clientMessage.Payload.BaseStream.Position = pos; } catch { }
                    return true;
                }
            }
        }
    }
}
