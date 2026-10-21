namespace Kufr.Backend.Features.Conversations;

// An absent epoch is legacy identity zero. Keep the original nullable wire
// representation; a server must never substitute a newer device epoch.
internal static class OperationEpoch
{
    private const string Maximum = "18446744073709551615";

    internal static bool IsValid(string? value)
    {
        if (value is null) { return true; }
        if (value.Length is < 1 or > 20 || value.Length > 1 && value[0] == '0')
        {
            return false;
        }
        foreach (var character in value)
        {
            if (character is < '0' or > '9') { return false; }
        }
        return value.Length < 20 || string.CompareOrdinal(value, Maximum) <= 0;
    }

    internal static string Key(string? value)
    {
        if (!IsValid(value)) { throw new ArgumentException("Invalid operation epoch.", nameof(value)); }
        return value ?? "0";
    }

    internal static bool Same(string? left, string? right) =>
        IsValid(left) && IsValid(right) && Key(left) == Key(right);
}
