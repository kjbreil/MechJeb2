using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MuMech.Mcp
{
    // KSPAddon singleton that owns the HttpListener, lifecycle, audit log,
    // settings, and the registered MCP tool set. One instance for the entire
    // KSP session (KSPAddon.Startup.MainMenu, once:true).
    //
    // Lifecycle (per best-practices A1/A5):
    //   Awake:
    //     1. Load settings; if disabled, log and bail.
    //     2. Open audit log.
    //     3. Build tool registry (Phase 1: dev_ping only).
    //     4. Allocate JsonRpcTransport.
    //     5. Bind HttpListener (port-scan on EADDRINUSE).
    //     6. Write mcp-endpoint.json for client discovery.
    //     7. Spawn the accept loop on a Task (GetContextAsync-based; cancellable).
    //   OnDestroy:
    //     1. Cancel the CancellationTokenSource.
    //     2. Close() the listener (NOT Stop()-then-Close — macOS Mono port-release).
    //     3. Wait up to 1s for the accept loop to drain.
    //     4. Dispose audit log.
    //
    // Threading: HttpListener callbacks land on threadpool threads. Each request
    // is handled synchronously on that thread (no main-thread marshaling at
    // Phase 1 because dev_ping doesn't touch game state). Phase 2's registry
    // adds the main-thread bridge via MainThreadGate.
    [KSPAddon(KSPAddon.Startup.MainMenu, once: true)]
    public sealed class McpServerAddon : MonoBehaviour
    {
        private static McpServerAddon _instance;
        public static McpServerAddon Instance => _instance;

        private McpServerSettings _settings;
        private McpAuditLog _audit;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private JsonRpcTransport _transport;
        private string _serverInstanceId;
        private int _boundPort;

        public string ServerInstanceId => _serverInstanceId;
        public int BoundPort => _boundPort;
        public bool IsRunning => _listener != null && _listener.IsListening;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;
            DontDestroyOnLoad(this);

            try
            {
                StartUp();
            }
            catch (Exception ex)
            {
                Debug.LogError("[MechJeb-MCP] Awake failed; MCP server disabled. " + ex);
                TryShutDown();
            }
        }

        private void StartUp()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _settings = McpServerSettings.LoadOrDefault();
            if (!_settings.Enabled)
            {
                Debug.Log("[MechJeb-MCP] Disabled in settings; not starting listener. " +
                          "Toggle in " + McpServerSettings.SettingsPath + " (enabled = True) to enable.");
                return;
            }

            _serverInstanceId = Guid.NewGuid().ToString("N");

            // Audit log under KSP root /Logs/
            string logDir = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "Logs");
            string auditPath = Path.Combine(logDir, "mechjeb-mcp-audit.log");
            _audit = new McpAuditLog(auditPath);

            // Phase 1: register dev_ping only. Later phases extend this list.
            var tools = new List<JsonRpcTransport.ToolDefinition>();
            tools.Add(BuildDevPingTool());

            _transport = new JsonRpcTransport(tools, _audit, _settings, () => _serverInstanceId);

            // Port scan and bind.
            _listener = TryStartListener(_settings.Port, _settings.PortScanRange, out _boundPort);
            if (_listener == null)
            {
                Debug.LogError(string.Format(
                    "[MechJeb-MCP] Could not bind any port in [{0}, {1}). Disabling.",
                    _settings.Port, _settings.Port + _settings.PortScanRange));
                TryShutDown();
                return;
            }

            // Write the endpoint discovery file. Operator and clients read it
            // from <KspDir>/GameData/MechJeb2/Plugins/PluginData/MechJeb2/mcp-endpoint.json
            WriteEndpointFile();

            _cts = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

            Debug.Log(string.Format(
                "[MechJeb-MCP] Listening on http://127.0.0.1:{0}/mcp/ (instance {1}). Awake total {2}ms.",
                _boundPort, _serverInstanceId, sw.ElapsedMilliseconds));
        }

        private HttpListener TryStartListener(int basePort, int range, out int boundPort)
        {
            boundPort = -1;
            for (int i = 0; i < Math.Max(1, range); i++)
            {
                int port = basePort + i;
                var listener = new HttpListener();
                listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}{1}", port, HttpAccessControl.EndpointPath));
                try
                {
                    listener.Start();
                    boundPort = port;
                    return listener;
                }
                catch (HttpListenerException ex)
                {
                    // 5 = ERROR_ACCESS_DENIED (Windows urlacl); 48/98 = EADDRINUSE
                    Debug.LogWarning(string.Format(
                        "[MechJeb-MCP] Bind failed on port {0}: {1}", port, ex.Message));
                    try { listener.Close(); } catch { }
                }
                catch (SocketException ex)
                {
                    Debug.LogWarning(string.Format(
                        "[MechJeb-MCP] Socket error on port {0}: {1}", port, ex.Message));
                    try { listener.Close(); } catch { }
                }
            }
            return null;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return; // Shutdown path; expected.
                }
                catch (HttpListenerException)
                {
                    return; // Shutdown path; listener was Closed.
                }
                catch (Exception ex)
                {
                    Debug.LogError("[MechJeb-MCP] Accept error: " + ex.Message);
                    continue;
                }

                if (token.IsCancellationRequested) return;
                // Handle each request on a threadpool thread.
                _ = Task.Run(() => SafeHandle(ctx));
            }
        }

        private void SafeHandle(HttpListenerContext ctx)
        {
            try { _transport.Handle(ctx); }
            catch (Exception ex)
            {
                Debug.LogError("[MechJeb-MCP] Request handler threw: " + ex);
            }
            finally
            {
                try { ctx.Response.Close(); } catch { /* already closed by handler */ }
            }
        }

        private void OnDestroy()
        {
            TryShutDown();
            if (_instance == this) _instance = null;
        }

        private void TryShutDown()
        {
            try { _cts?.Cancel(); } catch { }

            try
            {
                if (_listener != null)
                {
                    // Close() releases the port immediately on Mono macOS; Stop()
                    // first has been observed to leak the port. (corefx#25016)
                    _listener.Close();
                }
            }
            catch (Exception ex) { Debug.LogWarning("[MechJeb-MCP] Listener close error: " + ex.Message); }

            try
            {
                if (_acceptTask != null)
                {
                    Task.WhenAny(_acceptTask, Task.Delay(1000)).Wait();
                }
            }
            catch { }

            try { _audit?.Dispose(); } catch { }

            _listener = null;
            _acceptTask = null;
            _cts?.Dispose();
            _cts = null;
            _audit = null;

            Debug.Log("[MechJeb-MCP] Stopped.");
        }

        private void WriteEndpointFile()
        {
            try
            {
                string dir = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty,
                    "GameData", "MechJeb2", "Plugins", "PluginData", "MechJeb2");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "mcp-endpoint.json");

                var info = new JsonObject()
                    .Set("url", "http://127.0.0.1:" + _boundPort + "/mcp/")
                    .Set("port", _boundPort)
                    .Set("instance_id", _serverInstanceId)
                    .Set("server_name", JsonRpcTransport.ServerName)
                    .Set("server_version", JsonRpcTransport.ServerVersion)
                    .Set("protocol_version", HttpAccessControl.McpProtocolVersion);

                File.WriteAllText(path, Json.Stringify(info, pretty: true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MechJeb-MCP] Could not write mcp-endpoint.json: " + ex.Message);
            }
        }

        // -- Built-in dev_ping tool. Phase 1 proof-of-life. ---------------------
        private static JsonRpcTransport.ToolDefinition BuildDevPingTool()
        {
            var inputSchema = new JsonObject()
                .Set("type", "object")
                .Set("properties", new JsonObject()
                    .Set("echo", new JsonObject()
                        .Set("type", "string")
                        .Set("description", "Optional string to echo back.")))
                .Set("additionalProperties", false);
            var outputSchema = new JsonObject()
                .Set("type", "object")
                .Set("required", new JsonArray().Add("pong").Add("server_instance_id").Add("server_version"))
                .Set("properties", new JsonObject()
                    .Set("pong", new JsonObject().Set("type", "boolean"))
                    .Set("server_instance_id", new JsonObject().Set("type", "string"))
                    .Set("server_version", new JsonObject().Set("type", "string"))
                    .Set("ksp_root", new JsonObject().Set("type", "string"))
                    .Set("echo", new JsonObject().Set("type", "string")));
            var annotations = new JsonObject()
                .Set("readOnlyHint", true)
                .Set("idempotentHint", true)
                .Set("openWorldHint", false);

            return new JsonRpcTransport.ToolDefinition
            {
                Name = "dev_ping",
                Description = "Sanity probe. Returns server identity + an optional echo. " +
                              "Read-only, idempotent, free to call. Phase 1 placeholder until the " +
                              "full mj_status / mj_discover / mj_invoke surface lands.",
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Annotations = annotations,
                IsMutating = false,
                Handler = (args, ctx) =>
                {
                    string echo = null;
                    if (args is JsonObject argObj && argObj.TryGet("echo", out JsonValue ev) && ev.Type == JsonType.String)
                        echo = ev.AsString;

                    string instance = Instance != null ? Instance._serverInstanceId : "(no instance)";
                    var result = new JsonObject()
                        .Set("pong", true)
                        .Set("server_instance_id", instance)
                        .Set("server_version", JsonRpcTransport.ServerVersion)
                        .Set("ksp_root", KSPUtil.ApplicationRootPath ?? "");
                    if (echo != null) result.Set("echo", echo);
                    return JsonRpcTransport.Ok(result);
                },
            };
        }
    }
}
