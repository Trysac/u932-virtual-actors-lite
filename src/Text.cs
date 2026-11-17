namespace Trysac.VirtualActorsLite;

/// <summary>Internal text helpers.</summary>
internal static class Text
{
    public static string Normalize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            throw new BlankInputException();
        }

        return trimmed.ToLowerInvariant();
    }
}
