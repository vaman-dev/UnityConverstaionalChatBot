# Test suite policy

Tests protect customer-visible behavior, wire contracts, architecture boundaries, and costly regressions. They do not archive migrations or restate implementation.

## Ownership

- Put tests under the feature they protect: `LipSync`, `Vision`, `Transcripts`, `Emotion`, `Gaze`, and similar folders.
- Use `Runtime` for public component orchestration and `Infrastructure` for transport/protocol behavior.
- Use `Samples` for shipped sample code and `Release` for platform/package smoke gates.
- Keep `PlayMode` for frame ordering and Unity lifecycle behavior that cannot be proved in Edit Mode. Prefer `EditMode` otherwise.
- Keep reusable doubles in `Fixtures` or `Mocks`. Extend an existing double before creating another local implementation.

## Keep or delete

Keep a test when its failure identifies a broken behavior or contract. Delete or merge tests that only prove:

- constructors assign properties;
- events can be subscribed or unsubscribed;
- a method does not throw without checking resulting state;
- a deleted type, file, wording, migration phase, or temporary compatibility bridge remains absent;
- private implementation details, unless reflection is required by a deliberate public-surface or architecture guard.

Prefer one scenario with strong state and event assertions over many one-assertion tests. Parameterize genuine input matrices. Use immediate schedulers, injected clocks, and mock transport packet injection instead of sleeps.

## Verification

Run focused Edit Mode tests while iterating. Before merge, run all package Edit Mode tests and the small package Play Mode lifecycle suite. Use the pinned Unity editor in batch mode when no editor owns the project, or run `Convai → Developer → Automation Testing → Run PR Validation` in-editor.

Repository-root tests under `Assets/Convai/Tests` are developer automation and live validation, not package test ownership. Keep backend-dependent validation explicitly opt-in and do not interpret generated `TestReport` output as a current run.
