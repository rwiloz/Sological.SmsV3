using Npgsql;

namespace Sological.Sms.Service.Data.Configuration;

/// <summary>Writes tuning overrides to `config_entries` (S7 spec §2.9). Every write bumps
/// the sentinel (out-of-process detection) AND force-reloads the in-process provider so
/// IOptionsMonitor consumers see the change immediately. Ported from AI-Workforce's
/// PostgresConfigurationWriter, minus categories and feature flags (v3 has neither).</summary>
public sealed class PostgresConfigurationWriter(string connectionString, ILogger<PostgresConfigurationWriter> logger)
{
    public async Task<Dictionary<string, string>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM config_entries WHERE key <> 'Sentinel'";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO config_entries (key, value, updated_at)
            VALUES (@key, @value, now())
            ON CONFLICT (key) DO UPDATE SET value = @value, updated_at = now()
            """;
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", value);
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("config_entries updated: {Key}", key);
        await BumpSentinelAsync(connection, ct);
        NpgsqlConfigurationProvider.ForceReloadAll();
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM config_entries WHERE key = @key AND key <> 'Sentinel'";
        cmd.Parameters.AddWithValue("key", key);
        var removed = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (removed)
        {
            logger.LogInformation("config_entries override removed: {Key}", key);
            await BumpSentinelAsync(connection, ct);
            NpgsqlConfigurationProvider.ForceReloadAll();
        }
        return removed;
    }

    private static async Task BumpSentinelAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO config_entries (key, value, updated_at)
            VALUES ('Sentinel', @value, now())
            ON CONFLICT (key) DO UPDATE SET value = @value, updated_at = now()
            """;
        cmd.Parameters.AddWithValue("value", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
