#!/usr/bin/env bash
# Phase 1 smoke test for the MechJeb MCP server.
# Run a copy of KSP with MechJeb installed and the MCP toggle on, then:
#   $ ./scripts/mcp/smoke.sh
#
# By default reads the bound port from the published mcp-endpoint.json so you
# don't have to know it. Override:
#   MCP_URL=http://127.0.0.1:17653/mcp/ ./scripts/mcp/smoke.sh

set -euo pipefail

discover_url() {
  # Standard Steam install paths on the three OSes
  local mac_paths=(
    "$HOME/Library/Application Support/Steam/steamapps/common/Kerbal Space Program/GameData/MechJeb2/Plugins/PluginData/MechJeb2/mcp-endpoint.json"
  )
  local linux_paths=(
    "$HOME/.steam/steam/steamapps/common/Kerbal Space Program/GameData/MechJeb2/Plugins/PluginData/MechJeb2/mcp-endpoint.json"
  )
  for f in "${mac_paths[@]}" "${linux_paths[@]}"; do
    if [[ -f "$f" ]]; then
      if command -v jq >/dev/null; then jq -r '.url' "$f"
      else grep -o '"url": *"[^"]*"' "$f" | head -1 | sed 's/^"url": *"//; s/"$//'; fi
      return 0
    fi
  done
  return 1
}

URL="${MCP_URL:-$(discover_url || echo "")}"
if [[ -z "$URL" ]]; then
  echo "Could not find mcp-endpoint.json; set MCP_URL=http://127.0.0.1:<port>/mcp/" >&2
  exit 2
fi

PROTO="2025-06-18"
COMMON=( -sS -H "Content-Type: application/json"
         -H "Accept: application/json, text/event-stream"
         -H "Host: 127.0.0.1" )

post() {
  local body="$1"
  curl "${COMMON[@]}" -H "MCP-Protocol-Version: $PROTO" -X POST "$URL" -d "$body"
}

post_init() {
  local body="$1"
  curl "${COMMON[@]}" -X POST "$URL" -d "$body"
}

echo "--- initialize ---"
post_init '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}' | tee /tmp/mcp_init.json
echo

echo "--- notifications/initialized ---"
curl -sS -o /dev/null -w "HTTP %{http_code}\n" "${COMMON[@]}" -H "MCP-Protocol-Version: $PROTO" -X POST "$URL" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'

echo "--- tools/list ---"
post '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | tee /tmp/mcp_tools.json
echo

echo "--- tools/call dev_ping ---"
post '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"dev_ping","arguments":{"echo":"hello"}}}' | tee /tmp/mcp_ping.json
echo

echo "--- negative: unknown tool ---"
post '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"no_such_tool"}}'
echo

echo "--- negative: malformed JSON (expect JSON-RPC parse error) ---"
post '{this is not json}'
echo

echo "--- negative: wrong Host header (expect 400 from HttpListener prefix check, or 403 from our HttpAccessControl) ---"
# Either status is correct — both indicate the request was rejected. HttpListener
# does its own Host check against the registered prefix; only requests whose
# Host matches the prefix even reach our handler.
curl -sS -o /dev/null -w "HTTP %{http_code}\n" \
  -H "Content-Type: application/json" -H "Accept: application/json" \
  -H "MCP-Protocol-Version: $PROTO" -H "Host: evil.example" \
  -X POST "$URL" -d '{"jsonrpc":"2.0","id":99,"method":"tools/list"}'

echo "--- negative: missing MCP-Protocol-Version (expect 400 / PROTOCOL_VERSION_MISSING) ---"
curl -sS -o /dev/null -w "HTTP %{http_code}\n" \
  -H "Content-Type: application/json" -H "Accept: application/json" -H "Host: 127.0.0.1" \
  -X POST "$URL" -d '{"jsonrpc":"2.0","id":98,"method":"tools/list"}'

echo "--- negative: wrong content-type (expect 415) ---"
curl -sS -o /dev/null -w "HTTP %{http_code}\n" \
  -H "Content-Type: text/plain" -H "Accept: application/json" \
  -H "MCP-Protocol-Version: $PROTO" -H "Host: 127.0.0.1" \
  -X POST "$URL" -d '{"jsonrpc":"2.0","id":97,"method":"tools/list"}'

echo
echo "Smoke OK if the dev_ping result has \"pong\":true and the negative cases produced the expected statuses."
