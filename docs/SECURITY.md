# Security & Threat Model

## Threats

1. **Resource Exhaustion (DoS)**: Attacker provides oversized URLs, massive JSON payloads, or deep nesting to exhaust memory/CPU.
2. **Parameter Smuggling**: Attacker overwrites path variables via query or body to bypass upstream security.
3. **Path Traversal**: Attacker exploits percent-encoded characters (like `%2F`) to escape path segments.

## Mitigations

- **Size Limits**: URL/Query (8,192 chars), JSON body (4 MiB), Query params (1,000 count), Field depth (32 levels), JSON depth (64 levels).
- **Binding Safety**: `conflicting_binding` (400) prevents fields from being bound multiple times.
- **Encoding**: Multi-segment variables preserve `%2F` (reserved expansion) to block path traversal.
- **Error Safety**: `TranscodingException` messages are truncated to 200 chars and never echo raw request bodies.
