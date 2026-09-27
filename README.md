# AI#

AI# is a planned C# service for the intrinsic work of an AI assistant: talking to model providers, owning conversations and runs, streaming responses, and coordinating tool calls. Applications will connect to the service through a stable API instead of each application rebuilding its own agent loop.

The long-term direction is a distributed control plane. This repository starts with the AI engine that such a control plane could use. The first target is one durable, single-node service.

> **Current status:** Phases 1–3 implemented. OpenCode Go provider (`muse-spark-1.3-contributor`) with streaming text, an owned `calc`/`time_now` tool loop, durable sessions/runs/tool-calls/event replay, and an HTTP+SSE API. See `ai-sharp --help`.

## Repository today

One `net11.0` project, no external package dependencies. The API key is read from `OPENCODE_GO_API_KEY` or `~/.config/ai-sharp/opencode-go.key` (never logged); transcripts persist under `./data` (or `AI_SHARP_DATA_DIR`).

```sh
dotnet build
./bin/Debug/net11.0/ai-sharp doctor     # validate config, credential, store, provider
./bin/Debug/net11.0/ai-sharp selftest   # offline engine tests
./bin/Debug/net11.0/ai-sharp chat -m "What is 12*13? Use calc."
./bin/Debug/net11.0/ai-sharp serve --port 5111
```

## What the service will own

- The canonical session, message, run, and tool-call records.
- Provider and model selection, request translation, streaming, and usage accounting.
- The run and flow lifecycle: assemble context, call a model, handle tool requests, return tool results, and finish or cancel work.
- Tool registration, input validation, execution policy, and audit events.
- A transport-neutral application layer exposed through HTTP first, with gRPC and Unix domain socket access considered after the core behavior works.

Clients may be a CLI, web app, IDE integration, automation, or a later control plane. AI# owns the transcript and execution state they observe and control.

## First usable milestone

Run the service locally, create a session, send a prompt to a directly integrated model provider, stream the run's events, complete a simple tool call, and read the persisted transcript after restart. See [scope](docs/SCOPE.md) and the [implementation plan](docs/PLAN.md) for the proposed boundaries and sequence.

## Project direction

OpenCode is an inspiration for a client/server assistant that owns its [sessions and messages](https://opencode.ai/docs/server/), [provider integration](https://opencode.ai/docs/providers/), and [tools](https://opencode.ai/docs/tools/). AI# is its own C# implementation with its own API and scope; it is not a controller for OpenCode, Codex, or Claude Code.

A shallow clone of OpenCode is available at `~/projects/opencode` for local reference. It is reference material, not a dependency of AI#.

## Repository today

One `net11.0` project, no external package dependencies. The API key is read from `OPENCODE_GO_API_KEY` or `~/.config/ai-sharp/opencode-go.key` (never logged); transcripts persist under `./data` (or `AI_SHARP_DATA_DIR`).

```sh
dotnet build
./bin/Debug/net11.0/ai-sharp doctor     # validate config, credential, store, provider
./bin/Debug/net11.0/ai-sharp selftest   # offline engine tests
./bin/Debug/net11.0/ai-sharp chat -m "What is 12*13? Use calc."
./bin/Debug/net11.0/ai-sharp serve --port 5111
```

## Planning docs

- [Scope and first milestone](docs/SCOPE.md)
- [Phased implementation plan](docs/PLAN.md)
- [Architecture sketch and design decisions](docs/ARCHITECTURE.md)

The plans are initial proposals. They should change as the first vertical slice exposes real constraints.
