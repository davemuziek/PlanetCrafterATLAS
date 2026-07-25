using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ATLAS
{
    /// <summary>
    /// Patch topology and severity of one tracked method, computed once from Harmony and looked
    /// up during the diff so severity is a function of the change AND of what the mod does to it.
    /// </summary>
    internal sealed class PatchMeta
    {
        public string PatchKinds = "";
        public readonly List<string> Owners = new List<string>();
        public bool AnyTranspiler;
        public bool AnyPrefixOrFinalizer;
        public bool PostfixOnly;
    }

    /// <summary>
    /// One game member an installed mod declares a Harmony patch against, kept in structured form
    /// (unlike the flat <see cref="PatchTargetIndex.Keys"/>, which the self-heal needs but which
    /// loses the type/method split). Consumed by the baselineless compatibility check, which must
    /// resolve the exact (type, method) against the live assembly.
    /// </summary>
    internal sealed class DeclaredPatchTarget
    {
        public string Type = "";     // game-namespace type full name
        public string Method = "";   // "" for a class/type-level target carrying no method name
        public string Owner = "";    // plugin assembly name declaring the patch
    }

    /// <summary>
    /// The game members installed mods currently declare a Harmony patch against, gathered live from
    /// the plugin DLLs. Keys are both "Type" and "Type.Method". Owners are the plugin assembly names
    /// that reference each key, so a finding can be attributed to the mod still targeting a renamed
    /// or removed member - or found to be targeted by nobody, which is what "fixed" looks like.
    /// <see cref="Targets"/> is the same data in structured form for the compatibility check.
    /// </summary>
    internal sealed class PatchTargetIndex
    {
        public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Owners =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Structured (type, method, owner) rows, deduped, for CheckCompatibility. Additive: the flat
        // Keys/Owners above are unchanged, so DriftLiveStatus's self-heal is untouched.
        public readonly List<DeclaredPatchTarget> Targets = new List<DeclaredPatchTarget>();
        private readonly HashSet<string> _seenTargets = new HashSet<string>(StringComparer.Ordinal);

        public void Add(string key, string owner)
        {
            Keys.Add(key);
            if (!Owners.TryGetValue(key, out var list)) { list = new List<string>(); Owners[key] = list; }
            if (!list.Contains(owner)) list.Add(owner);
        }

        public void AddTarget(string type, string method, string owner)
        {
            var k = type + '\u0001' + method + '\u0001' + owner;   // sep cannot occur in a type/member/asm name
            if (_seenTargets.Add(k))
                Targets.Add(new DeclaredPatchTarget { Type = type, Method = method, Owner = owner });
        }
    }

    /// <summary>
    /// Part A — the code surface. Reads Assembly-CSharp.dll with Mono.Cecil (never locking it),
    /// records the normalised IL of the methods installed mods actually patch or reflect into,
    /// and diffs that against a recorded baseline. Catches the quiet and silent breakage grades
    /// that the log archive and the conflict scanner are blind to by construction.
    ///
    /// Read-only. It reads two kinds of DLL (the game assembly and the plugin assemblies) and
    /// writes only its own baseline files under BepInEx/ATLAS. It never calls .Resolve() on a
    /// reference (see <see cref="NullResolver"/>) - names are already on the reference, and the
    /// default resolver walks the probing path and throws on anything it cannot find.
    /// </summary>
    internal static class CodeDrift
    {
        /// <summary>
        /// Two-line resolver that returns null from both overloads. We only ever read names, so
        /// resolution is never needed; the stock resolver's probing throws AssemblyResolutionException.
        /// </summary>
        private sealed class NullResolver : IAssemblyResolver
        {
            public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;
            public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
            public void Dispose() { }
        }

        private static ReaderParameters MakeReaderParams() => new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            InMemory = true,          // releases the file handle; never lock a game DLL
            AssemblyResolver = new NullResolver(),
            ReadSymbols = false,
        };

        /// <summary>
        /// Reads a module fully into memory and releases the file handle immediately. Returns
        /// null on any failure - a missing or unreadable assembly is a state (§8 Unavailable),
        /// not an exception the caller has to catch.
        /// </summary>
        public static ModuleDefinition? ReadModule(string path, out string error)
        {
            error = "";
            if (!File.Exists(path)) { error = "assembly not found: " + path; return null; }
            try
            {
                return ModuleDefinition.ReadModule(path, MakeReaderParams());
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        // ── capture: patch targets ─────────────────────────────────────────────────────

        /// <summary>
        /// One pass over the game module recording every method whose (type, name, arg-count)
        /// matches something an installed mod patches. Signature and body hash both come from
        /// Cecil so that a later run compares Cecil-against-Cecil and never drifts on a
        /// reflection-vs-Cecil formatting difference.
        /// </summary>
        public static List<CodeMethodRow> CaptureMethods(
            ModuleDefinition game, HashSet<string> patchedKeys, out int tracked)
        {
            var rows = new List<CodeMethodRow>();
            var sb = new StringBuilder(4096);
            using var sha = SHA256.Create();

            foreach (var type in game.GetTypes())
            {
                if (!type.HasMethods) continue;
                var typeFull = type.FullName;
                foreach (var md in type.Methods)
                {
                    var key = MethodKey(typeFull, md.Name, md.Parameters.Count);
                    if (!patchedKeys.Contains(key)) continue;

                    rows.Add(new CodeMethodRow
                    {
                        Type = typeFull,
                        Name = md.Name,
                        ReturnType = md.ReturnType != null ? md.ReturnType.FullName : "System.Void",
                        Params = ParamsString(md),
                        Hash = NormalisedBodyHash(md, sb, sha),
                    });
                }
            }

            tracked = rows.Count;
            return rows;
        }

        private static string ParamsString(MethodDefinition md)
        {
            if (md.Parameters.Count == 0) return "()";
            var sb = new StringBuilder(64);
            sb.Append('(');
            for (int i = 0; i < md.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var pt = md.Parameters[i].ParameterType;
                sb.Append(pt != null ? pt.FullName : "?");
            }
            sb.Append(')');
            return sb.ToString();
        }

        // ── normalised IL hash (§5.3) ──────────────────────────────────────────────────

        /// <summary>
        /// Hashes the RESOLVED instruction stream, not raw IL bytes. Raw bytes embed metadata
        /// tokens that renumber whenever anything unrelated shifts in the assembly, so a raw hash
        /// would flag nearly every method on nearly every update and be ignored forever. Branch
        /// targets are emitted as instruction INDICES rather than byte offsets, which is what
        /// makes the hash survive an unrelated instruction being inserted earlier in the method.
        ///
        /// No LINQ here and one reused StringBuilder: this runs over every tracked method.
        /// </summary>
        public static string NormalisedBodyHash(MethodDefinition md, StringBuilder sb, SHA256 sha)
        {
            sb.Length = 0;

            if (md.RVA == 0 || !md.HasBody) return "-";   // abstract / extern: no body to hash

            var body = md.Body;
            var instrs = body.Instructions;

            // Index map for branch targets. Built once per method; the hash loop only reads it.
            var index = new Dictionary<Instruction, int>(instrs.Count);
            for (int i = 0; i < instrs.Count; i++) index[instrs[i]] = i;

            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];
                if (ins.OpCode.Code == Code.Nop) continue;

                sb.Append(ins.OpCode.Name);
                sb.Append('|');
                AppendOperand(sb, ins.Operand, index);
                sb.Append('\n');
            }

            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return ToHex16(bytes);
        }

        private static void AppendOperand(StringBuilder sb, object? op, Dictionary<Instruction, int> index)
        {
            switch (op)
            {
                case null:
                    return;
                case MethodReference mr:
                    sb.Append(mr.FullName); return;
                case FieldReference fr:
                    sb.Append(fr.FullName); return;
                case TypeReference tr:
                    sb.Append(tr.FullName); return;
                case string s:
                    sb.Append(s.Length > 64 ? s.Substring(0, 64) : s); return;
                case VariableDefinition v:
                    sb.Append('V').Append(v.Index.ToString(CultureInfo.InvariantCulture)); return;
                case ParameterDefinition p:
                    sb.Append('P').Append(p.Index.ToString(CultureInfo.InvariantCulture)); return;
                case Instruction target:
                    sb.Append('L').Append(TargetIndex(index, target)); return;
                case Instruction[] targets:
                    sb.Append('S');
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(TargetIndex(index, targets[i]));
                    }
                    return;
                default:
                    sb.Append(Convert.ToString(op, CultureInfo.InvariantCulture) ?? ""); return;
            }
        }

        private static string TargetIndex(Dictionary<Instruction, int> index, Instruction target)
            => index.TryGetValue(target, out var n) ? n.ToString(CultureInfo.InvariantCulture) : "?";

        private static string ToHex16(byte[] hash)
        {
            const string hex = "0123456789abcdef";
            var c = new char[16];
            for (int i = 0; i < 8; i++)
            {
                c[i * 2] = hex[(hash[i] >> 4) & 0xF];
                c[i * 2 + 1] = hex[hash[i] & 0xF];
            }
            return new string(c);
        }

        // ── reflection target collection (§5.4) ────────────────────────────────────────

        /// <summary>
        /// Walks every plugin DLL (excluding ATLAS's own) and recovers the type+member pairs
        /// mods reflect into via AccessTools / Type.GetX. Only members whose declaring type is in
        /// a game namespace are recorded; a target whose type could not be STATICALLY recovered
        /// (it came from a local, a field, or a stored typeof) is never guessed at - it increments
        /// the unresolved counter instead, and that count reaches the report so the reader knows
        /// coverage is partial and by how much.
        /// </summary>
        public static List<ReflRow> CollectReflection(
            string pluginPath, string[] gameNamespaces, string ownAssemblyName, out int unresolved,
            Dictionary<string, int>? unresolvedByOwner = null)
        {
            unresolved = 0;
            var rows = new List<ReflRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string[] dlls;
            try { dlls = Directory.GetFiles(pluginPath, "*.dll", SearchOption.AllDirectories); }
            catch { return rows; }

            foreach (var dll in dlls)
            {
                ModuleDefinition? mod = null;
                try
                {
                    mod = ModuleDefinition.ReadModule(dll, MakeReaderParams());
                    var asmName = mod.Assembly != null ? mod.Assembly.Name.Name : "";
                    if (string.Equals(asmName, ownAssemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ScanModuleForReflection(mod, asmName, gameNamespaces, rows, seen, ref unresolved, unresolvedByOwner);
                }
                catch { /* not a managed assembly, or unreadable: skip */ }
                finally { mod?.Dispose(); }
            }

            return rows;
        }

        // ── patch-target declaration collection (0.7.1, for live self-heal) ────────────

        /// <summary>
        /// The set of game members installed mods currently declare a Harmony patch against, read
        /// from the <c>[HarmonyPatch]</c> attributes in the plugin DLLs (class-level and method-level
        /// combine, exactly as Harmony merges them). Keyed both as "Type" and "Type.Method" so a
        /// finding can ask "does any current mod still target this?" - the answer drives whether a
        /// renamed/removed-target finding is still Active or has been fixed away.
        ///
        /// Used only to CONFIRM a fix, never to hide one: a form this does not parse simply leaves
        /// the target absent, which keeps the finding Active (visible), never falsely Resolved. It
        /// mirrors <see cref="CollectReflection"/>'s read-only, own-DLL-excluded plugin walk.
        /// </summary>
        public static PatchTargetIndex CollectPatchTargets(
            string pluginPath, string[] gameNamespaces, string ownAssemblyName,
            Dictionary<string, int>? unresolvedByOwner = null)
        {
            var index = new PatchTargetIndex();

            string[] dlls;
            try { dlls = Directory.GetFiles(pluginPath, "*.dll", SearchOption.AllDirectories); }
            catch { return index; }

            foreach (var dll in dlls)
            {
                ModuleDefinition? mod = null;
                try
                {
                    mod = ModuleDefinition.ReadModule(dll, MakeReaderParams());
                    var asmName = mod.Assembly != null ? mod.Assembly.Name.Name : "";
                    if (string.Equals(asmName, ownAssemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var type in mod.GetTypes())
                    {
                        HarmonyTargetFromAttrs(type.CustomAttributes, out var classType, out var classMethod);

                        // Track whether this type produced any tracked (game-namespace) target. A
                        // type that is clearly a Harmony patch class but yields none has a target
                        // ATLAS could not resolve to tracked game code (a dynamic/computed target, or
                        // one aimed at untracked code) - the coverage blind spot (0.12.0).
                        var recorded = false;

                        // A class-level [HarmonyPatch(typeof(T), "M")] with plain Prefix/Postfix
                        // methods and no per-method attribute: record the class-level target once.
                        if (classType != null)
                            recorded |= RecordTarget(index, gameNamespaces, classType, classMethod, asmName);

                        if (type.HasMethods)
                            foreach (var md in type.Methods)
                            {
                                if (!md.HasCustomAttributes) continue;
                                HarmonyTargetFromAttrs(md.CustomAttributes, out var mType, out var mMethod);
                                var t = mType ?? classType;
                                var n = mMethod ?? classMethod;
                                if (t != null) recorded |= RecordTarget(index, gameNamespaces, t, n, asmName);
                            }

                        if (unresolvedByOwner != null && !recorded && IsHarmonyPatchType(type))
                            Bump(unresolvedByOwner, asmName);
                    }
                }
                catch { /* not a managed assembly, or unreadable: skip */ }
                finally { mod?.Dispose(); }
            }

            return index;
        }

        /// <summary>Whether a type is a Harmony patch class, by attribute name only (no resolution):
        /// a class-level or method-level attribute whose name starts with "Harmony".</summary>
        private static bool IsHarmonyPatchType(TypeDefinition type)
        {
            if (HasHarmonyAttr(type.CustomAttributes)) return true;
            if (type.HasMethods)
                foreach (var md in type.Methods)
                    if (md.HasCustomAttributes && HasHarmonyAttr(md.CustomAttributes)) return true;
            return false;
        }

        private static bool HasHarmonyAttr(Mono.Collections.Generic.Collection<CustomAttribute> attrs)
        {
            if (attrs == null) return false;
            foreach (var a in attrs)
                if (a.AttributeType != null
                    && a.AttributeType.Name.StartsWith("Harmony", StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Bump(Dictionary<string, int>? map, string key)
        {
            if (map == null) return;
            map.TryGetValue(key, out var c);
            map[key] = c + 1;
        }

        /// <summary>Records a tracked (game-namespace) patch target; returns whether it did.</summary>
        private static bool RecordTarget(
            PatchTargetIndex index, string[] gameNamespaces, string type, string? name, string owner)
        {
            if (!MatchesNamespace(type, gameNamespaces)) return false;
            var method = string.IsNullOrEmpty(name) ? "" : name!;
            index.Add(type, owner);
            if (method.Length > 0) index.Add(type + "." + method, owner);
            index.AddTarget(type, method, owner);
            return true;
        }

        // ── coverage (0.12.0) ──────────────────────────────────────────────────────────

        /// <summary>
        /// Per-mod static visibility, derived from what the collectors actually resolved (never
        /// re-derived): resolved reflection sites and patch targets grouped by owner, against the
        /// per-owner unresolved tallies the collectors produced. One row per mod that hooks the game
        /// at all. Owner = plugin assembly name, as the reflection/compat findings use.
        /// </summary>
        public static List<ModCoverage> BuildCoverage(
            List<ReflRow> refl, List<DeclaredPatchTarget> patchTargets,
            Dictionary<string, int> reflUnresolvedByOwner, Dictionary<string, int> patchUnresolvedByOwner)
        {
            var map = new Dictionary<string, ModCoverage>(StringComparer.Ordinal);

            ModCoverage Get(string owner)
            {
                if (!map.TryGetValue(owner, out var c)) { c = new ModCoverage { Mod = owner }; map[owner] = c; }
                return c;
            }

            foreach (var r in refl) Get(r.Owner).ReflectionResolved++;
            foreach (var t in patchTargets) Get(t.Owner).PatchResolved++;
            foreach (var kv in reflUnresolvedByOwner) Get(kv.Key).ReflectionUnresolved += kv.Value;
            foreach (var kv in patchUnresolvedByOwner) Get(kv.Key).PatchUnresolved += kv.Value;

            var list = new List<ModCoverage>(map.Values);
            // Least-visible first (most unresolved), then by mod name for a stable read.
            list.Sort((a, b) =>
            {
                var u = b.Unresolved.CompareTo(a.Unresolved);
                return u != 0 ? u : string.CompareOrdinal(a.Mod, b.Mod);
            });
            return list;
        }

        /// <summary>
        /// Pulls a declaring type and/or a method name out of the <c>[HarmonyPatch]</c> attributes on
        /// one member. Reads only the unambiguous constructor-argument shapes - a `typeof(T)` type
        /// token and a string method name; a `MethodType`/`Type[]` argument is not a name and is
        /// ignored. Multiple attributes on the member accumulate (Harmony merges them).
        /// </summary>
        private static void HarmonyTargetFromAttrs(
            Mono.Collections.Generic.Collection<CustomAttribute> attrs, out string? type, out string? name)
        {
            type = null; name = null;
            if (attrs == null) return;

            foreach (var attr in attrs)
            {
                if (attr.AttributeType == null || attr.AttributeType.Name != "HarmonyPatch") continue;
                if (!attr.HasConstructorArguments) continue;

                foreach (var arg in attr.ConstructorArguments)
                {
                    var argTypeName = arg.Type != null ? arg.Type.FullName : "";
                    if (argTypeName == "System.Type" && arg.Value is TypeReference tr)
                    {
                        type ??= tr.FullName;
                    }
                    else if (argTypeName == "System.String" && arg.Value is string s && s.Length > 0)
                    {
                        // A "Namespace.Type" string form (rare) carries a dot and no separate type
                        // token; leave those to the reflection/attribute type token instead of
                        // guessing which half is the method. A plain identifier is the method name.
                        if (name == null && s.IndexOf('.') < 0 && s.IndexOf(':') < 0) name = s;
                    }
                }
            }
        }

        private static void ScanModuleForReflection(
            ModuleDefinition mod, string owner, string[] gameNamespaces,
            List<ReflRow> rows, HashSet<string> seen, ref int unresolved,
            Dictionary<string, int>? unresolvedByOwner)
        {
            foreach (var type in mod.GetTypes())
            {
                if (!type.HasMethods) continue;
                foreach (var md in type.Methods)
                {
                    if (md.RVA == 0 || !md.HasBody) continue;
                    var instrs = md.Body.Instructions;

                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var ins = instrs[i];
                        var code = ins.OpCode.Code;
                        if (code != Code.Call && code != Code.Callvirt) continue;
                        if (!(ins.Operand is MethodReference mr)) continue;

                        var kind = Classify(mr, out var isTypeByName);
                        if (kind == null) continue;

                        if (isTypeByName)
                        {
                            var typeName = FindPrecedingLdstr(instrs, i, 8, out _);
                            if (typeName == null) { continue; }
                            if (MatchesNamespace(typeName, gameNamespaces))
                                Add(rows, seen, typeName, "-", "type", owner);
                            continue;
                        }

                        var member = FindPrecedingLdstr(instrs, i, 8, out var memberIdx);
                        if (member == null) continue;   // not a name-based lookup we understand

                        var declType = RecoverType(instrs, memberIdx);
                        if (declType == null) { unresolved++; Bump(unresolvedByOwner, owner); continue; }   // do not guess the type

                        if (MatchesNamespace(declType, gameNamespaces))
                            Add(rows, seen, declType, member, kind, owner);
                        // resolved but outside a game namespace: not our business, not unresolved
                    }
                }
            }
        }

        private static void Add(List<ReflRow> rows, HashSet<string> seen,
                                string type, string member, string kind, string owner)
        {
            var key = type + "|" + member + "|" + kind + "|" + owner;
            if (!seen.Add(key)) return;
            rows.Add(new ReflRow { Type = type, Member = member, Kind = kind, Owner = owner });
        }

        private static string? Classify(MethodReference mr, out bool isTypeByName)
        {
            isTypeByName = false;
            var dt = mr.DeclaringType != null ? mr.DeclaringType.FullName : "";
            var n = mr.Name;

            if (dt == "HarmonyLib.AccessTools")
            {
                switch (n)
                {
                    case "Field":
                    case "DeclaredField": return "field";
                    case "Method":
                    case "DeclaredMethod": return "method";
                    case "Property":
                    case "DeclaredProperty":
                    case "PropertyGetter":
                    case "PropertySetter": return "property";
                    case "Inner":
                    case "InnerType": return "type";
                    case "TypeByName": isTypeByName = true; return "type";
                    default: return null;
                }
            }

            if (dt == "System.Type")
            {
                switch (n)
                {
                    case "GetField": return "field";
                    case "GetMethod": return "method";
                    case "GetProperty": return "property";
                    default: return null;
                }
            }

            return null;
        }

        /// <summary>Nearest ldstr scanning backward within the window; null if none.</summary>
        private static string? FindPrecedingLdstr(
            Mono.Collections.Generic.Collection<Instruction> instrs, int fromIdx, int window, out int idx)
        {
            idx = -1;
            var lo = Math.Max(0, fromIdx - window);
            for (int j = fromIdx - 1; j >= lo; j--)
            {
                if (instrs[j].OpCode.Code == Code.Ldstr && instrs[j].Operand is string s)
                {
                    idx = j;
                    return s;
                }
            }
            return null;
        }

        /// <summary>
        /// Recovers a declaring type only from a clean `ldtoken T; call Type::GetTypeFromHandle`
        /// pair sitting IMMEDIATELY before the member-name ldstr. Exact adjacency is deliberate:
        /// a looser backward scan grabs a DIFFERENT nearby AccessTools call's type token - e.g.
        /// two `AccessTools.Field(typeof(GroupNetworkBase), ...)` calls followed by an
        /// `AccessTools.Method(instance.GetType(), "DisplayStatus")` - and attributes the member to
        /// the wrong type. That confident wrong attribution is the worst possible output for this
        /// feature (§5.4), so anything that is not exactly `ldtoken; GetTypeFromHandle; ldstr` is
        /// counted as unresolved instead of guessed.
        /// </summary>
        private static string? RecoverType(
            Mono.Collections.Generic.Collection<Instruction> instrs, int memberIdx)
        {
            if (memberIdx < 2) return null;
            var handle = instrs[memberIdx - 1];
            var token = instrs[memberIdx - 2];
            if (handle.OpCode.Code == Code.Call
                && handle.Operand is MethodReference m
                && m.Name == "GetTypeFromHandle"
                && m.DeclaringType != null
                && m.DeclaringType.FullName == "System.Type"
                && token.OpCode.Code == Code.Ldtoken
                && token.Operand is TypeReference tr)
                return tr.FullName;
            return null;
        }

        private static bool MatchesNamespace(string typeFullName, string[] prefixes)
        {
            foreach (var p in prefixes)
                if (p.Length > 0 && typeFullName.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── compare: code methods (§7) ─────────────────────────────────────────────────

        public static List<DriftFinding> CompareMethods(
            CodeBaseline baseline, ModuleDefinition game,
            Dictionary<string, TypeDefinition> typeIndex,
            Dictionary<string, PatchMeta> patchMeta,
            HashSet<string> changedPlugins)
        {
            var findings = new List<DriftFinding>();
            var sb = new StringBuilder(4096);
            using var sha = SHA256.Create();

            foreach (var br in baseline.Methods)
            {
                var key = MethodKey(br.Type, br.Name, ParamCount(br.Params));
                patchMeta.TryGetValue(key, out var meta);

                if (!typeIndex.TryGetValue(br.Type, out var type))
                {
                    findings.Add(Make(DriftKind.TypeMissing, Severity.High, br.Type + "." + br.Name,
                        "The type is gone from the game assembly. Every patch and reflection into it "
                        + "will fail when the mod loads.", meta, changedPlugins,
                        DriftOrigin.PatchMethod, br.Type, br.Name));
                    continue;
                }

                // Candidate methods with this name on this exact declaring type.
                MethodDefinition? exact = null;
                var nameExists = false;
                foreach (var md in type.Methods)
                {
                    if (md.Name != br.Name) continue;
                    nameExists = true;
                    if (md.Parameters.Count == ParamCount(br.Params)
                        && ParamsString(md) == br.Params
                        && SafeReturn(md) == br.ReturnType)
                    {
                        exact = md;
                        break;
                    }
                }

                if (exact == null)
                {
                    if (nameExists)
                        findings.Add(Make(DriftKind.SignatureChanged, Severity.High, br.Type + "." + br.Name,
                            "The method still exists but its signature changed (arguments or return "
                            + "type). A patch pinned to the old signature will not apply.", meta, changedPlugins,
                            DriftOrigin.PatchMethod, br.Type, br.Name));
                    else
                        findings.Add(Make(DriftKind.TargetMissing, Severity.High, br.Type + "." + br.Name,
                            "The method is gone from its type. A patch targeting it throws at load.",
                            meta, changedPlugins, DriftOrigin.PatchMethod, br.Type, br.Name));
                    continue;
                }

                var currentHash = NormalisedBodyHash(exact, sb, sha);
                if (currentHash != br.Hash && br.Hash != "-" && currentHash != "-")
                {
                    var sev = BodyChangeSeverity(meta);
                    findings.Add(Make(DriftKind.BodyChanged, sev, br.Type + "." + br.Name,
                        BodyChangeDetail(meta), meta, changedPlugins,
                        DriftOrigin.PatchMethod, br.Type, br.Name));
                }
            }

            return findings;
        }

        private static Severity BodyChangeSeverity(PatchMeta? meta)
        {
            if (meta == null) return Severity.Low;           // no longer patched: informational
            if (meta.AnyTranspiler) return Severity.High;    // transpilers assume an IL layout
            if (meta.AnyPrefixOrFinalizer) return Severity.Medium;
            return Severity.Low;                             // postfix-only stacks cleanly
        }

        private static string BodyChangeDetail(PatchMeta? meta)
        {
            if (meta == null)
                return "The method body changed since the baseline. No mod currently patches it, "
                     + "so this is informational.";
            if (meta.AnyTranspiler)
                return "The method body changed and a transpiler rewrites its IL. Transpilers pin to "
                     + "a specific instruction layout, so this commonly breaks the patch outright.";
            if (meta.AnyPrefixOrFinalizer)
                return "The method body changed under a prefix or finalizer. Usually survivable, but "
                     + "a prefix that reads locals or skips the original may now be wrong.";
            return "The method body changed, but only postfixes run here, which normally still stack.";
        }

        // ── compare: reflection (§7) ───────────────────────────────────────────────────

        /// <summary>
        /// Verifies reflected members against the LIVE game types via the same HarmonyLib.AccessTools
        /// the mods themselves use. AccessTools walks the full hierarchy - declared and inherited,
        /// public and non-public, including base classes in OTHER assemblies (Unity.Netcode's
        /// NetworkBehaviour.IsServer/IsSpawned, UnityEngine.MonoBehaviour, ...). A Cecil-only walk
        /// stops at the game-assembly boundary and would falsely flag every inherited member. Using
        /// AccessTools makes verification identical to the mod's own lookup: if AccessTools finds it,
        /// the mod finds it, so there is nothing to report.
        /// </summary>
        public static List<DriftFinding> CompareReflection(CodeBaseline baseline)
        {
            var cache = new Dictionary<string, MemberSets?>(StringComparer.Ordinal);
            return ResolveReflectionRows(baseline.Refl, cache);
        }

        /// <summary>
        /// The per-row reflection resolution shared by the baseline comparison and the baselineless
        /// compatibility check, so both verify a reflected member against the live game types in
        /// exactly the same way. The caller supplies the type cache so a type reflected AND patched
        /// resolves once across both passes.
        ///
        /// One hierarchy walk per DISTINCT type, cached (a null entry means the type no longer
        /// resolves): many mods reflect a dozen members into the same handful of types, and the
        /// per-member cold reflection calls dominated Part A's cost (~1 s on the real install).
        /// </summary>
        private static List<DriftFinding> ResolveReflectionRows(
            IEnumerable<ReflRow> rows, Dictionary<string, MemberSets?> cache)
        {
            var findings = new List<DriftFinding>();

            foreach (var rr in rows)
            {
                var sets = ResolveType(rr.Type, cache);

                if (sets == null)
                {
                    var member = rr.Member == "-" ? rr.Type : rr.Type + "." + rr.Member;
                    var f = Make(DriftKind.TypeMissing, Severity.High, member,
                        "A mod reflects into this type, which no longer resolves in the game assembly. "
                        + "The reflection call returns null and typically NREs later, far from here.",
                        null, null, DriftOrigin.Reflection, rr.Type, rr.Member == "-" ? "" : rr.Member);
                    f.Owners.Add(rr.Owner);
                    findings.Add(f);
                    continue;
                }

                if (rr.Kind == "type") continue;   // type-only check: the type exists, done

                var exists =
                    rr.Kind == "field" ? sets.Fields.Contains(rr.Member) :
                    rr.Kind == "method" ? sets.Methods.Contains(rr.Member) :
                    rr.Kind == "property" ? sets.Properties.Contains(rr.Member) :
                    true;

                if (!exists)
                {
                    var f = Make(DriftKind.ReflectedMemberMissing, Severity.High,
                        rr.Type + "." + rr.Member,
                        "A mod reflects a " + rr.Kind + " named '" + rr.Member + "' that the type no "
                        + "longer has (the type itself is still present). AccessTools returns null "
                        + "and the failure surfaces later as an NRE.", null, null,
                        DriftOrigin.Reflection, rr.Type, rr.Member);
                    f.Owners.Add(rr.Owner);
                    findings.Add(f);
                }
            }

            return findings;
        }

        /// <summary>Cached AccessTools resolve of a type to its member sets; null means it is gone.</summary>
        private static MemberSets? ResolveType(string typeName, Dictionary<string, MemberSets?> cache)
        {
            if (cache.TryGetValue(typeName, out var sets)) return sets;
            Type? type;
            try { type = AccessTools.TypeByName(typeName); }
            catch { type = null; }
            sets = type == null ? null : BuildMemberSets(type);
            cache[typeName] = sets;
            return sets;
        }

        // ── compatibility check (0.10.0) ───────────────────────────────────────────────

        /// <summary>
        /// The baselineless axis: resolve what the CURRENT mods declare they patch or reflect
        /// against the LIVE game assembly, and report the targets that do not exist. Unlike
        /// <see cref="CompareMethods"/> / <see cref="CompareReflection"/> this reads no baseline, so
        /// it fires on a fresh install and for a mod that has never been baselined - which is exactly
        /// the out-of-date-mod case the baseline diff is structurally blind to.
        ///
        /// Deduped against the drift findings from the same scan: <paramref name="skipTypes"/> holds
        /// game types drift already reported missing, and <paramref name="skipMembers"/> holds
        /// "Type|Member" pairs drift already reported missing. A baseline-tracked dead target is thus
        /// reported once (by drift, which carries the baseline context), never twice. Method targets
        /// are checked by NAME existence only - the [HarmonyPatch] argument-type overload is not
        /// parsed - so a same-name/changed-signature method reads as present here (that is drift's
        /// SignatureChanged to catch, for baselined mods); this never fabricates a signature check.
        /// </summary>
        public static List<DriftFinding> CheckCompatibility(
            List<DeclaredPatchTarget> patchTargets, List<ReflRow> reflRows,
            HashSet<string> skipTypes, HashSet<string> skipMembers)
        {
            var findings = new List<DriftFinding>();
            var cache = new Dictionary<string, MemberSets?>(StringComparer.Ordinal);

            // ── declared patch targets ──
            // Aggregate owners so one missing target shared by several mods is a single row.
            var typeMissing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var methodMissing = new Dictionary<string, List<string>>(StringComparer.Ordinal); // "Type|Method"
            var methodRow = new Dictionary<string, DeclaredPatchTarget>(StringComparer.Ordinal);

            foreach (var t in patchTargets)
            {
                var sets = ResolveType(t.Type, cache);
                if (sets == null)
                {
                    if (!skipTypes.Contains(t.Type)) AddOwner(typeMissing, t.Type, t.Owner);
                    continue;
                }
                if (t.Method.Length == 0) continue;             // type-level target, type present: fine
                if (sets.Methods.Contains(t.Method)) continue;  // method present by name: fine

                var key = t.Type + "|" + t.Method;
                if (skipTypes.Contains(t.Type) || skipMembers.Contains(key)) continue;
                AddOwner(methodMissing, key, t.Owner);
                methodRow[key] = t;
            }

            foreach (var kv in typeMissing)
            {
                var f = Make(DriftKind.TypeMissing, Severity.High, kv.Key,
                    "This mod declares a Harmony patch against a game type that does not exist in the "
                    + "installed build, so the patch throws when the mod loads. Usually the type was "
                    + "renamed or removed in a game update the mod has not caught up with.",
                    null, null, DriftOrigin.PatchMethod, kv.Key, "");
                AddOwners(f, kv.Value);
                findings.Add(f);
            }

            foreach (var kv in methodMissing)
            {
                var t = methodRow[kv.Key];
                var f = Make(DriftKind.TargetMissing, Severity.High, t.Type + "." + t.Method,
                    "This mod declares a Harmony patch against a game method the type no longer has in "
                    + "the installed build. Harmony throws when it tries to apply the patch. Usually the "
                    + "method was renamed or removed in a game update.",
                    null, null, DriftOrigin.PatchMethod, t.Type, t.Method);
                AddOwners(f, kv.Value);
                findings.Add(f);
            }

            // ── reflected members ── (same resolver as the baseline path, sharing the type cache)
            // A type already reported missing by the patch pass is not reported again here: a mod
            // that both patches and reflects the same vanished type is one problem, not two rows.
            foreach (var f in ResolveReflectionRows(reflRows, cache))
            {
                if (skipTypes.Contains(f.MatchType)) continue;
                if (f.Kind == DriftKind.TypeMissing && typeMissing.ContainsKey(f.MatchType)) continue;
                if (f.Kind == DriftKind.ReflectedMemberMissing
                    && skipMembers.Contains(f.MatchType + "|" + f.MatchName)) continue;
                findings.Add(f);
            }

            return findings;
        }

        private static void AddOwner(Dictionary<string, List<string>> map, string key, string owner)
        {
            if (!map.TryGetValue(key, out var list)) { list = new List<string>(); map[key] = list; }
            if (!list.Contains(owner)) list.Add(owner);
        }

        private static void AddOwners(DriftFinding f, List<string> owners)
        {
            owners.Sort(StringComparer.Ordinal);
            foreach (var o in owners) f.Owners.Add(o);
        }

        private sealed class MemberSets
        {
            public readonly HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Methods = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Properties = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Every member name reachable on a type, walking the whole hierarchy with DeclaredOnly at
        /// each level so inherited NON-public members are included too - matching what
        /// AccessTools.Field/Method/Property (which the reflecting mod uses) would find, including
        /// base classes in other assemblies (Unity.Netcode's NetworkBehaviour, UnityEngine, ...).
        /// </summary>
        private static MemberSets BuildMemberSets(Type type)
        {
            var s = new MemberSets();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var t = type;
            var guard = 0;
            while (t != null && t != typeof(object) && guard++ < 64)
            {
                try
                {
                    foreach (var f in t.GetFields(flags)) s.Fields.Add(f.Name);
                    foreach (var m in t.GetMethods(flags)) s.Methods.Add(m.Name);
                    foreach (var p in t.GetProperties(flags)) s.Properties.Add(p.Name);
                }
                catch { /* a level that will not reflect: skip it, keep walking */ }
                t = t.BaseType;
            }
            return s;
        }

        // ── shared helpers ─────────────────────────────────────────────────────────────

        public static Dictionary<string, TypeDefinition> BuildTypeIndex(ModuleDefinition game)
        {
            var d = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            foreach (var t in game.GetTypes()) d[t.FullName] = t;
            return d;
        }

        private static string SafeReturn(MethodDefinition md)
            => md.ReturnType != null ? md.ReturnType.FullName : "System.Void";

        private static DriftFinding Make(
            DriftKind kind, Severity sev, string member, string detail,
            PatchMeta? meta, HashSet<string>? changedPlugins,
            DriftOrigin origin, string matchType, string matchName)
        {
            var f = new DriftFinding
            {
                Kind = kind, Severity = sev, Member = member, Detail = detail,
                Origin = origin, MatchType = matchType, MatchName = matchName,
            };
            if (meta != null)
            {
                f.PatchKinds = meta.PatchKinds;
                foreach (var o in meta.Owners) f.Owners.Add(o);
                if (changedPlugins != null)
                    foreach (var o in meta.Owners)
                        if (changedPlugins.Contains(o)) { f.OwnerVersionChanged = true; break; }
            }
            return f;
        }

        /// <summary>type|name|argCount, with nested-type '+' normalised to Cecil's '/'.</summary>
        public static string MethodKey(string typeFullName, string name, int paramCount)
            => typeFullName.Replace('+', '/') + "|" + name + "|" + paramCount.ToString(CultureInfo.InvariantCulture);

        /// <summary>Top-level argument count of a "(A,B)" params string (commas inside &lt;&gt;/[] ignored).</summary>
        public static int ParamCount(string paramsString)
        {
            if (string.IsNullOrEmpty(paramsString)) return 0;
            var inner = paramsString;
            if (inner.Length >= 2 && inner[0] == '(' && inner[inner.Length - 1] == ')')
                inner = inner.Substring(1, inner.Length - 2);
            if (inner.Length == 0) return 0;

            var count = 1;
            var depth = 0;
            foreach (var ch in inner)
            {
                if (ch == '<' || ch == '[') depth++;
                else if (ch == '>' || ch == ']') { if (depth > 0) depth--; }
                else if (ch == ',' && depth == 0) count++;
            }
            return count;
        }
    }
}
