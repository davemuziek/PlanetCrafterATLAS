using System.Collections.Generic;

namespace ATLAS
{
    /// <summary>
    /// The complete result of one scan. This is the seam: the Scanner produces it, and
    /// renderers (text file now, in-game panel later, config Editor in v2) only ever read
    /// it. Nothing here reaches back into BepInEx or Harmony - by the time a ScanReport
    /// exists, all reflection is done and the data is inert. That is what lets the Editor
    /// bolt on later without the render path ever having assumed writability.
    /// </summary>
    internal sealed class ScanReport
    {
        public string GameVersion = "";
        public string UnityVersion = "";
        public int PluginCount;

        public readonly List<PluginRecord> Plugins = new List<PluginRecord>();
        public readonly List<ConflictRecord> Conflicts = new List<ConflictRecord>();
        public readonly List<MissingDependency> MissingDependencies = new List<MissingDependency>();

        public readonly List<BindRecord> Binds = new List<BindRecord>();
        public readonly List<BindOverlap> BindOverlaps = new List<BindOverlap>();
        public readonly List<string> FreeKeys = new List<string>();
        public readonly List<string> FreeControllerButtons = new List<string>();
        public readonly List<string> MalformedBinds = new List<string>();
        public readonly List<string> OrphanedConfigs = new List<string>();

        // Items the user has dismissed via decisions.tsv (0.7.2). Partitioned out of the active
        // lists above before any status is computed, so an ignored/approved item no longer weighs
        // in on the verdict - but it stays visible in the Ignored tab. ExistingDecisions carries the
        // ignore lines already on disk, so the report's Export can merge them with new clicks.
        public readonly List<IgnoredItem> IgnoredItems = new List<IgnoredItem>();
        public readonly List<string> ExistingDecisions = new List<string>();

        public readonly List<ObservedKey> ObservedKeys = new List<ObservedKey>();
        public readonly List<ObservedKeyCollision> ObservedKeyCollisions = new List<ObservedKeyCollision>();
        public bool KeyLearningActive;
        public long KeyReadsIntercepted;
        public long KeyReadsUnattributed;
        public bool GameBindingsFound;   // false if the InputActionAsset could not be reached

        // Convenience roll-ups so a renderer never has to re-scan the lists to headline.
        public int HighCount;
        public int MediumCount;
        public int LowCount;

        // Cross-reference results. ArchiveChecked is false when there were no logs to check.
        public bool ArchiveChecked;
        public int ArchiveLogCount;
        public int ObservedConflictCount;   // conflicts that appear in at least one error log

        // ── Drift (0.7.0) ──────────────────────────────────────────────────────────────
        // The 0.6.0 field freeze is lifted for this version only, to carry the update-breakage
        // comparison. DriftState.ApplyTo(report) fills these before any renderer sees the
        // report, mirroring ObservedConflicts.Apply - so the report stays inert data.
        public bool   DriftChecked;              // a comparison actually ran
        public bool   DriftBaselineExists;
        public bool   GameBuildChanged;
        public string BaselineGameVersion = "";
        public string BaselineCapturedUtc = "";
        public string BaselineMvid = "";
        public string CurrentMvid = "";
        public bool   PluginRosterChanged;
        public int    DriftUnresolvedReflectionSites;
        public int    DriftMethodsTracked;
        public long   DriftScanMillis;

        // Which of the five §8 lifecycle states each surface landed in, as plain strings so a
        // renderer never has to know the enum. "" until Drift runs.
        public string DriftCodeState = "";       // NoBaseline / Unchanged / Changed / Unreadable / Unavailable
        public string DriftContentState = "";
        public string DriftCodeDetail = "";      // one line explaining an Unreadable/Unavailable state
        public string DriftContentDetail = "";

        public readonly List<DriftFinding> DriftFindings = new List<DriftFinding>();
        public int DriftHighCount, DriftMediumCount, DriftLowCount;

        // Live status roll-ups (0.7.1). Each finding is re-verified against the current mods every
        // scan, not replayed from a saved verdict: Active is still-broken, Review is "the ground
        // moved in a way static analysis cannot verify a mod-side fix for", Resolved is a finding
        // that was open last scan and is now gone - shown once as confirmation, then dropped.
        public int DriftActiveCount, DriftReviewCount, DriftResolvedCount;

        // ── Compatibility (0.10.0) ─────────────────────────────────────────────────────
        // A SEPARATE axis from Drift, deliberately never folded into DriftFindings. Drift asks
        // "did the game change under my ACCEPTED BASELINE?" (keyed on the assembly mvid); it is
        // silent for a mod it never baselined - including an old mod freshly dropped in. This axis
        // asks the baseline-independent question: "do the game members each INSTALLED mod hooks -
        // its Harmony patch targets and reflected members - still exist in the game as installed
        // RIGHT NOW?" It needs no baseline and fires on a fresh install, which is exactly where the
        // baseline diff is blind. A finding here means the owning mod is out of date with the
        // installed game (a target it declares is gone/renamed), not merely old: a mod whose hooks
        // all still resolve produces nothing. These are inherently live - re-derived each session
        // from current mods vs the live assembly - so they carry no Active/Review/Resolved state
        // and never touch drift_seen.tsv; present means broken now, absent means fixed.
        public bool CompatChecked;               // the compatibility resolver actually ran this scan
        public readonly List<DriftFinding> CompatFindings = new List<DriftFinding>();
        public int CompatHighCount;              // headline count (every compat finding is High today)

        // ── Runtime patch verification (0.11.0) ────────────────────────────────────────
        // The runtime-truth axis. Drift/compat ask what the game HAS (static); this asks what
        // actually HAPPENED after every mod loaded: did each mod's declared Harmony patches apply,
        // and did every mod even load? Read at SCAN time (not Awake - the applied registry is only
        // complete once every plugin's Awake has run) from Harmony's live registry and the archived
        // logs. It catches a patch that silently did not take even though its target still exists -
        // which the compatibility check, being a static name-existence test, cannot see - and it
        // names mods that failed to load and are therefore absent from the roster everything else
        // scans. Deduped against CompatFindings so a missing member is reported once (by compat).
        public bool PatchCheckRan;               // the verification actually ran this scan
        public readonly List<PatchApplyFinding> PatchApplyFindings = new List<PatchApplyFinding>();
        public readonly List<PluginLoadFailure> PluginLoadFailures = new List<PluginLoadFailure>();
        public int PatchDeclaredChecked;         // declared method-level patch targets considered
        public int PatchAppliedVerified;         // of those, found live in Harmony's registry
        public int PatchApplyConfirmedCount;     // High (log-corroborated) not-applied findings

        // ── Analysis coverage (0.12.0) ─────────────────────────────────────────────────
        // Per-mod static visibility: how much of each mod's hooking surface ATLAS could actually
        // resolve. Turns the global "what ATLAS cannot see" caveat into a per-mod figure, so a
        // clean result on a fully-visible mod is trustworthy while a partial mod's clean result
        // only covers what ATLAS could read. Purely informational - it frames the other sections'
        // confidence and never feeds the verdict.
        public bool CoverageChecked;
        public readonly List<ModCoverage> ModCoverages = new List<ModCoverage>();
        public int CoverageFullyVisibleMods;
        public int CoveragePartialMods;

        // ── Dependency version satisfaction (0.12.0) ───────────────────────────────────
        // A declared dependency that IS loaded but is older than the dependent declares it needs
        // (BepInDependency.MinimumVersion). Mostly catches the unenforced soft-dependency case;
        // a hard-dependency version miss makes the dependent fail to load instead (surfaced by the
        // 0.11.0 load-failure mining).
        public readonly List<DependencyVersionIssue> DependencyVersionIssues = new List<DependencyVersionIssue>();

        // ── Log-activity summary (0.14.0) ──────────────────────────────────────────────
        // A step back from single errors: what's been happening across the archived session logs -
        // which errors/warnings recur (consistently firing) vs fired once (situational), and which
        // mods are noisiest. Diagnostic context only; never feeds the verdict or the H/M/L counts.
        public LogActivitySummary LogActivity = new LogActivitySummary();
    }

    /// <summary>One error/warning signature seen across the archived logs (0.14.0).</summary>
    internal sealed class LogEventGroup
    {
        public string Level = "";           // ERROR / FATAL / WARNING
        public string ExceptionType = "";   // may be ""
        public string Frame = "";           // short "Type.Method", may be ""
        public string Label = "";           // display line
        public string Source = "";          // BepInEx logger source tag ("Unity", a mod name, ...)
        public int Count;                   // total occurrences across all kept logs
        public int SessionCount;            // distinct session logs it appeared in
        public string FirstSeen = "";       // earliest session time it appeared in
        public string LastSeen = "";        // latest session time
        public string ExampleFrame = "";    // full frame type, for context
    }

    /// <summary>A log source (BepInEx logger tag: "Unity", a mod name, ...) and how much log noise it
    /// accounts for (0.14.2), plus its full activity catalog for the per-source export. Attribution by
    /// source tag is comprehensive — every archived line carries one — where the earlier frame-based
    /// attribution missed the frameless engine warnings that dominate the volume.</summary>
    internal sealed class NoisySource
    {
        public string Source = "";
        public int Count;                   // total events from this source
        public int SessionCount;            // distinct sessions across its signatures
        public int EventTotal;              // distinct signatures before the catalog cap
        public readonly List<LogEventGroup> Events = new List<LogEventGroup>();  // its signatures, for the catalog export
    }

    /// <summary>
    /// The result of summarising the archived logs (0.14.0): the recurring signatures apart from the
    /// one-offs, and who is noisiest. Session granularity - "recurs" and first/last seen are by session
    /// log, not per-line wall-clock (BepInEx lines are not individually timestamped).
    /// </summary>
    internal sealed class LogActivitySummary
    {
        public bool Analyzed;
        public int LogsScanned;
        public int TotalEvents;
        public readonly List<LogEventGroup> Consistent = new List<LogEventGroup>();   // seen in >= 2 sessions
        public readonly List<LogEventGroup> Situational = new List<LogEventGroup>();  // seen in 1 session
        public readonly List<NoisySource> Noisy = new List<NoisySource>();           // noisiest sources
        public int ConsistentTotal;   // full counts before top-N capping, for "+K more"
        public int SituationalTotal;
        public int NoisyTotal;
    }

    /// <summary>
    /// One mod's static-analysis coverage: how many of its game hooks ATLAS could resolve versus
    /// how many it could not (a dynamic/computed patch target, or a reflection type it could not
    /// statically recover). "Unresolved" honestly lumps "couldn't resolve" with "targets untracked
    /// code" - both mean the game-facing checks cannot verify it. Owner is the plugin assembly name,
    /// consistent with the reflection/compat findings.
    /// </summary>
    internal sealed class ModCoverage
    {
        public string Mod = "";
        public int PatchResolved, PatchUnresolved, ReflectionResolved, ReflectionUnresolved;
        public int Unresolved => PatchUnresolved + ReflectionUnresolved;
        public int Total => PatchResolved + PatchUnresolved + ReflectionResolved + ReflectionUnresolved;
        public bool FullyVisible => Unresolved == 0;
    }

    /// <summary>A loaded dependency whose version is below the minimum the dependent declares.</summary>
    internal sealed class DependencyVersionIssue
    {
        public string DependentName = "";
        public string DependentGuid = "";
        public string DepGuid = "";
        public string RequiredVersion = "";
        public string InstalledVersion = "";
        public bool HardDependency;
    }

    /// <summary>
    /// A game method a mod DECLARES a Harmony patch against that is not present in Harmony's live
    /// registry after all mods loaded - the patch did not take. High when a load/patch error in the
    /// archive corroborates it; Low (informational) otherwise, since a not-applied patch is often a
    /// benign conditional/dynamic patch rather than a failure. A separate axis from drift/compat, so
    /// it is its own record with its own fields (no baseline, no live-status).
    /// </summary>
    internal sealed class PatchApplyFinding
    {
        public string Member = "";               // "SpaceCraft.Foo.Bar"
        public readonly List<string> Owners = new List<string>();   // declaring mod assembly name(s)
        public Severity Severity;                // High if log-corroborated, else Low
        public bool LogCorroborated;
        public string Detail = "";
    }

    /// <summary>
    /// A plugin the archived logs show failed to load (threw during load/Awake). It is, by
    /// definition, absent from the loaded roster the rest of the scan sees, so this is the only
    /// surface that can name it. Evidence from the log, not a verdict ATLAS computed.
    /// </summary>
    internal sealed class PluginLoadFailure
    {
        public string Plugin = "";               // name as it appeared in the BepInEx error line
        public string Error = "";                // the trailing message, for context (may be empty)
        public string LogName = "";              // which archived log it came from
    }

    /// <summary>
    /// One difference between the game as it was when the baseline was captured and the game
    /// as it is now, scoped to a member some installed mod actually patches or reflects into.
    /// Severity is a function of the change AND of what the mod does to that member - see the
    /// grading table in the 0.7.0 work order (§7).
    /// </summary>
    internal sealed class DriftFinding
    {
        public DriftKind Kind;
        public Severity  Severity;          // reuse the existing enum
        public string    Member = "";       // "SpaceCraft.UiWindowCraft.CreateGrid"
        public string    Detail = "";       // human sentence, not a code
        public List<string> Owners = new List<string>();   // mod display names
        public string    PatchKinds = "";   // "transpiler x1, postfix x2" - why it matters
        public bool      OwnerVersionChanged;  // mod updated since baseline; may be fixed already

        // ── live status (0.7.1) ──────────────────────────────────────────────────────
        // Assigned fresh every scan by DriftLiveStatus, never persisted as a verdict.
        public DriftStatus Status = DriftStatus.Active;
        public DriftOrigin Origin = DriftOrigin.PatchMethod;

        // The raw (declaring type, member/method name) this finding is about, kept apart from the
        // display Member so the live re-verify can match it against the mod's current reflection
        // sites and patch targets without re-parsing a dotted string. MatchName is "" for a
        // type-only finding.
        public string MatchType = "";
        public string MatchName = "";
    }

    /// <summary>
    /// What a finding is re-verified against every scan. Active: the breakage is still present in
    /// the current game + current mods. Review: the ground moved in a way static comparison cannot
    /// confirm a mod-side fix for (a body/signature change, a content change) - never auto-cleared,
    /// waits for an explicit Accept. Resolved: it was open last scan and is now gone - the fix took;
    /// shown once, then it drops off.
    /// </summary>
    internal enum DriftStatus { Active, Review, Resolved }

    /// <summary>
    /// Where a finding's breakage lives, which decides how it can be re-verified. Reflection and
    /// PatchMethod findings are hooks in a MOD's own DLL, so they self-heal when the mod is fixed;
    /// Content findings are facts about the game update itself and cannot be verified as fixed.
    /// </summary>
    internal enum DriftOrigin { PatchMethod, Reflection, Content }

    internal enum DriftKind
    {
        TypeMissing, TargetMissing, SignatureChanged, BodyChanged,
        ReflectedMemberMissing,
        GroupAdded, GroupRemoved, GroupFieldChanged, NullCraftableInList,
        NotTracked,   // a patched method with no baseline row: recorded on the current build,
                      // reported as informational rather than as a change (never invents history)
    }

    internal sealed class PluginRecord
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
        public int ConfigEntryCount;   // cheap signal of how much the Editor will surface later
    }

    /// <summary>A method patched by two or more distinct mods.</summary>
    internal sealed class ConflictRecord
    {
        public string Method = "";                       // "SpaceCraft.UiWindowCraft.CreateGrid"
        public Severity Severity;
        public string Reason = "";                       // human sentence, not a code
        public List<PatchOwner> Owners = new List<PatchOwner>();

        // Cross-reference against the log archive: how many archived error logs contain this
        // method in a stack trace. -1 means "archive not checked" (no logs, or checking off);
        // 0 means checked and never seen; >0 means this conflict has actually thrown.
        public int ObservedInLogs = -1;

        // Transparency for prefix-aware grading (item 3, 0.9.0). Set in Grade for a prefix-stack
        // conflict: false means every prefix returns void, so none can skip the original or each
        // other (the provably-lower-risk shape). Default true is the conservative reading used
        // everywhere it does not apply. No renderer is required to show it; it exists so one could.
        public bool PrefixesCanSkip = true;

        // ── applied execution order (0.12.0, interaction depth) ───────────────────────
        // The order Harmony runs the patches on this method: prefixes (priority desc, then load
        // order), then transpilers (rewrite the body), then postfixes, then finalizers. Exact when
        // no before/after constraints are declared; HasOrderingConstraints flags the case where a
        // patch requests explicit ordering, so the real order may differ from this priority sort -
        // ATLAS does not replicate Harmony's topological sort, it says so.
        public readonly List<PatchStep> Order = new List<PatchStep>();
        public bool HasOrderingConstraints;
    }

    /// <summary>One step in a method's patch execution order.</summary>
    internal sealed class PatchStep
    {
        public string Owner = "";
        public string Kind = "";     // "prefix" / "transpiler" / "postfix" / "finalizer"
        public int Priority;
    }

    /// <summary>One mod's contribution to a patched method, split by patch kind.</summary>
    internal sealed class PatchOwner
    {
        public string OwnerId = "";      // Harmony owner id, mapped to plugin name where possible
        public string DisplayName = "";
        public int Prefixes;
        public int Postfixes;
        public int Transpilers;
        public int Finalizers;
    }

    /// <summary>A key a mod was actually seen polling at runtime.</summary>
    internal sealed class ObservedKey
    {
        public string Plugin = "";
        public string Control = "";      // normalised: "F7", "BUTTONSOUTH"
        public string RawControl = "";   // "/Keyboard/f7", "/Gamepad/buttonSouth"
        public long Count;
        public string FirstSeen = "";
        public string LastSeen = "";
        public bool InConfig;            // true if a config entry already declared this bind
        public bool IsController;        // control lives on a gamepad, not the keyboard/mouse
    }

    /// <summary>Two mods observed reading the same control on the same frame.</summary>
    internal sealed class ObservedKeyCollision
    {
        public string Control = "";
        public string PluginA = "";
        public string PluginB = "";
        public long Count;
        public string LastSeen = "";
        public bool IsController;        // control lives on a gamepad, not the keyboard/mouse
    }

    /// <summary>
    /// One item the user has chosen to set aside (an intentional placeholder bind, a deliberately
    /// shared key, a conflict they have vetted). It no longer counts toward the verdict, but it is
    /// kept and shown in the Ignored tab so a decision is never invisible. Key is the stable
    /// identifier written to decisions.tsv.
    /// </summary>
    internal sealed class IgnoredItem
    {
        public string Category = "";   // "conflict" / "overlap" / "malformed"
        public string Label = "";      // display headline
        public string Detail = "";     // secondary line, may be empty
        public string Key = "";        // decisions.tsv key
    }

    internal sealed class MissingDependency
    {
        public string DependentName = "";
        public string DependentGuid = "";
        public string MissingGuid = "";
        public bool HardDependency;      // BepInDependency without SoftDependency flag
    }

    /// <summary>Where a binding came from, which decides how much to trust it.</summary>
    internal enum BindSource
    {
        GameAction,      // read from the live InputActionAsset (includes user rebinds)
        ModConfigTyped,  // ConfigEntry of KeyboardShortcut / KeyCode / Key - certain
        ModConfigGuessed // ConfigEntry<string> that looks like a key - best effort
    }

    internal sealed class BindRecord
    {
        public string Owner = "";        // plugin name, or "Game"
        public string Label = "";        // "Jump", or "Celestial Cycle / Clock.ToggleKey"
        public string Control = "";      // normalised: "F6", "LEFTCTRL+F6", "BUTTONSOUTH"
        public string RawValue = "";     // exactly what was configured, for the report
        public bool IsController;
        public BindSource Source;
    }

    /// <summary>Two or more bindings resolving to the same control.</summary>
    internal sealed class BindOverlap
    {
        public string Control = "";
        public bool IsController;
        public readonly List<BindRecord> Binds = new List<BindRecord>();

        // ── runtime confirmation (item 2, 0.9.0) ─────────────────────────────────────
        // Stamped by KeybindScanner.CorrelateConfirmed when a matching ObservedKeyCollision
        // exists: the two mods were actually caught reading this control on the same frame it was
        // pressed. This distinguishes a theoretical overlap that HAS happened from one that has
        // not - it does not raise the severity band, because both mods seeing the press is not
        // proof both acted on it. Absence of a confirmation is not evidence of safety: it only
        // means the key has not been pressed with both mods loaded while key learning was on.
        public bool   Confirmed;
        public long   ConfirmedCount;
        public string ConfirmedLast = "";
        public string ConfirmedBy = "";   // e.g. "Celestial Cycle + NAVIGATOR"
    }

    /// <summary>
    /// Ordered so numeric comparison works: a scan can headline "worst severity seen"
    /// by taking the max. The whole point of tiering is that most co-patches are None -
    /// two postfixes on one method almost never interact - so only real risks draw the eye.
    /// </summary>
    internal enum Severity
    {
        None = 0,          // informational co-existence (e.g. plain postfix stacking)
        Low = 1,           // worth knowing, rarely a problem
        Medium = 2,        // order-dependent, may interact (multiple prefixes)
        High = 3           // likely to break (transpiler collision, or skipping prefix)
    }
}
