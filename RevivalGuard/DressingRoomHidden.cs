using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevivalGuard
{
    /// <summary>Inspire looks staff marked "Not style".</summary>
    public sealed class InspireHidden
    {
        public HashSet<string> All { get; set; } = new HashSet<string>();
        public Dictionary<string, HashSet<string>> By { get; set; } = new Dictionary<string, HashSet<string>>();

        public bool IsHidden(uint character, IEnumerable<string> ids)
        {
            if (ids == null) return false;
            By.TryGetValue(character.ToString(), out var mine);
            return ids.Any(i => All.Contains(i) || (mine != null && mine.Contains(i)));
        }

        /// <summary>Hides these ids for one character, or for everyone.</summary>
        public int Hide(uint character, IEnumerable<string> ids, bool everyone)
        {
            HashSet<string> to;
            if (everyone) to = All;
            else if (!By.TryGetValue(character.ToString(), out to)) By[character.ToString()] = to = new HashSet<string>();
            int n = 0;
            foreach (var i in ids ?? Enumerable.Empty<string>()) if (!string.IsNullOrEmpty(i) && to.Add(i)) n++;
            return n;
        }

        public int CountFor(uint character) => All.Count + (By.TryGetValue(character.ToString(), out var m) ? m.Count(i => !All.Contains(i)) : 0);

        public static InspireHidden Load(string file)
        {
            try
            {
                if (file != null && File.Exists(file))
                {
                    var h = JsonSerializer.Deserialize<InspireHidden>(File.ReadAllText(file));
                    if (h != null)
                    {
                        h.All ??= new HashSet<string>();
                        h.By ??= new Dictionary<string, HashSet<string>>();
                        return h;
                    }
                }
            }
            catch { }   // a broken file hides nothing rather than stopping Inspire
            return new InspireHidden();
        }

        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, true);
        }
    }
}
