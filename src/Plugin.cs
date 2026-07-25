using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace ATLAS
{
    [BepInPlugin(Guid, "ATLAS", Ver)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "davemuziek.planetcrafter.atlas";
        public const string Ver = "0.15.0";

        internal static Plugin? Instance;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> CfgEnabled = null!;
        internal static ConfigEntry<string> CfgDirectory = null!;
        internal static ConfigEntry<LogLevel> CfgMinLevel = null!;
        internal static ConfigEntry<int> CfgMaxErrorLogs = null!;
        internal static ConfigEntry<int> CfgMaxHealthyLogs = null!;
        internal static ConfigEntry<bool> CfgKeepHealthy = null!;
        internal static ConfigEntry<bool> CfgRollOnReturnToMenu = null!;
        internal static ConfigEntry<bool> CfgSeedFromExistingLog = null!;
        internal static ConfigEntry<bool> CfgCaptureUnityDirect = null!;
        internal static ConfigEntry<string> CfgInterestingNamespaces = null!;
        internal static ConfigEntry<string> CfgFrameworkNamespaces = null!;
        internal static ConfigEntry<string> CfgIgnoreErrorsContaining = null!;

        internal static ConfigEntry<bool> CfgPanelEnabled = null!;
        internal static ConfigEntry<Key> CfgPanelKey = null!;
        internal static ConfigEntry<bool> CfgPanelHint = null!;
        internal static ConfigEntry<bool> CfgPanelBlockInput = null!;
        internal static ConfigEntry<int> CfgPanelFontSize = null!;
        internal static ConfigEntry<string> CfgPanelWindowRect = null!;

        internal static ConfigEntry<bool> CfgScannerEnabled = null!;
        internal static ConfigEntry<bool> CfgScanOnGameLoad = null!;
        internal static ConfigEntry<bool> CfgScanWriteFile = null!;
        internal static ConfigEntry<bool> CfgScanWriteHtml = null!;
        internal static ConfigEntry<bool> CfgScanWriteIndex = null!;
        internal static ConfigEntry<int> CfgScanMaxReports = null!;
        internal static ConfigEntry<bool> CfgKeybindDiagnostics = null!;
        internal static ConfigEntry<bool> CfgLearnHardcodedKeys = null!;
        internal static ConfigEntry<bool> CfgVerifyPatchesApplied = null!;
        internal static ConfigEntry<bool> CfgLogActivitySummary = null!;

        internal static ConfigEntry<bool> CfgDriftEnabled = null!;
        internal static ConfigEntry<bool> CfgDriftAcceptCurrentBuild = null!;
        internal static ConfigEntry<bool> CfgDriftScanReflection = null!;
        internal static ConfigEntry<bool> CfgDriftContent = null!;
        internal static ConfigEntry<string> CfgDriftGameNamespaces = null!;
        internal static ConfigEntry<bool> CfgDriftCheckCompatibility = null!;

        internal static ConfigEntry<bool> CfgBundleAutoBuild = null!;
        internal static ConfigEntry<BundleScope> CfgBundleScope = null!;
        internal static ConfigEntry<bool> CfgBundleRedact = null!;
        internal static ConfigEntry<string> CfgBundleExtraRedactions = null!;
        internal static ConfigEntry<bool> CfgBundleIncludeModConfigs = null!;

        private ArchiveSession? _session;
        private ArchiveLogListener? _listener;
        private Harmony? _harmony;
        private bool _finalized;
        private bool _finalScanned;

        // Scanner uses the game's own scene constant (see SteamRichPresence, which compares
        // against GameConfig.mainSceneName with CompareOrdinal). A save load re-scans, which
        // also avoids the Input.GetKey() throw a keybind would hit under the New Input System.
        private const string MainSceneName = "GameMainScene";
        private bool _scannedThisScene;

        internal string ArchiveDir { get; private set; } = string.Empty;
        internal string ScanDir { get; private set; } = string.Empty;

        // The most recent scan, kept in memory so the in-game panel can render it and re-verify
        // decisions without re-scanning. Null until the first scan runs.
        internal ScanReport? LastReport { get; private set; }

        // The most recent support bundle build (0.15.0). The report and homepage render its facts and
        // link; the panel echoes it. Assigned from a background thread, read on the main thread — a
        // reference swap, so a reader sees either the old or new value, never a torn one.
        internal SupportBundle.BundleInfo? LastBundleInfo { get; private set; }
        internal void SetLastBundle(SupportBundle.BundleInfo info) => LastBundleInfo = info;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            BindConfig();

            if (!CfgEnabled.Value)
            {
                Log.LogInfo($"ATLAS BUILD ACTIVE v{Ver} - disabled by config");
                return;
            }

            // Everything ATLAS writes lives under one BepInEx/ATLAS folder - LogArchive, Scans,
            // Baseline, and observed_keys.tsv - so a user has one place to look. ArchiveDirectory
            // names the log subfolder under it.
            ArchiveDir = Path.Combine(Paths.BepInExRootPath, "ATLAS", CfgDirectory.Value);
            Directory.CreateDirectory(ArchiveDir);

            // Order matters: recover orphans BEFORE opening this session's own .active file,
            // so we never inspect our own in-progress log.
            OrphanRecovery.FinalizeOrphans(ArchiveDir, Log);
            Retention.Prune(ArchiveDir, CfgMaxErrorLogs.Value, CfgMaxHealthyLogs.Value, Log);

            _session = new ArchiveSession(ArchiveDir);
            if (CfgSeedFromExistingLog.Value) _session.SeedFromLiveLogOutput();

            _listener = new ArchiveLogListener(_session, CfgMinLevel.Value);
            BepInEx.Logging.Logger.Listeners.Add(_listener);

            if (CfgCaptureUnityDirect.Value)
                Application.logMessageReceivedThreaded += OnUnityLog;

            // Order matters: the final scan runs first (while the world is still up and on the main
            // thread), then the archive is finalized. ProcessExit deliberately does NOT scan - it can
            // fire on a finalizer thread where touching Unity would be unsafe.
            Application.quitting += FinalScanOnQuit;
            Application.quitting += FinalizeOnce;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            if (CfgRollOnReturnToMenu.Value)
            {
                _harmony = new Harmony(Guid);
                _harmony.PatchAll(typeof(SessionBoundaryPatch));
            }

            // ── Scanner subsystem ─────────────────────────────────────────────
            // Read-only. Shares the master switch but has its own on/off. Nothing here
            // writes to game or save state - a scan only reads BepInEx and Harmony metadata.
            if (CfgScannerEnabled.Value)
            {
                ScanDir = Path.Combine(Paths.BepInExRootPath, "ATLAS", "Scans");

                // Act on any abandoned-config deletions queued in decisions.tsv. Done here at
                // startup - deliberately outside the read-only scan - and processed exactly once,
                // before the first scan can list the config again. This is the only file ATLAS
                // deletes, and only ever a .cfg whose owning mod is not installed.
                try { Decisions.ProcessDeletions(Decisions.PathIn(Paths.BepInExRootPath), Paths.ConfigPath, Log); }
                catch (Exception ex) { Log.LogWarning("ATLAS: config-deletion pass failed: " + ex.Message); }

                // The in-game overlay. A component on ATLAS's own (DontDestroyOnLoad) object, so it
                // draws in every scene - main menu and in-game - with no game-UI patching.
                if (CfgPanelEnabled.Value)
                {
                    try { gameObject.AddComponent<AtlasPanel>(); }
                    catch (Exception ex) { Log.LogWarning("ATLAS: could not start the overlay: " + ex.Message); }
                }

                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;

                if (CfgLearnHardcodedKeys.Value)
                {
                    // Chainloader has finished by Awake, so every plugin assembly is present
                    // for attribution before the first key can be pressed.
                    KeyObserver.Start(Path.Combine(Paths.BepInExRootPath, "ATLAS"));
                    _harmony ??= new Harmony(Guid);

                    // Two independent capture paths. Either can fail to bind without taking
                    // the other down, so they are patched separately and reported separately.
                    TryPatch(typeof(ButtonReadPatch), "key press watcher");
                    TryPatch(typeof(KeyLookupPatch), "key lookup watcher");
                }
            }

            // ── Drift subsystem ───────────────────────────────────────────────────────
            // Independent on/off under the master switch. Part A (code) runs synchronously here
            // at Awake, when Assembly-CSharp.dll is on disk and no game state is needed. Part B
            // (content) needs a loaded world, so it hangs off a postfix on LoadStaticData.
            // Every drift entry point is wrapped, so a drift failure degrades to a state and logs
            // a warning rather than taking the scan - or the game - down with it.
            if (CfgDriftEnabled.Value)
            {
                DriftState.InitCode();

                if (CfgDriftContent.Value)
                {
                    _harmony ??= new Harmony(Guid);
                    TryPatch(typeof(StaticDataPatch), "drift content hook");
                }
            }

            Log.LogInfo($"ATLAS BUILD ACTIVE v{Ver} - archiving to {ArchiveDir}");
        }

        private void BindConfig()
        {
            CfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch. When off, no archiving happens at all.");
            CfgDirectory = Config.Bind("General", "ArchiveDirectory", "LogArchive",
                "Subfolder for archived logs, kept under BepInEx/ATLAS so all ATLAS output "
                + "(logs, scan reports, drift baselines) sits in one place.");
            CfgMinLevel = Config.Bind("General", "MinLevelToArchive", LogLevel.Info,
                "Lowest severity written to the archive. Debug produces very large files.");

            CfgMaxErrorLogs = Config.Bind("Retention", "MaxErrorLogs", 20,
                "How many logs containing errors to keep. Oldest are pruned at startup.");
            CfgMaxHealthyLogs = Config.Bind("Retention", "MaxHealthyLogs", 3,
                "How many clean logs to keep as rolling backups.");
            CfgKeepHealthy = Config.Bind("Retention", "KeepHealthyLogs", true,
                "When false, clean sessions are deleted on exit instead of archived.");

            CfgRollOnReturnToMenu = Config.Bind("Sessions", "RollOnReturnToMenu", true,
                "Finalize the archive and start a fresh one when quitting to the main menu, "
                + "so each archived log maps to one world rather than one process.");
            CfgSeedFromExistingLog = Config.Bind("Sessions", "SeedFromExistingLog", true,
                "Copy the current contents of LogOutput.log into the archive on startup, "
                + "so preloader and earlier-loading plugin lines are captured too.");

            CfgCaptureUnityDirect = Config.Bind("Capture", "CaptureUnityDirect", false,
                "Also subscribe to Unity's own log callback as a redundant second channel. "
                + "Normally unnecessary: BepInEx already forwards Unity messages inbound. "
                + "(This is NOT what 'Unable to start Unity log writer' means - that message "
                + "concerns the outbound direction, BepInEx writing into Unity's Player.log, "
                + "and does not affect capture.)");
            CfgInterestingNamespaces = Config.Bind("Capture", "InterestingNamespaces",
                "SpaceCraft.,ATLAS.,ARCHITECT.,NAVIGATOR.,FieldTerminal.",
                "Comma-separated stack frame prefixes preferred when naming a log after an "
                + "error. Matched case-insensitively.");
            CfgFrameworkNamespaces = Config.Bind("Capture", "FrameworkNamespaces",
                "UnityEngine.,Unity.,UnityEngineInternal.,System.,Mono.,HarmonyLib.,MonoMod.,DMD",
                "Comma-separated prefixes treated as noise when picking a frame to name a log "
                + "after. The top of a Unity stack is usually a framework throw helper.");
            CfgIgnoreErrorsContaining = Config.Bind("Capture", "IgnoreErrorsContaining",
                "Unable to start Unity log writer",
                "Pipe-separated substrings. Matching errors are still archived, but do not "
                + "count toward the ERR tag or the filename. The default entry is emitted by "
                + "BepInEx on the first line of every session on Unity 6 titles, so without "
                + "it no log would ever be tagged OK.");

            CfgScannerEnabled = Config.Bind("Scanner", "Enabled", true,
                "Enable the mod scanner: reports patch conflicts and missing dependencies. "
                + "Read-only; it never modifies game, save, or plugin state.");
            CfgScanOnGameLoad = Config.Bind("Scanner", "ScanOnGameLoad", true,
                "Re-scan automatically each time a save is loaded.");
            CfgScanWriteFile = Config.Bind("Scanner", "WriteReportFile", true,
                "Write a timestamped ModScan report to BepInEx/ATLAS/Scans on each scan.");
            CfgScanWriteHtml = Config.Bind("Scanner", "WriteHtmlReport", true,
                "Also write a browser-readable HTML version of each scan report alongside the text one.");
            CfgScanWriteIndex = Config.Bind("Scanner", "WriteScansIndex", true,
                "Also write a homepage (BepInEx/ATLAS/Scans/index.html) listing every kept scan newest "
                + "first, with a Changes view diffing the latest against an earlier one. Open it once and "
                + "it updates on each scan. Requires WriteHtmlReport (it opens the HTML scans in a frame).");
            CfgScanMaxReports = Config.Bind("Scanner", "MaxReports", 10,
                "How many scan reports to keep. Oldest are pruned after each scan.");
            CfgKeybindDiagnostics = Config.Bind("Scanner", "KeybindDiagnostics", false,
                "Log every config setting the keybind scanner considered and rejected, with "
                + "the reason. Use this when a binding you know exists is not being detected.");
            CfgLearnHardcodedKeys = Config.Bind("Scanner", "LearnHardcodedKeys", false,
                "Watch which mod polls which key or controller button at runtime, building up a "
                + "list of hardcoded bindings that no config file declares, and recording when two "
                + "mods read the same control on the same frame. This is the only ATLAS feature "
                + "that patches a frequently-called method, so it is off by default; the patch does "
                + "nothing unless a button is actually down.");
            CfgVerifyPatchesApplied = Config.Bind("Scanner", "VerifyPatchesApplied", true,
                "After every mod has loaded, check that each mod's declared Harmony patches actually "
                + "applied (against Harmony's live registry), and read the error logs for mods that "
                + "failed to load. Catches a patch that silently did not take even though its target "
                + "still exists - which the compatibility check, a static existence test, cannot see - "
                + "and names mods that never loaded and so are absent from the rest of the scan. "
                + "Reconciling declared patches needs the Drift code pass on; the log check does not.");
            CfgLogActivitySummary = Config.Bind("Scanner", "LogActivitySummary", true,
                "Summarise the archived logs: which errors/warnings recur across sessions (consistently "
                + "firing) versus fired once (situational), and which mods are noisiest. Read-only, over "
                + "the logs ATLAS already keeps. Diagnostic context; it never affects the verdict.");

            // ── Drift (update-breakage detection) ─────────────────────────────────────
            // Sits under the master General.Enabled, same as Scanner.Enabled. Read-only: reads
            // game assemblies and the live group list, writes only its own baseline files.
            CfgDriftEnabled = Config.Bind("Drift", "Enabled", true,
                "Compare the game's code and content against a recorded baseline, and report which "
                + "patched or reflected members changed. Read-only: it reads game assemblies and "
                + "writes only its own baseline files under BepInEx/ATLAS.");
            CfgDriftAcceptCurrentBuild = Config.Bind("Drift", "AcceptCurrentBuild", false,
                "One-shot. Set true and load a save to accept the current game build as the new "
                + "baseline, clearing outstanding findings and the resolved-once state. Resets itself "
                + "to false afterwards. Active findings self-heal when you fix the mod and re-scan; "
                + "this is mainly for the Review band - changes static analysis cannot confirm a fix "
                + "for - which persists until you accept, so an update cannot quietly stop being reported.");
            CfgDriftScanReflection = Config.Bind("Drift", "ScanReflection", true,
                "Also scan plugin assemblies for reflected member names (AccessTools.Field and "
                + "similar) and verify them against the game. Adds a one-off pass over the plugins "
                + "folder at startup.");
            CfgDriftContent = Config.Bind("Drift", "ScanContent", true,
                "Snapshot the group roster and key GroupData fields, to catch content updates that "
                + "add, remove, or recategorise items and constructibles.");
            CfgDriftGameNamespaces = Config.Bind("Drift", "GameNamespaces", "SpaceCraft.",
                "Comma-separated namespace prefixes treated as game code. Reflection targets outside "
                + "these are not verified.");
            CfgDriftCheckCompatibility = Config.Bind("Drift", "CheckModCompatibility", true,
                "Check every installed mod's Harmony patch targets and reflected members against the "
                + "game as it is installed right now, and report the ones that no longer exist. Unlike "
                + "the baseline comparison this needs no baseline and fires on a fresh install: it is "
                + "how an out-of-date mod that targets a since-removed or renamed game member is caught. "
                + "A mod whose targets all still exist is reported as fine, even if it is old. Reuses "
                + "the drift code pass's inputs, so it is nearly free when Drift is on.");

            // ── Support bundle (0.15.0) ───────────────────────────────────────────────
            // One scrubbed, self-describing zip a user can hand to a mod author or attach to a GitHub
            // issue - the whole diagnostic history, not a pasted fragment. Read-mostly and no-network:
            // it reuses the scans, logs and configs already on disk. A one-shot artifact, so it writes
            // beside the reports (the browser's download folder is the destination) and tracks nothing.
            CfgBundleAutoBuild = Config.Bind("Bundle", "AutoBuild", true,
                "Build ATLAS_support_bundle.zip alongside each scan (a single copy, overwritten each "
                + "time). Off means no zip is written and no download link is rendered; the in-game panel "
                + "can still build one on demand.");
            CfgBundleScope = Config.Bind("Bundle", "Scope", BundleScope.Last3,
                "How much history the bundle carries: Session (newest), Last3, or All. The full history is "
                + "the strength, but a large zip never gets hosted and GitHub caps attachments at 25 MB, so "
                + "Last3 is the honest default. The panel's export can override to All when an author asks.");
            CfgBundleRedact = Config.Bind("Bundle", "Redact", true,
                "Scrub usernames, profile paths, machine name, and Steam / multiplayer join IDs out of every "
                + "text file entering the zip. Best-effort by design - a mod that logs something personal in "
                + "a shape ATLAS does not recognise can slip through, so review before sharing.");
            CfgBundleExtraRedactions = Config.Bind("Bundle", "ExtraRedactions", "",
                "Comma-separated literals to also remove from the bundle (a gamertag, a session name - "
                + "anything you know about). Deliberately manual: auto-detecting these would mean reflecting "
                + "into live game state, which over-promises.");
            CfgBundleIncludeModConfigs = Config.Bind("Bundle", "IncludeModConfigs", true,
                "Include other mods' BepInEx .cfg files in the bundle (scrubbed). High triage value for an "
                + "author and small; turn off to keep configs out entirely.");

            // ── In-game panel ─────────────────────────────────────────────────────────
            // A summon-key overlay for reviewing the latest scan and setting exceptions in-game -
            // no browser, no download. It draws its own window rather than patching the game's UI,
            // so a game update cannot break how it opens.
            CfgPanelEnabled = Config.Bind("Panel", "Enabled", true,
                "Show the in-game ATLAS overlay. Summon it with the toggle key at the main menu or "
                + "in-game to review the latest scan and set exceptions. Read-only apart from decisions.tsv.");
            CfgPanelKey = Config.Bind("Panel", "ToggleKey", Key.F3,
                "Key that opens and closes the overlay (New Input System). Avoid F10/F12 (Windows "
                + "system key / Steam screenshot); ATLAS warns in the panel if this key is also bound "
                + "by the game or another mod.");
            CfgPanelHint = Config.Bind("Panel", "ShowHint", true,
                "Show a small corner hint naming the toggle key, so the overlay is discoverable "
                + "without an on-screen button.");
            CfgPanelBlockInput = Config.Bind("Panel", "BlockGameInput", true,
                "While the overlay is open, suspend the game's own input (movement, interaction, "
                + "look) so clicks and keys do not leak through to the world behind it. Restores it "
                + "on close. Turn off if it interferes with your setup.");
            CfgPanelFontSize = Config.Bind("Panel", "FontSize", 16,
                "Overlay text size in points. Raise it for readability on high-resolution displays.");
            CfgPanelWindowRect = Config.Bind("Panel", "WindowRect", "",
                "Remembered overlay window position and size as \"x,y,width,height\" in pixels. "
                + "Managed automatically - drag or resize the panel and it is saved when you close it. "
                + "Leave empty to let ATLAS centre and size it on first open. A saved rect that is "
                + "off-screen or too small (e.g. after a resolution change) is clamped back into a "
                + "usable range when loaded.");
        }

        /// <summary>
        /// Applies one patch class and says so. A capture path that fails to bind is the exact
        /// failure this feature had before: silent, and indistinguishable from "nobody pressed
        /// anything". Naming each one at startup makes that visible immediately.
        /// </summary>
        private void TryPatch(Type patchClass, string label)
        {
            try
            {
                _harmony!.PatchAll(patchClass);
                Log.LogInfo($"ATLAS {label}: installed.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"ATLAS {label}: FAILED to install - {ex.Message}");
            }
        }

        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            var text = string.IsNullOrEmpty(stackTrace) ? condition : condition + "\n" + stackTrace;
            _session?.WriteUnityDirect(text);
        }

        /// <summary>Close the current archive and immediately open a new one.</summary>
        internal void RollSession(string reason)
        {
            var closed = _session;
            if (closed == null) return;

            var next = new ArchiveSession(ArchiveDir);
            _session = next;
            _listener?.Retarget(next);

            var final = closed.FinalizeSession(false, CfgKeepHealthy.Value);
            if (final != null) Log.LogInfo($"Rolled archive ({reason}) -> {Path.GetFileName(final)}");
        }

        private void OnProcessExit(object sender, EventArgs e) => FinalizeOnce();

        // ── Scanner ───────────────────────────────────────────────────────────

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!CfgScanOnGameLoad.Value) return;
            if (string.CompareOrdinal(scene.name, MainSceneName) != 0) return;
            if (_scannedThisScene) return;

            _scannedThisScene = true;

            // The chainloader is long finished by now, unlike at Awake, so this is the first
            // moment every plugin assembly is guaranteed to be resolvable for attribution.
            if (CfgLearnHardcodedKeys.Value) KeyObserver.BuildAssemblyMap();

            Scan("game load");
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (string.CompareOrdinal(scene.name, MainSceneName) != 0) return;
            _scannedThisScene = false;

            // The load-time scan happens before the player can press anything, so runtime key
            // learning would never reach a report without a second pass. Leaving the world is
            // the natural end-of-session point and is a safe callback to do real work in.
            if (CfgLearnHardcodedKeys.Value) Scan("session end");
        }

        /// <summary>
        /// Run a scan and render it. Safe at any time - the whole pipeline is read-only.
        /// Internal so a later keybind or in-game panel can trigger it directly.
        /// </summary>
        internal void Scan(string reason)
        {
            try
            {
                var decisions = Decisions.Load(Decisions.PathIn(Paths.BepInExRootPath));
                var report = Scanner.Run(decisions);

                // Cross-reference against the log archive so conflicts that have actually
                // thrown are marked and lead the report. Only meaningful when archiving is on.
                if (CfgEnabled.Value && !string.IsNullOrEmpty(ArchiveDir))
                    ObservedConflicts.Apply(report, ArchiveDir);

                // Fold in the update-breakage comparison, immediately after the observed-conflict
                // cross-reference and before any renderer sees the report - the same seam pattern.
                if (CfgDriftEnabled.Value)
                    DriftState.ApplyTo(report);

                // Runtime patch verification (0.11.0): must run here, at scan time, not at Awake -
                // Harmony's applied-patch registry is only complete once every mod has loaded, and by
                // the scene-load / session-end scan it is. Deduped against the drift/compat findings
                // just applied above. Wrapped so a failure never takes the scan down.
                if (CfgVerifyPatchesApplied.Value)
                {
                    try { RuntimePatchCheck.Run(report, DriftState.DeclaredPatchTargets, ArchiveDir); }
                    catch (Exception ex) { Log.LogWarning("ATLAS patch-verify failed, scan otherwise intact: " + ex.Message); }
                }

                // Log-activity summary (0.14.0): step back over the archived logs — what recurs vs
                // what fired once, and who is noisiest. Needs archiving on (there are logs to read).
                if (CfgLogActivitySummary.Value && CfgEnabled.Value && !string.IsNullOrEmpty(ArchiveDir))
                {
                    try { report.LogActivity = LogActivity.Analyze(ArchiveDir); }
                    catch (Exception ex) { Log.LogWarning("ATLAS log activity failed, scan otherwise intact: " + ex.Message); }
                }

                if (CfgLearnHardcodedKeys.Value)
                    KeyObserver.Save();          // checkpoint, so a later crash keeps the data

                LastReport = report;             // hand the finished report to the in-game panel

                Log.LogInfo($"ATLAS scan ({reason}): {report.PluginCount} plugins, "
                          + $"{report.HighCount} high / {report.MediumCount} medium / {report.LowCount} low "
                          + $"conflicts, {report.MissingDependencies.Count} missing deps"
                          + (report.ArchiveChecked
                                ? $", {report.ObservedConflictCount} seen in {report.ArchiveLogCount} error logs."
                                : "."));

                if (CfgScanWriteFile.Value)
                {
                    var path = TextReportRenderer.Write(report, ScanDir, CfgScanMaxReports.Value);
                    Log.LogInfo("ATLAS scan report -> " + path);
                }

                if (CfgScanWriteHtml.Value)
                {
                    // The report and homepage state the previous build's bundle facts and link (this
                    // scan's bundle is written just below, after the HTML is on disk to be zipped). The
                    // fixed-name link always resolves to the freshest zip; only the facts line lags a scan.
                    var htmlPath = HtmlReportRenderer.Write(report, ScanDir, CfgScanMaxReports.Value,
                        CfgBundleAutoBuild.Value, LastBundleInfo);
                    Log.LogInfo("ATLAS scan report (html) -> " + htmlPath);

                    // Scans homepage (0.13.0): ledger the scan and rewrite index.html. Needs the html
                    // report (the index frames it), so it lives inside this branch. Wrapped so a
                    // homepage failure never takes the scan down.
                    if (CfgScanWriteIndex.Value)
                    {
                        try
                        {
                            var entry = ScanLedger.BuildEntry(report, Path.GetFileName(htmlPath),
                                HtmlReportRenderer.VerdictState(report), DateTime.UtcNow, DateTime.Now);
                            var entries = ScanLedger.Append(ScanDir, entry, CfgScanMaxReports.Value);
                            var indexPath = IndexRenderer.Write(ScanDir, entries,
                                CfgBundleAutoBuild.Value, LastBundleInfo);
                            Log.LogInfo("ATLAS scans homepage -> " + indexPath);
                        }
                        catch (Exception ex) { Log.LogWarning("ATLAS scans homepage failed, scan otherwise intact: " + ex.Message); }
                    }
                }

                // Support bundle (0.15.0): built AFTER the report + homepage are on disk (they are zipped
                // in). Pure file IO with no Unity calls, so it runs on a background thread and lets the
                // scan return - EXCEPT on the quit path, where a background thread would not finish before
                // teardown, so it builds synchronously (and skips an All-scope build that could be too slow
                // to finish, which would yield a truncated zip on exactly the session a crash made
                // interesting). Wrapped so a bundle failure never takes the scan down.
                if (CfgBundleAutoBuild.Value && !string.IsNullOrEmpty(ScanDir))
                {
                    try
                    {
                        var opt = MakeBundleOptions(report, CfgBundleScope.Value);
                        var onQuit = reason == "quit from game";
                        if (onQuit)
                        {
                            if (opt.Scope == BundleScope.All)
                                Log.LogInfo("ATLAS support bundle skipped on quit (Scope=All) to avoid a truncated zip during teardown.");
                            else
                                SetLastBundle(SupportBundle.Build(ScanDir, ArchiveDir, report, opt));
                        }
                        else
                        {
                            var scanDir = ScanDir; var archiveDir = ArchiveDir;
                            var t = new System.Threading.Thread(() =>
                            {
                                try { SetLastBundle(SupportBundle.Build(scanDir, archiveDir, report, opt)); }
                                catch (Exception ex) { Log.LogWarning("ATLAS support bundle thread failed: " + ex.Message); }
                            })
                            { IsBackground = true, Name = "ATLAS-bundle" };
                            t.Start();
                        }
                    }
                    catch (Exception ex) { Log.LogWarning("ATLAS support bundle setup failed, scan otherwise intact: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Log.LogError("ATLAS scan failed: " + ex);
            }
        }

        /// <summary>
        /// Snapshots — on the calling (main) thread — everything <see cref="SupportBundle.Build"/> needs,
        /// so the build itself touches no Unity or game API and is safe to run on a background thread.
        /// Reused by the in-game panel's on-demand export with a scope override.
        /// </summary>
        internal BundleOptions MakeBundleOptions(ScanReport report, BundleScope scope) => new BundleOptions
        {
            Scope = scope,
            Redact = CfgBundleRedact.Value,
            IncludeModConfigs = CfgBundleIncludeModConfigs.Value,
            ExtraRedactions = CfgBundleExtraRedactions.Value ?? "",
            AtlasVersion = Ver,
            BepInExVersion = BepInExVersionString(),
            Mvid = MvidOf(report),
            ConfigDir = Paths.ConfigPath,
            DecisionsPath = Decisions.PathIn(Paths.BepInExRootPath),
        };

        private static string BepInExVersionString()
        {
            try { return typeof(BaseUnityPlugin).Assembly.GetName().Version?.ToString() ?? ""; }
            catch { return ""; }
        }

        /// <summary>The game assembly's module version id. Prefers the value drift already captured;
        /// otherwise reads it straight off the loaded game assembly.</summary>
        private static string MvidOf(ScanReport report)
        {
            if (report != null && !string.IsNullOrEmpty(report.CurrentMvid)) return report.CurrentMvid;
            try
            {
                var t = HarmonyLib.AccessTools.TypeByName("SpaceCraft.GameConfig");
                return t?.Assembly.ManifestModule.ModuleVersionId.ToString() ?? "";
            }
            catch { return ""; }
        }

        private void FinalizeOnce()
        {
            if (_finalized) return;
            _finalized = true;

            // Before the listener goes: this summary is a diagnostic, and a diagnostic that
            // lands only in LogOutput.log is invisible in the archived copy that gets shared.
            if (CfgLearnHardcodedKeys != null && CfgLearnHardcodedKeys.Value) KeyObserver.Stop();

            if (_listener != null) BepInEx.Logging.Logger.Listeners.Remove(_listener);
            if (CfgCaptureUnityDirect.Value) Application.logMessageReceivedThreaded -= OnUnityLog;

            var final = _session?.FinalizeSession(false, CfgKeepHealthy.Value);
            if (final != null) Log.LogInfo($"Archived -> {Path.GetFileName(final)}");
        }

        private void OnApplicationQuit() { FinalScanOnQuit(); FinalizeOnce(); }

        /// <summary>
        /// A final scan when the game quits straight from a loaded save. Save Keeper (and Alt+F4, and
        /// Steam's Stop) can quit-to-desktop without ever returning to the main menu, so
        /// <see cref="OnSceneUnloaded"/> never fires and the complete end-of-session report - the one
        /// carrying the content surface and any runtime-learned keys - would be lost. <c>_scannedThisScene</c>
        /// is true only while we are in the game scene, so a quit from the menu (already unloaded)
        /// correctly skips this. Runs once; the whole scan pipeline is read-only.
        /// </summary>
        private void FinalScanOnQuit()
        {
            if (_finalScanned) return;
            _finalScanned = true;
            if (!CfgScannerEnabled.Value || !_scannedThisScene) return;
            try
            {
                Scan("quit from game");
            }
            catch (Exception ex) { Log.LogWarning("ATLAS: final scan on quit failed: " + ex.Message); }
        }

        private void OnDestroy()
        {
            if (CfgScannerEnabled != null && CfgScannerEnabled.Value)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
            }
        }
    }
}
