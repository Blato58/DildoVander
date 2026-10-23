using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

internal static partial class AccountIdentityTests
{
    public static async Task RunCustomerDataStartupAsync()
    {
        await ArchiveConfiguredWhileKeepsakesDisabledAsync();
        await IdentityDisabledLeavesCustomerDataRoutesAbsentAsync();
        await EnabledKeepsakesStillRequireArchiveRootAsync();
        await MissingArchiveKeepsIdentityButRejectsDeletionAsync();

        Console.WriteLine("PASS customer data production startup gates, worker graph, deletion preflight, and keepsake root validation");
    }

    private static async Task ArchiveConfiguredWhileKeepsakesDisabledAsync()
    {
        await using var server = await BackendServer.StartAsync(new BackendConfiguration(
            IdentityEnabled: true,
            ArchiveConfigured: true,
            KeepsakesEnabled: false,
            GenerationEnabled: false));

        using var client = server.Client();
        using var memory = await client.GetAsync("/app/v1/customer/memory");
        Check(memory.StatusCode == HttpStatusCode.Unauthorized,
            "archive-enabled customer memory route is mapped and protected while keepsake production is disabled");

        using var export = await client.GetAsync($"/app/v1/customer/data/exports/{Guid.NewGuid():D}");
        Check(export.StatusCode == HttpStatusCode.Unauthorized,
            "archive-enabled account export route is mapped and protected while keepsake production is disabled");

        var exportDatabase = AccountDataExportStore.PathFor(Path.Combine(server.Root, "accounts"));
        Check(File.Exists(exportDatabase),
            "production account export worker resolves the configured archive-backed dependency graph at startup");
    }

    private static async Task MissingArchiveKeepsIdentityButRejectsDeletionAsync()
    {
        await using var server = await BackendServer.StartAsync(new BackendConfiguration(
            IdentityEnabled: true,
            ArchiveConfigured: false,
            KeepsakesEnabled: false,
            GenerationEnabled: false));

        using var owner = server.Client();
        using var passkey = new VirtualPasskey();
        await EnrollAsync(owner, passkey, Fixture.Bootstrap, "Startup test owner");
        var session = await CsrfAsync(owner);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;

        using var memory = await owner.GetAsync("/app/v1/customer/memory");
        Check(memory.StatusCode == HttpStatusCode.NotFound,
            "customer memory routes stay absent when no archive root is configured");
        using var export = await owner.GetAsync($"/app/v1/customer/data/exports/{Guid.NewGuid():D}");
        Check(export.StatusCode == HttpStatusCode.NotFound,
            "account export routes stay absent when no archive root is configured");

        using var deletion = await owner.DeleteAsync("/app/v1/account");
        var deletionBody = await deletion.Content.ReadAsStringAsync();
        Check(deletion.StatusCode == HttpStatusCode.ServiceUnavailable
            && deletionBody.Contains("account_deletion_unavailable", StringComparison.Ordinal),
            "account deletion fails closed when the archive-backed cleanup graph is missing");
        Check(await server.IsAccountEnabledAsync(ownerId),
            "failed deletion preflight does not disable the account in the production identity database");
    }

    private static async Task IdentityDisabledLeavesCustomerDataRoutesAbsentAsync()
    {
        await using var server = await BackendServer.StartAsync(new BackendConfiguration(
            IdentityEnabled: false,
            ArchiveConfigured: true,
            KeepsakesEnabled: false,
            GenerationEnabled: false));

        using var client = server.Client();
        using var memory = await client.GetAsync("/app/v1/customer/memory");
        var memoryBody = await memory.Content.ReadAsStringAsync();
        Check(memory.StatusCode == HttpStatusCode.ServiceUnavailable
            && memoryBody.Contains("accounts_not_configured", StringComparison.Ordinal),
            "disabled account API fallback handles requests without mounting customer memory routes");
        using var export = await client.GetAsync($"/app/v1/customer/data/exports/{Guid.NewGuid():D}");
        var exportBody = await export.Content.ReadAsStringAsync();
        Check(export.StatusCode == HttpStatusCode.ServiceUnavailable
            && exportBody.Contains("accounts_not_configured", StringComparison.Ordinal),
            "disabled account API fallback handles requests without mounting account export routes");

        Check(Directory.Exists(Path.Combine(server.Root, "archive")),
            "configured keepsake archive store initializes without application identity");
        var exportDatabase = AccountDataExportStore.PathFor(Path.Combine(server.Root, "accounts"));
        Check(!File.Exists(exportDatabase),
            "identity-disabled startup does not construct the account export worker or its store");
    }

    private static async Task EnabledKeepsakesStillRequireArchiveRootAsync()
    {
        await using var server = await BackendServer.LaunchAsync(new BackendConfiguration(
            IdentityEnabled: false,
            ArchiveConfigured: false,
            KeepsakesEnabled: true,
            GenerationEnabled: false));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await server.Process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Account identity: enabled keepsakes did not reject a missing archive root during production startup.");
        }

        var output = await server.ReadOutputAsync();
        Check(server.Process.ExitCode != 0
            && output.Contains("Keepsake:DataRoot must be configured.", StringComparison.Ordinal),
            "production startup still rejects enabled keepsakes without an archive root");
    }

    private sealed record BackendConfiguration(bool IdentityEnabled, bool ArchiveConfigured,
        bool KeepsakesEnabled, bool GenerationEnabled);

    private sealed class BackendServer : IAsyncDisposable
    {
        private const string CertificatePassword = "startup-test-certificate";
        private readonly HttpClientHandler handler;
        private readonly Task<string> standardOutput;
        private readonly Task<string> standardError;

        private BackendServer(string root, Process process, HttpClientHandler handler,
            Task<string> standardOutput, Task<string> standardError, Uri address)
        {
            Root = root;
            Process = process;
            this.handler = handler;
            this.standardOutput = standardOutput;
            this.standardError = standardError;
            Address = address;
        }

        internal string Root { get; }
        internal Process Process { get; }
        private Uri Address { get; }

        internal HttpClient Client()
        {
            var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = Address };
            client.DefaultRequestHeaders.Add("Origin", "https://localhost");
            client.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
            return client;
        }

        internal static async Task<BackendServer> StartAsync(BackendConfiguration configuration)
        {
            var server = await LaunchAsync(configuration);
            try
            {
                using var client = server.Client();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!timeout.IsCancellationRequested)
                {
                    if (server.Process.HasExited)
                    {
                        var output = await server.ReadOutputAsync();
                        throw new InvalidOperationException("Account identity: production backend exited before readiness. " + output);
                    }

                    try
                    {
                        using var response = await client.GetAsync("/healthz", timeout.Token);
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            return server;
                        }
                    }
                    catch (HttpRequestException) { }

                    await Task.Delay(100, timeout.Token);
                }

                throw new TimeoutException("Account identity: production backend did not become ready.");
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        internal static async Task<BackendServer> LaunchAsync(BackendConfiguration configuration)
        {
            var root = Path.Combine(Path.GetTempPath(), "dv-customer-data-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var port = ReserveLoopbackPort();
                var address = new Uri($"https://127.0.0.1:{port}");
                var certificatePath = Path.Combine(root, "localhost.pfx");
                await WriteCertificateAsync(certificatePath);

                var backendAssembly = typeof(AccountConfiguration).Assembly.Location;
                var testAssembly = typeof(AccountIdentityTests).Assembly.Location;
                if (configuration.IdentityEnabled)
                {
                    await RunBackendCommandAsync(CreateStartInfo(root, backendAssembly, testAssembly,
                        certificatePath, configuration, "--account-migrate"));
                }

                var start = CreateStartInfo(root, backendAssembly, testAssembly,
                    certificatePath, configuration, "--urls", address.ToString());
                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("Account identity: failed to launch the production backend process.");
                }

                return new BackendServer(root, process,
                    new HttpClientHandler
                    {
                        UseProxy = false,
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                    },
                    process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync(), address);
            }
            catch
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
                throw;
            }
        }

        private static ProcessStartInfo CreateStartInfo(string root, string backendAssembly,
            string testAssembly, string certificatePath, BackendConfiguration configuration,
            params string[] backendArguments)
        {
            var runtimeConfig = Path.ChangeExtension(backendAssembly, ".runtimeconfig.json");
            var depsFile = Path.ChangeExtension(backendAssembly, ".deps.json");
            var useTestRuntime = !File.Exists(runtimeConfig) || !File.Exists(depsFile);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (useTestRuntime)
            {
                start.ArgumentList.Add("exec");
                start.ArgumentList.Add("--runtimeconfig");
                start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
                start.ArgumentList.Add("--depsfile");
                start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
            }

            start.ArgumentList.Add(backendAssembly);
            foreach (var argument in backendArguments)
            {
                start.ArgumentList.Add(argument);
            }

            var identityRoot = Path.Combine(root, "accounts");
            var keysRoot = Path.Combine(root, "keys");
            var archiveRoot = configuration.ArchiveConfigured ? Path.Combine(root, "archive") : "";
            var values = start.Environment;
            values["ASPNETCORE_ENVIRONMENT"] = "Production";
            values["DOTNET_ENVIRONMENT"] = "Production";
            values["ASPNETCORE_Kestrel__Certificates__Default__Path"] = certificatePath;
            values["ASPNETCORE_Kestrel__Certificates__Default__Password"] = CertificatePassword;
            values["ApplicationIdentity__Enabled"] = configuration.IdentityEnabled.ToString();
            values["ApplicationIdentity__DataRoot"] = identityRoot;
            values["ApplicationIdentity__DataProtectionRoot"] = keysRoot;
            values["ApplicationIdentity__RelyingPartyId"] = "localhost";
            values["ApplicationIdentity__AllowedOrigins__0"] = "https://localhost";
            values["ApplicationIdentity__Bootstrap__TokenSha256"] = AccountEndpoints.Hash(Fixture.Bootstrap);
            values["ApplicationIdentity__Bootstrap__ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
            values["ApplicationIdentity__RequestsPerMinute"] = "120";
            values["Backend__DatabasePath"] = Path.Combine(root, "operation.sqlite");
            values["Keepsake__Enabled"] = configuration.KeepsakesEnabled.ToString();
            values["Keepsake__GenerationEnabled"] = configuration.GenerationEnabled.ToString();
            values["Keepsake__DataRoot"] = archiveRoot;
            values["Keepsake__DataProtectionRoot"] = keysRoot;
            values["Backend__Memory__Enabled"] = "false";
            values["Backend__Memory__CloudIndexingEnabled"] = "false";
            values["Backend__Memory__LongTermPersonalEnabled"] = "false";
            values["Show__DeviceCueSpeechMode"] = "off";
            values.Remove("KUFR_TWIN_CONFIG");
            values.Remove("OPENAI_API_KEY");
            values.Remove("OPENAI_ORG_ID");
            values.Remove("OPENAI_BASE_URL");
            values.Remove("AZURE_OPENAI_API_KEY");
            return start;
        }

        private static async Task RunBackendCommandAsync(ProcessStartInfo start)
        {
            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Account identity: failed to start the isolated account migration command.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("Account identity: isolated account migration did not finish within its deadline.");
            }

            var output = (await stdout + Environment.NewLine + await stderr).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Account identity: isolated account migration failed. " + output);
            }
        }

        internal async Task<bool> IsAccountEnabledAsync(string accountId)
        {
            var identityPath = Path.Combine(Root, "accounts", "identity.sqlite");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = identityPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Disabled FROM AspNetUsers WHERE Id = $id";
            command.Parameters.AddWithValue("$id", accountId);
            var disabled = await command.ExecuteScalarAsync();
            return disabled is not null && disabled is not DBNull && Convert.ToInt32(disabled) == 0;
        }

        internal async Task<string> ReadOutputAsync()
        {
            var stdout = await standardOutput;
            var stderr = await standardError;
            return (stdout + Environment.NewLine + stderr).Trim();
        }

        public async ValueTask DisposeAsync()
        {
            handler.Dispose();
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await Process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Account identity: production backend process did not stop within its cleanup deadline.");
                }
            }

            Process.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static int ReserveLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static async Task WriteCertificateAsync(string path)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var pfx = certificate.Export(X509ContentType.Pfx, CertificatePassword);
            await File.WriteAllBytesAsync(path, pfx);
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
