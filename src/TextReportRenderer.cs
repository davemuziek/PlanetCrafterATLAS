using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// One of (eventually) several renderers over ScanReport. This one writes a plain-text
    /// file a player can open and paste into a Nexus bug report. It reads the report and
    /// nothing else - the same object will later feed an in-game panel unchanged.
    /// </summary>
    internal static class TextReportRenderer
    {
        public static string Write(ScanReport report, string directory, int keep)
        {
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var path = Path.Combine(directory, $"ModScan_{stamp}.txt");

            var sb = new StringBuilder(8 * 1024);
            Header(sb, report);
            Summary(sb, report);
            UpdateImpact(sb, report);
            Compatibility(sb, report);
            PatchVerify(sb, report);
            Coverage(sb, report);
            LogActivitySection(sb, report);
            Conflicts(sb, report);
            Keybinds(sb, report);
            MissingDeps(sb, report);
            Ignored(sb, report);
            Plugins(sb, report);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Prune(directory, keep);
            return path;
        }

        /// <summary>Rolling backups: keep the newest N reports, oldest pruned first.</summary>
        private static void Prune(string directory, int keep)
        {
            if (keep < 1) return;
            try
            {
                var old = Directory.GetFiles(directory, "ModScan_*.txt")
                                   .OrderByDescending(File.GetLastWriteTimeUtc)
                                   .Skip(keep);
                foreach (var f in old)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("scan report prune failed: " + ex.Message);
            }
        }

        private static void Header(StringBuilder sb, ScanReport r)
        {
            sb.AppendLine("==================================================");
            sb.AppendLine("  ATLAS scan report");
            sb.AppendLine("  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine($"  Game {r.GameVersion}  |  Unity {r.UnityVersion}");
            sb.AppendLine($"  {r.PluginCount} plugins loaded");
            sb.AppendLine("==================================================");
            sb.AppendLine();
        }

        private static void Summary(StringBuilder sb, ScanReport r)
        {
            sb.AppendLine("SUMMARY");
            sb.AppendLine($"  High-severity conflicts   : {r.HighCount}");
            sb.AppendLine($"  Medium-severity conflicts : {r.MediumCount}");
            sb.AppendLine($"  Low / informational       : {r.LowCount}");
            sb.AppendLine($"  Missing dependencies      : {r.MissingDependencies.Count}");
            sb.AppendLine($"  Keybind overlaps          : {r.BindOverlaps.Count}");

            if (r.ArchiveChecked)
            {
                sb.AppendLine($"  Seen in error logs        : {r.ObservedConflictCount} "
                            + $"(cross-referenced against {r.ArchiveLogCount} archived error log(s))");
            }
            else
            {
                sb.AppendLine("  Seen in error logs        : not checked (no archived error logs yet)");
            }
            sb.AppendLine();

            if (r.HighCount == 0 && r.MissingDependencies.Count == 0)
                sb.AppendLine("  No high-severity conflicts or missing dependencies found.");
            if (r.ArchiveChecked && r.ObservedConflictCount == 0 && r.Conflicts.Count > 0)
                sb.AppendLine("  None of the listed conflicts have appeared in an archived error log,"
                            + " so they are theoretical on this install.");
            sb.AppendLine();
        }

        /// <summary>
        /// The sharpest two-second question ATLAS answers: did a game update move the ground under
        /// the mods? Rendered near the top for that reason. When Drift has anything to say it
        /// outranks everything below it - a moved patch target is the cause, and the conflict and
        /// keybind sections are downstream of it. (In 0.6.0 this same content fills the reserved
        /// Update Impact section of the HTML renderer; here it is the text-report equivalent.)
        /// </summary>
        private static void UpdateImpact(StringBuilder sb, ScanReport r)
        {
            // Drift disabled entirely: emit nothing, exactly like the section did not exist.
            if (!r.DriftChecked && r.DriftCodeState.Length == 0 && r.DriftContentState.Length == 0)
                return;

            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("UPDATE IMPACT  (drift - did a game update move the ground under your mods?)");
            sb.AppendLine("--------------------------------------------------");

            sb.AppendLine("  Code surface    : " + StateLine(r.DriftCodeState, r.DriftCodeDetail));
            sb.AppendLine("  Content surface : " + StateLine(r.DriftContentState, r.DriftContentDetail));

            if (r.GameBuildChanged)
                sb.AppendLine($"  Game build CHANGED: baseline {r.BaselineGameVersion} (mvid {Short(r.BaselineMvid)}) "
                            + $"-> now {r.GameVersion} (mvid {Short(r.CurrentMvid)}).");
            if (r.PluginRosterChanged)
                sb.AppendLine("  Plugin roster changed since the baseline, so added/removed groups cannot be "
                            + "cleanly attributed.");

            sb.AppendLine($"  Methods tracked : {r.DriftMethodsTracked}   |   Unresolved reflection sites: "
                        + $"{r.DriftUnresolvedReflectionSites}   |   Part A scan: {r.DriftScanMillis} ms");
            sb.AppendLine();

            var active = new System.Collections.Generic.List<DriftFinding>();
            var review = new System.Collections.Generic.List<DriftFinding>();
            var resolved = new System.Collections.Generic.List<DriftFinding>();
            var notTracked = new System.Collections.Generic.List<DriftFinding>();
            foreach (var f in r.DriftFindings)
            {
                if (f.Kind == DriftKind.NotTracked) { notTracked.Add(f); continue; }
                switch (f.Status)
                {
                    case DriftStatus.Active: active.Add(f); break;
                    case DriftStatus.Review: review.Add(f); break;
                    case DriftStatus.Resolved: resolved.Add(f); break;
                }
            }

            var openFindings = active.Count + review.Count;

            // Resolved rows lead when present: they are the good news, and each is shown only once.
            if (resolved.Count > 0)
            {
                sb.AppendLine($"  RESOLVED ({resolved.Count}) - re-verified against the current mods and no longer "
                            + "broken. Shown once, then cleared.");
                foreach (var f in resolved)
                {
                    var who = f.Owners.Count > 0 ? "  [" + string.Join(", ", f.Owners.ToArray()) + "]" : "";
                    sb.AppendLine($"    OK  {f.Member}{who}");
                }
                sb.AppendLine();
            }

            if (openFindings == 0)
            {
                if (IsFresh(r.DriftCodeState) || IsFresh(r.DriftContentState))
                    sb.AppendLine("  Baseline established now. Nothing to compare yet - this run records the "
                                + "ground, it does not verify it.");
                else if (Unusable(r.DriftCodeState) || Unusable(r.DriftContentState))
                    sb.AppendLine("  No comparison ran on one or more surfaces (see the surface states above). "
                                + "The rest of the scan is unaffected.");
                else if (resolved.Count == 0)
                    sb.AppendLine("  No differences found in the surfaces ATLAS tracks. That is NOT the same as "
                                + "nothing being broken (see limits below).");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"  OPEN FINDINGS: {active.Count} active (still broken) / {review.Count} review "
                            + "(ground moved, cannot auto-verify a fix).");

                if (active.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  ACTIVE - re-verified as still broken in the current game + mods:");
                    Findings(sb, active);
                }
                if (review.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  REVIEW - a body/signature/content change static analysis cannot confirm a fix");
                    sb.AppendLine("  for. These do NOT self-heal; accept the build once you have checked them.");
                    Findings(sb, review);
                }

                sb.AppendLine();
                sb.AppendLine("  Active findings clear themselves once you fix the mod and re-scan. Review findings");
                sb.AppendLine("  persist until you accept the current build as the new baseline: set");
                sb.AppendLine($"  Drift.AcceptCurrentBuild = true and load a save (baseline captured {Ago(r.BaselineCapturedUtc)}).");
                sb.AppendLine();
            }

            if (notTracked.Count > 0)
            {
                sb.AppendLine($"  NOT YET TRACKED ({notTracked.Count}) - newly patched since the baseline, recorded");
                sb.AppendLine("  on this build for future comparison (no history to compare against yet):");
                foreach (var f in notTracked)
                    sb.AppendLine("    - " + f.Member);
                sb.AppendLine();
            }

            sb.AppendLine("  Limits - drift cannot see:");
            foreach (var lim in DriftState.Limits)
                sb.AppendLine("    - " + lim);
            sb.AppendLine("  A clean result means: \"" + DriftState.CleanCaveat + "\"");
            sb.AppendLine();
        }

        /// <summary>
        /// The compatibility axis, kept visibly separate from Update impact above: baseline-free,
        /// "do the game members each installed mod hooks still exist in this build?" This is what
        /// fires for an out-of-date mod on a fresh install, which the baseline diff cannot see.
        /// </summary>
        private static void Compatibility(StringBuilder sb, ScanReport r)
        {
            if (!r.CompatChecked) return;   // check did not run: emit nothing, like the section did not exist

            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("MOD COMPATIBILITY  (do this mod's hooks exist in the game as installed? - no baseline)");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("  Independent of Update impact above: resolves every installed mod's Harmony patch");
            sb.AppendLine("  targets and reflected members against the game exactly as installed right now.");
            sb.AppendLine();

            if (r.CompatFindings.Count == 0)
            {
                sb.AppendLine("  All installed mods' patch targets and reflected members resolve against this");
                sb.AppendLine("  build. An out-of-date mod whose hooks all still exist is fine, and stays silent here.");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"  INCOMPATIBLE ({r.CompatFindings.Count}) - the owning mod is out of date with the installed game:");
                foreach (var f in r.CompatFindings)
                {
                    var origin = f.Origin == DriftOrigin.Reflection ? "reflection" : "patch target";
                    var who = f.Owners.Count > 0 ? "  [" + string.Join(", ", f.Owners.ToArray()) + "]" : "";
                    sb.AppendLine();
                    sb.AppendLine($"  [HIGH] ({origin}) {f.Member}{who}");
                    sb.AppendLine("    " + f.Detail);
                }
                sb.AppendLine();
            }

            sb.AppendLine("  Limits - the compatibility check cannot see:");
            sb.AppendLine("    - hardcoded/inlined targets and [HarmonyPatch] overloads ATLAS does not parse");
            sb.AppendLine("      (absence of a finding is not proof of compatibility)");
            sb.AppendLine("    - a method whose name exists but whose signature changed (that is Update impact's job)");
            sb.AppendLine("    - targets outside the game namespaces, and whether a resolvable hook still behaves the same");
            sb.AppendLine();
        }

        /// <summary>
        /// The runtime-truth axis: did declared patches apply, and did every mod load? Read from
        /// Harmony's live registry (after all mods loaded) and the error logs. Distinct from the
        /// static drift/compat sections above.
        /// </summary>
        private static void PatchVerify(StringBuilder sb, ScanReport r)
        {
            if (!r.PatchCheckRan) return;

            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("PATCH VERIFICATION  (did declared patches apply, and did every mod load?)");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("  Read from Harmony's live registry after all mods loaded, and from the error logs.");
            sb.AppendLine("  Reports what is broken right now - no baseline, no game update needed.");
            sb.AppendLine();

            if (r.PluginLoadFailures.Count > 0)
            {
                sb.AppendLine($"  FAILED TO LOAD ({r.PluginLoadFailures.Count}) - the logs show these mods threw during load:");
                foreach (var lf in r.PluginLoadFailures)
                {
                    sb.AppendLine($"    - {lf.Plugin}   (from {lf.LogName})");
                    if (lf.Error.Length > 0) sb.AppendLine("        " + lf.Error);
                }
                sb.AppendLine();
            }

            var confirmed = new System.Collections.Generic.List<PatchApplyFinding>();
            var unconfirmed = new System.Collections.Generic.List<PatchApplyFinding>();
            foreach (var f in r.PatchApplyFindings)
                (f.LogCorroborated ? confirmed : unconfirmed).Add(f);

            if (confirmed.Count > 0)
            {
                sb.AppendLine($"  DID NOT APPLY ({confirmed.Count}) - declared, absent from the registry, and corroborated by a");
                sb.AppendLine("  load/patch error in the logs:");
                foreach (var f in confirmed)
                {
                    var who = f.Owners.Count > 0 ? "  [" + string.Join(", ", f.Owners.ToArray()) + "]" : "";
                    sb.AppendLine();
                    sb.AppendLine($"  [HIGH] {f.Member}{who}");
                    sb.AppendLine("    " + f.Detail);
                }
                sb.AppendLine();
            }

            if (r.PatchDeclaredChecked > 0)
                sb.AppendLine($"  Verified applied : {r.PatchAppliedVerified} of {r.PatchDeclaredChecked} declared patch target(s).");
            else
                sb.AppendLine("  No declared patches to reconcile (Drift code pass off, or no game-namespace patches).");
            sb.AppendLine();

            if (unconfirmed.Count > 0)
            {
                sb.AppendLine($"  DECLARED BUT NOT OBSERVED APPLIED ({unconfirmed.Count}) - often benign (a conditional or");
                sb.AppendLine("  dynamically-targeted patch), sometimes a silent failure. Confirm the feature works:");
                foreach (var f in unconfirmed)
                {
                    var who = f.Owners.Count > 0 ? "  [" + string.Join(", ", f.Owners.ToArray()) + "]" : "";
                    sb.AppendLine($"    - {f.Member}{who}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("  Limits - patch verification cannot tell you:");
            sb.AppendLine("    - whether an applied patch is correct (only that it applied)");
            sb.AppendLine("    - about dynamically-computed/hardcoded targets (never in the declared set)");
            sb.AppendLine("    - a same-name overload apart from the one declared (matching is by name, not signature)");
            sb.AppendLine();
        }

        /// <summary>Per-mod static visibility (0.12.0). Informational — frames the other sections.</summary>
        private static void Coverage(StringBuilder sb, ScanReport r)
        {
            if (!r.CoverageChecked || r.ModCoverages.Count == 0) return;

            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("ANALYSIS COVERAGE  (how much of each mod ATLAS could statically resolve)");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine($"  {r.CoverageFullyVisibleMods} of {r.ModCoverages.Count} mods fully visible. A clean result on a");
            sb.AppendLine("  partial mod covers only the hooks ATLAS could resolve. \"Not resolvable\" = a dynamic/");
            sb.AppendLine("  computed patch target or an unrecoverable reflection type - a blind spot, not always a bug.");
            sb.AppendLine();
            foreach (var c in r.ModCoverages)
            {
                var vis = c.FullyVisible ? "full" : $"PARTIAL ({c.Unresolved} not resolvable)";
                sb.AppendLine($"  {c.Mod}: {c.PatchResolved} patch + {c.ReflectionResolved} reflection resolved  [{vis}]");
            }
            sb.AppendLine();
        }

        /// <summary>Log-activity summary (0.14.0): recurring vs one-off, and noisiest namespaces.</summary>
        private static void LogActivitySection(StringBuilder sb, ScanReport r)
        {
            var la = r.LogActivity;
            if (la == null || !la.Analyzed) return;

            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("LOG ACTIVITY  (what the archived logs have been doing)");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine($"  {la.TotalEvents} error/warning event(s) across {la.LogsScanned} archived log(s).");
            sb.AppendLine("  Consistently firing = seen in 2+ sessions (a standing issue); situational = one session.");
            sb.AppendLine("  By session log, not per line (BepInEx lines are not individually timestamped).");
            sb.AppendLine();

            if (la.TotalEvents == 0)
            {
                sb.AppendLine("  No errors or warnings in the kept logs.");
                sb.AppendLine();
                return;
            }

            if (la.Consistent.Count > 0)
            {
                sb.AppendLine($"  CONSISTENTLY FIRING ({la.ConsistentTotal}) - seen across 2+ sessions:");
                foreach (var g in la.Consistent) LogLine(sb, g);
                if (la.ConsistentTotal > la.Consistent.Count)
                    sb.AppendLine($"    + {la.ConsistentTotal - la.Consistent.Count} more not shown.");
                sb.AppendLine();
            }

            if (la.Situational.Count > 0)
            {
                sb.AppendLine($"  SITUATIONAL ({la.SituationalTotal}) - one session only:");
                foreach (var g in la.Situational) LogLine(sb, g);
                if (la.SituationalTotal > la.Situational.Count)
                    sb.AppendLine($"    + {la.SituationalTotal - la.Situational.Count} more not shown.");
                sb.AppendLine();
            }

            if (la.Noisy.Count > 0)
            {
                sb.AppendLine($"  NOISIEST SOURCES ({la.NoisyTotal}) - by BepInEx logger tag:");
                foreach (var n in la.Noisy)
                    sb.AppendLine($"    - {n.Source}: {n.Count} event(s) in {n.SessionCount} session(s)");
                sb.AppendLine();
            }
        }

        private static void LogLine(StringBuilder sb, LogEventGroup g)
        {
            var when = g.FirstSeen.Length > 0
                ? "  (" + g.FirstSeen + (g.LastSeen.Length > 0 && g.LastSeen != g.FirstSeen ? " -> " + g.LastSeen : "") + ")"
                : "";
            var src = g.Source.Length > 0 ? "  <" + g.Source + ">" : "";
            sb.AppendLine($"    [{g.Level}]{src} {g.Label}  x{g.Count} in {g.SessionCount} session(s){when}");
        }

        private static void Findings(StringBuilder sb, System.Collections.Generic.List<DriftFinding> list)
        {
            foreach (var f in list)
            {
                var tier = f.Severity == Severity.High ? "HIGH"
                         : f.Severity == Severity.Medium ? "MEDIUM" : "LOW";
                sb.AppendLine();
                sb.AppendLine($"  [{tier}] {f.Kind} {f.Member}");
                sb.AppendLine("    " + f.Detail);
                if (f.Owners.Count > 0)
                {
                    var line = "    owners: " + string.Join(", ", f.Owners.ToArray());
                    if (f.PatchKinds.Length > 0) line += "   (" + f.PatchKinds + ")";
                    sb.AppendLine(line);
                }
                if (f.OwnerVersionChanged)
                    sb.AppendLine("    note: a patching mod was updated since the baseline - it may already be fixed "
                                + "(this does not lower the severity).");
            }
        }

        private static string StateLine(string state, string detail)
        {
            if (state.Length == 0) return "not run";
            return detail.Length > 0 ? state + " - " + detail : state;
        }

        private static bool IsFresh(string state) => state == "NoBaseline";
        private static bool Unusable(string state) => state == "Unavailable" || state == "Unreadable";

        private static string Short(string mvid)
            => string.IsNullOrEmpty(mvid) ? "?" : (mvid.Length > 8 ? mvid.Substring(0, 8) : mvid);

        private static string Ago(string capturedUtc)
        {
            if (string.IsNullOrEmpty(capturedUtc)) return "at an unknown time";
            if (DateTime.TryParse(capturedUtc, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.AdjustToUniversal
                                  | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            {
                var span = DateTime.UtcNow - t;
                if (span.TotalDays >= 1) return $"{(int)span.TotalDays} day(s) ago ({capturedUtc})";
                if (span.TotalHours >= 1) return $"{(int)span.TotalHours} hour(s) ago ({capturedUtc})";
                return $"{Math.Max(0, (int)span.TotalMinutes)} minute(s) ago ({capturedUtc})";
            }
            return "on " + capturedUtc;
        }

        private static void Conflicts(StringBuilder sb, ScanReport r)
        {
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("CONFLICTS  (methods patched by 2+ mods)");
            sb.AppendLine("--------------------------------------------------");

            if (r.Conflicts.Count == 0)
            {
                sb.AppendLine("  none");
                sb.AppendLine();
                return;
            }

            foreach (var c in r.Conflicts)
            {
                var evidence = c.ObservedInLogs > 0 ? "  <-- SEEN IN ERROR LOGS"
                             : c.ObservedInLogs == 0 ? "  (not seen in logs)"
                             : "";
                sb.AppendLine();
                sb.AppendLine($"[{c.Severity.ToString().ToUpperInvariant()}] {c.Method}{evidence}");
                sb.AppendLine("  " + c.Reason);
                foreach (var o in c.Owners)
                    sb.AppendLine("    - " + o.DisplayName + "  " + PatchKinds(o));
                if (c.Order.Count > 0)
                {
                    sb.AppendLine("    order: " + string.Join(" -> ",
                        c.Order.Select(s => s.Owner + " (" + s.Kind + ")").ToArray()));
                    if (c.HasOrderingConstraints)
                        sb.AppendLine("      (before/after ordering declared; actual order may differ)");
                }
            }
            sb.AppendLine();
        }

        private static string PatchKinds(PatchOwner o)
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if (o.Prefixes > 0) parts.Add($"prefix x{o.Prefixes}");
            if (o.Postfixes > 0) parts.Add($"postfix x{o.Postfixes}");
            if (o.Transpilers > 0) parts.Add($"transpiler x{o.Transpilers}");
            if (o.Finalizers > 0) parts.Add($"finalizer x{o.Finalizers}");
            return "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static void Keybinds(StringBuilder sb, ScanReport r)
        {
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("KEYBINDS");
            sb.AppendLine("--------------------------------------------------");

            var kbBinds = r.Binds.Count(b => !b.IsController);
            var padBinds = r.Binds.Count(b => b.IsController);
            sb.AppendLine($"  {kbBinds} keyboard/mouse and {padBinds} controller bindings seen"
                        + (r.GameBindingsFound ? " (game + mods)." : " (mods only - game bindings unavailable)."));
            sb.AppendLine("  Hardcoded mod keys that are not exposed as config entries cannot be");
            sb.AppendLine("  detected, so this list is a strong hint rather than a guarantee.");
            sb.AppendLine();

            if (r.BindOverlaps.Count == 0)
            {
                sb.AppendLine("  OVERLAPS: none found.");
            }
            else
            {
                sb.AppendLine($"  OVERLAPS ({r.BindOverlaps.Count}) - same control claimed by 2+ owners.");
                sb.AppendLine("  Not always a bug: mods active in different contexts can share a key.");
                foreach (var o in r.BindOverlaps)
                {
                    sb.AppendLine();
                    sb.AppendLine($"    [{(o.IsController ? "PAD" : "KEY")}] {o.Control}"
                        + (o.Confirmed
                            ? $"   <-- CONFIRMED: fired in {o.ConfirmedBy} ({o.ConfirmedCount}x, last {o.ConfirmedLast})"
                            : ""));
                    foreach (var b in o.Binds)
                        sb.AppendLine($"      - {b.Label}   = {b.RawValue}{SourceTag(b.Source)}");
                }
            }
            sb.AppendLine();

            if (r.OrphanedConfigs.Count > 0)
            {
                sb.AppendLine($"  LEFTOVER CONFIGS ({r.OrphanedConfigs.Count}) - these declare keybinds but no");
                sb.AppendLine("  matching mod is loaded, so the bindings are inactive. Safe to delete.");
                foreach (var o in r.OrphanedConfigs)
                    sb.AppendLine("    - " + o + ".cfg");
                sb.AppendLine();
            }

            if (r.MalformedBinds.Count > 0)
            {
                sb.AppendLine($"  MALFORMED BINDINGS ({r.MalformedBinds.Count}) - these will not work:");
                foreach (var m in r.MalformedBinds)
                    sb.AppendLine("    - " + m);
                sb.AppendLine();
            }

            if (r.KeyLearningActive)
            {
                sb.AppendLine("  LEARNED AT RUNTIME (which mod was seen polling which key):");
                if (r.ObservedKeys.Count == 0)
                {
                    sb.AppendLine($"    nothing recorded yet. ({r.KeyReadsIntercepted} button read(s) "
                                + $"intercepted, {r.KeyReadsUnattributed} could not be traced to a mod.)");
                    if (r.KeyReadsIntercepted == 0)
                        sb.AppendLine("    Zero interceptions means the watcher is not seeing key reads at all.");
                    sb.AppendLine("    The load-time scan also runs before you can press anything, so a fresh");
                    sb.AppendLine("    world looks empty here regardless; totals carry over between sessions");
                    sb.AppendLine("    via BepInEx/ATLAS/observed_keys.tsv.");
                }
                else
                {
                    var undeclared = r.ObservedKeys.Count(o => !o.InConfig);
                    sb.AppendLine($"    {r.ObservedKeys.Count} key(s) seen being watched by mods; "
                                + $"{undeclared} declared in no config file.");
                    foreach (var o in r.ObservedKeys)
                    {
                        var activity = o.Count > 0
                            ? $"pressed {o.Count}x"
                            : "watched, not pressed";
                        var device = o.IsController ? "[PAD] " : "";
                        var tag = o.InConfig ? "" : "   <-- hardcoded, not in any config";
                        sb.AppendLine($"    - {o.Plugin}  {device}{o.Control}  ({activity}, first {o.FirstSeen}){tag}");
                    }
                }
                sb.AppendLine();

                if (r.ObservedKeyCollisions.Count > 0)
                {
                    sb.AppendLine($"  CONFIRMED SIMULTANEOUS USE ({r.ObservedKeyCollisions.Count}):");
                    sb.AppendLine("  Two mods read the same control on the same frame - not a");
                    sb.AppendLine("  theoretical overlap, an observed one.");
                    foreach (var c in r.ObservedKeyCollisions)
                    {
                        var device = c.IsController ? "[PAD] " : "";
                        sb.AppendLine($"    - {device}{c.Control}: {c.PluginA} + {c.PluginB}  "
                                    + $"({c.Count}x, last {c.LastSeen})");
                    }
                    sb.AppendLine();
                }
            }

            if (r.FreeKeys.Count > 0)
            {
                sb.AppendLine("  UNUSED KEYS (safe candidates for a new binding):");
                for (int i = 0; i < r.FreeKeys.Count; i += 12)
                {
                    var row = r.FreeKeys.Skip(i).Take(12);
                    sb.AppendLine("    " + string.Join("  ", row.ToArray()));
                }
                sb.AppendLine();
                sb.AppendLine("  Excluded from the list regardless of use: F10 (Windows delivers it as a");
                sb.AppendLine("  system key and it fails even when polled directly) and F12 (Steam");
                sb.AppendLine("  screenshot on most installs), plus modifiers and game-reserved keys.");
            }
            sb.AppendLine();

            if (r.FreeControllerButtons.Count > 0)
            {
                sb.AppendLine("  UNUSED CONTROLLER BUTTONS (safe candidates for a new binding):");
                for (int i = 0; i < r.FreeControllerButtons.Count; i += 6)
                {
                    var row = r.FreeControllerButtons.Skip(i).Take(6);
                    sb.AppendLine("    " + string.Join("  ", row.ToArray()));
                }
                sb.AppendLine();
                sb.AppendLine("  Only discrete gamepad buttons are listed. The sticks, the D-pad");
                sb.AppendLine("  directions and the analog triggers arrive as composite paths that the");
                sb.AppendLine("  scanner cannot track, so they are neither counted as used nor offered");
                sb.AppendLine("  here; Start and Select are excluded as the reserved menu pair. A button");
                sb.AppendLine("  the game already binds on the controller drops off this list on its own,");
                sb.AppendLine("  since the game's own controller bindings are read live.");
            }
            sb.AppendLine();
        }

        private static string SourceTag(BindSource s) => s switch
        {
            BindSource.ModConfigGuessed => "   (inferred from a text setting)",
            _ => "",
        };

        private static void MissingDeps(StringBuilder sb, ScanReport r)        {
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("MISSING DEPENDENCIES");
            sb.AppendLine("--------------------------------------------------");

            if (r.MissingDependencies.Count == 0 && r.DependencyVersionIssues.Count == 0)
            {
                sb.AppendLine("  none");
                sb.AppendLine();
                return;
            }

            foreach (var d in r.MissingDependencies)
            {
                var kind = d.HardDependency ? "HARD" : "soft";
                sb.AppendLine($"  [{kind}] {d.DependentName} needs {d.MissingGuid}");
            }

            // Version satisfaction (0.12.0): present but older than declared.
            foreach (var v in r.DependencyVersionIssues)
            {
                var kind = v.HardDependency ? "HARD/ver" : "soft/ver";
                sb.AppendLine($"  [{kind}] {v.DependentName} needs {v.DepGuid} >= {v.RequiredVersion}, "
                            + $"but {v.InstalledVersion} is installed");
            }
            sb.AppendLine();
        }

        private static void Ignored(StringBuilder sb, ScanReport r)
        {
            if (r.IgnoredItems.Count == 0) return;

            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("IGNORED");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("  Set aside via decisions.tsv - not counted toward status, kept for visibility.");
            foreach (var it in r.IgnoredItems)
            {
                sb.AppendLine($"  [{it.Category}] {it.Label}");
                if (it.Detail.Length > 0) sb.AppendLine("    " + it.Detail);
            }
            sb.AppendLine();
        }

        private static void Plugins(StringBuilder sb, ScanReport r)
        {
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine("INSTALLED PLUGINS");
            sb.AppendLine("--------------------------------------------------");
            foreach (var p in r.Plugins)
                sb.AppendLine($"  {p.Name}  v{p.Version}  [{p.Guid}]  ({p.ConfigEntryCount} config entries)");
            sb.AppendLine();
        }
    }
}
