using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// One scan's entry in the homepage ledger: enough to list it in the sidebar and to diff it
    /// against another scan, without re-reading the (self-contained, un-fetchable from file://) scan
    /// html. Keys are stable, human-readable strings per category so the Changes view reads directly.
    /// </summary>
    internal sealed class LedgerEntry
    {
        public string File = "";        // "ModScan_2026-07-24_193000.html"
        public string TimeUtc = "";     // "2026-07-24T19:30:00Z"
        public string TimeLocal = "";   // "2026-07-24 19:30:00"
        public string Verdict = "";     // "CLEAN" / "ATTENTION" / "PROBLEM"
        public int H, M, L;
        public readonly Dictionary<string, List<string>> Keys =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The on-disk ledger behind the scans homepage (0.13.0). Row-typed TSV at
    /// <c>Scans/scan-index.tsv</c>, following the observed_keys / drift-baseline precedent: no
    /// serializer dependency, greppable, appended per scan and pruned in lockstep with the html ring.
    /// A file:// page cannot fetch sibling files, so the index embeds this ledger inline instead of
    /// reading the scan htmls at view time.
    /// </summary>
    internal static class ScanLedger
    {
        public const string Magic = "atlas-scan-index";
        public const string FormatVersion = "v1";
        private const string FileName = "scan-index.tsv";

        // The diff categories, in display order. Kept here so the ledger and the renderer agree.
        public static readonly string[] Categories =
            { "conflict", "compat", "drift", "patch", "load", "dep", "overlap" };

        // ── build one entry from a report ──────────────────────────────────────────────

        public static LedgerEntry BuildEntry(
            ScanReport r, string htmlFileName, string verdict, DateTime nowUtc, DateTime nowLocal)
        {
            var e = new LedgerEntry
            {
                File = htmlFileName,
                TimeUtc = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                TimeLocal = nowLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                Verdict = verdict,
                H = r.HighCount,
                M = r.MediumCount,
                L = r.LowCount,
            };

            foreach (var c in r.Conflicts) Add(e, "conflict", c.Method);
            foreach (var f in r.CompatFindings) Add(e, "compat", f.Member);
            foreach (var f in r.DriftFindings)
                if (f.Kind != DriftKind.NotTracked && f.Status != DriftStatus.Resolved)
                    Add(e, "drift", f.Member);
            foreach (var f in r.PatchApplyFindings) Add(e, "patch", f.Member);
            foreach (var lf in r.PluginLoadFailures) Add(e, "load", lf.Plugin);
            foreach (var d in r.MissingDependencies) Add(e, "dep", d.DependentName + " → " + d.MissingGuid);
            foreach (var v in r.DependencyVersionIssues) Add(e, "dep", v.DependentName + " → " + v.DepGuid + " (version)");
            foreach (var o in r.BindOverlaps) Add(e, "overlap", (o.IsController ? "[PAD] " : "[KEY] ") + o.Control);

            return e;
        }

        private static void Add(LedgerEntry e, string category, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!e.Keys.TryGetValue(category, out var list)) { list = new List<string>(); e.Keys[category] = list; }
            if (!list.Contains(value)) list.Add(value);
        }

        // ── append + prune ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Appends <paramref name="entry"/>, then keeps only the newest <paramref name="keep"/> scans
        /// whose html still exists, so the ledger never lists a scan the html ring has pruned. Returns
        /// the surviving entries in chronological (append) order.
        /// </summary>
        public static List<LedgerEntry> Append(string dir, LedgerEntry entry, int keep)
        {
            var entries = Read(dir);
            entries.Add(entry);

            // Drop entries whose html is gone (pruned by the html ring, or deleted by hand).
            var live = new List<LedgerEntry>(entries.Count);
            foreach (var e in entries)
            {
                try { if (File.Exists(Path.Combine(dir, e.File))) live.Add(e); }
                catch { live.Add(e); }   // can't stat: keep it rather than silently drop
            }

            // Keep the newest `keep` (append order is chronological, so trim from the front).
            if (keep > 0 && live.Count > keep) live.RemoveRange(0, live.Count - keep);

            Write(dir, live);
            return live;
        }

        // ── read ───────────────────────────────────────────────────────────────────────

        public static List<LedgerEntry> Read(string dir)
        {
            var entries = new List<LedgerEntry>();
            var path = Path.Combine(dir, FileName);
            string[] lines;
            try
            {
                if (!File.Exists(path)) return entries;
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS scan index: read failed: " + ex.Message); return entries; }

            LedgerEntry? cur = null;
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (f[0] == "S")
                {
                    if (f.Length < 8) { cur = null; continue; }
                    cur = new LedgerEntry
                    {
                        File = f[1], TimeUtc = f[2], TimeLocal = f[3], Verdict = f[4],
                        H = ParseInt(f[5]), M = ParseInt(f[6]), L = ParseInt(f[7]),
                    };
                    entries.Add(cur);
                }
                else if (f[0] == "K" && cur != null && f.Length >= 4)
                {
                    // A key row belongs to the entry whose file it names (defensive: match cur).
                    if (f[1] == cur.File) Add(cur, f[2], f[3]);
                }
            }
            return entries;
        }

        // ── write ──────────────────────────────────────────────────────────────────────

        public static void Write(string dir, List<LedgerEntry> entries)
        {
            try
            {
                var sb = new StringBuilder(8 * 1024);
                sb.Append("# ").Append(Magic).Append("  ").Append(FormatVersion).Append('\n');
                foreach (var e in entries)
                {
                    sb.Append("S\t").Append(San(e.File)).Append('\t').Append(San(e.TimeUtc)).Append('\t')
                      .Append(San(e.TimeLocal)).Append('\t').Append(San(e.Verdict)).Append('\t')
                      .Append(e.H).Append('\t').Append(e.M).Append('\t').Append(e.L).Append('\n');
                    foreach (var cat in Categories)
                    {
                        if (!e.Keys.TryGetValue(cat, out var list)) continue;
                        foreach (var v in list)
                            sb.Append("K\t").Append(San(e.File)).Append('\t').Append(San(cat)).Append('\t')
                              .Append(San(v)).Append('\n');
                    }
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, FileName), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS scan index: write failed: " + ex.Message); }
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────

        private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;

        private static string San(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
