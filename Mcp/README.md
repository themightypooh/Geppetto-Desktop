# MCP server (stdio) — Geppetto Desktop

`marionette-desktop --mcp` speaks the [Model Context Protocol](https://modelcontextprotocol.io/)
over stdio: newline-delimited JSON-RPC 2.0 on stdin/stdout. No HTTP/SSE in v1,
no new dependency (System.Text.Json is the house JSON stack).

## Run it

```sh
marionette-desktop --mcp
```

One JSON value per line in, one JSON response per line out. Diagnostics go to
stderr; stdout carries protocol traffic only. EOF on stdin (or a `shutdown`
request) exits 0. A bad line yields a JSON-RPC error object, never a crash,
a hang, or a stack trace on stdout.

## Client config (Claude Desktop)

```json
{
  "mcpServers": {
    "geppetto-desktop": {
      "command": "marionette-desktop",
      "args": ["--mcp"]
    }
  }
}
```

Generic MCP clients: same command, stdio transport, protocol version
`2024-11-05` (the server echoes the client's requested version when one is
given).

## Methods

| Method | Result |
| --- | --- |
| `initialize` | `protocolVersion`, `capabilities` (`tools` always; `resources` / `prompts` when registered), `serverInfo` (`geppetto-desktop` / `1.0.0`) |
| `ping` | `{}` |
| `tools/list` | The `AiSuiteTools` definitions mapped verbatim to MCP shape (`parameters` → `inputSchema`), plus `run_script`, plus an `echo` tool |
| `tools/call` | `{content: [{type: "text", text}], isError}` — suite tools run through `McpModelingTools.CallAsync`; `run_script` executes BuildScript lines on a copy of the studio |
| `resources/list` | The four `geppetto://` resources (document, document/mesh, tests/latest, catalog) |
| `resources/read` | `{contents: [{uri, mimeType, text}]}` |
| `prompts/list` | The four prompts with their arguments |
| `prompts/get` | `{description, messages: [{role, content: {type: "text", text}}]}` |
| `shutdown` | `{}` then a clean exit 0 |

Notifications (no `id`, e.g. `notifications/initialized`) get no response.
Batch arrays are rejected with `-32600` (one JSON value per line).

## Smoke test

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hello"}}}
{"jsonrpc":"2.0","id":4,"method":"shutdown"}
```

Expect four response lines: initialize (capabilities incl. tools, resources,
prompts), tools/list (43 AiSuiteTools + `run_script` + echo), tools/call (echo returns the
arguments as text), shutdown (`{}`), then exit 0.

## Error codes

`-32700` parse error (id null), `-32600` invalid request (bad `jsonrpc`,
missing method, batch array), `-32601` method not found, `-32602` invalid
params (unknown tool, missing `name`/`uri`, unknown resource/prompt, missing
required prompt argument, non-string prompt argument), `-32603` internal
error. A listed tool with no executor yet fails with `-32602` and a message
saying so.

## Safety (wired in, not optional)

- At startup the server calls `SafetyGates.ValidateDefinitions` on the live
  tool list AND on the full MCP list (definitions + `run_script`): if any
  definition names a `shell`/`exec` tool it refuses to start (exit 1, stderr only).
- Every non-`echo` `tools/call` runs inside `SafetyGates.BeginCall("mcp",
  ...)`: the tool must be known, arguments are capped (256 KiB) and the
  whole request is capped (1 MiB — enforced on every line, not just tool
  calls), file-looking arguments must resolve inside the run dir, and
  destructive tools (`remove_feature`) are refused unless the call's
  arguments carry `"confirm": true`. Refusals return `-32602` and are
  written to the audit log.
- KNOWN GAP: `BeginCall` builds its known-tool set from
  `AiSuiteTools.Definitions` only, so it refuses `run_script` (an MCP-only
  tool defined by `McpModelingTools`). Until `SafetyGates` learns the tool
  (extend the known set or add a `RegisterKnownTool` API — a security-ticket
  / integrator follow-up, not a change to make here), `run_script` takes a
  manual path: the arg/request size caps are enforced, the forbidden check
  runs at startup on the full list, and the call is dispatched — but it gets
  NO `GuardedCall` audit entry and no verb-level destructive gating (the
  script text is opaque to the gate).
- Every tool result is passed through `SafetyGates.ScrubOutput` before it
  leaves: secret keys and `sk-` / `ghp_` / `AKIA` / `Bearer` shapes come
  out as `[redacted]`.
- The run dir (and its append-only `audit.log`) is a per-process temp dir
  (`/tmp/geppetto-mcp-<pid>`). `echo` bypasses `BeginCall` because it is
  not an AiSuiteTools tool and performs no operation, but its output is
  still scrubbed and the 1 MiB request cap covers it.

## Seams for the sibling MCP tickets

- **Tools** (`mcp-tools-modeling-operations`, landed): `tools/list` is
  `McpModelingTools.ListTools()` (definitions verbatim + `run_script`) plus
  `echo`; `tools/call` dispatches suite tools through
  `McpModelingTools.CallAsync` (isolated child-process routing preserved).
  `McpServer.RegisterToolHandler(name, args => result)` overrides the
  modeling dispatcher for one tool — the registered handler still runs
  inside the safety gate.
- **Resources/prompts** (`mcp-resources-prompts-document-mesh-tests`,
  landed): the handlers delegate to `McpResources`; its `ArgumentException`s
  map to `-32602`. Live state comes in through `McpServer.DocumentSource`
  (`CurrentDocument` / `LatestTestRun`); until the host fills it, the document
  resources honestly report `open:false` / `recorded:false`.
- **Methods**: `McpServer.RegisterMethod(method, params => result)` adds any
  further method; `initialize` advertises it automatically.
- **Safety** (`mcp-cli-security-safety-gates`, landed): the guard module is
  `McpSafety.cs` at the Desktop root; the transport calls it as described
  above. Policy defaults (1 MiB request / 256 KiB args / 5 min call
  timeout / destructive off) live in `SafetyPolicy`.

## Files

- `Mcp/JsonRpc.cs` — JSON-RPC 2.0 message shapes (parse, success/error, serialize).
- `Mcp/McpServer.cs` — the stdio loop, method dispatch, tools/resources/prompts wiring, safety gates.
- `tests/McpServerTests.cs` — group `mcp-server`, 15 checks, driven through
  in-memory transcripts (no process spawn, no window, no network).
- `Program.cs` — one line: `--mcp` dispatches to `Mcp.McpServer.Run`.
