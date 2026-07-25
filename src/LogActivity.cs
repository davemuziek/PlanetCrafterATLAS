using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// Summarises the archived session logs (0.14.0): groups every Error/Warning/Fatal event by a
    /// stable signature across the kept logs, so the standing problems (consistently firing, seen in
    /// two or more sessions) stand apart from the flukes (situational, one session), and tallies which
    /// log source (BepInEx logger tag) is noisiest. Read-only; diagnostic context only - never the verdict.
    ///
    /// Session granularity by design: BepInEx log lines are not individually timestamped, so "recurs"
    /// and first/last-seen are by session log (dated from the filename), not per-line wall-clock.
    /// </summary>
    internal static class LogActivity
    {
        private const int TopEvents = 25;    // cap each on-page list so a huge archive can't produce a wall
        private const int TopNoisy = 12;
        private const int CatalogCap = 300;  // cap a source's exported catalog (defensive against bloat)

        private sealed class Acc
        {
            public string Level = "", Source = "", ExceptionType = "", Frame = "", Label = "", ExampleFrame = "";
            public int Count;
            public readonly HashSet<string> Sessions = new HashSet<string>(StringComparer.Ordinal);
            public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;
        }

        public static LogActivitySummary Analyze(string archiveDir)
        {
            var s = new LogActivitySummary();
            if (string.IsNullOrEmpty(archiveDir) || !Directory.Exists(archiveDir)) return s;

            string[] files;
            try { files = Directory.GetFiles(archiveDir, "*.log"); }
            catch { return s; }

            var groups = new Dictionary<string, Acc>(StringComparer.Ordinal);

            foreach (var path in files)
            {
                var file = Path.GetFileName(path);
                var when = SessionTime(file, path);
                s.LogsScanned++;

                IEnumerable<string> lines;
                try { lines = File.ReadLines(path); }
                catch { continue; }

                try
                {
                    foreach (var (level, source, text) in ErrorClassifier.Blocks(lines))
                    {
                        if (level != "Error" && level != "Fatal" && level != "Warning") continue;
                        if (ErrorClassifier.IsIgnored(text)) continue;

                        s.TotalEvents++;

                        var sig = ErrorClassifier.Classify(text, "archive");
                        var excType = sig.ExceptionType ?? "";
                        var frame = sig.Frame ?? "";
                        var src = source.Length > 0 ? source : "(unknown)";
                        // Source is part of the signature: the same message text from two different
                        // loggers is two different events (e.g. Unity's warning vs a mod's own).
                        var key = src + '\u0001' + level + "|" + excType + "|" + (frame.Length > 0 ? frame : NormalizedLine(text));

                        if (!groups.TryGetValue(key, out var a))
                        {
                            a = new Acc
                            {
                                Level = level.ToUpperInvariant(),
                                Source = src,
                                ExceptionType = excType,
                                Frame = frame,
                                Label = BuildLabel(excType, frame, text),
                                ExampleFrame = ErrorClassifier.BestFrameType(text) ?? "",
                            };
                            groups[key] = a;
                        }
                        a.Count++;
                        a.Sessions.Add(file);
                        if (when < a.First) a.First = when;
                        if (when > a.Last) a.Last = when;
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("ATLAS log activity: failed reading " + file + ": " + ex.Message); }
            }

            // ── finalize ──
            // One LogEventGroup per signature, reused for both the on-page lists and the per-source
            // catalogs, so a source's exported catalog is exactly the events attributed to it.
            var byAcc = new Dictionary<Acc, LogEventGroup>();
            foreach (var a in groups.Values) byAcc[a] = ToGroup(a);

            var consistent = new List<LogEventGroup>();
            var situational = new List<LogEventGroup>();
            foreach (var g in byAcc.Values) (g.SessionCount >= 2 ? consistent : situational).Add(g);
            consistent.Sort((x, y) =>
            {
                var sc = y.SessionCount.CompareTo(x.SessionCount);
                return sc != 0 ? sc : y.Count.CompareTo(x.Count);
            });
            situational.Sort((x, y) => y.Count.CompareTo(x.Count));

            s.ConsistentTotal = consistent.Count;
            s.SituationalTotal = situational.Count;
            AddCapped(s.Consistent, consistent, TopEvents);
            AddCapped(s.Situational, situational, TopEvents);

            // Noisiest sources, by the BepInEx logger tag every event carries — comprehensive, unlike
            // the earlier frame-based attribution which missed the frameless engine warnings that
            // dominate the volume. Session count is the union across a source's signatures; each carries
            // its full (capped) signature list for the catalog export.
            var srcAccs = new Dictionary<string, List<Acc>>(StringComparer.Ordinal);
            foreach (var a in groups.Values)
            {
                if (!srcAccs.TryGetValue(a.Source, out var l)) { l = new List<Acc>(); srcAccs[a.Source] = l; }
                l.Add(a);
            }

            var noisy = new List<NoisySource>();
            foreach (var kv in srcAccs)
            {
                var nn = new NoisySource { Source = kv.Key };
                var sessions = new HashSet<string>(StringComparer.Ordinal);
                var evs = new List<LogEventGroup>(kv.Value.Count);
                foreach (var a in kv.Value) { nn.Count += a.Count; sessions.UnionWith(a.Sessions); evs.Add(byAcc[a]); }
                nn.SessionCount = sessions.Count;
                evs.Sort((x, y) => y.Count.CompareTo(x.Count));
                nn.EventTotal = evs.Count;
                for (int i = 0; i < evs.Count && i < CatalogCap; i++) nn.Events.Add(evs[i]);
                noisy.Add(nn);
            }
            noisy.Sort((x, y) => y.Count.CompareTo(x.Count));
            s.NoisyTotal = noisy.Count;
            for (int i = 0; i < noisy.Count && i < TopNoisy; i++) s.Noisy.Add(noisy[i]);

            s.Analyzed = true;
            return s;
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────

        private static void AddCapped(List<LogEventGroup> dst, List<LogEventGroup> src, int cap)
        {
            for (int i = 0; i < src.Count && i < cap; i++) dst.Add(src[i]);
        }

        private static LogEventGroup ToGroup(Acc a)
        {
            return new LogEventGroup
            {
                Level = a.Level,
                ExceptionType = a.ExceptionType,
                Frame = a.Frame,
                Label = a.Label,
                Source = a.Source,
                Count = a.Count,
                SessionCount = a.Sessions.Count,
                FirstSeen = a.First == DateTime.MaxValue ? "" : a.First.ToString("yyyy-MM-dd HH:mm"),
                LastSeen = a.Last == DateTime.MinValue ? "" : a.Last.ToString("yyyy-MM-dd HH:mm"),
                ExampleFrame = a.ExampleFrame,
            };
        }

        private static string BuildLabel(string excType, string frame, string text)
        {
            if (excType.Length > 0 && frame.Length > 0) return excType + " @ " + frame;
            if (excType.Length > 0) return excType;
            if (frame.Length > 0) return frame;
            return NormalizedLine(text);
        }

        /// <summary>First meaningful line of a block, minus the "[Level : Source]" prefix, with digit
        /// runs collapsed to '#' so counters/ids don't split one recurring warning into many.</summary>
        private static string NormalizedLine(string block)
        {
            var nl = block.IndexOf('\n');
            var line = (nl < 0 ? block : block.Substring(0, nl)).Trim();
            var close = line.IndexOf(']');
            if (close >= 0 && close + 1 < line.Length) line = line.Substring(close + 1).Trim();

            var sb = new StringBuilder(line.Length);
            var inDigits = false;
            foreach (var c in line)
            {
                if (char.IsDigit(c)) { if (!inDigits) { sb.Append('#'); inDigits = true; } }
                else { sb.Append(c); inDigits = false; }
            }
            var norm = sb.ToString();
            return norm.Length > 100 ? norm.Substring(0, 100) + "…" : norm;
        }

        /// <summary>The session start time, from the "yyyy-MM-dd_HHmm_TAG_…" filename; falls back to the
        /// file's last-write time when the name doesn't parse.</summary>
        private static DateTime SessionTime(string file, string path)
        {
            var parts = file.Split('_');
            if (parts.Length >= 2 && DateTime.TryParseExact(
                    parts[0] + "_" + parts[1], "yyyy-MM-dd_HHmm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return t;
            try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
        }
    }
}
