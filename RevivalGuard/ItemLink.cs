using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;
using Spell = ACE.Server.Entity.Spell;

namespace RevivalGuard
{
    /// <summary>
    /// ITEM LINKS IN CHAT (docs/CHAT-IMPROVEMENTS-01.md piece 7, ours). AC chat carries only text, and nobody can
    /// appraise an item in someone else's pack, so a link is a snapshot the server keeps:
    ///
    ///   @itemlink make 0xGUID   (the client's /link, or an item dropped on the chat box) takes the stats of an item
    ///                           you carry NOW, keeps them under a number, and answers "ITEMLINK n Name", a data line
    ///                           the client turns into "[Name]" in your chat entry (clients up to 0.1.117 wrote
    ///                           "[Name #n]", which put the number in everyone's chat, retail's too).
    ///   @itemlink view n        (a click on a link whose number the client knows) and
    ///   @itemlink find age Speaker|Name   (a click on "[Name]" in Speaker's line, said `age` seconds ago) answer with
    ///                           the snapshot as an examine response (IdentifyObjectResponse) for guid
    ///                           0xFE000000 | n, so the clicker's appraisal window opens with the item as it was.
    ///   @itemlink show n        (clients up to 0.1.117) sends that snapshot's lines to the asker as text.
    ///
    /// The snapshot is taken at link time, so a link stays right after the item is sold, changed or gone. The newest
    /// 5,000 are kept in memory; a restart forgets them ("That item link has expired.").
    /// </summary>
    public static class ItemLink
    {
        const int KEEP = 5000;
        const uint LINK_GUID_BASE = 0xFE000000u;   // client AcItemLinkText.LinkGuid: no live object has these
        sealed class Snap { public string Name, By; public List<string> Lines; public byte[] Appraisal; public DateTime At; }

        /// <summary>An examine response built from a stored AppraiseInfo (written at link time).</summary>
        sealed class LinkAppraisal : GameEventMessage
        {
            public LinkAppraisal(Session s, uint guid, byte[] info) : base(GameEventType.IdentifyObjectResponse, GameMessageGroup.UIQueue, s)
            { Writer.Write(guid); Writer.Write(info); }
        }
        static readonly Dictionary<int, Snap> s_Snaps = new Dictionary<int, Snap>();
        static readonly Queue<int> s_Order = new Queue<int>();
        static readonly object s_Lock = new object();
        // RANDOM IDS, NOT A COUNTER (security audit 2026-10-20): a counter let anyone walk every link ever made. Seven
        // digits because every released client's tag reads at most seven (ChatHud.ITEM_TAG); clients from 0.1.74 read
        // ten, so a later server can widen ID_MAX once the older clients are gone.
        const int ID_MIN = 1_000_000, ID_MAX = 9_999_999;

        public static void Register()
        {
            CommandManager.TryAddCommand(Handle, "itemlink", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Item links in chat (the client's /link and its clickable [Item]).", "make 0xGUID | view n | find age Speaker|Name | show n");
        }

        static void Say(Session s, string t, ChatMessageType type = ChatMessageType.Broadcast) =>
            s?.Network.EnqueueSend(new GameMessageSystemChat(t, type));

        static void Handle(Session session, params string[] parameters)
        {
            var me = session?.Player;
            if (me == null || parameters == null || parameters.Length < 2) return;
            string verb = parameters[0].ToLowerInvariant(), arg = parameters[1].Trim();
            if (verb == "make")
            {
                if (!arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !uint.TryParse(arg.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint guid)) return;
                var item = me.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);
                if (item == null) { Say(session, "You can only link an item you are carrying."); return; }
                var snap = new Snap { Name = Clean(item.GetNameWithMaterial()), By = me.Name.TrimStart('+'), Lines = Describe(item),
                                      Appraisal = AppraisalOf(item, me), At = DateTime.UtcNow };
                int id;
                lock (s_Lock)
                {
                    do id = System.Security.Cryptography.RandomNumberGenerator.GetInt32(ID_MIN, ID_MAX + 1);
                    while (s_Snaps.ContainsKey(id));
                    s_Snaps[id] = snap; s_Order.Enqueue(id);
                    while (s_Order.Count > KEEP) s_Snaps.Remove(s_Order.Dequeue());
                }
                Say(session, $"ITEMLINK {id} {snap.Name}");
                return;
            }
            if (verb == "show" && int.TryParse(arg.TrimStart('#'), out int n))
            {
                Snap snap; lock (s_Lock) s_Snaps.TryGetValue(n, out snap);
                if (snap == null) { Say(session, "That item link has expired."); return; }
                Say(session, $"{snap.Name} (linked by {snap.By})", ChatMessageType.Appraisal);
                foreach (var l in snap.Lines) Say(session, "   " + l, ChatMessageType.Appraisal);
                return;
            }
            if (verb == "view" || verb == "find")
            {
                int id = 0; Snap snap = null;
                lock (s_Lock)
                {
                    if (verb == "view") { if (int.TryParse(arg.TrimStart('#'), out id)) s_Snaps.TryGetValue(id, out snap); }
                    else if (int.TryParse(arg, out int age) && parameters.Length >= 3)
                    {
                        string rest = string.Join(" ", parameters, 2, parameters.Length - 2);
                        int bar = rest.IndexOf('|');
                        string by = bar >= 0 ? rest.Substring(0, bar).Trim().TrimStart('+') : "", name = (bar >= 0 ? rest.Substring(bar + 1) : rest).Trim();
                        id = FindSnap(by, name, DateTime.UtcNow.AddSeconds(-Math.Max(0, age)));
                        if (id != 0) snap = s_Snaps[id];
                    }
                }
                if (snap == null) { Say(session, "That item link has expired."); return; }
                if (snap.Appraisal == null) { Say(session, $"{snap.Name} (linked by {snap.By})", ChatMessageType.Appraisal); foreach (var l in snap.Lines) Say(session, "   " + l, ChatMessageType.Appraisal); return; }
                session.Network.EnqueueSend(new LinkAppraisal(session, LINK_GUID_BASE | ((uint)id & 0x00FFFFFFu), snap.Appraisal));
            }
        }

        /// <summary>The snapshot a "[Name]" in `by`'s line points at: theirs of that name made at or before the line was
        /// said (plus a little clock slack), newest first; else their newest of that name; else anyone's newest of that
        /// name (a link pasted on by someone else). Under s_Lock. 0 = none.</summary>
        static int FindSnap(string by, string name, DateTime saidAt)
        {
            DateTime cut = saidAt.AddSeconds(10);
            int best = 0, newest = 0, anyone = 0; DateTime bestAt = DateTime.MinValue, newestAt = DateTime.MinValue, anyAt = DateTime.MinValue;
            foreach (var kv in s_Snaps)
            {
                var s = kv.Value;
                if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.At > anyAt) { anyAt = s.At; anyone = kv.Key; }
                if (!string.Equals(s.By, by, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.At > newestAt) { newestAt = s.At; newest = kv.Key; }
                if (s.At <= cut && s.At > bestAt) { bestAt = s.At; best = kv.Key; }
            }
            return best != 0 ? best : newest != 0 ? newest : anyone;
        }

        /// <summary>The item's examine response body as its owner would get it now (ACE's AppraiseInfo, written once).
        /// null if ACE cannot build it (the click then falls back to the text lines).</summary>
        static byte[] AppraisalOf(WorldObject item, Player me)
        {
            try
            {
                using var ms = new System.IO.MemoryStream();
                using var w = new System.IO.BinaryWriter(ms);
                w.Write(new AppraiseInfo(item, me, true));
                w.Flush();
                return ms.ToArray();
            }
            catch (Exception e) { Console.WriteLine($"[RevivalGuard] ItemLink: appraisal of 0x{item.Guid.Full:X8} failed: {e.Message}"); return null; }
        }

        /// <summary>A link's name may not hold the characters the tag is built from.</summary>
        static string Clean(string s) => (s ?? "item").Replace("[", "(").Replace("]", ")").Replace("#", "");

        static List<string> Describe(WorldObject i)
        {
            var o = new List<string>();
            int? dmg = i.GetProperty(PropertyInt.Damage);
            if (dmg > 0)
            {
                double v = i.DamageVariance ?? 0;
                int lo = (int)Math.Round(dmg.Value * (1 - v));
                o.Add($"Damage {lo} - {dmg}" + (i.WeaponTime > 0 ? $", speed {i.WeaponTime}" : ""));
            }
            if (i.DamageMod > 1.0) o.Add($"Damage bonus +{Math.Round((i.DamageMod.Value - 1) * 100)}%");
            if (i.ElementalDamageBonus > 0) o.Add($"Elemental damage +{i.ElementalDamageBonus}");
            if (i.ElementalDamageMod > 1.0) o.Add($"Elemental damage +{Math.Round((i.ElementalDamageMod.Value - 1) * 100)}% vs monsters");
            if (i.ArmorLevel > 0) o.Add($"Armor level {i.ArmorLevel}");
            var ratings = new List<string>();
            if (i.GearDamage > 0) ratings.Add($"Damage {i.GearDamage}");
            if (i.GearDamageResist > 0) ratings.Add($"Damage resist {i.GearDamageResist}");
            if (i.GearCrit > 0) ratings.Add($"Crit {i.GearCrit}");
            if (i.GearCritDamage > 0) ratings.Add($"Crit damage {i.GearCritDamage}");
            if (ratings.Count > 0) o.Add("Ratings: " + string.Join(", ", ratings));
            var book = i.Biota?.PropertiesSpellBook;
            if (book != null && book.Count > 0)
            {
                var names = new List<string>();
                foreach (var id in book.Keys.Take(12))
                {
                    try { var sp = new Spell((uint)id, false); if (!string.IsNullOrEmpty(sp.Name)) names.Add(sp.Name); } catch { }
                }
                if (names.Count > 0) o.Add("Spells: " + string.Join(", ", names) + (book.Count > 12 ? $" and {book.Count - 12} more" : ""));
            }
            if (i.ItemMaxMana > 0) o.Add($"Mana {i.ItemCurMana ?? 0} / {i.ItemMaxMana}");
            if (i.WieldRequirements == WieldRequirement.Level && i.WieldDifficulty > 0) o.Add($"Wield requires level {i.WieldDifficulty}");
            else if (i.WieldRequirements != 0 && i.WieldDifficulty > 0) o.Add($"Wield requires {i.WieldRequirements} {i.WieldDifficulty}");
            var tail = new List<string>();
            if (i.ItemWorkmanship > 0) tail.Add($"workmanship {i.ItemWorkmanship}");
            if (i.Value > 0) tail.Add($"value {i.Value:N0}");
            if (i.EncumbranceVal > 0) tail.Add($"burden {i.EncumbranceVal}");
            if (tail.Count > 0) o.Add(char.ToUpper(tail[0][0]) + string.Join(", ", tail).Substring(1));
            if (o.Count == 0) o.Add("Nothing more to tell.");
            return o;
        }
    }
}
