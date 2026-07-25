using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// The 0.7.1 answer to "I fixed my mod but the finding is still there." Detection stays
    /// baseline-anchored (a finding is still raised the moment the game moves), but every finding
    /// is then RE-VERIFIED against the current mods on each scan rather than replaying a saved
    /// verdict:
    ///
    ///  - <b>Reflection</b> findings self-heal. If the mod no longer reflects the missing member
    ///    (checked against the reflection sites ATLAS already scans each run), the fix took.
    ///  - <b>Renamed/removed method or type</b> findings self-heal via the live
    ///    <see cref="PatchTargetIndex"/>: if no current mod still targets the missing member, it is
    ///    fixed away. This is also where these findings finally get attributed to a mod.
    ///  - <b>Signature/body/content</b> changes become <see cref="DriftStatus.Review"/>: static
    ///    comparison cannot confirm a mod-side fix (a transpiler that still applies may still be
    ///    wrong), so they are never auto-cleared - they wait for an explicit Accept.
    ///
    /// A finding that was open last scan and is gone this scan is shown once as
    /// <see cref="DriftStatus.Resolved"/> - the confirmation the user asked for - then suppressed by
    /// a tiny state file so it does not linger. The direction of every uncertainty is the same:
    /// when in doubt, keep it visible. Nothing is ever silently cleared.
    /// </summary>
    internal static class DriftLiveStatus
    {
        private const char Sep = '\u0001';   // separator that cannot occur in a type or member name

        // ── status assignment ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets <see cref="DriftFinding.Status"/> on every finding, and attributes the
        /// method/type findings the baseline could not (it records no method owners) from the live
        /// patch-target index. Mutates the findings in place.
        /// </summary>
        public static void Assign(
            List<DriftFinding> findings, List<ReflRow> currentRefl, PatchTargetIndex patchTargets)
        {
            // What each mod currently reflects (owner + type + member), and what any mod currently
            // references at all (for treating an AccessTools-based manual patch target as "still
            // targeted" alongside the [HarmonyPatch] declarations).
            var reflNow = new HashSet<string>(StringComparer.Ordinal);
            var referencedNow = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rr in currentRefl)
            {
                var member = rr.Member == "-" ? "" : rr.Member;
                reflNow.Add(rr.Owner + Sep + rr.Type + Sep + member);
                referencedNow.Add(rr.Type);
                if (member.Length > 0) referencedNow.Add(rr.Type + "." + member);
            }

            foreach (var f in findings)
            {
                if (IsContentKind(f.Kind)) { f.Status = DriftStatus.Review; continue; }
                if (f.Kind == DriftKind.NotTracked) { f.Status = DriftStatus.Review; continue; }

                if (f.Origin == DriftOrigin.Reflection)
                {
                    var owner = f.Owners.Count > 0 ? f.Owners[0] : "";
                    var key = owner + Sep + f.MatchType + Sep + f.MatchName;
                    // Still reflected by that mod -> still broken. No longer reflected -> the mod
                    // re-pointed or dropped the call: fixed.
                    f.Status = reflNow.Contains(key) ? DriftStatus.Active : DriftStatus.Resolved;
                    continue;
                }

                // PatchMethod origin.
                if (f.Kind == DriftKind.BodyChanged || f.Kind == DriftKind.SignatureChanged)
                {
                    // Static comparison cannot tell whether the patch still behaves correctly.
                    f.Status = DriftStatus.Review;
                    AttributeMethod(f, patchTargets);
                    continue;
                }

                // TypeMissing / TargetMissing: fixed iff no current mod targets the missing member.
                var dotKey = f.MatchName.Length > 0 ? f.MatchType + "." + f.MatchName : f.MatchType;
                var referenced =
                    patchTargets.Keys.Contains(dotKey) || referencedNow.Contains(dotKey)
                    || (f.Kind == DriftKind.TypeMissing
                        && (patchTargets.Keys.Contains(f.MatchType) || referencedNow.Contains(f.MatchType)));

                f.Status = referenced ? DriftStatus.Active : DriftStatus.Resolved;
                AttributeMethod(f, patchTargets);
            }
        }

        /// <summary>Gives an unattributed method/type finding the owner(s) that declare its target.</summary>
        private static void AttributeMethod(DriftFinding f, PatchTargetIndex patchTargets)
        {
            if (f.Owners.Count > 0) return;
            var dotKey = f.MatchName.Length > 0 ? f.MatchType + "." + f.MatchName : f.MatchType;
            if (patchTargets.Owners.TryGetValue(dotKey, out var byMember)) f.Owners.AddRange(byMember);
            else if (patchTargets.Owners.TryGetValue(f.MatchType, out var byType)) f.Owners.AddRange(byType);
        }

        private static bool IsContentKind(DriftKind k) =>
            k == DriftKind.GroupAdded || k == DriftKind.GroupRemoved
            || k == DriftKind.GroupFieldChanged || k == DriftKind.NullCraftableInList;

        // ── reconcile against last scan (Resolved shows once, then clears) ───────────────

        /// <summary>
        /// Given the status-assigned findings and the set of keys already announced as resolved,
        /// returns the findings to display this scan and the set to persist. Active/Review pass
        /// through. A Resolved finding is shown the first time and remembered; on later scans, while
        /// it stays resolved, it is suppressed. If it regresses to Active it is shown again and
        /// forgotten, so a real regression can never hide behind a stale "resolved" mark.
        /// </summary>
        public static List<DriftFinding> Reconcile(
            List<DriftFinding> findings, HashSet<string> alreadyAnnounced, out HashSet<string> nextAnnounced)
        {
            var display = new List<DriftFinding>();
            nextAnnounced = new HashSet<string>(StringComparer.Ordinal);

            foreach (var f in findings)
            {
                if (f.Status == DriftStatus.Resolved)
                {
                    var k = Key(f);
                    if (alreadyAnnounced.Contains(k)) { nextAnnounced.Add(k); continue; }  // suppress
                    display.Add(f);                                                          // show once
                    nextAnnounced.Add(k);
                }
                else
                {
                    display.Add(f);   // Active / Review: always visible, never remembered as resolved
                }
            }

            return display;
        }

        /// <summary>Stable identity of a finding across scans - origin, member, and owners.</summary>
        public static string Key(DriftFinding f)
        {
            var owners = "";
            if (f.Owners.Count > 0)
            {
                var arr = f.Owners.ToArray();
                Array.Sort(arr, StringComparer.Ordinal);
                owners = string.Join(",", arr);
            }
            return (int)f.Origin + "|" + f.MatchType + "|" + f.MatchName + "|" + owners;
        }

        // ── announced-resolved state file ────────────────────────────────────────────────

        public static HashSet<string> Load(string path)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return set;
                foreach (var line in File.ReadAllLines(path))
                {
                    var s = line.Trim();
                    if (s.Length == 0 || s[0] == '#') continue;
                    set.Add(s);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS drift: could not read resolved-state: " + ex.Message); }
            return set;
        }

        public static void Save(string path, HashSet<string> keys)
        {
            try
            {
                var sb = new StringBuilder(1024);
                sb.AppendLine("# ATLAS drift resolved-state. Keys of findings already shown as Resolved,");
                sb.AppendLine("# suppressed on later scans until they regress or the build is accepted.");
                foreach (var k in keys) sb.AppendLine(k);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS drift: could not write resolved-state: " + ex.Message); }
        }

        /// <summary>Accepting the build is a clean slate; forget every announced-resolved key.</summary>
        public static void Wipe(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS drift: could not clear resolved-state: " + ex.Message); }
        }
    }
}
