using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ATLAS
{
    /// <summary>
    /// The in-game overlay (0.8.0). A summon-key window that reviews the latest scan and lets the
    /// user set exceptions right there - the mod has file access, so a decision is written to
    /// decisions.tsv the instant it is made, no browser or download in the loop.
    ///
    /// Deliberately draws its own IMGUI window instead of patching the game's menus: an overlay is
    /// scene-agnostic (so it works at the main menu AND in-game for free), and nothing it does can
    /// be broken by a game update - which would be a poor look for the mod that detects exactly that.
    ///
    /// State changes are deferred to the top of the next OnGUI pass. IMGUI runs the window function
    /// several times per frame (Layout, then Repaint, plus input events); mutating the visible item
    /// set mid-pass throws "mismatched LayoutGroup", so a click - whether a decision or a section
    /// collapse - only records an Action that is applied before the next pass begins.
    /// </summary>
    internal sealed class AtlasPanel : MonoBehaviour
    {
        private const int WinId = 0x0A71A5;

        private bool _open;
        private bool _sized;
        private bool _resizing;
        private int _fontBuilt = -1;
        private Rect _win = new Rect(48, 48, 760, 580);
        private Vector2 _scroll;
        private string _path = "";
        private DecisionSet _dec = new DecisionSet();
        private Action? _pending;
        private string _status = "";

        // Support bundle (0.15.0). The build runs on a background thread; _bundleBuilding gates the
        // button and is written from that thread, so it is volatile. _bundle mirrors the latest build
        // for the facts line; _bundleScope is the panel's export override (starts from config).
        private volatile bool _bundleBuilding;
        private SupportBundle.BundleInfo? _bundle;
        private BundleScope _bundleScope = BundleScope.Last3;

        // Section title -> expanded. Absent means expanded (default open).
        private readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>();

        // Game-input suspension while open.
        private InputActionAsset? _asset;
        private readonly List<InputActionMap> _restore = new List<InputActionMap>();

        // Cursor suspension while open. The panel forces the cursor free (visible + unlocked) every
        // OnGUI pass so its buttons are clickable; these remember what gameplay had so closing can
        // put it back. Captured, not hardcoded to hide+lock, so opening the panel at the main menu -
        // where the cursor is legitimately visible - restores a visible cursor, not a hidden one.
        private bool _cursorSaved;
        private CursorLockMode _savedCursorLock;
        private bool _savedCursorVisible;

        private GUIStyle? _hintStyle;
        private GUIStyle? _sectionStyle;
        private GUIStyle? _warnStyle;
        private GUIStyle? _row;
        private GUIStyle? _btn;

        private void Awake()
        {
            _path = Decisions.PathIn(Paths.BepInExRootPath);
            _dec = Decisions.Load(_path);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            var key = Plugin.CfgPanelKey.Value;
            if (key != Key.None && kb[key].wasPressedThisFrame) Toggle();
            else if (_open && kb[Key.Escape].wasPressedThisFrame) Close();

            // While open, keep the game's input suspended even if the game re-enables a map.
            if (_open) MaintainBlock();
        }

        private void OnDisable() => RestorePanelState();   // never leave the game with input off or a stray cursor
        private void OnDestroy() => RestorePanelState();

        private void Toggle() { if (_open) Close(); else Open(); }

        private void Open()
        {
            _open = true;
            _dec = Decisions.Load(_path);   // pick up any hand edits
            _status = "";
            _bundle = Plugin.Instance?.LastBundleInfo;   // reflect the newest build in the facts line
            _bundleScope = Plugin.CfgBundleScope.Value;
            CaptureCursor();   // before OnGUI forces the cursor free this frame
            CaptureAndBlock();
        }

        private void Close()
        {
            _open = false;
            RestorePanelState();
        }

        /// <summary>Undoes everything opening the panel changed: game input, and the cursor.</summary>
        private void RestorePanelState()
        {
            RestoreInput();
            RestoreCursor();
            SaveWindowRect();   // remember where the user left the window
        }

        // ── window geometry persistence ──────────────────────────────────────────────────

        /// <summary>
        /// Parses the remembered "x,y,width,height" into a rect, clamped so a stale value cannot
        /// hurt: size is held to the same [480+ , 300+] minimums the resize grip enforces, and the
        /// position is kept far enough on-screen that the drag bar is always reachable - which
        /// matters after a resolution change, when a saved rect could otherwise land off-screen.
        /// Returns false (so the caller centres at the default) when nothing is saved or it does
        /// not parse. InvariantCulture on both ends so the comma-separated value never collides
        /// with a locale that uses a comma decimal separator.
        /// </summary>
        private static bool TryLoadWindowRect(out Rect rect)
        {
            rect = default;
            var raw = Plugin.CfgPanelWindowRect.Value;
            if (string.IsNullOrEmpty(raw)) return false;

            var p = raw.Split(',');
            if (p.Length != 4) return false;

            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return false;
            if (!float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return false;

            w = Mathf.Clamp(w, 480f, Screen.width);
            h = Mathf.Clamp(h, 300f, Screen.height);
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 60f));   // keep the title/drag bar on-screen
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 30f));
            rect = new Rect(x, y, w, h);
            return true;
        }

        /// <summary>
        /// Writes the current window rect back to config so the next session restores it. Only when
        /// the window has actually been placed this session, and only when the value changed, so
        /// closing the panel without moving it does not churn the .cfg on disk.
        /// </summary>
        private void SaveWindowRect()
        {
            if (!_sized) return;
            var s = string.Format(CultureInfo.InvariantCulture, "{0:0.#},{1:0.#},{2:0.#},{3:0.#}",
                                  _win.x, _win.y, _win.width, _win.height);
            if (Plugin.CfgPanelWindowRect.Value != s) Plugin.CfgPanelWindowRect.Value = s;
        }

        private void OnGUI()
        {
            // Apply a queued change before any layout runs, so every pass sees a stable item set.
            if (_pending != null) { var p = _pending; _pending = null; try { p(); } catch (Exception ex) { _status = "error: " + ex.Message; } }

            EnsureStyles();

            if (!_open)
            {
                if (Plugin.CfgPanelHint.Value)
                {
                    var hint = Plugin.CfgPanelKey.Value + " — ATLAS";
                    GUI.Label(new Rect(Screen.width - 168, Screen.height - 26, 160, 22), hint, _hintStyle);
                }
                return;
            }

            // Free the cursor while the panel is up so its buttons are clickable in-game.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (!_sized)
            {
                // A remembered rect (from a previous session) wins; otherwise centre at a default
                // size. Done here rather than in Awake because it needs the live Screen dimensions.
                if (!TryLoadWindowRect(out _win))
                {
                    var w = Mathf.Min(1200f, Screen.width - 80f);
                    var h = Screen.height - 90f;
                    _win = new Rect((Screen.width - w) * 0.5f, 45f, w, h);
                }
                _sized = true;
            }

            // Drag-to-resize from the bottom-right grip. Handled here (before the window draws and
            // its events fire), so the mouse drag resizes rather than dragging the window.
            var e = Event.current;
            var grip = new Rect(_win.xMax - 22, _win.yMax - 22, 22, 22);
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { _resizing = true; e.Use(); }
            else if (e.type == EventType.MouseUp) { _resizing = false; }
            else if (e.type == EventType.MouseDrag && _resizing)
            {
                _win.width = Mathf.Clamp(_win.width + e.delta.x, 480f, Screen.width);
                _win.height = Mathf.Clamp(_win.height + e.delta.y, 300f, Screen.height);
                e.Use();
            }

            _win = GUI.Window(WinId, _win, DrawWindow, "ATLAS  v" + Plugin.Ver + "  —  scan review");
            GUI.Box(new Rect(_win.xMax - 22, _win.yMax - 22, 22, 22), "◢");
        }

        private void DrawWindow(int id)
        {
            var r = Plugin.Instance?.LastReport;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rescan", _btn, GUILayout.Width(100)))
            {
                Plugin.Instance?.Scan("panel");
                _dec = Decisions.Load(_path);
                _status = "rescanned.";
            }
            if (GUILayout.Button("Reset exceptions", _btn, GUILayout.Width(160)))
                _pending = () => { Decisions.Clear(_path); _dec = new DecisionSet(); _status = "all exceptions cleared."; };
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", _btn, GUILayout.Width(90))) Close();
            GUILayout.EndHorizontal();

            if (r == null)
            {
                GUILayout.Space(10);
                GUILayout.Label("No scan yet. Load a save (a scan runs on save load), or press Rescan.", _row);
                GUI.DragWindow(new Rect(0, 0, 100000, 22));
                return;
            }

            GUILayout.Label(Summary(r), _row);

            var conflict = KeyConflict(r);
            if (conflict != null) GUILayout.Label("⚠  " + conflict, _warnStyle);

            GUILayout.Space(4);
            // Fixed-height scroll body: the window no longer auto-grows, so long reports scroll
            // instead of running off the screen. Height tracks the (resizable) window.
            var bodyH = Mathf.Max(140f, _win.height - (conflict != null ? 172f : 150f));
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(bodyH));

            Conflicts(r);
            Drift(r);
            Compat(r);
            PatchVerify(r);
            Coverage(r);
            LogActivitySection(r);
            Overlaps(r);
            Malformed(r);
            Orphans(r);
            IgnoredSection(r);
            BundleSection(r);

            GUILayout.EndScrollView();

            if (_status.Length > 0) { GUILayout.Space(4); GUILayout.Label(_status, _row); }
            GUILayout.Label("Decisions are written to decisions.tsv immediately; a rescan reflects them fully.", _hintStyle);

            GUI.DragWindow(new Rect(0, 0, 100000, 22));
        }

        // ── collapsible section header ───────────────────────────────────────────────────

        /// <summary>Draws a foldout header and returns whether the body should be drawn. Sections
        /// default to open; pass defaultOpen:false for one that should start collapsed (its state is
        /// still remembered once the user toggles it).</summary>
        private bool Section(string title, int count, bool defaultOpen = true)
        {
            var open = _expanded.TryGetValue(title, out var v) ? v : defaultOpen;
            GUILayout.Space(6);
            if (GUILayout.Button((open ? "▼  " : "▶  ") + title + "   (" + count + ")", _sectionStyle))
            {
                var t = title; var cur = open;
                _pending = () => _expanded[t] = !cur;   // deferred, to avoid a mid-pass layout change
            }
            return open;
        }

        // ── sections ─────────────────────────────────────────────────────────────────────

        private void Conflicts(ScanReport r)
        {
            var n = 0;
            foreach (var c in r.Conflicts) if (!_dec.Ignored.Contains(Decisions.ConflictKey(c.Method))) n++;
            if (!Section("Conflicts", n)) return;
            if (n == 0) { Empty("no conflicts"); return; }

            foreach (var c in r.Conflicts)
            {
                var key = Decisions.ConflictKey(c.Method);
                if (_dec.Ignored.Contains(key)) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"[{Sev(c.Severity)}] {c.Method}", _row);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Approve", _btn, GUILayout.Width(90)))
                    _pending = () => Ignore(key, "approved conflict " + c.Method);
                GUILayout.EndHorizontal();

                // Execution order (0.12.0): whose patch runs first.
                if (c.Order.Count > 0)
                {
                    var parts = new List<string>(c.Order.Count);
                    foreach (var s in c.Order) parts.Add(s.Owner + " (" + s.Kind + ")");
                    var line = "    order: " + string.Join(" -> ", parts.ToArray());
                    if (c.HasOrderingConstraints) line += "   (before/after set; may differ)";
                    GUILayout.Label(line, _hintStyle);
                }
            }
        }

        private void Drift(ScanReport r)
        {
            var n = 0;
            foreach (var f in r.DriftFindings) if (f.Kind != DriftKind.NotTracked) n++;
            if (!Section("Update impact", n)) return;
            if (n == 0) { Empty("nothing open"); return; }

            foreach (var f in r.DriftFindings)
            {
                if (f.Kind == DriftKind.NotTracked) continue;
                var owners = f.Owners.Count > 0 ? "  (" + string.Join(", ", f.Owners.ToArray()) + ")" : "";
                GUILayout.Label($"[{f.Status}] {f.Member}{owners}", _row);
            }
        }

        // Mod compatibility (0.10.0): the baselineless axis, kept separate from Update impact. Only
        // drawn when the check actually ran, so an off/unavailable check leaves no empty section.
        private void Compat(ScanReport r)
        {
            if (!r.CompatChecked) return;
            if (!Section("Mod compatibility", r.CompatFindings.Count)) return;
            if (r.CompatFindings.Count == 0) { Empty("all mod hooks resolve against this build"); return; }

            foreach (var f in r.CompatFindings)
            {
                var owners = f.Owners.Count > 0 ? "  (" + string.Join(", ", f.Owners.ToArray()) + ")" : "";
                var origin = f.Origin == DriftOrigin.Reflection ? "reflection" : "patch";
                GUILayout.Label($"[{origin}] {f.Member}{owners}", _row);
            }
        }

        // Patch verification (0.11.0): runtime truth — did declared patches apply, did every mod
        // load? Only drawn when the check ran. Load failures and confirmed not-applied lead; the
        // often-benign unconfirmed rows follow.
        private void PatchVerify(ScanReport r)
        {
            if (!r.PatchCheckRan) return;
            var n = r.PluginLoadFailures.Count + r.PatchApplyFindings.Count;
            if (!Section("Patch verification", n)) return;
            if (n == 0)
            {
                Empty(r.PatchDeclaredChecked > 0
                    ? $"{r.PatchAppliedVerified}/{r.PatchDeclaredChecked} patches applied, all mods loaded"
                    : "no declared patches to check");
                return;
            }

            foreach (var lf in r.PluginLoadFailures)
                GUILayout.Label("[FAILED TO LOAD] " + lf.Plugin, _warnStyle);

            foreach (var f in r.PatchApplyFindings)
            {
                var owners = f.Owners.Count > 0 ? "  (" + string.Join(", ", f.Owners.ToArray()) + ")" : "";
                var tag = f.LogCorroborated ? "[NOT APPLIED]" : "[unconfirmed]";
                GUILayout.Label($"{tag} {f.Member}{owners}", f.LogCorroborated ? _warnStyle : _row);
            }
        }

        // Analysis coverage (0.12.0): per-mod static visibility. Informational — the partial mods
        // (the ones ATLAS is blind on) are what's worth seeing, so only those are listed.
        private void Coverage(ScanReport r)
        {
            if (!r.CoverageChecked || r.ModCoverages.Count == 0) return;
            if (!Section("Analysis coverage", r.CoveragePartialMods)) return;
            if (r.CoveragePartialMods == 0)
            {
                Empty($"all {r.ModCoverages.Count} mods fully visible");
                return;
            }

            GUILayout.Label($"{r.CoverageFullyVisibleMods}/{r.ModCoverages.Count} fully visible; these have hooks ATLAS can't verify:", _hintStyle);
            foreach (var c in r.ModCoverages)
            {
                if (c.FullyVisible) continue;
                GUILayout.Label($"  {c.Mod}: {c.Unresolved} not resolvable ({c.PatchResolved}+{c.ReflectionResolved} resolved)", _row);
            }
        }

        // Log activity (0.14.0): recurring-vs-one-off summary of the archived logs. Compact — the
        // standing (consistently firing) signatures plus the noisiest namespace.
        private void LogActivitySection(ScanReport r)
        {
            var la = r.LogActivity;
            if (la == null || !la.Analyzed) return;
            if (!Section("Log activity", la.ConsistentTotal, defaultOpen: false)) return;
            if (la.TotalEvents == 0) { Empty("no errors/warnings in the kept logs"); return; }

            GUILayout.Label($"{la.TotalEvents} events across {la.LogsScanned} logs.", _hintStyle);
            if (la.Consistent.Count == 0)
                GUILayout.Label($"Nothing recurring across sessions; {la.SituationalTotal} situational.", _row);
            var shown = 0;
            foreach (var g in la.Consistent)
            {
                var src = g.Source.Length > 0 ? " <" + g.Source + ">" : "";
                GUILayout.Label($"[{g.Level}]{src} {g.Label}   ×{g.Count} / {g.SessionCount} sess", _row);
                if (++shown >= 6) break;
            }
            if (la.Noisy.Count > 0)
                GUILayout.Label($"Noisiest source: {la.Noisy[0].Source} ({la.Noisy[0].Count})", _hintStyle);
        }

        private void Overlaps(ScanReport r)
        {
            var n = 0;
            foreach (var o in r.BindOverlaps) if (!_dec.Ignored.Contains(Decisions.OverlapKey(o.IsController, o.Control))) n++;
            if (!Section("Keybind overlaps", n)) return;
            if (n == 0) { Empty("no overlaps"); return; }

            foreach (var o in r.BindOverlaps)
            {
                var key = Decisions.OverlapKey(o.IsController, o.Control);
                if (_dec.Ignored.Contains(key)) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label((o.IsController ? "[PAD] " : "[KEY] ") + o.Control + "  " + Join(o.Binds), _row);
                // Runtime-confirmed overlap (item 2): flag it in the warn colour, in place.
                if (o.Confirmed)
                    GUILayout.Label("[CONFIRMED " + o.ConfirmedCount + "×]", _warnStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Ignore", _btn, GUILayout.Width(90)))
                    _pending = () => Ignore(key, "ignored overlap " + o.Control);
                GUILayout.EndHorizontal();
            }
        }

        private void Malformed(ScanReport r)
        {
            var n = 0;
            foreach (var m in r.MalformedBinds) if (!_dec.Ignored.Contains(Decisions.MalformedKey(m))) n++;
            if (!Section("Malformed bindings", n)) return;
            if (n == 0) { Empty("none"); return; }

            foreach (var m in r.MalformedBinds)
            {
                var key = Decisions.MalformedKey(m);
                if (_dec.Ignored.Contains(key)) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(m, _row);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Ignore", _btn, GUILayout.Width(90)))
                    _pending = () => Ignore(key, "ignored malformed bind");
                GUILayout.EndHorizontal();
            }
        }

        private void Orphans(ScanReport r)
        {
            if (!Section("Leftover configs", r.OrphanedConfigs.Count)) return;
            if (r.OrphanedConfigs.Count == 0) { Empty("none"); return; }

            foreach (var o in r.OrphanedConfigs)
            {
                var queued = _dec.DeleteConfigs.Contains(o);
                GUILayout.BeginHorizontal();
                GUILayout.Label(o + ".cfg" + (queued ? "   (queued for deletion next launch)" : ""), _row);
                GUILayout.FlexibleSpace();
                if (!queued && GUILayout.Button("Delete", _btn, GUILayout.Width(90)))
                {
                    var guid = o;
                    _pending = () =>
                    {
                        if (!_dec.DeleteConfigs.Contains(guid)) _dec.DeleteConfigs.Add(guid);
                        Decisions.Write(_path, _dec);
                        _status = "queued " + guid + ".cfg for deletion.";
                    };
                }
                GUILayout.EndHorizontal();
            }
        }

        private void IgnoredSection(ScanReport r)
        {
            var rows = new List<(string cat, string label, string key)>();
            foreach (var it in r.IgnoredItems) rows.Add((it.Category, it.Label, it.Key));
            foreach (var c in r.Conflicts)
            {
                var key = Decisions.ConflictKey(c.Method);
                if (_dec.Ignored.Contains(key) && !InReport(r, key)) rows.Add(("conflict", c.Method, key));
            }
            foreach (var o in r.BindOverlaps)
            {
                var key = Decisions.OverlapKey(o.IsController, o.Control);
                if (_dec.Ignored.Contains(key) && !InReport(r, key)) rows.Add(("overlap", (o.IsController ? "[PAD] " : "[KEY] ") + o.Control, key));
            }
            foreach (var m in r.MalformedBinds)
            {
                var key = Decisions.MalformedKey(m);
                if (_dec.Ignored.Contains(key) && !InReport(r, key)) rows.Add(("malformed", m, key));
            }

            if (!Section("Ignored", rows.Count)) return;
            if (rows.Count == 0) { Empty("nothing set aside"); return; }

            foreach (var row in rows)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("[" + row.cat + "] " + row.label, _row);
                GUILayout.FlexibleSpace();
                var key = row.key;
                if (GUILayout.Button("Restore", _btn, GUILayout.Width(90)))
                    _pending = () => { _dec.Ignored.Remove(key); Decisions.Write(_path, _dec); _status = "restored."; };
                GUILayout.EndHorizontal();
            }
        }

        // Support bundle (0.15.0): on-demand rebuild with a scope override, plus the two things a
        // file:// HTML page cannot do — copy the path to the clipboard, and open the containing folder.
        // The build runs off the main thread so the game does not hitch; the button shows Building… and
        // disables until it returns.
        private void BundleSection(ScanReport r)
        {
            var info = _bundle ?? Plugin.Instance?.LastBundleInfo;
            if (!Section("Support bundle", info != null && info.Built ? 1 : 0, defaultOpen: false)) return;

            if (_bundleBuilding)
                GUILayout.Label("   building…", _row);
            else if (info != null && info.Built)
                GUILayout.Label("   " + SupportBundle.FactsLine(info), _row);
            else if (info != null && info.Error.Length > 0)
                GUILayout.Label("   last build failed: " + info.Error, _warnStyle);
            else
                GUILayout.Label("   no bundle built yet — Export to write one.", _row);

            GUILayout.BeginHorizontal();
            var exportLbl = _bundleBuilding
                ? "Building…"
                : (Plugin.CfgBundleRedact.Value ? "Export bundle (redacted)" : "Export bundle (NOT redacted)");
            GUI.enabled = !_bundleBuilding;
            if (GUILayout.Button(exportLbl, _btn, GUILayout.Width(250)))
                _pending = StartBundleBuild;   // deferred: starting the build alters the visible button label
            if (GUILayout.Button("Scope: " + _bundleScope, _btn, GUILayout.Width(150)))
                _bundleScope = NextScope(_bundleScope);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy path", _btn, GUILayout.Width(120)))
            {
                var p = info != null && info.Path.Length > 0
                    ? info.Path
                    : System.IO.Path.Combine(Plugin.Instance?.ScanDir ?? "", SupportBundle.FileName);
                GUIUtility.systemCopyBuffer = p;
                _status = "bundle path copied to clipboard.";
            }
            if (GUILayout.Button("Open folder", _btn, GUILayout.Width(120)))
            {
                var dir = Plugin.Instance?.ScanDir ?? "";
                if (dir.Length > 0) { Application.OpenURL("file://" + dir); _status = "opened the scans folder."; }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Redaction is best-effort — review the zip before sharing. Set Bundle.ExtraRedactions "
                          + "for anything ATLAS can't know to remove (a gamertag, a session name).", _hintStyle);
        }

        /// <summary>Kicks a build off on a background thread. Deferred via _pending so the button-label
        /// change does not alter the item set mid-pass.</summary>
        private void StartBundleBuild()
        {
            var plugin = Plugin.Instance;
            var r = plugin?.LastReport;
            if (plugin == null || r == null) { _status = "no scan yet — press Rescan first."; return; }
            if (_bundleBuilding) return;

            _bundleBuilding = true;
            _status = "building bundle (" + _bundleScope + ")…";
            var scope = _bundleScope;
            var scanDir = plugin.ScanDir;
            var archiveDir = plugin.ArchiveDir;

            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var opt = plugin.MakeBundleOptions(r, scope);
                    var built = SupportBundle.Build(scanDir, archiveDir, r, opt);
                    plugin.SetLastBundle(built);
                    _bundle = built;
                    _status = built.Built
                        ? "bundle built: " + SupportBundle.HumanSize(built.Bytes)
                        : "bundle failed: " + built.Error;
                }
                catch (Exception ex) { _status = "bundle error: " + ex.Message; }
                finally { _bundleBuilding = false; }
            })
            { IsBackground = true, Name = "ATLAS-bundle-panel" };
            t.Start();
        }

        private static BundleScope NextScope(BundleScope s) =>
            s == BundleScope.Session ? BundleScope.Last3
            : s == BundleScope.Last3 ? BundleScope.All
            : BundleScope.Session;

        // ── game-input suspension ────────────────────────────────────────────────────────

        /// <summary>
        /// The game's live InputActionAsset, reached the same way the keybind scanner reaches it
        /// (SpaceCraft.BindingTextTranslator.Instance.actions). Null at the main menu, where there
        /// is no player input to suspend anyway. Re-attempts until found rather than caching null.
        /// </summary>
        private InputActionAsset? GameInputAsset()
        {
            if (_asset != null) return _asset;
            try
            {
                var t = AccessTools.TypeByName("SpaceCraft.BindingTextTranslator");
                var inst = t?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var f = t?.GetField("actions", BindingFlags.NonPublic | BindingFlags.Instance);
                _asset = f?.GetValue(inst) as InputActionAsset;
            }
            catch { /* not resolvable yet */ }
            return _asset;
        }

        /// <summary>Records the action maps that were live and disables them, to be restored on close.</summary>
        private void CaptureAndBlock()
        {
            if (!Plugin.CfgPanelBlockInput.Value) return;
            var asset = GameInputAsset();
            if (asset == null) return;
            try
            {
                _restore.Clear();
                foreach (var map in asset.actionMaps) if (map.enabled) _restore.Add(map);
                foreach (var map in _restore) map.Disable();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS panel: could not suspend game input: " + ex.Message); }
        }

        /// <summary>Re-disables any map the game re-enabled while the panel is open.</summary>
        private void MaintainBlock()
        {
            if (!Plugin.CfgPanelBlockInput.Value || _asset == null) return;
            try { foreach (var map in _asset.actionMaps) if (map.enabled) map.Disable(); }
            catch { }
        }

        /// <summary>
        /// Remembers the cursor's lock+visibility before the panel forces it free. Without this the
        /// cursor is left visible after the panel closes: OnGUI sets Cursor.visible every pass the
        /// panel is open, but the game only re-asserts cursor state on its own screen transitions, so
        /// nothing hides it again when the overlay goes away.
        /// </summary>
        private void CaptureCursor()
        {
            try
            {
                _savedCursorLock = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;
                _cursorSaved = true;
            }
            catch { _cursorSaved = false; }
        }

        /// <summary>Puts the cursor back exactly as gameplay (or the menu) had it before opening.</summary>
        private void RestoreCursor()
        {
            if (!_cursorSaved) return;
            _cursorSaved = false;
            try
            {
                Cursor.lockState = _savedCursorLock;
                Cursor.visible = _savedCursorVisible;
            }
            catch { }
        }

        /// <summary>Re-enables exactly the maps that were live when the panel opened.</summary>
        private void RestoreInput()
        {
            if (_restore.Count == 0) return;
            try { foreach (var map in _restore) { try { map.Enable(); } catch { } } }
            finally { _restore.Clear(); }
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────

        private void Ignore(string key, string msg)
        {
            _dec.Ignored.Add(key);
            Decisions.Write(_path, _dec);
            _status = msg + ".";
        }

        private static bool InReport(ScanReport r, string key)
        {
            foreach (var it in r.IgnoredItems) if (it.Key == key) return true;
            return false;
        }

        private string? KeyConflict(ScanReport r)
        {
            var kn = Plugin.CfgPanelKey.Value.ToString().ToUpperInvariant();
            foreach (var b in r.Binds)
            {
                if (b.IsController) continue;
                if (string.Equals(b.Control, kn, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(b.Owner, "ATLAS", StringComparison.OrdinalIgnoreCase))
                    return $"{kn} (your panel key) is also bound by {b.Owner}. Change Panel.ToggleKey if that clashes.";
            }
            return null;
        }

        private static string Summary(ScanReport r)
        {
            var s = $"Game {r.GameVersion}  ·  {r.PluginCount} plugins  ·  "
                  + $"conflicts {r.HighCount}/{r.MediumCount}/{r.LowCount} (H/M/L)  ·  "
                  + $"{r.BindOverlaps.Count} overlaps  ·  {r.MalformedBinds.Count} malformed  ·  "
                  + $"{r.MissingDependencies.Count} missing deps";
            if (r.DriftChecked) s += $"  ·  drift {r.DriftActiveCount} active / {r.DriftReviewCount} review";
            if (r.CompatChecked && r.CompatFindings.Count > 0) s += $"  ·  {r.CompatFindings.Count} incompatible";
            var patchFails = r.PatchApplyConfirmedCount + r.PluginLoadFailures.Count;
            if (r.PatchCheckRan && patchFails > 0) s += $"  ·  {patchFails} patch failure" + (patchFails == 1 ? "" : "s");
            return s;
        }

        private static string Join(List<BindRecord> binds)
        {
            var parts = new List<string>(binds.Count);
            foreach (var b in binds) parts.Add(b.Owner);
            return "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static string Sev(Severity s) =>
            s == Severity.High ? "HIGH" : s == Severity.Medium ? "MED" : s == Severity.Low ? "LOW" : "-";

        private void Empty(string what) => GUILayout.Label("   " + what + ".", _row);

        private void EnsureStyles()
        {
            var fs = Mathf.Clamp(Plugin.CfgPanelFontSize.Value, 10, 40);
            if (_row != null && _fontBuilt == fs) return;   // rebuild only when the size changes
            _fontBuilt = fs;

            _row = new GUIStyle(GUI.skin.label) { fontSize = fs, wordWrap = false };
            _btn = new GUIStyle(GUI.skin.button) { fontSize = Mathf.Max(11, fs - 1) };
            _sectionStyle = new GUIStyle(GUI.skin.button)
            { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = fs + 1 };
            _warnStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = fs };
            _warnStyle.normal.textColor = new Color(1f, 0.75f, 0.2f);
            _hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = Mathf.Max(11, fs - 3) };
            _hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.55f);
        }
    }
}
