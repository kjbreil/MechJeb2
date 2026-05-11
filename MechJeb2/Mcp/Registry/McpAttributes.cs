using System;

namespace MuMech.Mcp
{
    // Marker attributes that drive the MCP capability registry. See
    // McpRegistry.Scan for how these are interpreted at startup.
    //
    // Path conventions:
    //   * Use "/" as the separator: "autopilot/ascent/engage"
    //   * Lowercase + underscores within a segment: "saves/quickload",
    //     "autopilot/node_executor/execute_next"
    //   * The registry is case-sensitive.

    public enum SideEffect
    {
        ReadOnly,           // no game-state mutation; safe to cache
        Mutating,           // mutates state but completes synchronously
        MutatingLongRunning // returns a handle; poll via mj_ops_status
    }

    public enum Access
    {
        Read,
        Write,
        ReadWrite,
    }

    /// <summary>
    /// Marks a static method as an MCP-invokable command. The registry binds
    /// the method's parameters via JSON Schema; method must be static so the
    /// registry doesn't need a MechJeb-specific receiver model.
    ///
    /// Methods MAY return a DTO (which is serialized to JSON), an
    /// <see cref="IRunningOp"/> (which mints a handle and dispatches polling
    /// through OpRegistry), or void.
    ///
    /// Throw <see cref="McpException"/> for typed errors; any other exception
    /// becomes INTERNAL.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class McpCommandAttribute : Attribute
    {
        public string Path { get; }
        public string Description { get; set; }
        public SideEffect SideEffect { get; set; } = SideEffect.Mutating;
        public string Version { get; set; } = "1.0.0";
        public bool Deprecated { get; set; }
        public string ReplacedBy { get; set; }
        public int MainThreadTimeoutMs { get; set; } = 5_000;

        /// <summary>
        /// Comma-separated scene names this command is valid in. Empty = any.
        /// Recognized: MAINMENU, SPACECENTER, FLIGHT, EDITOR, TRACKSTATION, MAPVIEW.
        /// </summary>
        public string RequiredScenes { get; set; } = "";

        public McpCommandAttribute(string path) { Path = path; }
    }

    /// <summary>
    /// Marks a static property or static field as an MCP-readable (and
    /// optionally writable) value. The registry generates a JSON Schema from
    /// the value type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class McpPropertyAttribute : Attribute
    {
        public string Path { get; }
        public string Description { get; set; }
        public Access Access { get; set; } = Access.Read;
        public string Version { get; set; } = "1.0.0";
        public bool Deprecated { get; set; }
        public string ReplacedBy { get; set; }
        public string RequiredScenes { get; set; } = "";

        public McpPropertyAttribute(string path) { Path = path; }
    }

    /// <summary>
    /// Describes a namespace node — used on container classes so mj_discover
    /// returns a human-readable summary when the LLM walks the tree.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class McpDescriptionAttribute : Attribute
    {
        public string Description { get; }
        public string Version { get; set; } = "1.0.0";
        public McpDescriptionAttribute(string description) { Description = description; }
    }

    /// <summary>
    /// Adds extra metadata to a method parameter. The registry uses these to
    /// enrich the generated JSON Schema (description, range, enum values).
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public sealed class McpParamAttribute : Attribute
    {
        public string Description { get; set; }
        public double Min { get; set; } = double.NaN;
        public double Max { get; set; } = double.NaN;
        public string[] EnumValues { get; set; }
    }
}
