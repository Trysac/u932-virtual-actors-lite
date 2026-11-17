namespace Trysac.VirtualActorsLite;

/// <summary>Thrown when a text helper receives blank input.</summary>
internal sealed class BlankInputException : ArgumentException
{
    public BlankInputException()
        : base("input is blank")
    {
    }

    public BlankInputException(string message)
        : base(message)
    {
    }

    public BlankInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
