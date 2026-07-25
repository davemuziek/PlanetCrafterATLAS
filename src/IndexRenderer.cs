using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ATLAS
{
    /// <summary>
    /// The scans homepage (0.13.0): one self-contained <c>Scans/index.html</c>, rewritten each scan,
    /// that lists every kept scan (newest first), diffs the newest against a chosen earlier scan, and
    /// shows the selected scan in an iframe. It embeds the ledger inline — a file:// page cannot fetch
    /// sibling files, so it can't read the scan htmls at view time; it can only iframe them (allowed)
    /// and diff from the embedded data. Every third-party string reaches the DOM via textContent in the
    /// script, never innerHTML, so a mod named &lt;script&gt; cannot execute.
    /// </summary>
    internal static class IndexRenderer
    {
        public static string Write(string dir, List<LedgerEntry> entries,
                                   bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "index.html");
            File.WriteAllText(path, Render(entries, bundleEnabled, bundle), new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// The homepage as a string. Reused by <see cref="SupportBundle"/>, which renders a copy from a
        /// ledger filtered to the scans it bundles, with the download block off (the reader is already
        /// holding the bundle).
        /// </summary>
        public static string Render(List<LedgerEntry> entries, bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            var sb = new StringBuilder(16 * 1024);
            Page(sb, entries, bundleEnabled, bundle);
            return sb.ToString();
        }

        private static void Page(StringBuilder sb, List<LedgerEntry> entries,
                                 bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>ATLAS scans</title>\n<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");

            sb.Append("<aside class=\"side\">\n");
            sb.Append("  <div class=\"brand\">ATLAS <span>scans</span></div>\n");
            BundlePin(sb, bundleEnabled, bundle);
            sb.Append("  <button class=\"nav-item nav-changes\" data-view=\"changes\">Changes</button>\n");
            sb.Append("  <div class=\"scan-list\" id=\"scan-list\"></div>\n");
            sb.Append("  <div class=\"side-foot\">Static file — no network. Open once; it updates on each scan.</div>\n");
            sb.Append("</aside>\n");

            sb.Append("<main class=\"main\" id=\"main\"></main>\n");

            // Embedded ledger (newest first). No fetch: the browser reads it straight from here.
            sb.Append("<script>\nwindow.ATLAS_INDEX=").Append(DataJson(entries)).Append(";\n</script>\n");
            sb.Append("<script>\n").Append(Script).Append("\n</script>\n");
            sb.Append("</body>\n</html>\n");
        }

        // ── support bundle pin ───────────────────────────────────────────────────────────
        // A plain hyperlink to the sibling zip (browsers navigate file:// → file:// freely, and a .zip
        // is not renderable, so it downloads — the fetch() restriction a disk-opened page has does not
        // apply to a link). Facts, not a preview: a static page cannot measure at click time, but it can
        // state what was true at build time. Nothing rendered when the feature is off or auto-build has
        // not produced a bundle yet.

        private static void BundlePin(StringBuilder sb, bool bundleEnabled, SupportBundle.BundleInfo? bundle)
        {
            if (!bundleEnabled) return;

            sb.Append("  <div class=\"bundle-pin\">\n");
            if (bundle != null && bundle.Built)
            {
                sb.Append("    <a class=\"bundle-dl\" href=\"").Append(SupportBundle.FileName)
                  .Append("\" download>⭳ Support bundle</a>\n");
                sb.Append("    <div class=\"bundle-facts\">").Append(EscHtml(SupportBundle.FactsLine(bundle))).Append("</div>\n");
                sb.Append("    <div class=\"bundle-frame\">One scrubbed zip to hand a mod author — scans, logs and configs. "
                        + "A finding is a lead, not a verdict.</div>\n");
            }
            else
            {
                var reason = bundle == null
                    ? "will be written after the first scan completes."
                    : (bundle.Error.Length > 0 ? "last build failed — " + bundle.Error : "not built yet.");
                sb.Append("    <div class=\"bundle-facts\">Support bundle: ").Append(EscHtml(reason)).Append("</div>\n");
            }
            sb.Append("  </div>\n");
        }

        /// <summary>Minimal HTML-text escape for the server-rendered sidebar strings.</summary>
        private static string EscHtml(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s!.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // ── embedded data ──────────────────────────────────────────────────────────────

        private static string DataJson(List<LedgerEntry> entries)
        {
            // Newest first.
            var sb = new StringBuilder(8 * 1024);
            sb.Append("{\"scans\":[");
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (i < entries.Count - 1) sb.Append(',');
                sb.Append("{\"file\":").Append(Js(e.File))
                  .Append(",\"time\":").Append(Js(e.TimeLocal))
                  .Append(",\"verdict\":").Append(Js(e.Verdict))
                  .Append(",\"h\":").Append(e.H).Append(",\"m\":").Append(e.M).Append(",\"l\":").Append(e.L)
                  .Append(",\"keys\":{");
                var firstCat = true;
                foreach (var cat in ScanLedger.Categories)
                {
                    if (!e.Keys.TryGetValue(cat, out var list) || list.Count == 0) continue;
                    if (!firstCat) sb.Append(',');
                    firstCat = false;
                    sb.Append(Js(cat)).Append(":[");
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append(Js(list[k]));
                    }
                    sb.Append(']');
                }
                sb.Append("}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>JSON string literal, safe to embed in a &lt;script&gt; block (a value cannot close the tag).</summary>
        private static string Js(string s)
        {
            var sb = new StringBuilder((s?.Length ?? 0) + 8);
            sb.Append('"');
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ── inline assets ────────────────────────────────────────────────────────────────

        private const string Css = @"
:root{--bg:#12151b;--panel:#1a1f28;--edge:#2a3140;--ink:#e7ebf1;--dim:#9aa4b2;
  --ok:#4caf78;--warn:#e0a800;--high:#e5534b;--accent:#6ea8fe;}
*{box-sizing:border-box}
html,body{margin:0;height:100%}
body{display:flex;background:var(--bg);color:var(--ink);
  font:14px/1.55 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}
.side{width:264px;min-width:264px;height:100vh;overflow:auto;background:var(--panel);
  border-right:1px solid var(--edge);display:flex;flex-direction:column;padding:14px 12px;gap:6px}
.brand{font-size:16px;font-weight:700;letter-spacing:.02em;padding:4px 6px 10px}
.brand span{color:var(--dim);font-weight:500}
.nav-item{display:block;width:100%;text-align:left;background:transparent;color:var(--ink);
  border:1px solid transparent;border-radius:8px;padding:8px 10px;cursor:pointer;font:inherit}
.nav-item:hover{background:#212836}
.nav-item.active{background:#243049;border-color:var(--edge)}
.nav-changes{font-weight:600;border:1px solid var(--edge);margin-bottom:4px}
.bundle-pin{border:1px solid var(--edge);background:#141c2b;border-radius:8px;padding:9px 10px;margin:2px 0 6px}
.bundle-dl{display:block;text-align:center;background:var(--accent);color:#0b0f16;font-weight:700;
  text-decoration:none;border-radius:6px;padding:7px 8px;font-size:13px}
.bundle-dl:hover{filter:brightness(1.08)}
.bundle-facts{color:var(--dim);font-size:11px;line-height:1.4;margin-top:7px;font-variant-numeric:tabular-nums}
.bundle-frame{color:var(--dim);font-size:11px;line-height:1.4;margin-top:6px;font-style:italic}
.scan-list{display:flex;flex-direction:column;gap:3px;margin-top:2px}
.scan-row{display:flex;align-items:center;gap:9px}
.dot{width:9px;height:9px;border-radius:50%;flex:none;background:var(--dim)}
.dot.clean{background:var(--ok)}.dot.attention{background:var(--warn)}.dot.problem{background:var(--high)}
.scan-time{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.scan-sub{color:var(--dim);font-size:12px}
.side-foot{margin-top:auto;color:var(--dim);font-size:11.5px;padding:8px 6px;line-height:1.4}
.main{flex:1;height:100vh;overflow:auto}
.main iframe{width:100%;height:100%;border:0;background:var(--bg)}
.changes{padding:22px 26px;max-width:900px}
.changes h1{font-size:20px;margin:0 0 4px}
.changes .lead{color:var(--dim);margin:0 0 18px}
.verdict-line{margin:0 0 18px;padding:10px 14px;border:1px solid var(--edge);border-radius:8px;background:var(--panel)}
.cat{margin:0 0 16px;border:1px solid var(--edge);border-radius:10px;background:var(--panel);overflow:hidden}
.cat h2{font-size:13px;text-transform:uppercase;letter-spacing:.05em;color:var(--dim);
  margin:0;padding:9px 14px;border-bottom:1px solid var(--edge)}
.chg{display:flex;gap:10px;align-items:flex-start;padding:6px 14px}
.chg .mark{font-weight:700;flex:none;width:16px;text-align:center}
.chg.app .mark{color:var(--warn)}.chg.gone .mark{color:var(--ok)}
.chg code{font:12.5px/1.5 ui-monospace,Consolas,monospace;background:#0e1218;border:1px solid var(--edge);
  border-radius:4px;padding:1px 6px;word-break:break-word}
.none{color:var(--dim);padding:8px 0}
.picker{margin:0 0 18px;color:var(--dim)}
.picker select{background:var(--panel);color:var(--ink);border:1px solid var(--edge);border-radius:6px;padding:4px 8px;font:inherit}
.empty{padding:40px 26px;color:var(--dim)}
";

        private const string Script = @"
(function(){
  var data = (window.ATLAS_INDEX && window.ATLAS_INDEX.scans) || [];
  var main = document.getElementById('main');
  var list = document.getElementById('scan-list');
  var changesBtn = document.querySelector('.nav-changes');

  function vClass(v){ v=(v||'').toUpperCase();
    return v==='CLEAN'?'clean':v==='ATTENTION'?'attention':v==='PROBLEM'?'problem':''; }

  function clearActive(){ document.querySelectorAll('.nav-item.active').forEach(function(n){n.classList.remove('active');}); }

  // Sidebar scan rows.
  data.forEach(function(s, i){
    var b = document.createElement('button');
    b.className='nav-item scan-item';
    var row=document.createElement('div'); row.className='scan-row';
    var dot=document.createElement('span'); dot.className='dot '+vClass(s.verdict); row.appendChild(dot);
    var t=document.createElement('span'); t.className='scan-time'; t.textContent=s.time||s.file; row.appendChild(t);
    b.appendChild(row);
    var sub=document.createElement('div'); sub.className='scan-sub';
    sub.textContent=(s.verdict||'').toLowerCase()+'  ·  '+s.h+'/'+s.m+'/'+s.l+' H/M/L';
    b.appendChild(sub);
    b.addEventListener('click', function(){ clearActive(); b.classList.add('active'); showScan(i); });
    list.appendChild(b);
  });

  function showScan(i){
    var s=data[i]; if(!s) return;
    main.innerHTML='';
    var f=document.createElement('iframe'); f.setAttribute('src', s.file); f.setAttribute('title','scan'); main.appendChild(f);
  }

  var CATS=[['conflict','Conflicts'],['compat','Compatibility'],['drift','Update impact (drift)'],
            ['patch','Patch verification'],['load','Load failures'],['dep','Dependencies'],['overlap','Keybind overlaps']];

  function diff(a,b){
    var out=[];
    CATS.forEach(function(c){
      var ak=(a.keys&&a.keys[c[0]])||[], bk=(b.keys&&b.keys[c[0]])||[];
      var app=ak.filter(function(x){return bk.indexOf(x)<0;});
      var gone=bk.filter(function(x){return ak.indexOf(x)<0;});
      if(app.length||gone.length) out.push({label:c[1],app:app,gone:gone});
    });
    return out;
  }

  function rowsFor(container, cls, mark, items){
    items.forEach(function(x){
      var d=document.createElement('div'); d.className='chg '+cls;
      var m=document.createElement('span'); m.className='mark'; m.textContent=mark; d.appendChild(m);
      var code=document.createElement('code'); code.textContent=x; d.appendChild(code);
      container.appendChild(d);
    });
  }

  function showChanges(baseIdx){
    clearActive(); changesBtn.classList.add('active');
    main.innerHTML='';
    var wrap=document.createElement('div'); wrap.className='changes';
    if(data.length<2){
      wrap.innerHTML='';
      var h=document.createElement('h1'); h.textContent='Changes'; wrap.appendChild(h);
      var p=document.createElement('p'); p.className='empty';
      p.textContent=data.length===1?'Only one scan so far — nothing to compare against yet.':'No scans yet.';
      wrap.appendChild(p); main.appendChild(wrap); return;
    }
    var a=data[0], b=data[baseIdx];

    var h=document.createElement('h1'); h.textContent='Changes'; wrap.appendChild(h);
    var lead=document.createElement('p'); lead.className='lead';
    lead.textContent='Newest scan ('+(a.time||a.file)+') compared with '+(b.time||b.file)+'. Within one session the load and end scans differ mainly by the content surface — pick an earlier session below for a session-over-session view.';
    wrap.appendChild(lead);

    // baseline picker
    var pk=document.createElement('div'); pk.className='picker';
    pk.appendChild(document.createTextNode('Compare against: '));
    var sel=document.createElement('select');
    for(var i=1;i<data.length;i++){
      var o=document.createElement('option'); o.value=i; o.textContent=data[i].time||data[i].file;
      if(i===baseIdx) o.selected=true; sel.appendChild(o);
    }
    sel.addEventListener('change', function(){ showChanges(parseInt(sel.value,10)); });
    pk.appendChild(sel); wrap.appendChild(pk);

    // verdict change
    var vl=document.createElement('div'); vl.className='verdict-line';
    vl.textContent = a.verdict===b.verdict
      ? ('Verdict unchanged: '+a.verdict)
      : ('Verdict: '+b.verdict+'  →  '+a.verdict);
    wrap.appendChild(vl);

    var d=diff(a,b);
    if(d.length===0){
      var none=document.createElement('div'); none.className='none';
      none.textContent='Nothing changed in the tracked categories between these two scans.';
      wrap.appendChild(none);
    } else {
      d.forEach(function(cat){
        var box=document.createElement('div'); box.className='cat';
        var ch=document.createElement('h2'); ch.textContent=cat.label; box.appendChild(ch);
        rowsFor(box,'app','+',cat.app);   // appeared since the baseline (attention)
        rowsFor(box,'gone','−',cat.gone); // gone since the baseline (resolved)
        wrap.appendChild(box);
      });
    }
    main.appendChild(wrap);
  }

  changesBtn.addEventListener('click', function(){ showChanges(1); });

  // Default view: Changes if we can diff, else the newest scan.
  if(data.length>=2) showChanges(1);
  else if(data.length===1){ list.firstChild.classList.add('active'); showScan(0); }
  else { main.innerHTML='<div class=\""empty\"">No scans yet.</div>'; }
})();
";
    }
}
