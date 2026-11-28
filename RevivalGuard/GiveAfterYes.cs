using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Declining a luminance purchase still eats the token (docs/STATE.md open bug 3).</summary>
    [HarmonyPatch(typeof(Player), "GiveObjectToNPC")]
    public static class GiveAfterYes
    {
        const double PENDING_TTL = 120.0;
        const int WALK_LIMIT = 64;          // emote sets visited before giving up on the static walk (fall back to ACE)

        internal sealed class Pending { public uint Target; public uint Item; public uint Wcid; public string Npc; public DateTime At; }
        internal static readonly ConditionalWeakTable<Player, Pending> s_Pending = new ConditionalWeakTable<Player, Pending>();

        static readonly MethodInfo s_RemoveItemForGive = AccessTools.Method(typeof(Player), "RemoveItemForGive");

        /// <summary>Emote actions that hand out nothing: safe to run while the player still holds the item.</summary>
        static readonly HashSet<EmoteType> Harmless = new HashSet<EmoteType>
        {
            EmoteType.Act, EmoteType.Motion, EmoteType.Move, EmoteType.PhysScript, EmoteType.Say, EmoteType.Sound,
            EmoteType.Tell, EmoteType.Turn, EmoteType.TurnToTarget, EmoteType.TextDirect, EmoteType.WorldBroadcast,
            EmoteType.LocalBroadcast, EmoteType.DirectBroadcast, EmoteType.TellFellow, EmoteType.FellowBroadcast,
            EmoteType.BLog, EmoteType.AdminSpam, EmoteType.PopUp, EmoteType.Goto,
            EmoteType.InqQuest, EmoteType.InqQuestSolves, EmoteType.InqBoolStat, EmoteType.InqIntStat,
            EmoteType.InqFloatStat, EmoteType.InqStringStat, EmoteType.InqAttributeStat, EmoteType.InqRawAttributeStat,
            EmoteType.InqSecondaryAttributeStat, EmoteType.InqRawSecondaryAttributeStat, EmoteType.InqSkillStat,
            EmoteType.InqRawSkillStat, EmoteType.InqSkillTrained, EmoteType.InqSkillSpecialized, EmoteType.InqEvent,
            EmoteType.InqFellowQuest, EmoteType.InqFellowNum, EmoteType.InqNumCharacterTitles, EmoteType.InqOwnsItems,
            EmoteType.InqMyQuest, EmoteType.InqMyQuestSolves, EmoteType.InqPackSpace, EmoteType.InqQuestBitsOn,
            EmoteType.InqQuestBitsOff, EmoteType.InqMyQuestBitsOn, EmoteType.InqMyQuestBitsOff, EmoteType.InqInt64Stat,
            EmoteType.InqContractsFull,
        };

        /// <summary>True when every set reachable from start before an InqYesNo is harmless and at least one path reaches an InqYesNo.</summary>
        internal static bool AsksBeforeTaking(WorldObject npc, PropertiesEmote start)
        {
            var all = npc.Biota?.PropertiesEmote;
            if (all == null || start == null) return false;

            var seen = new HashSet<PropertiesEmote>();
            var stack = new Stack<PropertiesEmote>();
            stack.Push(start);
            bool asks = false;

            while (stack.Count > 0)
            {
                var set = stack.Pop();
                if (!seen.Add(set)) continue;
                if (seen.Count > WALK_LIMIT) return false;

                foreach (var a in set.PropertiesEmoteAction)
                {
                    var t = (EmoteType)a.Type;
                    if (t == EmoteType.InqYesNo) { asks = true; continue; }   // the branches after the answer are ACE's business
                    if (!Harmless.Contains(t)) return false;
                    if (string.IsNullOrEmpty(a.Message)) continue;
                    foreach (var e in all)
                        if (e != set && e.Quest != null && !seen.Contains(e)
                            && e.Quest.Equals(a.Message, StringComparison.OrdinalIgnoreCase))
                            stack.Push(e);
                }
            }
            return asks;
        }

        static bool Unasked(Player p, Pending cur, WorldObject target)
        {
            try
            {
                var pending = Traverse.Create(p.ConfirmationManager).Field("confirmations")
                    .GetValue<System.Collections.Concurrent.ConcurrentDictionary<ConfirmationType, Confirmation>>();
                if (pending != null && pending.ContainsKey(ConfirmationType.Yes_No)) return false;
            }
            catch (Exception) { return false; }     // cannot tell: keep ACE's refusal
            bool over = cur.Target == target.Guid.Full || (DateTime.UtcNow - cur.At).TotalSeconds > 15.0;
            if (over) s_Pending.Remove(p);
            return over;
        }

        static bool Prefix(Player __instance, WorldObject target, WorldObject item, Container itemFoundInContainer,
                           Container itemRootOwner, bool itemWasEquipped, int amount)
        {
            var p = __instance;
            if (target == null || item == null || p.Session == null) return true;

            // ACE's own refusals come before the give in the original; let them stand.
            if (target.EmoteManager.IsBusy) return true;
            if (p.IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(p.ItemsInTradeWindow)) return true;
            if (item is PetDevice pd && pd.Pet is not null) return true;
            if (p.IsOlthoiPlayer && target.CreatureType != ACE.Entity.Enum.CreatureType.Olthoi) return true;
            if (target.AiAcceptEverything || !target.AllowGive) return true;

            if (!target.HasGiveOrRefuseEmoteForItem(item, out var emote)) return true;
            if (emote == null || emote.Category != EmoteCategory.Give) return true;
            if (!AsksBeforeTaking(target, emote)) return true;

            // One question at a time: a second token while the first is unanswered is refused the way ACE refuses a gift mid-emote.
            if (s_Pending.TryGetValue(p, out var cur) && (DateTime.UtcNow - cur.At).TotalSeconds < PENDING_TTL
                && !Unasked(p, cur, target))
            {
                p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, item.Guid.Full));
                p.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(p.Session, WeenieErrorWithString.AiRefuseItemDuringEmote, target.Name));
                return false;
            }

            var pend = new Pending { Target = target.Guid.Full, Item = item.Guid.Full, Wcid = item.WeenieClassId, Npc = target.Name, At = DateTime.UtcNow };
            s_Pending.AddOrUpdate(p, pend);

            // Retail's line for an item shown but not taken, and the event that makes the client keep the item in the pack.
            p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You allow {target.Name} to examine your {item.NameWithMaterial}.", ChatMessageType.Broadcast));
            p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, item.Guid.Full, WeenieError.TradeAiRefuseEmote));

            if (!target.EmoteManager.ExecuteEmoteSet(emote, p))
            {
                // lost the IsBusy race: nothing was asked, so nothing is pending
                s_Pending.Remove(p);
                p.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(p.Session, WeenieErrorWithString.AiRefuseItemDuringEmote, target.Name));
                return false;
            }

            Mod.Log.Info($"[RevivalGuard] give held for the answer: {p.Name} showed '{item.Name}' 0x{item.Guid.Full:X8} to {target.Name}; taken only on Yes");
            return false;   // ACE's take-first path is skipped
        }

        /// <summary>The Yes arrived: take the item now, then let the TestSuccess set run.</summary>
        internal static bool OnAnswer(Confirmation_YesNo c, bool response)
        {
            var p = c.Player;
            if (p == null) return true;
            if (!s_Pending.TryGetValue(p, out var pend) || pend.Target != c.SourceGuid.Full) return true;
            s_Pending.Remove(p);

            if (!response)
            {
                Mod.Log.Info($"[RevivalGuard] {p.Name} declined {pend.Npc}'s question; '{pend.Wcid}' 0x{pend.Item:X8} stays in the pack");
                return true;    // the NPC's own decline text (TestFailure) runs
            }

            var npc = p.FindObject(pend.Target, Player.SearchLocations.Landblock);
            if (npc == null) return true;   // ACE finds no source either and does nothing

            var item = p.FindObject(pend.Item, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems,
                                    out var inContainer, out var rootOwner, out var wasEquipped);
            if (item == null || item.WeenieClassId != pend.Wcid || p.Session == null)
            {
                // Said Yes without the token any more (dropped, sold, traded away): retail's own words.
                p.SendWeenieError(WeenieError.YouDoNotOwnThatItem);
                Mod.Log.Info($"[RevivalGuard] {p.Name} said Yes to {pend.Npc} but no longer holds 0x{pend.Item:X8}; nothing granted");
                return false;   // no purchase without the payment
            }

            var args = new object[] { item, inContainer, wasEquipped, rootOwner, 1, null, false };
            var ok = s_RemoveItemForGive != null && (bool)s_RemoveItemForGive.Invoke(p, args);
            var taken = args[5] as WorldObject;
            if (!ok || taken == null)
            {
                Mod.Log.Warn($"[RevivalGuard] {p.Name} said Yes to {pend.Npc} but RemoveItemForGive refused 0x{pend.Item:X8}; nothing granted");
                return false;   // RemoveItemForGive already told the client the move failed
            }

            // Retail's TakeItems wording, as the wiki transcript records it after the Yes.
            p.Session.Network.EnqueueSend(new GameMessageSystemChat($"You hand over 1 of your {taken.PluralName ?? taken.Name}.", ChatMessageType.Broadcast));
            npc.EnqueueBroadcast(new GameMessageSound(npc.Guid, Sound.ReceiveItem));
            taken.Destroy();

            Mod.Log.Info($"[RevivalGuard] {p.Name} said Yes to {pend.Npc}; '{taken.Name}' taken and the purchase runs");
            return true;    // the TestSuccess set (SpendLuminance, the aura) runs now
        }
    }

    /// <summary>The answer to an InqYesNo.</summary>
    [HarmonyPatch(typeof(Confirmation_YesNo), nameof(Confirmation_YesNo.ProcessConfirmation))]
    public static class GiveAfterYes_Answer
    {
        static bool Prefix(Confirmation_YesNo __instance, bool response) => GiveAfterYes.OnAnswer(__instance, response);
    }
}
