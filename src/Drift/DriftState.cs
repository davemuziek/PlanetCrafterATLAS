using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ATLAS
{
    /// <summary>
    /// Session-scoped holder for the drift comparison, computed at most once per surface per
    /// session. <see cref="ApplyTo"/> copies the result onto a ScanReport, mirroring
    /// <c>ObservedConflicts.Apply</c> - so the report stays inert data by the time a renderer
    /// sees it, and neither renderer learns anything new about how drift works.
    ///
    /// Every public entry point is wrapped so a drift failure degrades to a §8 state and logs a
    /// warning rather than taking the scan down with it.
    /// </summary>
    internal static class DriftState
    {
        /// <summary>The honest framing for a clean result, and the limits that must reach the reader (§11).</summary>
        public const string CleanCaveat =
            "No differences found in the surfaces ATLAS tracks. That is not the same as nothing being broken.";

        public static readonly string[] Limits =
        {
            "prefab hierarchy changes, renamed GameObjects, moved child transforms",
            "AssetBundle or shader mismatches after a Unity or pipeline change",
            "changed enum VALUES where the name is unchanged (a name-based snapshot cannot see a reordering)",
            "semantic drift: a body that is byte-identical but now called from a different place, time, or lock",
            "anything in an assembly ATLAS does not scan",
            "reflection whose type argument could not be statically recovered (see the unresolved-sites count)",
        };

        // ── computed results (read by ApplyTo) ─────────────────────────────────────────
        private static readonly object Gate = new object();
        private static readonly List<DriftFinding> Findings = new List<DriftFinding>();

        private static bool _codeDone;
        private static bool _contentDone;
        private static bool _acceptPendingContent;

        private static bool _driftChecked;
        private static bool _baselineExists;
        private static bool _buildChanged;
        private static bool _rosterChanged;

        private static string _codeState = "";
        private static string _contentState = "";
        private static string _codeDetail = "";
        private static string _contentDetail = "";

        private static string _baselineGameVersion = "";
        private static string _baselineCapturedUtc = "";
        private static string _baselineMvid = "";
        private static string _currentMvid = "";

        private static int _unresolved;
        private static int _methodsTracked;
        private static long _scanMillis;

        private static string _codePath = "";
        private static string _contentPath = "";
        private static string _seenPath = "";

        // Live inputs for the per-scan re-verification, captured once at Awake (the mod set does not
        // change within a session) and reused by ApplyTo on every scan.
        private static List<ReflRow> _currentRefl = new List<ReflRow>();
        private static PatchTargetIndex _patchTargets = new PatchTargetIndex();

        // Compatibility axis (0.10.0). Computed once at InitCode from the current mods vs the live
        // assembly, independent of the baseline; copied onto each report by ApplyTo without going
        // through the Active/Review/Resolved reconciliation (they are inherently live).
        private static List<DriftFinding> _compatFindings = new List<DriftFinding>();
        private static bool _compatChecked;

        // The game members installed mods DECLARE a Harmony patch against, collected once at InitCode
        // (from the plugin DLLs). Exposed for the scan-time runtime patch check (0.11.0), which
        // reconciles these declarations against Harmony's live registry once every mod has loaded.
        public static IReadOnlyList<DeclaredPatchTarget> DeclaredPatchTargets => _patchTargets.Targets;

        // Per-mod static visibility (0.12.0), computed once at InitCode from the Cecil walks.
        private static List<ModCoverage> _coverage = new List<ModCoverage>();
        private static bool _coverageChecked;

        // The <AssemblyName> in ATLAS.csproj. Used to exclude ATLAS's own DLL from the plugin
        // walk in the reflection scan.
        private const string OwnAssemblyName = "ATLAS";

        // ── code surface (kicked from Plugin.Awake) ────────────────────────────────────

        public static void InitCode()
        {
            try { InitCodeCore(); }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    _codeState = nameof(DriftBaselineState.Unavailable);
                    _codeDetail = ex.GetType().Name + ": " + ex.Message;
                    _driftChecked = false;
                }
                Plugin.Log.LogWarning("ATLAS drift (code) failed, scan unaffected: " + ex.Message);
            }
        }

        private static void InitCodeCore()
        {
            if (_codeDone) return;
            _codeDone = true;

            var sw = Stopwatch.StartNew();

            var baselineDir = Path.Combine(Paths.BepInExRootPath, "ATLAS", "Baseline");
            _codePath = Path.Combine(baselineDir, "baseline_code.tsv");
            _contentPath = Path.Combine(baselineDir, "baseline_content.tsv");
            _seenPath = Path.Combine(baselineDir, "drift_seen.tsv");

            var asmPath = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
            var module = CodeDrift.ReadModule(asmPath, out var err);
            if (module == null)
            {
                _codeState = nameof(DriftBaselineState.Unavailable);
                _codeDetail = err;
                sw.Stop();
                _scanMillis = sw.ElapsedMilliseconds;
                Plugin.Log.LogWarning("ATLAS drift: game assembly unavailable (" + err
                                    + "). Conflict and keybind sections still render.");
                return;
            }

            using (module)
            {
                _currentMvid = module.Mvid.ToString();
                var typeIndex = CodeDrift.BuildTypeIndex(module);

                var patchedKeys = new HashSet<string>(StringComparer.Ordinal);
                var patchMeta = new Dictionary<string, PatchMeta>(StringComparer.Ordinal);
                BuildPatchInfo(patchedKeys, patchMeta);

                var currentMethods = CodeDrift.CaptureMethods(module, patchedKeys, out var tracked);
                _methodsTracked = tracked;

                // Per-owner unresolved tallies feed the 0.12.0 coverage score: how much of each mod
                // ATLAS could statically resolve. Filled by the collectors alongside their normal work.
                var reflUnresolvedByOwner = new Dictionary<string, int>(StringComparer.Ordinal);
                var patchUnresolvedByOwner = new Dictionary<string, int>(StringComparer.Ordinal);

                var currentRefl = new List<ReflRow>();
                if (Plugin.CfgDriftScanReflection.Value)
                    currentRefl = CodeDrift.CollectReflection(
                        Paths.PluginPath, GameNamespaces(), OwnAssemblyName, out _unresolved, reflUnresolvedByOwner);

                // Live re-verification inputs (0.7.1): what mods currently reflect, and what they
                // currently declare Harmony patches against. Cheap attribute read; used by ApplyTo
                // to self-heal findings whose mod has been fixed.
                _currentRefl = currentRefl;
                _patchTargets = CodeDrift.CollectPatchTargets(
                    Paths.PluginPath, GameNamespaces(), OwnAssemblyName, patchUnresolvedByOwner);

                // Coverage is purely static (these Cecil walks), so compute it here at InitCode.
                _coverage = CodeDrift.BuildCoverage(
                    currentRefl, _patchTargets.Targets, reflUnresolvedByOwner, patchUnresolvedByOwner);
                _coverageChecked = true;

                var currentRoster = CurrentRoster(out var currentByGuid);

                // One-shot accept: recapture and rewrite, clear findings, reset the entry.
                if (Plugin.CfgDriftAcceptCurrentBuild.Value)
                {
                    var header = FreshHeader(currentRoster);
                    DriftBaseline.WriteCode(_codePath, header, currentMethods, currentRefl);

                    _baselineExists = true;
                    _baselineGameVersion = header.Game;
                    _baselineCapturedUtc = header.CapturedUtc;
                    _baselineMvid = _currentMvid;
                    _buildChanged = false;
                    _codeState = nameof(DriftBaselineState.Unchanged);
                    _codeDetail = "current build accepted as the new baseline";

                    // Accepting is a clean slate: forget every announced-resolved key so nothing is
                    // carried over from the old baseline.
                    DriftLiveStatus.Wipe(_seenPath);

                    _acceptPendingContent = Plugin.CfgDriftContent.Value;
                    Plugin.CfgDriftAcceptCurrentBuild.Value = false;
                    Plugin.Log.LogInfo("ATLAS drift: accepted current build, code baseline rewritten. "
                                     + (_acceptPendingContent ? "Content baseline rewrites on next save load."
                                                              : "Outstanding findings cleared."));
                    _driftChecked = true;
                }
                else
                {
                    var baseline = DriftBaseline.ReadCode(_codePath);

                    if (!baseline.FileExists)
                    {
                        var header = FreshHeader(currentRoster);
                        DriftBaseline.WriteCode(_codePath, header, currentMethods, currentRefl);
                        _baselineExists = false;
                        _baselineGameVersion = header.Game;
                        _baselineCapturedUtc = header.CapturedUtc;
                        _baselineMvid = _currentMvid;
                        _codeState = nameof(DriftBaselineState.NoBaseline);
                        _codeDetail = "baseline established now; nothing to compare yet";
                    }
                    else if (!baseline.Ok)
                    {
                        _baselineExists = true;
                        _codeState = nameof(DriftBaselineState.Unreadable);
                        _codeDetail = baseline.Error + " - baseline left untouched, not recaptured";
                        Plugin.Log.LogWarning("ATLAS drift: code baseline unreadable (" + baseline.Error
                                            + "). Not recapturing; set Drift.AcceptCurrentBuild to rebuild it.");
                    }
                    else
                    {
                        _baselineExists = true;
                        _baselineGameVersion = baseline.Header.Game;
                        _baselineCapturedUtc = baseline.Header.CapturedUtc;
                        _baselineMvid = baseline.Header.Mvid;
                        _buildChanged = !string.Equals(_baselineMvid, _currentMvid, StringComparison.Ordinal);
                        _codeState = _buildChanged
                            ? nameof(DriftBaselineState.Changed)
                            : nameof(DriftBaselineState.Unchanged);

                        _rosterChanged = RosterDiffers(baseline.Header.Plugins, currentRoster);
                        var changedPlugins = ChangedPluginNames(baseline.Header.Plugins, currentByGuid);

                        var codeFindings = CodeDrift.CompareMethods(
                            baseline, module, typeIndex, patchMeta, changedPlugins);
                        var reflFindings = CodeDrift.CompareReflection(baseline);

                        lock (Gate)
                        {
                            Findings.AddRange(codeFindings);
                            Findings.AddRange(reflFindings);
                        }

                        LazilyAddTracking(baseline, currentMethods, currentRefl);
                    }

                    _driftChecked = true;
                }
            }

            // ── mod compatibility (0.10.0) ─────────────────────────────────────────────
            // Baselineless: resolve what the CURRENT mods declare they patch/reflect against the
            // LIVE assembly, whatever the baseline state above (it runs even on NoBaseline - the
            // fresh-install case is where it matters most). Deduped against the drift findings just
            // computed so a baseline-tracked dead target is reported once, by drift, not twice here.
            // Reached only when the game module loaded (the null case returned early above), so the
            // live-assembly resolve has something to check against.
            if (Plugin.CfgDriftCheckCompatibility != null && Plugin.CfgDriftCheckCompatibility.Value)
            {
                try
                {
                    var skipTypes = new HashSet<string>(StringComparer.Ordinal);
                    var skipMembers = new HashSet<string>(StringComparer.Ordinal);
                    lock (Gate)
                        foreach (var f in Findings)
                        {
                            if (f.Kind == DriftKind.TypeMissing) skipTypes.Add(f.MatchType);
                            else if (f.Kind == DriftKind.TargetMissing || f.Kind == DriftKind.ReflectedMemberMissing)
                                skipMembers.Add(f.MatchType + "|" + f.MatchName);
                        }

                    _compatFindings = CodeDrift.CheckCompatibility(
                        _patchTargets.Targets, _currentRefl, skipTypes, skipMembers);
                    _compatChecked = true;
                    Plugin.Log.LogInfo($"ATLAS drift (compat): {_compatFindings.Count} installed-mod "
                                     + "hook(s) do not resolve against the current game assembly.");
                }
                catch (Exception ex)
                {
                    _compatChecked = false;
                    Plugin.Log.LogWarning("ATLAS drift: compatibility check failed, scan unaffected: " + ex.Message);
                }
            }

            sw.Stop();
            _scanMillis = sw.ElapsedMilliseconds;

            Plugin.Log.LogInfo($"ATLAS drift (code): {_methodsTracked} method(s) tracked, "
                             + $"{_unresolved} unresolved reflection site(s), {_scanMillis} ms.");
            if (_scanMillis > 750)
                Plugin.Log.LogInfo("ATLAS drift: code scan exceeded ~750 ms; consider moving Part A to a "
                                 + "background task (the number is recorded so the call is evidence-based).");
        }

        /// <summary>
        /// A patched method with no baseline row is recorded on the current build so future diffs
        /// cover it, and reported as NOT TRACKED rather than as changed. Never invent history for a
        /// method that has none. Only writes when there is something new (so an unchanged rescan
        /// leaves the file untouched).
        /// </summary>
        private static void LazilyAddTracking(
            CodeBaseline baseline, List<CodeMethodRow> currentMethods, List<ReflRow> currentRefl)
        {
            var haveMethod = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in baseline.Methods) haveMethod.Add(m.SignatureKey());
            var haveRefl = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in baseline.Refl) haveRefl.Add(r.Key());

            var newMethods = new List<CodeMethodRow>();
            foreach (var m in currentMethods)
                if (!haveMethod.Contains(m.SignatureKey())) newMethods.Add(m);

            var newRefl = new List<ReflRow>();
            foreach (var r in currentRefl)
                if (!haveRefl.Contains(r.Key())) newRefl.Add(r);

            if (newMethods.Count == 0 && newRefl.Count == 0) return;

            foreach (var m in newMethods)
            {
                var f = new DriftFinding
                {
                    Kind = DriftKind.NotTracked,
                    Severity = Severity.None,     // informational: excluded from the tiered counts
                    Member = m.Type + "." + m.Name,
                    Detail = "Newly patched since the baseline. Recorded on the current build so future "
                           + "comparisons cover it; no history to compare against yet.",
                };
                lock (Gate) Findings.Add(f);
            }

            // Append to the baseline, preserving every existing row and the original header so the
            // build-change key still reflects the accepted build.
            var merged = new List<CodeMethodRow>(baseline.Methods);
            merged.AddRange(newMethods);
            var mergedRefl = new List<ReflRow>(baseline.Refl);
            mergedRefl.AddRange(newRefl);

            try
            {
                DriftBaseline.WriteCode(_codePath, baseline.Header, merged, mergedRefl);
                Plugin.Log.LogInfo($"ATLAS drift: began tracking {newMethods.Count} new method(s) and "
                                 + $"{newRefl.Count} new reflection target(s) on the current build.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("ATLAS drift: could not persist newly-tracked targets: " + ex.Message);
            }
        }

        // ── content surface (kicked from StaticDataPatch, after LoadStaticData) ─────────

        public static void CaptureContent()
        {
            try { CaptureContentCore(); }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    _contentState = nameof(DriftBaselineState.Unavailable);
                    _contentDetail = ex.GetType().Name + ": " + ex.Message;
                }
                Plugin.Log.LogWarning("ATLAS drift (content) failed, scan unaffected: " + ex.Message);
            }
        }

        private static void CaptureContentCore()
        {
            if (_contentDone && !_acceptPendingContent) return;
            _contentDone = true;

            if (string.IsNullOrEmpty(_contentPath))
                _contentPath = Path.Combine(Paths.BepInExRootPath, "ATLAS", "Baseline", "baseline_content.tsv");

            var cap = ContentDrift.Capture();
            if (!cap.Available)
            {
                _contentState = nameof(DriftBaselineState.Unavailable);
                _contentDetail = "the group roster was not reachable at capture time";
                return;
            }

            // The null-craftableInList assertion is a standing check, not a diff: it fires even
            // with no baseline and even right after an accept, because it is a live landmine.
            lock (Gate) Findings.AddRange(cap.NullFindings);

            var currentRoster = CurrentRoster(out _);

            if (_acceptPendingContent)
            {
                var header = FreshHeader(currentRoster);
                header.Groups = cap.Groups.Count;
                DriftBaseline.WriteContent(_contentPath, header, cap.Groups);
                _contentState = nameof(DriftBaselineState.Unchanged);
                _contentDetail = "current content accepted as the new baseline";
                _acceptPendingContent = false;
                Plugin.Log.LogInfo("ATLAS drift: content baseline rewritten (build accepted).");
                return;
            }

            var baseline = DriftBaseline.ReadContent(_contentPath);

            if (!baseline.FileExists)
            {
                var header = FreshHeader(currentRoster);
                header.Groups = cap.Groups.Count;
                DriftBaseline.WriteContent(_contentPath, header, cap.Groups);
                _contentState = nameof(DriftBaselineState.NoBaseline);
                _contentDetail = "content baseline established now; nothing to compare yet";
            }
            else if (!baseline.Ok)
            {
                _contentState = nameof(DriftBaselineState.Unreadable);
                _contentDetail = baseline.Error + " - baseline left untouched, not recaptured";
                Plugin.Log.LogWarning("ATLAS drift: content baseline unreadable (" + baseline.Error + ").");
            }
            else
            {
                var rosterChanged = RosterDiffers(baseline.Header.Plugins, currentRoster);
                _rosterChanged = _rosterChanged || rosterChanged;

                var diff = ContentDrift.Diff(baseline, cap.Groups, rosterChanged);
                lock (Gate) Findings.AddRange(diff);

                _contentState = diff.Count > 0
                    ? nameof(DriftBaselineState.Changed)
                    : nameof(DriftBaselineState.Unchanged);
                _contentDetail = rosterChanged
                    ? "plugin roster changed since the baseline; added/removed groups cannot be cleanly attributed"
                    : "";
            }

            Plugin.Log.LogInfo($"ATLAS drift (content): {cap.Groups.Count} group(s) snapshotted, "
                             + $"state {_contentState}.");
        }

        // ── apply to report (called from Plugin.Scan) ──────────────────────────────────

        public static void ApplyTo(ScanReport report)
        {
            try
            {
                report.DriftChecked = _driftChecked;
                report.DriftBaselineExists = _baselineExists;
                report.GameBuildChanged = _buildChanged;
                report.BaselineGameVersion = _baselineGameVersion;
                report.BaselineCapturedUtc = _baselineCapturedUtc;
                report.BaselineMvid = _baselineMvid;
                report.CurrentMvid = _currentMvid;
                report.PluginRosterChanged = _rosterChanged;
                report.DriftUnresolvedReflectionSites = _unresolved;
                report.DriftMethodsTracked = _methodsTracked;
                report.DriftScanMillis = _scanMillis;
                report.DriftCodeState = _codeState;
                report.DriftContentState = _contentState;
                report.DriftCodeDetail = _codeDetail;
                report.DriftContentDetail = _contentDetail;

                List<DriftFinding> snapshot;
                lock (Gate) { snapshot = new List<DriftFinding>(Findings); }

                // ── live re-verification (0.7.1) ──────────────────────────────────────────
                // Re-test each finding against the current mods instead of replaying a verdict,
                // then reconcile against the announced-resolved state so a fix shows Resolved once
                // and then drops off. Guarded to the case where a comparison actually ran; when it
                // did not (no baseline / unreadable / assembly unavailable) there is nothing to
                // re-verify and the seen-state is left untouched.
                List<DriftFinding> display;
                if (_driftChecked)
                {
                    DriftLiveStatus.Assign(snapshot, _currentRefl, _patchTargets);
                    var seen = DriftLiveStatus.Load(_seenPath);
                    display = DriftLiveStatus.Reconcile(snapshot, seen, out var nextSeen);
                    DriftLiveStatus.Save(_seenPath, nextSeen);
                }
                else
                {
                    display = snapshot;
                }

                foreach (var f in display) report.DriftFindings.Add(f);

                // Active first, then Review, then the one-time Resolved rows; within a status band,
                // highest severity first, then a stable kind order so a report reads top-down.
                report.DriftFindings.Sort((a, b) =>
                {
                    var st = ((int)a.Status).CompareTo((int)b.Status);
                    if (st != 0) return st;
                    var s = ((int)b.Severity).CompareTo((int)a.Severity);
                    return s != 0 ? s : ((int)a.Kind).CompareTo((int)b.Kind);
                });

                foreach (var f in report.DriftFindings)
                {
                    if (f.Kind == DriftKind.NotTracked) continue;   // informational, not a graded finding
                    switch (f.Status)
                    {
                        case DriftStatus.Active: report.DriftActiveCount++; break;
                        case DriftStatus.Review: report.DriftReviewCount++; break;
                        case DriftStatus.Resolved: report.DriftResolvedCount++; break;
                    }
                    if (f.Status == DriftStatus.Resolved) continue;   // resolved is not a live severity
                    switch (f.Severity)
                    {
                        case Severity.High: report.DriftHighCount++; break;
                        case Severity.Medium: report.DriftMediumCount++; break;
                        case Severity.Low: report.DriftLowCount++; break;
                    }
                }

                // ── compatibility findings (0.10.0) ───────────────────────────────────────
                // A separate, baselineless axis: copied straight onto the report, NOT routed through
                // DriftLiveStatus. They are re-derived from scratch each session, so present means
                // broken now and absent means fixed - there is no verdict to replay and no seen-state
                // to reconcile. Worst-first, then by member for a stable read.
                report.CompatChecked = _compatChecked;
                var compat = new List<DriftFinding>(_compatFindings);
                compat.Sort((a, b) =>
                {
                    var s = ((int)b.Severity).CompareTo((int)a.Severity);
                    return s != 0 ? s : string.CompareOrdinal(a.Member, b.Member);
                });
                foreach (var f in compat)
                {
                    report.CompatFindings.Add(f);
                    if (f.Severity == Severity.High) report.CompatHighCount++;
                }

                // ── coverage (0.12.0) ─────────────────────────────────────────────────────
                // Informational per-mod static-visibility, copied straight onto the report. Never
                // feeds the verdict - it frames how much the other sections could actually see.
                report.CoverageChecked = _coverageChecked;
                foreach (var c in _coverage)
                {
                    report.ModCoverages.Add(c);
                    if (c.FullyVisible) report.CoverageFullyVisibleMods++; else report.CoveragePartialMods++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("ATLAS drift: ApplyTo failed: " + ex.Message);
            }
        }

        // ── patch info / roster helpers ────────────────────────────────────────────────

        private static void BuildPatchInfo(HashSet<string> keys, Dictionary<string, PatchMeta> meta)
        {
            IEnumerable<MethodBase> patched;
            try { patched = Harmony.GetAllPatchedMethods(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("ATLAS drift: could not enumerate patched methods: " + ex.Message);
                return;
            }

            var idToName = BuildIdToName();

            foreach (var method in patched)
            {
                if (method?.DeclaringType == null) continue;
                var key = CodeDrift.MethodKey(
                    method.DeclaringType.FullName ?? "?", method.Name, ParamLen(method));
                keys.Add(key);

                HarmonyLib.Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                var m = new PatchMeta();
                int pre = 0, post = 0, tr = 0, fin = 0;
                var owners = new HashSet<string>(StringComparer.Ordinal);

                foreach (var p in info.Prefixes) { pre++; owners.Add(NameFor(p.owner, idToName)); }
                foreach (var p in info.Postfixes) { post++; owners.Add(NameFor(p.owner, idToName)); }
                foreach (var p in info.Transpilers) { tr++; owners.Add(NameFor(p.owner, idToName)); }
                foreach (var p in info.Finalizers) { fin++; owners.Add(NameFor(p.owner, idToName)); }

                m.AnyTranspiler = tr > 0;
                m.AnyPrefixOrFinalizer = pre > 0 || fin > 0;
                m.PostfixOnly = post > 0 && !m.AnyTranspiler && !m.AnyPrefixOrFinalizer;
                m.PatchKinds = KindsString(pre, post, tr, fin);
                foreach (var o in owners) m.Owners.Add(o);

                meta[key] = m;
            }
        }

        private static string KindsString(int pre, int post, int tr, int fin)
        {
            var parts = new List<string>(4);
            if (pre > 0) parts.Add("prefix x" + pre);
            if (post > 0) parts.Add("postfix x" + post);
            if (tr > 0) parts.Add("transpiler x" + tr);
            if (fin > 0) parts.Add("finalizer x" + fin);
            return string.Join(", ", parts.ToArray());
        }

        private static int ParamLen(MethodBase m)
        {
            try { return m.GetParameters().Length; } catch { return 0; }
        }

        private static Dictionary<string, string> BuildIdToName()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Chainloader.PluginInfos)
            {
                var meta = kv.Value?.Metadata;
                if (meta == null) continue;
                d[meta.GUID] = meta.Name;
            }
            return d;
        }

        private static string NameFor(string ownerId, Dictionary<string, string> idToName)
            => idToName.TryGetValue(ownerId, out var name) ? name : ownerId;

        private static List<PluginStamp> CurrentRoster(out Dictionary<string, string> byGuidVersion)
        {
            var list = new List<PluginStamp>();
            byGuidVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Chainloader.PluginInfos)
            {
                var meta = kv.Value?.Metadata;
                if (meta == null) continue;
                var ver = meta.Version != null ? meta.Version.ToString() : "?";
                list.Add(new PluginStamp { Guid = meta.GUID, Version = ver });
                byGuidVersion[meta.GUID] = ver;
            }
            list.Sort((a, b) => string.Compare(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static bool RosterDiffers(List<PluginStamp> baseline, List<PluginStamp> current)
        {
            if (baseline.Count != current.Count) return true;
            var b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in baseline) b[p.Guid] = p.Version;
            foreach (var p in current)
            {
                if (!b.TryGetValue(p.Guid, out var v)) return true;
                if (!string.Equals(v, p.Version, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static HashSet<string> ChangedPluginNames(
            List<PluginStamp> baseline, Dictionary<string, string> currentByGuid)
        {
            var idToName = BuildIdToName();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in baseline) b[p.Guid] = p.Version;

            foreach (var kv in currentByGuid)
            {
                if (b.TryGetValue(kv.Key, out var oldVer)
                    && !string.Equals(oldVer, kv.Value, StringComparison.Ordinal))
                {
                    changed.Add(idToName.TryGetValue(kv.Key, out var n) ? n : kv.Key);
                }
            }
            return changed;
        }

        private static BaselineHeader FreshHeader(List<PluginStamp> roster)
        {
            var h = new BaselineHeader
            {
                CapturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Game = SafeGet(() => Application.version),
                Unity = SafeGet(() => Application.unityVersion),
                Mvid = _currentMvid,
            };
            h.Plugins.AddRange(roster);
            return h;
        }

        private static string[] GameNamespaces()
        {
            var raw = Plugin.CfgDriftGameNamespaces != null ? Plugin.CfgDriftGameNamespaces.Value : "SpaceCraft.";
            var parts = (raw ?? "").Split(',');
            var outp = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) outp.Add(t);
            }
            if (outp.Count == 0) outp.Add("SpaceCraft.");
            return outp.ToArray();
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }
    }
}
