using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Identity;

internal static class CustomerAdmissionDatabasePaths
{
    internal static void Validate(string accountConnectionString, string characterPath, params string[] additionalPaths)
    {
        var settings = new SqliteConnectionStringBuilder(accountConnectionString);
        if (settings.Mode == SqliteOpenMode.Memory) { throw Alias(); }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        foreach (var path in new[] { settings.DataSource, characterPath }.Concat(additionalPaths))
        {
            if (!paths.Add(Canonical(path))) { throw Alias(); }
        }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:" || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) { throw Alias(); }
        var absolute = Path.GetFullPath(path);
        var current = Path.GetPathRoot(absolute)!;
        foreach (var segment in absolute[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null) { current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw Alias(); }
        }
        return Path.GetFullPath(current);
    }

    private static CustomerDataAccessException Alias() => new(503, "customer_data_database_alias");
}
