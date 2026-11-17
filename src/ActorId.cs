namespace Trysac.VirtualActorsLite;

/// <summary>
/// Uniquely identifies a virtual actor by its type name and key.
/// </summary>
/// <param name="Type">The actor type name.</param>
/// <param name="Key">The actor instance key (1 to 256 characters).</param>
public readonly record struct ActorId(string Type, string Key)
{
    /// <summary>Maximum allowed length for an actor key or type name.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>
    /// Validates the <paramref name="key"/> according to actor key constraints (1 to 256 characters).
    /// </summary>
    /// <param name="key">The actor key to validate.</param>
    /// <exception cref="ActorException">Thrown with code <c>invalid_key</c> when the key is null, empty, or exceeds 256 characters.</exception>
    public static void ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            throw new ActorException(
                ActorException.InvalidKey,
                "Actor key must be between 1 and 256 characters.");
        }
    }

    /// <summary>
    /// Validates both the <see cref="Type"/> and <see cref="Key"/> of an <see cref="ActorId"/>.
    /// </summary>
    /// <param name="id">The actor identifier to validate.</param>
    /// <exception cref="ActorException">Thrown with code <c>invalid_key</c> when Type or Key is invalid.</exception>
    public static void Validate(ActorId id)
    {
        if (string.IsNullOrEmpty(id.Type) || id.Type.Length > MaxKeyLength)
        {
            throw new ActorException(
                ActorException.InvalidKey,
                "Actor type must be between 1 and 256 characters.");
        }

        ValidateKey(id.Key);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Type}/{Key}";
}
