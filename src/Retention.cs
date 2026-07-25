using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;

namespace ATLAS
{
    internal static class OrphanRecovery
    {
        /// <summary>
        /// Any ".active" file left on disk belongs to a session that never reached its exit
        /// hook - a hard crash, a kill, or a power loss. Classify it by scanning the text
        /// (the one place where reading the file after the fact is the right tool) and
        /// rename it with a CRASH tag.
        /// </summary>
        public static void FinalizeOrphans(string dir, ManualLogSource log)
        {
            string[] candidates;
            try { candidates = Directory.GetFiles(dir, "*.active"); }
            catch (Exception ex) { log.LogWarning("Could not list archive folder: " + ex.Message); return; }

            foreach (var path in candidates)
            {
                try
                {
                    // A second running instance still holds its own .active file open.
                    // Testing the lock is more reliable than recording and re-checking a PID.
                    if (IsStillHeld(path)) continue;

                    var sig = ErrorClassifier.ScanFile(path, out int errorCount);
                    var started = File.GetCreationTime(path);

                    var name = NameBuilder.Build(started, true, Math.Max(errorCount, 1), sig);
                    var target = NameBuilder.MakeUnique(Path.Combine(dir, name));
                    File.Move(path, target);

                    log.LogWarning("Recovered crashed session -> " + Path.GetFileName(target));
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Could not recover {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }

        private static bool IsStillHeld(string path)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    internal static class Retention
    {
        public static void Prune(string dir, int maxError, int maxHealthy, ManualLogSource log)
        {
            try
            {
                var files = Directory.GetFiles(dir, "*.log");
                PruneGroup(files.Where(IsProblem), maxError, log);
                PruneGroup(files.Where(f => !IsProblem(f)), maxHealthy, log);
            }
            catch (Exception ex)
            {
                log.LogWarning("Retention pass failed: " + ex.Message);
            }
        }

        private static bool IsProblem(string path)
        {
            var name = Path.GetFileName(path);
            return name.IndexOf("_ERR", StringComparison.Ordinal) >= 0
                || name.IndexOf("_CRASH", StringComparison.Ordinal) >= 0;
        }

        private static void PruneGroup(IEnumerable<string> group, int keep, ManualLogSource log)
        {
            if (keep < 0) return;

            foreach (var path in group.OrderByDescending(File.GetLastWriteTimeUtc).Skip(keep))
            {
                try
                {
                    File.Delete(path);
                    log.LogInfo("Pruned old archive " + Path.GetFileName(path));
                }
                catch
                {
                    // A locked or missing file is not worth failing startup over.
                }
            }
        }
    }
}
