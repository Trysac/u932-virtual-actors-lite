# Security & Resource Limits

## Threat Model

- **Defended**: Unbounded resource growth in actors (via bounded mailboxes), accidental concurrency reentrancy (via `AsyncLocal` call-chain tracking), invalid actor identity or naming conventions, and inconsistent state writes (via opaque etags).
- **Not Defended**: This library is intended for in-process testing. It does not provide network-level security, sandboxing, or protection against malicious code execution within the host process.

## Limits

- **Actor Keys & Names**: Max 256 characters (enforced by `ActorId.ValidateKey`).
- **Mailbox Capacity**: `ClusterOptions.MailboxCapacity` defaults to 1,000; exceeding this throws `mailbox_full`.
- **State Storage**: `InMemoryStateStore` limits payloads to 1 MiB (`MaxStateBytes`) and total entries to 100,000. `JsonSerializerOptions` recursion depth is capped at 32.
- **Directory Capacity**: `InMemoryActivationDirectory` capped at 100,000 entries.