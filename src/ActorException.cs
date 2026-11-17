namespace Trysac.VirtualActorsLite;

/// <summary>
/// Exception thrown by the virtual actor runtime with a stable machine-readable error <see cref="Code"/>.
/// </summary>
public sealed class ActorException : Exception
{
    /// <summary>Error code when a state write fails due to an etag mismatch.</summary>
    public const string EtagConflict = "etag_conflict";

    /// <summary>Error code when a reminder period or due time is invalid.</summary>
    public const string InvalidPeriod = "invalid_period";

    /// <summary>Error code when a call attempts to re-enter an actor already in the current call chain.</summary>
    public const string ReentrantCall = "reentrant_call";

    /// <summary>Error code when an actor's bounded mailbox has reached its capacity.</summary>
    public const string MailboxFull = "mailbox_full";

    /// <summary>Error code when an actor key or identifier is invalid.</summary>
    public const string InvalidKey = "invalid_key";

    /// <summary>Error code when the target silo is killed or no silos are available.</summary>
    public const string SiloUnavailable = "silo_unavailable";

    /// <summary>Error code when an activation has deactivated before processing a message.</summary>
    public const string Deactivated = "deactivated";

    /// <summary>Initializes a new <see cref="ActorException"/> with the default error code.</summary>
    public ActorException()
        : base("actor error")
    {
        Code = "actor_error";
    }

    /// <summary>Initializes a new <see cref="ActorException"/> with a message.</summary>
    /// <param name="message">A human-readable error message.</param>
    public ActorException(string message)
        : base(TruncateMessage(message))
    {
        Code = "actor_error";
    }

    /// <summary>Initializes a new <see cref="ActorException"/> with a message and inner exception.</summary>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="innerException">The underlying cause of the exception.</param>
    public ActorException(string message, Exception innerException)
        : base(TruncateMessage(message), innerException)
    {
        Code = "actor_error";
    }

    /// <summary>Initializes a new <see cref="ActorException"/> with a machine-readable code and message.</summary>
    /// <param name="code">The stable machine-readable error code.</param>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="innerException">Optional underlying cause.</param>
    public ActorException(string code, string message, Exception? innerException = null)
        : base(TruncateMessage(message), innerException)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
    }

    /// <summary>Gets the stable machine-readable error code.</summary>
    public string Code { get; }

    private static string TruncateMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "actor error";
        }

        return message.Length <= 200 ? message : string.Concat(message.AsSpan(0, 197), "...");
    }
}
