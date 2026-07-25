using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace ATLAS
{
    /// <summary>
    /// The runtime-truth axis (0.11.0). Static drift/compat ask what the game HAS; this asks what
    /// actually HAPPENED once every mod loaded: did each mod's declared Harmony patches apply, and
    /// did every mod even load?
    ///
    /// TIMING is load-bearing. Harmony's applied-patch registry is only complete after every
    /// plugin's Awake has run; ATLAS's own Awake runs mid-load, so enumerating there would miss
    /// every mod that loads after ATLAS and false-flag its patches as "not applied". So this runs at
    /// SCAN time (scene load / session end), never at Awake - the opposite of the compat check,
    /// which correctly runs at Awake because game types exist regardless of mod load order.
    ///
    /// Read-only: it reads Harmony's live registry and the archived logs, and writes nothing but the
    /// report. Two signals, both needing no baseline and no game update:
    ///  - declared-but-not-applied: a patch a mod asked for that is absent from the registry - the
    ///    "member exists but the patch still did not take" case compat is structurally blind to;
    ///  - plugin load failures mined from the logs: a mod that threw during load is absent from the
    ///    roster everything else scans, so this is the only surface that can name it.
    /// </summary>
    internal static class RuntimePatchCheck
    {
        // BepInEx chainloader plugin load/start failure, e.g.
        //   [Error  :   BepInEx] Error loading [Celestial Cycle 1.2.3] : System.Exception: ...
        // Loose on purpose - the exact wording drifts across BepInEx versions - and presented as
        // evidence ("the log shows"), never as a verdict, so a missed variant is a quiet miss and a
        // matched line is never a false accusation. The version token (starts with a digit) anchors
        // the end of the plugin name.
        private static readonly Regex LoadFailRx = new Regex(
            @"\[(?:Error|Fatal)\s*:\s*BepInEx\s*\][^\n]*?(?:load|start)[^\n]*?\[([^\]]+?)\s+[0-9][\w.]*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static void Run(ScanReport report, IReadOnlyList<DeclaredPatchTarget> declared, string archiveDir)
        {
            report.PatchCheckRan = true;

            // ── applied set: owner-agnostic, name-level ────────────────────────────────
            // "Did ANYONE patch this member?" sidesteps the Harmony-owner-id (usually a GUID) vs.
            // Cecil-assembly-name mismatch: if nobody patched a method a mod declared, that mod's
            // patch failed regardless of who owns what. Normalise nested-type '+' (reflection) to
            // '/' (Cecil) so the applied set matches DeclaredPatchTarget.Type.
            var applied = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<MethodBase> patched;
            try { patched = Harmony.GetAllPatchedMethods(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("ATLAS patch-verify: could not enumerate patched methods: " + ex.Message);
                patched = Array.Empty<MethodBase>();
            }
            foreach (var m in patched)
            {
                if (m?.DeclaringType == null) continue;
                var full = (m.DeclaringType.FullName ?? "").Replace('+', '/');
                if (full.Length == 0) continue;
                applied.Add(full + "." + m.Name);
            }

            // ── plugin load failures from the archive ──────────────────────────────────
            var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lf in MineLoadFailures(archiveDir))
            {
                report.PluginLoadFailures.Add(lf);
                failedNames.Add(lf.Plugin);
            }

            // ── dedup vs compat: a missing member is compat's job, not double-reported here ──
            var skip = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in report.CompatFindings)
            {
                if (f.Kind == DriftKind.TypeMissing) skip.Add(f.MatchType);   // whole type gone
                skip.Add(f.MatchType + "." + f.MatchName);                    // specific member
            }

            // ── reconcile declared vs applied ──────────────────────────────────────────
            var notApplied = new Dictionary<string, List<string>>(StringComparer.Ordinal); // member -> owners
            foreach (var t in declared)
            {
                if (t.Method.Length == 0) continue;         // type-level declaration: not method-verifiable
                report.PatchDeclaredChecked++;

                var member = t.Type + "." + t.Method;
                if (applied.Contains(member)) { report.PatchAppliedVerified++; continue; }

                if (skip.Contains(member) || skip.Contains(t.Type)) continue;   // compat already reports it
                if (!notApplied.TryGetValue(member, out var owners)) { owners = new List<string>(); notApplied[member] = owners; }
                if (!owners.Contains(t.Owner)) owners.Add(t.Owner);
            }

            foreach (var kv in notApplied)
            {
                var owners = kv.Value;
                owners.Sort(StringComparer.Ordinal);

                // Corroborated when a declaring mod also shows a load/patch error in the archive.
                var corroborated = false;
                foreach (var o in owners) if (failedNames.Contains(o)) { corroborated = true; break; }

                var f = new PatchApplyFinding
                {
                    Member = kv.Key,
                    Severity = corroborated ? Severity.High : Severity.Low,
                    LogCorroborated = corroborated,
                    Detail = corroborated
                        ? "This mod declares a Harmony patch here, the patch is not in Harmony's live registry, "
                          + "and the mod logged a load/patch error - so the patch did not apply."
                        : "This mod declares a Harmony patch here, but ATLAS did not see it in Harmony's live "
                          + "registry. Often benign - a patch applied only under a config toggle, or a target "
                          + "resolved dynamically at runtime - but it can also be a silent failure. Confirm the "
                          + "feature that patch drives actually works.",
                };
                foreach (var o in owners) f.Owners.Add(o);
                report.PatchApplyFindings.Add(f);
                if (f.Severity == Severity.High) report.PatchApplyConfirmedCount++;
            }

            // High (confirmed) first, then Low; within a tier, by member for a stable read.
            report.PatchApplyFindings.Sort((a, b) =>
            {
                var s = ((int)b.Severity).CompareTo((int)a.Severity);
                return s != 0 ? s : string.CompareOrdinal(a.Member, b.Member);
            });
        }

        /// <summary>
        /// Pulls plugin load/start failures out of the ERR/CRASH archive logs (clean OK logs cannot
        /// contain one), one row per distinct failed plugin, carrying the trailing message for
        /// context. Mirrors the ObservedConflicts archive walk - same file filter, read-only.
        /// </summary>
        private static List<PluginLoadFailure> MineLoadFailures(string archiveDir)
        {
            var outp = new List<PluginLoadFailure>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(archiveDir) || !Directory.Exists(archiveDir)) return outp;

            string[] files;
            try { files = Directory.GetFiles(archiveDir, "*.log"); }
            catch { return outp; }

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (name.IndexOf("_ERR", StringComparison.Ordinal) < 0
                    && name.IndexOf("_CRASH", StringComparison.Ordinal) < 0)
                    continue;

                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }

                foreach (Match m in LoadFailRx.Matches(text))
                {
                    var plugin = m.Groups[1].Value.Trim();
                    if (plugin.Length == 0 || !seen.Add(plugin)) continue;

                    outp.Add(new PluginLoadFailure
                    {
                        Plugin = plugin,
                        Error = TailMessage(text, m.Index + m.Length),
                        LogName = name,
                    });
                }
            }
            return outp;
        }

        /// <summary>The rest of the matched line after the plugin bracket, trimmed and length-capped.</summary>
        private static string TailMessage(string text, int from)
        {
            if (from >= text.Length) return "";
            var end = text.IndexOf('\n', from);
            var tail = end < 0 ? text.Substring(from) : text.Substring(from, end - from);
            tail = tail.TrimStart(' ', ':', '\t').TrimEnd();
            if (tail.Length > 160) tail = tail.Substring(0, 160) + "…";
            return tail;
        }
    }
}
