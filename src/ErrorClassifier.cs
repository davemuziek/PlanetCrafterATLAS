using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;

namespace ATLAS
{
    internal sealed class ErrorSignature
    {
        public string? ExceptionType;   // e.g. "NullReferenceException"
        public string? Frame;           // e.g. "BlueprintManager.Update"
        public string? Source;          // e.g. "Unity Log"

        /// <summary>
        /// Higher wins. frameTier * 2 + (exception type present ? 1 : 0), where frameTier is
        /// 0 none / 1 framework-only / 2 non-framework / 3 explicitly interesting.
        /// The first error in a session is almost never the most informative one, so a later
        /// error with a better signature replaces an earlier weak one.
        /// </summary>
        public int Quality;
    }

    internal static class ErrorClassifier
    {
        // "System.NullReferenceException", "ArgumentOutOfRangeException", ...
        private static readonly Regex TypeRx =
            new Regex(@"\b(?:[A-Za-z_]\w*\.)*([A-Za-z_]\w*Exception)\b", RegexOptions.Compiled);

        // Handles BOTH stack formats:
        //   Unity logMessageReceived:  "ARCHITECT.BlueprintManager.Update () (at <hash>:0)"
        //   Mono Exception.ToString(): "  at SpaceCraft.Foo.Bar (int x) [0x00000] in <hash>:0"
        // The Unity form has no leading "at" - that word lives inside the trailing
        // parenthetical location instead. Assuming otherwise cost us every frame name.
        private static readonly Regex FrameRx =
            new Regex(@"(?:^|\n)[ \t]*(?:at[ \t]+)?([\w.<>+`\[\]]+)\.([\w<>`\[\]]+)[ \t]*\(",
                      RegexOptions.Compiled);

        // A BepInEx log line prefix. A line without one is a continuation of the block above.
        private static readonly Regex LevelRx =
            new Regex(@"^\[(Fatal|Error|Warning|Message|Info|Debug)\s*:", RegexOptions.Compiled);

        // The full header, capturing the source tag too: "[Warning:   Unity] ..." -> level, "Unity".
        // The source is the BepInEx logger name (a mod's name, "Unity" for engine/game Debug.Log,
        // "BepInEx", ...) - far more reliably present than a stack frame, so it drives attribution.
        private static readonly Regex HeaderRx =
            new Regex(@"^\[(Fatal|Error|Warning|Message|Info|Debug)\s*:\s*([^\]]*)\]", RegexOptions.Compiled);

        private static string[]? _interesting;
        private static string[]? _framework;
        private static string[]? _ignored;

        private static string[] Interesting => _interesting ??= Split(Plugin.CfgInterestingNamespaces, ',');
        private static string[] Framework => _framework ??= Split(Plugin.CfgFrameworkNamespaces, ',');
        private static string[] Ignored => _ignored ??= Split(Plugin.CfgIgnoreErrorsContaining, '|');

        private static string[] Split(ConfigEntry<string>? entry, char separator)
        {
            var raw = entry?.Value ?? string.Empty;
            var parts = raw.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) kept.Add(t);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// True for errors that are noise on every launch and must not tag or name a log.
        /// BepInEx's "Unable to start Unity log writer" is the motivating case: it is the
        /// first line of every session on Unity 6 titles, so without this every log is ERR.
        /// </summary>
        public static bool IsIgnored(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return true;
            foreach (var pattern in Ignored)
                if (raw.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static ErrorSignature Classify(string raw, string source)
        {
            var sig = new ErrorSignature { Source = source };
            if (string.IsNullOrEmpty(raw)) return sig;

            var tm = TypeRx.Match(raw);
            if (tm.Success) sig.ExceptionType = tm.Groups[1].Value;

            sig.Frame = PickFrame(raw, out int tier);
            sig.Quality = tier * 2 + (sig.ExceptionType != null ? 1 : 0);
            return sig;
        }

        /// <summary>
        /// Prefers a frame from a namespace we care about. Failing that, the first frame that
        /// is not framework noise - the top of a Unity stack is usually something like
        /// UnityEngine.Bindings.ThrowHelper.ThrowNullReferenceException, which is a real frame
        /// and a useless name.
        /// </summary>
        private static string? PickFrame(string raw, out int tier)
        {
            string? firstNonFramework = null;
            string? firstAny = null;

            foreach (Match m in FrameRx.Matches(raw))
            {
                var type = m.Groups[1].Value;
                var label = ShortName(type) + "." + m.Groups[2].Value;

                firstAny ??= label;

                if (StartsWithAny(type, Interesting))
                {
                    tier = 3;
                    return label;
                }
                if (firstNonFramework == null && !StartsWithAny(type, Framework))
                    firstNonFramework = label;
            }

            if (firstNonFramework != null) { tier = 2; return firstNonFramework; }
            if (firstAny != null) { tier = 1; return firstAny; }

            tier = 0;
            return null;
        }

        private static bool StartsWithAny(string value, string[] prefixes)
        {
            foreach (var p in prefixes)
                if (value.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ShortName(string type)
        {
            var dot = type.LastIndexOf('.');
            return dot >= 0 ? type.Substring(dot + 1) : type;
        }

        /// <summary>
        /// Groups already-written log lines into error blocks and returns the best signature.
        /// Shared by the seed pass and by crash recovery so the two cannot drift apart -
        /// they did, and that is why seeded sessions produced worse names than rolled ones.
        /// </summary>
        public static ErrorSignature? ScanLines(IEnumerable<string> lines, out int errorCount)
        {
            ErrorSignature? best = null;
            var count = 0;
            var block = new StringBuilder();
            var inError = false;

            foreach (var line in lines)
            {
                var header = LevelRx.Match(line);
                if (header.Success)
                {
                    if (inError) Consider(block.ToString(), ref best, ref count);
                    block.Length = 0;

                    var level = header.Groups[1].Value;
                    inError = level == "Error" || level == "Fatal";
                    if (inError) block.AppendLine(line);
                }
                else if (inError)
                {
                    block.AppendLine(line);
                }
            }
            if (inError) Consider(block.ToString(), ref best, ref count);

            errorCount = count;
            return best;
        }

        public static ErrorSignature? ScanFile(string path, out int errorCount)
            => ScanLines(File.ReadLines(path), out errorCount);

        private static void Consider(string text, ref ErrorSignature? best, ref int count)
        {
            if (text.Length == 0 || IsIgnored(text)) return;

            count++;
            var sig = Classify(text, "archive");
            if (best == null || sig.Quality > best.Quality) best = sig;
        }

        /// <summary>
        /// Every BepInEx log block as (level, source, text), split on the level-prefixed header lines -
        /// a headerless line continues the block above it. Where <see cref="ScanLines"/> returns only
        /// the single best block, this yields them all, so the log-activity summary can group every
        /// event. Levels are the raw header words (Fatal / Error / Warning / Message / Info / Debug);
        /// source is the BepInEx logger name. The caller filters to the levels it cares about.
        /// </summary>
        public static IEnumerable<(string Level, string Source, string Text)> Blocks(IEnumerable<string> lines)
        {
            var block = new StringBuilder();
            var level = "";
            var source = "";
            foreach (var line in lines)
            {
                var header = HeaderRx.Match(line);
                if (header.Success)
                {
                    if (level.Length > 0 && block.Length > 0)
                        yield return (level, source, block.ToString());
                    block.Length = 0;
                    level = header.Groups[1].Value;
                    source = header.Groups[2].Value.Trim();
                    block.AppendLine(line);
                }
                else if (level.Length > 0)
                {
                    block.AppendLine(line);
                }
            }
            if (level.Length > 0 && block.Length > 0)
                yield return (level, source, block.ToString());
        }

        /// <summary>
        /// The full (namespace-qualified) declaring type of the most informative frame in a block - an
        /// interesting-namespace frame first, else the first non-framework frame - for attributing a log
        /// event to a mod by namespace. Null if no usable frame. Reuses the same Interesting/Framework
        /// classification <see cref="PickFrame"/> uses, but keeps the full type (not the short name).
        /// </summary>
        public static string? BestFrameType(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string? firstNonFramework = null;
            foreach (Match m in FrameRx.Matches(raw))
            {
                var type = m.Groups[1].Value;
                if (StartsWithAny(type, Interesting)) return type;
                if (firstNonFramework == null && !StartsWithAny(type, Framework)) firstNonFramework = type;
            }
            return firstNonFramework;
        }
    }

    internal static class NameBuilder
    {
        private const int MaxLength = 120;

        public static string Build(DateTime started, bool crashed, int errorCount, ErrorSignature? sig)
        {
            var tag = crashed ? "CRASH" : (errorCount > 0 ? "ERR" : "OK");

            var parts = new List<string>
            {
                started.ToString("yyyy-MM-dd_HHmm"),
                tag
            };

            if (errorCount > 0 && sig != null)
            {
                if (!string.IsNullOrEmpty(sig.ExceptionType))
                    parts.Add(TrimSuffix(sig.ExceptionType!, "Exception"));
                if (!string.IsNullOrEmpty(sig.Frame))
                    parts.Add(sig.Frame!);
                if (parts.Count == 2 && !string.IsNullOrEmpty(sig.Source))
                    parts.Add(sig.Source!);
            }

            var name = Sanitize(string.Join("_", parts.ToArray()));
            if (name.Length > MaxLength) name = name.Substring(0, MaxLength);
            return name + ".log";
        }

        private static string TrimSuffix(string value, string suffix)
        {
            return value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length
                ? value.Substring(0, value.Length - suffix.Length)
                : value;
        }

        /// <summary>
        /// Decompiled generic and closure names are full of characters Windows will not accept
        /// in a filename, so strip anything that is not clearly safe.
        /// </summary>
        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
                else if (c == ' ') sb.Append('_');
            }
            var result = sb.ToString().Trim('.', '_');
            return result.Length == 0 ? "log" : result;
        }

        public static string MakeUnique(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(path);

            for (int i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(dir, $"{stem}_{i}.log");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}.log");
        }
    }
}
