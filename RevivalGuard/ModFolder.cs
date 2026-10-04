using ACE.Server.Mods;

namespace RevivalGuard
{
    /// <summary>
    /// The folder the mod dll lives in (~/ace/ace-runtime/Mods/RevivalGuard on the shard). Files the
    /// mod owns go beside the dll: deploy-revivalguard.sh copies only the dll, so they survive a
    /// deploy, and they need no database table. SeasonalOverrides.json was the first; the audit
    /// trail and character snapshots follow it.
    ///
    /// GetModContainerByName is exact-match on purpose; a mod that is still initialising (this is
    /// called from Mod.Initialize) falls back to ModPath/RevivalGuard, which is the same folder by
    /// ACE's convention: FolderName is the dll name.
    /// </summary>
    internal static class ModFolder
    {
        public static string Path()
        {
            string folder = null;
            try { folder = ModManager.GetModContainerByName("RevivalGuard", allowPartial: false)?.FolderPath; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) folder = System.IO.Path.Combine(ModManager.ModPath, "RevivalGuard");
            return folder;
        }

        /// <summary>A subfolder beside the dll, created if missing. Null (and a log line) if it cannot be.</summary>
        public static string Sub(string name)
        {
            var dir = System.IO.Path.Combine(Path(), name);
            try { Directory.CreateDirectory(dir); return dir; }
            catch (Exception e)
            {
                Mod.Log.Error($"[RevivalGuard] cannot create {dir}: {e.Message}");
                return null;
            }
        }
    }
}
