# weave-copilot

An [ACP](https://agentclientprotocol.com) agent (newline-delimited JSON-RPC 2.0 over stdio) that wraps GitHub's Copilot .NET SDK (`GitHub.Copilot.SDK` 1.0.28, tested against Copilot CLI 1.0.105). Built for Weave Fleet. It runs the Copilot CLI as a child process over stdio (never in-process) and mirrors the shapes of `copilot --acp`.

```
weave-copilot [--copilot-cli <path>] [--hydrafusion]
weave-copilot --version        # prints "weave-copilot 0.2.0", exits 0, does not start the CLI
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
| `session/new` `{cwd, mcpServers}` | `{sessionId, models:{currentModelId, availableModels:[{modelId, name, _meta?:{copilotUsage:"1x", copilotPriceTier:"low", copilotCategory:"lightweight"}}]}}`. `copilotPriceTier` (`low`, `medium`, `high`, `very_high`) and `copilotCategory` (`lightweight`, `versatile`, `powerful`) are GitHub's own labels, passed through as the CLI gives them; a model without them (like `auto`) has no such key, and an older CLI gives none at all. Models come from the SDK's `ListModelsAsync`. Default model `auto` (Copilot's router) when listed, else the first model listed; it is passed to the CLI, so the session runs on the model it reports. A resumed session keeps its own. `mcpServers` is ignored. |
| `_fleet/models` `{cwd?}` | Fleet extension: `{models:[{modelId, name, _meta?}], currentModelId?}` from `ListModelsAsync`, with the `hydrafusion` entry under exactly the same `--hydrafusion` gate and cached probe as `session/new`. It creates no Copilot session, so nothing shows up in the user's Copilot session list. |
| `session/set_model` `{sessionId, modelId}` | Switches model. `session/set_config_option` with `configId:"model"` (`value`) does the same. |
| `session/load` `{sessionId, cwd, mcpServers}` | Resumes, replays history as `session/update` (`user_message_chunk`, `agent_message_chunk`, `tool_call`, `tool_call_update`; no thoughts), then responds `{models}`. |
| `session/prompt` `{sessionId, prompt:[...]}` | Text blocks (plus `resource` / `resource_link`, flattened to text). Responds `{stopReason:"end_turn"\|"cancelled", usage?}` when the session goes idle. A session error (for example `fusion routing transport failed`) responds with a JSON-RPC error (code -32000) whose message is the readable text. It never hangs. |
| `session/prompt` `_meta.modelNotes` | `string[]` beside `sessionId`/`prompt`. Non-empty: the model gets the notes joined by a blank line, then the text; the transcript (and `session/load` replay) shows only the text. |
| `session/cancel` (notification) | Aborts; the prompt returns `cancelled`. |
| `_fleet/usage` `{}` | Fleet extension: `{authenticated, login, plan, resetAt, quotas:[{id:"premium_interactions"\|"chat"\|"completions", entitlement, remaining, percentRemaining, unlimited}]}` from GitHub's `copilot_internal/user`, called with the token the CLI already holds (environment token, else the CLI's `config.json`). The token is never returned or logged. Not signed in: `{authenticated:false, quotas:[]}`. GitHub refusing or answering something unexpected: error -32000 with a plain message. `WEAVE_COPILOT_USAGE_URL` replaces the GitHub address (for tests). |
| `_fleet/compact` `{sessionId, instructions?}` | Fleet extension: compacts the session's history now (manual trigger; `instructions` guide the summary). Responds `{success, tokensRemoved, messagesRemoved}` when done. A turn running: error -32001 "A turn is running; compact the session when it's over". Not successful: error -32000. |
| `_fleet/compaction` (notification) | `{sessionId, phase:"started"\|"ended"\|"failed", trigger:"manual"\|"auto"\|null, tokensBefore, tokensAfter, summary, error}`. Live: `started`, then `ended`/`failed` (an automatic one can end after the turn did). `session/load` replays only `ended`/`failed`. |
| `session/new` / `session/load` `_meta.skillDirectories` | `string[]` of folders whose `<name>/SKILL.md` skills the session gets (beside the folder's own `.github/skills`). Every session also turns on Copilot's config discovery, so the folder's `.github/agents` and `.github/skills` are seen. |
| `_fleet/commands` `{sessionId}` | Fleet extension: `{commands:[{name, description, hint, kind}]}`, `kind` is `builtin`, `skill` or `client`; `hint` is the argument hint or null. All of them; the client filters. |
| `_fleet/agents` `{cwd}` | Fleet extension, no session needed: `{agents:[{name, displayName, description, source}]}` of the custom agents Copilot finds for the folder, leaving out those with `userInvocable: false`. |
| `session/set_config_option` `{sessionId, configId:"agent", value}` | A non-empty `value` runs later turns as that custom agent; `""` or null goes back to Copilot's own. Responds `{}`; an unknown agent is error -32000 with the CLI's message. Not kept across `session/load`: select again. |
| `session/prompt` slash command | A text that starts with `/name` where `name` is in the session's command list is expanded first. A prompt result is sent (the model gets the notes, then the expansion; the transcript shows the typed text). A text or completed result is one `agent_message_chunk` and `end_turn`, with no turn. Anything needing Copilot's terminal (dialogs, subcommand pickers, model or plan switches) is error -32000 "/name needs Copilot's own terminal; Fleet can't show it." An unknown name is plain text. |
| `session/prompt` `_meta.skills` | `string[]`: each name that is a `skill` command is expanded, in order, and goes between the notes and the text; the transcript shows only the text. Other names are skipped, and a prompt that is itself a command takes none. |
| `_fleet/steer` `{sessionId, prompt}` | Sends with delivery mode `immediate` (interjects the running turn). Responds `{}`. Only valid while a `session/prompt` is in flight: with no running turn it responds with error -32001 "No turn is running; send the text as a prompt instead" (queue it as a normal prompt). |
| `_fleet/shell` `{sessionId, command, requestId?}` | Fleet extension: runs a command the user typed (a `!` command) in the session's folder through the CLI's user-requested shell, and responds when it ends: `{toolCallId, success, exitCode, output, error}`. The model sees the output on its next turn. While a prompt is running: error -32001 "A turn is running; run the command when it's over". |
| `_fleet/ask` `{sessionId, question}` | Fleet extension: an off-the-record question (the CLI's ephemeral query). The model sees the whole conversation, nothing is added to it. Responds `{answer}`; `answer` is null when the model says nothing, and for a session with no user message yet (the model isn't called). |
| `_fleet/fork` `{sessionId}` | Fleet extension: copies the session with the CLI's fork and responds `{sessionId, boundary:{id, at, offset}\|null}`. While a prompt runs the copy stops before the message that started it. `boundary` is the newest copied Fleet message, keyed like a replayed one (a user message: its event id and timestamp, offset 1 once the agent started replying to it, else 0; a `!` command: its tool call id and timestamp, offset 0); null when nothing keyed was copied. The fork is not opened here: load it with `session/load`. |

Agent to client: `session/request_permission` `{sessionId, toolCall:{toolCallId, title (e.g. "Write src/foo.ts"), kind, status, rawInput, locations, content?}, options:[allow_once, allow_always, reject_once]}`. `allow_once` approves once, `allow_always` approves for the session (write, read, shell commands, MCP tool; otherwise once), `reject_once` and a cancelled outcome deny.

`session/update` kinds: `agent_message_chunk`, `agent_thought_chunk`, `user_message_chunk` (load only), `tool_call` / `tool_call_update` (`kind`: read, edit, execute, search, fetch, other; `content` with `{type:"diff", path, oldText, newText}` for edits and creates; `locations`; `rawInput`; `rawOutput`), `usage_update {used, size}`: before each model call the context Copilot is about to send; after each of the main agent's calls (not a subagent's) its tokens, with `_meta.call: {input (counting the cached), cacheRead, cacheWrite, output (counting reasoning), reasoning}`. On `session/load` every `user_message_chunk` carries `_meta:{fleetKey:{id, at}}` (the event id and its ISO-8601 timestamp), so a replayed message keeps the same identity every time.

A `!` command is reported with a Fleet extension notification, never as `tool_call`: `_fleet/shell_command {sessionId, toolCallId, phase:"started"|"output"|"ended", at, command? (started), text? (output: an appended chunk, live only), output?, exitCode?, success? (ended)}`. A load replays `started` and `ended` in place among the other updates.

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

## Releases

Pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml`, which builds six self-contained binaries and publishes a GitHub Release. Fleet downloads these, so the names are a contract:

- `weave-copilot-X.Y.Z-<rid>.zip` for `win-x64` and `win-arm64` (holds `weave-copilot.exe`, `THIRD-PARTY-NOTICES`)
- `weave-copilot-X.Y.Z-<rid>.tar.gz` for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` (holds `weave-copilot`, `THIRD-PARTY-NOTICES`)
- `SHA256SUMS`, in `sha256sum` format, covering every archive

The tag sets the version (`weave-copilot --version` prints it). The Copilot CLI is not in the archives; Fleet fetches it from GitHub's own releases. Running the workflow by hand with a `version` input only builds the archives and uploads them as a workflow artifact.

## Build, test, publish

```
dotnet build && dotnet test
dotnet publish src/WeaveCopilot -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained -o publish
```

The publish produces one self-contained `publish/weave-copilot` (about 90 MB). JSON uses source-generated `System.Text.Json` and `JsonNode` only, so no reflection serialization.

## Licence

weave-copilot is MIT-licensed; see [LICENSE](LICENSE). Release archives also carry `THIRD-PARTY-NOTICES` for GitHub.Copilot.SDK (MIT). The GitHub Copilot CLI isn't included in this repository or its releases: Weave Fleet downloads it from GitHub's own releases, under GitHub's licence for it.
