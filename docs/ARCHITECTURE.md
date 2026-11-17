# Architecture

## Overview

`Trysac.GrpcJsonTranscoding` is a self-contained .NET library that translates REST/HTTP requests into gRPC method identifiers and proto3 JSON payloads (`JsonObject`) according to `google/api/http.proto`.

## Components

1. **Descriptor Model**: Defines protobuf messages and fields using `MessageDescriptor` and `FieldDescriptor`, enabling mapping without a protobuf runtime dependency.
2. **Path Template Engine**: Parses the `google/api/http.proto` grammar. It supports single-segment (`*`), multi-segment (`**`), literals, and variables (`{name=...}`). It handles RFC-compliant percent-decoding, including reserved expansion for multi-segment variables.
3. **Transcoder**: The primary orchestrator. It verifies `HttpRule` configurations (path variables, body/response_body selectors) at construction time. It matches HTTP requests against routes using specificity-based matching (literals > `*` > `**`) and handles variable, body, and query parameter binding precedence.
4. **JSON Mapping**: Maps inputs to `JsonObject` following the proto3 JSON standard, including snake_case to lowerCamelCase conversion, 64-bit integer-to-string serialization, and enum-by-name handling.

## Differences from `grpc-gateway`

1. **Strict Query Parameters**: Unknown query parameters trigger `unknown_field` (400) instead of being ignored.
2. **Conflict Detection**: Fields bound via multiple sources (e.g., path variable + query parameter) trigger `conflicting_binding` (400).
