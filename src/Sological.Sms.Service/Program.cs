using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;
using Sological.Sms.Service.Data;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // ── Azure Key Vault ────────────────────────────────────────────────────────
    // A vault URI outside *.vault.azure.net is the LOCAL Key Vault emulator (the machine-wide
    // AzureKeyVault__VaultUri env var; ops/setup_keyvault_emulator.ps1 in the AI-Workforce repo):
    // static credential (the emulator accepts any bearer; no Azure login needed), challenge
    // verification off, and a non-fatal load so this service still starts when the emulator
    // container is down. A real vault keeps fail-fast startup semantics. Vault loads AFTER env
    // vars (vault wins on overlap); hermetic test harnesses must blank AzureKeyVault__VaultUri.
    var keyVaultUri = builder.Configuration["AzureKeyVault:VaultUri"];
    if (!string.IsNullOrEmpty(keyVaultUri))
    {
        var vaultUri = new Uri(keyVaultUri);
        var isEmulator = !vaultUri.Host.EndsWith("vault.azure.net", StringComparison.OrdinalIgnoreCase);

        Azure.Core.TokenCredential credential = isEmulator
            ? new KeyVaultEmulatorCredential()
            : new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"]
            });

        var secretClient = new Azure.Security.KeyVault.Secrets.SecretClient(
            vaultUri, credential,
            new Azure.Security.KeyVault.Secrets.SecretClientOptions { DisableChallengeResourceVerification = isEmulator });

        try
        {
            builder.Configuration.AddAzureKeyVault(secretClient, new Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationOptions());
        }
        catch (Exception) when (isEmulator)
        {
            Log.Warning("Key Vault emulator at {VaultUri} is unreachable — starting without it (env vars only)", vaultUri);
        }
    }

    // ── Database ───────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Host=localhost;Database=sologicalsms;Username=postgres;Password=postgres";

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.EnableDynamicJson(); // Dictionary<string,string> jsonb payloads
    var dataSource = dataSourceBuilder.Build();

    builder.Services.AddDbContext<SmsDbContext>(options => options
        .UseNpgsql(dataSource)
        .UseSnakeCaseNamingConvention());

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<SmsDbContext>("database");

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapHealthChecks("/health");

    // ── Migrate on startup (same pattern as the sibling services) ──────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        Log.Information("Applying database migrations");
        await db.Database.MigrateAsync();
    }

    Log.Information("Sological SMS v2 service starting");
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Static bearer credential for the local Key Vault emulator, which accepts any bearer token.
/// Selected by vault host only — never used against a real vault.
/// </summary>
internal sealed class KeyVaultEmulatorCredential : Azure.Core.TokenCredential
{
    // { "alg": "none", "typ": "JWT" } . { "sub": "local-dev" } . <no signature>
    private const string FakeJwt = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiJsb2NhbC1kZXYifQ.";

    private static Azure.Core.AccessToken Token() => new(FakeJwt, DateTimeOffset.UtcNow.AddHours(1));

    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => Token();

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(Token());
}
