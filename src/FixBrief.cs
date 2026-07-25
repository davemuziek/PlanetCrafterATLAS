using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// Turns one <see cref="DriftFinding"/> into a self-contained fix brief a human or an AI coder
    /// can read cold and know how to start. It is the handoff artefact the Update-impact section
    /// can only gesture at inline: the finding says WHAT moved; the brief adds who is affected, the
    /// exact build context, and a concrete, kind-specific plan of attack.
    ///
    /// One content model, two renderers - the same seam the whole report uses. <see cref="Model"/>
    /// builds an ordered list of blocks; <see cref="BuildText"/> renders them as ASCII-clean
    /// plain text (opens without fuss in Notepad, safe to paste into a model, no encoding prompt),
    /// and <see cref="BuildHtml"/> renders a standalone styled document (opens in any browser with
    /// full readability, no editor or extension association needed). Deliberately kept ASCII in the
    /// model so neither output can trip a "which encoding?" dialog. It reads only the already-inert
    /// report, exactly like the renderers, so generating a brief can never touch game or save state.
    /// </summary>
    internal static class FixBrief
    {
        // ── public API ───────────────────────────────────────────────────────────────────

        public static string BuildText(DriftFinding f, ScanReport r) => RenderText(Model(f, r));
        public static string BuildHtml(DriftFinding f, ScanReport r) => RenderHtml(Model(f, r), f.Member);

        /// <summary>Download stem without an extension: "ATLAS_fix_SpaceCraft.UiWindowCraft.CreateGrid".</summary>
        public static string FileNameBase(DriftFinding f)
        {
            var member = string.IsNullOrEmpty(f.Member) ? "finding" : f.Member;
            var sb = new StringBuilder("ATLAS_fix_", member.Length + 12);
            foreach (var c in member)
                sb.Append((char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') ? c : '_');
            return sb.ToString();
        }

        // ── content model ────────────────────────────────────────────────────────────────

        private enum BK { H1, H2, Para, Note, Bullets, Ordered, KeyVals, Rule }

        private sealed class Block
        {
            public BK Kind;
            public string Text = "";
            public List<string> Items = new List<string>();
            public List<KeyValuePair<string, string>> Pairs = new List<KeyValuePair<string, string>>();
        }

        private static Block H1(string t) => new Block { Kind = BK.H1, Text = t };
        private static Block H2(string t) => new Block { Kind = BK.H2, Text = t };
        private static Block Para(string t) => new Block { Kind = BK.Para, Text = t };
        private static Block Note(string t) => new Block { Kind = BK.Note, Text = t };
        private static Block Bullets(IEnumerable<string> x) => new Block { Kind = BK.Bullets, Items = x.ToList() };
        private static Block Ordered(IEnumerable<string> x) => new Block { Kind = BK.Ordered, Items = x.ToList() };
        private static Block Rule() => new Block { Kind = BK.Rule };

        private static Block KeyVals(params (string k, string v)[] pairs)
        {
            var b = new Block { Kind = BK.KeyVals };
            foreach (var p in pairs) b.Pairs.Add(new KeyValuePair<string, string>(p.k, p.v));
            return b;
        }

        /// <summary>
        /// The brief as an ordered list of blocks. Every string here is ASCII; the only formatting
        /// markers are Markdown-style **bold** and `code`, which read fine as plain text and are
        /// turned into tags by the HTML renderer.
        /// </summary>
        private static List<Block> Model(DriftFinding f, ScanReport r)
        {
            var blocks = new List<Block>
            {
                H1("ATLAS fix brief - " + f.Member),
                KeyVals(
                    ("Change type", KindTitle(f.Kind)),
                    ("Severity", SevWord(f.Severity)),
                    ("Status", StatusWord(f.Status)),
                    ("Source", "ATLAS " + Plugin.Ver + " drift scan, The Planet Crafter (BepInEx mod)")),
                H2("What changed"),
                Para(f.Detail.Length > 0 ? f.Detail : KindTitle(f.Kind)),
            };

            if (f.OwnerVersionChanged)
                blocks.Add(Note("A mod that patches this member was updated since the baseline was "
                    + "captured, so it may already account for this change. Confirm against the current "
                    + "mod version before spending time on it."));

            blocks.Add(H2("Who is affected"));
            if (f.Owners.Count > 0)
            {
                blocks.Add(Bullets(f.Owners));
                if (f.PatchKinds.Length > 0)
                {
                    var pad = f.PatchKinds.IndexOf("transpiler", StringComparison.OrdinalIgnoreCase) >= 0
                        ? " A transpiler is in play, so this is IL-sensitive - treat a body change as high-touch."
                        : " No transpiler here, so a body change is usually survivable; a missing or renamed target is the real risk.";
                    blocks.Add(Para("Patch shape: **" + f.PatchKinds + "**." + pad));
                }
            }
            else
            {
                blocks.Add(Para("No installed mod patches this member through Harmony. It surfaced through "
                    + "reflection or the content roster, so the affected code is a reflected lookup or a "
                    + "group/id reference rather than a patch."));
            }

            blocks.Add(H2("Build context"));
            blocks.Add(KeyVals(
                ("Baseline game version", Val(r.BaselineGameVersion)),
                ("Current game version", Val(r.GameVersion)),
                ("Game build changed", r.GameBuildChanged ? "yes" : "no"),
                ("Assembly mvid", Short(r.BaselineMvid) + " -> " + Short(r.CurrentMvid)),
                ("Baseline captured (UTC)", Val(r.BaselineCapturedUtc)),
                ("Plugin roster changed", r.PluginRosterChanged ? "yes" : "no"),
                ("Methods tracked by drift", r.DriftMethodsTracked.ToString())));

            blocks.Add(H2("How to approach it"));
            blocks.Add(Para(ApproachLead(f.Kind)));
            blocks.Add(Bullets(ApproachSteps(f.Kind)));

            blocks.Add(H2("Confirm against the current game"));
            blocks.Add(Para("ATLAS compares the game to a recorded baseline: it can tell you *that* this "
                + "member moved, not what it moved to. To see the new shape, open the current game "
                + "assembly in a decompiler and compare it with how your mod uses it:"));
            blocks.Add(Bullets(new[]
            {
                "File: `Planet Crafter_Data/Managed/Assembly-CSharp.dll` (dnSpy, ILSpy, or dotPeek).",
                "Look at: `" + f.Member + "`" + TypeHint(f.Member),
                "Compare with: the patch target or reflection call in the affected mod above.",
            }));

            blocks.Add(H2("After you've fixed it"));
            blocks.Add(Ordered(new[]
            {
                "Rebuild the mod and drop the DLL back into `BepInEx/plugins`.",
                "Clear this finding by accepting the current build as the new baseline: set "
                    + "`Drift.AcceptCurrentBuild = true` in ATLAS's config and load a save. It records the "
                    + "current game as the baseline and resets itself to `false`.",
                "Re-run the scan (load a save). This member should drop off Update impact; if it is still "
                    + "listed, the target still is not resolving.",
            }));

            blocks.Add(Rule());
            var verbatim = "Verbatim finding - kind=`" + f.Kind + "`, severity=`" + f.Severity
                         + "`, member=`" + f.Member + "`";
            if (f.Owners.Count > 0) verbatim += ", owners=`" + string.Join(", ", f.Owners.ToArray()) + "`";
            if (f.PatchKinds.Length > 0) verbatim += ", patches=`" + f.PatchKinds + "`";
            blocks.Add(Para(verbatim + "."));

            return blocks;
        }

        // ── text renderer ────────────────────────────────────────────────────────────────

        private static string RenderText(List<Block> blocks)
        {
            var sb = new StringBuilder(2048);
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case BK.H1: sb.Append("# ").Append(b.Text).Append("\n\n"); break;
                    case BK.H2: sb.Append("## ").Append(b.Text).Append("\n\n"); break;
                    case BK.Para: sb.Append(b.Text).Append("\n\n"); break;
                    case BK.Note: sb.Append("> ").Append(b.Text).Append("\n\n"); break;
                    case BK.Rule: sb.Append("---\n\n"); break;
                    case BK.Bullets:
                        foreach (var it in b.Items) sb.Append("- ").Append(it).Append('\n');
                        sb.Append('\n');
                        break;
                    case BK.Ordered:
                        for (int i = 0; i < b.Items.Count; i++)
                            sb.Append(i + 1).Append(". ").Append(b.Items[i]).Append('\n');
                        sb.Append('\n');
                        break;
                    case BK.KeyVals:
                        foreach (var p in b.Pairs) sb.Append("- ").Append(p.Key).Append(": ").Append(p.Value).Append('\n');
                        sb.Append('\n');
                        break;
                }
            }
            return sb.ToString().TrimEnd() + "\n";
        }

        // ── html renderer ────────────────────────────────────────────────────────────────

        private static string RenderHtml(List<Block> blocks, string member)
        {
            var sb = new StringBuilder(4096);
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>ATLAS fix brief - ").Append(EscHtml(member)).Append("</title>\n");
            sb.Append("<style>\n").Append(BriefCss).Append("\n</style>\n</head>\n<body>\n<main>\n");

            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case BK.H1: sb.Append("<h1>").Append(Inline(b.Text)).Append("</h1>\n"); break;
                    case BK.H2: sb.Append("<h2>").Append(Inline(b.Text)).Append("</h2>\n"); break;
                    case BK.Para: sb.Append("<p>").Append(Inline(b.Text)).Append("</p>\n"); break;
                    case BK.Note: sb.Append("<blockquote>").Append(Inline(b.Text)).Append("</blockquote>\n"); break;
                    case BK.Rule: sb.Append("<hr>\n"); break;
                    case BK.Bullets:
                        sb.Append("<ul>\n");
                        foreach (var it in b.Items) sb.Append("<li>").Append(Inline(it)).Append("</li>\n");
                        sb.Append("</ul>\n");
                        break;
                    case BK.Ordered:
                        sb.Append("<ol>\n");
                        foreach (var it in b.Items) sb.Append("<li>").Append(Inline(it)).Append("</li>\n");
                        sb.Append("</ol>\n");
                        break;
                    case BK.KeyVals:
                        sb.Append("<table>\n");
                        foreach (var p in b.Pairs)
                            sb.Append("<tr><th>").Append(EscHtml(p.Key)).Append("</th><td>")
                              .Append(Inline(p.Value)).Append("</td></tr>\n");
                        sb.Append("</table>\n");
                        break;
                }
            }

            sb.Append("</main>\n</body>\n</html>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Minimal inline formatter for the HTML renderer: escape first (every value is untrusted -
        /// a mod may be named <c>&lt;script&gt;</c>), then turn `**bold**` and `` `code` `` into tags.
        /// The two delimiters do not collide, and both survive HTML escaping, so a single left-to-right
        /// pass is safe. No other Markdown is used in the model, so none is interpreted here.
        /// </summary>
        private static string Inline(string s)
        {
            var esc = EscHtml(s);
            esc = ReplacePairs(esc, "**", "<strong>", "</strong>");
            esc = ReplacePairs(esc, "`", "<code>", "</code>");
            return esc;
        }

        /// <summary>Replaces matched delimiter pairs. An unmatched trailing delimiter is left literal.</summary>
        private static string ReplacePairs(string s, string delim, string open, string close)
        {
            var sb = new StringBuilder(s.Length + 16);
            int i = 0; bool inSpan = false;
            while (i < s.Length)
            {
                if (i + delim.Length <= s.Length && string.CompareOrdinal(s, i, delim, 0, delim.Length) == 0)
                {
                    // Only open a span if a matching closing delimiter exists later; otherwise literal.
                    if (!inSpan && s.IndexOf(delim, i + delim.Length, StringComparison.Ordinal) < 0)
                    {
                        sb.Append(s, i, delim.Length);
                    }
                    else
                    {
                        sb.Append(inSpan ? close : open);
                        inSpan = !inSpan;
                    }
                    i += delim.Length;
                }
                else
                {
                    sb.Append(s[i]);
                    i++;
                }
            }
            if (inSpan) sb.Append(close);   // defensive: never leave a span open
            return sb.ToString();
        }

        private static string EscHtml(string s)
        {
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ── kind-specific guidance ───────────────────────────────────────────────────────

        /// <summary>The lead sentence of the plan, chosen by how this kind of change actually fails.</summary>
        private static string ApproachLead(DriftKind k) => k switch
        {
            DriftKind.TypeMissing =>
                "The type this mod targets no longer exists under that name - renamed, moved to a different "
                + "namespace, or removed outright.",
            DriftKind.TargetMissing =>
                "The method this mod patches no longer resolves: renamed, overload changed, inlined, or "
                + "removed. Harmony will fail to apply the patch, or silently patch nothing.",
            DriftKind.SignatureChanged =>
                "The method still exists but its signature changed - parameters, return type, or "
                + "static/instance. The patch will fail to bind, or bind the wrong injected values.",
            DriftKind.BodyChanged =>
                "The method body changed but its signature did not. Prefix/postfix patches usually keep "
                + "working; the real exposure is transpilers and any patch that assumed specific internal behaviour.",
            DriftKind.ReflectedMemberMissing =>
                "A member the mod reaches by name through reflection (`AccessTools.Field/Method/PropertyGetter`, "
                + "`GetField(\"...\")`) no longer exists - almost always a rename.",
            DriftKind.GroupAdded =>
                "A content group was added since the baseline. Not a break on its own - it matters only if "
                + "your mod enumerates the roster or hardcodes a set of ids.",
            DriftKind.GroupRemoved =>
                "A content group was removed since the baseline. Any code that references its id by string "
                + "now gets a null back.",
            DriftKind.GroupFieldChanged =>
                "A tracked field on a content group changed - often a recategorisation. If your mod keys off "
                + "that field, this item's behaviour changed silently.",
            DriftKind.NullCraftableInList =>
                "`craftableInList` came back null - a known landmine that takes down every crafter screen on "
                + "the first run. This is usually an init/load-order problem, not the patch itself.",
            _ =>
                "Open the member in a decompiler and compare it with how the affected mod uses it, then update "
                + "the patch target, signature, or reflected name to match.",
        };

        /// <summary>The concrete steps for the plan. These are what make a bare finding actionable on first read.</summary>
        private static string[] ApproachSteps(DriftKind k) => k switch
        {
            DriftKind.TypeMissing => new[]
            {
                "Search the current `Assembly-CSharp` for a type with the same members/shape; a rename usually keeps the body intact.",
                "Update every `typeof(...)`, `[HarmonyPatch(typeof(X))]`, and `AccessTools.TypeByName(\"...\")` that names it.",
                "If the type was split or merged, the members you patch may now live on more than one type - re-point each patch to wherever its method actually landed.",
            },
            DriftKind.TargetMissing => new[]
            {
                "Find the equivalent method on the current type - compare bodies, not just names.",
                "Update the `[HarmonyPatch(...)]` target: both the method name and the argument-type array that disambiguates the overload.",
                "If it split into an overload set, pick the overload you actually meant and pin its parameter types explicitly.",
                "If it was inlined away, patch its caller instead, or the nearest surviving method.",
            },
            DriftKind.SignatureChanged => new[]
            {
                "Update the `[HarmonyPatch]` argument-type array to the new parameter types.",
                "Update your patch method's injected parameters: `__result` type, `ref`/`out` on args, `___field` names, and any added or removed parameters.",
                "For a transpiler, re-check `ldarg`/`starg` indices - they shift when the parameter list changes, even if your matched IL pattern did not.",
            },
            DriftKind.BodyChanged => new[]
            {
                "If any owner above is a **transpiler**, treat this as high-touch: the IL your matcher keys on may no longer be present. Re-derive the pattern against the current method body.",
                "For prefix/postfix, confirm the behaviour you depend on (an early-out, a field write, a side effect) still happens where you expect it.",
                "Diff the old and new method bodies in a decompiler to see exactly what moved.",
            },
            DriftKind.ReflectedMemberMissing => new[]
            {
                "Find the renamed field/method on the type and update the string literal.",
                "These fail at runtime, not compile time, so add a null-check-and-log around the lookup if you want a softer failure the next time the ground moves.",
            },
            DriftKind.GroupAdded => new[]
            {
                "If you iterate all groups, confirm the newcomer does not violate an assumption (its category, unlock, or recipe shape).",
                "If you keep a hardcoded id list, decide whether the new id belongs in it.",
            },
            DriftKind.GroupRemoved => new[]
            {
                "Search the mod for the removed id and either drop the reference or guard it.",
                "If the item was replaced rather than deleted, re-point to the replacement id.",
            },
            DriftKind.GroupFieldChanged => new[]
            {
                "Find where you read this field and confirm the new value still routes the item the way you intended.",
            },
            DriftKind.NullCraftableInList => new[]
            {
                "Make sure whatever populates `craftableInList` runs before the crafter UI reads it.",
                "Guard your own access with a null check and log rather than throw, so one empty list does not cascade into the whole UI.",
            },
            _ => new[]
            {
                "Update the patch target, signature, or reflected name to match the current game, then rebuild and re-scan.",
            },
        };

        // ── small helpers ────────────────────────────────────────────────────────────────

        private static string Val(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        private static string Short(string mvid) =>
            string.IsNullOrEmpty(mvid) ? "-" : (mvid.Length <= 8 ? mvid : mvid.Substring(0, 8));

        /// <summary>"SpaceCraft.UiWindowCraft.CreateGrid" -> " (on type `SpaceCraft.UiWindowCraft`)".</summary>
        private static string TypeHint(string member)
        {
            var dot = member.LastIndexOf('.');
            if (dot <= 0) return "";
            return " (on type `" + member.Substring(0, dot) + "`)";
        }

        private static string KindTitle(DriftKind k) => k switch
        {
            DriftKind.TypeMissing => "Patched type missing",
            DriftKind.TargetMissing => "Patched method missing",
            DriftKind.SignatureChanged => "Patched method signature changed",
            DriftKind.BodyChanged => "Patched method body changed",
            DriftKind.ReflectedMemberMissing => "Reflected member missing",
            DriftKind.GroupAdded => "Content group added",
            DriftKind.GroupRemoved => "Content group removed",
            DriftKind.GroupFieldChanged => "Content group field changed",
            DriftKind.NullCraftableInList => "Null craftableInList",
            DriftKind.NotTracked => "Newly patched (not yet tracked)",
            _ => k.ToString(),
        };

        private static string SevWord(Severity s) => s switch
        {
            Severity.High => "High",
            Severity.Medium => "Medium",
            Severity.Low => "Low",
            _ => "None",
        };

        private static string StatusWord(DriftStatus s) => s switch
        {
            DriftStatus.Active => "Active - re-verified as still broken in the current game + mods",
            DriftStatus.Review => "Review - static analysis cannot confirm a mod-side fix; verify, then accept the build",
            _ => "Resolved",
        };

        // ── standalone brief stylesheet ──────────────────────────────────────────────────
        // Light, print-friendly, self-contained. A brief is read on its own, not inside the dark
        // report shell, so it gets a clean document look that needs no external font or network.
        private const string BriefCss = @"
:root { color-scheme: light; }
* { box-sizing: border-box; }
body {
  margin: 0; padding: 40px 20px; background: #f4f5f7; color: #1b1f24;
  font: 16px/1.6 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
}
main {
  max-width: 780px; margin: 0 auto; background: #fff; padding: 40px 44px;
  border: 1px solid #e2e5e9; border-radius: 10px; box-shadow: 0 1px 3px rgba(0,0,0,.05);
}
h1 { font-size: 22px; line-height: 1.3; margin: 0 0 20px; word-break: break-word; }
h2 { font-size: 15px; text-transform: uppercase; letter-spacing: .05em; color: #5a6472;
     margin: 28px 0 8px; padding-bottom: 6px; border-bottom: 1px solid #eceef1; }
p { margin: 8px 0; }
ul, ol { margin: 8px 0; padding-left: 24px; }
li { margin: 5px 0; }
blockquote {
  margin: 12px 0; padding: 10px 16px; background: #fff8e6;
  border-left: 3px solid #e0a800; border-radius: 4px; color: #6b5300;
}
table { border-collapse: collapse; margin: 10px 0; width: 100%; }
th, td { text-align: left; padding: 6px 12px; border-bottom: 1px solid #eceef1; vertical-align: top; }
th { color: #5a6472; font-weight: 600; white-space: nowrap; width: 1%; }
code {
  font: 13.5px/1.5 ui-monospace, 'SF Mono', 'Cascadia Mono', Consolas, 'Liberation Mono', monospace;
  background: #f0f2f4; padding: 1px 5px; border-radius: 4px; border: 1px solid #e2e5e9; word-break: break-word;
}
strong { font-weight: 700; }
hr { border: none; border-top: 1px solid #e2e5e9; margin: 28px 0 16px; }
main > p:last-child { color: #7a828d; font-size: 13px; }
";
    }
}
