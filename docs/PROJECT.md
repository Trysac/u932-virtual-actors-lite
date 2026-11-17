# Project record

## Objective

Run Orleans-style virtual actors in process with on-demand single activation per key, turn-based mailboxes, etag-checked persistent state, and durable reminders for deterministic testing.

## Scope

- In scope: The core virtual actor runtime including `Actor` base class, `TestCluster` implementation, persistence stores, and in-memory directory for activation routing.
- Out of scope: Networking stack, cross-process serialization, grain interface code generation, streaming, transaction support, and non-round-robin placement strategies.

## Timeline

| Date | Event |
|---|---|
| 2026 | Repository created from the factory utility template. |

## Effort metrics

| Metric | Value |
|---|---|
| Tokens (input / output) | 3034125 / 32679 |
| Hours (human) | pending measurement |
| Cost (USD) | 1.0743 |

## Factory run u932 (measured from API usage)

| Date (UTC) | Phase | Model | Batch | Input | Output | Cache write | Cache read | Cost (USD) |
|---|---|---|---|---|---|---|---|---|
| 2026-11-17 | generation | google/gemini-4-argon | yes | 5694 | 26437 | 0 | 0 | 0.5515 |
| 2026-11-17 | fix-1 | google/gemini-3.8-flash | no | 168767 | 3480 | 0 | 1593568 | 0.2591 |
| 2026-11-17 | audit | google/gemini-3.8-flash | no | 233451 | 1493 | 0 | 1012083 | 0.2566 |
| 2026-11-17 | docs | google/gemini-3.1-flash-lite | no | 20562 | 1269 | 0 | 0 | 0.0070 |

Total: $1.0743. Prices: vertex line, as measured by its adapter. Providers serve the current version of each model id, so the date tells which version answered; versions, launch stages and price sources per line are in the factory's model register (docs/MODELS.md).
