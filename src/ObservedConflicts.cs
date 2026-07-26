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

        // Harmony/MonoMod render a PATCHED method's stack frame as a dynamic-method wrapper:
        //   (wrapper dynamic-method) SpaceCraft.ActionGrabable.DMD<SpaceCraft.ActionGrabable::OnAction>(...)
        // FrameRx cannot read that - the "::" and the ">(" do not fit "Type.Method(" - so the only
        // frame it captures from such a throw is the UNPATCHED caller, and a conflict on the patched
        // method (which every conflict is) would never corroborate. This recovers the inner declaring
        // type + method from the "<Namespace.Type::Method>" token so those frames count too.
        private static readonly Regex WrapperFrameRx =
            new Regex(@"<\s*([\w.<>+`\[\]]+)::([\w<>`\[\]]+)\s*>", RegexOptions.Compiled);

        /// <summary>
        /// Reads every archived log and returns the set of "Type.Method" labels that appear
        /// AT THE THROW SITE of an error/fatal block. Reads only logs that already contain
        /// errors (ERR/CRASH in the name) - clean OK logs cannot contain a thrown conflict,
        /// so we skip them.
        ///
        /// Only the frames nearest the throw are harvested, never pass-through ancestors.
        /// A stack trace lists the throw site at the TOP and every frame below it is a caller
        /// the exception merely unwound through. Harmony renders a *patched* method as a
        /// dynamic-method wrapper frame, so a patched ancestor - e.g. an input dispatcher the
        /// keypress passed through on its way to the real fault deep below - would otherwise be
        /// harvested and its conflict stamped "seen in logs" though it never threw. (Concretely:
        /// a NullReference thrown in Netcode's __endSendRpc, unwinding up through the patched
        /// PlayerInputDispatcher.OnActionDispatcher, used to falsely corroborate the NAVIGATOR /
        /// zARCHITECT conflict on that dispatcher.) Harvesting all frames over-attributes exactly
        /// as badly as 0.15.0 under-attributed - both are the "which frame is the culprit"
        /// problem, one erring long, one short. Per stack we therefore keep only:
        ///   - the throw-site frame itself, if it renders plain ("Type.Method(", the Mono form), and
        ///   - the first (nearest-the-throw) "&lt;Type::Method&gt;" Harmony wrapper frame.
        /// Ancestors of both kinds are dropped.
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
                    HarvestThrowSites(File.ReadAllText(path), seen);
                }
                catch { /* unreadable log - skip */ }
            }

            return seen;
        }

        /// <summary>
        /// Walks the log a line at a time, grouping frames into stacks and adding only the
        /// throw-site frames of each. Frames are contiguous; any non-frame line (the exception
        /// header, a blank line, an interleaved [Info]/[Warning] entry, the "Stack trace:"
        /// label) closes the current stack, so the next frame line opens a fresh one with both
        /// slots empty.
        /// </summary>
        private static void HarvestThrowSites(string text, HashSet<string> seen)
        {
            bool sawAnyFrame = false;   // has this stack produced a frame yet? (first == throw site)
            bool haveWrapper = false;   // has this stack contributed its nearest-throw wrapper yet?

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                var wrap = WrapperFrameRx.Match(line);
                // A wrapper frame line is a wrapper, not a plain frame - only test FrameRx when
                // this line is not a wrapper, so the inner "<Type::Method>" token can't also be
                // misread as a plain "Type.Method(" frame.
                var plain = wrap.Success ? Match.Empty : FrameRx.Match(line);

                if (!wrap.Success && !plain.Success)
                {
                    // Not a stack frame - close the current stack.
                    sawAnyFrame = false;
                    haveWrapper = false;
                    continue;
                }

                if (wrap.Success)
                {
                    // Nearest-throw patched frame: the first wrapper in the stack, ancestors dropped.
                    if (!haveWrapper)
                    {
                        seen.Add(wrap.Groups[1].Value + "." + wrap.Groups[2].Value);
                        haveWrapper = true;
                    }
                }
                else if (!sawAnyFrame)
                {
                    // The throw site itself, only when it renders as a plain frame (topmost of the
                    // stack). Plain frames below the top are pass-through callers - never added.
                    seen.Add(plain.Groups[1].Value + "." + plain.Groups[2].Value);
                }

                sawAnyFrame = true;
            }
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
