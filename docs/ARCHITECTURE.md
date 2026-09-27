# Architecture sketch

**Status:** proposed boundaries, not implemented.

```text
CLI / UI / other service
        │
        ▼
HTTP + SSE adapter          later: gRPC / Unix domain socket adapters
        │
        ▼
Application operations ── session and run store ── event log
        │
        ▼
Run engine ──────────────── tool registry / executor / policy
        │
        ▼
Provider adapter(s) ─────── model provider APIs
```

## Core boundaries

- **Application operations** create sessions and runs, read history, subscribe to events, and request cancellation. They return transport-neutral results and errors.
- **Run engine** owns the state machine and ordering. It decides when to call the provider, when a tool call is ready to execute, what to persist, and when a run is terminal.
- **Provider adapters** translate each provider's wire format into common events while preserving useful provider-specific details and capability flags. The engine must not rely on provider-hosted chat state as the canonical record.
- **Tool executor** looks up registered tools, validates arguments, applies policy, executes with cancellation and limits, then records a result or failure. A provider's request to call a tool is data until this layer authorizes and executes it.
- **Storage** preserves ordered messages, run transitions, tool outcomes, and event sequence numbers. It should support recovery after process restart.
- **Transport adapters** expose the same operations over different protocols. The first implementation is HTTP with SSE; transport-specific types do not enter the core model.

## Suggested run states

`queued → running → completed | failed | cancelled`

A running turn may alternate between provider streaming and tool execution. Persist each transition and give every emitted event a run-scoped sequence number. A client reconnecting with the last seen event ID can replay missed events before receiving live events. Terminal state must be durable even when the client disconnects.

## Data and compatibility rules

- Use AI# identifiers for sessions, runs, messages, and tool calls; keep provider IDs as metadata.
- Store structured content parts and tool calls, not just concatenated display text.
- Preserve raw provider metadata only where useful for debugging or future translation, with redaction and retention limits.
- Define a small normalized event set for clients: run state, text delta, tool-call request, tool result, usage, and error. Include versioning before external clients depend on it.
- Treat cancellation as a requested state change: stop work promptly, then record the actual terminal outcome. Do not claim a provider or tool side effect was undone.
- Avoid simultaneous writes to one session in the MVP; serialize or reject competing runs explicitly.

## Early design choices

| Topic | Initial direction | Reason |
| --- | --- | --- |
| Deployment | One process and local durable storage | Proves the engine before distributed coordination. |
| Public API | HTTP resources and SSE events | Easy for varied clients to integrate and inspect. |
| Provider integration | One direct adapter first | Exposes real streaming and tool-call constraints early. |
| Tools | Registered, validated, policy-gated | Keeps execution under AI# control. |
| Code organization | Separate core interfaces from infrastructure; split projects when useful | Keeps the initial vertical slice small without coupling the engine to HTTP or a provider SDK. |

These are proposals. Record concrete choices and their tradeoffs as the implementation proceeds.

## OpenCode reference

A shallow OpenCode clone is available at `~/projects/opencode` (`/home/aimaster/projects/opencode`). Its current `packages/` tree includes `ai`, `core`, `protocol`, `server`, and `sdk`, which are useful places to inspect when designing provider, runtime, and client boundaries. It is inspiration and reference material; AI# does not depend on or wrap that checkout. The [OpenCode server docs](https://opencode.ai/docs/server/) describe its client/server API.
