# weave-copilot

An [ACP](https://agentclientprotocol.com) agent (newline-delimited JSON-RPC 2.0 over stdio) that wraps GitHub's Copilot .NET SDK (`GitHub.Copilot.SDK` 1.0.22). Built for Weave Fleet. It runs the Copilot CLI as a child process over stdio (never in-process) and mirrors the shapes of `copilot --acp`.

```
weave-copilot [--copilot-cli <path>] [--hydrafusion]
weave-copilot --version        # prints "weave-copilot 0.1.0", exits 0, does not start the CLI
```

- CLI path: `--copilot-cli <path>`, then env `WEAVE_COPILOT_CLI`, then `copilot` on `PATH`. The Copilot CLI is not shipped.
- `COPILOT_HOME` passes through the environment and is also used as the SDK base directory.
- One process serves one Fleet session, but several `sessionId`s work.
- stdout carries only protocol lines; diagnostics go to stderr. If the CLI child dies the process exits non-zero with a stderr message. SIGTERM/SIGINT/stdin EOF stop the CLI child.
- Login is the CLI's own (`copilot login`); when not signed in, requests fail with a readable error.

## Protocol contract

| Method | Behaviour |
|---|---|
| `initialize` | `{protocolVersion:1, agentCapabilities:{loadSession:true, promptCapabilities:{image:false, embeddedContext:true}}, agentInfo:{name:"weave-copilot", version}, _meta:{cliVersion}}` |
| `session/new` `{cwd, mcpServers}` | `{sessionId, models:{currentModelId, availableModels:[{modelId, name, _meta?:{copilotUsage:"1x"}}]}}`. Models come from the SDK's `ListModelsAsync`. Default model `gpt-5-mini` when listed, else the SDK default. `mcpServers` is ignored. |
| `_fleet/models` `{cwd?}` | Fleet extension: `{models:[{modelId, name, _meta?}], currentModelId?}` from `ListModelsAsync`, with the `hydrafusion` entry under exactly the same `--hydrafusion` gate and cached probe as `session/new`. It creates no Copilot session, so nothing shows up in the user's Copilot session list. |
| `session/set_model` `{sessionId, modelId}` | Switches model. `session/set_config_option` with `configId:"model"` (`value`) does the same. |
| `session/load` `{sessionId, cwd, mcpServers}` | Resumes, replays history as `session/update` (`user_message_chunk`, `agent_message_chunk`, `tool_call`, `tool_call_update`; no thoughts), then responds `{models}`. |
| `session/prompt` `{sessionId, prompt:[...]}` | Text blocks (plus `resource` / `resource_link`, flattened to text). Responds `{stopReason:"end_turn"\|"cancelled", usage?}` when the session goes idle. A session error (for example `fusion routing transport failed`) responds with a JSON-RPC error (code -32000) whose message is the readable text. It never hangs. |
| `session/cancel` (notification) | Aborts; the prompt returns `cancelled`. |
| `_fleet/steer` `{sessionId, prompt}` | Sends with delivery mode `immediate` (interjects the running turn). Responds `{}`. Only valid while a `session/prompt` is in flight: with no running turn it responds with error -32001 "No turn is running; send the text as a prompt instead" (queue it as a normal prompt). |

Agent to client: `session/request_permission` `{sessionId, toolCall:{toolCallId, title (e.g. "Write src/foo.ts"), kind, status, rawInput, locations, content?}, options:[allow_once, allow_always, reject_once]}`. `allow_once` approves once, `allow_always` approves for the session (write, read, shell commands, MCP tool; otherwise once), `reject_once` and a cancelled outcome deny.

`session/update` kinds: `agent_message_chunk`, `agent_thought_chunk`, `user_message_chunk` (load only), `tool_call` / `tool_call_update` (`kind`: read, edit, execute, search, fetch, other; `content` with `{type:"diff", path, oldText, newText}` for edits and creates; `locations`; `rawInput`; `rawOutput`), `usage_update {used, size}`.

### HydraFusion (`--hydrafusion`)

**settings.json.** The Copilot CLI writes `{"experimental": true}` to `<COPILOT_HOME>/settings.json` (default `~/.copilot`) when started with `--experimental`. The SDK's per-session `EnableExperimentalMode` and `FeatureFlags` never write it, but they are not enough alone: with a fresh `COPILOT_HOME`, a session with model `hydrafusion` is only creatable when the CLI is started with `--experimental` (checked with session flags only, and with the `COPILOT_CLI_ENABLED_FEATURE_FLAGS` environment variable: both fail with "Model hydrafusion is not available"). So the write cannot be avoided, and `--hydrafusion` snapshots the user's original `experimental` value (and whether the file existed) before starting the CLI and restores it on exit, SIGTERM and a fatal CLI death. A backup file `.weave-copilot-experimental-backup.json` in the same folder marks an unrestored run, so the next start repairs a crash (kill -9) before snapshotting again. While a `--hydrafusion` process runs, the CLI is experimental, as it must be.


Only with `--hydrafusion` does the agent pass `--experimental` to the CLI and create/resume sessions with `EnableExperimentalMode = true` and the single feature flag `HYDRAFUSION_ROLLOUT`. Without it neither is ever set. The `hydrafusion` model is offered in `session/new` only when `--hydrafusion` is on and a cheap, cached, create-only probe session with that model succeeds.

Each fusion pass is reported with a Fleet extension notification (not `session/update`, so other clients ignore it):

```
_fleet/fusion {sessionId, event:"resolved"|"phase_started"|"phase_completed"|"completed",
               pattern, phase, role, model, status, verdict?, durationMs?,
               credits? (phase, totalNanoAiu/1e9), totalCredits? (completed), index (0-based pass order), hint?}
```

HydraFusion fires tool and message events live and again at commit with `fusion.commitId`. A commit copy is dropped only when its live twin was already emitted (same message id with text, or same tool call id started / completed); with streaming on, an answer can reach us only on the commit copy, and then it is emitted from there. Usage events carrying a commit id are always dropped.

Null fields are omitted from every message written to stdout (raw tool `rawInput` / `rawOutput` are passed through as they are). The stop reason comes from the session going idle, not from turn events.

## Build, test, publish

```
dotnet build && dotnet test
dotnet publish src/WeaveCopilot -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained -o publish
```

The publish produces one self-contained `publish/weave-copilot` (about 90 MB). JSON uses source-generated `System.Text.Json` and `JsonNode` only, so no reflection serialization.
