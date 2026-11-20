using System.Collections.Generic;

namespace RevivalGuard
{
    /// <summary>HelmOverHood's choice, pure (no ACE types), so tools/nettest compiles and checks it by path.
    /// A worn robe whose ClothingTable puts a hood on the head (part 16) and a drawn helm both land in the ObjDesc;
    /// the helm wins: the robe's head model and its head textures leave the desc, the helm's (or the wearer's own head)
    /// stay. Ticket 261120-0019.</summary>
    public static class HelmOverHoodRule
    {
        /// <summary>The head on every playable setup (ACE GetVisualPriority's HUMAN_HEAD; the bare head ACE adds).</summary>
        public const int HEAD = 16;
        /// <summary>The null part (ACE AddSetupAsClothingBase): a table entry with it draws nothing there.</summary>
        public const uint NULL_PART = 0x010001EC;

        /// <summary>One worn piece's entry for this body: its parts (index, model) and texture swaps (index, old, new).</summary>
        public sealed class Piece
        {
            public bool IsHelm;
            public List<(int Index, uint Model)> Parts = new List<(int, uint)>();
            public List<(int Index, uint Old, uint New)> Textures = new List<(int, uint, uint)>();
        }

        /// <summary>A helm draws when its entry for this body has a real model (helmets on a Tumerok have none).</summary>
        public static bool Draws(Piece helm)
        {
            foreach (var p in helm.Parts) if (p.Model != NULL_PART) return true;
            return false;
        }

        /// <summary>What to take out of the desc: the hooded pieces' head models and head textures, unless a drawn helm
        /// uses the very same one (the Hood helm shares the Faran Robe with Hood's head model).</summary>
        public static (HashSet<uint> Models, HashSet<(uint Old, uint New)> Textures) Drop(IEnumerable<Piece> worn)
        {
            var models = new HashSet<uint>();
            var tex = new HashSet<(uint, uint)>();
            var keepModels = new HashSet<uint>();
            var keepTex = new HashSet<(uint, uint)>();
            bool helm = false;
            var hooded = new List<Piece>();
            foreach (var w in worn)
            {
                if (w.IsHelm)
                {
                    if (!Draws(w)) continue;
                    helm = true;
                    foreach (var p in w.Parts) if (p.Index == HEAD) keepModels.Add(p.Model);
                    foreach (var t in w.Textures) if (t.Index == HEAD) keepTex.Add((t.Old, t.New));
                }
                else hooded.Add(w);
            }
            if (!helm) return (models, tex);
            foreach (var w in hooded)
            {
                bool hood = false;
                foreach (var p in w.Parts)
                    if (p.Index == HEAD && p.Model != NULL_PART && !keepModels.Contains(p.Model)) { models.Add(p.Model); hood = true; }
                if (!hood) continue;
                foreach (var t in w.Textures)
                    if (t.Index == HEAD && !keepTex.Contains((t.Old, t.New))) tex.Add((t.Old, t.New));
            }
            return (models, tex);
        }
    }
}
