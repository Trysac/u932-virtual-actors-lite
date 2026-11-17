using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Represents an individual in-process silo hosting actor activations.
/// </summary>
internal sealed class Silo
{
    private readonly string _siloId;
    private readonly IActivationDirectory _directory;
    private readonly IStateStore _stateStore;
    private readonly IReminderStore _reminderStore;
    private readonly ManualClock _clock;
    private readonly int _mailboxCapacity;
    private readonly TimeSpan _idleTimeout;
    private readonly IActorClusterDispatcher _cluster;
    private readonly Func<string, Silo?> _resolveSilo;
    private readonly ConcurrentDictionary<ActorId, ActorActivation> _activations = new();
    private readonly object _activationGate = new();
    private volatile bool _killed;

    public Silo(
        string siloId,
        IActivationDirectory directory,
        IStateStore stateStore,
        IReminderStore reminderStore,
        ManualClock clock,
        int mailboxCapacity,
        TimeSpan idleTimeout,
        IActorClusterDispatcher cluster,
        Func<string, Silo?> resolveSilo)
    {
        _siloId = siloId;
        _directory = directory;
        _stateStore = stateStore;
        _reminderStore = reminderStore;
        _clock = clock;
        _mailboxCapacity = mailboxCapacity;
        _idleTimeout = idleTimeout;
        _cluster = cluster;
        _resolveSilo = resolveSilo;
    }

    public string SiloId => _siloId;

    public bool IsKilled => _killed;

    public async Task<TResult> ExecuteCallAsync<TActor, TResult>(
        ActorId id,
        Func<TActor, Task<TResult>> call,
        ImmutableHashSet<ActorId> callerChain)
        where TActor : Actor, new()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (_killed)
            {
                throw new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' is unavailable.");
            }

            if (!_directory.TryRegister(id, _siloId, out var ownerSiloId))
            {
                var ownerSilo = _resolveSilo(ownerSiloId);
                if (ownerSilo is null || ownerSilo.IsKilled)
                {
                    _directory.RemoveSilo(ownerSiloId);
                    continue;
                }

                return await ownerSilo.ExecuteLocalOrRetryAsync(id, call, callerChain).ConfigureAwait(false);
            }

            var (enqueued, task) = TryEnqueueLocal(id, () => new TActor(), call, callerChain);
            if (enqueued)
            {
                return await task.ConfigureAwait(false);
            }

            await Task.Yield();
        }

        throw new ActorException(ActorException.Deactivated, $"Actor '{id}' could not be activated after retries.");
    }

    public async Task ExecuteReminderAsync(
        ActorId id,
        string reminderName,
        Func<Actor> actorFactory,
        ImmutableHashSet<ActorId> callerChain)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (_killed)
            {
                throw new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' is unavailable.");
            }

            if (!_directory.TryRegister(id, _siloId, out var ownerSiloId))
            {
                var ownerSilo = _resolveSilo(ownerSiloId);
                if (ownerSilo is null || ownerSilo.IsKilled)
                {
                    _directory.RemoveSilo(ownerSiloId);
                    continue;
                }

                var (forwardedEnqueued, forwardedTask) = ownerSilo.TryEnqueueLocalReminder(id, actorFactory, reminderName, callerChain);
                if (forwardedEnqueued)
                {
                    await forwardedTask.ConfigureAwait(false);
                    return;
                }

                await Task.Yield();
                continue;
            }

            var (enqueued, task) = TryEnqueueLocalReminder(id, actorFactory, reminderName, callerChain);
            if (enqueued)
            {
                await task.ConfigureAwait(false);
                return;
            }

            await Task.Yield();
        }

        throw new ActorException(ActorException.Deactivated, $"Reminder '{reminderName}' for '{id}' could not be delivered after retries.");
    }

    public async Task DeactivateIdleActorsAsync(DateTimeOffset now)
    {
        if (_killed)
        {
            return;
        }

        var toDeactivate = new List<ActorActivation>();
        foreach (var pair in _activations)
        {
            if (pair.Value.IsIdle(now, _idleTimeout))
            {
                toDeactivate.Add(pair.Value);
            }
        }

        foreach (var activation in toDeactivate)
        {
            await activation.InitiateDeactivateAsync().ConfigureAwait(false);
        }
    }

    public async Task DeactivateActorAsync(ActorId id)
    {
        if (_activations.TryGetValue(id, out var activation))
        {
            await activation.InitiateDeactivateAsync().ConfigureAwait(false);
        }
    }

    public void Kill()
    {
        _killed = true;
        _directory.RemoveSilo(_siloId);
        foreach (var pair in _activations)
        {
            if (_activations.TryRemove(pair.Key, out var activation))
            {
                activation.Kill();
            }
        }
    }

    private async Task<TResult> ExecuteLocalOrRetryAsync<TActor, TResult>(
        ActorId id,
        Func<TActor, Task<TResult>> call,
        ImmutableHashSet<ActorId> callerChain)
        where TActor : Actor, new()
    {
        var (enqueued, task) = TryEnqueueLocal(id, () => new TActor(), call, callerChain);
        if (enqueued)
        {
            return await task.ConfigureAwait(false);
        }

        await Task.Yield();
        return await ExecuteCallAsync(id, call, callerChain).ConfigureAwait(false);
    }

    private (bool Enqueued, Task<TResult> ResultTask) TryEnqueueLocal<TActor, TResult>(
        ActorId id,
        Func<Actor> factory,
        Func<TActor, Task<TResult>> call,
        ImmutableHashSet<ActorId> callerChain)
        where TActor : Actor
    {
        var activation = GetOrCreateActivation(id, factory);
        if (activation.TryEnqueueCall(call, callerChain, out var task))
        {
            return (true, task);
        }

        return (false, task);
    }

    private (bool Enqueued, Task CompletionTask) TryEnqueueLocalReminder(
        ActorId id,
        Func<Actor> factory,
        string reminderName,
        ImmutableHashSet<ActorId> callerChain)
    {
        var activation = GetOrCreateActivation(id, factory);
        if (activation.TryEnqueueReminder(reminderName, callerChain, out var task))
        {
            return (true, task);
        }

        return (false, task);
    }

    private ActorActivation GetOrCreateActivation(ActorId id, Func<Actor> factory)
    {
        if (_activations.TryGetValue(id, out var existing))
        {
            return existing;
        }

        lock (_activationGate)
        {
            if (_killed)
            {
                throw new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' is unavailable.");
            }

            if (_activations.TryGetValue(id, out existing))
            {
                return existing;
            }

            var context = new ActorContext(
                id,
                _siloId,
                _stateStore,
                _reminderStore,
                _clock,
                _cluster,
                factory);

            var created = new ActorActivation(
                id,
                _siloId,
                factory,
                context,
                _clock,
                _mailboxCapacity,
                OnActivationDeactivated);

            _activations[id] = created;
            return created;
        }
    }

    private void OnActivationDeactivated(ActorId id, ActorActivation activation)
    {
        lock (_activationGate)
        {
            ((ICollection<KeyValuePair<ActorId, ActorActivation>>)_activations).Remove(
                new KeyValuePair<ActorId, ActorActivation>(id, activation));
            if (!_killed)
            {
                _directory.Unregister(id, _siloId);
            }
        }
    }
}
