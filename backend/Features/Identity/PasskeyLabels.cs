using Microsoft.AspNetCore.Identity;

namespace Kufr.Backend.Features.Identity;

internal static class PasskeyLabels
{
    internal static string Account(string displayName, IEnumerable<string> roles)
    {
        var role = AccountRoles.All.FirstOrDefault(roles.Contains) ?? AccountRoles.Viewer;
        return $"DildoVander {role} · {Bound(displayName.Trim(), 40)}";
    }

    internal static void NameNew(UserPasskeyInfo passkey, string accountLabel)
    {
        passkey.Name = $"{Bound(accountLabel, 52)} · {passkey.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
    }

    private static string Bound(string value, int limit)
    {
        if (value.Length <= limit) { return value; }
        var end = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        return value[..end].TrimEnd();
    }
}
