# Project record

## Objective

Transcode REST JSON requests to gRPC messages by the google.api.http rules: path templates, body and query binding, additional bindings and proto3 JSON names.

## Scope

- In scope: The public API documented in [README.md](../README.md).
- Out of scope: Server streaming, binary protobuf encoding, HTTP/2 framing, and field masks.

## Effort metrics

| Metric | Value |
|---|---|
| Tokens (input / output) | 3215884 / 46783 |
| Hours (human) | pending measurement |
| Cost (USD) | 1.2867 |

## Factory run u934 (measured from API usage)

| Date (UTC) | Phase | Model | Batch | Input | Output | Cache write | Cache read | Cost (USD) |
|---|---|---|---|---|---|---|---|---|
| 2026-11-17 | generation | google/gemini-4-argon | yes | 6116 | 31566 | 0 | 0 | 0.6558 |
| 2026-11-17 | fix-1 | google/gemini-3.8-flash | no | 129688 | 1531 | 0 | 787470 | 0.1621 |
| 2026-11-17 | fix-2 | google/gemini-3.8-flash | no | 140218 | 6596 | 0 | 922645 | 0.1991 |
| 2026-11-17 | fix-3 | google/gemini-3.8-flash | no | 108217 | 4005 | 0 | 601176 | 0.1413 |
| 2026-11-17 | audit | google/gemini-3.8-flash | no | 117074 | 899 | 0 | 381901 | 0.1198 |
| 2026-11-17 | docs | google/gemini-3.1-flash-lite | no | 21379 | 2186 | 0 | 0 | 0.0086 |

Total: $1.2867. Prices: vertex line, as measured by its adapter. Providers serve the current version of each model id, so the date tells which version answered; versions, launch stages and price sources per line are in the factory's model register (docs/MODELS.md).
