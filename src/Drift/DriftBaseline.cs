using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// The five lifecycle states a baseline surface can be in (§8). Every one of them must
    /// render distinctly: collapsing any pair is how a tool starts lying. In particular
    /// <see cref="Changed"/> never overwrites the baseline - findings persist across sessions
    /// until the user explicitly accepts the new build.
    /// </summary>
    internal enum DriftBaselineState
    {
        NoBaseline,   // file absent - baseline established now, nothing to compare yet
        Unchanged,    // build key matches - game unchanged since baseline
        Changed,      // build key differs - the diff, with findings
        Unreadable,   // file present, parse failed - say so, do not silently recapture
        Unavailable,  // Cecil load failed / assembly missing - the rest of the scan is unaffected
    }

    internal sealed class PluginStamp
    {
        public string Guid = "";
        public string Version = "";
    }

    /// <summary>Parsed `# key value` header shared by both baseline files.</summary>
    internal sealed class BaselineHeader
    {
        public string Magic = "";
        public string Version = "";
        public string CapturedUtc = "";
        public string Game = "";
        public string Unity = "";
        public string Mvid = "";
        public int Groups;
        public readonly List<PluginStamp> Plugins = new List<PluginStamp>();
    }

    /// <summary>One tracked method: `M  type  name  returnType  params  bodyHash`.</summary>
    internal sealed class CodeMethodRow
    {
        public string Type = "";
        public string Name = "";
        public string ReturnType = "";
        public string Params = "";      // "()" or "(System.Int32,System.String)"
        public string Hash = "";        // first 16 hex chars of the normalised-IL SHA-256

        public string SignatureKey() => Type + "|" + Name + "|" + Params;
    }

    /// <summary>
    /// One reflected member (`R  type  member  kind  owner`) or a type-only check
    /// (`T  type  -  type  owner`). Kind is one of field / method / property / type.
    /// </summary>
    internal sealed class ReflRow
    {
        public string Type = "";
        public string Member = "";      // "-" for a type-only row
        public string Kind = "";        // field / method / property / type
        public string Owner = "";

        public string Key() => Type + "|" + Member + "|" + Kind;
    }

    /// <summary>One group snapshot: `G  id  concreteType  k=v;k=v;...`.</summary>
    internal sealed class ContentGroupRow
    {
        public string Id = "";
        public string ConcreteType = "";   // "Item" / "Constructible"
        public readonly Dictionary<string, string> Fields =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal sealed class CodeBaseline
    {
        public bool FileExists;
        public bool Ok;                 // parsed cleanly (false => Unreadable when FileExists)
        public string Error = "";
        public int SkippedRows;         // rows with an unrecognised type letter (forward-compat)
        public BaselineHeader Header = new BaselineHeader();
        public readonly List<CodeMethodRow> Methods = new List<CodeMethodRow>();
        public readonly List<ReflRow> Refl = new List<ReflRow>();
    }

    internal sealed class ContentBaseline
    {
        public bool FileExists;
        public bool Ok;
        public string Error = "";
        public int SkippedRows;
        public BaselineHeader Header = new BaselineHeader();
        public readonly List<ContentGroupRow> Groups = new List<ContentGroupRow>();
    }

    /// <summary>
    /// Row-typed TSV, matching the observed_keys.tsv precedent: no serializer dependency,
    /// greppable, diffable by hand. UTF-8 no BOM, '\n' only. A row whose type letter is
    /// unrecognised is skipped with a counted warning (a newer ATLAS reading an older baseline
    /// must degrade, not die); a row of a KNOWN type with too few columns is corruption and
    /// flips the whole file to Unreadable. Bump the version on any breaking format change and
    /// treat a mismatched version as Unreadable.
    /// </summary>
    internal static class DriftBaseline
    {
        public const string CodeMagic = "atlas-drift-code";
        public const string ContentMagic = "atlas-drift-content";
        public const string FormatVersion = "v1";

        // ── write ────────────────────────────────────────────────────────────────────

        public static void WriteCode(string path, BaselineHeader h,
                                      List<CodeMethodRow> methods, List<ReflRow> refl)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.Append("# ").Append(CodeMagic).Append("  ").Append(FormatVersion).Append('\n');
            sb.Append("# captured  ").Append(San(h.CapturedUtc)).Append('\n');
            sb.Append("# game      ").Append(San(h.Game)).Append('\n');
            sb.Append("# unity     ").Append(San(h.Unity)).Append('\n');
            sb.Append("# mvid      ").Append(San(h.Mvid)).Append('\n');
            foreach (var p in h.Plugins)
                sb.Append("# plugin    ").Append(San(p.Guid)).Append("  ").Append(San(p.Version)).Append('\n');

            foreach (var m in methods)
                sb.Append("M\t").Append(San(m.Type)).Append('\t').Append(San(m.Name)).Append('\t')
                  .Append(San(m.ReturnType)).Append('\t').Append(San(m.Params)).Append('\t')
                  .Append(San(m.Hash)).Append('\n');

            foreach (var r in refl)
            {
                var letter = r.Kind == "type" ? "T" : "R";
                sb.Append(letter).Append('\t').Append(San(r.Type)).Append('\t')
                  .Append(San(string.IsNullOrEmpty(r.Member) ? "-" : r.Member)).Append('\t')
                  .Append(San(r.Kind)).Append('\t').Append(San(r.Owner)).Append('\n');
            }

            AtomicWrite(path, sb.ToString());
        }

        public static void WriteContent(string path, BaselineHeader h, List<ContentGroupRow> groups)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("# ").Append(ContentMagic).Append("  ").Append(FormatVersion).Append('\n');
            sb.Append("# captured  ").Append(San(h.CapturedUtc)).Append('\n');
            sb.Append("# game      ").Append(San(h.Game)).Append('\n');
            sb.Append("# groups    ").Append(h.Groups.ToString()).Append('\n');
            foreach (var p in h.Plugins)
                sb.Append("# plugin    ").Append(San(p.Guid)).Append("  ").Append(San(p.Version)).Append('\n');

            foreach (var g in groups)
                sb.Append("G\t").Append(San(g.Id)).Append('\t').Append(San(g.ConcreteType)).Append('\t')
                  .Append(EncodeFields(g.Fields)).Append('\n');

            AtomicWrite(path, sb.ToString());
        }

        // ── read ─────────────────────────────────────────────────────────────────────

        public static CodeBaseline ReadCode(string path)
        {
            var b = new CodeBaseline();
            if (!File.Exists(path)) { b.FileExists = false; b.Ok = false; return b; }
            b.FileExists = true;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { b.Ok = false; b.Error = "unreadable file: " + ex.Message; return b; }

            if (!ParseHeader(lines, CodeMagic, b.Header, out var err)) { b.Ok = false; b.Error = err; return b; }

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                switch (f[0])
                {
                    case "M":
                        if (f.Length < 6) { b.Ok = false; b.Error = "malformed M row"; return b; }
                        b.Methods.Add(new CodeMethodRow
                        {
                            Type = f[1], Name = f[2], ReturnType = f[3], Params = f[4], Hash = f[5],
                        });
                        break;
                    case "R":
                        if (f.Length < 5) { b.Ok = false; b.Error = "malformed R row"; return b; }
                        b.Refl.Add(new ReflRow { Type = f[1], Member = f[2], Kind = f[3], Owner = f[4] });
                        break;
                    case "T":
                        if (f.Length < 5) { b.Ok = false; b.Error = "malformed T row"; return b; }
                        b.Refl.Add(new ReflRow { Type = f[1], Member = "-", Kind = "type", Owner = f[4] });
                        break;
                    default:
                        b.SkippedRows++;   // unrecognised type letter: degrade, do not die
                        break;
                }
            }

            b.Ok = true;
            return b;
        }

        public static ContentBaseline ReadContent(string path)
        {
            var b = new ContentBaseline();
            if (!File.Exists(path)) { b.FileExists = false; b.Ok = false; return b; }
            b.FileExists = true;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { b.Ok = false; b.Error = "unreadable file: " + ex.Message; return b; }

            if (!ParseHeader(lines, ContentMagic, b.Header, out var err)) { b.Ok = false; b.Error = err; return b; }

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (f[0] == "G")
                {
                    if (f.Length < 3) { b.Ok = false; b.Error = "malformed G row"; return b; }
                    var g = new ContentGroupRow { Id = f[1], ConcreteType = f[2] };
                    if (f.Length >= 4) DecodeFields(f[3], g.Fields);
                    b.Groups.Add(g);
                }
                else
                {
                    b.SkippedRows++;
                }
            }

            b.Ok = true;
            return b;
        }

        // ── header ───────────────────────────────────────────────────────────────────

        private static bool ParseHeader(string[] lines, string expectedMagic, BaselineHeader h, out string error)
        {
            error = "";
            var sawMagic = false;

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '#') continue;

                // "# key   value..." - the magic line is "# <magic>  <version>".
                var body = line.Substring(1).Trim();
                if (body.Length == 0) continue;

                if (!sawMagic)
                {
                    var parts = SplitWs(body);
                    if (parts.Count < 2 || parts[0] != expectedMagic)
                    {
                        error = "unrecognised baseline header (expected " + expectedMagic + ")";
                        return false;
                    }
                    if (parts[1] != FormatVersion)
                    {
                        error = "baseline format " + parts[1] + " != " + FormatVersion + " (treated as corrupt)";
                        return false;
                    }
                    h.Magic = parts[0];
                    h.Version = parts[1];
                    sawMagic = true;
                    continue;
                }

                var kv = SplitWs(body);
                if (kv.Count == 0) continue;
                switch (kv[0])
                {
                    case "captured": h.CapturedUtc = kv.Count > 1 ? kv[1] : ""; break;
                    case "game":     h.Game = kv.Count > 1 ? kv[1] : ""; break;
                    case "unity":    h.Unity = kv.Count > 1 ? kv[1] : ""; break;
                    case "mvid":     h.Mvid = kv.Count > 1 ? kv[1] : ""; break;
                    case "groups":   h.Groups = kv.Count > 1 && int.TryParse(kv[1], out var n) ? n : 0; break;
                    case "plugin":
                        if (kv.Count >= 3)
                            h.Plugins.Add(new PluginStamp { Guid = kv[1], Version = kv[2] });
                        else if (kv.Count == 2)
                            h.Plugins.Add(new PluginStamp { Guid = kv[1], Version = "" });
                        break;
                }
            }

            if (!sawMagic) { error = "no baseline header line"; return false; }
            return true;
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        /// <summary>Any field that could contain a tab or newline is sanitised on write.</summary>
        private static string San(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        // Field blob: "k=v;k=v". Values here are ids / enum names / numbers, so ';' and '='
        // never legitimately appear; strip them defensively rather than invent an escape scheme.
        private static string FieldSan(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return San(s).Replace(';', ',').Replace('=', '-');
        }

        private static string EncodeFields(Dictionary<string, string> fields)
        {
            var sb = new StringBuilder(128);
            var first = true;
            foreach (var kv in fields)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(FieldSan(kv.Key)).Append('=').Append(FieldSan(kv.Value));
            }
            return sb.ToString();
        }

        private static void DecodeFields(string blob, Dictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(blob)) return;
            foreach (var pair in blob.Split(';'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                if (eq < 0) { into[pair] = ""; continue; }
                into[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }
        }

        private static List<string> SplitWs(string s)
        {
            var outp = new List<string>(4);
            var i = 0; var n = s.Length;
            while (i < n)
            {
                while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
                if (i >= n) break;
                var start = i;
                while (i < n && s[i] != ' ' && s[i] != '\t') i++;
                outp.Add(s.Substring(start, i - start));
            }
            return outp;
        }

        /// <summary>
        /// Write via a temp file then move, so a crash mid-write cannot leave a truncated
        /// baseline that the next launch would read as Unreadable.
        /// </summary>
        private static void AtomicWrite(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
