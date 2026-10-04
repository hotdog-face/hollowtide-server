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
    /// <summary>
    /// DECLINING A LUMINANCE PURCHASE STILL EATS THE TOKEN (docs/STATE.md open bug 3).
    ///
    /// Nalicana (wcid 43398) sells her auras for a token the player hands back to her: the Give
    /// emote set for the token checks AvailableLuminance (InqInt64Stat), the TestSuccess set asks
    /// InqYesNo, and only the Yes branch runs SpendLuminance and grants the aura. ACE's
    /// Player.GiveObjectToNPC takes the item FIRST (RemoveItemForGive, then ExecuteEmoteSet, then
    /// Destroy) and the question is asked seconds later from the emote chain, so No, a timeout, or
    /// "You do not have enough Luminance." all leave the player one token poorer for nothing.
    ///
    /// WHAT RETAIL DID. The wiki transcript for the sibling path on the same NPC (Blank Augmentation
    /// Gem for 10,000 Luminance; AC-Wiki pages/N/Nalicana.md, revision 348995) reads, in order:
    /// "You allow Nalicana to examine your Blank Augmentation Gem." then the question, then on No
    /// "You decline to empower yourself with Luminance at this time.", and on Yes "Nalicana tells
    /// you, 'Very well...'", "You hand over 1 of your Blank Augmentation Gems.", "You've earned
    /// 10,000 Luminance." Retail examined the item, asked, and took it only after the Yes. That is
    /// wiki-tier evidence (player-written, ranked below the decompile and the DATs), but the retail
    /// server's emote logic is in neither of those, and the transcript is the only retail record of
    /// this flow found. There is no transcript for an aura token itself.
    ///
    /// SO THE ITEM STAYS IN THE PACK UNTIL THE PLAYER SAYS YES. When a Give set's chain leads to an
    /// InqYesNo and nothing of value is handed out before the question, the give is answered the way
    /// ACE answers a Refuse emote (retail's "You allow X to examine your Y." line plus the client's
    /// put-it-back event), the emote chain runs as normal, and the item is taken at the moment the
    /// Yes arrives, with retail's TakeItems wording. No or a timeout run the NPC's own decline text
    /// and the token is never touched.
    ///
    /// WHY THE WALK IS CONSERVATIVE. Deferring the take on a chain that awards something BEFORE the
    /// question, or on a branch that never asks, would pay the reward for free. So every emote set
    /// reachable from the give before an InqYesNo must consist only of talk, motion and inquiries
    /// (<see cref="Harmless"/>); anything else (awards, stamps, gives, spells, teleports) leaves that
    /// NPC on ACE's original take-first path. Nalicana's token sets are TurnToTarget + one inquiry,
    /// and her failure sets are a Tell, so they qualify.
    /// </summary>
    /// <para>PATCHED ON <c>Player</c>, which declares the private <c>GiveObjectToNPC</c>, and on
    /// <c>Confirmation_YesNo</c>, which declares its own <c>ProcessConfirmation</c> override. Naming a
    /// type that does not declare the method throws "Undefined target method" inside PatchAll and
    /// loads the whole mod Inactive (2026-09-22).</para>
    [HarmonyPatch(typeof(Player), "GiveObjectToNPC")]
    public static class GiveAfterYes
    {
        const double PENDING_TTL = 120.0;   // ACE's confirmation timeout is 30 s; a client that never answers must not hold the NPC for ever
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

        /// <summary>True when every set reachable from <paramref name="start"/> before an InqYesNo is
        /// harmless and at least one path reaches an InqYesNo. Follows any set on the NPC whose Quest
        /// equals an action's Message (TestSuccess/TestFailure/QuestSuccess/QuestFailure/Goto all key
        /// that way); the question's own branches are not entered.</summary>
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

        /// <summary>A HELD GIVE WHOSE QUESTION NEVER CAME. The chain can end without asking: Nalicana's
        /// "You do not have enough Luminance." branch is a plain tell. The entry then sat for the whole
        /// PENDING_TTL, and every give to anyone in that window was refused as "too busy to accept
        /// gifts" (release check 2026-09-24: 45 s after the tell she was idle and still refused). It
        /// is over when no Yes/No is waiting for the player AND either the same NPC has finished
        /// its emotes (the caller has just checked IsBusy) or 15 s have gone by.</summary>
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

            // One question at a time: a second token while the first is unanswered is refused the way
            // ACE refuses a gift mid-emote (retail's AiRefuseItemDuringEmote string).
            if (s_Pending.TryGetValue(p, out var cur) && (DateTime.UtcNow - cur.At).TotalSeconds < PENDING_TTL
                && !Unasked(p, cur, target))
            {
                p.Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(p.Session, item.Guid.Full));
                p.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(p.Session, WeenieErrorWithString.AiRefuseItemDuringEmote, target.Name));
                return false;
            }

            var pend = new Pending { Target = target.Guid.Full, Item = item.Guid.Full, Wcid = item.WeenieClassId, Npc = target.Name, At = DateTime.UtcNow };
            s_Pending.AddOrUpdate(p, pend);

            // Retail's line for an item shown but not taken (the same one ACE prints for a Refuse
            // emote), and the event that makes the client keep the item in the pack.
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

        /// <summary>The Yes arrived: take the item now, then let the TestSuccess set run. No, a
        /// timeout, a missing NPC or a missing item leave the player exactly as they were.</summary>
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

    /// <summary>The answer to an InqYesNo. Patched on the class that declares this override.</summary>
    [HarmonyPatch(typeof(Confirmation_YesNo), nameof(Confirmation_YesNo.ProcessConfirmation))]
    public static class GiveAfterYes_Answer
    {
        static bool Prefix(Confirmation_YesNo __instance, bool response) => GiveAfterYes.OnAnswer(__instance, response);
    }
}
