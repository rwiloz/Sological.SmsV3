using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Sological.Sms.Service.Data;

/// <summary>Used by `dotnet ef` and by model-only tests — builds the context without
/// booting the host (no Key Vault, no migration-on-start). Never opens a connection
/// unless the caller does.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SmsDbContext>
{
    public SmsDbContext CreateDbContext(string[] args)
        => new(BuildOptions(
            Environment.GetEnvironmentVariable("SologicalSMS__ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=sologicalsms;Username=postgres;Password=postgres"));

    /// <summary>The one place the provider wiring (dynamic JSON + snake_case) is defined
    /// for non-host callers; Program.cs mirrors it for the running service.</summary>
    public static DbContextOptions<SmsDbContext> BuildOptions(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson(); // Dictionary<string,string> jsonb payloads
        return new DbContextOptionsBuilder<SmsDbContext>()
            .UseNpgsql(dataSourceBuilder.Build())
            .UseSnakeCaseNamingConvention()
            .Options;
    }
}
