using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ATLAS
{
    /// <summary>Which sessions' logs and scans a bundle carries. Default <see cref="Last3"/> — the
    /// full history is the strength, but a 40 MB zip never gets hosted and GitHub caps attachments at
    /// 25 MB, so the honest middle is the default and the panel can override to <see cref="All"/>.</summary>
    internal enum BundleScope { Session, Last3, All }

    /// <summary>
    /// Everything <see cref="SupportBundle.Build"/> needs, snapshotted on the main thread so the build
    /// itself touches no Unity or game API and can run on a background thread. The version / mvid /
    /// BepInEx facts are captured by the caller; the paths are static BepInEx roots.
    /// </summary>
    internal sealed class BundleOptions
    {
        public BundleScope Scope = BundleScope.Last3;
        public bool Redact = true;
        public bool IncludeModConfigs = true;
        public string ExtraRedactions = "";

        public string AtlasVersion = Plugin.Ver;
        public string BepInExVersion = "";
        public string Mvid = "";
        public string ConfigDir = "";        // BepInEx/config
        public string DecisionsPath = "";    // BepInEx/ATLAS/decisions.tsv
    }

    /// <summary>
    /// The support bundle (0.15.0): one scrubbed, self-describing <c>ATLAS_support_bundle.zip</c> a user
    /// can hand to a mod author (or attach to a GitHub issue) carrying the whole diagnostic history, not
    /// a pasted fragment. Fixed filename, single copy, overwritten each scan — there is no value in stale
    /// bundles and every reason not to grow the folder.
    ///
    /// Read-mostly and no-network: it reuses the scan htmls, text reports, archived logs and configs
    /// already on disk, routing every text file through <see cref="Redactor"/> on the way in. Built with
    /// <see cref="ZipArchive"/> over a <see cref="FileStream"/> (not <c>ZipFile.CreateFromDirectory</c>,
    /// which lives in a separate assembly and cannot scrub per entry) into a temp file that is then moved
    /// into place, so a failed build never leaves a half-written zip where the report links to it.
    /// </summary>
    internal static class SupportBundle
    {
        public const string FileName = "ATLAS_support_bundle.zip";
        public const int SchemaVersion = 1;

        // GitHub's per-attachment ceiling. Surfaced in the facts line / panel when the built zip exceeds it.
        public const long GitHubAttachmentLimit = 25L * 1024 * 1024;

        /// <summary>The outcome of one build, read by the renderers (facts line) and the panel.</summary>
        internal sealed class BundleInfo
        {
            public bool Built;
            public string Path = "";
            public long Bytes;
            public int Sessions;
            public int Scans;
            public int Logs;
            public bool Redacted;
            public string Scope = "";
            public string BuiltLocal = "";   // "14:22", for the facts line
            public string Error = "";

            public bool OverGitHubLimit => Bytes > GitHubAttachmentLimit;
        }

        public static BundleInfo Build(string scanDir, string archiveDir, ScanReport report, BundleOptions opt)
        {
            var info = new BundleInfo
            {
                Redacted = opt.Redact,
                Scope = opt.Scope.ToString(),
                Path = string.IsNullOrEmpty(scanDir) ? "" : System.IO.Path.Combine(scanDir, FileName),
            };

            try
            {
                if (string.IsNullOrEmpty(scanDir)) throw new InvalidOperationException("no scan directory");
                Directory.CreateDirectory(scanDir);

                // The scrubber applied to every text file entering the zip. A no-op when redaction is off.
                Func<string, string> scrub;
                if (opt.Redact) scrub = s => Redactor.Scrub(s, opt.ExtraRedactions);
                else scrub = s => s;

                // ── scope selection ──────────────────────────────────────────────────────────
                // Session / Last3 / All map to 1 / 3 / all most-recent sessions. Logs are one file
                // per session; scans run up to twice per session (a load scan and an end scan), so the
                // scan ring is selected at double the session count. manifest, READ_ME, Config and
                // decisions are always included in full regardless of scope.
                int sessions = opt.Scope == BundleScope.Session ? 1
                             : opt.Scope == BundleScope.Last3 ? 3
                             : int.MaxValue;
                var logs = NewestFiles(archiveDir, "*.log", sessions);
                var scanHtmls = NewestFiles(scanDir, "ModScan_*.html",
                                            sessions == int.MaxValue ? int.MaxValue : sessions * 2);

                info.Logs = logs.Count;
                info.Sessions = sessions == int.MaxValue ? logs.Count : Math.Min(sessions, logs.Count);
                info.Scans = scanHtmls.Count;

                var htmlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in scanHtmls) htmlNames.Add(System.IO.Path.GetFileName(h));

                // The homepage, re-rendered from the ledger filtered to just the scans in this bundle,
                // so its sidebar and iframes only reference files that are actually present. Rendered
                // WITHOUT a bundle block — the reader is already holding the bundle. Empty when there
                // are no scans in scope.
                string? indexHtml = null;
                if (scanHtmls.Count > 0)
                {
                    var ledger = ScanLedger.Read(scanDir);
                    var kept = ledger.FindAll(e => htmlNames.Contains(e.File));
                    if (kept.Count > 0) indexHtml = IndexRenderer.Render(kept, false, null);
                }

                var verdict = HtmlReportRenderer.VerdictState(report);
                var generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

                // Config (other mods' .cfg — small, high triage value) and decisions.tsv, both always-in-full.
                var configFiles = opt.IncludeModConfigs ? SafeGetFiles(opt.ConfigDir, "*.cfg") : new List<string>();
                bool haveDecisions = HasContent(opt.DecisionsPath);

                // The manifest describes the whole bundle including its own file list, so it is built
                // from the selections above before anything is written.
                var files = new List<(string path, string kind)> { ("READ_ME_FIRST.html", "readme"), ("manifest.json", "manifest") };
                if (indexHtml != null) files.Add(("Scans/index.html", "scan-index"));
                foreach (var h in scanHtmls) files.Add(("Scans/" + System.IO.Path.GetFileName(h), "scan-html"));
                foreach (var h in scanHtmls)
                {
                    var txt = System.IO.Path.ChangeExtension(h, ".txt");
                    if (File.Exists(txt)) files.Add(("Scans/" + System.IO.Path.GetFileName(txt), "scan-text"));
                }
                foreach (var l in logs) files.Add(("Logs/" + System.IO.Path.GetFileName(l), "log"));
                foreach (var c in configFiles) files.Add(("Config/" + System.IO.Path.GetFileName(c), "config"));
                if (haveDecisions) files.Add(("decisions.tsv", "decisions"));

                var manifest = BuildManifest(report, opt, verdict, generatedUtc, files);
                var readme = ReadMeRenderer.Write(report, opt, verdict, generatedUtc);

                // ── write to a temp, then move into place ────────────────────────────────────
                var tmp = System.IO.Path.Combine(scanDir, FileName + ".building");
                TryDelete(tmp);

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddText(zip, "READ_ME_FIRST.html", readme, scrub);
                    AddText(zip, "manifest.json", manifest, scrub);
                    if (indexHtml != null) AddText(zip, "Scans/index.html", indexHtml, scrub);

                    foreach (var h in scanHtmls)
                    {
                        AddFile(zip, "Scans/" + System.IO.Path.GetFileName(h), h, scrub);
                        var txt = System.IO.Path.ChangeExtension(h, ".txt");
                        if (File.Exists(txt)) AddFile(zip, "Scans/" + System.IO.Path.GetFileName(txt), txt, scrub);
                    }
                    foreach (var l in logs) AddFile(zip, "Logs/" + System.IO.Path.GetFileName(l), l, scrub);
                    foreach (var c in configFiles) AddFile(zip, "Config/" + System.IO.Path.GetFileName(c), c, scrub);
                    if (haveDecisions) AddFile(zip, "decisions.tsv", opt.DecisionsPath, scrub);
                }

                ReplaceInto(tmp, info.Path);

                info.Bytes = new FileInfo(info.Path).Length;
                info.BuiltLocal = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                info.Built = true;
            }
            catch (Exception ex)
            {
                // A bundle failure logs a warning and never takes the scan down. The previous zip, if
                // any, is left untouched because the new one is only moved into place on success.
                info.Built = false;
                info.Error = ex.Message;
                Plugin.Log.LogWarning("ATLAS support bundle failed (scan unaffected): " + ex.Message);
            }

            return info;
        }

        /// <summary>
        /// The one-line facts summary the report and homepage state beside the download link (a static
        /// page cannot measure at click time, but it can state what was true at build time), and the
        /// panel echoes. Plain text; the caller escapes it for HTML.
        /// </summary>
        public static string FactsLine(BundleInfo b)
        {
            var sb = new StringBuilder();
            sb.Append(HumanSize(b.Bytes));
            sb.Append(" · ").Append(b.Sessions).Append(b.Sessions == 1 ? " session" : " sessions");
            sb.Append(" · ").Append(b.Scans).Append(b.Scans == 1 ? " scan" : " scans");
            sb.Append(b.Redacted ? " · redacted" : " · not redacted");
            if (b.BuiltLocal.Length > 0) sb.Append(" · built ").Append(b.BuiltLocal);
            if (b.Scope.Length > 0) sb.Append(" · scope ").Append(b.Scope.ToLowerInvariant());
            if (b.OverGitHubLimit) sb.Append(" · over GitHub's 25 MB limit — narrow the scope");
            return sb.ToString();
        }

        /// <summary>Human-readable byte size for the facts line and the panel.</summary>
        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        // ── manifest.json ─────────────────────────────────────────────────────────────────────
        // Hand-written (no serializer — same reasoning as the ledger TSV and drift baseline), every
        // string escaped. `schema` is the contract, bumped only on a breaking shape change; additive
        // fields do not bump it. The shape is published as BUNDLE_FORMAT.md so anyone can write a parser.

        private static string BuildManifest(ScanReport r, BundleOptions opt, string verdict,
                                            string generatedUtc, List<(string path, string kind)> files)
        {
            var sb = new StringBuilder(4 * 1024);
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(SchemaVersion).Append(",\n");
            sb.Append("  \"atlasVersion\": ").Append(J(opt.AtlasVersion)).Append(",\n");
            sb.Append("  \"generatedUtc\": ").Append(J(generatedUtc)).Append(",\n");
            sb.Append("  \"scope\": ").Append(J(opt.Scope.ToString())).Append(",\n");
            sb.Append("  \"redacted\": ").Append(opt.Redact ? "true" : "false").Append(",\n");
            sb.Append("  \"game\": { \"version\": ").Append(J(r.GameVersion))
              .Append(", \"assemblyMvid\": ").Append(J(opt.Mvid)).Append(" },\n");
            sb.Append("  \"bepinex\": ").Append(J(opt.BepInExVersion)).Append(",\n");
            sb.Append("  \"unity\": ").Append(J(r.UnityVersion)).Append(",\n");
            sb.Append("  \"verdict\": ").Append(J(verdict.ToLowerInvariant())).Append(",\n");
            sb.Append("  \"counts\": { \"high\": ").Append(r.HighCount)
              .Append(", \"medium\": ").Append(r.MediumCount)
              .Append(", \"low\": ").Append(r.LowCount).Append(" },\n");
            sb.Append("  \"axes\": { \"drift\": ").Append(J(r.DriftCodeState.Length > 0 ? r.DriftCodeState : "n/a"))
              .Append(", \"compatChecked\": ").Append(r.CompatChecked ? "true" : "false")
              .Append(", \"patchCheckRan\": ").Append(r.PatchCheckRan ? "true" : "false")
              .Append(", \"coverageChecked\": ").Append(r.CoverageChecked ? "true" : "false").Append(" },\n");

            // mods
            sb.Append("  \"mods\": [");
            for (int i = 0; i < r.Plugins.Count; i++)
            {
                var p = r.Plugins[i];
                if (i > 0) sb.Append(',');
                sb.Append("\n    { \"guid\": ").Append(J(p.Guid))
                  .Append(", \"name\": ").Append(J(p.Name))
                  .Append(", \"version\": ").Append(J(p.Version))
                  .Append(", \"coverage\": ").Append(J(CoverageOf(r, p.Name))).Append(" }");
            }
            sb.Append(r.Plugins.Count > 0 ? "\n  ],\n" : "],\n");

            // findings — the notable High/Medium items across axes, enough for a parser to triage.
            var findings = CollectFindings(r);
            sb.Append("  \"findings\": [");
            for (int i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                if (i > 0) sb.Append(',');
                sb.Append("\n    { \"axis\": ").Append(J(f.axis))
                  .Append(", \"severity\": ").Append(J(f.severity))
                  .Append(", \"owner\": ").Append(J(f.owner))
                  .Append(", \"member\": ").Append(J(f.member)).Append(" }");
            }
            sb.Append(findings.Count > 0 ? "\n  ],\n" : "],\n");

            // files
            sb.Append("  \"files\": [");
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("\n    { \"path\": ").Append(J(files[i].path))
                  .Append(", \"kind\": ").Append(J(files[i].kind)).Append(" }");
            }
            sb.Append(files.Count > 0 ? "\n  ]\n" : "]\n");

            sb.Append("}\n");
            return sb.ToString();
        }

        internal readonly struct FindingRow
        {
            public readonly string axis, severity, owner, member;
            public FindingRow(string axis, string severity, string owner, string member)
            { this.axis = axis; this.severity = severity; this.owner = owner; this.member = member; }
        }

        /// <summary>The manifest's finding list, also reused by the README headline: compat, open drift,
        /// patch-apply and load failures, and High/Medium conflicts. Ordered worst-axis first.</summary>
        internal static List<FindingRow> CollectFindings(ScanReport r)
        {
            var list = new List<FindingRow>();

            foreach (var lf in r.PluginLoadFailures)
                list.Add(new FindingRow("load", "High", lf.Plugin, "failed to load"));

            foreach (var f in r.CompatFindings)
                list.Add(new FindingRow("compat", SevName(f.Severity), Owner(f.Owners), f.Member));

            foreach (var f in r.PatchApplyFindings)
                if (f.LogCorroborated)
                    list.Add(new FindingRow("patch", SevName(f.Severity), Owner(f.Owners), f.Member));

            foreach (var f in r.DriftFindings)
                if (f.Kind != DriftKind.NotTracked && f.Status != DriftStatus.Resolved)
                    list.Add(new FindingRow("drift", SevName(f.Severity), Owner(f.Owners), f.Member));

            foreach (var c in r.Conflicts)
                if (c.Severity >= Severity.Medium)
                    list.Add(new FindingRow("conflict", SevName(c.Severity),
                        c.Owners.Count > 0 ? c.Owners[0].DisplayName : "", c.Method));

            return list;
        }

        private static string CoverageOf(ScanReport r, string modName)
        {
            if (!r.CoverageChecked) return "unknown";
            foreach (var c in r.ModCoverages)
                if (string.Equals(c.Mod, modName, StringComparison.OrdinalIgnoreCase))
                    return c.FullyVisible ? "full" : "partial";
            return "unknown";
        }

        private static string Owner(List<string> owners) => owners != null && owners.Count > 0 ? owners[0] : "";

        private static string SevName(Severity s) => s switch
        {
            Severity.High => "High",
            Severity.Medium => "Medium",
            Severity.Low => "Low",
            _ => "None",
        };

        // ── zip / file helpers ──────────────────────────────────────────────────────────────────

        private static void AddText(ZipArchive zip, string entryPath, string content, Func<string, string> scrub)
        {
            var entry = zip.CreateEntry(entryPath.Replace('\\', '/'), CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(scrub(content ?? ""));
        }

        private static void AddFile(ZipArchive zip, string entryPath, string sourcePath, Func<string, string> scrub)
        {
            string content;
            try { content = File.ReadAllText(sourcePath); }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS bundle: skipped " + System.IO.Path.GetFileName(sourcePath) + " — " + ex.Message); return; }
            AddText(zip, entryPath, content, scrub);
        }

        /// <summary>The newest <paramref name="keep"/> files matching <paramref name="pattern"/>, newest
        /// first. Returns an empty list rather than throwing when the directory is missing.</summary>
        private static List<string> NewestFiles(string dir, string pattern, int keep)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(dir)) return result;
            try
            {
                var files = Directory.GetFiles(dir, pattern);
                Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                for (int i = 0; i < files.Length && (keep == int.MaxValue || i < keep); i++)
                    result.Add(files[i]);
            }
            catch { }
            return result;
        }

        private static List<string> SafeGetFiles(string dir, string pattern)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return new List<string>(Directory.GetFiles(dir, pattern)); }
            catch { }
            return new List<string>();
        }

        private static bool HasContent(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }

        private static void ReplaceInto(string tmp, string finalPath)
        {
            try
            {
                if (File.Exists(finalPath))
                {
                    // File.Replace preserves nothing we need and can fail across odd filesystems; a
                    // delete-then-move is simpler and the window is a few milliseconds on the same dir.
                    File.Delete(finalPath);
                }
                File.Move(tmp, finalPath);
            }
            catch
            {
                // Last resort: copy over, then drop the temp. Never leave the temp lying around.
                try { File.Copy(tmp, finalPath, true); } finally { TryDelete(tmp); }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>JSON string literal, every control char and quote escaped.</summary>
        private static string J(string? s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
