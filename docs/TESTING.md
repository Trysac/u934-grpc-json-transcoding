# Testing

## Policy

- Line coverage must stay at or above 80%.
- Tests exercise the public API, asserting return values, thrown `TranscodingException` types, and exception codes.
- No network dependencies, no clock reliance, deterministic execution.

## Structure

- Unit tests: Located in `tests/`, covering specific components (`PathTemplateTests.cs`, `TranscoderTests.cs`).
- Fixtures: `tests/fixtures/` contains the JSON representations of `google/api/http.proto` reference examples, which the library uses to verify compliance.
- Property tests: Uses FsCheck to verify routing specificity and round-trip consistency.

## Execution

```sh
dotnet test
dotnet run --project scripts/Check -- # Run gates
```
