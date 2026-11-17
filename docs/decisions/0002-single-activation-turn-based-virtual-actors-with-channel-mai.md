# Single-Activation Turn-Based Virtual Actors with Channel Mailboxes and ManualClock

Status: accepted (generated, pending human review)

## Context

Applications modeling independent stateful entities require single-activation semantics across silos, non-interleaving turn execution, optimistic state persistence, and durable reminders without pulling in a heavy distributed runtime or nondeterministic wall-clock timers.

## Decision

Implement virtual actor activations backed by single-reader System.Threading.Channels loops, an atomic IActivationDirectory with owner forwarding and transparent retry on deactivation races, AsyncLocal call-chain reentrancy detection, System.Text.Json state persistence with opaque etags, and chronological reminder scheduling driven by ManualClock.

## Consequences

Single-activation guarantees, turn isolation, state conflict handling, and multi-period reminder catch-up are completely deterministic and testable in-process with zero external runtime dependencies.
