# Trysac.VirtualActorsLite

This library implements a lightweight, in-process virtual actor model designed for deterministic testing of concurrent entity state management. It solves the problem of race conditions and inconsistency when handling many independent entities (like accounts, devices, or sessions) that require strict single-activation, sequential turn-based processing, and persistent state across a simulated multi-silo cluster.

## Installation

Add the NuGet package `Trysac.VirtualActorsLite` to your project.

## Usage Example

```csharp
using Trysac.VirtualActorsLite;

// Create a cluster with 3 simulated silos
var cluster = TestCluster.Create(3, new ClusterOptions(IdleTimeout: TimeSpan.FromMinutes(2)));

// Get a reference and invoke a method
var counter = cluster.GetActor<CounterActor>("k1");
var result = await counter.CallAsync(a => a.IncrementAsync());
```

## Public API Summary

- **`Actor`**: Base class; implement `OnActivateAsync`, `OnDeactivateAsync`, or `OnReminderAsync`. Access `Context` for persistence and messaging.
- **`ActorContext`**: Provides `ReadStateAsync<T>`, `WriteStateAsync<T>`, `RegisterReminderAsync`, `UnregisterReminderAsync`, and `GetActor<TActor>`.
- **`ActorRef<TActor>`**: Typed reference for `CallAsync` (with or without return values).
- **`TestCluster`**: In-process container managing silos, directory, state, and reminders. Use `AdvanceAsync` for deterministic time progression.
- **`ActorException`**: Throws with stable codes: `etag_conflict`, `invalid_period`, `reentrant_call`, `mailbox_full`, `invalid_key`, `silo_unavailable`, `deactivated`.

## Why not Orleans?

Orleans is a full-scale distributed runtime for production systems. `VirtualActorsLite` is a dependency-free, lightweight alternative designed specifically for local integration tests and simulations. It provides the same virtual actor semantics (single-activation, turn-based execution) while allowing fully deterministic execution using a manual clock, making it ideal for verifying complex logic without the overhead of networking or distributed infrastructure.

See [docs/SECURITY.md](docs/SECURITY.md) for security and resource limits.