using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.AspNetCore.Identity;

namespace Kufr.Backend.Features.Identity;

// Migration generation never starts providers or opens the configured live DB.
internal sealed class AccountDatabaseFactory : IDesignTimeDbContextFactory<AccountDatabase>
{
    public AccountDatabase CreateDbContext(string[] args)
    {
        // Identity reads StoreOptions from the DbContext's application service
        // provider during model construction; a bare options builder defaults
        // to schema v1 and silently omits its maintained passkey entity.
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        services.AddDbContext<AccountDatabase>(options => options.UseSqlite("Data Source=:memory:"));
        return services.BuildServiceProvider().GetRequiredService<AccountDatabase>();
    }
}
