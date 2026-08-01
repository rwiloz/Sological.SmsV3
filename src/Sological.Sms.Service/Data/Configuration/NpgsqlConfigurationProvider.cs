using System.Collections.Concurrent;
using Npgsql;

namespace Sological.Sms.Service.Data.Configuration;

/// <summary>IConfigurationSource that creates a <see cref="NpgsqlConfigurationProvider"/>.
/// Ported from AI-Workforce's proven pattern (S7 spec §0 tier 3; Ray's ruling 2026-08-01) —
/// v3 differences: table lives in the public schema (`config_entries`), single global
/// category, and there is no message-bus push (single service — the settings PUT calls
/// <see cref="NpgsqlConfigurationProvider.ForceReloadAll"/> in-process instead, which beats
/// the bus for latency).</summary>
public sealed class NpgsqlConfigurationSource : IConfigurationSource
{
    public required string ConnectionString { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public ILoggerFactory? LoggerFactory { get; init; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new NpgsqlConfigurationProvider(this);
}

/// <summary>Loads service-tuning overrides from PostgreSQL (`config_entries`), layered
/// AFTER Key Vault so the table wins. Sentinel polling detects out-of-band edits; the
/// management API's writer bumps the sentinel AND force-reloads in-process.</summary>
public sealed class NpgsqlConfigurationProvider : ConfigurationProvider, IDisposable
{
    private static readonly ConcurrentDictionary<int, WeakReference<NpgsqlConfigurationProvider>> Instances = new();

    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger? _logger;
    private readonly Timer _timer;
    private readonly int _instanceKey;
    private string? _lastSentinelValue;
    private bool _disposed;

    public NpgsqlConfigurationProvider(NpgsqlConfigurationSource source)
    {
        _connectionString = source.ConnectionString;
        _pollInterval = source.PollInterval;
        _logger = source.LoggerFactory?.CreateLogger<NpgsqlConfigurationProvider>();
        _instanceKey = _connectionString.GetHashCode();
        Instances[_instanceKey] = new WeakReference<NpgsqlConfigurationProvider>(this);
        _timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Startup load fails hard (a service that can't reach its DB can't run
    /// anyway — migrations are next); runtime reloads log-and-retry.</summary>
    public override void Load()
    {
        var isStartup = _lastSentinelValue is null;
        try
        {
            LoadFromDatabase();
            if (isStartup)
                _timer.Change(_pollInterval, _pollInterval);
        }
        catch (Exception ex) when (!isStartup)
        {
            _logger?.LogWarning(ex, "Failed to reload config_entries — retrying in {Interval}s", _pollInterval.TotalSeconds);
        }
    }

    public void ForceReload()
    {
        try
        {
            LoadFromDatabase();
            OnReload();
            _logger?.LogInformation("config_entries force-reloaded");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to force-reload config_entries");
        }
    }

    /// <summary>Called by the settings write path — same process, so overrides apply to
    /// IOptionsMonitor consumers immediately (the 30s sentinel poll is the fallback for
    /// out-of-band psql edits).</summary>
    public static void ForceReloadAll()
    {
        foreach (var kvp in Instances)
        {
            if (kvp.Value.TryGetTarget(out var provider))
                provider.ForceReload();
            else
                Instances.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>Created here, not by an EF migration: the provider loads BEFORE the app
    /// (and its migrations) exist, so it must bootstrap its own table on first boot.</summary>
    private static void EnsureTableExists(NpgsqlConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS config_entries (
                key varchar(256) NOT NULL,
                value text NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT pk_config_entries PRIMARY KEY (key)
            );
            INSERT INTO config_entries (key, value)
            VALUES ('Sentinel', '2026-01-01T00:00:00.0000000+00:00')
            ON CONFLICT (key) DO NOTHING;
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadFromDatabase()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        if (_lastSentinelValue is null)
            EnsureTableExists(connection);

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM config_entries";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            data[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (data.TryGetValue("Sentinel", out var sentinel))
        {
            _lastSentinelValue = sentinel;
            data.Remove("Sentinel");
        }

        Data = data;
    }

    private void OnTimerElapsed(object? state)
    {
        if (_disposed) return;
        try
        {
            var currentSentinel = ReadSentinel();
            if (currentSentinel != _lastSentinelValue)
            {
                _logger?.LogInformation("Config sentinel changed — reloading");
                LoadFromDatabase();
                OnReload();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to check config sentinel — retrying in {Interval}s", _pollInterval.TotalSeconds);
        }
    }

    private string? ReadSentinel()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM config_entries WHERE key = 'Sentinel'";
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Instances.TryRemove(_instanceKey, out _);
    }
}
