using System;
using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Away message.</summary>
    public static class AwayMessage
    {
        internal const PropertyBool ON = (PropertyBool)29180, AUTO = (PropertyBool)29182;
        internal const PropertyString MSG = (PropertyString)29181;
        internal static readonly AwayMessageRule Rule = new AwayMessageRule();
        const string CHANNEL = "away";

        internal static void Register()
        {
            CommandManager.TryAddCommand(Typed, "away", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Away message: anyone who tells you gets it automatically (once every 5 minutes each). /away off when you are back.", "[<message> | on | off]");
            CommandManager.TryAddCommand(Option, "rgaway", AccessLevel.Player, CommandHandlerFlag.RequiresWorld,
                "Our client's away-message switch (both Assistants' away boxes).", "[1 | 0 | msg <text> | assist 1|0]");
            Mod.Log.Info("[RevivalGuard] AwayMessage: on; /away");
        }

        static bool IsOn(Player p) => p.GetProperty(ON) == true;
        static string Msg(Player p) => p.GetProperty(MSG);

        static void Tell(Session s, string line) => s?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

        /// <summary>"/away ..." typed: always answered in chat; the client's boxes follow.</summary>
        static void Typed(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            string all = string.Join(" ", parameters ?? Array.Empty<string>()).Trim();
            string a = all.ToLowerInvariant();
            if (a == "off") SetOn(p, false, false);
            else if (a == "on") SetOn(p, true, false);
            else if (a.Length == 0) SetOn(p, !IsOn(p), false);
            else { SetMsg(p, all); SetOn(p, true, false); }
            Push(session);
        }

        /// <summary>"@rgaway": the client's silent form.</summary>
        static void Option(Session session, params string[] parameters)
        {
            var p = session?.Player;
            if (p == null) return;
            var a = parameters ?? Array.Empty<string>();
            string verb = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            if (verb == "1") SetOn(p, true, false);
            else if (verb == "0") SetOn(p, false, false);
            else if (verb == "msg")
            {
                SetMsg(p, string.Join(" ", a, 1, a.Length - 1));
                Tell(session, $"Away message saved: {Msg(p) ?? AwayMessageRule.DEFAULT_MESSAGE}");
            }
            else if (verb == "assist" && a.Length > 1)
            {
                // The Assistant started or stopped.
                if (a[1] == "1" && !IsOn(p)) SetOn(p, true, true);
                else if (a[1] == "0" && IsOn(p) && p.GetProperty(AUTO) == true) SetOn(p, false, true);
            }
            Push(session);
        }

        static void SetMsg(Player p, string typed)
        {
            var m = AwayMessageRule.Clean(typed);
            if (m == null) p.RemoveProperty(MSG); else p.SetProperty(MSG, m);
            p.CharacterChangesDetected = true;
        }

        static void SetOn(Player p, bool on, bool byAssistant)
        {
            bool was = IsOn(p);
            if (on) { p.SetProperty(ON, true); if (byAssistant && !was) p.SetProperty(AUTO, true); else if (!byAssistant) p.RemoveProperty(AUTO); }
            else { p.RemoveProperty(ON); p.RemoveProperty(AUTO); }
            p.CharacterChangesDetected = true;
            var s = p.Session;
            if (on && !was)
                Tell(s, (byAssistant ? "Your away message is on while the Assistant runs. " : "Your away message is on. ")
                    + "Anyone who tells you gets, once every 5 minutes: " + AwayMessageRule.Line(p.Name, Msg(p)));
            else if (!on && was)
            {
                int n = Rule.Back(p.Guid.Full);
                Tell(s, "Your away message is off." + (n == 0 ? "" : n == 1 ? " 1 person told you while you were away."
                                                                         : $" {n} people told you while you were away."));
            }
            else if (!on) Tell(s, "Your away message is off. Type /away and a message to set one.");
            else if (!byAssistant) Tell(s, "Your away message is on: " + AwayMessageRule.Line(p.Name, Msg(p)));
            if (was != on) Mod.Log.Info($"[RevivalGuard] AwayMessage: {p.Name} {(on ? "away" : "back")}{(byAssistant ? " (Assistant)" : "")}");
        }

        /// <summary>The state for our client's boxes: {"on":bool,"auto":bool,"msg":"..."} (msg null = the default).</summary>
        internal static void Push(Session session)
        {
            var p = session?.Player;
            if (p == null || !ModChannel.Capable(session)) return;
            ModChannel.Send(session, CHANNEL, "{\"on\":" + (IsOn(p) ? "true" : "false") + ",\"auto\":" + (p.GetProperty(AUTO) == true ? "true" : "false")
                + ",\"msg\":" + Telemetry.J(Msg(p)) + "}");
        }

        /// <summary>From GameActionTell's postfix: a tell told from from just reached to.</summary>
        internal static void Delivered(Player from, Player to, string told)
        {
            if (from == null || to == null || from == to || !IsOn(to) || to.IsAfk) return;   // retail AFK: ACE answered
            if (!Rule.Answer(from.Guid.Full, to.Guid.Full, told, DateTime.UtcNow)) return;
            from.Session?.Network.EnqueueSend(new GameMessageSystemChat(AwayMessageRule.Line(to.Name, Msg(to)), ChatMessageType.Tell));
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
        static class OnEnter
        {
            static void Postfix(Player __instance)
            {
                try
                {
                    var p = __instance;
                    if (p == null || !IsOn(p)) return;
                    if (p.GetProperty(AUTO) == true)
                    {
                        // The Assistant is off at login; an away it turned on ends with the session.
                        p.RemoveProperty(ON); p.RemoveProperty(AUTO); p.CharacterChangesDetected = true;
                        return;
                    }
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(8);
                    chain.AddAction(p, () => Tell(p.Session, "Your away message is still on: " + AwayMessageRule.Line(p.Name, Msg(p)) + " Type /away off when you are back."));
                    chain.EnqueueChain();
                }
                catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AwayMessage enter: {e.Message}"); }
            }
        }
    }

    /// <summary>After ACE's GameActionTell: answer for an away target.</summary>
    [HarmonyPatch(typeof(GameActionTell), nameof(GameActionTell.Handle))]
    static class AwayMessageTell
    {
        static void Prefix(ClientMessage clientMessage, out long __state)
        {
            __state = -1;
            try { if (clientMessage?.Payload != null) __state = clientMessage.Payload.BaseStream.Position; } catch { }
        }

        static void Postfix(ClientMessage clientMessage, Session session, bool __runOriginal, long __state)
        {
            if (!__runOriginal || __state < 0 || clientMessage?.Payload == null || session?.Player == null) return;
            try
            {
                var from = session.Player;
                if (from.IsGagged) return;
                long end = clientMessage.Payload.BaseStream.Position;
                clientMessage.Payload.BaseStream.Position = __state;
                string told = clientMessage.Payload.ReadString16L();
                string name = clientMessage.Payload.ReadString16L().Trim();
                clientMessage.Payload.BaseStream.Position = end;
                var to = PlayerManager.GetOnlinePlayer(name);
                if (to == null || to.SquelchManager.Squelches.Contains(from, ChatMessageType.Tell)) return;
                AwayMessage.Delivered(from, to, told);
            }
            catch (Exception e) { Mod.Log.Warn($"[RevivalGuard] AwayMessage tell: {e.Message}"); }
        }
    }
}
