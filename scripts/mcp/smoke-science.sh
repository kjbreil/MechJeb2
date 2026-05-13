#!/usr/bin/env bash
# Smoke test for read-only science MCP commands.
# Run KSP in a science/career flight with the MCP server enabled, then:
#   $ ./scripts/mcp/smoke-science.sh

set -euo pipefail

discover_url() {
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

PROTO="2025-11-25"
COMMON=( -sS -H "Content-Type: application/json"
         -H "Accept: application/json, text/event-stream"
         -H "Host: 127.0.0.1" )

post() {
  local body="$1"
  curl "${COMMON[@]}" -H "MCP-Protocol-Version: $PROTO" -X POST "$URL" -d "$body"
}

call_tool() {
  local id="$1"
  local name="$2"
  local arguments="$3"
  post "{\"jsonrpc\":\"2.0\",\"id\":$id,\"method\":\"tools/call\",\"params\":{\"name\":\"$name\",\"arguments\":$arguments}}"
}

invoke() {
  local id="$1"
  local path="$2"
  local args="${3:-{}}"
  call_tool "$id" "mj_invoke" "{\"path\":\"$path\",\"args\":$args}"
}

echo "--- initialize ---"
curl "${COMMON[@]}" -X POST "$URL" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"science-smoke","version":"1.0"}}}'
echo

echo "--- notifications/initialized ---"
curl -sS -o /dev/null -w "HTTP %{http_code}\n" "${COMMON[@]}" -H "MCP-Protocol-Version: $PROTO" -X POST "$URL" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'

echo "--- mj_discover science ---"
call_tool 2 "mj_discover" '{"path":"science"}'
echo

echo "--- science/totals ---"
invoke 3 "science/totals"
echo

echo "--- science/earned ---"
invoke 4 "science/earned" '{"limit":10}'
echo

echo "--- science/here ---"
invoke 5 "science/here"
echo

echo "--- science/next_easy ---"
invoke 6 "science/next_easy" '{"limit":10}'
echo

echo
echo "Science smoke OK if discover lists science commands and the command calls return structuredContent without schema errors."
