namespace Kufr.Backend.Features.DigitalTwin;

// The operator selects an existing server file. Read only the provider variable;
// never import production device identities, database paths or other settings.
internal static class TwinProviderCredentials
{
    internal static void Load(IConfigurationManager configuration)
    {
        var path = configuration["DigitalTwin:ProviderEnvFile"];
        if (path is null) { return; }
        var root = configuration["DigitalTwin:DataRoot"];
        var database = configuration["Backend:DatabasePath"];
        if (!configuration.GetValue<bool>("DigitalTwin:ExternalServicesAuthorized")
            || root is null || database is null || !System.IO.Path.IsPathFullyQualified(root)
            || !System.IO.Path.GetFullPath(database).StartsWith(System.IO.Path.GetFullPath(root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || configuration.GetValue<bool>("Backend:Memory:Enabled")
            || !System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path)
            || new FileInfo(path).Length > 65536)
        {
            throw new InvalidOperationException("Twin provider source requires explicit authorization and isolated storage.");
        }
        var values = File.ReadLines(path).Where(line => line.StartsWith("OPENAI_API_KEY=", StringComparison.Ordinal))
            .Select(line => line["OPENAI_API_KEY=".Length..].Trim().Trim('"', '\'')).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0].Any(char.IsControl))
        {
            throw new InvalidOperationException("Configured Twin provider credential is unavailable.");
        }
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = values[0] });
    }
}
