# Testing Strategy

Tests rely on deterministic simulation using `ManualClock` instead of wall-clock timers.

- **`VirtualActorsAcceptanceTests`**: Covers concurrency racing, single-activation invariants, persistence, and reminder lifecycles. It verifies that `OnActivateAsync` runs once, even with concurrent calls across silos.
- **Property-Based Tests**: FsCheck properties validate key length constraints, directory ownership election, and `ManualClock` monotonic progression.
- **Scenario Coverage**:
    - `KillSilo`: Ensures directory entries are removed and queued calls fail gracefully with `silo_unavailable`.
    - `AdvanceAsync`: Validates reminder firing order and idle deactivation processing.
    - `Reentrancy`: Confirms rejection of call cycles with `reentrant_call` exception.