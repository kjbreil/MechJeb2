using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MuMech.Mcp
{
    // Reflection-time scan over MechJeb's assemblies that turns [McpCommand]
    // and [McpProperty] annotations into a path-addressable capability tree.
    //
    // Scan is one-shot at addon Awake. Tree is read-only after build.
    // Schemas are pre-built and memoized — mj_discover responses are O(1).
    public sealed class McpRegistry
    {
        public sealed class CommandBinding
        {
            public string Path;
            public string Description;
            public MethodInfo Method;
            public ParameterInfo[] Parameters;
            public Type ReturnType;
            public JsonObject InputSchema;
            public JsonObject OutputSchema;
            public JsonObject Annotations;
            public SideEffect SideEffect;
            public string Version;
            public bool Deprecated;
            public string ReplacedBy;
            public int MainThreadTimeoutMs;
            public GameScenes[] RequiredScenes;
            public double MaxTimeWarpRate;
            public bool ReturnsHandle;

            public bool IsMutating => SideEffect != SideEffect.ReadOnly;
        }

        public sealed class PropertyBinding
        {
            public string Path;
            public string Description;
            public PropertyInfo Property;       // may be null if Field is set
            public FieldInfo Field;
            public Type ValueType;
            public Access Access;
            public JsonObject Schema;
            public string Version;
            public bool Deprecated;
            public string ReplacedBy;
            public GameScenes[] RequiredScenes;

            public bool CanRead => Access == Access.Read || Access == Access.ReadWrite;
            public bool CanWrite => Access == Access.Write || Access == Access.ReadWrite;
            public string DeclaringType => (Property?.DeclaringType ?? Field?.DeclaringType)?.FullName ?? "?";

            public object GetValue()
            {
                if (Property != null) return Property.GetValue(null, null);
                return Field.GetValue(null);
            }

            public void SetValue(object value)
            {
                if (Property != null) Property.SetValue(null, value, null);
                else Field.SetValue(null, value);
            }
        }

        public sealed class Node
        {
            public string Name;            // last segment
            public string FullPath;        // dotted ("autopilot/ascent")
            public string Description;     // from [McpDescription] on the container class, if any
            public string Version;
            public Dictionary<string, Node> Children = new Dictionary<string, Node>(StringComparer.Ordinal);
            public CommandBinding Command; // leaf only
            public PropertyBinding Property; // leaf only

            public bool IsLeaf => Command != null || Property != null;
        }

        public Node Root { get; private set; } = new Node { Name = "", FullPath = "" };

        private readonly Dictionary<string, CommandBinding> _commandsByPath = new Dictionary<string, CommandBinding>(StringComparer.Ordinal);
        private readonly Dictionary<string, PropertyBinding> _propertiesByPath = new Dictionary<string, PropertyBinding>(StringComparer.Ordinal);

        public int CommandCount => _commandsByPath.Count;
        public int PropertyCount => _propertiesByPath.Count;

        public CommandBinding ResolveCommand(string path) =>
            _commandsByPath.TryGetValue(path, out CommandBinding c) ? c : null;

        public PropertyBinding ResolveProperty(string path) =>
            _propertiesByPath.TryGetValue(path, out PropertyBinding p) ? p : null;

        public Node ResolveNode(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return Root;
            string[] parts = path.Split('/');
            Node cur = Root;
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (!cur.Children.TryGetValue(part, out Node next)) return null;
                cur = next;
            }
            return cur;
        }

        // Build the tree from annotations on the supplied assemblies. Logs
        // a per-binding summary to KSP.log.
        public static McpRegistry Scan(IEnumerable<Assembly> assemblies)
        {
            var sw = Stopwatch.StartNew();
            var reg = new McpRegistry();
            int classes = 0;

            foreach (Assembly asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MechJeb-MCP] Skipping assembly " + asm.GetName().Name + ": " + ex.Message);
                    continue;
                }

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    if (!t.IsDefined(typeof(McpDescriptionAttribute), false)
                        && !HasAnyMcpMember(t)) continue;
                    classes++;
                    reg.ScanType(t);
                }
            }

            sw.Stop();
            Debug.Log(string.Format(
                "[MechJeb-MCP] Registry scan: {0} commands + {1} properties from {2} classes in {3}ms",
                reg.CommandCount, reg.PropertyCount, classes, sw.ElapsedMilliseconds));
            return reg;
        }

        private static bool HasAnyMcpMember(Type t)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic;
            foreach (MethodInfo m in t.GetMethods(bf))
                if (Attribute.IsDefined(m, typeof(McpCommandAttribute), inherit: false)) return true;
            foreach (PropertyInfo p in t.GetProperties(bf))
                if (Attribute.IsDefined(p, typeof(McpPropertyAttribute), inherit: false)) return true;
            foreach (FieldInfo f in t.GetFields(bf))
                if (Attribute.IsDefined(f, typeof(McpPropertyAttribute), inherit: false)) return true;
            return false;
        }

        private void ScanType(Type t)
        {
            // [McpDescription] attaches to a container node.
            var descAttr = t.GetCustomAttribute<McpDescriptionAttribute>();
            // We'll attach the description once we see at least one member with
            // a path under this type (the type's "namespace path" is implied
            // by the longest common prefix of its members).

            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;

            foreach (MethodInfo m in t.GetMethods(bf))
            {
                var attr = m.GetCustomAttribute<McpCommandAttribute>();
                if (attr == null) continue;
                try { RegisterCommand(m, attr); }
                catch (Exception ex)
                {
                    Debug.LogError("[MechJeb-MCP] Failed to register command " + attr.Path
                        + " on " + t.FullName + "." + m.Name + ": " + ex.Message);
                }
            }

            foreach (PropertyInfo p in t.GetProperties(bf))
            {
                var attr = p.GetCustomAttribute<McpPropertyAttribute>();
                if (attr == null) continue;
                try { RegisterProperty(p, null, attr); }
                catch (Exception ex)
                {
                    Debug.LogError("[MechJeb-MCP] Failed to register property " + attr.Path
                        + " on " + t.FullName + "." + p.Name + ": " + ex.Message);
                }
            }

            foreach (FieldInfo f in t.GetFields(bf))
            {
                var attr = f.GetCustomAttribute<McpPropertyAttribute>();
                if (attr == null) continue;
                try { RegisterProperty(null, f, attr); }
                catch (Exception ex)
                {
                    Debug.LogError("[MechJeb-MCP] Failed to register property " + attr.Path
                        + " on " + t.FullName + "." + f.Name + ": " + ex.Message);
                }
            }

            // Attach description to the container if specified.
            if (descAttr != null)
            {
                // Common prefix heuristic: pick the parent path of any registered
                // command/property whose declaring type is this one.
                string commonParent = FindCommonParentForType(t);
                if (commonParent != null)
                {
                    Node n = EnsureNode(commonParent);
                    if (string.IsNullOrEmpty(n.Description)) n.Description = descAttr.Description;
                    if (string.IsNullOrEmpty(n.Version)) n.Version = descAttr.Version;
                }
            }
        }

        private string FindCommonParentForType(Type t)
        {
            string parent = null;
            foreach (KeyValuePair<string, CommandBinding> kv in _commandsByPath)
            {
                if (kv.Value.Method.DeclaringType == t)
                {
                    string p = ParentOf(kv.Key);
                    parent = parent == null ? p : LongestCommonPrefix(parent, p);
                }
            }
            foreach (KeyValuePair<string, PropertyBinding> kv in _propertiesByPath)
            {
                Type d = kv.Value.Property?.DeclaringType ?? kv.Value.Field?.DeclaringType;
                if (d == t)
                {
                    string p = ParentOf(kv.Key);
                    parent = parent == null ? p : LongestCommonPrefix(parent, p);
                }
            }
            return parent;
        }

        private void RegisterCommand(MethodInfo m, McpCommandAttribute attr)
        {
            if (_commandsByPath.ContainsKey(attr.Path))
                throw new InvalidOperationException("Duplicate path: " + attr.Path);

            ParameterInfo[] parameters = m.GetParameters();
            JsonObject inputSchema = JsonSchemaGenerator.ForParameters(parameters);

            // Output schema: try to generate, but tolerate failure for handle-
            // returning commands which return IRunningOp.
            JsonObject outputSchema = null;
            Type returnType = m.ReturnType;
            bool returnsHandle = typeof(IRunningOp).IsAssignableFrom(returnType);
            try
            {
                if (returnsHandle)
                {
                    outputSchema = new JsonObject()
                        .Set("type", "object")
                        .Set("description", "Long-running operation handle. Poll with mj_ops_status.")
                        .Set("properties", new JsonObject()
                            .Set("handle", new JsonObject().Set("type", "string"))
                            .Set("status", new JsonObject().Set("type", "string")));
                }
                else if (returnType != typeof(void))
                {
                    outputSchema = JsonSchemaGenerator.ForType(returnType, attr.Path + " return");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Cannot generate output schema for " + attr.Path + ": " + ex.Message);
            }

            var binding = new CommandBinding
            {
                Path = attr.Path,
                Description = attr.Description,
                Method = m,
                Parameters = parameters,
                ReturnType = returnType,
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Annotations = BuildAnnotations(attr.SideEffect),
                SideEffect = attr.SideEffect,
                Version = attr.Version,
                Deprecated = attr.Deprecated,
                ReplacedBy = attr.ReplacedBy,
                MainThreadTimeoutMs = attr.MainThreadTimeoutMs,
                RequiredScenes = ParseScenes(attr.RequiredScenes),
                MaxTimeWarpRate = attr.MaxTimeWarpRate,
                ReturnsHandle = returnsHandle,
            };

            _commandsByPath[attr.Path] = binding;
            Node node = EnsureNode(attr.Path);
            node.Command = binding;
            node.Description = attr.Description;
            node.Version = attr.Version;
        }

        private void RegisterProperty(PropertyInfo prop, FieldInfo field, McpPropertyAttribute attr)
        {
            if (_propertiesByPath.ContainsKey(attr.Path))
                throw new InvalidOperationException("Duplicate property path: " + attr.Path);
            if (_commandsByPath.ContainsKey(attr.Path))
                throw new InvalidOperationException("Path collision with command: " + attr.Path);

            Type valueType = prop?.PropertyType ?? field.FieldType;
            JsonObject schema = JsonSchemaGenerator.ForType(valueType, attr.Path);

            // Refine access against actual reflection: if no setter and Access requests writes, downgrade.
            Access access = attr.Access;
            if (prop != null && prop.GetSetMethod(false) == null && access != Access.Read)
                access = Access.Read;

            var binding = new PropertyBinding
            {
                Path = attr.Path,
                Description = attr.Description,
                Property = prop,
                Field = field,
                ValueType = valueType,
                Access = access,
                Schema = schema,
                Version = attr.Version,
                Deprecated = attr.Deprecated,
                ReplacedBy = attr.ReplacedBy,
                RequiredScenes = ParseScenes(attr.RequiredScenes),
            };
            _propertiesByPath[attr.Path] = binding;
            Node node = EnsureNode(attr.Path);
            node.Property = binding;
            node.Description = attr.Description;
            node.Version = attr.Version;
        }

        private Node EnsureNode(string path)
        {
            string[] parts = path.Split('/');
            Node cur = Root;
            string accumulated = "";
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;
                if (!cur.Children.TryGetValue(part, out Node next))
                {
                    next = new Node { Name = part, FullPath = accumulated };
                    cur.Children[part] = next;
                }
                cur = next;
            }
            return cur;
        }

        private static JsonObject BuildAnnotations(SideEffect se)
        {
            bool readOnly = se == SideEffect.ReadOnly;
            return new JsonObject()
                .Set("readOnlyHint", readOnly)
                .Set("destructiveHint", !readOnly)
                .Set("idempotentHint", readOnly)
                .Set("openWorldHint", false);
        }

        private static GameScenes[] ParseScenes(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            string[] parts = spec.Split(',');
            var scenes = new List<GameScenes>();
            foreach (string raw in parts)
            {
                string s = raw.Trim().ToUpperInvariant();
                if (s.Length == 0) continue;
                if (Enum.TryParse(s, ignoreCase: true, result: out GameScenes scene))
                    scenes.Add(scene);
            }
            return scenes.Count == 0 ? null : scenes.ToArray();
        }

        private static string ParentOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? "" : path.Substring(0, i);
        }

        private static string LongestCommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            int lastSlash = -1;
            while (i < n && a[i] == b[i])
            {
                if (a[i] == '/') lastSlash = i;
                i++;
            }
            if (i == a.Length && i == b.Length) return a;
            return lastSlash < 0 ? "" : a.Substring(0, lastSlash);
        }
    }
}
