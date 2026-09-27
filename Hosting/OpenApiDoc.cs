namespace AiSharp.Hosting;

/// <summary>
/// Minimal OpenAPI 3.1 description of the actual Phase 1 contract.
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
            "/health": { "get": { "summary": "Health + provider/model", "responses": { "200": { "description": "ok" } } } },
            "/openapi.json": { "get": { "summary": "This document", "responses": { "200": { "description": "OpenAPI JSON" } } } },
            "/v1/sessions": {
              "post": { "summary": "Create session", "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "title": { "type": "string" } } } } } }, "responses": { "200": { "description": "session" } } },
              "get": { "summary": "List sessions", "responses": { "200": { "description": "sessions" } } }
            },
            "/v1/sessions/{id}": { "get": { "summary": "Get session with messages+runs", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "session" }, "404": { "description": "not found" } } } },
            "/v1/sessions/{id}/messages": { "get": { "summary": "Ordered transcript (durable across restart)", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "messages" } } } },
            "/v1/sessions/{id}/runs": { "get": { "summary": "List runs", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }], "responses": { "200": { "description": "runs" } } } },
            "/v1/sessions/{id}/runs-stream-note": { "get": { "summary": "SSE via POST /v1/sessions/{id}/runs with Accept: text/event-stream", "responses": { "200": { "description": "events: run.start, text.delta, run.completed, run.failed" } } } }
          }
        }
        """;
}
