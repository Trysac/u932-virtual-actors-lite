# Architecture

`Trysac.VirtualActorsLite` implements the virtual actor programming model in-process over three pluggable stores (`IActivationDirectory`, `IStateStore`, `IReminderStore`) and a deterministic `ManualClock`.

## Core Components

1. **`TestCluster` and `Silo`**:
   - `TestCluster` creates $N$ logical silos (`"s1"`, `"s2"`, ...) sharing a directory, state store, reminder store, and `ManualClock`.
   - Incoming calls via `ActorRef<TActor>.CallAsync` pick a gateway silo using round-robin routing (or a custom `ClusterOptions.Router`).
   - The gateway silo queries `IActivationDirectory.TryRegister`. If another live silo already owns the `ActorId`, the gateway forwards the message to the owner silo.

2. **`ActorActivation` and Turn-Based Mailbox**:
   - Each activation owns a single-reader channel loop.
   - On the first message, the loop instantiates the actor with `new()`, injects `ActorContext`, and awaits `OnActivateAsync()` before executing the message.
   - Queue capacity is enforced against `ClusterOptions.MailboxCapacity`; when 1 call is executing and `MailboxCapacity` calls are queued, the next call throws `ActorException` with code `mailbox_full`.
   - On idle deactivation (`InitiateDeactivateAsync`), the activation stops accepting new messages, drains already queued messages, awaits `OnDeactivateAsync()`, and unregisters itself from `IActivationDirectory`. Any call that races with deactivation is retried transparently on a fresh activation.
   - On `KillSilo(siloId)`, the silo's directory entries are removed immediately and any queued messages on that silo fail with `ActorException` (`silo_unavailable`).

3. **State and Reminders**:
   - `ActorContext.ReadStateAsync<T>` and `WriteStateAsync<T>` persist JSON payloads through `IStateStore` and track the activation's latest etag. Mismatched etags throw `ActorException` (`etag_conflict`).
   - `ActorContext.RegisterReminderAsync` validates that `period >= 1s` (`invalid_period` otherwise) and stores a `ReminderEntry` in `IReminderStore`. `TestCluster.AdvanceAsync` steps through due reminders in chronological order, firing missed periods once each through the target actor's mailbox.
