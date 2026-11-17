# Trysac.GrpcJsonTranscoding

Transcode REST JSON requests to gRPC messages by the `google.api.http` rules: path templates, body and query binding, additional bindings and proto3 JSON names.

## Why Trysac.GrpcJsonTranscoding?

Teams that expose gRPC services to browsers and partners need REST routes that follow the `google.api.http` annotations. Hand-written mappings often drift from proto definitions, and existing transcoders are coupled to specific reverse proxies or ASP.NET Core hosting. `Trysac.GrpcJsonTranscoding` implements the `google/api/http.proto` path template grammar and binding rules as a host-independent, testable pure function from an HTTP request to the target gRPC method and its request message in proto3 JSON, with zero runtime dependencies.

## Why not `grpc-gateway`?

`grpc-gateway` is a powerful tool, but it is tied to the Go ecosystem. `Trysac.GrpcJsonTranscoding` is a pure .NET library. Unlike `grpc-gateway`, which ignores unknown query parameters by default, `Trysac.GrpcJsonTranscoding` explicitly rejects unknown fields and conflicting bindings (fields bound in path/query/body simultaneously) to prevent parameter smuggling and catch API drift early.

## Installation

Add the library to your .NET project:

```bash
dotnet add package Trysac.GrpcJsonTranscoding
```

## Usage

```csharp
using Trysac.GrpcJsonTranscoding;

var request = new MessageDescriptor("GetMessageRequest", [new FieldDescriptor("name", FieldType.String)]);
var response = new MessageDescriptor("Message", [new FieldDescriptor("name", FieldType.String)]);
var binding = new MethodBinding("Messaging.GetMessage", new HttpRule("GET", "/v1/{name=messages/*}"), request, response);
var transcoder = new Transcoder([binding]);

TranscodedRequest call = transcoder.TranscodeRequest("GET", "/v1/messages/123456", jsonBody: null);
```

## Public API Summary

- **Models**: `HttpRule`, `MethodBinding`, `MessageDescriptor`, `FieldDescriptor`, `FieldType` (Scalar types: `String`, `Bytes`, `Bool`, `Int32/64`, `UInt32/64`, `Double`, `Float`, `Enum`, `Message`).
- **Engine**: `PathTemplate` (Parses grammar: `*`, `**`, literals, variables), `Transcoder` (Validates bindings, routes HTTP to gRPC, maps JSON).
- **Errors**: `TranscodingException` (`Code` and `HttpStatus` properties). Codes: `not_found` (404), `method_not_allowed` (405), `invalid_body` (400), `unknown_field` (400), `invalid_value` (400), `conflicting_binding` (400), `input_too_large` (413/400), `invalid_template` (500), `invalid_rule` (500).
- **Limits**: URL 8,192 chars; JSON body 4 MiB; 1,000 query params; 32 levels field depth; 64 levels JSON depth.

See [docs/SECURITY.md](docs/SECURITY.md) for threat modeling and limits.
