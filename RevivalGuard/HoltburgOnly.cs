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
    /// <summary>
    /// HOLTBURG ONLY, ENFORCED SERVER SIDE (owner 2026-11-09: "only one starter town is available. holtburg only,
    /// remove the other options"; Holtburg is the entry point and hub). The client's creator no longer offers a town
    /// (AcCharGenHud: the Town page pins only Holtburg), but older clients, retail clients and anyone with a packet
    /// editor still send whatever index they like, so the create is rewritten here: StartArea's index becomes Holtburg's
    /// before ACE's own CharacterCreateEx reads it. The 0x80 (skip the Academy) and 0x40 (Samurai) flags are kept.
    ///
    /// The payload is a seekable, writable MemoryStream (ACE's MessageBuffer and NetworkSession both build one over the
    /// fragment bytes). Like NameGuard and OlthoiChargenGuard, and the Dereth Network mod's own prefix on this method,
    /// this reads the create and puts the position back; unlike them it also writes four bytes in place, which none of
    /// the others read (NameGuard the name, OlthoiChargenGuard the heritage, Dereth Network the name). Prefix order does
    /// not matter: whichever runs first, ACE's Unpack runs last and sees Holtburg.
    ///
    /// Holtburg's index is resolved by name (HoltburgOnlyRule.HomeIndex): the shard's starter_towns_override list when
    /// set (the box test shard runs "Holtburg,Eastham,Cragstone,Arwic"), else the DAT's StarterAreas. The Holtburg
    /// Training Academy's exit portal already leads to Holtburg, so the Academy route needs no change.
    ///   @modifybool rg_holtburg_only false   the client's choice again (from the next create)
    /// </summary>
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
