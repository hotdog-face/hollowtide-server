using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace RevivalGuard
{
    /// <summary>Hollowtide's side of the dereth channel</summary>
    static class DerethChatHost
    {
        internal static void Register()
        {
            DerethChat.Bridge.LogInfo = s => Mod.Log.Info("[RevivalGuard] " + s);
            DerethChat.Bridge.LogWarn = s => Mod.Log.Warn("[RevivalGuard] " + s);
            DerethChat.Bridge.IsBotHook = (Player p) => Mercenaries.IsMerc(p);
            DerethChat.Bridge.ChatLogHook = (server, name, text) => ChatLog.Write("Dereth", $"{name} ({server})", text);
            // what our players read: the client calls it the Cross-Server tab and /crossserver
            DerethChat.Bridge.ChannelName = "cross-server chat";
            DerethChat.Bridge.PlayerHint = "/crossserver";
            DerethChat.Bridge.Aliases = new[] { "crossserver" };
            DerethChat.Bridge.DeliverHook = Deliver;
            DerethChat.Bridge.StateHook = p => SendState(p?.Session);
            DerethChat.Bridge.Start(ModFolder.Path());
        }

        static bool Deliver(Player p, string server, string name, string text, bool bot)
        {
            var s = p?.Session;
            if (!DerethChat.Bridge.ClientShowsChannel(s) || !ModChannel.Capable(s)) return false;
            ModChannel.Send(s, "dereth", "{\"s\":" + Telemetry.J(server) + ",\"n\":" + Telemetry.J(name) + ",\"t\":" + Telemetry.J(text) + ",\"b\":" + (bot ? "true" : "false") + "}");
            return true;
        }

        /// <summary>ModChannel.Hello: the channel exists on this shard, and whether this character hears it.</summary>
        internal static void Hello(Session session) => SendState(session);

        static void SendState(Session s)
        {
            try
            {
                if (!DerethChat.Bridge.On || s?.Player == null || !ModChannel.Capable(s)) return;
                ModChannel.Send(s, "derethstate", "{\"on\":" + (DerethChat.Bridge.IsOn(s.Player) ? "true" : "false") + ",\"cmd\":" + Telemetry.J(DerethChat.Bridge.Command) + "}");
            }
            catch (System.Exception e) { Mod.Log.Warn($"[RevivalGuard] DerethChat state: {e.Message}"); }
        }
    }
}
