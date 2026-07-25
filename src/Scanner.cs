using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ATLAS
{
    /// <summary>
    /// Pure read side. Produces a ScanReport and touches nothing else. Every method here
    /// is a query; none mutates game, config, or plugin state. That invariant is the whole
    /// safety story for v1 - a report cannot corrupt a save because generating it never writes.
    /// </summary>
    internal static class Scanner
    {
        public static ScanReport Run(DecisionSet decisions)
        {
            var report = new ScanReport
            {
                GameVersion = SafeGet(() => Application.version),
                UnityVersion = SafeGet(() => Application.unityVersion),
            };

            var idToName = CollectPlugins(report, out var asmToName);
            CollectConflicts(report, idToName, asmToName);
            CollectMissingDependencies(report, idToName);

            try { KeybindScanner.Run(report, idToName); }
            catch (Exception ex) { Plugin.Log.LogWarning("Keybind scan failed: " + ex.Message); }

            // Set aside anything the user has ignored BEFORE grading, so a dismissed conflict /
            // overlap / malformed bind no longer weighs in on the counts or the verdict.
            try { Decisions.Partition(report, decisions); }
            catch (Exception ex) { Plugin.Log.LogWarning("ATLAS decisions: partition failed: " + ex.Message); }

            foreach (var c in report.Conflicts)
            {
                switch (c.Severity)
                {
                    case Severity.High: report.HighCount++; break;
                    case Severity.Medium: report.MediumCount++; break;
                    case Severity.Low: report.LowCount++; break;
                }
            }
            return report;
        }

        // ── plugins ──────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> CollectPlugins(
            ScanReport report, out Dictionary<string, string> asmToName)
        {
            var idToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            asmToName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                var meta = info?.Metadata;
                if (meta == null) continue;

                var rec = new PluginRecord
                {
                    Guid = meta.GUID,
                    Name = meta.Name,
                    Version = meta.Version?.ToString() ?? "?",
                    ConfigEntryCount = CountConfigEntries(info),
                };
                report.Plugins.Add(rec);
                idToName[meta.GUID] = meta.Name;

                // Map the plugin's own assembly to its name. When a mod patches without
                // setting a Harmony id, Harmony invents "harmony-auto-<guid>" and there is no
                // plugin GUID to map back - but the patch method still lives in the mod's
                // assembly, so we can recover the name that way. First writer wins; multiple
                // plugins in one assembly (e.g. ATLAS + its test harness) is rare and only
                // affects the tie-broken display name, never correctness.
                try
                {
                    var asm = info!.Instance?.GetType().Assembly.FullName;
                    if (!string.IsNullOrEmpty(asm) && !asmToName.ContainsKey(asm!))
                        asmToName[asm!] = meta.Name;
                }
                catch { /* Instance may not be a resolvable type; skip */ }
            }

            report.Plugins.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            report.PluginCount = report.Plugins.Count;
            return idToName;
        }

        private static int CountConfigEntries(PluginInfo info)
        {
            try
            {
                var instance = info.Instance as BaseUnityPlugin;
                var cfg = instance?.Config;
                if (cfg == null) return 0;

                // ConfigFile implements IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.
                var count = 0;
                foreach (var _ in cfg) count++;
                return count;
            }
            catch
            {
                return 0;
            }
        }

        // ── conflicts ────────────────────────────────────────────────────────────────

        private static void CollectConflicts(
            ScanReport report, Dictionary<string, string> idToName, Dictionary<string, string> asmToName)
        {
            IEnumerable<MethodBase> patched;
            try { patched = Harmony.GetAllPatchedMethods(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not enumerate patched methods: " + ex.Message);
                return;
            }

            foreach (var method in patched)
            {
                HarmonyLib.Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch { continue; }
                if (info == null) continue;

                var owners = BuildOwners(info, idToName, asmToName);
                if (owners.Count < 2) continue;   // single-owner patches are not conflicts

                var rec = new ConflictRecord
                {
                    Method = DescribeMethod(method),
                    Owners = owners,
                };
                Grade(rec, owners, AnyPrefixCanSkip(info));
                try { rec.HasOrderingConstraints = BuildOrder(info, idToName, asmToName, rec.Order); }
                catch { /* order is explanatory, never load-bearing: skip on any inspection failure */ }
                report.Conflicts.Add(rec);
            }

            // Highest severity first, then noisiest method, so the report leads with what matters.
            report.Conflicts.Sort((a, b) =>
            {
                var s = ((int)b.Severity).CompareTo((int)a.Severity);
                return s != 0 ? s : b.Owners.Count.CompareTo(a.Owners.Count);
            });
        }

        private static List<PatchOwner> BuildOwners(
            HarmonyLib.Patches info, Dictionary<string, string> idToName, Dictionary<string, string> asmToName)
        {
            var byOwner = new Dictionary<string, PatchOwner>(StringComparer.Ordinal);

            PatchOwner Owner(string id, MethodInfo patchMethod)
            {
                if (!byOwner.TryGetValue(id, out var o))
                {
                    o = new PatchOwner
                    {
                        OwnerId = id,
                        DisplayName = ResolveName(id, patchMethod, idToName, asmToName),
                    };
                    byOwner[id] = o;
                }
                return o;
            }

            foreach (var p in info.Prefixes) Owner(p.owner, p.PatchMethod).Prefixes++;
            foreach (var p in info.Postfixes) Owner(p.owner, p.PatchMethod).Postfixes++;
            foreach (var p in info.Transpilers) Owner(p.owner, p.PatchMethod).Transpilers++;
            foreach (var p in info.Finalizers) Owner(p.owner, p.PatchMethod).Finalizers++;

            return byOwner.Values.ToList();
        }

        /// <summary>
        /// The order Harmony runs the patches on this method (0.12.0): prefixes (priority desc, then
        /// registration order), then transpilers (they rewrite the body), then postfixes, then
        /// finalizers. This is exactly Harmony's order when no before/after constraint is declared;
        /// returns true when any patch declares before/after, so the renderer can warn that the real
        /// order may differ - ATLAS deliberately does not replicate Harmony's topological sort.
        /// </summary>
        private static bool BuildOrder(
            HarmonyLib.Patches info, Dictionary<string, string> idToName,
            Dictionary<string, string> asmToName, List<PatchStep> order)
        {
            var constraints = false;

            void AddKind(IEnumerable<Patch> patches, string kind, bool byPriority)
            {
                var list = new List<Patch>(patches);
                list.Sort((a, b) =>
                {
                    if (byPriority) { var pr = b.priority.CompareTo(a.priority); if (pr != 0) return pr; }
                    return a.index.CompareTo(b.index);
                });
                foreach (var p in list)
                {
                    if ((p.before != null && p.before.Length > 0) || (p.after != null && p.after.Length > 0))
                        constraints = true;
                    order.Add(new PatchStep
                    {
                        Owner = ResolveName(p.owner, p.PatchMethod, idToName, asmToName),
                        Kind = kind,
                        Priority = p.priority,
                    });
                }
            }

            AddKind(info.Prefixes, "prefix", true);
            AddKind(info.Transpilers, "transpiler", false);
            AddKind(info.Postfixes, "postfix", true);
            AddKind(info.Finalizers, "finalizer", false);
            return constraints;
        }

        /// <summary>
        /// Owner id -> readable name. Direct GUID match first; then, for auto-generated
        /// "harmony-auto-*" ids that have no plugin GUID, fall back to the assembly the patch
        /// method lives in. Only the raw id survives if neither resolves.
        /// </summary>
        private static string ResolveName(
            string id, MethodInfo patchMethod,
            Dictionary<string, string> idToName, Dictionary<string, string> asmToName)
        {
            if (idToName.TryGetValue(id, out var byGuid)) return byGuid;

            try
            {
                var asm = patchMethod?.DeclaringType?.Assembly.FullName;
                if (!string.IsNullOrEmpty(asm) && asmToName.TryGetValue(asm!, out var byAsm))
                    return byAsm;
            }
            catch { /* fall through to raw id */ }

            return id;
        }

        /// <summary>
        /// Whether any prefix on this method can actually skip the original (item 3, 0.9.0). In
        /// Harmony only a prefix that returns <c>bool</c> can return false and skip the original
        /// body and the remaining prefixes; a <c>void</c> prefix can only add behaviour. So this
        /// is read from the patch methods themselves, never guessed from what the target method
        /// does. A null/unresolved return type is treated as "can skip" - conservative, so we
        /// never invent safety. An always-returns-true bool prefix still counts as "can skip":
        /// we cannot prove it never returns false, and assuming otherwise would downplay real risk.
        /// </summary>
        private static bool AnyPrefixCanSkip(HarmonyLib.Patches info)
        {
            try
            {
                foreach (var p in info.Prefixes)
                {
                    var rt = p.PatchMethod?.ReturnType;
                    if (rt == null || rt == typeof(bool)) return true;   // unknown -> conservative
                }
                return false;   // every prefix returns void (or there are none)
            }
            catch { return true; }   // could not inspect -> assume it can skip
        }

        /// <summary>
        /// Severity tiering is the trust model. If every co-patch reads as a warning, players
        /// learn to ignore all of them - including the one that matters. So the bar for High
        /// is "this class of interaction actually breaks things," not "more than one mod is here."
        /// </summary>
        private static void Grade(ConflictRecord rec, List<PatchOwner> owners, bool anyPrefixCanSkip)
        {
            var transpilerOwners = owners.Count(o => o.Transpilers > 0);
            var prefixOwners = owners.Count(o => o.Prefixes > 0);

            if (transpilerOwners >= 2)
            {
                rec.Severity = Severity.High;
                rec.Reason = $"{transpilerOwners} mods rewrite this method's IL (transpilers). "
                           + "Transpilers assume a specific instruction layout, so stacking them "
                           + "commonly breaks one or both.";
                return;
            }

            if (transpilerOwners == 1 && owners.Count >= 2)
            {
                rec.Severity = Severity.Medium;
                rec.Reason = "One mod rewrites this method's IL while others patch around it. "
                           + "Usually fine, but the transpiler may invalidate assumptions the "
                           + "prefixes/postfixes make.";
                return;
            }

            if (prefixOwners >= 2)
            {
                rec.PrefixesCanSkip = anyPrefixCanSkip;
                if (anyPrefixCanSkip)
                {
                    rec.Severity = Severity.Medium;
                    rec.Reason = $"{prefixOwners} mods run prefixes here. Prefixes can skip the "
                               + "original method or each other; behaviour depends on patch order.";
                }
                else
                {
                    // Every prefix returns void, so none can skip the original or each other -
                    // the provably lower-risk shape. Graded from the patch return types, not from
                    // guessing what the target method does.
                    rec.Severity = Severity.Low;
                    rec.Reason = $"{prefixOwners} mods run prefixes here, but all are void - none can "
                               + "skip the original or each other; they add behaviour rather than "
                               + "replace it, which normally coexists. Residual risk only if two "
                               + "prefixes mutate the same argument in an order-dependent way.";
                }
                return;
            }

            // Everything left is postfix-only (and/or finalizers) co-existence.
            rec.Severity = Severity.Low;
            rec.Reason = $"{owners.Count} mods patch this method, but only with postfixes/"
                       + "finalizers, which normally stack cleanly. Listed for completeness.";
        }

        // ── missing dependencies ─────────────────────────────────────────────────────

        private static void CollectMissingDependencies(ScanReport report, Dictionary<string, string> idToName)
        {
            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                var meta = info?.Metadata;
                if (meta == null) continue;

                foreach (var dep in info.Dependencies)
                {
                    var hard = (dep.Flags & BepInDependency.DependencyFlags.HardDependency) != 0;

                    if (!Chainloader.PluginInfos.TryGetValue(dep.DependencyGUID, out var depInfo))
                    {
                        report.MissingDependencies.Add(new MissingDependency
                        {
                            DependentName = meta.Name,
                            DependentGuid = meta.GUID,
                            MissingGuid = dep.DependencyGUID,
                            HardDependency = hard,
                        });
                        continue;
                    }

                    // Present, but check the declared minimum version (0.12.0). This mostly catches
                    // the unenforced SOFT-dependency case - a hard-dep version miss makes the
                    // dependent fail to load, surfacing via the 0.11.0 load-failure mining instead.
                    try
                    {
                        var min = dep.MinimumVersion;
                        var installed = depInfo?.Metadata?.Version;
                        if (min != null && min > new Version(0, 0, 0, 0) && installed != null && installed < min)
                        {
                            report.DependencyVersionIssues.Add(new DependencyVersionIssue
                            {
                                DependentName = meta.Name,
                                DependentGuid = meta.GUID,
                                DepGuid = dep.DependencyGUID,
                                RequiredVersion = min.ToString(),
                                InstalledVersion = installed.ToString(),
                                HardDependency = hard,
                            });
                        }
                    }
                    catch { /* version shapes vary across BepInEx; a bad compare is not a finding */ }
                }
            }

            // Hard deps first - those are the ones that actually stop a mod working.
            report.MissingDependencies.Sort((a, b) => b.HardDependency.CompareTo(a.HardDependency));
            report.DependencyVersionIssues.Sort((a, b) => b.HardDependency.CompareTo(a.HardDependency));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        private static string DescribeMethod(MethodBase m)
        {
            var type = m.DeclaringType != null ? m.DeclaringType.FullName : "?";
            return type + "." + m.Name;
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }
    }
}
