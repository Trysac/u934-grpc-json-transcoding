# Descriptor-Driven Proto3 JSON Transcoding Without Protobuf Runtime

Status: accepted (generated, pending human review)

## Context

Teams exposing gRPC APIs over REST according to google/api/http.proto need an accurate, host-independent transcoder. Existing solutions either require running an external Go proxy (grpc-gateway) or coupling to ASP.NET Core hosting and compiled protobuf assemblies (Microsoft.AspNetCore.Grpc.JsonTranscoding).

## Decision

Implement the google/api/http.proto path template parser, route matcher, and request/response binding rules against a lightweight immutable descriptor model (MessageDescriptor and FieldDescriptor) producing System.Text.Json.Nodes.JsonObject payloads with zero external runtime dependencies. Enforce strict validation by rejecting unknown query parameters ('unknown_field') and duplicate bindings across path, query, and body ('conflicting_binding').

## Consequences

The library has zero third-party dependencies, runs in any .NET host or test suite, and protects against parameter overriding and silent client typos, while allowing callers to pass the resulting proto3 JSON directly into Google.Protobuf.JsonParser or any gRPC client.
