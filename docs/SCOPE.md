# Scope: AI# engine

**Status:** initial proposal

## Purpose

AI# is the service that owns AI conversations and execution. It accepts client requests, persists the canonical session state, talks directly to model providers, handles model-requested tools, and reports progress back to clients. It is the AI operations core for a possible future distributed control plane.

The first release targets a single running service with durable local state. It should prove the engine and its API before adding distributed coordination.

## Intended users and integrations

- A local CLI or UI that creates sessions, sends messages, and watches runs.
- Another service that embeds AI workflows through a network or local-process API.
- Eventually, a control plane that schedules work across multiple AI# instances.

AI# is headless by design. A full client experience can be built separately.

## Ownership boundary

AI# is the source of truth for:

| Area | Responsibility |
| --- | --- |
| Sessions | Identity, metadata, ordering, and durable message history. |
| Runs and flows | One execution of the assistant loop, including status, cancellation, tool steps, and terminal outcome. Longer, reusable workflows come later. |
| Provider calls | Model selection, request/response translation, streaming deltas, errors, and reported usage. |
| Tools | Registered definitions, validated arguments, execution, results, and policy decisions. |
| Events | A stable stream of run lifecycle and output events for clients. |

Clients own their presentation and user interaction. Provider adapters own protocol-specific translation. The engine owns the sequence of decisions and the durable record.

## First usable milestone (MVP)

The MVP includes:

1. **Sessions and messages:** create/list/get sessions, append a user message through a run request, and retrieve ordered history after a process restart. Store structured content parts so later multimodal support does not require replacing the message model; initially support text and tool-related parts.
2. **One real provider:** configure one direct provider adapter with credentials from environment or local secret configuration. Select a model that supports text streaming and tool calls; support model selection, normalized errors, and provider-reported usage. Use a deterministic fake provider for tests.
3. **Run loop:** create a run, assemble conversation context, stream model output, execute a registered tool, feed its result back to the provider, and reach a terminal state. Bound tool-loop iterations and output size. Make cancellation observable and stop in-flight work when possible.
4. **Tools:** a registry with input contracts, argument validation, result/error records, and a policy gate before execution. Demonstrate the loop with a small safe built-in tool; arbitrary shell or filesystem mutation is outside the first milestone.
5. **HTTP API:** endpoints for sessions and runs plus server-sent events (SSE) for run progress. Publish an OpenAPI description. Keep application behavior independent of HTTP request objects so another transport can call the same engine.
6. **Local persistence and diagnostics:** durable session/run/message/tool records, stable event IDs for replay after reconnect, structured logs with correlation IDs, and basic provider usage data.
7. **Local security baseline:** bind to loopback by default; avoid logging credentials or full sensitive payloads; require an explicit authentication/configuration decision before listening on a non-loopback interface.

### Acceptance scenario

A client starts AI#, creates a session, submits a prompt, and receives ordered events for text output and one tool call. The run finishes with a persisted assistant response. After restarting AI#, the client can read the same session history and run outcome. A second run can reuse that history. Cancellation and provider failure each produce a terminal state; provider failure also produces an actionable error event.

## Later, after the MVP

- More provider adapters and a capability matrix for provider/model differences.
- More content types, including images and attachments, when a concrete client needs them.
- Richer tool hosts, external tool protocols, and per-tool approval workflows.
- Reusable multi-step flow definitions and scheduling beyond a single interactive run.
- gRPC and Unix domain socket listeners over the same application services.
- Multiple concurrent clients, stronger auth, quotas, and tenant isolation.
- Distributed scheduling, worker placement, shared state, failover, and fleet management as a separate control-plane phase.

## Out of scope for this repository's first milestone

- Driving Codex, Claude Code, or OpenCode as a subprocess or delegating the core session to them.
- A full TUI, web UI, IDE extension, or coding-agent-specific file editing workflow.
- A provider marketplace or broad model catalog.
- Cluster membership, distributed consensus, cross-node run migration, or a hosted multi-tenant service.
- Exposing arbitrary local tool execution to unauthenticated remote clients.

## Working assumptions to validate

- The initial deployment is local or on a trusted single host; remote exposure needs an explicit auth design.
- HTTP plus SSE is the simplest first client contract; gRPC and Unix domain sockets are transport additions rather than separate engines.
- A local transactional store is sufficient for the MVP. The exact store and migration scheme are chosen during the persistence work.
- The first real provider is selected when implementation begins, based on available credentials and the smallest useful tool-streaming integration.
