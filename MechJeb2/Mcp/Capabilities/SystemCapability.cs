using System.Collections.Generic;

namespace MuMech.Mcp
{
    // System-level capabilities — KSP/solar-system context the LLM needs to
    // orient. Read-only, registered as static methods on a static class.
    [McpDescription("System-level KSP context: bodies, version info, server identity.", Version = "1.0.0")]
    public static class SystemCapability
    {
        // DTO returned by system/bodies.
        public sealed class BodyDto
        {
            public string name;
            public string parent;          // parent body name or null for the star
            public double mass;            // kg
            public double radius;          // m
            public double sma;             // semi-major axis around parent (m), 0 for star
            public double soi;             // sphere of influence radius (m), 0 if undefined
            public bool has_atmosphere;
            public double atmosphere_depth;
            public bool ocean;
            public bool tidally_locked;
        }

        [McpCommand("system/bodies",
            Description = "List all celestial bodies in the current solar system. " +
                          "Stock KSP returns 17 bodies; Real Solar System / mods may return more or different.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static List<BodyDto> Bodies()
        {
            var list = new List<BodyDto>();
            if (FlightGlobals.Bodies == null) return list;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b == null) continue;
                var dto = new BodyDto
                {
                    name = b.bodyName ?? "?",
                    parent = b.referenceBody == b ? null : b.referenceBody?.bodyName,
                    mass = b.Mass,
                    radius = b.Radius,
                    sma = b.referenceBody == b ? 0.0 : (b.orbit != null ? b.orbit.semiMajorAxis : 0.0),
                    soi = b.sphereOfInfluence,
                    has_atmosphere = b.atmosphere,
                    atmosphere_depth = b.atmosphereDepth,
                    ocean = b.ocean,
                    tidally_locked = b.tidallyLocked,
                };
                list.Add(dto);
            }
            return list;
        }

        public sealed class VersionDto
        {
            public string ksp_version;
            public string mechjeb_version;
            public string mcp_protocol_version;
            public string mcp_server_version;
            public string platform;
        }

        [McpCommand("system/version",
            Description = "Returns KSP, MechJeb, and MCP server/protocol version strings.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static VersionDto Version()
        {
            string ksp = "?";
            try { ksp = Versioning.GetVersionString(); } catch { }
            string mj = "?";
            try { mj = typeof(MechJebCore).Assembly.GetName().Version?.ToString() ?? "?"; } catch { }
            return new VersionDto
            {
                ksp_version = ksp,
                mechjeb_version = mj,
                mcp_protocol_version = HttpAccessControl.McpProtocolVersion,
                mcp_server_version = JsonRpcTransport.ServerVersion,
                platform = UnityEngine.Application.platform.ToString(),
            };
        }
    }
}
