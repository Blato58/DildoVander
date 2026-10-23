using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.CustomerData;

internal static class CustomerDataDatabasePaths
{
    internal static void Validate(string accountConnectionString, string characterPath,
        string journalPath, string keepsakePath, string memoryPath, string? exportJobPath = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var account = Canonical(new SqliteConnectionStringBuilder(accountConnectionString).DataSource);
        var character = Canonical(characterPath);
        var journal = Canonical(journalPath);
        var keepsake = Canonical(keepsakePath);
        var memory = Canonical(memoryPath);
        var unique = new HashSet<string>([account, character, journal, memory], comparer);
        if (!comparer.Equals(journal, keepsake) || unique.Count != 4
            || exportJobPath is not null && !unique.Add(Canonical(exportJobPath)))
        {
            throw new CustomerDataAccessException(503, "customer_data_database_alias");
        }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
        {
            throw new CustomerDataAccessException(503, "customer_data_database_alias");
        }
        var absolute = Path.GetFullPath(path);
        var current = Path.GetPathRoot(absolute)!;
        foreach (var segment in absolute[current.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new CustomerDataAccessException(503, "customer_data_database_alias");
            }
        }
        return Path.GetFullPath(current);
    }
}
