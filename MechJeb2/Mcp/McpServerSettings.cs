using System;
using System.IO;
using UnityEngine;

namespace MuMech.Mcp
{
    // MCP server settings — addon-global, not per-vessel. Persisted as a
    // ConfigNode (KSP's native format) at
    //   <KspDir>/GameData/MechJeb2/Plugins/PluginData/MechJeb2/mcp_settings.cfg
    //
    // Why not a DisplayModule / ComputerModule (per the pattern-recognition
    // review F1): McpServerAddon is a KSPAddon singleton, not a per-vessel
    // PartModule, so the existing ComputerModule.OnLoad/OnSave persistence
    // chain doesn't apply cleanly. We hand-roll IO using ConfigNode — same
    // format as everything else MechJeb writes, but the IO is ours.
    public sealed class McpServerSettings
    {
        public bool Enabled = false;             // Opt-in default. Operator flips on.
        public int Port = 17653;
        public int PortScanRange = 10;           // Try Port..Port+range-1 on conflict.
        public bool AllowNullOrigin = false;     // Browser-CSRF posture. Off by default.
        public string AuthToken = "";            // Optional bearer token; empty = none.

        public static string SettingsPath
        {
            get
            {
                // KSPUtil.ApplicationRootPath is the KSP install root.
                // The PluginData path is standard for MechJeb persistent data.
                string root = KSPUtil.ApplicationRootPath ?? string.Empty;
                return Path.Combine(root,
                    "GameData", "MechJeb2", "Plugins", "PluginData", "MechJeb2",
                    "mcp_settings.cfg");
            }
        }

        public static McpServerSettings LoadOrDefault()
        {
            var s = new McpServerSettings();
            string p = SettingsPath;
            if (!File.Exists(p)) return s;

            try
            {
                ConfigNode node = ConfigNode.Load(p);
                if (node == null) return s;
                ConfigNode root = node.GetNode("McpServerSettings") ?? node;

                if (root.HasValue("enabled")) bool.TryParse(root.GetValue("enabled"), out s.Enabled);
                if (root.HasValue("port")) int.TryParse(root.GetValue("port"), out s.Port);
                if (root.HasValue("port_scan_range")) int.TryParse(root.GetValue("port_scan_range"), out s.PortScanRange);
                if (root.HasValue("allow_null_origin")) bool.TryParse(root.GetValue("allow_null_origin"), out s.AllowNullOrigin);
                if (root.HasValue("auth_token")) s.AuthToken = root.GetValue("auth_token") ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogError("[MechJeb-MCP] Failed to load settings; using defaults. " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                string p = SettingsPath;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var root = new ConfigNode("McpServerSettings");
                root.AddValue("enabled", Enabled);
                root.AddValue("port", Port);
                root.AddValue("port_scan_range", PortScanRange);
                root.AddValue("allow_null_origin", AllowNullOrigin);
                root.AddValue("auth_token", AuthToken ?? "");

                var wrapper = new ConfigNode();
                wrapper.AddNode(root);
                wrapper.Save(p);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MechJeb-MCP] Failed to save settings. " + ex.Message);
            }
        }
    }
}
