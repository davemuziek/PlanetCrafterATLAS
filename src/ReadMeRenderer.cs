using System;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// Writes <c>READ_ME_FIRST.html</c> — the one file in the support bundle that must open standalone
    /// and explain everything to a reader who has never installed ATLAS. Its own compact dark CSS (the
    /// <see cref="IndexRenderer"/> shell precedent, not the full report CSS), and every third-party
    /// string routed through <see cref="Esc"/>, because a mod named <c>&lt;script&gt;</c> must not execute
    /// here either.
    ///
    /// The order is fixed by the work order: what this is, environment, mod roster, headline findings,
    /// where to look next, the framing line, and the one honest note about the archived reports' own
    /// (dead) bundle button.
    /// </summary>
    internal static class ReadMeRenderer
    {
        // The framing line, verbatim and prominent — the single most important sentence for a cold reader.
        public const string Framing =
            "A finding is a lead, not a verdict. ATLAS reports what it can see statically; it cannot see "
            + "everything, and a named mod is not necessarily at fault. Treat this as triage input.";

        public static string Write(ScanReport r, BundleOptions opt, string verdict, string generatedUtc)
        {
            var sb = new StringBuilder(16 * 1024);

            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>ATLAS support bundle — read me first</title>\n<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");
            sb.Append("<main>\n");

            sb.Append("<h1>ATLAS support bundle</h1>\n");

            // 1. What this is — for someone who has never heard of ATLAS — plus where to get it.
            sb.Append("<p class=\"lead\">This is a diagnostic bundle from <strong>ATLAS</strong>, a free, read-only "
                    + "BepInEx plugin for <em>The Planet Crafter</em> that watches installed mods for patch conflicts, "
                    + "missing dependencies, keybind clashes, and breakage after a game update. The person who sent it "
                    + "is running ATLAS; it collected their scan history, session logs and mod configs into this one zip "
                    + "so you don't have to work from a pasted fragment. The bundle's machine-readable shape is documented "
                    + "in <code>manifest.json</code> (and in <code>BUNDLE_FORMAT.md</code> in the ATLAS repository).</p>\n");

            // 6 (hoisted): the framing line, prominent, right under the intro so it frames everything below.
            sb.Append("<p class=\"framing\">").Append(Esc(Framing)).Append("</p>\n");

            // 2. Environment.
            sb.Append("<section class=\"box\">\n  <h2>Environment</h2>\n  <table class=\"kv\">\n");
            Kv(sb, "ATLAS version", opt.AtlasVersion);
            Kv(sb, "Game version", r.GameVersion);
            Kv(sb, "Assembly MVID", opt.Mvid.Length > 0 ? opt.Mvid : "(not recorded)");
            Kv(sb, "Unity", r.UnityVersion);
            Kv(sb, "BepInEx", opt.BepInExVersion.Length > 0 ? opt.BepInExVersion : "(unknown)");
            Kv(sb, "OS", SafeOs());
            Kv(sb, "Generated (UTC)", generatedUtc);
            Kv(sb, "Scope", opt.Scope.ToString() + (opt.Redact ? " · redacted" : " · NOT redacted"));
            sb.Append("  </table>\n</section>\n");

            // 3. Mod roster, partial-visibility flagged.
            sb.Append("<section class=\"box\">\n  <h2>Installed mods <span class=\"muted\">(")
              .Append(r.Plugins.Count).Append(")</span></h2>\n");
            sb.Append("  <table class=\"roster\">\n    <thead><tr><th>Mod</th><th>Version</th><th>ATLAS visibility</th></tr></thead>\n    <tbody>\n");
            foreach (var p in r.Plugins)
            {
                var vis = VisibilityOf(r, p.Name);
                sb.Append("      <tr><td>").Append(Esc(p.Name)).Append("</td><td class=\"mono\">").Append(Esc(p.Version))
                  .Append("</td><td>").Append(vis).Append("</td></tr>\n");
            }
            sb.Append("    </tbody>\n  </table>\n");
            if (r.CoveragePartialMods > 0)
                sb.Append("  <p class=\"muted\">“Partial” means ATLAS could not statically resolve some of that mod's hooks, "
                        + "so a clean result for it only covers what ATLAS could see — not a guarantee.</p>\n");
            sb.Append("</section>\n");

            // 4. Headline findings — the verdict and the High/Medium items, nothing more.
            sb.Append("<section class=\"box\">\n  <h2>Headline</h2>\n");
            sb.Append("  <p class=\"verdict v-").Append(verdict.ToLowerInvariant()).Append("\">Verdict: <strong>")
              .Append(Esc(Title(verdict))).Append("</strong> — ")
              .Append(r.HighCount).Append(" high / ").Append(r.MediumCount).Append(" medium / ").Append(r.LowCount)
              .Append(" low conflicts");
            if (r.CompatChecked && r.CompatFindings.Count > 0) sb.Append(" · ").Append(r.CompatFindings.Count).Append(" incompatible");
            if (r.PluginLoadFailures.Count > 0) sb.Append(" · ").Append(r.PluginLoadFailures.Count).Append(" failed to load");
            sb.Append(".</p>\n");

            var findings = SupportBundle.CollectFindings(r);
            if (findings.Count == 0)
            {
                sb.Append("  <p class=\"muted\">No high- or medium-severity findings. See <code>Scans/index.html</code> for the full detail.</p>\n");
            }
            else
            {
                sb.Append("  <ul class=\"findings\">\n");
                int shown = 0;
                foreach (var f in findings)
                {
                    if (shown++ >= 20) break;
                    sb.Append("    <li><span class=\"axis\">").Append(Esc(f.axis)).Append("</span>")
                      .Append("<span class=\"sev sev-").Append(f.severity.ToLowerInvariant()).Append("\">").Append(Esc(f.severity)).Append("</span>")
                      .Append("<code>").Append(Esc(f.member)).Append("</code>");
                    if (f.owner.Length > 0) sb.Append("<span class=\"owner\">").Append(Esc(f.owner)).Append("</span>");
                    sb.Append("</li>\n");
                }
                sb.Append("  </ul>\n");
                if (findings.Count > 20)
                    sb.Append("  <p class=\"muted\">+ ").Append(findings.Count - 20).Append(" more — see the full report.</p>\n");
            }
            sb.Append("</section>\n");

            // 5. Where to look next.
            sb.Append("<section class=\"box\">\n  <h2>Where to look next</h2>\n");
            sb.Append("  <p>Open <code>Scans/index.html</code> for the full history — every kept scan, newest first, with a "
                    + "Changes view that diffs sessions. Each scan also has a plain-text twin (<code>.txt</code>) that pastes "
                    + "cleanly into an issue. Session logs are under <code>Logs/</code>; mod configs under <code>Config/</code>.</p>\n");
            sb.Append("</section>\n");

            // 7. Honest notes.
            sb.Append("<section class=\"box notes\">\n  <h2>Two honest notes</h2>\n  <ul>\n");
            if (opt.Redact)
                sb.Append("    <li><strong>Redaction is best-effort.</strong> ATLAS removed profile paths, the account and "
                        + "machine names, and Steam / multiplayer join IDs it could recognise. A mod that logged something "
                        + "personal in a shape ATLAS does not know about may have slipped through — skim before sharing "
                        + "further, and the sender can add literals to remove via <code>Bundle.ExtraRedactions</code>.</li>\n");
            else
                sb.Append("    <li><strong>This bundle was NOT redacted.</strong> The sender turned redaction off, so paths, "
                        + "account names and IDs may be present in the logs and configs. Handle accordingly.</li>\n");
            sb.Append("    <li>The archived scan reports below carry their own “Download support bundle” button. Inside this "
                    + "zip that button won't do anything — you are already holding the bundle it would build.</li>\n");
            sb.Append("  </ul>\n</section>\n");

            sb.Append("<footer>Generated by ATLAS ").Append(Esc(opt.AtlasVersion)).Append(". Static file — no network, no tracking.</footer>\n");
            sb.Append("</main>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static void Kv(StringBuilder sb, string k, string v)
        {
            sb.Append("    <tr><th>").Append(Esc(k)).Append("</th><td>").Append(Esc(v)).Append("</td></tr>\n");
        }

        private static string VisibilityOf(ScanReport r, string modName)
        {
            if (!r.CoverageChecked) return "<span class=\"muted\">—</span>";
            foreach (var c in r.ModCoverages)
                if (string.Equals(c.Mod, modName, StringComparison.OrdinalIgnoreCase))
                    return c.FullyVisible
                        ? "<span class=\"vis-full\">full</span>"
                        : "<span class=\"vis-partial\">partial</span>";
            return "<span class=\"muted\">—</span>";
        }

        private static string SafeOs()
        {
            try { return Environment.OSVersion.ToString(); } catch { return "(unknown)"; }
        }

        private static string Title(string verdict)
        {
            if (string.IsNullOrEmpty(verdict)) return "";
            var v = verdict.ToLowerInvariant();
            return char.ToUpperInvariant(v[0]) + v.Substring(1);
        }

        private static string Esc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length + 8);
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
:root{--bg:#12151b;--panel:#1a1f28;--edge:#2a3140;--ink:#e7ebf1;--dim:#9aa4b2;
  --ok:#4caf78;--warn:#e0a800;--high:#e5534b;--accent:#6ea8fe;--mono:ui-monospace,Consolas,monospace;}
*{box-sizing:border-box}
html,body{margin:0;background:var(--bg);color:var(--ink)}
body{font:15px/1.6 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;padding:0}
main{max-width:860px;margin:0 auto;padding:32px 22px 60px}
h1{font-size:26px;margin:0 0 4px;letter-spacing:.01em}
h2{font-size:15px;text-transform:uppercase;letter-spacing:.06em;color:var(--dim);margin:0 0 12px}
.lead{color:var(--ink);margin:10px 0 18px;font-size:15.5px}
.lead code,.notes code,p code,li code{font-family:var(--mono);font-size:12.5px;background:#0e1218;
  border:1px solid var(--edge);border-radius:4px;padding:1px 6px;color:#cdd6e2;word-break:break-word}
.framing{border-left:3px solid var(--accent);background:#141c2b;color:var(--ink);
  padding:12px 16px;margin:0 0 22px;border-radius:0 8px 8px 0;font-size:15px}
.box{border:1px solid var(--edge);background:var(--panel);border-radius:10px;padding:16px 18px;margin:0 0 16px}
.muted{color:var(--dim);font-size:13px}
table.kv,table.roster{width:100%;border-collapse:collapse;font-size:14px}
.kv th{text-align:left;color:var(--dim);font-weight:600;width:180px;vertical-align:top;padding:4px 10px 4px 0;white-space:nowrap}
.kv td{padding:4px 0;color:var(--ink);font-family:var(--mono);font-size:13px;word-break:break-word}
.roster th{text-align:left;color:var(--dim);font-weight:600;font-size:11.5px;text-transform:uppercase;
  letter-spacing:.05em;padding:6px 10px;border-bottom:1px solid var(--edge)}
.roster td{padding:6px 10px;border-bottom:1px solid #222834}
.mono{font-family:var(--mono);font-size:12.5px;color:#cdd6e2}
.vis-full{color:var(--ok)}.vis-partial{color:var(--warn);font-weight:600}
.verdict{padding:10px 14px;border-radius:8px;border:1px solid var(--edge);margin:0 0 12px;font-size:14.5px}
.v-clean{border-color:#25563f;background:#122019}
.v-attention{border-color:#5a4a15;background:#1e1a10}
.v-problem{border-color:#5a2523;background:#1f1413}
ul.findings{list-style:none;margin:6px 0 0;padding:0}
ul.findings li{display:flex;gap:9px;align-items:center;flex-wrap:wrap;padding:6px 0;border-bottom:1px solid #222834}
.axis{font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:var(--dim);
  border:1px solid var(--edge);border-radius:999px;padding:1px 8px;flex:none}
.sev{font-size:11px;font-weight:700;border-radius:4px;padding:1px 7px;flex:none}
.sev-high{color:var(--high);border:1px solid var(--high)}
.sev-medium{color:var(--warn);border:1px solid var(--warn)}
.sev-low{color:var(--dim);border:1px solid var(--edge)}
.sev-none{color:var(--dim);border:1px solid var(--edge)}
ul.findings code{font-family:var(--mono);font-size:12.5px;background:#0e1218;border:1px solid var(--edge);
  border-radius:4px;padding:1px 6px;color:#cdd6e2;word-break:break-word;min-width:0}
.owner{color:var(--dim);font-size:12.5px}
.notes ul{margin:6px 0 0;padding-left:18px}
.notes li{margin:8px 0;color:var(--ink)}
footer{color:var(--dim);font-size:12px;margin-top:22px;padding-top:14px;border-top:1px solid var(--edge)}
";
    }
}
