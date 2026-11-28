using System;
using System.Collections.Generic;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Handlers;
using HarmonyLib;

namespace RevivalGuard
{
    /// <summary>Holtburg only, enforced server side.</summary>
    [HarmonyPatch(typeof(CharacterHandler), "CharacterCreateEx")]
    static class HoltburgOnly
    {
        internal const string P_ON = "rg_holtburg_only";
        static bool s_WarnedNoHome;

        static List<string> DatNames()
        {
            var l = new List<string>();
            var areas = DatManager.PortalDat?.CharGen?.StarterAreas;
            if (areas != null) foreach (var a in areas) l.Add(a.Name);
            return l;
        }

        static bool Prefix(ClientMessage message, Session session)
        {
            try
            {
                if (!PropertyManager.GetBool(P_ON, true).Item) return true;
                var stream = message.Payload.BaseStream;
                long pos = stream.Position;
                var info = new CharacterCreateInfo();
                info.Unpack(message.Payload);          // only to find where the create ends (and the name, for the log)
                long end = stream.Position;
                stream.Position = pos;                 // ACE's own Unpack reads it again from here

                int home = HoltburgOnlyRule.HomeIndex(PropertyManager.GetString("starter_towns_override").Item, DatNames());
                if (home < 0)
                {
                    if (!s_WarnedNoHome)
                    {
                        s_WarnedNoHome = true;
                        Mod.Log.Error("[RevivalGuard] HoltburgOnly: no starter area is named Holtburg (starter_towns_override or the DAT); creates keep the client's town");
                    }
                    return true;
                }
                if (HoltburgOnlyRule.Rewrite(stream, end, home, out uint before, out uint after))
                    Mod.Log.Info($"[RevivalGuard] HoltburgOnly: '{info.Name}' (account '{session?.Account}') asked for start area "
                                 + $"{before & HoltburgOnlyRule.AREA_MASK}; set to Holtburg ({home}), 0x{before:X2} -> 0x{after:X2}");
                stream.Position = pos;
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"[RevivalGuard] HoltburgOnly: {e.Message}; letting ACE decide");
                return true;
            }
        }
    }
}
