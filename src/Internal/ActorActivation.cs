using System.Collections.Immutable;
using System.Threading.Channels;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Represents a single live activation of an <see cref="Actor"/> hosted on a silo, backed by a bounded turn-based mailbox.
/// </summary>
internal sealed class ActorActivation
{
    private readonly ActorId _id;
    private readonly string _siloId;
    private readonly Func<Actor> _factory;
    private readonly ActorContext _context;
    private readonly ManualClock _clock;
    private readonly int _mailboxCapacity;
    private readonly Action<ActorId, ActorActivation> _onDeactivatedCallback;
    private readonly Channel<MailboxMessage> _channel;
    private readonly object _stateLock = new();
    private readonly Task _processingLoopTask;

    private Actor? _instance;
    private bool _activated;
    private bool _deactivating;
    private bool _killed;
    private int _queuedCount;
    private int _activeCalls;
    private DateTimeOffset _lastActivityAt;

    public ActorActivation(
        ActorId id,
        string siloId,
        Func<Actor> factory,
        ActorContext context,
        ManualClock clock,
        int mailboxCapacity,
        Action<ActorId, ActorActivation> onDeactivatedCallback)
    {
        _id = id;
        _siloId = siloId;
        _factory = factory;
        _context = context;
        _clock = clock;
        _mailboxCapacity = mailboxCapacity;
        _onDeactivatedCallback = onDeactivatedCallback;
        _lastActivityAt = clock.UtcNow;

        _channel = Channel.CreateUnbounded<MailboxMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _processingLoopTask = Task.Run(ProcessMailboxLoopAsync);
    }

    public ActorId Id => _id;

    public string SiloId => _siloId;

    public bool IsIdle(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_stateLock)
        {
            if (_deactivating || _killed || _queuedCount > 0 || _activeCalls > 0)
            {
                return false;
            }

            return now - _lastActivityAt >= idleTimeout;
        }
    }

    public bool TryEnqueueCall<TActor, TResult>(
        Func<TActor, Task<TResult>> call,
        ImmutableHashSet<ActorId> callerChain,
        out Task<TResult> resultTask)
        where TActor : Actor
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            if (_killed)
            {
                throw new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' is unavailable.");
            }

            if (_deactivating)
            {
                resultTask = Task.FromException<TResult>(new ActorException(ActorException.Deactivated, "Activation is deactivating."));
                return false;
            }

            if (_activeCalls > 0 && _queuedCount >= _mailboxCapacity)
            {
                throw new ActorException(ActorException.MailboxFull, $"Mailbox for actor '{_id}' is full (capacity {_mailboxCapacity}).");
            }

            _queuedCount++;
            _lastActivityAt = _clock.UtcNow;
        }

        var message = new MailboxMessage(
            ExecuteAsync: async actor =>
            {
                var prevChain = CallChainContext.GetCurrent();
                CallChainContext.SetCurrent(callerChain);
                try
                {
                    var typed = (TActor)actor;
                    var res = await call(typed).ConfigureAwait(false);
                    tcs.TrySetResult(res);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    CallChainContext.SetCurrent(prevChain);
                }
            },
            Abort: ex => tcs.TrySetException(ex));

        if (!_channel.Writer.TryWrite(message))
        {
            lock (_stateLock)
            {
                _queuedCount--;
            }

            resultTask = Task.FromException<TResult>(new ActorException(ActorException.Deactivated, "Activation mailbox closed."));
            return false;
        }

        resultTask = tcs.Task;
        return true;
    }

    public bool TryEnqueueReminder(
        string reminderName,
        ImmutableHashSet<ActorId> callerChain,
        out Task completionTask)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            if (_killed)
            {
                throw new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' is unavailable.");
            }

            if (_deactivating)
            {
                completionTask = Task.FromException(new ActorException(ActorException.Deactivated, "Activation is deactivating."));
                return false;
            }

            if (_activeCalls > 0 && _queuedCount >= _mailboxCapacity)
            {
                throw new ActorException(ActorException.MailboxFull, $"Mailbox for actor '{_id}' is full (capacity {_mailboxCapacity}).");
            }

            _queuedCount++;
            _lastActivityAt = _clock.UtcNow;
        }

        var message = new MailboxMessage(
            ExecuteAsync: async actor =>
            {
                var prevChain = CallChainContext.GetCurrent();
                CallChainContext.SetCurrent(callerChain);
                try
                {
                    await actor.OnReminderAsync(reminderName).ConfigureAwait(false);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    CallChainContext.SetCurrent(prevChain);
                }
            },
            Abort: ex => tcs.TrySetException(ex));

        if (!_channel.Writer.TryWrite(message))
        {
            lock (_stateLock)
            {
                _queuedCount--;
            }

            completionTask = Task.FromException(new ActorException(ActorException.Deactivated, "Activation mailbox closed."));
            return false;
        }

        completionTask = tcs.Task;
        return true;
    }

    public async Task InitiateDeactivateAsync()
    {
        lock (_stateLock)
        {
            if (_deactivating || _killed)
            {
                return;
            }

            _deactivating = true;
            _channel.Writer.TryComplete();
        }

        await _processingLoopTask.ConfigureAwait(false);
    }

    public void Kill()
    {
        lock (_stateLock)
        {
            if (_killed)
            {
                return;
            }

            _killed = true;
            _deactivating = true;
            _channel.Writer.TryComplete();
        }

        while (_channel.Reader.TryRead(out var msg))
        {
            lock (_stateLock)
            {
                if (_queuedCount > 0)
                {
                    _queuedCount--;
                }
            }

            msg.Abort(new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' was killed."));
        }
    }

    private async Task ProcessMailboxLoopAsync()
    {
        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                bool isKilled;
                lock (_stateLock)
                {
                    isKilled = _killed;
                    if (!isKilled)
                    {
                        _queuedCount--;
                        _activeCalls++;
                    }
                }

                if (isKilled)
                {
                    msg.Abort(new ActorException(ActorException.SiloUnavailable, $"Silo '{_siloId}' was killed."));
                    continue;
                }

                try
                {
                    if (!_activated)
                    {
                        _instance = _factory();
                        _instance.Context = _context;
                        var prevChain = CallChainContext.GetCurrent();
                        CallChainContext.SetCurrent(CallChainContext.CaptureWith(_id));
                        try
                        {
                            await _instance.OnActivateAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            CallChainContext.SetCurrent(prevChain);
                        }

                        _activated = true;
                    }

                    await msg.ExecuteAsync(_instance!).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    msg.Abort(ex);
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _activeCalls--;
                        _lastActivityAt = _clock.UtcNow;
                    }
                }
            }

            bool runDeactivate;
            lock (_stateLock)
            {
                runDeactivate = !_killed && _activated && _instance is not null;
            }

            if (runDeactivate)
            {
                var prevChain = CallChainContext.GetCurrent();
                CallChainContext.SetCurrent(CallChainContext.CaptureWith(_id));
                try
                {
                    await _instance!.OnDeactivateAsync().ConfigureAwait(false);
                }
                finally
                {
                    CallChainContext.SetCurrent(prevChain);
                }
            }
        }
        finally
        {
            _onDeactivatedCallback(_id, this);
        }
    }

    private sealed record MailboxMessage(Func<Actor, Task> ExecuteAsync, Action<Exception> Abort);
}
