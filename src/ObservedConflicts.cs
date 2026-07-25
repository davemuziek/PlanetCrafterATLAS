using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ATLAS
{
    /// <summary>
    /// The bridge between ATLAS's two halves. The Scanner says which methods COULD conflict
    /// from patch topology; the log archive says which ones actually threw. Cross-referencing
    /// them turns a wall of theoretical "medium" rows into the handful that have real evidence
    /// behind them - which is the honest version of "stress testing" for a mod set you can't
    /// safely invoke directly.
    /// </summary>
    internal static class ObservedConflicts
    {
        // Same frame shape ErrorClassifier uses: matches both the Unity logMessageReceived
        // form ("Type.Method () (at <hash>:0)") and the Mono form ("at Type.Method (args)").
        private static readonly Regex FrameRx =
            new Regex(@"(?:^|\n)[ \t]*(?:at[ \t]+)?([\w.<>+`\[\]]+)\.([\w<>`\[\]]+)[ \t]*\(",
                      RegexOptions.Compiled);

        /// <summary>
        /// Reads every archived log and returns the set of "Type.Method" labels that appear
        /// inside an error/fatal block. Reads only logs that already contain errors (ERR/CRASH
        /// in the name) - clean OK logs cannot contain a thrown conflict, so we skip them.
        /// </summary>
        public static HashSet<string> CollectObservedMethods(string archiveDir, out int logsScanned)
        {
            logsScanned = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(archiveDir) || !Directory.Exists(archiveDir)) return seen;

            string[] files;
            try { files = Directory.GetFiles(archiveDir, "*.log"); }
            catch { return seen; }

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (name.IndexOf("_ERR", StringComparison.Ordinal) < 0
                    && name.IndexOf("_CRASH", StringComparison.Ordinal) < 0)
                    continue;

                try
                {
                    logsScanned++;
                    // Pull frames from the whole file. An error block's frames are what we care
                    // about; scanning the whole file is simpler and the false-positive risk is
                    // low because conflict method names are specific.
                    var text = File.ReadAllText(path);
                    foreach (Match m in FrameRx.Matches(text))
                        seen.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
                }
                catch { /* unreadable log - skip */ }
            }

            return seen;
        }

        /// <summary>
        /// Annotates each conflict with how many archived logs mention its method, and fills
        /// the report-level roll-up. Does NOT rewrite Severity: a high-severity transpiler
        /// collision that has not fired yet is still dangerous, and demoting it because it is
        /// "unobserved" would be false comfort - especially on a fresh install with no logs.
        /// The evidence is presented alongside the tier, not folded into it.
        /// </summary>
        public static void Apply(ScanReport report, string archiveDir)
        {
            var observed = CollectObservedMethods(archiveDir, out int logsScanned);

            report.ArchiveLogCount = logsScanned;
            report.ArchiveChecked = logsScanned > 0;

            if (!report.ArchiveChecked) return;   // leave ObservedInLogs at -1 (unchecked)

            foreach (var c in report.Conflicts)
            {
                c.ObservedInLogs = observed.Contains(c.Method) ? 1 : 0;
                if (c.ObservedInLogs > 0) report.ObservedConflictCount++;
            }

            // Re-sort so anything with real evidence leads, then by severity, then noise.
            report.Conflicts.Sort((a, b) =>
            {
                var obs = b.ObservedInLogs.CompareTo(a.ObservedInLogs);
                if (obs != 0) return obs;
                var sev = ((int)b.Severity).CompareTo((int)a.Severity);
                return sev != 0 ? sev : b.Owners.Count.CompareTo(a.Owners.Count);
            });
        }
    }
}
