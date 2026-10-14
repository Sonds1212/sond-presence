using MySqlConnector;

namespace SondPresence;

/// <summary>
/// Presença num banco MySQL/TiDB (ex.: TiDB Cloud Starter). Ao contrário do Redis da rede
/// interna, o TiDB Cloud tem endereço público, então o serviço pode rodar em qualquer lugar.
///
/// Uma linha por jogador; quem passa do TTL sem heartbeat deixa de contar e é apagado
/// de tempos em tempos. Cada heartbeat custa 2 comandos (upsert + select), o que cabe com
/// folga no plano grátis para um grupo de amigos.
/// </summary>
public sealed class SqlPresenceStore : IPresenceStore
{
    private readonly string _connectionString;
    private readonly TimeSpan _ttl;
    private readonly string _table;
    private long _lastCleanupTicks;

    public SqlPresenceStore(string connectionString, TimeSpan ttl, string table)
    {
        _connectionString = connectionString;
        _ttl = ttl;
        _table = table;
    }

    /// <summary>Cria a tabela (e o banco, se a connection string apontar para um que não existe).</summary>
    public async Task InitializeAsync()
    {
        var builder = new MySqlConnectionStringBuilder(_connectionString);
        var database = builder.Database;
        if (!string.IsNullOrEmpty(database))
        {
            builder.Database = "";
            await using var bootstrap = new MySqlConnection(builder.ConnectionString);
            await bootstrap.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{database}`", bootstrap);
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"""
            CREATE TABLE IF NOT EXISTS `{_table}` (
                uuid      CHAR(36)     NOT NULL PRIMARY KEY,
                name      VARCHAR(32)  NOT NULL DEFAULT '',
                server    VARCHAR(255) NOT NULL,
                last_seen BIGINT       NOT NULL,
                KEY idx_server_seen (server, last_seen),
                KEY idx_seen (last_seen)
            )
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private long Cutoff() => NowMs() - (long)_ttl.TotalMilliseconds;

    public async Task<PresenceUser[]> HeartbeatAsync(string uuid, string name, string server)
    {
        await using var connection = await OpenAsync();

        // Upsert: trocar de servidor é só sobrescrever a coluna — sai da lista antiga na hora.
        await using (var upsert = new MySqlCommand($"""
            INSERT INTO `{_table}` (uuid, name, server, last_seen) VALUES (@uuid, @name, @server, @now)
            ON DUPLICATE KEY UPDATE name = VALUES(name), server = VALUES(server), last_seen = VALUES(last_seen)
            """, connection))
        {
            upsert.Parameters.AddWithValue("@uuid", uuid);
            upsert.Parameters.AddWithValue("@name", name);
            upsert.Parameters.AddWithValue("@server", server);
            upsert.Parameters.AddWithValue("@now", NowMs());
            await upsert.ExecuteNonQueryAsync();
        }

        var users = new List<PresenceUser>();
        await using (var select = new MySqlCommand(
            $"SELECT uuid, name FROM `{_table}` WHERE server = @server AND last_seen >= @cutoff", connection))
        {
            select.Parameters.AddWithValue("@server", server);
            select.Parameters.AddWithValue("@cutoff", Cutoff());
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // O MySqlConnector entrega CHAR(36) como Guid; ToString() volta ao formato do mod.
                users.Add(new PresenceUser(reader.GetValue(0).ToString()!, reader.GetString(1)));
            }
        }

        await CleanupIfDueAsync(connection);
        return users.ToArray();
    }

    /// <summary>Apaga quem sumiu, no máximo uma vez por minuto (não a cada heartbeat).</summary>
    private async Task CleanupIfDueAsync(MySqlConnection connection)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (now - last < TimeSpan.FromMinutes(1).Ticks ||
            Interlocked.CompareExchange(ref _lastCleanupTicks, now, last) != last)
        {
            return;
        }

        await using var delete = new MySqlCommand($"DELETE FROM `{_table}` WHERE last_seen < @cutoff", connection);
        delete.Parameters.AddWithValue("@cutoff", Cutoff());
        await delete.ExecuteNonQueryAsync();
    }

    public async Task LeaveAsync(string uuid)
    {
        await using var connection = await OpenAsync();
        await using var delete = new MySqlCommand($"DELETE FROM `{_table}` WHERE uuid = @uuid", connection);
        delete.Parameters.AddWithValue("@uuid", uuid);
        await delete.ExecuteNonQueryAsync();
    }

    public async Task<PresenceStats> StatsAsync()
    {
        await using var connection = await OpenAsync();
        await using var select = new MySqlCommand(
            $"SELECT server, COUNT(*) FROM `{_table}` WHERE last_seen >= @cutoff GROUP BY server", connection);
        select.Parameters.AddWithValue("@cutoff", Cutoff());

        var porServidor = new Dictionary<string, long>();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            porServidor[reader.GetString(0)] = reader.GetInt64(1);
        }

        return new PresenceStats(porServidor.Values.Sum(), porServidor);
    }
}
