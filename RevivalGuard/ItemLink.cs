using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
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
    ///                           the client turns into "[Name #n]" in your chat entry. Send it on any channel.
    ///   @itemlink show n        (a click on "[Name #n]" in anyone's chat) sends that snapshot's lines to the asker.
    ///
    /// The snapshot is text taken at link time, so a link stays right after the item is sold, changed or gone. The
    /// newest 5,000 are kept in memory; a restart forgets them ("That item link has expired.").
    /// </summary>
    public static class ItemLink
    {
        const int KEEP = 5000;
        sealed class Snap { public string Name, By; public List<string> Lines; }
        static readonly Dictionary<int, Snap> s_Snaps = new Dictionary<int, Snap>();
        static readonly Queue<int> s_Order = new Queue<int>();
        static readonly object s_Lock = new object();
        static int s_Next = 1;

        public static void Register()
        {
            CommandManager.TryAddCommand(Handle, "itemlink", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Item links in chat (the client's /link and its clickable [Item #n]).", "make 0xGUID | show n");
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
                var snap = new Snap { Name = Clean(item.GetNameWithMaterial()), By = me.Name.TrimStart('+'), Lines = Describe(item) };
                int id;
                lock (s_Lock)
                {
                    id = s_Next++;
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
            }
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
