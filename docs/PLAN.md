# Initial implementation plan

**Status:** proposed; planning only. The repository currently contains a placeholder console program.

## Delivery principle

Build one end-to-end path before broadening features: client request → durable run → direct provider call → streamed events → tool result → durable final answer. Keep provider protocols and public transports at the edges of the engine. Each phase should leave a runnable, inspectable slice.

## Phase 0 — Foundation

- Define the core types and state transitions for sessions, messages/content parts, runs, tool calls, and events.
- Implement a transport-neutral run-state skeleton exercised with fake provider and tool dependencies.
- Choose a durable local store and migration approach; document the tradeoff in [architecture](ARCHITECTURE.md).
- Set up tests using a fake provider and a fake tool. Keep these tests focused on observable run behavior, cancellation, and persistence.
- Decide the first real provider and document its supported capabilities rather than pretending all providers behave identically.

**Done when:** the domain model and a run-state contract are documented, and tests can exercise the engine without a network provider.

## Phase 1 — Direct provider and conversation slice

- Implement session creation and durable message history.
- Add a provider interface for request, streamed response events, cancellation, tool-call data, errors, and usage; implement one direct provider adapter.
- Add the smallest turn executor that sends a text prompt, records the response, and reaches a clear success or failure state.
- Add a local HTTP host with session/run operations and SSE streaming; publish OpenAPI for the actual contract.

**Done when:** a separate client can create a session, stream a real model response, restart the service, and retrieve the same transcript.

## Phase 2 — Owned tool loop

- Implement the tool registry, input validation, execution policy, timeout, and output limits.
- Persist tool-call intent, execution result, and failures with stable IDs before continuing the model turn.
- Support a bounded model → tool → model loop using a safe demonstration tool.
- Add cancellation while a provider request or tool is active, plus a clear interrupted-run state.

**Done when:** an end-to-end run can call a tool and continue to a final assistant answer; replayed history shows every step without relying on provider-side conversation state.

## Phase 3 — Service hardening

- Make event delivery reconnectable through ordered, persisted event IDs.
- Add configuration validation, secret handling, model/provider capability checks, and actionable error responses.
- Add structured logs and correlation IDs; record provider usage when reported, without inventing precise cost figures.
- Test restart recovery, duplicate client submissions, provider timeouts, malformed tool arguments, and concurrent access to a session.
- Define the policy for non-loopback binding and authentication before enabling remote clients.

**Done when:** the MVP acceptance scenario in [scope](SCOPE.md) works under failures and restart, not only on the happy path.

## Phase 4 — Additional adapters and transports

- Add a second provider to test the abstraction against real protocol differences.
- Add gRPC where typed streaming or internal service integration justifies it.
- Add Unix domain socket hosting for local clients, using the same application operations and event semantics.
- Version the API and publish client examples after the contract has stabilized.

**Done when:** clients using different transports observe the same session and run state, and provider-specific differences remain visible as capabilities rather than silent feature loss.

## Future control-plane work

Only after the single-node engine is reliable, design shared state, worker identity, scheduling, leases, failure recovery, quotas, and tenancy. The engine's durable run IDs and event semantics should make that migration possible, but Phase 0–4 do not promise distributed execution.

## Decisions to make during implementation

1. Which provider and model offer the first direct integration with usable streaming tool calls?
2. Which local store best fits append-heavy transcripts and replayable events, and what retention controls are needed?
3. What is the exact HTTP resource and event schema? Resolve this with one client integration rather than guessing every future client need.
4. Which tool actions may run automatically, and what approval protocol must a client use for higher-risk actions?
5. What authentication model is required before the service binds beyond loopback?

## Success measure

The first release succeeds when a client can operate a complete AI run through AI#'s own API, including a tool round trip, and the service can reconstruct the run from its own durable records. No external coding-agent process is required for that path.
