namespace AiSharp.Hosting;

/// <summary>
/// OpenAPI 3.1 description of the actual Phase 1 contract.
/// </summary>
public static class OpenApiDoc
{
    public static string Generate() => """
        {
          "openapi": "3.1.0",
          "info": {
            "title": "AI# Phase 1",
            "version": "0.1.0",
            "description": "Sessions, runs, messages + SSE. Provider: opencode-go (muse-spark-1.3-contributor) via OpenAI Responses API. See https://opencode.ai/docs/go/."
          },
          "paths": {
            "/health": { "get": { "summary": "Health + provider/model/protocols", "responses": { "200": { "description": "ok" } } } },
            "/openapi.json": { "get": { "summary": "This document", "responses": { "200": { "description": "OpenAPI JSON" } } } },
            "/v1/sessions": {
              "post": { "summary": "Create session", "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "title": { "type": "string" } } } } } }, "responses": { "200": { "description": "session {id, createdAt, title}" } } },
              "get": { "summary": "List sessions", "responses": { "200": { "description": "sessions" } } }
            },
            "/v1/sessions/{id}": { "get": { "summary": "Get session with messages+runs", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "session" }, "400": { "description": "invalid_session_id" }, "404": { "description": "session_not_found" } } } },
            "/v1/sessions/{id}/messages": { "get": { "summary": "Ordered transcript (durable across restart)", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "messages" }, "400": { "description": "invalid_session_id" }, "404": { "description": "session_not_found" } } } },
            "/v1/sessions/{id}/runs": {
              "get": { "summary": "List runs", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "runs" }, "400": { "description": "invalid_session_id" }, "404": { "description": "session_not_found" } } },
              "post": {
                "summary": "Create and execute a run (blocking JSON by default; SSE with Accept: text/event-stream or ?stream=true)",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                  { "name": "stream", "in": "query", "required": false, "schema": { "type": "string", "enum": ["true"] } }
                ],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object", "required": ["prompt"], "properties": { "prompt": { "type": "string" }, "model": { "type": "string", "description": "Responses-protocol model id; other Go protocols are rejected (Phase 4)" } } } } } },
                "responses": {
                  "200": { "description": "Blocking mode: {runId, sessionId, status, text, usage, providerResponseId}. SSE mode: text/event-stream with run.start {sessionId, runId, model}, text.delta {sessionId, runId, delta}, run.completed {sessionId, runId, status, text, usage, providerResponseId}, run.failed {sessionId, runId, error, message, retryable}." },
                  "400": { "description": "invalid_session_id | empty_prompt | provider BadRequest/ModelUnavailable as JSON {error, message, retryable, runId}" },
                  "404": { "description": "session_not_found" },
                  "408": { "description": "cancelled" },
                  "429": { "description": "rate_limited" },
                  "502": { "description": "upstream auth/server/timeout/truncation {error, message, retryable, runId}" }
                }
              }
            }
          }
        }
        """;
}
