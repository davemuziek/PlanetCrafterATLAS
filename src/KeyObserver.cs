using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.InputSystem.Controls;

namespace ATLAS
{
    /// <summary>
    /// Learns which keys mods actually use, including hardcoded ones that never appear in any
    /// config file - the one blind spot static config scanning cannot cover.
    ///
    /// The trick is attribution. Logging "F7 was pressed" is worthless on its own, because the
    /// player presses keys constantly and nothing says which mod cared. But when a mod polls
    /// Keyboard.current[Key.F7].isPressed, that mod's own code is on the call stack at that
    /// instant. Walking it names the mod directly rather than guessing from correlation.
    ///
    /// This is the only part of ATLAS that patches a hot path, so it is opt-in and built to
    /// bail out in a couple of nanoseconds on the overwhelmingly common "not pressed" case.
    /// </summary>
    internal static class KeyObserver
    {
        private sealed class Observation
        {
            public string Plugin = "";
            public string Control = "";
            public long Count;
            public string FirstSeen = "";
            public string LastSeen = "";
        }

        private sealed class Collision
        {
            public string Control = "";
            public string PluginA = "";
            public string PluginB = "";
            public long Count;
            public string LastSeen = "";
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Observation> Observations =
            new Dictionary<string, Observation>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Collision> Collisions =
            new Dictionary<string, Collision>(StringComparer.Ordinal);

        // Which plugin touched a given control most recently, and on which frame. Same control
        // + same frame + different plugin is a confirmed simultaneous read.
        private static readonly Dictionary<string, (string plugin, int frame)> LastTouch =
            new Dictionary<string, (string, int)>(StringComparer.Ordinal);

        // Assembly -> owning plugin name. Built once; the hot path only ever reads it.
        private static Dictionary<Assembly, string> _asmToPlugin = new Dictionary<Assembly, string>();

        private static bool _active;
        private static bool _dirty;
        private static string _path = "";

        // Diagnostics. These separate the two ways this feature can silently do nothing:
        // the patch never running at all, versus running but failing to name a caller.
        private static long _rawHits;
        private static long _rawLookups;
        private static long _attributed;
        private static long _unattributed;
        private static int _logged;

        public static long RawHits => _rawHits;
        public static long RawLookups => _rawLookups;
        public static long Attributed => _attributed;
        public static long Unattributed => _unattributed;

        /// <summary>Frames whose assemblies never represent the real caller.</summary>
        private static readonly string[] SkipAssemblies =
            { "0Harmony", "MonoMod", "UnityEngine", "Unity.InputSystem", "mscorlib", "System" };

        public static void Start(string atlasDir)
        {
            _path = Path.Combine(atlasDir, "observed_keys.tsv");
            BuildAssemblyMap();
            Load();
            _active = true;
        }

        public static void Stop()
        {
            _active = false;
            Plugin.Log.LogInfo($"ATLAS key learning: {_rawHits} press interception(s), "
                             + $"{_rawLookups} lookup interception(s), {_attributed} attributed, "
                             + $"{_unattributed} unattributed.");
            Save();
        }

        /// <summary>
        /// Maps plugin assemblies to names. Must be rebuildable: ATLAS's Awake runs partway
        /// through the chainloader, so at that moment roughly half the plugins do not exist
        /// yet and a map built once at startup can never attribute any of them.
        /// </summary>
        public static void BuildAssemblyMap()
        {
            var map = new Dictionary<Assembly, string>();

            foreach (var kv in Chainloader.PluginInfos)
            {
                var meta = kv.Value?.Metadata;
                if (meta == null) continue;

                try
                {
                    var asm = kv.Value.Instance?.GetType().Assembly;
                    if (asm != null && !map.ContainsKey(asm)) map[asm] = meta.Name;
                }
                catch { }
            }

            // Second pass by file path, which covers plugins that are loaded but whose
            // Instance is not assigned yet.
            foreach (var kv in Chainloader.PluginInfos)
            {
                var meta = kv.Value?.Metadata;
                var location = kv.Value?.Location;
                if (meta == null || string.IsNullOrEmpty(location)) continue;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (map.ContainsKey(asm)) continue;
                    try
                    {
                        if (string.IsNullOrEmpty(asm.Location)) continue;
                        if (string.Equals(Path.GetFullPath(asm.Location),
                                          Path.GetFullPath(location!),
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            map[asm] = meta.Name;
                            break;
                        }
                    }
                    catch { }
                }
            }

            _asmToPlugin = map;
        }

        private static int _lastMapRefresh = -100000;

        /// <summary>
        /// Rebuilds the map when an assembly shows up that we cannot name, throttled so a
        /// genuinely unknown assembly (the game's own, say) cannot cause a rebuild storm.
        /// </summary>
        private static void MaybeRefreshMap()
        {
            var frame = Time.frameCount;
            if (frame - _lastMapRefresh < 300) return;
            _lastMapRefresh = frame;
            BuildAssemblyMap();
        }

        // ── the hot path ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called from the ButtonControl.isPressed postfix. Everything here is arranged so the
        /// common case - a button that is not pressed - costs one bool test and a return.
        /// </summary>
        public static void OnButtonRead(ButtonControl control, bool pressed)
        {
            if (!_active || !pressed || control == null) return;

            _rawHits++;

            string controlName;
            try { controlName = control.path; }
            catch { return; }
            if (string.IsNullOrEmpty(controlName)) return;

            var plugin = CallerPlugin();

            if (Plugin.CfgKeybindDiagnostics.Value && _logged < 12)
            {
                _logged++;
                Plugin.Log.LogInfo($"[keyobs] hit #{_rawHits} {controlName} -> "
                                 + (plugin ?? "unattributed (game or unknown assembly)"));
            }

            if (plugin == null)
            {
                _unattributed++;
                return;   // the game itself, or a frame we cannot name
            }
            _attributed++;

            var frame = Time.frameCount;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var key = plugin + "|" + controlName;

            lock (Gate)
            {
                if (!Observations.TryGetValue(key, out var obs))
                {
                    obs = new Observation
                    {
                        Plugin = plugin,
                        Control = controlName,
                        FirstSeen = stamp,
                    };
                    Observations[key] = obs;
                }
                obs.Count++;
                obs.LastSeen = stamp;
                _dirty = true;

                // Same control, same frame, different plugin: two mods genuinely reacting to
                // one key at the same moment, which is the question a static overlap cannot
                // answer.
                if (LastTouch.TryGetValue(controlName, out var last)
                    && last.frame == frame
                    && !string.Equals(last.plugin, plugin, StringComparison.Ordinal))
                {
                    var a = string.CompareOrdinal(last.plugin, plugin) < 0 ? last.plugin : plugin;
                    var b = string.CompareOrdinal(last.plugin, plugin) < 0 ? plugin : last.plugin;
                    var ckey = controlName + "|" + a + "|" + b;

                    if (!Collisions.TryGetValue(ckey, out var col))
                    {
                        col = new Collision { Control = controlName, PluginA = a, PluginB = b };
                        Collisions[ckey] = col;
                    }
                    col.Count++;
                    col.LastSeen = stamp;
                }
                LastTouch[controlName] = (plugin, frame);
            }
        }

        /// <summary>
        /// Finds the first stack frame belonging to a loaded plugin. Returns null when the
        /// caller is the game or the input system itself, which is the majority of calls.
        /// Depth is capped because a mod's own polling site is always shallow.
        ///
        /// The observer's own plumbing has to be skipped by TYPE rather than by assembly:
        /// the postfix frame sits in ATLAS's assembly, so an assembly-level skip would either
        /// attribute every press to ATLAS (it is the nearest matching frame) or make the test
        /// harness undetectable. Skipping the two known infrastructure types does neither.
        /// </summary>
        private static string? CallerPlugin()
        {
            try
            {
                // One StackTrace, not a StackFrame per depth: constructing StackFrame(i) walks
                // the stack from scratch every time, so the loop form did the work a dozen
                // times over on a path that runs inside input polling.
                var trace = new StackTrace(2, false);
                var frames = trace.GetFrames();
                if (frames == null) return null;

                var sawUnknown = false;

                for (int i = 0; i < frames.Length && i < 14; i++)
                {
                    var type = frames[i].GetMethod()?.DeclaringType;
                    if (type == null) continue;              // Harmony dynamic method, etc.

                    if (type == typeof(KeyObserver)
                        || type == typeof(ButtonReadPatch)
                        || type == typeof(KeyLookupPatch)) continue;

                    var asm = type.Assembly;
                    var name = asm.GetName().Name;
                    if (name == null || ShouldSkip(name)) continue;

                    if (_asmToPlugin.TryGetValue(asm, out var plugin)) return plugin;

                    // Could be a plugin that loaded after the map was last built.
                    if (!sawUnknown)
                    {
                        sawUnknown = true;
                        MaybeRefreshMap();
                        if (_asmToPlugin.TryGetValue(asm, out var retry)) return retry;
                    }
                }
            }
            catch { }
            return null;
        }

        // ── second capture path ──────────────────────────────────────────────────────

        private static int _walkFrame = -1;
        private static int _walksThisFrame;

        // Per-control walk history. Without this, the global budget is consumed every frame by
        // whichever controls were discovered first, and nothing new is ever found again - the
        // watcher learns a handful of keys and then goes permanently blind.
        private static readonly Dictionary<string, (int lastFrame, int attempts)> WalkHistory =
            new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        /// <summary>
        /// Decides whether to spend a stack walk on this control right now. A control gets a
        /// few closely-spaced looks when first seen - enough to catch every mod polling it -
        /// and is then revisited rarely, which keeps the per-frame budget available for
        /// controls nobody has attributed yet.
        /// </summary>
        private static bool AllowWalk(string controlName)
        {
            var frame = Time.frameCount;
            if (frame != _walkFrame) { _walkFrame = frame; _walksThisFrame = 0; }

            WalkHistory.TryGetValue(controlName, out var hist);

            // Early looks are cheap and frequent; later ones back off to an occasional recheck
            // in case a mod only polls a key in certain states.
            var cooldown = hist.attempts < 8 ? 15 : 900;
            if (hist.lastFrame != 0 && frame - hist.lastFrame < cooldown) return false;

            if (_walksThisFrame >= 8) return false;
            _walksThisFrame++;

            WalkHistory[controlName] = (frame, hist.attempts + 1);
            return true;
        }

        /// <summary>
        /// Records that a mod looked up a key at all, pressed or not. This catches hardcoded
        /// bindings that are never pressed during the session, and survives the case where the
        /// tiny isPressed property gets inlined into the caller and its patch never runs.
        /// </summary>
        public static void OnKeyLookup(string controlName)
        {
            if (!_active || string.IsNullOrEmpty(controlName)) return;
            if (!AllowWalk(controlName)) return;

            _rawLookups++;

            var plugin = CallerPlugin();
            if (plugin == null) return;

            var key = plugin + "|" + controlName;
            lock (Gate)
            {
                if (Observations.ContainsKey(key)) return;   // already known; nothing to add

                Observations[key] = new Observation
                {
                    Plugin = plugin,
                    Control = controlName,
                    Count = 0,                                // polled, not yet seen pressed
                    FirstSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                };
                _dirty = true;
            }

            if (Plugin.CfgKeybindDiagnostics.Value && _logged < 12)
            {
                _logged++;
                Plugin.Log.LogInfo($"[keyobs] lookup {controlName} -> {plugin}");
            }
        }

        private static bool ShouldSkip(string assemblyName)
        {
            foreach (var s in SkipAssemblies)
                if (assemblyName.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── persistence ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Simple tab-separated text rather than a serialised blob: it merges across sessions,
        /// survives a partial write, and can be read or hand-edited without a tool.
        /// </summary>
        public static void Save()
        {
            if (!_dirty || _path.Length == 0) return;

            try
            {
                var sb = new StringBuilder(4096);
                sb.AppendLine("# ATLAS observed keys. Learned by watching which mod polled which control.");
                sb.AppendLine("# obs\tplugin\tcontrol\tcount\tfirstSeen\tlastSeen");
                sb.AppendLine("# col\tcontrol\tpluginA\tpluginB\tcount\tlastSeen");

                lock (Gate)
                {
                    foreach (var o in Observations.Values)
                        sb.AppendLine($"obs\t{o.Plugin}\t{o.Control}\t{o.Count}\t{o.FirstSeen}\t{o.LastSeen}");
                    foreach (var c in Collisions.Values)
                        sb.AppendLine($"col\t{c.Control}\t{c.PluginA}\t{c.PluginB}\t{c.Count}\t{c.LastSeen}");
                    _dirty = false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not save observed keys: " + ex.Message);
            }
        }

        private static void Load()
        {
            if (_path.Length == 0 || !File.Exists(_path)) return;

            try
            {
                foreach (var line in File.ReadAllLines(_path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split('\t');

                    if (f[0] == "obs" && f.Length >= 6)
                    {
                        var key = f[1] + "|" + f[2];
                        Observations[key] = new Observation
                        {
                            Plugin = f[1],
                            Control = f[2],
                            Count = long.TryParse(f[3], out var n) ? n : 0,
                            FirstSeen = f[4],
                            LastSeen = f[5],
                        };
                    }
                    else if (f[0] == "col" && f.Length >= 6)
                    {
                        var ckey = f[1] + "|" + f[2] + "|" + f[3];
                        Collisions[ckey] = new Collision
                        {
                            Control = f[1],
                            PluginA = f[2],
                            PluginB = f[3],
                            Count = long.TryParse(f[4], out var n) ? n : 0,
                            LastSeen = f[5],
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not load observed keys: " + ex.Message);
            }
        }

        // ── report feed ──────────────────────────────────────────────────────────────

        /// <summary>Folds what has been learned into a scan report.</summary>
        public static void Apply(ScanReport report)
        {
            report.KeyReadsIntercepted = _rawHits + _rawLookups;
            report.KeyReadsUnattributed = _unattributed;

            lock (Gate)
            {
                foreach (var o in Observations.Values)
                {
                    report.ObservedKeys.Add(new ObservedKey
                    {
                        Plugin = o.Plugin,
                        Control = NormaliseControlPath(o.Control),
                        RawControl = o.Control,
                        Count = o.Count,
                        FirstSeen = o.FirstSeen,
                        LastSeen = o.LastSeen,
                        IsController = IsControllerPath(o.Control),
                    });
                }

                foreach (var c in Collisions.Values)
                {
                    report.ObservedKeyCollisions.Add(new ObservedKeyCollision
                    {
                        Control = NormaliseControlPath(c.Control),
                        PluginA = c.PluginA,
                        PluginB = c.PluginB,
                        Count = c.Count,
                        LastSeen = c.LastSeen,
                        IsController = IsControllerPath(c.Control),
                    });
                }
            }

            report.ObservedKeys.Sort((a, b) =>
            {
                var p = string.Compare(a.Plugin, b.Plugin, StringComparison.OrdinalIgnoreCase);
                return p != 0 ? p : string.Compare(a.Control, b.Control, StringComparison.Ordinal);
            });
            report.ObservedKeyCollisions.Sort((a, b) => b.Count.CompareTo(a.Count));
        }

        /// <summary>"/Keyboard/f7" -> "F7", so observed keys compare against configured ones.</summary>
        private static string NormaliseControlPath(string path)
        {
            var slash = path.LastIndexOf('/');
            var control = slash >= 0 ? path.Substring(slash + 1) : path;
            return control.ToUpperInvariant();
        }

        /// <summary>
        /// True when a control path names a gamepad control rather than a keyboard key. The
        /// device segment of a ButtonControl.path is "/Gamepad/..." (or a pad-specific layout
        /// such as DualShock/XInput), which is the same family NormalisePath treats as controller
        /// on the static side - kept in sync so an observed pad button lines up with a configured
        /// one and lands in the controller free-button maths, not the keyboard's.
        /// </summary>
        private static bool IsControllerPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Joystick", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("DualShock", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("XInput", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
