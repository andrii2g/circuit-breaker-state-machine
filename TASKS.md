# Codex Task Checklist

Execute these tasks in order and keep commits or logical patches small enough to review.

- [x] Bootstrap .NET 10 `.slnx`, projects, references, central packages, and build settings.
- [x] Implement Core enums/options/result/snapshot types.
- [x] Implement `CircuitBreaker` with injected `TimeProvider`.
- [x] Add deterministic Core transition tests.
- [x] Add exact half-open race test without sleeps.
- [x] Implement scenario domain, validation, and periodic request expansion.
- [x] Implement deterministic fake downstream service.
- [x] Implement immutable simulation events and run-result model.
- [x] Implement baseline runner.
- [x] Implement breaker-protected runner.
- [x] Assert schedule identity between baseline and protected runs.
- [x] Implement metrics derivation.
- [x] Implement recovery-latency and outage-load calculations.
- [x] Implement six built-in scenarios.
- [x] Implement JSON scenario loader and example validation.
- [x] Implement CLI parsing and error handling.
- [x] Implement JSON result exporters.
- [x] Implement Mermaid state-machine exporter.
- [x] Implement bounded/readable Mermaid sequence exporter.
- [x] Implement offline HTML/SVG timeline exporter.
- [x] Add deterministic reporting tests.
- [x] Complete README examples and verify docs contain Mermaid rather than ASCII diagrams.
- [x] Run Release restore/build/test gates.
- [x] Run at least `long-outage` and `flapping`; inspect generated artifacts.
