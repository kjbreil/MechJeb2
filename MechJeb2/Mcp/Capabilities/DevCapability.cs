using System.Collections.Generic;

namespace MuMech.Mcp
{
    // Developer-facing introspection tools. Used by docs/mcp/SURFACE.md
    // regeneration and ad-hoc debugging. Read-only.
    [McpDescription("Developer introspection helpers — surface dump, registry stats.", Version = "1.0.0")]
    public static class DevCapability
    {
        public sealed class SurfaceEntryDto
        {
            public string path;
            public string kind;           // "command" / "property"
            public string description;
            public string version;
            public bool deprecated;
            public string replaced_by;
            public string side_effect;    // commands only
            public string access;         // properties only
            public string[] required_scenes;
            public double max_time_warp_rate;
            public bool returns_handle;
        }

        public sealed class SurfaceDumpDto
        {
            public string server_instance_id;
            public string surface_version;
            public int command_count;
            public int property_count;
            public List<SurfaceEntryDto> entries;
        }

        [McpCommand("dev/dump_surface",
            Description = "Dump every registered command and property as a flat list. " +
                          "Used by `scripts/mcp/gen-surface-doc.sh` to regenerate docs/mcp/SURFACE.md.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static SurfaceDumpDto DumpSurface()
        {
            McpServerAddon addon = McpServerAddon.Instance;
            McpRegistry reg = addon?.Registry;
            if (reg == null) throw new McpException(ErrorCode.Internal, "Registry not initialized");

            var entries = new List<SurfaceEntryDto>();
            Walk(reg.Root, entries);
            return new SurfaceDumpDto
            {
                server_instance_id = addon.ServerInstanceId,
                surface_version = JsonRpcTransport.ServerVersion,
                command_count = reg.CommandCount,
                property_count = reg.PropertyCount,
                entries = entries,
            };
        }

        private static void Walk(McpRegistry.Node node, List<SurfaceEntryDto> sink)
        {
            if (node.Command != null)
            {
                McpRegistry.CommandBinding c = node.Command;
                sink.Add(new SurfaceEntryDto
                {
                    path = c.Path,
                    kind = "command",
                    description = c.Description,
                    version = c.Version,
                    deprecated = c.Deprecated,
                    replaced_by = c.ReplacedBy,
                    side_effect = c.SideEffect.ToString(),
                    required_scenes = SceneStrings(c.RequiredScenes),
                    max_time_warp_rate = c.MaxTimeWarpRate,
                    returns_handle = c.ReturnsHandle,
                });
            }
            if (node.Property != null)
            {
                McpRegistry.PropertyBinding p = node.Property;
                sink.Add(new SurfaceEntryDto
                {
                    path = p.Path,
                    kind = "property",
                    description = p.Description,
                    version = p.Version,
                    deprecated = p.Deprecated,
                    replaced_by = p.ReplacedBy,
                    access = p.Access.ToString().ToLowerInvariant(),
                    required_scenes = SceneStrings(p.RequiredScenes),
                });
            }
            foreach (var kv in node.Children) Walk(kv.Value, sink);
        }

        private static string[] SceneStrings(GameScenes[] scenes)
        {
            if (scenes == null) return new string[0];
            var arr = new string[scenes.Length];
            for (int i = 0; i < scenes.Length; i++) arr[i] = scenes[i].ToString();
            return arr;
        }
    }
}
