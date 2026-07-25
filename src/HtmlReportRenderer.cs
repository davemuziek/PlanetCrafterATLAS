using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// The second renderer over <see cref="ScanReport"/>. It writes a single self-contained
    /// <c>.html</c> file next to the text report on every scan. Same seam as the text renderer:
    /// it reads the (already inert) report and nothing else, and knows nothing about the text
    /// renderer or about how any of the data - conflicts, keybinds, drift - was produced.
    ///
    /// Where the text report is a flat transcript, this one inverts the order: verdict first,
    /// evidence on demand. The single job of the page is to answer "is anything wrong?" in under
    /// two seconds, and only then support drilling in.
    ///
    /// No external assets, no network, no storage. One &lt;style&gt; block, one &lt;script&gt;
    /// block. Every third-party string (mod names, method names, config values, drift members and
    /// details) is routed through <see cref="Esc"/> before it reaches the page.
    /// </summary>
    internal static class HtmlReportRenderer
    {
        public static string Write(ScanReport report, string directory, int keep,
                                   bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            System.IO.Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var path = System.IO.Path.Combine(directory, $"ModScan_{stamp}.html");

            var sb = new StringBuilder(48 * 1024);
            Page(sb, report, bundleEnabled, bundle);

            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Prune(directory, keep);
            return path;
        }

        /// <summary>
        /// Rolling backups, mirroring the text renderer but on its own ring: keep the newest N
        /// <c>.html</c> reports, oldest pruned first. Deliberately globs <c>ModScan_*.html</c> only,
        /// so the html ring never touches the <c>.txt</c> ring even though they share a count.
        /// </summary>
        private static void Prune(string directory, int keep)
        {
            if (keep < 1) return;
            try
            {
                var old = System.IO.Directory.GetFiles(directory, "ModScan_*.html")
                                   .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                                   .Skip(keep);
                foreach (var f in old)
                {
                    try { System.IO.File.Delete(f); } catch { }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("scan report (html) prune failed: " + ex.Message);
            }
        }

        // ── Page skeleton ───────────────────────────────────────────────────────────────────

        private static void Page(StringBuilder sb, ScanReport r, bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
            sb.Append("<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>ATLAS scan — ").Append(Esc(r.GameVersion)).Append("</title>\n");
            sb.Append("<style>\n").Append(Css).Append("\n</style>\n");
            sb.Append("</head>\n<body>\n");

            // Header
            sb.Append("<header class=\"topbar\">\n");
            sb.Append("  <div class=\"brand\">ATLAS <span>scan report</span></div>\n");
            sb.Append("  <div class=\"meta\">Game ").Append(Esc(r.GameVersion))
              .Append(" · Unity ").Append(Esc(r.UnityVersion))
              .Append(" · ").Append(r.PluginCount).Append(" plugins loaded · ")
              .Append(Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))).Append("</div>\n");
            sb.Append("</header>\n");

            BundleBar(sb, bundleEnabled, bundle);

            // Controls (filter + expand/collapse). Progressive enhancement: inert without JS.
            sb.Append("<div class=\"controls\" role=\"search\">\n");
            sb.Append("  <input id=\"filter\" type=\"search\" autocomplete=\"off\" spellcheck=\"false\" "
                    + "placeholder=\"Filter by mod, method, or key…\" aria-label=\"Filter rows by mod, method, or key\">\n");
            sb.Append("  <button id=\"expand-all\" type=\"button\">Expand all</button>\n");
            sb.Append("  <button id=\"collapse-all\" type=\"button\">Collapse all</button>\n");
            sb.Append("  <span id=\"filter-status\" class=\"filter-status\" aria-live=\"polite\"></span>\n");
            sb.Append("</div>\n");

            // Read-only report. Every write-action - setting exceptions, deleting a stale config -
            // lives in the in-game panel, which has real file access; a static file opened from disk
            // cannot write into the game folder (the old Save-As/download buttons only ever handed
            // back a decisions.tsv to place by hand, which is exactly the confusion they caused). So
            // point at the panel, and note that stale configs are just files the user can delete too.
            if (r.OrphanedConfigs.Count > 0 || r.IgnoredItems.Count > 0)
            {
                var n = r.OrphanedConfigs.Count;
                sb.Append("<div class=\"decisions-bar\">\n");
                sb.Append("  <span class=\"db-msg\">Read-only report — set and clear exceptions in the in-game panel "
                        + "(<strong>F3</strong>), which writes the change directly, with no file to move.");
                if (n > 0)
                    sb.Append(" To remove the ").Append(n).Append(" leftover config").Append(S(n))
                      .Append(" listed below, use the panel’s <strong>Leftover configs</strong> section, or just "
                            + "delete the <code>.cfg</code> file").Append(S(n))
                      .Append(" yourself from <code>BepInEx/config</code>.");
                sb.Append("</span>\n");
                sb.Append("</div>\n");
            }

            sb.Append("<main>\n");
            Verdict(sb, r);
            UpdateImpact(sb, r);   // emits nothing at all when drift is disabled
            Compatibility(sb, r);  // emits nothing at all when the compatibility check did not run
            PatchVerify(sb, r);    // emits nothing at all when runtime patch verification did not run
            Coverage(sb, r);       // emits nothing at all when coverage was not computed
            LogActivity(sb, r);    // emits nothing at all when the log summary did not run
            Keybinds(sb, r);
            Conflicts(sb, r);
            Dependencies(sb, r);
            Ignored(sb, r);
            Plugins(sb, r);
            sb.Append("</main>\n");

            sb.Append("<footer class=\"foot\">Generated by ATLAS. Static file — no network, no tracking. "
                    + "The text report (<code>.txt</code>) alongside this one is better for pasting into a bug report.</footer>\n");

            sb.Append("<script>\n").Append(Script).Append("\n</script>\n");
            sb.Append("</body>\n</html>\n");
        }

        // ── Support bundle bar ────────────────────────────────────────────────────────────
        // The 95%-path download: a plain <a download> link to the sibling zip. A hyperlink to a
        // sibling is not subject to the file:// fetch() restriction — the browser navigates
        // file:// → file:// and a .zip downloads. The zip is NOT base64-embedded (that would inflate
        // every scan HTML by ~1.33× the zip, across the whole retention ring). Facts, not a preview:
        // stated from the last build, since a static page cannot measure at click time. Rendered only
        // when auto-build is on; if the bundle is not built, the reason replaces the link.

        private static void BundleBar(StringBuilder sb, bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            if (!bundleEnabled) return;

            sb.Append("<div class=\"bundle-bar\">\n");
            if (bundle != null && bundle.Built)
            {
                sb.Append("  <div class=\"bb-main\">\n");
                sb.Append("    <a class=\"bb-dl\" href=\"").Append(Esc(SupportBundle.FileName))
                  .Append("\" download>⭳ Download support bundle</a>\n");
                sb.Append("    <span class=\"bb-facts\">").Append(Esc(SupportBundle.FactsLine(bundle))).Append("</span>\n");
                sb.Append("  </div>\n");
                sb.Append("  <p class=\"bb-frame\">One scrubbed, self-describing zip to hand a mod author or attach to a "
                        + "GitHub issue — this scan history, session logs and mod configs together. A finding is a lead, "
                        + "not a verdict; it is triage input.</p>\n");
            }
            else
            {
                var reason = bundle == null
                    ? "will be written after this scan completes — reload this report then."
                    : (bundle.Error.Length > 0 ? "last build failed — " + bundle.Error : "not built.");
                sb.Append("  <span class=\"bb-facts\">Support bundle: ").Append(Esc(reason)).Append("</span>\n");
            }
            sb.Append("</div>\n");
        }

        // ── Verdict ─────────────────────────────────────────────────────────────────────────

        private static void Verdict(StringBuilder sb, ScanReport r)
        {
            var (state, sentence) = ComputeVerdict(r);
            var cls = state == "PROBLEM" ? "v-problem" : state == "ATTENTION" ? "v-attention" : "v-clean";
            var label = state == "PROBLEM" ? "Problem" : state == "ATTENTION" ? "Attention" : "Clean";

            sb.Append("<section id=\"verdict\" class=\"verdict ").Append(cls).Append("\" aria-label=\"Verdict\">\n");
            sb.Append("  <div class=\"v-head\">\n");
            sb.Append("    <span class=\"v-dot\" aria-hidden=\"true\"></span>\n");
            sb.Append("    <span class=\"v-state\">").Append(label).Append("</span>\n");
            sb.Append("  </div>\n");
            sb.Append("  <p class=\"v-sentence\">").Append(Esc(sentence)).Append("</p>\n");

            // Compact stat strip.
            sb.Append("  <div class=\"stats\">\n");
            Stat(sb, r.PluginCount.ToString(), "plugins");
            Stat(sb, $"{r.HighCount} / {r.MediumCount} / {r.LowCount}", "conflicts H / M / L");
            Stat(sb, r.BindOverlaps.Count.ToString(), "keybind overlaps");
            if (r.ArchiveChecked)
                Stat(sb, $"{r.ObservedConflictCount} / {r.ArchiveLogCount}", "seen / error logs");
            else
                Stat(sb, "—", "no error logs yet");
            if (r.MissingDependencies.Count > 0)
                Stat(sb, r.MissingDependencies.Count.ToString(), "missing deps");
            if (DriftRan(r))
                Stat(sb, DriftCell(r), "update impact", true);
            if (r.CompatChecked)
                Stat(sb, r.CompatFindings.Count == 0 ? "all resolve" : $"{r.CompatFindings.Count} incompatible", "mod compatibility", true);
            if (r.PatchCheckRan)
                Stat(sb, PatchCell(r), "patch verification", true);
            sb.Append("  </div>\n");
            sb.Append("</section>\n");
        }

        private static void Stat(StringBuilder sb, string value, string label, bool wide = false)
        {
            sb.Append("    <div class=\"stat").Append(wide ? " stat-wide" : "").Append("\">");
            sb.Append("<span class=\"stat-v\">").Append(Esc(value)).Append("</span>");
            sb.Append("<span class=\"stat-l\">").Append(Esc(label)).Append("</span></div>\n");
        }

        private static string DriftCell(ScanReport r)
        {
            var code = r.DriftCodeState.Length > 0 ? r.DriftCodeState : "—";
            var content = r.DriftContentState.Length > 0 ? r.DriftContentState : "—";
            var open = r.DriftActiveCount + r.DriftReviewCount;
            var tail = r.DriftResolvedCount > 0 ? $" · {r.DriftResolvedCount} resolved" : "";
            return $"code: {code} · content: {content} · {r.DriftMethodsTracked} tracked · {open} open{tail}";
        }

        /// <summary>
        /// One private method, small (state, sentence) return, reason lists as List&lt;string&gt;,
        /// the sentence assembled from them - the same shape the text renderer's summary is built
        /// for extension. Drift contributes reasons from its High / Medium counts and finding kinds.
        /// </summary>
        /// <summary>The verdict state string ("CLEAN"/"ATTENTION"/"PROBLEM"), for the scans-index
        /// ledger to record the exact same verdict the report shows.</summary>
        public static string VerdictState(ScanReport r) => ComputeVerdict(r).state;

        private static (string state, string sentence) ComputeVerdict(ScanReport r)
        {
            var problems = new List<string>();
            if (r.HighCount > 0) problems.Add($"{r.HighCount} high-severity patch conflict{S(r.HighCount)}");
            if (r.ObservedConflictCount > 0) problems.Add($"{r.ObservedConflictCount} conflict{S(r.ObservedConflictCount)} seen in your error logs");
            if (r.MalformedBinds.Count > 0) problems.Add($"{r.MalformedBinds.Count} malformed keybind{S(r.MalformedBinds.Count)}");
            var hardDeps = r.MissingDependencies.Count(d => d.HardDependency);
            if (hardDeps > 0) problems.Add($"{hardDeps} missing hard {Dep(hardDeps)}");
            if (r.DriftHighCount > 0) problems.AddRange(DriftReasons(r, Severity.High, r.DriftHighCount));
            if (r.CompatHighCount > 0) problems.Add($"{r.CompatHighCount} incompatible mod hook{S(r.CompatHighCount)} (out of date with this game build)");
            if (r.PluginLoadFailures.Count > 0) problems.Add($"{r.PluginLoadFailures.Count} mod{S(r.PluginLoadFailures.Count)} failed to load");
            if (r.PatchApplyConfirmedCount > 0) problems.Add($"{r.PatchApplyConfirmedCount} declared patch{(r.PatchApplyConfirmedCount == 1 ? "" : "es")} did not apply");
            var hardVer = r.DependencyVersionIssues.Count(d => d.HardDependency);
            if (hardVer > 0) problems.Add($"{hardVer} hard {Dep(hardVer)} below the required version");

            var attention = new List<string>();
            if (r.MediumCount > 0) attention.Add($"{r.MediumCount} order-dependent {Patch(r.MediumCount)}");
            if (r.BindOverlaps.Count > 0) attention.Add($"{r.BindOverlaps.Count} keybind overlap{S(r.BindOverlaps.Count)}");
            if (r.ObservedKeyCollisions.Count > 0) attention.Add($"{r.ObservedKeyCollisions.Count} observed key collision{S(r.ObservedKeyCollisions.Count)}");
            var softDeps = r.MissingDependencies.Count(d => !d.HardDependency);
            if (softDeps > 0) attention.Add($"{softDeps} missing soft {Dep(softDeps)}");
            if (r.OrphanedConfigs.Count > 0) attention.Add($"{r.OrphanedConfigs.Count} leftover config{S(r.OrphanedConfigs.Count)}");
            if (r.DriftMediumCount > 0) attention.AddRange(DriftReasons(r, Severity.Medium, r.DriftMediumCount));
            var softVer = r.DependencyVersionIssues.Count(d => !d.HardDependency);
            if (softVer > 0) attention.Add($"{softVer} soft {Dep(softVer)} below the required version");

            if (problems.Count > 0)
                return ("PROBLEM", Cap(JoinReasons(problems)) + ".");

            if (attention.Count > 0)
                return ("ATTENTION", "Nothing broken, but " + JoinReasons(attention) + " worth a look.");

            // CLEAN — name the actual numbers, then the honest caveat if drift ran.
            string sentence;
            if (r.Conflicts.Count > 0 && r.ArchiveChecked)
                sentence = $"No problems found. {r.Conflicts.Count} shared patch point{S(r.Conflicts.Count)} checked against "
                         + $"{r.ArchiveLogCount} error log{S(r.ArchiveLogCount)}; none has ever thrown.";
            else if (r.Conflicts.Count > 0)
                sentence = $"No problems found. {r.Conflicts.Count} shared patch point{S(r.Conflicts.Count)} checked; "
                         + "no archived error logs to cross-reference against yet.";
            else
                sentence = "No problems found. No patch conflicts, missing dependencies, or keybind problems detected.";

            if (r.DriftChecked)
                sentence += " " + DriftState.CleanCaveat;

            return ("CLEAN", sentence);
        }

        private static IEnumerable<string> DriftReasons(ScanReport r, Severity sev, int fallbackCount)
        {
            var byKind = r.DriftFindings
                .Where(f => f.Kind != DriftKind.NotTracked && f.Status != DriftStatus.Resolved && f.Severity == sev)
                .GroupBy(f => f.Kind)
                .Select(g => $"{g.Count()} {KindPhrase(g.Key)}")
                .ToList();
            if (byKind.Count > 0) return byKind;
            // Counts said there is something at this tier but no finding rows carried it: never
            // drop the signal from the verdict - fall back to a generic, still-numbered reason.
            return new[] { $"{fallbackCount} update-impact finding{S(fallbackCount)}" };
        }

        /// <summary>Short, reader-facing phrase for a drift kind, used to build verdict reasons
        /// like "1 reflected member gone" / "1 patched method now missing".</summary>
        private static string KindPhrase(DriftKind k) => k switch
        {
            DriftKind.TypeMissing => "patched type now missing",
            DriftKind.TargetMissing => "patched method now missing",
            DriftKind.SignatureChanged => "patched method signature changed",
            DriftKind.BodyChanged => "patched method body changed",
            DriftKind.ReflectedMemberMissing => "reflected member gone",
            DriftKind.GroupAdded => "content group added",
            DriftKind.GroupRemoved => "content group removed",
            DriftKind.GroupFieldChanged => "content field changed",
            DriftKind.NullCraftableInList => "null craftable in a list",
            _ => "update-impact finding",
        };

        // ── Update Impact (drift) ─────────────────────────────────────────────────────────────
        // Reproduces, in HTML, the content of TextReportRenderer.UpdateImpact. That method is the
        // canonical wording; this matches it, it does not reword it.

        private static void UpdateImpact(StringBuilder sb, ScanReport r)
        {
            // Drift disabled entirely: emit nothing, exactly like the section did not exist.
            // Zero bytes - no empty <section>, no stray whitespace. Mirrors the text renderer.
            if (!r.DriftChecked && r.DriftCodeState.Length == 0 && r.DriftContentState.Length == 0)
                return;

            var active = new List<DriftFinding>();
            var review = new List<DriftFinding>();
            var resolved = new List<DriftFinding>();
            var notTracked = new List<DriftFinding>();
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

            sb.Append("<section class=\"card drift\" aria-label=\"Update impact\">\n");
            sb.Append("  <h2 class=\"card-title\">Update impact "
                    + "<span class=\"card-sub\">did a game update move the ground under your mods?</span></h2>\n");

            // 1. Surface states — five distinct treatments; Changed wears the external-change register.
            sb.Append("  <div class=\"surfaces\">\n");
            SurfaceState(sb, "Code surface", r.DriftCodeState, r.DriftCodeDetail);
            SurfaceState(sb, "Content surface", r.DriftContentState, r.DriftContentDetail);
            sb.Append("  </div>\n");

            // 2. Build-changed banner — the reserved "something external moved" register.
            if (r.GameBuildChanged)
            {
                sb.Append("  <p class=\"ext-banner\">Game build <strong>changed</strong>: baseline ")
                  .Append(Esc(r.BaselineGameVersion)).Append(" (mvid ").Append(Esc(Short(r.BaselineMvid)))
                  .Append(") → now ").Append(Esc(r.GameVersion)).Append(" (mvid ").Append(Esc(Short(r.CurrentMvid)))
                  .Append(").</p>\n");
            }

            // 3. Roster-changed note.
            if (r.PluginRosterChanged)
                sb.Append("  <p class=\"note\">Plugin roster changed since the baseline, so added / removed "
                        + "groups cannot be cleanly attributed.</p>\n");

            // 4. Stat line.
            sb.Append("  <p class=\"drift-stats\"><span>").Append(r.DriftMethodsTracked)
              .Append(" tracked</span> · <span>").Append(r.DriftUnresolvedReflectionSites)
              .Append(" unresolved reflection site").Append(S(r.DriftUnresolvedReflectionSites))
              .Append("</span> · <span>").Append(r.DriftScanMillis).Append(" ms</span></p>\n");

            // 5. Findings, grouped by live status: Resolved (good news, shown once) leads, then the
            //    Active problems, then the Review band that cannot be auto-verified.
            if (resolved.Count > 0)
            {
                sb.Append("  <div class=\"drift-resolved\">\n");
                sb.Append("    <p class=\"drift-count\"><span class=\"chip st-resolved\">Resolved ✓</span> ")
                  .Append(resolved.Count).Append(" re-verified against your current mods and no longer broken — "
                        + "your fix took. Shown once, then cleared.</p>\n");
                foreach (var f in resolved) DriftRow(sb, f, r);
                sb.Append("  </div>\n");
            }

            if (openFindings == 0)
            {
                sb.Append("  <p class=\"drift-clean\">");
                if (IsFresh(r.DriftCodeState) || IsFresh(r.DriftContentState))
                    sb.Append("Baseline established now. Nothing to compare yet — this run records the ground, "
                            + "it does not verify it.");
                else if (Unusable(r.DriftCodeState) || Unusable(r.DriftContentState))
                    sb.Append("No comparison ran on one or more surfaces (see the surface states above). "
                            + "The rest of the scan is unaffected.");
                else if (resolved.Count == 0)
                    sb.Append("No differences found in the surfaces ATLAS tracks. That is <strong>not</strong> the same "
                            + "as nothing being broken (see limits below).");
                else
                    sb.Append("Nothing else open — every finding has been resolved or accepted.");
                sb.Append("</p>\n");
            }
            else
            {
                sb.Append("  <p class=\"drift-count\"><strong>").Append(active.Count)
                  .Append(" active</strong> (still broken) / ").Append(review.Count)
                  .Append(" review (ground moved, can’t auto-verify a fix).</p>\n");

                foreach (var f in active) DriftRow(sb, f, r);
                foreach (var f in review) DriftRow(sb, f, r);

                // How the two open bands leave the report - self-heal vs. deliberate accept.
                sb.Append("  <div class=\"accept\">\n");
                sb.Append("    <p><span class=\"chip st-active\">Active</span> findings clear themselves once you "
                        + "fix the mod and re-scan — you’ll see them flip to Resolved once, then drop off.</p>\n");
                sb.Append("    <p><span class=\"chip st-review\">Review</span> findings are changes static analysis "
                        + "cannot confirm a fix for (a body/signature/content change). They never auto-clear.</p>\n");
                sb.Append("    <p>Baseline captured ").Append(Esc(Ago(r.BaselineCapturedUtc)))
                  .Append(" on game ").Append(Esc(r.BaselineGameVersion))
                  .Append(". To accept this build as the new baseline and clear the Review band, set "
                        + "<code>Drift.AcceptCurrentBuild = true</code> and load a save. It resets itself afterwards.</p>\n");
                sb.Append("  </div>\n");
            }

            // 6. Not-yet-tracked — quiet collapsed block, never in the tiered list.
            if (notTracked.Count > 0)
            {
                sb.Append("  <details class=\"quiet\">\n");
                sb.Append("    <summary>Not yet tracked (").Append(notTracked.Count)
                  .Append(") — newly patched since the baseline, recorded for future comparison</summary>\n");
                sb.Append("    <ul class=\"plain\">\n");
                foreach (var f in notTracked)
                    sb.Append("      <li class=\"frow\" data-f=\"").Append(EscAttr(f.Member.ToLowerInvariant()))
                      .Append("\"><code class=\"member\">").Append(Esc(f.Member)).Append("</code></li>\n");
                sb.Append("    </ul>\n");
                sb.Append("  </details>\n");
            }

            // 7. Limits — always, in-section. A tool that implies more certainty than it has is
            //    worse than no tool, so this reaches the reader here, not only in the README.
            sb.Append("  <details class=\"limits\">\n");
            sb.Append("    <summary>What drift cannot see</summary>\n");
            sb.Append("    <ul class=\"plain\">\n");
            foreach (var lim in DriftState.Limits)
                sb.Append("      <li>").Append(Esc(lim)).Append("</li>\n");
            sb.Append("    </ul>\n");
            sb.Append("    <p class=\"caveat\">A clean result means: “").Append(Esc(DriftState.CleanCaveat)).Append("”</p>\n");
            sb.Append("  </details>\n");

            sb.Append("</section>\n");
        }

        // ── Mod compatibility (0.10.0) ──────────────────────────────────────────────────────────
        // A SEPARATE axis from Update impact above: baseline-independent, "do this mod's hooks exist
        // in the game as installed right now?" The header keeps the two from ever reading as one.

        private static void Compatibility(StringBuilder sb, ScanReport r)
        {
            // Check did not run (disabled, or the game assembly was unavailable): emit nothing at
            // all, exactly like the section did not exist. Mirrors the Update-impact convention.
            if (!r.CompatChecked) return;

            sb.Append("<section class=\"card\" aria-label=\"Mod compatibility\">\n");
            sb.Append("  <h2 class=\"card-title\">Mod compatibility "
                    + "<span class=\"card-sub\">do the game members each installed mod hooks still exist in this build?</span></h2>\n");
            sb.Append("  <p class=\"note\">Independent of the update-impact check above, and needs no baseline: it resolves every "
                    + "installed mod's Harmony patch targets and reflected members against the game exactly as installed, right now. "
                    + "This is how an out-of-date mod that targets a since-removed or renamed game member is caught — the case the "
                    + "baseline comparison, which only watches for the game changing, cannot see.</p>\n");

            if (r.CompatFindings.Count == 0)
            {
                sb.Append("  <p class=\"drift-clean\">Every installed mod's patch targets and reflected members resolve against "
                        + "this game build. A mod can still misbehave in ways this cannot see (below), and an old mod whose hooks "
                        + "all still exist is correctly counted as fine here.</p>\n");
            }
            else
            {
                sb.Append("  <p class=\"drift-count\"><strong>").Append(r.CompatFindings.Count)
                  .Append("</strong> mod hook").Append(S(r.CompatFindings.Count))
                  .Append(" do not resolve against this build — the owning mod is out of date with the installed game and will "
                        + "fail where it reaches for the missing member.</p>\n");

                foreach (var f in r.CompatFindings)
                {
                    var attrib = f.Owners.Count > 0
                        ? "<span class=\"owners\">" + Esc(string.Join(", ", f.Owners.ToArray())) + "</span>"
                        : "";
                    var chip = f.Origin == DriftOrigin.Reflection
                        ? "<span class=\"estate es-warn\">reflection</span>"
                        : "<span class=\"estate es-warn\">patch target</span>";
                    var df = (f.Member + " " + string.Join(" ", f.Owners.ToArray()) + " " + f.Kind + " incompatible").ToLowerInvariant();
                    FindingRow(sb, f.Severity, f.Member, chip, f.Detail, attrib, null, df);
                }
            }

            // Limits — always in-section, per the house rule that a tool must state where it is blind.
            sb.Append("  <details class=\"limits\">\n");
            sb.Append("    <summary>What the compatibility check cannot see</summary>\n");
            sb.Append("    <ul class=\"plain\">\n");
            sb.Append("      <li>hardcoded or inlined targets, and <code>[HarmonyPatch]</code> overloads ATLAS does not statically "
                    + "parse — the same coverage ceiling the reflection scan has; absence of a finding is not proof of compatibility</li>\n");
            sb.Append("      <li>a method whose name still exists but whose signature changed — that is the baseline comparison's job "
                    + "(Update impact), checked by name here, not by signature</li>\n");
            sb.Append("      <li>targets outside the game namespaces (<code>Drift.GameNamespaces</code>) — a mod-to-mod reference is not adjudicated</li>\n");
            sb.Append("      <li>whether a resolvable hook still <em>behaves</em> the same — existence is checked, not meaning</li>\n");
            sb.Append("    </ul>\n");
            sb.Append("  </details>\n");

            sb.Append("</section>\n");
        }

        // ── Patch verification (0.11.0) ─────────────────────────────────────────────────────────
        // The runtime-truth axis: did declared patches actually apply, and did every mod load? Read
        // from Harmony's live registry after all mods loaded and from the error logs — distinct from
        // the static drift/compat sections above, and headed to say so.

        private static string PatchCell(ScanReport r)
        {
            var applied = r.PatchDeclaredChecked > 0 ? $"{r.PatchAppliedVerified}/{r.PatchDeclaredChecked} applied" : "no declarations";
            var fails = r.PatchApplyConfirmedCount + r.PluginLoadFailures.Count;
            return fails > 0 ? $"{applied} · {fails} failure{S(fails)}" : $"{applied} · clean";
        }

        private static void PatchVerify(StringBuilder sb, ScanReport r)
        {
            if (!r.PatchCheckRan) return;   // did not run: emit nothing, like the section did not exist

            var confirmed = new List<PatchApplyFinding>();
            var unconfirmed = new List<PatchApplyFinding>();
            foreach (var f in r.PatchApplyFindings)
                (f.LogCorroborated ? confirmed : unconfirmed).Add(f);

            sb.Append("<section class=\"card\" aria-label=\"Patch verification\">\n");
            sb.Append("  <h2 class=\"card-title\">Patch verification "
                    + "<span class=\"card-sub\">did each mod's declared patches apply, and did every mod load?</span></h2>\n");
            sb.Append("  <p class=\"note\">Read from Harmony's live registry after every mod loaded, and from the error logs — "
                    + "so it reports what is broken right now, with no baseline and no game update needed. This is the axis "
                    + "that catches a patch which silently did not take even though its target still exists (which the "
                    + "compatibility check cannot see), and names mods that failed to load and are absent from the roster above.</p>\n");

            // 1. Plugin load failures — unambiguous, lead the section.
            if (r.PluginLoadFailures.Count > 0)
            {
                sb.Append("  <div class=\"seen-banner\">\n");
                sb.Append("    <div class=\"seen-head\">Failed to load <span class=\"count\">").Append(r.PluginLoadFailures.Count)
                  .Append("</span> — the error logs show these mods threw during load, so they are not running</div>\n");
                foreach (var lf in r.PluginLoadFailures)
                {
                    var df = (lf.Plugin + " " + lf.Error).ToLowerInvariant();
                    var reason = lf.Error.Length > 0 ? lf.Error : "load error (see " + lf.LogName + ")";
                    FindingRow(sb, Severity.High, lf.Plugin, "<span class=\"estate es-hit\">did not load</span>",
                        reason, "<span class=\"owners\">from " + Esc(lf.LogName) + "</span>", null, df);
                }
                sb.Append("  </div>\n");
            }

            // 2. Confirmed not-applied — High, log-corroborated.
            if (confirmed.Count > 0)
            {
                sb.Append("  <details open class=\"sub\">\n");
                sb.Append("    <summary>Patches that did not apply <span class=\"count\">").Append(confirmed.Count)
                  .Append("</span> <span class=\"chip sev-high\">confirmed by the logs</span></summary>\n");
                foreach (var f in confirmed) PatchRow(sb, f);
                sb.Append("  </details>\n");
            }

            // 3. Positive summary — makes the negatives legible.
            if (r.PatchDeclaredChecked > 0)
                sb.Append("  <p class=\"drift-count\"><strong>").Append(r.PatchAppliedVerified).Append(" / ")
                  .Append(r.PatchDeclaredChecked).Append("</strong> declared patch target")
                  .Append(S(r.PatchDeclaredChecked)).Append(" verified live in Harmony's registry.</p>\n");
            else
                sb.Append("  <p class=\"note\">No declared patches to reconcile (the Drift code pass is off, or no mod declares a "
                        + "game-namespace <code>[HarmonyPatch]</code>). The load-failure check above still ran.</p>\n");

            // 4. Unconfirmed not-applied — Low, collapsed, with the benign-causes note.
            if (unconfirmed.Count > 0)
            {
                sb.Append("  <details class=\"sub quiet\">\n");
                sb.Append("    <summary>Declared but not observed applied <span class=\"count\">").Append(unconfirmed.Count)
                  .Append("</span> <span class=\"chip sev-low\">often benign</span></summary>\n");
                sb.Append("    <p class=\"note\">A declared patch ATLAS did not find in the live registry, with no error in the "
                        + "logs. Usually a patch applied only under a config toggle, or a target resolved dynamically at runtime — "
                        + "not necessarily a failure. Worth confirming the feature works.</p>\n");
                foreach (var f in unconfirmed) PatchRow(sb, f);
                sb.Append("  </details>\n");
            }

            // 5. Limits.
            sb.Append("  <details class=\"limits\">\n");
            sb.Append("    <summary>What patch verification cannot tell you</summary>\n");
            sb.Append("    <ul class=\"plain\">\n");
            sb.Append("      <li>a patch that applied but is wrong — verification confirms it applied, not that it behaves correctly</li>\n");
            sb.Append("      <li>patches with dynamically-computed or hardcoded targets ATLAS could not statically recover — those are never in the declared set to reconcile</li>\n");
            sb.Append("      <li>matching is by method name, not signature; a same-name overload that applied reads as applied</li>\n");
            sb.Append("      <li>the load-failure list depends on the error being in an archived log; a fresh install with no logs shows none</li>\n");
            sb.Append("    </ul>\n");
            sb.Append("  </details>\n");

            sb.Append("</section>\n");
        }

        private static void PatchRow(StringBuilder sb, PatchApplyFinding f)
        {
            var attrib = f.Owners.Count > 0
                ? "<span class=\"owners\">" + Esc(string.Join(", ", f.Owners.ToArray())) + "</span>"
                : "";
            var df = (f.Member + " " + string.Join(" ", f.Owners.ToArray()) + " not-applied").ToLowerInvariant();
            FindingRow(sb, f.Severity, f.Member, "<span class=\"estate es-warn\">not applied</span>", f.Detail, attrib, null, df);
        }

        // ── Analysis coverage (0.12.0) ──────────────────────────────────────────────────────────
        // Per-mod static visibility — how much of each mod ATLAS could actually resolve. Informational:
        // it frames how much weight to put on the other sections' "clean" results, and never feeds the
        // verdict. Collapsed by default; the partial-visibility mods (the ones ATLAS is blind on) lead.

        private static void Coverage(StringBuilder sb, ScanReport r)
        {
            if (!r.CoverageChecked || r.ModCoverages.Count == 0) return;

            sb.Append("<section class=\"card\" aria-label=\"Analysis coverage\">\n");
            sb.Append("  <details class=\"sub\">\n");
            sb.Append("    <summary class=\"card-title-summary\">Analysis coverage <span class=\"count\">")
              .Append(r.CoverageFullyVisibleMods).Append(" / ").Append(r.ModCoverages.Count)
              .Append(" fully visible</span> <span class=\"sub-note\">how much of each mod ATLAS could resolve</span></summary>\n");
            sb.Append("    <p class=\"note\">A clean result on a fully-visible mod is trustworthy; on a partial mod it covers "
                    + "only the hooks ATLAS could statically resolve. “Not resolvable” means a dynamic/computed patch "
                    + "target or a reflection type ATLAS could not recover — not necessarily a problem, just a blind spot.</p>\n");
            if (r.CoveragePartialMods > 0)
                sb.Append("    <p class=\"note strong\">").Append(r.CoveragePartialMods)
                  .Append(" mod").Append(S(r.CoveragePartialMods)).Append(" with hooks ATLAS cannot statically verify.</p>\n");

            sb.Append("    <table class=\"plugins\">\n");
            sb.Append("      <thead><tr><th>Mod</th><th class=\"num\">Patch</th><th class=\"num\">Reflection</th>"
                    + "<th class=\"num\">Not resolvable</th><th>Visibility</th></tr></thead>\n");
            sb.Append("      <tbody>\n");
            foreach (var c in r.ModCoverages)
            {
                var df = (c.Mod + (c.FullyVisible ? " full" : " partial")).ToLowerInvariant();
                var vis = c.FullyVisible
                    ? "<span class=\"chip sev-none\">full</span>"
                    : "<span class=\"chip sev-medium\">partial</span>";
                sb.Append("        <tr class=\"frow\" data-f=\"").Append(EscAttr(df)).Append("\">");
                sb.Append("<td>").Append(Esc(c.Mod)).Append("</td>");
                sb.Append("<td class=\"num\">").Append(c.PatchResolved).Append("</td>");
                sb.Append("<td class=\"num\">").Append(c.ReflectionResolved).Append("</td>");
                sb.Append("<td class=\"num\">").Append(c.Unresolved > 0 ? c.Unresolved.ToString() : "—").Append("</td>");
                sb.Append("<td>").Append(vis).Append("</td></tr>\n");
            }
            sb.Append("      </tbody>\n    </table>\n");
            sb.Append("  </details>\n");
            sb.Append("</section>\n");
        }

        // ── Log activity (0.14.0) ───────────────────────────────────────────────────────────────
        // A step back over the archived logs: what recurs vs what fired once, and who is noisiest.
        // Diagnostic context — never feeds the verdict or the H/M/L counts.

        private static void LogActivity(StringBuilder sb, ScanReport r)
        {
            var la = r.LogActivity;
            if (la == null || !la.Analyzed) return;

            sb.Append("<section class=\"card\" aria-label=\"Log activity\">\n");
            sb.Append("  <h2 class=\"card-title\">Log activity <span class=\"card-sub\">what your archived logs have been doing</span></h2>\n");
            sb.Append("  <p class=\"note\">").Append(la.TotalEvents).Append(" error/warning event").Append(S(la.TotalEvents))
              .Append(" across ").Append(la.LogsScanned).Append(" archived log").Append(S(la.LogsScanned))
              .Append(". <strong>Consistently firing</strong> = seen in 2+ sessions (a standing issue); "
                    + "<strong>situational</strong> = one session. Grouped by exception + frame; “recurs” and "
                    + "first/last seen are by session log, not per line.</p>\n");

            if (la.TotalEvents == 0)
            {
                sb.Append("  <p class=\"drift-clean\">No errors or warnings in the kept logs.</p>\n");
                sb.Append("</section>\n");
                return;
            }

            if (la.Consistent.Count > 0)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Consistently firing <span class=\"count\">").Append(la.ConsistentTotal)
                  .Append("</span> <span class=\"sub-note\">seen across 2+ sessions</span></summary>\n");
                foreach (var g in la.Consistent) LogRow(sb, g);
                MoreNote(sb, la.ConsistentTotal, la.Consistent.Count);
                sb.Append("  </details>\n");
            }

            if (la.Situational.Count > 0)
            {
                sb.Append("  <details class=\"sub quiet\">\n");
                sb.Append("    <summary>Situational <span class=\"count\">").Append(la.SituationalTotal)
                  .Append("</span> <span class=\"sub-note\">one session only</span></summary>\n");
                foreach (var g in la.Situational) LogRow(sb, g);
                MoreNote(sb, la.SituationalTotal, la.Situational.Count);
                sb.Append("  </details>\n");
            }

            if (la.Noisy.Count > 0)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Noisiest sources <span class=\"count\">").Append(la.NoisyTotal)
                  .Append("</span> <span class=\"sub-note\">by BepInEx logger tag — export a source's full activity catalog</span></summary>\n");
                sb.Append("    <ul class=\"plain\">\n");
                foreach (var n in la.Noisy) NoisyRow(sb, n);
                sb.Append("    </ul>\n");
                sb.Append("  </details>\n");
            }

            sb.Append("</section>\n");
        }

        /// <summary>
        /// A noisiest-source row with a per-source catalog export (0.14.1/0.14.2), reusing the exact
        /// fix-brief export machinery: a <c>.brief-export</c> group carrying both renderings as base64,
        /// picked up by the same <c>.brief-btn</c> handler — so it downloads from a file:// page with no
        /// network and no new JS. The catalog is the source's FULL (capped) activity list, not just the
        /// top-N shown on the page.
        /// </summary>
        private static void NoisyRow(StringBuilder sb, NoisySource n)
        {
            var html = Convert.ToBase64String(Encoding.UTF8.GetBytes(LogCatalog.BuildHtml(n)));
            var txt = Convert.ToBase64String(Encoding.UTF8.GetBytes(LogCatalog.BuildText(n)));

            sb.Append("      <li class=\"frow noisy-row\" data-f=\"").Append(EscAttr(n.Source.ToLowerInvariant())).Append("\">");
            sb.Append("<span class=\"noisy-label\"><code>").Append(Esc(n.Source)).Append("</code> — ").Append(n.Count)
              .Append(" event").Append(S(n.Count)).Append(" in ").Append(n.SessionCount)
              .Append(" session").Append(S(n.SessionCount)).Append("</span>");
            sb.Append("<span class=\"brief-export\" data-html=\"").Append(html).Append("\" data-txt=\"").Append(txt)
              .Append("\" data-file=\"").Append(EscAttr(LogCatalog.FileNameBase(n.Source))).Append("\">")
              .Append("<span class=\"brief-lbl\">Catalog</span>")
              .Append("<button type=\"button\" class=\"brief-btn\" data-fmt=\"html\" "
                    + "title=\"Download an HTML catalog of this source's log activity\">HTML</button>")
              .Append("<button type=\"button\" class=\"brief-btn\" data-fmt=\"txt\" "
                    + "title=\"Download a plain-text catalog (also copied to the clipboard)\">.txt</button>")
              .Append("</span>");
            sb.Append("</li>\n");
        }

        private static void MoreNote(StringBuilder sb, int total, int shown)
        {
            if (total > shown)
                sb.Append("    <p class=\"note\">+ ").Append(total - shown).Append(" more not shown.</p>\n");
        }

        private static void LogRow(StringBuilder sb, LogEventGroup g)
        {
            var cls = g.Level == "WARNING" ? "sev-medium" : "sev-high";
            var icon = g.Level == "WARNING" ? "◆" : "▲";
            var df = (g.Label + " " + g.Level + " " + g.Source + " " + g.ExampleFrame).ToLowerInvariant();

            sb.Append("  <div class=\"finding ").Append(cls).Append(" frow\" data-f=\"").Append(EscAttr(df)).Append("\">\n");
            sb.Append("    <span class=\"ficon\" aria-hidden=\"true\">").Append(icon).Append("</span>\n");
            sb.Append("    <div class=\"finding-body\">\n");
            sb.Append("      <div class=\"finding-head\"><code class=\"member\">").Append(Esc(g.Label)).Append("</code>")
              .Append("<span class=\"chip ").Append(cls).Append("\">").Append(Esc(g.Level)).Append("</span>");
            if (g.Source.Length > 0)
                sb.Append("<span class=\"src-tag\">").Append(Esc(g.Source)).Append("</span>");
            sb.Append("</div>\n");
            var meta = "×" + g.Count + " in " + g.SessionCount + " session" + S(g.SessionCount);
            if (g.FirstSeen.Length > 0)
                meta += " · " + g.FirstSeen + (g.LastSeen.Length > 0 && g.LastSeen != g.FirstSeen ? " → " + g.LastSeen : "");
            sb.Append("      <p class=\"reason\">").Append(Esc(meta)).Append("</p>\n");
            if (g.ExampleFrame.Length > 0 && g.ExampleFrame != g.Frame)
                sb.Append("      <p class=\"attrib\"><span class=\"owners\">").Append(Esc(g.ExampleFrame)).Append("</span></p>\n");
            sb.Append("    </div>\n");
            sb.Append("  </div>\n");
        }

        private static void SurfaceState(StringBuilder sb, string label, string state, string detail)
        {
            var (cls, text) = SurfaceStyle(state);
            sb.Append("    <div class=\"surface\">\n");
            sb.Append("      <span class=\"surface-label\">").Append(Esc(label)).Append("</span>\n");
            sb.Append("      <span class=\"estate ").Append(cls).Append("\">").Append(Esc(text)).Append("</span>\n");
            if (detail.Length > 0)
                sb.Append("      <span class=\"surface-detail\">").Append(Esc(detail)).Append("</span>\n");
            sb.Append("    </div>\n");
        }

        /// <summary>
        /// The five §8 lifecycle states, each rendered distinctly (criterion 15). Changed wears the
        /// reserved external-change register; Unreadable and Unavailable are muted but never
        /// collapsed into each other.
        /// </summary>
        private static (string cls, string text) SurfaceStyle(string state) => state switch
        {
            "Unchanged"   => ("es-ok", "Unchanged"),
            "Changed"     => ("es-ext", "Changed"),
            "NoBaseline"  => ("es-idle", "No baseline"),
            "Unreadable"  => ("es-warn", "Unreadable"),
            "Unavailable" => ("es-muted", "Unavailable"),
            _             => ("es-idle", "not run"),
        };

        private static void DriftRow(StringBuilder sb, DriftFinding f, ScanReport r)
        {
            var attrib = new StringBuilder();
            if (f.Owners.Count > 0)
            {
                attrib.Append("<span class=\"owners\">").Append(Esc(string.Join(", ", f.Owners.ToArray()))).Append("</span>");
                if (f.PatchKinds.Length > 0) attrib.Append(' ').Append(MarksFromString(f.PatchKinds));
            }
            var resolved = f.Status == DriftStatus.Resolved;
            var note = (!resolved && f.OwnerVersionChanged)
                ? "a patching mod was updated since the baseline — may already be fixed"
                : null;
            var dataF = (f.Member + " " + string.Join(" ", f.Owners.ToArray()) + " " + f.Kind + " " + f.Status)
                        .ToLowerInvariant();

            // A live-status chip beside the severity chip, so a row says both how bad it is and
            // whether it is still real right now.
            var statusChip = f.Status switch
            {
                DriftStatus.Active => "<span class=\"chip st-active\">Active</span>",
                DriftStatus.Review => "<span class=\"chip st-review\">Review</span>",
                _ => "<span class=\"chip st-resolved\">Resolved ✓</span>",
            };

            // The fix-brief export, on Active/Review only - a resolved finding has nothing to fix.
            // Both renderings are carried on the row as base64 so either download works from a
            // file:// page with no network, keeping the report a single self-contained file.
            var action = statusChip;
            if (!resolved)
            {
                var htmlPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(FixBrief.BuildHtml(f, r)));
                var txtPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(FixBrief.BuildText(f, r)));
                action += "<span class=\"brief-export\" data-html=\"" + htmlPayload + "\" data-txt=\"" + txtPayload
                       + "\" data-file=\"" + EscAttr(FixBrief.FileNameBase(f)) + "\">"
                       + "<span class=\"brief-lbl\">Fix brief</span>"
                       + "<button type=\"button\" class=\"brief-btn\" data-fmt=\"html\" "
                       + "title=\"Download an HTML brief you can open in any browser\">HTML</button>"
                       + "<button type=\"button\" class=\"brief-btn\" data-fmt=\"txt\" "
                       + "title=\"Download a plain-text brief (also copied to clipboard)\">.txt</button>"
                       + "</span>";
            }

            // Resolved rows are shown at rest severity so the row does not shout in red.
            var sev = resolved ? Severity.None : f.Severity;
            FindingRow(sb, sev, f.Member, "", f.Detail, attrib.ToString(), note, dataF, action);
        }

        // ── Keybinds ──────────────────────────────────────────────────────────────────────────

        private static void Keybinds(StringBuilder sb, ScanReport r)
        {
            sb.Append("<section class=\"card\" aria-label=\"Keybinds\">\n");
            sb.Append("  <h2 class=\"card-title\">Keybinds</h2>\n");

            var kb = r.Binds.Count(b => !b.IsController);
            var pad = r.Binds.Count(b => b.IsController);
            sb.Append("  <p class=\"note\">").Append(kb).Append(" keyboard/mouse and ").Append(pad)
              .Append(" controller bindings seen ")
              .Append(r.GameBindingsFound ? "(game + mods)." : "(mods only — game bindings unavailable).")
              .Append(" Hardcoded mod keys not exposed as config entries cannot be detected, so this is a strong hint, "
                    + "not a guarantee.</p>\n");

            // 1. Malformed — expanded whenever non-empty. Outright broken.
            if (r.MalformedBinds.Count > 0)
            {
                sb.Append("  <details open class=\"sub\">\n");
                sb.Append("    <summary>Malformed bindings <span class=\"count\">").Append(r.MalformedBinds.Count)
                  .Append("</span> <span class=\"chip sev-high\">will not work</span></summary>\n");
                sb.Append("    <p class=\"note\">Some are intentional — a placeholder in a controller bind, say. "
                        + "Set those aside in-game with the ATLAS panel (F3).</p>\n");
                foreach (var m in r.MalformedBinds)
                {
                    sb.Append("    <div class=\"finding sev-high frow\" data-f=\"").Append(EscAttr(m.ToLowerInvariant())).Append("\">");
                    sb.Append("<span class=\"ficon\" aria-hidden=\"true\">▲</span>");
                    sb.Append("<code class=\"member\">").Append(Esc(m)).Append("</code></div>\n");
                }
                sb.Append("  </details>\n");
            }

            // 2. Overlaps — expanded. Not automatically a bug.
            sb.Append("  <details open class=\"sub\">\n");
            sb.Append("    <summary>Overlaps <span class=\"count\">").Append(r.BindOverlaps.Count).Append("</span></summary>\n");
            if (r.BindOverlaps.Count == 0)
            {
                sb.Append("    <p class=\"empty\">No two bindings resolve to the same control.</p>\n");
            }
            else
            {
                sb.Append("    <p class=\"note\">Not always a bug: mods active in different contexts can share a key.</p>\n");
                // Confirmed overlaps (runtime evidence, item 2) lead the block. OrderByDescending is
                // stable, so unconfirmed overlaps keep their existing device/control order below.
                foreach (var o in r.BindOverlaps.OrderByDescending(ov => ov.Confirmed))
                {
                    var df = (o.Control + " " + string.Join(" ", o.Binds.Select(b => b.Label + " " + b.Owner))
                              + (o.Confirmed ? " confirmed" : "")).ToLowerInvariant();
                    sb.Append("    <div class=\"overlap frow\" data-f=\"").Append(EscAttr(df)).Append("\">\n");
                    sb.Append("      <div class=\"overlap-key\">").Append(Keycaps(o.Control, o.IsController));
                    if (o.Confirmed)
                        sb.Append(" <span class=\"estate es-hit\">confirmed · fired in ").Append(Esc(o.ConfirmedBy))
                          .Append(" (").Append(o.ConfirmedCount).Append("×)</span>");
                    sb.Append("</div>\n");
                    sb.Append("      <ul class=\"overlap-claims\">\n");
                    foreach (var b in o.Binds)
                        sb.Append("        <li><span class=\"claim-label\">").Append(Esc(b.Label))
                          .Append("</span> <span class=\"claim-val\">").Append(Esc(b.RawValue)).Append("</span>")
                          .Append(SourceTag(b.Source)).Append("</li>\n");
                    sb.Append("      </ul>\n");
                    sb.Append("    </div>\n");
                }
            }
            sb.Append("  </details>\n");

            // 3. Learned at runtime — only when learning is active.
            if (r.KeyLearningActive)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Learned at runtime <span class=\"count\">").Append(r.ObservedKeys.Count).Append("</span></summary>\n");
                if (r.ObservedKeys.Count == 0)
                {
                    sb.Append("    <p class=\"empty\">Nothing recorded yet. ").Append(r.KeyReadsIntercepted)
                      .Append(" button read").Append(S(r.KeyReadsIntercepted)).Append(" intercepted, ")
                      .Append(r.KeyReadsUnattributed).Append(" could not be traced to a mod. ");
                    if (r.KeyReadsIntercepted == 0)
                        sb.Append("Zero interceptions means the watcher is not seeing key reads at all. ");
                    sb.Append("The load-time scan also runs before you can press anything, so a fresh world looks empty here "
                            + "regardless; totals carry over between sessions.</p>\n");
                }
                else
                {
                    var undeclared = r.ObservedKeys.Count(o => !o.InConfig);
                    sb.Append("    <p class=\"note\">").Append(r.ObservedKeys.Count).Append(" key")
                      .Append(S(r.ObservedKeys.Count)).Append(" seen being watched by mods; ").Append(undeclared)
                      .Append(" declared in no config file (those are the discoveries static scanning cannot make).</p>\n");
                    // InConfig == false first: the undeclared hardcoded keys are the payload.
                    foreach (var o in r.ObservedKeys.OrderBy(k => k.InConfig))
                    {
                        var activity = o.Count > 0 ? $"pressed {o.Count}×" : "watched, not pressed";
                        var df = (o.Plugin + " " + o.Control).ToLowerInvariant();
                        sb.Append("    <div class=\"kobs frow").Append(o.InConfig ? "" : " kobs-new").Append("\" data-f=\"")
                          .Append(EscAttr(df)).Append("\">");
                        sb.Append(Keycaps(o.Control, o.IsController));
                        sb.Append("<span class=\"kobs-plugin\">").Append(Esc(o.Plugin)).Append("</span>");
                        sb.Append("<span class=\"kobs-act\">").Append(Esc(activity)).Append(", first ").Append(Esc(o.FirstSeen)).Append("</span>");
                        if (!o.InConfig) sb.Append("<span class=\"chip sev-low\">hardcoded</span>");
                        sb.Append("</div>\n");
                    }

                    if (r.ObservedKeyCollisions.Count > 0)
                    {
                        sb.Append("    <p class=\"note strong\">Confirmed simultaneous use (").Append(r.ObservedKeyCollisions.Count)
                          .Append(") — two mods read the same control on the same frame:</p>\n");
                        foreach (var c in r.ObservedKeyCollisions)
                        {
                            var df = (c.Control + " " + c.PluginA + " " + c.PluginB).ToLowerInvariant();
                            sb.Append("    <div class=\"kobs frow\" data-f=\"").Append(EscAttr(df)).Append("\">");
                            sb.Append(Keycaps(c.Control, c.IsController));
                            sb.Append("<span class=\"kobs-plugin\">").Append(Esc(c.PluginA)).Append(" + ").Append(Esc(c.PluginB)).Append("</span>");
                            sb.Append("<span class=\"kobs-act\">").Append(c.Count).Append("×, last ").Append(Esc(c.LastSeen)).Append("</span>");
                            sb.Append("</div>\n");
                        }
                    }
                }
                sb.Append("  </details>\n");
            }

            // 4. Free keys — collapsed, rendered as keycaps.
            if (r.FreeKeys.Count > 0)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Unused keys <span class=\"count\">").Append(r.FreeKeys.Count)
                  .Append("</span> <span class=\"sub-note\">safe candidates for a new binding</span></summary>\n");
                sb.Append("    <div class=\"keycaps-grid\">\n");
                foreach (var k in r.FreeKeys)
                    sb.Append("      <span class=\"frow\" data-f=\"").Append(EscAttr(k.ToLowerInvariant())).Append("\">")
                      .Append(Keycaps(k, false)).Append("</span>\n");
                sb.Append("    </div>\n");
                sb.Append("    <p class=\"note\">Excluded regardless of use: F10 (Windows delivers it as a system key) and "
                        + "F12 (Steam screenshot), plus modifiers and game-reserved keys.</p>\n");
                sb.Append("  </details>\n");
            }

            // 4b. Free controller buttons — collapsed, rendered as pad keycaps.
            if (r.FreeControllerButtons.Count > 0)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Unused controller buttons <span class=\"count\">").Append(r.FreeControllerButtons.Count)
                  .Append("</span> <span class=\"sub-note\">safe candidates for a new binding</span></summary>\n");
                sb.Append("    <div class=\"keycaps-grid\">\n");
                foreach (var k in r.FreeControllerButtons)
                    sb.Append("      <span class=\"frow\" data-f=\"").Append(EscAttr(k.ToLowerInvariant())).Append("\">")
                      .Append(Keycaps(k, true)).Append("</span>\n");
                sb.Append("    </div>\n");
                sb.Append("    <p class=\"note\">Discrete gamepad buttons only. The sticks, the D-pad directions and the "
                        + "analog triggers arrive as composite paths the scanner cannot track, so they are neither counted "
                        + "as used nor offered here; Start and Select are excluded as the reserved menu pair. A button the "
                        + "game already binds drops off on its own, since the game's controller bindings are read live.</p>\n");
                sb.Append("  </details>\n");
            }

            // 5. Leftover configs — collapsed, with the safe-to-delete note.
            if (r.OrphanedConfigs.Count > 0)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Leftover configs <span class=\"count\">").Append(r.OrphanedConfigs.Count).Append("</span></summary>\n");
                sb.Append("    <p class=\"note\">These declare keybinds but no matching mod is loaded, so the bindings are "
                        + "inactive and safe to remove. Use <strong>Delete stale configs</strong> in the bar above — ATLAS "
                        + "deletes them on the next launch.</p>\n");
                sb.Append("    <ul class=\"plain\">\n");
                foreach (var o in r.OrphanedConfigs)
                    sb.Append("      <li class=\"frow\" data-f=\"").Append(EscAttr(o.ToLowerInvariant()))
                      .Append("\"><code>").Append(Esc(o)).Append(".cfg</code></li>\n");
                sb.Append("    </ul>\n");
                sb.Append("  </details>\n");
            }

            // 6. All bindings — collapsed, grouped by owner.
            sb.Append("  <details class=\"sub\">\n");
            sb.Append("    <summary>All bindings <span class=\"count\">").Append(r.Binds.Count).Append("</span></summary>\n");
            foreach (var g in r.Binds.GroupBy(b => b.Owner).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("    <div class=\"bind-group\">\n");
                sb.Append("      <div class=\"bind-owner\">").Append(Esc(g.Key)).Append("</div>\n");
                foreach (var b in g)
                {
                    var df = (b.Owner + " " + b.Label + " " + b.Control).ToLowerInvariant();
                    sb.Append("      <div class=\"bind frow\" data-f=\"").Append(EscAttr(df)).Append("\">");
                    sb.Append(Keycaps(b.Control, b.IsController));
                    sb.Append("<span class=\"bind-label\">").Append(Esc(b.Label)).Append("</span>");
                    sb.Append(SourceTag(b.Source));
                    sb.Append("</div>\n");
                }
                sb.Append("    </div>\n");
            }
            sb.Append("  </details>\n");

            sb.Append("</section>\n");
        }

        private static string SourceTag(BindSource s) => s switch
        {
            BindSource.ModConfigGuessed => " <span class=\"src-tag\">inferred from a text setting</span>",
            _ => "",
        };

        // ── Conflicts ───────────────────────────────────────────────────────────────────────

        private static void Conflicts(StringBuilder sb, ScanReport r)
        {
            sb.Append("<section class=\"card\" aria-label=\"Conflicts\">\n");
            sb.Append("  <h2 class=\"card-title\">Conflicts <span class=\"card-sub\">methods patched by 2+ mods</span></h2>\n");

            if (r.Conflicts.Count == 0)
            {
                sb.Append("  <p class=\"empty\">None. No method is patched by more than one mod.</p>\n");
                sb.Append("</section>\n");
                return;
            }

            var seen = r.Conflicts.Where(c => c.ObservedInLogs > 0).ToList();
            var high = r.Conflicts.Where(c => c.ObservedInLogs <= 0 && c.Severity == Severity.High).ToList();
            var med = r.Conflicts.Where(c => c.ObservedInLogs <= 0 && c.Severity == Severity.Medium).ToList();
            var low = r.Conflicts.Where(c => c.ObservedInLogs <= 0 && c.Severity <= Severity.Low).ToList();

            // Seen in error logs — expanded, at the top, visually separated. This has actually thrown.
            if (seen.Count > 0)
            {
                sb.Append("  <div class=\"seen-banner\">\n");
                sb.Append("    <div class=\"seen-head\">Seen in error logs <span class=\"count\">").Append(seen.Count)
                  .Append("</span> — these have actually thrown on this install</div>\n");
                foreach (var c in seen) ConflictRow(sb, c);
                sb.Append("  </div>\n");
            }

            // High — expanded.
            if (high.Count > 0)
            {
                sb.Append("  <details open class=\"sub\">\n");
                sb.Append("    <summary>High <span class=\"count\">").Append(high.Count)
                  .Append("</span> <span class=\"chip sev-high\">likely to break</span></summary>\n");
                foreach (var c in high) ConflictRow(sb, c);
                sb.Append("  </details>\n");
            }

            // Medium — collapsed into one expandable tier.
            if (med.Count > 0)
            {
                sb.Append("  <details class=\"sub\">\n");
                sb.Append("    <summary>Medium <span class=\"count\">").Append(med.Count)
                  .Append("</span> <span class=\"chip sev-medium\">order-dependent</span></summary>\n");
                foreach (var c in med) ConflictRow(sb, c);
                sb.Append("  </details>\n");
            }

            // Low — collapsed and quiet.
            if (low.Count > 0)
            {
                sb.Append("  <details class=\"sub quiet\">\n");
                sb.Append("    <summary>Low / informational <span class=\"count\">").Append(low.Count).Append("</span></summary>\n");
                foreach (var c in low) ConflictRow(sb, c);
                sb.Append("  </details>\n");
            }

            sb.Append("</section>\n");
        }

        private static void ConflictRow(StringBuilder sb, ConflictRecord c)
        {
            var attrib = new StringBuilder();
            for (int i = 0; i < c.Owners.Count; i++)
            {
                var o = c.Owners[i];
                if (i > 0) attrib.Append(' ');
                attrib.Append("<span class=\"owner\">").Append(Esc(o.DisplayName)).Append(' ')
                      .Append(Marks(o)).Append("</span>");
            }

            // ObservedInLogs state, kept explicitly tri-state: seen / not seen / not checked.
            string evidence = c.ObservedInLogs > 0
                ? "<span class=\"estate es-hit\">seen in logs</span>"
                : c.ObservedInLogs == 0
                    ? "<span class=\"estate es-ok\">not seen in logs</span>"
                    : "<span class=\"estate es-muted\">logs not checked</span>";

            var df = (c.Method + " " + string.Join(" ", c.Owners.Select(o => o.DisplayName)) + " " + c.Reason).ToLowerInvariant();

            // Execution order (0.12.0): whose patch runs first — usually what decides coexistence.
            string? note = null;
            if (c.Order.Count > 0)
            {
                note = "runs: " + string.Join(" → ", c.Order.Select(s => s.Owner + " (" + s.Kind + ")").ToArray());
                if (c.HasOrderingConstraints)
                    note += "  — before/after ordering declared, so the real order may differ from this priority sort";
            }
            FindingRow(sb, c.Severity, c.Method, evidence, c.Reason, attrib.ToString(), note, df);
        }

        // ── Dependencies ──────────────────────────────────────────────────────────────────────

        private static void Dependencies(StringBuilder sb, ScanReport r)
        {
            sb.Append("<section class=\"card\" aria-label=\"Dependencies\">\n");
            sb.Append("  <h2 class=\"card-title\">Missing dependencies</h2>\n");

            if (r.MissingDependencies.Count == 0 && r.DependencyVersionIssues.Count == 0)
            {
                sb.Append("  <p class=\"empty\">None. Every declared dependency is loaded and up to version.</p>\n");
                sb.Append("</section>\n");
                return;
            }

            var hard = r.MissingDependencies.Where(d => d.HardDependency).ToList();
            var soft = r.MissingDependencies.Where(d => !d.HardDependency).ToList();

            foreach (var d in hard)
            {
                var df = (d.DependentName + " " + d.MissingGuid).ToLowerInvariant();
                FindingRow(sb, Severity.High, d.DependentName,
                    "<span class=\"chip sev-high\">hard</span>",
                    $"Will not function until {d.MissingGuid} is installed.", "", null, df);
            }
            foreach (var d in soft)
            {
                var df = (d.DependentName + " " + d.MissingGuid).ToLowerInvariant();
                FindingRow(sb, Severity.Low, d.DependentName,
                    "<span class=\"chip sev-low\">soft</span>",
                    $"Optional integration with {d.MissingGuid} — a feature may quietly not appear, "
                    + "but the mod still loads.", "", null, df);
            }

            // Version satisfaction (0.12.0): a dep present but older than declared. Mostly the
            // unenforced soft-dep case; a hard version miss usually makes the dependent fail to load.
            foreach (var v in r.DependencyVersionIssues)
            {
                var df = (v.DependentName + " " + v.DepGuid + " version").ToLowerInvariant();
                var chip = v.HardDependency ? "<span class=\"chip sev-high\">hard · version</span>"
                                            : "<span class=\"chip sev-medium\">soft · version</span>";
                FindingRow(sb, v.HardDependency ? Severity.High : Severity.Medium, v.DependentName, chip,
                    $"Needs {v.DepGuid} ≥ {v.RequiredVersion}, but {v.InstalledVersion} is installed"
                    + (v.HardDependency ? "." : " — the optional integration may misbehave against the older version."),
                    "", null, df);
            }

            sb.Append("</section>\n");
        }

        // ── Plugins ─────────────────────────────────────────────────────────────────────────

        private static void Plugins(StringBuilder sb, ScanReport r)
        {
            sb.Append("<section class=\"card\" aria-label=\"Installed plugins\">\n");
            sb.Append("  <details class=\"sub\">\n");
            sb.Append("    <summary class=\"card-title-summary\">Installed plugins <span class=\"count\">")
              .Append(r.Plugins.Count).Append("</span></summary>\n");
            sb.Append("    <table class=\"plugins\">\n");
            sb.Append("      <thead><tr><th>Name</th><th>Version</th><th>GUID</th><th class=\"num\">Config</th></tr></thead>\n");
            sb.Append("      <tbody>\n");
            foreach (var p in r.Plugins)
            {
                var df = (p.Name + " " + p.Version + " " + p.Guid).ToLowerInvariant();
                sb.Append("        <tr class=\"frow\" data-f=\"").Append(EscAttr(df)).Append("\">");
                sb.Append("<td>").Append(Esc(p.Name)).Append("</td>");
                sb.Append("<td class=\"mono\">").Append(Esc(p.Version)).Append("</td>");
                sb.Append("<td class=\"mono guid\">").Append(Esc(p.Guid)).Append("</td>");
                sb.Append("<td class=\"num\">").Append(p.ConfigEntryCount).Append("</td></tr>\n");
            }
            sb.Append("      </tbody>\n    </table>\n");
            sb.Append("  </details>\n");
            sb.Append("</section>\n");
        }

        // ── Ignored tab ─────────────────────────────────────────────────────────────────────

        private static void Ignored(StringBuilder sb, ScanReport r)
        {
            sb.Append("<section class=\"card ignored-card\" aria-label=\"Ignored\">\n");
            sb.Append("  <details class=\"sub\" id=\"ignored-details\">\n");
            sb.Append("    <summary class=\"card-title-summary\">Ignored <span class=\"count\" id=\"ignored-count\">")
              .Append(r.IgnoredItems.Count)
              .Append("</span> <span class=\"sub-note\">set aside — not counted toward status</span></summary>\n");
            sb.Append("    <p class=\"note\">Items set aside as fine (an intentional placeholder bind, a deliberately "
                    + "shared key, a vetted conflict). They do not affect the verdict but stay here so the decision is "
                    + "visible. Add or remove exceptions in-game with the ATLAS panel (F3).</p>\n");
            sb.Append("    <div id=\"ignored-list\">\n");
            foreach (var it in r.IgnoredItems) IgnoredRow(sb, it);
            sb.Append("    </div>\n");
            sb.Append("    <p class=\"empty\"").Append(r.IgnoredItems.Count > 0 ? " hidden" : "")
              .Append(">Nothing set aside.</p>\n");
            sb.Append("  </details>\n");
            sb.Append("</section>\n");
        }

        private static void IgnoredRow(StringBuilder sb, IgnoredItem it)
        {
            var df = (it.Label + " " + it.Detail).ToLowerInvariant();
            sb.Append("      <div class=\"ign-row frow\" data-f=\"").Append(EscAttr(df)).Append("\">");
            sb.Append("<span class=\"ign-cat\">").Append(Esc(it.Category)).Append("</span>");
            sb.Append("<code class=\"member\">").Append(Esc(it.Label)).Append("</code>");
            if (it.Detail.Length > 0) sb.Append("<span class=\"ign-detail\">").Append(Esc(it.Detail)).Append("</span>");
            sb.Append("</div>\n");
        }

        // ── Shared, section-agnostic building blocks ────────────────────────────────────────

        /// <summary>
        /// The one finding-row shape used by conflicts, dependencies, and drift. Icon slot,
        /// monospace member, severity chip (+ any extra chip), prose reason, attribution line,
        /// optional muted note. Because every section calls this, moving a row's markup between
        /// sections renders it correctly with no CSS change (criterion 14).
        /// </summary>
        private static void FindingRow(StringBuilder sb, Severity sev, string member, string extraChip,
                                       string reason, string attribution, string? note, string dataF,
                                       string? actionHtml = null)
        {
            sb.Append("  <div class=\"finding ").Append(SevClass(sev)).Append(" frow\" data-f=\"")
              .Append(EscAttr(dataF)).Append("\">\n");
            sb.Append("    <span class=\"ficon\" aria-hidden=\"true\">").Append(SevIcon(sev)).Append("</span>\n");
            sb.Append("    <div class=\"finding-body\">\n");
            sb.Append("      <div class=\"finding-head\"><code class=\"member\">").Append(Esc(member)).Append("</code>")
              .Append("<span class=\"chip ").Append(SevClass(sev)).Append("\">").Append(SevLabel(sev)).Append("</span>");
            if (!string.IsNullOrEmpty(extraChip)) sb.Append(extraChip);
            // Optional right-aligned action (drift findings use it for the fix-brief export).
            if (!string.IsNullOrEmpty(actionHtml)) sb.Append(actionHtml);
            sb.Append("</div>\n");
            if (!string.IsNullOrEmpty(reason))
                sb.Append("      <p class=\"reason\">").Append(Esc(reason)).Append("</p>\n");
            if (!string.IsNullOrEmpty(attribution))
                sb.Append("      <p class=\"attrib\">").Append(attribution).Append("</p>\n");
            if (!string.IsNullOrEmpty(note))
                sb.Append("      <p class=\"row-note\">").Append(Esc(note)).Append("</p>\n");
            sb.Append("    </div>\n");
            sb.Append("  </div>\n");
        }

        private static string SevClass(Severity s) => s switch
        {
            Severity.High => "sev-high",
            Severity.Medium => "sev-medium",
            Severity.Low => "sev-low",
            _ => "sev-none",
        };

        private static string SevLabel(Severity s) => s switch
        {
            Severity.High => "High",
            Severity.Medium => "Medium",
            Severity.Low => "Low",
            _ => "None",
        };

        private static string SevIcon(Severity s) => s switch
        {
            Severity.High => "▲",     // ▲
            Severity.Medium => "◆",   // ◆
            Severity.Low => "●",      // ●
            _ => "○",                 // ○
        };

        /// <summary>Every key rendered as a physical keycap. The one motif genuinely of this subject.</summary>
        private static string Keycaps(string control, bool isController)
        {
            if (string.IsNullOrEmpty(control)) return "<span class=\"keycap keycap-empty\">—</span>";
            if (isController)
                return "<span class=\"keycap pad\">" + Esc(control) + "</span>";

            var sb = new StringBuilder();
            var tokens = control.Split('+');
            for (int i = 0; i < tokens.Length; i++)
            {
                if (i > 0) sb.Append("<span class=\"key-plus\">+</span>");
                sb.Append("<span class=\"keycap\">").Append(Esc(tokens[i].Trim())).Append("</span>");
            }
            return sb.ToString();
        }

        /// <summary>Patch-kind marks from an owner's counts. Prefix / postfix / transpiler / finalizer
        /// are four meaningfully different things; each gets a fixed, distinguishable mark.</summary>
        private static string Marks(PatchOwner o)
        {
            var sb = new StringBuilder();
            if (o.Prefixes > 0) Mark(sb, "pre", "prefix", o.Prefixes);
            if (o.Postfixes > 0) Mark(sb, "post", "postfix", o.Postfixes);
            if (o.Transpilers > 0) Mark(sb, "trans", "transpiler", o.Transpilers);
            if (o.Finalizers > 0) Mark(sb, "final", "finalizer", o.Finalizers);
            return sb.ToString();
        }

        /// <summary>Same marks, reconstructed from a drift finding's PatchKinds string
        /// ("transpiler x1, postfix x2") so drift rows reuse the exact conflict vocabulary.</summary>
        private static string MarksFromString(string kinds)
        {
            if (string.IsNullOrEmpty(kinds)) return "";
            var sb = new StringBuilder();
            foreach (var raw in kinds.Split(','))
            {
                var seg = raw.Trim();
                if (seg.Length == 0) continue;
                var sp = seg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var word = sp.Length > 0 ? sp[0].ToLowerInvariant() : "";
                var count = sp.Length > 1 ? sp[1].TrimStart('x', 'X') : "";
                var cls = word switch
                {
                    "prefix" => "pre",
                    "postfix" => "post",
                    "transpiler" => "trans",
                    "finalizer" => "final",
                    _ => "",
                };
                if (cls.Length == 0)
                {
                    sb.Append("<span class=\"pk pk-other\">").Append(Esc(seg)).Append("</span>");
                    continue;
                }
                int.TryParse(count, out var n);
                Mark(sb, cls, word, n > 0 ? n : 1);
            }
            return sb.ToString();
        }

        private static void Mark(StringBuilder sb, string cls, string label, int count)
        {
            sb.Append("<span class=\"pk pk-").Append(cls).Append("\">").Append(label)
              .Append("<span class=\"pk-n\">×").Append(count).Append("</span></span>");
        }

        // ── Small text helpers (ported from the text renderer where wording is canonical) ──────

        private static bool IsFresh(string state) => state == "NoBaseline";
        private static bool Unusable(string state) => state == "Unavailable" || state == "Unreadable";
        private static bool DriftRan(ScanReport r) => r.DriftChecked || r.DriftCodeState.Length > 0 || r.DriftContentState.Length > 0;

        private static string Short(string mvid)
            => string.IsNullOrEmpty(mvid) ? "?" : (mvid.Length > 8 ? mvid.Substring(0, 8) : mvid);

        private static string Ago(string capturedUtc)
        {
            if (string.IsNullOrEmpty(capturedUtc)) return "at an unknown time";
            if (DateTime.TryParse(capturedUtc, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            {
                var span = DateTime.UtcNow - t;
                if (span.TotalDays >= 1) return $"{(int)span.TotalDays} day(s) ago ({capturedUtc})";
                if (span.TotalHours >= 1) return $"{(int)span.TotalHours} hour(s) ago ({capturedUtc})";
                return $"{Math.Max(0, (int)span.TotalMinutes)} minute(s) ago ({capturedUtc})";
            }
            return "on " + capturedUtc;
        }

        private static string S(long n) => n == 1 ? "" : "s";
        private static string Dep(int n) => n == 1 ? "dependency" : "dependencies";
        private static string Patch(int n) => n == 1 ? "patch" : "patches";

        private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string JoinReasons(List<string> parts)
        {
            if (parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            if (parts.Count == 2) return parts[0] + " and " + parts[1];
            return string.Join(", ", parts.Take(parts.Count - 1).ToArray()) + ", and " + parts[parts.Count - 1];
        }

        // ── Escaping. Every third-party string is untrusted (§11). A mod named <script> must not
        //    execute; a mod named <img src=x onerror=...> must render as literal text. ─────────

        private static string Esc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // Attribute values (data-f) go through the same escape - &, <, >, " and ' are all covered,
        // so an injected value cannot close the attribute or the tag.
        private static string EscAttr(string? s) => Esc(s);

        // ── Inline assets. Palette reasoning is stated at the top of the CSS per §8. ───────────

        private const string Css = @"
/*  ATLAS HTML report — visual language.
    This is a diagnostic INSTRUMENT, not a document, so the page is a calm deep-slate panel
    (not pure black — pure black plus one accent is exactly the AI-design cliché §8 warns off)
    and status colour is the only thing that draws the eye. Hues are assigned by MEANING and
    reused everywhere, section-agnostic: red = high / problem, amber = medium / attention,
    green = clean / unchanged / ok, slate-blue = low / quiet, grey = none / not-checked.
    A separate VIOLET register is reserved exclusively for ‘the ground moved’ — a game update
    (drift Changed, GameBuildChanged) is categorically different from a mod fighting another
    mod, so it must never wear conflict-red. Two system font stacks only, no webfont fetch:
    a UI sans for prose, a monospace for members, control paths and GUIDs. Keycaps are the one
    bold motif — genuinely of this subject (physical keys) — and everything else stays quiet. */
:root {
  --bg: #10151d;
  --panel: #161e28;
  --panel-2: #1d2733;
  --panel-3: #243040;
  --edge: #2c3846;
  --edge-soft: #212c39;
  --ink: #e7edf4;
  --ink-dim: #a6b6c8;
  --ink-faint: #728296;
  --mono-ink: #d8e4f2;

  --high: #ff6f6f;   --high-bg: rgba(255,111,111,.13);  --high-edge: rgba(255,111,111,.42);
  --med:  #ffc061;   --med-bg:  rgba(255,192,97,.13);    --med-edge:  rgba(255,192,97,.42);
  --low:  #8fa6bd;   --low-bg:  rgba(143,166,189,.12);   --low-edge:  rgba(143,166,189,.34);
  --none: #66768a;   --none-bg: rgba(102,118,138,.12);   --none-edge: rgba(102,118,138,.30);
  --ok:   #55d6a0;   --ok-bg:   rgba(85,214,160,.12);    --ok-edge:   rgba(85,214,160,.40);
  --hit:  #ff9052;   --hit-bg:  rgba(255,144,82,.15);    --hit-edge:  rgba(255,144,82,.50);
  --ext:  #b98cff;   --ext-bg:  rgba(185,140,255,.14);   --ext-edge:  rgba(185,140,255,.46);
  --warn: #e2b45f;   --warn-bg: rgba(226,180,95,.12);    --warn-edge: rgba(226,180,95,.36);

  --pre:   #62b6ff;  --pre-bg:   rgba(98,182,255,.14);
  --post:  #55d6a0;  --post-bg:  rgba(85,214,160,.14);
  --transp:#ff6f6f;  --transp-bg:rgba(255,111,111,.14);
  --final: #b98cff;  --final-bg: rgba(185,140,255,.14);

  --accent: #5cc2ff;
  --focus: #7cd0ff;
  --keycap-shadow: rgba(0,0,0,.35);

  --r-s: 4px;  --r: 8px;  --r-l: 14px;
  --sans: system-ui, -apple-system, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;
  --mono: ui-monospace, ""Cascadia Code"", ""JetBrains Mono"", ""SF Mono"", Menlo, Consolas, ""Liberation Mono"", monospace;
}

* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body {
  margin: 0; background: var(--bg); color: var(--ink);
  font-family: var(--sans); font-size: 15px; line-height: 1.5;
  padding: 0 clamp(12px, 3vw, 40px) 64px;
  max-width: 1400px; margin-inline: auto;
}
code, .mono { font-family: var(--mono); }

/* Header */
.topbar { padding: 26px 2px 18px; border-bottom: 1px solid var(--edge); margin-bottom: 20px; }
.brand { font-size: 20px; font-weight: 700; letter-spacing: .06em; }
.brand span { color: var(--ink-faint); font-weight: 500; letter-spacing: .02em; }
.meta { color: var(--ink-dim); font-size: 13px; margin-top: 4px; }

/* Controls */
.controls {
  position: sticky; top: 0; z-index: 5; background: var(--bg);
  display: flex; gap: 10px; align-items: center; flex-wrap: wrap;
  padding: 12px 0; margin-bottom: 18px; border-bottom: 1px solid var(--edge-soft);
}
#filter {
  flex: 1 1 260px; min-width: 200px;
  background: var(--panel-2); color: var(--ink); border: 1px solid var(--edge);
  border-radius: var(--r); padding: 9px 12px; font: inherit;
}
#filter::placeholder { color: var(--ink-faint); }
.controls button {
  background: var(--panel-2); color: var(--ink-dim); border: 1px solid var(--edge);
  border-radius: var(--r); padding: 9px 14px; font: inherit; cursor: pointer;
}
.controls button:hover { color: var(--ink); border-color: var(--accent); }
.filter-status { color: var(--ink-faint); font-size: 13px; margin-left: auto; }

/* Verdict banner */
.verdict {
  border: 1px solid var(--edge); border-left-width: 6px; border-radius: var(--r-l);
  background: var(--panel); padding: 22px 24px; margin-bottom: 22px;
}
.v-head { display: flex; align-items: center; gap: 12px; }
.v-dot { width: 14px; height: 14px; border-radius: 50%; flex: none; }
.v-state { font-size: 22px; font-weight: 700; letter-spacing: .02em; }
.v-sentence { font-size: 17px; margin: 12px 0 0; color: var(--ink); max-width: 80ch; }
.verdict.v-clean { border-left-color: var(--ok); }
.verdict.v-clean .v-dot { background: var(--ok); box-shadow: 0 0 0 4px var(--ok-bg); }
.verdict.v-clean .v-state { color: var(--ok); }
.verdict.v-attention { border-left-color: var(--med); }
.verdict.v-attention .v-dot { background: var(--med); box-shadow: 0 0 0 4px var(--med-bg); }
.verdict.v-attention .v-state { color: var(--med); }
.verdict.v-problem { border-left-color: var(--high); }
.verdict.v-problem .v-dot { background: var(--high); box-shadow: 0 0 0 4px var(--high-bg); }
.verdict.v-problem .v-state { color: var(--high); }

.stats { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 20px; }
.stat {
  background: var(--panel-2); border: 1px solid var(--edge-soft); border-radius: var(--r);
  padding: 9px 14px; min-width: 92px; display: flex; flex-direction: column; gap: 2px;
}
.stat-wide { min-width: 200px; }
.stat-v { font-size: 17px; font-weight: 600; font-variant-numeric: tabular-nums; }
.stat-wide .stat-v { font-size: 13px; font-weight: 500; font-family: var(--mono); color: var(--ink-dim); }
.stat-l { font-size: 11px; text-transform: uppercase; letter-spacing: .06em; color: var(--ink-faint); }

/* Cards / sections */
.card {
  background: var(--panel); border: 1px solid var(--edge); border-radius: var(--r-l);
  padding: 18px 20px; margin-bottom: 18px;
}
.card-title { font-size: 16px; font-weight: 700; margin: 0 0 12px; letter-spacing: .02em; }
.card-sub, .card-title .card-sub { color: var(--ink-faint); font-weight: 500; font-size: 13px; margin-left: 8px; }
.note { color: var(--ink-dim); font-size: 13.5px; margin: 8px 0; max-width: 82ch; }
.note.strong { color: var(--ink); }
.empty { color: var(--ink-faint); font-style: italic; margin: 6px 0; }

/* details / summary */
details.sub { border-top: 1px solid var(--edge-soft); margin-top: 10px; padding-top: 6px; }
details.sub > summary, .quiet > summary {
  cursor: pointer; padding: 8px 4px; list-style: none;
  display: flex; align-items: center; gap: 10px; font-weight: 600;
}
summary::-webkit-details-marker { display: none; }
details > summary::before {
  content: ""\25b8""; color: var(--ink-faint); font-size: 12px;
  transition: transform .15s ease; display: inline-block;
}
details[open] > summary::before { transform: rotate(90deg); }
.count {
  background: var(--panel-3); color: var(--ink-dim); border-radius: 999px;
  padding: 1px 9px; font-size: 12px; font-variant-numeric: tabular-nums; font-weight: 600;
}
.sub-note, .card-sub { color: var(--ink-faint); font-weight: 500; font-size: 12.5px; }
.quiet > summary { color: var(--ink-dim); font-weight: 500; }
.plain { margin: 6px 0; padding-left: 22px; }
.plain li { margin: 3px 0; color: var(--ink-dim); }

/* Chips (severity — section-agnostic; a drift High and a conflict High are the same chip) */
.chip {
  display: inline-block; padding: 1px 9px; border-radius: 999px; font-size: 11.5px;
  font-weight: 700; text-transform: uppercase; letter-spacing: .05em;
  border: 1px solid transparent; white-space: nowrap;
}
.chip.sev-high { color: var(--high); background: var(--high-bg); border-color: var(--high-edge); }
.chip.sev-medium { color: var(--med); background: var(--med-bg); border-color: var(--med-edge); }
.chip.sev-low { color: var(--low); background: var(--low-bg); border-color: var(--low-edge); }
.chip.sev-none { color: var(--none); background: var(--none-bg); border-color: var(--none-edge); }
/* Live-status chips: Active reads urgent, Review muted-amber, Resolved the calm green register */
.chip.st-active   { color: var(--high); background: var(--high-bg); border-color: var(--high-edge); }
.chip.st-review   { color: var(--med);  background: var(--med-bg);  border-color: var(--med-edge); }
.chip.st-resolved { color: var(--ok);   background: var(--ok-bg);   border-color: var(--ok-edge); }
.drift-resolved { margin: 4px 0 14px; }
.drift-resolved .finding { opacity: .82; }

/* Evidence state (the three-way §5 treatment; drift surfaces reuse and extend it) */
.estate {
  display: inline-block; padding: 1px 9px; border-radius: var(--r-s); font-size: 12px;
  font-weight: 600; border: 1px solid transparent; white-space: nowrap;
}
.estate.es-idle  { color: var(--ink-dim); background: var(--none-bg); border-color: var(--none-edge); }
.estate.es-ok    { color: var(--ok);   background: var(--ok-bg);   border-color: var(--ok-edge); }
.estate.es-hit   { color: var(--hit);  background: var(--hit-bg);  border-color: var(--hit-edge); }
.estate.es-ext   { color: var(--ext);  background: var(--ext-bg);  border-color: var(--ext-edge); }
.estate.es-warn  { color: var(--warn); background: var(--warn-bg); border-color: var(--warn-edge); }
.estate.es-muted { color: var(--ink-faint); background: var(--edge-soft); border-color: var(--edge); }

/* Finding row — the one shape shared by conflicts, dependencies and drift findings */
.finding {
  display: flex; gap: 12px; align-items: flex-start;
  padding: 12px 12px 12px 14px; border-radius: var(--r); margin: 8px 0;
  background: var(--panel-2); border: 1px solid var(--edge-soft); border-left: 3px solid var(--none);
}
.finding.sev-high { border-left-color: var(--high); }
.finding.sev-medium { border-left-color: var(--med); }
.finding.sev-low { border-left-color: var(--low); }
.finding.sev-none { border-left-color: var(--none); }
.ficon { font-size: 13px; line-height: 1.6; flex: none; }
.finding.sev-high .ficon { color: var(--high); }
.finding.sev-medium .ficon { color: var(--med); }
.finding.sev-low .ficon { color: var(--low); }
.finding.sev-none .ficon { color: var(--none); }
.finding-body { min-width: 0; flex: 1; }
.finding-head { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.member {
  color: var(--mono-ink); font-size: 13.5px; word-break: break-word;
  background: var(--bg); padding: 1px 6px; border-radius: var(--r-s); border: 1px solid var(--edge-soft);
}
.reason { margin: 7px 0 0; color: var(--ink); font-size: 14px; max-width: 84ch; }
.attrib { margin: 7px 0 0; font-size: 13px; color: var(--ink-dim); display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: center; }
.owner, .owners { display: inline-flex; gap: 6px; align-items: center; flex-wrap: wrap; }
.row-note { margin: 6px 0 0; font-size: 12.5px; color: var(--warn); font-style: italic; }

/* Fix-brief export — right-aligned group in a drift finding's head: a label + two format buttons */
.noisy-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.noisy-row .noisy-label { min-width: 0; }
.brief-export { margin-left: auto; flex: none; display: inline-flex; align-items: center; gap: 6px; }
.brief-lbl { color: var(--ink-faint); font-size: 11px; text-transform: uppercase; letter-spacing: .05em; }
.brief-btn {
  background: var(--bg); color: var(--ink-dim); border: 1px solid var(--edge);
  border-radius: var(--r-s); padding: 3px 9px; font-size: 12px; font-weight: 600;
  cursor: pointer; white-space: nowrap; transition: color .12s, border-color .12s, background .12s;
}
.brief-btn:hover { color: var(--ink); border-color: var(--accent); }
.brief-btn.flash { color: var(--accent); border-color: var(--accent); }

.overlap { align-items: center; }

/* Support bundle bar — under the header, above the controls */
.bundle-bar {
  max-width: 1100px; margin: 12px auto 4px; padding: 12px 16px;
  background: var(--panel-2); border: 1px solid var(--accent); border-radius: var(--r);
}
.bundle-bar .bb-main { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; }
.bundle-bar .bb-dl {
  background: var(--accent); color: var(--bg); font-weight: 700; text-decoration: none;
  border-radius: var(--r-s); padding: 8px 16px; font-size: 14px; white-space: nowrap;
}
.bundle-bar .bb-dl:hover { filter: brightness(1.08); }
.bundle-bar .bb-facts { color: var(--ink-dim); font-size: 13px; font-variant-numeric: tabular-nums; }
.bundle-bar .bb-frame { margin: 9px 0 0; color: var(--ink-faint); font-size: 12.5px; max-width: 84ch; }

/* Decisions bar under the controls */
.decisions-bar {
  display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
  max-width: 1100px; margin: 0 auto 4px; padding: 10px 16px;
  background: var(--ok-bg); border: 1px solid var(--ok-edge); border-radius: var(--r);
}
.decisions-bar .db-msg { color: var(--ink); font-size: 13.5px; }
.decisions-bar .db-actions { margin-left: auto; display: flex; gap: 8px; }
.decisions-bar button {
  background: var(--panel-2); color: var(--ink); border: 1px solid var(--accent);
  border-radius: var(--r-s); padding: 6px 14px; font: inherit; font-size: 13px; cursor: pointer;
}
.decisions-bar button:hover { background: var(--accent); color: var(--bg); }
.decisions-bar button.db-danger { border-color: var(--high-edge); color: var(--high); }
.decisions-bar button.db-danger:hover { background: var(--high); color: var(--bg); border-color: var(--high); }

/* Ignored tab rows */
.ignored-card .ign-row {
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
  padding: 7px 4px; border-bottom: 1px solid var(--edge-soft);
}
.ign-cat {
  font-size: 11px; text-transform: uppercase; letter-spacing: .05em; color: var(--ink-faint);
  border: 1px solid var(--edge); border-radius: 999px; padding: 1px 8px; flex: none;
}
.ign-detail { color: var(--ink-dim); font-size: 12.5px; }

/* Patch-kind marks (prefix / postfix / transpiler / finalizer — four distinct marks) */
.pk {
  display: inline-flex; align-items: center; gap: 3px; font-family: var(--mono);
  font-size: 11px; font-weight: 600; padding: 1px 7px; border-radius: var(--r-s);
  border: 1px solid transparent;
}
.pk-n { opacity: .7; font-size: 10px; }
.pk-pre   { color: var(--pre);    background: var(--pre-bg);    border-color: var(--pre); }
.pk-post  { color: var(--post);   background: var(--post-bg);   border-color: var(--post); }
.pk-trans { color: var(--transp); background: var(--transp-bg); border-color: var(--transp); }
.pk-final { color: var(--final);  background: var(--final-bg);  border-color: var(--final); }
.pk-other { color: var(--ink-dim); background: var(--edge-soft); border-color: var(--edge); }

/* Keycaps — the signature element. Kept exclusive to keys. */
.keycap {
  display: inline-block; font-family: var(--mono); font-size: 12px; font-weight: 600;
  color: var(--ink); background: linear-gradient(180deg, var(--panel-3), var(--panel-2));
  border: 1px solid var(--edge); border-bottom-width: 3px; border-radius: 6px;
  padding: 3px 8px; min-width: 22px; text-align: center; line-height: 1.2;
  box-shadow: 0 1px 0 var(--keycap-shadow);
}
.keycap.pad {
  border-radius: 999px; color: var(--ok);
  border-color: var(--ok-edge); background: var(--ok-bg);
}
.keycap-empty { color: var(--ink-faint); border-bottom-width: 1px; }
.key-plus { color: var(--ink-faint); margin: 0 3px; font-size: 12px; }
.keycaps-grid { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0; }

/* Update Impact (drift) */
.drift .surfaces { display: flex; flex-wrap: wrap; gap: 10px 22px; margin: 4px 0 12px; }
.surface { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.surface-label { color: var(--ink-dim); font-size: 13px; }
.surface-detail { color: var(--ink-faint); font-size: 12.5px; }
.ext-banner {
  color: var(--ext); background: var(--ext-bg); border: 1px solid var(--ext-edge);
  border-radius: var(--r); padding: 10px 14px; margin: 10px 0; font-size: 14px;
}
.ext-banner strong { color: var(--ext); }
.drift-stats { color: var(--ink-dim); font-size: 13px; margin: 6px 0 12px; font-variant-numeric: tabular-nums; }
.drift-clean { color: var(--ink); font-size: 14px; margin: 8px 0; }
.drift-count { margin: 8px 0; }
.accept {
  margin: 12px 0 4px; padding: 12px 14px; border: 1px dashed var(--edge);
  border-radius: var(--r); background: var(--panel-2);
}
.accept p { margin: 4px 0; font-size: 13px; color: var(--ink-dim); }
.accept code { color: var(--accent); }
details.limits, details.quiet { border-top: 1px solid var(--edge-soft); margin-top: 12px; padding-top: 6px; }
details.limits > summary, details.quiet > summary {
  cursor: pointer; padding: 8px 4px; font-weight: 500; color: var(--ink-dim);
  display: flex; align-items: center; gap: 10px; list-style: none;
}
.caveat { color: var(--ink-dim); font-style: italic; margin: 8px 0 4px; font-size: 13px; }

/* Seen-in-logs banner (conflicts that have actually thrown) */
.seen-banner {
  border: 1px solid var(--hit-edge); background: var(--hit-bg);
  border-radius: var(--r); padding: 8px 12px 12px; margin: 4px 0 14px;
}
.seen-head { color: var(--hit); font-weight: 700; font-size: 14px; padding: 4px 2px 8px; display: flex; gap: 10px; align-items: center; }
.seen-banner .finding { background: var(--panel); }

/* Overlaps */
.overlap { padding: 10px 12px; border: 1px solid var(--edge-soft); border-radius: var(--r); margin: 8px 0; background: var(--panel-2); }
.overlap-key { margin-bottom: 8px; }
.overlap-claims { margin: 0; padding-left: 0; list-style: none; }
.overlap-claims li { padding: 3px 0; color: var(--ink-dim); font-size: 13.5px; display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; }
.claim-label { color: var(--ink); }
.claim-val { font-family: var(--mono); font-size: 12.5px; color: var(--mono-ink); }
.src-tag { color: var(--ink-faint); font-size: 12px; font-style: italic; }

/* Learned-at-runtime rows */
.kobs { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; padding: 7px 4px; border-bottom: 1px solid var(--edge-soft); }
.kobs-new { }
.kobs-plugin { font-weight: 600; }
.kobs-act { color: var(--ink-faint); font-size: 12.5px; }

/* All-bindings groups */
.bind-group { margin: 8px 0; }
.bind-owner { color: var(--ink-faint); font-size: 12px; text-transform: uppercase; letter-spacing: .06em; margin: 8px 0 4px; }
.bind { display: flex; align-items: center; gap: 10px; padding: 5px 4px; }
.bind-label { color: var(--ink-dim); font-size: 13.5px; }

/* Plugins table */
.plugins { width: 100%; border-collapse: collapse; margin-top: 8px; font-size: 13.5px; }
.plugins th { text-align: left; color: var(--ink-faint); font-weight: 600; font-size: 11.5px; text-transform: uppercase; letter-spacing: .05em; padding: 6px 10px; border-bottom: 1px solid var(--edge); }
.plugins td { padding: 6px 10px; border-bottom: 1px solid var(--edge-soft); color: var(--ink-dim); }
.plugins td.mono { font-family: var(--mono); font-size: 12.5px; color: var(--mono-ink); }
.plugins .guid { color: var(--ink-faint); }
.plugins .num { text-align: right; font-variant-numeric: tabular-nums; }
.card-title-summary { font-size: 16px; font-weight: 700; letter-spacing: .02em; }

/* Footer */
.foot { color: var(--ink-faint); font-size: 12.5px; margin-top: 22px; padding-top: 14px; border-top: 1px solid var(--edge-soft); max-width: 84ch; }
.foot code { color: var(--ink-dim); }

/* Filter hiding */
.f-hide { display: none !important; }

/* Focus — visible on every interactive element */
a:focus-visible, button:focus-visible, summary:focus-visible, input:focus-visible {
  outline: 2px solid var(--focus); outline-offset: 2px; border-radius: var(--r-s);
}
summary:focus { outline: none; }

/* Reduced motion */
@media (prefers-reduced-motion: reduce) {
  details > summary::before { transition: none; }
  * { scroll-behavior: auto !important; }
}

/* Wide screens: keep line length sane rather than stretching prose to 3840px */
@media (min-width: 1600px) { body { font-size: 15.5px; } }

/* Print — a sane bug-report PDF: expand everything, drop chrome, go light */
@media print {
  :root { --bg: #fff; --panel: #fff; --panel-2: #fff; --panel-3: #f0f0f0;
          --edge: #bbb; --edge-soft: #ddd; --ink: #111; --ink-dim: #333; --ink-faint: #666; --mono-ink: #111; }
  body { max-width: none; padding: 0; }
  .controls { display: none; }
  details > *:not(summary) { display: revert !important; }
  details > summary::before { content: ""\25be""; }
  .finding, .card, .verdict, .overlap { break-inside: avoid; }
  .keycap { box-shadow: none; }
}
";

        private const string Script = @"
(function () {
  'use strict';
  var filter = document.getElementById('filter');
  var status = document.getElementById('filter-status');
  var expandAll = document.getElementById('expand-all');
  var collapseAll = document.getElementById('collapse-all');
  var rows = Array.prototype.slice.call(document.querySelectorAll('.frow'));
  var details = Array.prototype.slice.call(document.querySelectorAll('details'));
  var savedOpen = null;   // remembers open-state so clearing the filter restores it

  function applyFilter() {
    var q = (filter.value || '').trim().toLowerCase();
    if (q === '') {
      for (var i = 0; i < rows.length; i++) rows[i].classList.remove('f-hide');
      if (savedOpen) {
        for (var d = 0; d < details.length; d++) details[d].open = savedOpen[d];
        savedOpen = null;
      }
      status.textContent = '';
      return;
    }
    if (!savedOpen) {
      savedOpen = [];
      for (var s = 0; s < details.length; s++) savedOpen[s] = details[s].open;
    }
    for (var o = 0; o < details.length; o++) details[o].open = true;  // reveal matches in collapsed blocks
    var hidden = 0;
    for (var r = 0; r < rows.length; r++) {
      var hay = rows[r].getAttribute('data-f');
      if (hay === null) hay = rows[r].textContent.toLowerCase();
      if (hay.indexOf(q) === -1) { rows[r].classList.add('f-hide'); hidden++; }
      else { rows[r].classList.remove('f-hide'); }
    }
    status.textContent = hidden === 0
      ? 'all rows match'
      : ('hiding ' + hidden + ' row' + (hidden === 1 ? '' : 's'));
  }

  if (filter) filter.addEventListener('input', applyFilter);
  if (expandAll) expandAll.addEventListener('click', function () {
    for (var i = 0; i < details.length; i++) details[i].open = true;
    savedOpen = null;
  });
  if (collapseAll) collapseAll.addEventListener('click', function () {
    for (var i = 0; i < details.length; i++) details[i].open = false;
    savedOpen = null;
  });

  // ── Fix-brief export ──────────────────────────────────────────────────────────
  // Each drift finding carries both renderings of its brief as base64 payloads, so a
  // download runs with no network and no server round-trip - it works from file://.
  // HTML opens in any browser; .txt opens fuss-free in any editor and is also copied to
  // the clipboard, ready to paste into an AI coder.
  function b64ToText(b64) {
    var bin = atob(b64);
    var bytes = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return (typeof TextDecoder !== 'undefined')
      ? new TextDecoder('utf-8').decode(bytes)
      : decodeURIComponent(escape(bin));
  }
  function flash(btn, msg) {
    if (!btn.getAttribute('data-label')) btn.setAttribute('data-label', btn.textContent);
    btn.textContent = msg;
    btn.classList.add('flash');
    setTimeout(function () {
      btn.textContent = btn.getAttribute('data-label');
      btn.classList.remove('flash');
    }, 1800);
  }
  function download(text, name, mime) {
    var blob = new Blob([text], { type: mime + ';charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url; a.download = name;
    document.body.appendChild(a); a.click(); document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 4000);
  }
  function exportBrief() {
    var btn = this;
    var box = btn.parentNode;              // the .brief-export group holds both payloads
    var fmt = btn.getAttribute('data-fmt');
    var isHtml = fmt === 'html';
    var base = box.getAttribute('data-file') || 'ATLAS_fix_brief';

    var text;
    try { text = b64ToText(box.getAttribute(isHtml ? 'data-html' : 'data-txt')); }
    catch (e) { flash(btn, 'Failed'); return; }

    try { download(text, base + (isHtml ? '.html' : '.txt'), isHtml ? 'text/html' : 'text/plain'); }
    catch (e2) { flash(btn, 'Failed'); return; }

    // The .txt is the paste-into-a-model rendering, so that button also loads the clipboard.
    // HTML is for reading, so it just saves.
    if (!isHtml && navigator.clipboard && navigator.clipboard.writeText) {
      try {
        navigator.clipboard.writeText(text).then(
          function () { flash(btn, 'Copied + saved'); },
          function () { flash(btn, 'Saved'); });
        return;
      } catch (e3) { /* fall through to the plain confirmation */ }
    }
    flash(btn, 'Saved');
  }
  var briefBtns = Array.prototype.slice.call(document.querySelectorAll('.brief-btn'));
  for (var eb = 0; eb < briefBtns.length; eb++) briefBtns[eb].addEventListener('click', exportBrief);

  // Cleanup (delete a stale config, reset exceptions) lives in the in-game ATLAS panel, which has
  // real file access. A static report opened from disk cannot write into the game folder, so the
  // old Save-As/download buttons only ever produced a decisions.tsv to place by hand — removed in
  // 0.12.1. Stale configs can also just be deleted from BepInEx/config directly.
})();
";
    }
}
