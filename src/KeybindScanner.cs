using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace ATLAS
{
    /// <summary>
    /// Collects every keyboard/controller binding it can see - the game's own InputActionAsset
    /// plus every mod config entry that looks like a keybind - normalises them to a common
    /// form, reports overlaps, and lists keys nothing is using.
    ///
    /// Honest limits, stated in the report itself so nobody over-trusts it:
    ///  - Hardcoded keybinds (a mod polling Keyboard.current directly, not via config) are
    ///    invisible. ATLAS's own test harness is exactly this case.
    ///  - An overlap is not automatically a bug. Two mods can share a key if they are active
    ///    in different contexts (one in a build UI, one in the world).
    /// </summary>
    internal static class KeybindScanner
    {
        // Config entries whose name/section hints at a binding. Used only for string-typed
        // entries, where the type alone cannot tell us it is a key.
        private static readonly string[] NameHints =
            { "key", "bind", "hotkey", "shortcut", "button", "toggle" };

        // Names a string config must resolve to before we treat it as a keybind.
        private static readonly HashSet<string> KnownKeyNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Keys that exist but should not be recommended as "free".
        private static readonly HashSet<string> NeverRecommend =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // F10 is delivered as WM_SYSKEYDOWN by Windows and is unreliable even when
                // polled directly - a hard-won lesson worth encoding rather than repeating.
                "F10",
                // Steam's default screenshot key on most installs.
                "F12",
                // Reserved by the game / OS.
                "ESCAPE", "ENTER", "TAB", "SPACE", "BACKSPACE",
                "LEFTALT", "RIGHTALT", "LEFTCTRL", "RIGHTCTRL", "LEFTSHIFT", "RIGHTSHIFT",
                "LEFTMETA", "RIGHTMETA", "PRINTSCREEN", "CONTEXTMENU",
            };

        // Controller buttons that exist but should never be offered as "free". Start and Select
        // are the pause/menu pair the game and the platform overlay both claim, whether or not
        // the InputActionAsset happens to bind them - recommending them invites a binding that
        // fights the menu. Names are the normalised (upper-case) form NormalisePath produces.
        private static readonly HashSet<string> NeverRecommendController =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "START", "SELECT",
            };

        private static readonly string[] CandidateKeys = BuildCandidatePool();
        private static readonly string[] CandidateControllerButtons = BuildControllerCandidatePool();

        public static void Run(ScanReport report, Dictionary<string, string> idToName)
        {
            BuildKnownKeyNames();

            CollectGameBindings(report);
            CollectModBindings(report);

            // Must happen before overlap and free-key computation: runtime observations are
            // bindings too, and a key learned at runtime is not a free key.
            if (Plugin.CfgLearnHardcodedKeys != null && Plugin.CfgLearnHardcodedKeys.Value)
            {
                report.KeyLearningActive = true;
                KeyObserver.Apply(report);
                MarkObservedAgainstConfig(report);
            }

            FindOverlaps(report);
            CorrelateConfirmed(report);
            ComputeFreeKeys(report);
            ComputeFreeControllerButtons(report);
        }

        /// <summary>
        /// Item 2 (0.9.0). Stamps a static overlap "confirmed" when runtime observation actually
        /// caught the two mods reading this control on the same frame it was pressed - i.e. an
        /// <see cref="ObservedKeyCollision"/> exists for it. A theoretical overlap that has
        /// happened is worth distinguishing from one that has not, but only that: both mods saw
        /// the press, which is not proof both acted on it, so a confirmation raises visibility and
        /// never the severity band (the overlap stays in the attention band, sorted first by the
        /// renderers). With key learning off there are no collisions and this is a no-op, so an
        /// unconfirmed overlap renders exactly as it did before.
        ///
        /// Device families are kept apart: a keyboard overlap can only be confirmed by a keyboard
        /// collision and a controller overlap by a controller collision, so a pad button never
        /// confirms a same-named key (and vice versa). The match is on the overlap's MAIN token
        /// with modifiers stripped ("LEFTCTRL+F6" -> "F6") against the bare collision control,
        /// which slightly over-confirms a modified overlap from a bare-key press - acceptable, and
        /// stated in the report.
        /// </summary>
        private static void CorrelateConfirmed(ScanReport report)
        {
            if (report.ObservedKeyCollisions.Count == 0) return;

            // Strongest evidence wins per (device, control): ObservedKeyCollisions arrives sorted
            // by count descending, so the first entry seen for a key is its highest-count pair.
            var byControl = new Dictionary<string, ObservedKeyCollision>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in report.ObservedKeyCollisions)
            {
                var k = c.IsController + "|" + c.Control;
                if (!byControl.ContainsKey(k)) byControl[k] = c;
            }

            foreach (var o in report.BindOverlaps)
            {
                var k = o.IsController + "|" + MainToken(o.Control);
                if (!byControl.TryGetValue(k, out var col)) continue;

                o.Confirmed = true;
                o.ConfirmedCount = col.Count;
                o.ConfirmedLast = col.LastSeen;
                o.ConfirmedBy = col.PluginA + " + " + col.PluginB;
            }
        }

        /// <summary>
        /// Flags observed keys that a config entry already declared. What is left - observed
        /// but not configured - is precisely the hardcoded set static scanning cannot see.
        /// </summary>
        private static void MarkObservedAgainstConfig(ScanReport report)
        {
            foreach (var obs in report.ObservedKeys)
            {
                foreach (var bind in report.Binds)
                {
                    if (!string.Equals(bind.Owner, obs.Plugin, StringComparison.OrdinalIgnoreCase)) continue;
                    // A keyboard key and a controller button can share a token in theory; keep
                    // the two device families from cross-matching so a hardcoded pad button is
                    // not silently marked "declared" by a same-named key config, or vice versa.
                    if (bind.IsController != obs.IsController) continue;
                    if (string.Equals(MainToken(bind.Control), obs.Control, StringComparison.OrdinalIgnoreCase))
                    {
                        obs.InConfig = true;
                        break;
                    }
                }
            }
        }

        // ── game side ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the live InputActionAsset via BindingTextTranslator.Instance. Reading it live
        /// (rather than a defaults table) matters: PlayerInputDispatcher.Start loads the
        /// player's saved rebinds from PlayerPrefs into the map, so the live asset reflects
        /// what the player has actually bound, not what shipped.
        /// </summary>
        private static void CollectGameBindings(ScanReport report)
        {
            InputActionAsset? asset = null;
            try
            {
                var translatorType = AccessToolsType("SpaceCraft.BindingTextTranslator");
                var instField = translatorType?.GetField("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                var instance = instField?.GetValue(null);
                if (instance == null) return;

                var actionsField = translatorType!.GetField("actions",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                asset = actionsField?.GetValue(instance) as InputActionAsset;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read game input bindings: " + ex.Message);
                return;
            }

            if (asset == null) return;
            report.GameBindingsFound = true;

            foreach (var map in asset.actionMaps)
            {
                foreach (var action in map.actions)
                {
                    foreach (var binding in action.bindings)
                    {
                        if (binding.isComposite) continue;   // the composite header has no path

                        var path = binding.effectivePath;
                        if (string.IsNullOrEmpty(path)) continue;

                        var control = NormalisePath(path, out bool isController, out bool usable);
                        if (!usable) continue;

                        report.Binds.Add(new BindRecord
                        {
                            Owner = "Game",
                            Label = map.name + " / " + action.name,
                            Control = control,
                            RawValue = path,
                            IsController = isController,
                            Source = BindSource.GameAction,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Turns an input path ("&lt;Keyboard&gt;/f6", "&lt;Gamepad&gt;/buttonSouth") into the
        /// same normalised form used for mod configs, so the two can be compared.
        /// </summary>
        private static string NormalisePath(string path, out bool isController, out bool usable)
        {
            isController = false;
            usable = false;

            var close = path.IndexOf('>');
            if (!path.StartsWith("<", StringComparison.Ordinal) || close < 0) return "";

            var device = path.Substring(1, close - 1);
            var control = path.Substring(close + 1).TrimStart('/');
            if (control.Length == 0) return "";

            // Ignore axis/stick/pointer controls - they cannot collide with a discrete keybind
            // in any way a user could act on.
            if (control.IndexOf('/') >= 0) return "";

            if (device.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) >= 0
                || device.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
                || device.IndexOf("DualShock", StringComparison.OrdinalIgnoreCase) >= 0
                || device.IndexOf("XInput", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isController = true;
                usable = true;
                return control.ToUpperInvariant();
            }

            if (device.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0
                || device.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                usable = true;
                return control.ToUpperInvariant();
            }

            return "";
        }

        // ── mod side ─────────────────────────────────────────────────────────────────

        private static void CollectModBindings(ScanReport report)
        {
            // Track what memory already gave us so the disk pass cannot double-report the
            // same setting - a duplicate would look like two owners claiming one key.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                var meta = info?.Metadata;
                if (meta == null) continue;
                guidToName[meta.GUID] = meta.Name;

                ConfigFile? cfg;
                try { cfg = (info!.Instance as BaseUnityPlugin)?.Config; }
                catch { continue; }
                if (cfg == null) continue;

                foreach (var entry in cfg)
                {
                    try
                    {
                        seen.Add(meta.GUID + "|" + entry.Key.Section + "|" + entry.Key.Key);

                        var rec = Interpret(meta.Name, entry.Key, entry.Value, out var why);
                        if (rec != null) report.Binds.Add(rec);
                        else
                        {
                            // A value that clearly meant to be an input path but does not parse
                            // is a real config error - the binding silently will not work.
                            if (why.IndexOf("does not parse", StringComparison.Ordinal) >= 0)
                                report.MalformedBinds.Add(
                                    $"{meta.Name} / {entry.Key.Section}.{entry.Key.Key} = {entry.Value.BoxedValue}");

                            if (Plugin.CfgKeybindDiagnostics.Value && why.Length > 0)
                                Plugin.Log.LogInfo($"[keybind] skipped {meta.Name} / "
                                                 + $"{entry.Key.Section}.{entry.Key.Key}: {why}");
                        }
                    }
                    catch { /* one odd entry must not stop the scan */ }
                }
            }

            CollectFromConfigFiles(report, seen, guidToName);
        }

        /// <summary>
        /// Second pass over BepInEx/config/*.cfg. Catches settings that memory enumeration
        /// misses: mods holding their own ConfigFile rather than BaseUnityPlugin.Config, and
        /// plugins whose instance is unreachable. BepInEx writes a "# Setting type:" comment
        /// above each entry, which identifies mod-defined enum types that would otherwise be
        /// unrecognisable from the value alone.
        /// </summary>
        private static void CollectFromConfigFiles(
            ScanReport report, HashSet<string> seen, Dictionary<string, string> guidToName)
        {
            string dir;
            try
            {
                dir = Paths.ConfigPath;
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
            }
            catch { return; }

            string[] files;
            try { files = System.IO.Directory.GetFiles(dir, "*.cfg"); }
            catch { return; }

            foreach (var path in files)
            {
                var guid = System.IO.Path.GetFileNameWithoutExtension(path);

                // A .cfg with no loaded plugin behind it is left over from a mod that has been
                // removed or disabled. Its settings cannot fire, so counting them as live
                // bindings would invent overlaps against something that is not there.
                if (!guidToName.TryGetValue(guid, out var owner))
                {
                    if (FileDeclaresABind(path)) report.OrphanedConfigs.Add(guid);
                    continue;
                }

                string[] lines;
                try { lines = System.IO.File.ReadAllLines(path); }
                catch { continue; }

                var section = "";
                var settingType = "";

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) { continue; }

                    if (line.StartsWith("#", StringComparison.Ordinal))
                    {
                        const string marker = "Setting type:";
                        var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        if (at >= 0) settingType = line.Substring(at + marker.Length).Trim();
                        continue;
                    }

                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        settingType = "";
                        continue;
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0) { settingType = ""; continue; }

                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    var typeForThis = settingType;
                    settingType = "";

                    if (seen.Contains(guid + "|" + section + "|" + key)) continue;
                    if (value.Length == 0) continue;

                    // Input System path form, same as the in-memory pass.
                    if (value.StartsWith("<", StringComparison.Ordinal))
                    {
                        var pControl = NormalisePath(value, out bool pIsPad, out bool pUsable);
                        if (!pUsable)
                        {
                            report.MalformedBinds.Add($"{owner} / {section}.{key} = {value}");
                            continue;
                        }
                        report.Binds.Add(new BindRecord
                        {
                            Owner = owner,
                            Label = owner + " / " + section + "." + key + "  [from .cfg]",
                            Control = pControl,
                            RawValue = value,
                            IsController = pIsPad,
                            Source = BindSource.ModConfigTyped,
                        });
                        continue;
                    }

                    var normalised = NormaliseTypedName(value);
                    if (!IsKnownKeyName(normalised)) continue;

                    // A declared type containing "Key"/"Shortcut" is decisive; otherwise fall
                    // back to the same name-hint / distinctive-value test used in memory.
                    var typeSaysKey = typeForThis.IndexOf("Key", StringComparison.OrdinalIgnoreCase) >= 0
                                   || typeForThis.IndexOf("Shortcut", StringComparison.OrdinalIgnoreCase) >= 0;
                    var distinctive = DistinctiveKeys.Contains(MainToken(normalised));
                    var hinted = LooksLikeBindName(section, key);

                    if (!typeSaysKey && !distinctive && !hinted) continue;

                    report.Binds.Add(new BindRecord
                    {
                        Owner = owner,
                        Label = owner + " / " + section + "." + key + "  [from .cfg]",
                        Control = normalised,
                        RawValue = value,
                        Source = typeSaysKey ? BindSource.ModConfigTyped : BindSource.ModConfigGuessed,
                    });
                }
            }
        }

        // Keys distinctive enough that seeing one as a config value is itself strong evidence
        // of a keybind, with no help needed from the setting's name. Deliberately excludes
        // ambiguous words - "Space", "Enter", "Escape", "Tab", bare letters and digits - which
        // turn up as ordinary text values ("SeparatorMode = Space").
        private static readonly HashSet<string> DistinctiveKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
                "INSERT","DELETE","HOME","END","PAGEUP","PAGEDOWN",
                "SCROLLLOCK","CAPSLOCK","NUMLOCK","PAUSE","PRINTSCREEN",
                "BACKQUOTE","LEFTBRACKET","RIGHTBRACKET","SEMICOLON","BACKSLASH",
                "UPARROW","DOWNARROW","LEFTARROW","RIGHTARROW",
                "NUMPAD0","NUMPAD1","NUMPAD2","NUMPAD3","NUMPAD4",
                "NUMPAD5","NUMPAD6","NUMPAD7","NUMPAD8","NUMPAD9",
                "NUMPADDIVIDE","NUMPADMULTIPLY","NUMPADMINUS","NUMPADPLUS","NUMPADPERIOD",
                "NUMPADENTER","NUMPADEQUALS",
            };

        /// <summary>
        /// Decides whether a config entry is a keybind, and normalises it if so.
        ///
        /// Typed entries (KeyboardShortcut, KeyCode, Key, or any enum whose current value
        /// names a real key) are certain. Loose entries are accepted when EITHER the setting
        /// name hints at a binding OR the value is a distinctive key that has no plausible
        /// non-key meaning - the first cut required both, which silently dropped settings
        /// named things like "OpenPanel = Home".
        /// </summary>
        private static BindRecord? Interpret(
            string owner, ConfigDefinition def, ConfigEntryBase entry, out string rejectReason)
        {
            rejectReason = "";
            var type = entry.SettingType;
            var raw = entry.BoxedValue;
            if (raw == null) { rejectReason = "null value"; return null; }

            var label = owner + " / " + def.Section + "." + def.Key;

            // BepInEx's own shortcut type, including modifiers.
            if (type == typeof(KeyboardShortcut))
            {
                var sc = (KeyboardShortcut)raw;
                if (sc.MainKey == UnityEngine.KeyCode.None) { rejectReason = "shortcut is None"; return null; }

                var parts = sc.Modifiers.Select(m => m.ToString().ToUpperInvariant()).ToList();
                parts.Add(sc.MainKey.ToString().ToUpperInvariant());

                return new BindRecord
                {
                    Owner = owner,
                    Label = label,
                    Control = NormaliseTypedName(string.Join("+", parts.ToArray())),
                    RawValue = sc.ToString(),
                    Source = BindSource.ModConfigTyped,
                };
            }

            // Any enum whose current value names a real key. Catches Key and KeyCode, and also
            // mod-defined enums, which the first cut missed entirely.
            if (type.IsEnum)
            {
                var name = raw.ToString();
                if (string.IsNullOrEmpty(name) || name == "None")
                {
                    rejectReason = "enum value is None";
                    return null;
                }
                var norm = NormaliseTypedName(name!);
                if (IsKnownKeyName(norm)) return Typed(owner, label, name!);

                rejectReason = $"enum {type.Name} value '{name}' is not a key name";
                return null;
            }

            // Loose text settings.
            if (type == typeof(string))
            {
                var s = ((string)raw).Trim();
                if (s.Length == 0) { rejectReason = "empty string"; return null; }

                // Input System path form: "<Keyboard>/home", "<Gamepad>/buttonSouth".
                // Several mods store bindings this way rather than as a key name.
                if (s.StartsWith("<", StringComparison.Ordinal))
                {
                    var pathControl = NormalisePath(s, out bool pathIsPad, out bool pathUsable);
                    if (!pathUsable)
                    {
                        rejectReason = $"'{s}' looks like an input path but does not parse";
                        return null;
                    }
                    return new BindRecord
                    {
                        Owner = owner,
                        Label = label,
                        Control = pathControl,
                        RawValue = s,
                        IsController = pathIsPad,
                        Source = BindSource.ModConfigTyped,
                    };
                }

                var normalised = NormaliseTypedName(s);
                if (!IsKnownKeyName(normalised))
                {
                    rejectReason = $"value '{s}' is not a key name";
                    return null;
                }

                var main = MainToken(normalised);
                var distinctive = DistinctiveKeys.Contains(main);
                var hinted = LooksLikeBindName(def.Section, def.Key);

                if (!distinctive && !hinted)
                {
                    rejectReason = $"value '{s}' is ambiguous and the setting name gives no hint";
                    return null;
                }

                return new BindRecord
                {
                    Owner = owner,
                    Label = label,
                    Control = normalised,
                    RawValue = s,
                    Source = hinted && distinctive ? BindSource.ModConfigTyped : BindSource.ModConfigGuessed,
                };
            }

            rejectReason = "type " + type.Name + " is not a bind type";
            return null;
        }

        /// <summary>
        /// Cheap test for whether an orphaned config is worth mentioning: only report leftovers
        /// that actually declare a binding, not every stale settings file on disk.
        /// </summary>
        private static bool FileDeclaresABind(string path)
        {
            try
            {
                foreach (var raw in System.IO.File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (value.Length == 0) continue;

                    if (value.StartsWith("<", StringComparison.Ordinal))
                    {
                        NormalisePath(value, out _, out bool ok);
                        if (ok) return true;
                        continue;
                    }

                    var norm = NormaliseTypedName(value);
                    if (!IsKnownKeyName(norm)) continue;
                    if (DistinctiveKeys.Contains(MainToken(norm)) || LooksLikeBindName("", key)) return true;
                }
            }
            catch { }
            return false;
        }

        private static string MainToken(string normalised) =>
            normalised.Contains("+") ? normalised.Substring(normalised.LastIndexOf('+') + 1) : normalised;

        private static BindRecord Typed(string owner, string label, string name) => new BindRecord
        {
            Owner = owner,
            Label = label,
            Control = NormaliseTypedName(name),
            RawValue = name,
            Source = BindSource.ModConfigTyped,
        };

        private static bool LooksLikeBindName(string section, string key)
        {
            foreach (var hint in NameHints)
            {
                if (key.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (section.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>Tokens that are modifiers wherever they appear in a combo.</summary>
        private static readonly HashSet<string> ModifierTokens =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LEFTCTRL", "RIGHTCTRL", "LEFTALT", "RIGHTALT",
                "LEFTSHIFT", "RIGHTSHIFT", "LEFTMETA", "RIGHTMETA",
            };

        /// <summary>
        /// Collapses the several spellings of the same physical key into one token, so a
        /// Key.Insert, a KeyCode.Insert and a config string "insert" all compare equal.
        /// Modifiers are recognised by name rather than by position, because a combo can be
        /// written either way round ("Shift+F6" and "F6+Shift" are the same binding).
        /// </summary>
        private static string NormaliseTypedName(string name)
        {
            var pieces = name.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var outParts = new List<string>(pieces.Length);

            foreach (var p in pieces)
            {
                var t = p.Trim().ToUpperInvariant();
                switch (t)
                {
                    case "CTRL": case "CONTROL": t = "LEFTCTRL"; break;
                    case "ALT": t = "LEFTALT"; break;
                    case "SHIFT": t = "LEFTSHIFT"; break;
                    case "META": case "WIN": case "CMD": t = "LEFTMETA"; break;
                    case "RETURN": t = "ENTER"; break;
                    case "CAPITAL": t = "CAPSLOCK"; break;
                    case "ESC": t = "ESCAPE"; break;
                }
                // "Alpha1" (KeyCode) and "Digit1" (Key) both mean the 1 key.
                if (t.StartsWith("ALPHA", StringComparison.Ordinal) && t.Length > 5) t = t.Substring(5);
                if (t.StartsWith("DIGIT", StringComparison.Ordinal) && t.Length > 5) t = t.Substring(5);
                outParts.Add(t);
            }

            if (outParts.Count <= 1) return outParts.Count == 1 ? outParts[0] : "";

            var mods = outParts.Where(t => ModifierTokens.Contains(t)).Distinct().ToList();
            var mains = outParts.Where(t => !ModifierTokens.Contains(t)).ToList();
            mods.Sort(StringComparer.Ordinal);

            // No non-modifier token means the whole thing was modifiers; keep them sorted.
            if (mains.Count == 0) return string.Join("+", mods.ToArray());

            mods.AddRange(mains);
            return string.Join("+", mods.ToArray());
        }

        private static void BuildKnownKeyNames()
        {
            if (KnownKeyNames.Count > 0) return;
            foreach (var n in Enum.GetNames(typeof(Key))) KnownKeyNames.Add(NormaliseTypedName(n));
            foreach (var n in Enum.GetNames(typeof(UnityEngine.KeyCode))) KnownKeyNames.Add(NormaliseTypedName(n));
        }

        private static bool IsKnownKeyName(string normalised)
        {
            if (normalised.Length == 0) return false;
            var main = normalised.Contains("+")
                ? normalised.Substring(normalised.LastIndexOf('+') + 1)
                : normalised;
            return KnownKeyNames.Contains(main);
        }

        // ── overlaps and free keys ───────────────────────────────────────────────────

        private static void FindOverlaps(ScanReport report)
        {
            var groups = report.Binds
                .Where(b => b.Control.Length > 0)
                .GroupBy(b => b.IsController + "|" + b.Control);

            foreach (var g in groups)
            {
                // Distinct owners only: one mod binding the same key twice is its own business.
                var owners = g.Select(b => b.Owner).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (owners < 2) continue;

                var overlap = new BindOverlap
                {
                    Control = g.First().Control,
                    IsController = g.First().IsController,
                };
                overlap.Binds.AddRange(g);
                report.BindOverlaps.Add(overlap);
            }

            report.BindOverlaps.Sort((a, b) =>
            {
                var c = a.IsController.CompareTo(b.IsController);
                return c != 0 ? c : string.Compare(a.Control, b.Control, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// Keys from the candidate pool that nothing binds - with modifier combos ignored,
        /// since a key used only as "Ctrl+F6" is still risky to claim bare. Runtime
        /// observations count too, which is what makes the list get more accurate the longer
        /// key learning has been running.
        /// </summary>
        private static void ComputeFreeKeys(ScanReport report)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in report.Binds)
            {
                if (b.IsController || b.Control.Length == 0) continue;
                var main = b.Control.Contains("+")
                    ? b.Control.Substring(b.Control.LastIndexOf('+') + 1)
                    : b.Control;
                used.Add(main);
            }
            foreach (var o in report.ObservedKeys)
                if (!o.IsController && o.Control.Length > 0) used.Add(o.Control);

            foreach (var candidate in CandidateKeys)
            {
                if (used.Contains(candidate)) continue;
                if (NeverRecommend.Contains(candidate)) continue;
                report.FreeKeys.Add(candidate);
            }
        }

        /// <summary>
        /// The controller counterpart to ComputeFreeKeys. Because the game's own controller
        /// bindings are read from the live InputActionAsset, a button the game already uses is
        /// in <paramref name="report"/>.Binds and drops out here - so what remains is the small,
        /// honest set of discrete pad buttons that neither the game nor any mod has claimed.
        /// The pool is deliberately discrete buttons only: sticks, triggers-as-axes and the
        /// individual d-pad directions arrive as composite paths that NormalisePath discards,
        /// so they are neither tracked nor recommended.
        /// </summary>
        private static void ComputeFreeControllerButtons(ScanReport report)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in report.Binds)
            {
                if (!b.IsController || b.Control.Length == 0) continue;
                var main = b.Control.Contains("+")
                    ? b.Control.Substring(b.Control.LastIndexOf('+') + 1)
                    : b.Control;
                used.Add(main);
            }
            foreach (var o in report.ObservedKeys)
                if (o.IsController && o.Control.Length > 0) used.Add(o.Control);

            foreach (var candidate in CandidateControllerButtons)
            {
                if (used.Contains(candidate)) continue;
                if (NeverRecommendController.Contains(candidate)) continue;
                report.FreeControllerButtons.Add(candidate);
            }
        }

        private static string[] BuildCandidatePool()
        {
            var pool = new List<string>();
            for (int i = 1; i <= 12; i++) pool.Add("F" + i);
            for (char c = 'A'; c <= 'Z'; c++) pool.Add(c.ToString());
            for (int i = 0; i <= 9; i++) pool.Add(i.ToString());
            for (int i = 0; i <= 9; i++) pool.Add("NUMPAD" + i);
            pool.AddRange(new[]
            {
                "INSERT", "DELETE", "HOME", "END", "PAGEUP", "PAGEDOWN",
                "BACKQUOTE", "MINUS", "EQUALS", "LEFTBRACKET", "RIGHTBRACKET",
                "SEMICOLON", "QUOTE", "COMMA", "PERIOD", "SLASH", "BACKSLASH",
                "SCROLLLOCK", "PAUSE", "NUMPADDIVIDE", "NUMPADMULTIPLY",
                "NUMPADMINUS", "NUMPADPLUS", "NUMPADPERIOD",
            });
            return pool.ToArray();
        }

        /// <summary>
        /// The discrete, individually-bindable buttons of a standard gamepad, in the normalised
        /// (upper-case) spelling NormalisePath emits for "&lt;Gamepad&gt;/buttonSouth" and friends.
        /// Only top-level buttons appear: the sticks, the d-pad's directions and the analog
        /// triggers-as-axes come through as composite paths ("leftStick/x", "dpad/up") that
        /// NormalisePath drops, so they cannot be tracked as used and must not be recommended.
        /// The generic Gamepad names are used rather than any pad-specific synonym (cross/circle,
        /// a/b) because that is the layer mods bind against with "&lt;Gamepad&gt;/...".
        /// </summary>
        private static string[] BuildControllerCandidatePool() => new[]
        {
            "BUTTONSOUTH", "BUTTONEAST", "BUTTONWEST", "BUTTONNORTH",
            "LEFTSHOULDER", "RIGHTSHOULDER",
            "LEFTTRIGGER", "RIGHTTRIGGER",
            "LEFTSTICKPRESS", "RIGHTSTICKPRESS",
            "START", "SELECT",
        };

        private static Type? AccessToolsType(string fullName)
        {
            try { return HarmonyLib.AccessTools.TypeByName(fullName); }
            catch { return null; }
        }
    }
}
