using System;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// Turns one log source's activity into a downloadable catalog (0.14.1) — every error/warning
    /// signature attributed to that BepInEx logger tag, with counts and first/last-seen. The handoff
    /// artefact the on-page "noisiest sources" list can only headline. Same two-renderer shape and
    /// file:// export path as <see cref="FixBrief"/>: a plain-text version (opens anywhere, pastes into
    /// a model) and a standalone styled HTML. Reads only the already-inert report data.
    /// </summary>
    internal static class LogCatalog
    {
        public static string BuildText(NoisySource n) => RenderText(n);
        public static string BuildHtml(NoisySource n) => RenderHtml(n);

        public static string FileNameBase(string source)
        {
            var sb = new StringBuilder("ATLAS_log_", (source?.Length ?? 0) + 12);
            foreach (var c in source ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        // ── text ─────────────────────────────────────────────────────────────────────────

        private static string RenderText(NoisySource n)
        {
            var sb = new StringBuilder(4096);
            sb.Append("# ATLAS log activity catalog - source ").Append(n.Source).Append("\n\n");
            sb.Append(n.Count).Append(" event(s) across ").Append(n.SessionCount).Append(" session(s); ")
              .Append(n.EventTotal).Append(" distinct signature(s).\n");
            sb.Append("Source: ATLAS ").Append(Plugin.Ver).Append(", The Planet Crafter (BepInEx mod). ")
              .Append("By session log, not per line.\n\n");

            sb.Append("## Activities (most frequent first)\n\n");
            foreach (var g in n.Events)
            {
                sb.Append("- [").Append(g.Level).Append("] ").Append(g.Label).Append('\n');
                sb.Append("    x").Append(g.Count).Append(" in ").Append(g.SessionCount).Append(" session(s)");
                if (g.FirstSeen.Length > 0)
                {
                    sb.Append("  ·  first ").Append(g.FirstSeen);
                    if (g.LastSeen.Length > 0 && g.LastSeen != g.FirstSeen) sb.Append("  ·  last ").Append(g.LastSeen);
                }
                sb.Append('\n');
                if (g.ExampleFrame.Length > 0) sb.Append("    frame: ").Append(g.ExampleFrame).Append('\n');
                sb.Append('\n');
            }
            if (n.EventTotal > n.Events.Count)
                sb.Append("(+ ").Append(n.EventTotal - n.Events.Count).Append(" more signature(s) not listed.)\n");

            return sb.ToString().TrimEnd() + "\n";
        }

        // ── html ─────────────────────────────────────────────────────────────────────────

        private static string RenderHtml(NoisySource n)
        {
            var sb = new StringBuilder(8192);
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>ATLAS log catalog - ").Append(Esc(n.Source)).Append("</title>\n");
            sb.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n<main>\n");

            sb.Append("<h1>Log activity — source ").Append(Esc(n.Source)).Append("</h1>\n");
            sb.Append("<p class=\"sub\">").Append(n.Count).Append(" event").Append(P(n.Count)).Append(" across ")
              .Append(n.SessionCount).Append(" session").Append(P(n.SessionCount)).Append("; ")
              .Append(n.EventTotal).Append(" distinct signature").Append(P(n.EventTotal))
              .Append(". ATLAS ").Append(Esc(Plugin.Ver)).Append(" · by session log, not per line.</p>\n");

            sb.Append("<table>\n<thead><tr><th>Level</th><th>Activity</th><th class=\"n\">Count</th>"
                    + "<th class=\"n\">Sessions</th><th>First → last</th></tr></thead>\n<tbody>\n");
            foreach (var g in n.Events)
            {
                var cls = g.Level == "WARNING" ? "warn" : "err";
                var span = g.FirstSeen.Length == 0 ? "—"
                    : Esc(g.FirstSeen) + (g.LastSeen.Length > 0 && g.LastSeen != g.FirstSeen ? " → " + Esc(g.LastSeen) : "");
                sb.Append("<tr><td><span class=\"lvl ").Append(cls).Append("\">").Append(Esc(g.Level)).Append("</span></td>");
                sb.Append("<td><code>").Append(Esc(g.Label)).Append("</code>");
                if (g.ExampleFrame.Length > 0)
                    sb.Append("<div class=\"frame\">").Append(Esc(g.ExampleFrame)).Append("</div>");
                sb.Append("</td>");
                sb.Append("<td class=\"n\">").Append(g.Count).Append("</td>");
                sb.Append("<td class=\"n\">").Append(g.SessionCount).Append("</td>");
                sb.Append("<td>").Append(span).Append("</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n");
            if (n.EventTotal > n.Events.Count)
                sb.Append("<p class=\"more\">+ ").Append(n.EventTotal - n.Events.Count).Append(" more signature").Append(P(n.EventTotal - n.Events.Count)).Append(" not listed.</p>\n");

            sb.Append("</main>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static string P(int n) => n == 1 ? "" : "s";

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

        private const string Css = @"
:root { color-scheme: light; }
* { box-sizing: border-box; }
body { margin: 0; padding: 40px 20px; background: #f4f5f7; color: #1b1f24;
  font: 15px/1.55 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }
main { max-width: 900px; margin: 0 auto; background: #fff; padding: 36px 40px;
  border: 1px solid #e2e5e9; border-radius: 10px; box-shadow: 0 1px 3px rgba(0,0,0,.05); }
h1 { font-size: 21px; margin: 0 0 6px; word-break: break-word; }
.sub { color: #5a6472; margin: 0 0 20px; }
table { border-collapse: collapse; width: 100%; }
th, td { text-align: left; padding: 7px 10px; border-bottom: 1px solid #eceef1; vertical-align: top; }
th { color: #5a6472; font-weight: 600; font-size: 13px; }
td.n, th.n { text-align: right; white-space: nowrap; width: 1%; }
code { font: 12.5px/1.5 ui-monospace, 'SF Mono', Consolas, monospace; word-break: break-word; }
.frame { color: #7a828d; font-size: 12px; margin-top: 2px; word-break: break-word; }
.lvl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .04em;
  padding: 1px 7px; border-radius: 999px; white-space: nowrap; }
.lvl.err { background: #fdecea; color: #b3261e; border: 1px solid #f4c7c3; }
.lvl.warn { background: #fff8e6; color: #8a6d00; border: 1px solid #f0d98a; }
.more { color: #7a828d; font-size: 13px; margin-top: 14px; }
";
    }
}
