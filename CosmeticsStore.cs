using System.Collections.Concurrent;
using MySqlConnector;

namespace SondPresence;

/// <summary>
/// Cosméticos do Sond Client. Por enquanto: cores do ícone (o selo da preguiça na tab list).
/// O dono (admin) dá cores para um jogador; o jogador vê só as que tem e equipa uma.
/// Tudo é guardado pelo nick em minúsculas (é por ele que o admin dá a cor).
/// </summary>
public interface ICosmeticsStore
{
    Task InitializeAsync();

    /// <summary>Ninguém tem cor nenhuma ainda (primeira vez com cosméticos).</summary>
    Task<bool> IsEmptyAsync();

    /// <summary>Cores que o jogador tem (RGB), na ordem em que ganhou.</summary>
    Task<int[]> OwnedAsync(string name);

    /// <summary>Cor equipada, ou null (selo branco padrão).</summary>
    Task<int?> EquippedAsync(string name);

    /// <summary>Cor equipada de vários jogadores de uma vez (só quem tem alguma).</summary>
    Task<Dictionary<string, int>> EquippedForAsync(IReadOnlyCollection<string> names);

    Task GrantAsync(string name, int color, string grantedBy);

    /// <summary>Tira a cor; se era a equipada, o jogador volta ao selo padrão.</summary>
    Task<bool> RevokeAsync(string name, int color);

    /// <summary>Equipa (ou tira, com null). Só aceita cor que o jogador tem.</summary>
    Task<bool> EquipAsync(string name, int? color);
}

public static class CosmeticNames
{
    public static string Key(string name) => name.Trim().ToLowerInvariant();
}

/// <summary>
/// Cache da cor equipada de todo mundo: o heartbeat de presença (a cada 20 s por jogador) lê daqui
/// em vez de ir ao banco. A tabela é pequena, então recarregar tudo de tempos em tempos é barato.
/// </summary>
public sealed class EquippedCache
{
    private readonly ICosmeticsStore _store;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(20);
    private Dictionary<string, int> _snapshot = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EquippedCache(ICosmeticsStore store)
    {
        _store = store;
    }

    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    public async Task<Dictionary<string, int>> ForNamesAsync(IEnumerable<string> names)
    {
        var keys = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(CosmeticNames.Key).Distinct().ToList();
        if (keys.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        if (DateTimeOffset.UtcNow - _loadedAt > _ttl)
        {
            await _lock.WaitAsync();
            try
            {
                if (DateTimeOffset.UtcNow - _loadedAt > _ttl)
                {
                    _snapshot = await _store.EquippedForAsync(Array.Empty<string>());
                    _loadedAt = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        var snapshot = _snapshot;
        var result = new Dictionary<string, int>();
        foreach (var key in keys)
        {
            if (snapshot.TryGetValue(key, out var color))
            {
                result[key] = color;
            }
        }

        return result;
    }
}

public sealed class SqlCosmeticsStore : ICosmeticsStore
{
    private readonly string _connectionString;
    private readonly string _owned;
    private readonly string _equipped;

    public SqlCosmeticsStore(string connectionString, string prefix)
    {
        _connectionString = connectionString;
        _owned = prefix + "cosmetic_colors";
        _equipped = prefix + "cosmetic_equipped";
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        await using (var create = new MySqlCommand($"""
            CREATE TABLE IF NOT EXISTS `{_owned}` (
                name       VARCHAR(16) NOT NULL,
                color      INT         NOT NULL,
                granted_by VARCHAR(16) NOT NULL DEFAULT '',
                granted_at BIGINT      NOT NULL,
                PRIMARY KEY (name, color)
            )
            """, connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (var create = new MySqlCommand($"""
            CREATE TABLE IF NOT EXISTS `{_equipped}` (
                name  VARCHAR(16) NOT NULL PRIMARY KEY,
                color INT         NOT NULL
            )
            """, connection))
        {
            await create.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> IsEmptyAsync()
    {
        await using var connection = await OpenAsync();
        await using var select = new MySqlCommand($"SELECT 1 FROM `{_owned}` LIMIT 1", connection);
        return await select.ExecuteScalarAsync() is null;
    }

    public async Task<int[]> OwnedAsync(string name)
    {
        await using var connection = await OpenAsync();
        await using var select = new MySqlCommand(
            $"SELECT color FROM `{_owned}` WHERE name = @name ORDER BY granted_at", connection);
        select.Parameters.AddWithValue("@name", CosmeticNames.Key(name));

        var colors = new List<int>();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            colors.Add(reader.GetInt32(0));
        }

        return colors.ToArray();
    }

    public async Task<int?> EquippedAsync(string name)
    {
        await using var connection = await OpenAsync();
        await using var select = new MySqlCommand($"SELECT color FROM `{_equipped}` WHERE name = @name", connection);
        select.Parameters.AddWithValue("@name", CosmeticNames.Key(name));
        var value = await select.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    public async Task<Dictionary<string, int>> EquippedForAsync(IReadOnlyCollection<string> names)
    {
        // Lista vazia = todo mundo (é o que o cache usa).
        await using var connection = await OpenAsync();
        await using var select = new MySqlCommand($"SELECT name, color FROM `{_equipped}`", connection);
        var wanted = names.Count == 0 ? null : names.Select(CosmeticNames.Key).ToHashSet();

        var result = new Dictionary<string, int>();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (wanted is null || wanted.Contains(name))
            {
                result[name] = reader.GetInt32(1);
            }
        }

        return result;
    }

    public async Task GrantAsync(string name, int color, string grantedBy)
    {
        await using var connection = await OpenAsync();
        await using var insert = new MySqlCommand($"""
            INSERT INTO `{_owned}` (name, color, granted_by, granted_at) VALUES (@name, @color, @by, @now)
            ON DUPLICATE KEY UPDATE granted_by = VALUES(granted_by)
            """, connection);
        insert.Parameters.AddWithValue("@name", CosmeticNames.Key(name));
        insert.Parameters.AddWithValue("@color", color);
        insert.Parameters.AddWithValue("@by", grantedBy.Length > 16 ? grantedBy[..16] : grantedBy);
        insert.Parameters.AddWithValue("@now", NowMs());
        await insert.ExecuteNonQueryAsync();
    }

    public async Task<bool> RevokeAsync(string name, int color)
    {
        var key = CosmeticNames.Key(name);
        await using var connection = await OpenAsync();
        int removed;
        await using (var delete = new MySqlCommand($"DELETE FROM `{_owned}` WHERE name = @name AND color = @color", connection))
        {
            delete.Parameters.AddWithValue("@name", key);
            delete.Parameters.AddWithValue("@color", color);
            removed = await delete.ExecuteNonQueryAsync();
        }

        await using (var unequip = new MySqlCommand($"DELETE FROM `{_equipped}` WHERE name = @name AND color = @color", connection))
        {
            unequip.Parameters.AddWithValue("@name", key);
            unequip.Parameters.AddWithValue("@color", color);
            await unequip.ExecuteNonQueryAsync();
        }

        return removed > 0;
    }

    public async Task<bool> EquipAsync(string name, int? color)
    {
        var key = CosmeticNames.Key(name);
        await using var connection = await OpenAsync();
        if (color is null)
        {
            await using var delete = new MySqlCommand($"DELETE FROM `{_equipped}` WHERE name = @name", connection);
            delete.Parameters.AddWithValue("@name", key);
            await delete.ExecuteNonQueryAsync();
            return true;
        }

        await using (var check = new MySqlCommand($"SELECT 1 FROM `{_owned}` WHERE name = @name AND color = @color", connection))
        {
            check.Parameters.AddWithValue("@name", key);
            check.Parameters.AddWithValue("@color", color.Value);
            if (await check.ExecuteScalarAsync() is null)
            {
                return false;
            }
        }

        await using var upsert = new MySqlCommand($"""
            INSERT INTO `{_equipped}` (name, color) VALUES (@name, @color)
            ON DUPLICATE KEY UPDATE color = VALUES(color)
            """, connection);
        upsert.Parameters.AddWithValue("@name", key);
        upsert.Parameters.AddWithValue("@color", color.Value);
        await upsert.ExecuteNonQueryAsync();
        return true;
    }
}

/// <summary>Sem banco (teste local): some ao reiniciar.</summary>
public sealed class MemoryCosmeticsStore : ICosmeticsStore
{
    private readonly ConcurrentDictionary<string, List<int>> _owned = new();
    private readonly ConcurrentDictionary<string, int> _equipped = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<bool> IsEmptyAsync() => Task.FromResult(_owned.IsEmpty);

    public Task<int[]> OwnedAsync(string name)
    {
        var list = _owned.GetValueOrDefault(CosmeticNames.Key(name));
        lock (_owned)
        {
            return Task.FromResult(list?.ToArray() ?? Array.Empty<int>());
        }
    }

    public Task<int?> EquippedAsync(string name) =>
        Task.FromResult(_equipped.TryGetValue(CosmeticNames.Key(name), out var c) ? c : (int?)null);

    public Task<Dictionary<string, int>> EquippedForAsync(IReadOnlyCollection<string> names)
    {
        var wanted = names.Count == 0 ? null : names.Select(CosmeticNames.Key).ToHashSet();
        return Task.FromResult(_equipped.Where(e => wanted is null || wanted.Contains(e.Key))
            .ToDictionary(e => e.Key, e => e.Value));
    }

    public Task GrantAsync(string name, int color, string grantedBy)
    {
        lock (_owned)
        {
            var list = _owned.GetOrAdd(CosmeticNames.Key(name), _ => new List<int>());
            if (!list.Contains(color))
            {
                list.Add(color);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(string name, int color)
    {
        var key = CosmeticNames.Key(name);
        bool removed;
        lock (_owned)
        {
            removed = _owned.TryGetValue(key, out var list) && list.Remove(color);
        }

        if (_equipped.TryGetValue(key, out var eq) && eq == color)
        {
            _equipped.TryRemove(key, out _);
        }

        return Task.FromResult(removed);
    }

    public Task<bool> EquipAsync(string name, int? color)
    {
        var key = CosmeticNames.Key(name);
        if (color is null)
        {
            _equipped.TryRemove(key, out _);
            return Task.FromResult(true);
        }

        lock (_owned)
        {
            if (!_owned.TryGetValue(key, out var list) || !list.Contains(color.Value))
            {
                return Task.FromResult(false);
            }
        }

        _equipped[key] = color.Value;
        return Task.FromResult(true);
    }
}
