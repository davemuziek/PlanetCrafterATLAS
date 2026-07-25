using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;

namespace ATLAS
{
    /// <summary>
    /// User decisions about what to set aside or clean up, persisted in a hand-editable
    /// <c>BepInEx/ATLAS/decisions.tsv</c>. The report is a static file and cannot touch the disk,
    /// so it is only the UI: its buttons record decisions and Export writes this file; ATLAS is the
    /// actuator that reads it back and honours them.
    ///
    /// Two verbs:
    ///  - <c>ignore &lt;key&gt;</c> - a conflict, keybind overlap, or malformed bind the user has
    ///    judged fine (an intentional controller placeholder, a deliberately shared key). It moves to
    ///    the Ignored tab and stops weighing in on the verdict. Persistent.
    ///  - <c>delete-config &lt;guid&gt;</c> - an abandoned config file to remove. Processed once at
    ///    startup, then the line is dropped. This is the one place ATLAS deletes a user file, and it
    ///    only ever touches a <c>.cfg</c> whose owning mod is not installed.
    /// </summary>
    internal static class Decisions
    {
        public static string PathIn(string bepInExRoot) =>
            Path.Combine(bepInExRoot, "ATLAS", "decisions.tsv");

        // ── read ─────────────────────────────────────────────────────────────────────────

        public static DecisionSet Load(string path)
        {
            var d = new DecisionSet();
            try
            {
                if (!File.Exists(path)) return d;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    var tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    var action = line.Substring(0, tab).Trim();
                    var key = line.Substring(tab + 1).Trim();
                    if (key.Length == 0) continue;

                    if (action == "ignore")
                    {
                        if (d.Ignored.Add(key)) d.IgnoreLines.Add("ignore\t" + key);
                    }
                    else if (action == "delete-config")
                    {
                        d.DeleteConfigs.Add(key);
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS decisions: read failed: " + ex.Message); }
            return d;
        }

        // ── act: delete abandoned configs (startup only) ─────────────────────────────────

        /// <summary>
        /// Deletes every config queued with <c>delete-config</c>, logs each, and rewrites the file
        /// keeping only the ignore lines - so a deletion happens exactly once. Runs at Awake, before
        /// any read-only scan, so the deletions never happen inside the scanner's read-only pass.
        /// </summary>
        public static void ProcessDeletions(string path, string configDir, ManualLogSource log)
        {
            DecisionSet d;
            try
            {
                if (!File.Exists(path)) return;
                d = Load(path);
            }
            catch { return; }

            // Nothing queued and nothing to clean up: leave the file untouched (no churn).
            if (d.DeleteConfigs.Count == 0 && d.IgnoreLines.Count > 0) return;

            var deleted = 0;
            foreach (var guid in d.DeleteConfigs)
            {
                try
                {
                    var cfg = Path.Combine(configDir, guid + ".cfg");
                    if (File.Exists(cfg))
                    {
                        File.Delete(cfg);
                        deleted++;
                        log.LogInfo($"ATLAS: deleted abandoned config {guid}.cfg (queued in decisions.tsv).");
                    }
                    else
                    {
                        log.LogInfo($"ATLAS: abandoned config {guid}.cfg already gone; clearing its delete request.");
                    }
                }
                catch (Exception ex) { log.LogWarning($"ATLAS: could not delete {guid}.cfg: {ex.Message}"); }
            }
            if (deleted > 0) log.LogInfo($"ATLAS: removed {deleted} abandoned config file(s).");

            try
            {
                if (d.IgnoreLines.Count == 0)
                {
                    // Reset (or a file with only spent delete requests): no exceptions left to keep,
                    // so remove decisions.tsv entirely rather than leaving an empty husk behind.
                    File.Delete(path);
                    log.LogInfo("ATLAS: decisions.tsv has no exceptions left; removed it.");
                }
                else
                {
                    // Keep the ignore lines, drop the spent delete-config lines.
                    var sb = new StringBuilder(1024);
                    sb.Append(Header);
                    foreach (var l in d.IgnoreLines) sb.Append(l).Append('\n');
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { log.LogWarning("ATLAS decisions: rewrite/cleanup failed: " + ex.Message); }
        }

        // ── write (used by the in-game panel to persist a decision instantly) ────────────

        /// <summary>Writes the whole set: header, the ignore lines, then the delete-config lines.</summary>
        public static void Write(string path, DecisionSet d)
        {
            try
            {
                var sb = new StringBuilder(1024);
                sb.Append(Header);
                foreach (var k in d.Ignored) sb.Append("ignore\t").Append(k).Append('\n');
                foreach (var g in d.DeleteConfigs) sb.Append("delete-config\t").Append(g).Append('\n');
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS decisions: write failed: " + ex.Message); }
        }

        /// <summary>Removes decisions.tsv entirely (the panel's Reset).</summary>
        public static void Clear(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS decisions: clear failed: " + ex.Message); }
        }

        // ── act: partition ignored items out of the report ───────────────────────────────

        /// <summary>
        /// Moves every ignored conflict / overlap / malformed bind out of its active list and into
        /// <see cref="ScanReport.IgnoredItems"/>, BEFORE any count or verdict is computed - which is
        /// what makes an ignored item stop weighing in on status while staying visible. Also carries
        /// the existing ignore lines onto the report so the HTML export can merge them with new clicks.
        /// </summary>
        public static void Partition(ScanReport report, DecisionSet d)
        {
            report.ExistingDecisions.AddRange(d.IgnoreLines);
            if (d.Ignored.Count == 0) return;

            for (int i = report.Conflicts.Count - 1; i >= 0; i--)
            {
                var c = report.Conflicts[i];
                var key = ConflictKey(c.Method);
                if (!d.Ignored.Contains(key)) continue;
                report.IgnoredItems.Add(new IgnoredItem
                {
                    Category = "conflict", Label = c.Method, Detail = c.Reason, Key = key,
                });
                report.Conflicts.RemoveAt(i);
            }

            for (int i = report.BindOverlaps.Count - 1; i >= 0; i--)
            {
                var o = report.BindOverlaps[i];
                var key = OverlapKey(o.IsController, o.Control);
                if (!d.Ignored.Contains(key)) continue;
                report.IgnoredItems.Add(new IgnoredItem
                {
                    Category = "overlap",
                    Label = (o.IsController ? "[PAD] " : "[KEY] ") + o.Control,
                    Detail = string.Join("  ·  ", o.Binds.Select(b => b.Owner + ": " + b.Label).ToArray()),
                    Key = key,
                });
                report.BindOverlaps.RemoveAt(i);
            }

            for (int i = report.MalformedBinds.Count - 1; i >= 0; i--)
            {
                var s = report.MalformedBinds[i];
                var key = MalformedKey(s);
                if (!d.Ignored.Contains(key)) continue;
                report.IgnoredItems.Add(new IgnoredItem { Category = "malformed", Label = s, Detail = "", Key = key });
                report.MalformedBinds.RemoveAt(i);
            }
        }

        // ── stable keys (shared with the renderer, which stamps them on each row) ─────────

        public static string ConflictKey(string method) => "conflict|" + method;

        public static string OverlapKey(bool isController, string control) =>
            "overlap|" + (isController ? "pad" : "key") + "|" + control;

        /// <summary>
        /// A malformed bind is displayed as "Owner / section.key = value"; the identity is the part
        /// before " = ", so ignoring it survives the placeholder's value being edited later.
        /// </summary>
        public static string MalformedKey(string malformed)
        {
            var id = malformed;
            var at = malformed.IndexOf(" = ", StringComparison.Ordinal);
            if (at >= 0) id = malformed.Substring(0, at).Trim();
            return "malformed|" + id;
        }

        public const string Header =
            "# ATLAS decisions. Edit by hand or export from the HTML report, then drop into\n"
            + "# BepInEx/ATLAS/decisions.tsv. 'ignore <key>' sets an item aside (it stops counting\n"
            + "# toward status but stays in the Ignored tab). 'delete-config <guid>' deletes an\n"
            + "# abandoned config on next launch, then clears itself.\n";
    }

    internal sealed class DecisionSet
    {
        public readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> DeleteConfigs = new List<string>();
        public readonly List<string> IgnoreLines = new List<string>();   // raw "ignore\t<key>", for re-export
    }
}
