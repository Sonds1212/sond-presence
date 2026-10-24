using System.Collections.Concurrent;
using System.Security.Cryptography;
using MySqlConnector;

namespace SondPresence;

/// <summary>
/// Onde ficam as versões publicadas (arquivos + manifesto assinado).
/// Disco quando o serviço roda num servidor próprio; banco quando roda em hospedagem
/// grátis (Render etc.), onde o disco é apagado a cada reinício.
/// </summary>
public interface IUpdateStore
{
    Task InitializeAsync();

    Task<bool> HasFileAsync(string sha);

    /// <summary>Grava o arquivo se o hash bater. Devolve null em caso de sucesso ou a mensagem de erro.</summary>
    Task<string?> SaveFileAsync(string sha, string name, Stream body, CancellationToken ct);

    /// <summary>Caminho local do arquivo para servir (o banco materializa num cache em disco).</summary>
    Task<string?> GetLocalPathAsync(string sha, string name, CancellationToken ct);

    /// <summary>Manifesto atual da plataforma ("windows" ou "linux").</summary>
    Task<string?> ReadManifestAsync(string platform = UpdatePlatforms.Windows);

    /// <summary>
    /// Publica o manifesto da plataforma e apaga arquivos que nenhuma das duas últimas versões
    /// de NENHUMA plataforma usa (publicar o Linux não pode apagar o que o Windows baixa).
    /// </summary>
    Task WriteManifestAsync(string body, IReadOnlyCollection<string> referencedShas, string platform = UpdatePlatforms.Windows);
}

public static class UpdatePlatforms
{
    public const string Windows = "windows";
    public const string Linux = "linux";

    public static readonly string[] All = [Windows, Linux];

    public static bool IsValid(string? platform) => platform is Windows or Linux;
}

internal static class HashHelper
{
    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}

public sealed class DiskUpdateStore : IUpdateStore
{
    private readonly string _directory;
    private readonly string _filesDir;

    public DiskUpdateStore(string directory)
    {
        _directory = directory;
        _filesDir = Path.Combine(directory, "files");
    }

    /// <summary>manifest.json (Windows, o de sempre) ou manifest-linux.json.</summary>
    private string ManifestPath(string platform) =>
        Path.Combine(_directory, platform == UpdatePlatforms.Windows ? "manifest.json" : $"manifest-{platform}.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_filesDir);
        return Task.CompletedTask;
    }

    private string ShaDir(string sha) => Path.Combine(_filesDir, sha);

    public Task<bool> HasFileAsync(string sha) =>
        Task.FromResult(Directory.Exists(ShaDir(sha)) && Directory.EnumerateFiles(ShaDir(sha)).Any(f => !f.EndsWith(".upload")));

    public async Task<string?> SaveFileAsync(string sha, string name, Stream body, CancellationToken ct)
    {
        var path = Path.Combine(ShaDir(sha), name);
        if (File.Exists(path))
        {
            return null;
        }

        Directory.CreateDirectory(ShaDir(sha));
        var temp = path + ".upload";
        using (var sha256 = SHA256.Create())
        {
            await using (var output = File.Create(temp))
            await using (var hashing = new CryptoStream(output, sha256, CryptoStreamMode.Write))
            {
                await body.CopyToAsync(hashing, ct);
            }

            var actual = HashHelper.Hex(sha256.Hash!);
            if (actual != sha)
            {
                File.Delete(temp);
                return $"hash não confere (recebido {actual})";
            }
        }

        File.Move(temp, path, overwrite: true);
        return null;
    }

    public Task<string?> GetLocalPathAsync(string sha, string name, CancellationToken ct)
    {
        var path = Path.Combine(ShaDir(sha), name);
        return Task.FromResult(File.Exists(path) ? path : null);
    }

    public async Task<string?> ReadManifestAsync(string platform = UpdatePlatforms.Windows) =>
        File.Exists(ManifestPath(platform)) ? await File.ReadAllTextAsync(ManifestPath(platform)) : null;

    public async Task WriteManifestAsync(string body, IReadOnlyCollection<string> referencedShas, string platform = UpdatePlatforms.Windows)
    {
        var path = ManifestPath(platform);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, body);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Arquivos em pedaços de 1 MB numa tabela (o TiDB limita o tamanho de cada linha), mais um
/// cache em disco local para não reler do banco a cada download — se o disco sumir, o cache
/// se refaz sozinho no próximo pedido.
/// </summary>
public sealed class SqlUpdateStore : IUpdateStore
{
    private const int ChunkSize = 1024 * 1024;

    private readonly string _connectionString;
    private readonly string _prefix;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SqlUpdateStore(string connectionString, string tablePrefix, string cacheDir)
    {
        _connectionString = connectionString;
        _prefix = tablePrefix;
        _cacheDir = cacheDir;
    }

    private string Files => $"`{_prefix}update_files`";
    private string Chunks => $"`{_prefix}update_chunks`";
    private string Manifests => $"`{_prefix}update_manifests`";

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_cacheDir);
        await using var connection = await OpenAsync();
        await ExecAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS {Files} (
                sha     CHAR(64)     NOT NULL PRIMARY KEY,
                name    VARCHAR(128) NOT NULL,
                size    BIGINT       NOT NULL,
                created BIGINT       NOT NULL
            )
            """);
        await ExecAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS {Chunks} (
                sha  CHAR(64)   NOT NULL,
                idx  INT        NOT NULL,
                data MEDIUMBLOB NOT NULL,
                PRIMARY KEY (sha, idx)
            )
            """);
        await ExecAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS {Manifests} (
                id        BIGINT     NOT NULL AUTO_INCREMENT PRIMARY KEY,
                body      MEDIUMTEXT NOT NULL,
                published BIGINT     NOT NULL,
                KEY idx_published (published)
            )
            """);

        // Manifestos por plataforma: tabelas antigas (só Windows) ganham a coluna.
        await using (var check = new MySqlCommand(
                   "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t AND column_name = 'platform'",
                   connection))
        {
            check.Parameters.AddWithValue("@t", _prefix + "update_manifests");
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) == 0)
            {
                await ExecAsync(connection, $"ALTER TABLE {Manifests} ADD COLUMN platform VARCHAR(16) NOT NULL DEFAULT 'windows'");
            }
        }
    }

    public async Task<bool> HasFileAsync(string sha)
    {
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"SELECT 1 FROM {Files} WHERE sha = @sha", connection);
        command.Parameters.AddWithValue("@sha", sha);
        return await command.ExecuteScalarAsync() is not null;
    }

    public async Task<string?> SaveFileAsync(string sha, string name, Stream body, CancellationToken ct)
    {
        if (await HasFileAsync(sha))
        {
            return null;
        }

        await using var connection = await OpenAsync();

        // Sobra de upload que caiu no meio.
        await ExecAsync(connection, $"DELETE FROM {Chunks} WHERE sha = @sha", ("@sha", sha));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        var index = 0;
        long size = 0;

        while (true)
        {
            var filled = 0;
            while (filled < ChunkSize)
            {
                var read = await body.ReadAsync(buffer.AsMemory(filled, ChunkSize - filled), ct);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, filled);
            await ExecAsync(connection, $"INSERT INTO {Chunks} (sha, idx, data) VALUES (@sha, @idx, @data)",
                ("@sha", sha), ("@idx", index++), ("@data", buffer.AsSpan(0, filled).ToArray()));
            size += filled;

            if (filled < ChunkSize)
            {
                break;
            }
        }

        var actual = HashHelper.Hex(hash.GetHashAndReset());
        if (actual != sha)
        {
            await ExecAsync(connection, $"DELETE FROM {Chunks} WHERE sha = @sha", ("@sha", sha));
            return $"hash não confere (recebido {actual})";
        }

        // A linha em update_files é o "commit": só existe depois de todos os pedaços.
        await ExecAsync(connection, $"INSERT INTO {Files} (sha, name, size, created) VALUES (@sha, @name, @size, @now)",
            ("@sha", sha), ("@name", name), ("@size", size), ("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return null;
    }

    public async Task<string?> GetLocalPathAsync(string sha, string name, CancellationToken ct)
    {
        var cached = Path.Combine(_cacheDir, sha, name);
        if (File.Exists(cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(sha, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(cached))
            {
                return cached;
            }

            if (!await HasFileAsync(sha))
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            var temp = cached + ".tmp";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var connection = await OpenAsync())
            await using (var command = new MySqlCommand($"SELECT data FROM {Chunks} WHERE sha = @sha ORDER BY idx", connection))
            {
                command.Parameters.AddWithValue("@sha", sha);
                await using var reader = await command.ExecuteReaderAsync(ct);
                await using var output = File.Create(temp);
                while (await reader.ReadAsync(ct))
                {
                    var data = (byte[])reader.GetValue(0);
                    hash.AppendData(data);
                    await output.WriteAsync(data, ct);
                }
            }

            if (HashHelper.Hex(hash.GetHashAndReset()) != sha)
            {
                File.Delete(temp);
                throw new InvalidOperationException($"arquivo {sha} corrompido no banco");
            }

            File.Move(temp, cached, overwrite: true);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> ReadManifestAsync(string platform = UpdatePlatforms.Windows)
    {
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand(
            $"SELECT body FROM {Manifests} WHERE platform = @p ORDER BY published DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("@p", platform);
        return await command.ExecuteScalarAsync() as string;
    }

    public async Task WriteManifestAsync(string body, IReadOnlyCollection<string> referencedShas, string platform = UpdatePlatforms.Windows)
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection, $"INSERT INTO {Manifests} (body, published, platform) VALUES (@body, @now, @p)",
            ("@body", body), ("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("@p", platform));

        // Guarda as duas últimas versões de cada plataforma: quem está no meio de uma atualização
        // ainda baixa da anterior, e publicar uma plataforma não apaga os arquivos da outra.
        var keep = new HashSet<string>(referencedShas, StringComparer.OrdinalIgnoreCase);
        foreach (var p in UpdatePlatforms.All)
        {
            await using var recent = new MySqlCommand(
                $"SELECT body FROM {Manifests} WHERE platform = @p ORDER BY published DESC LIMIT 2", connection);
            recent.Parameters.AddWithValue("@p", p);
            await using var reader = await recent.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                foreach (var sha in ManifestReader.ReferencedShas(reader.GetString(0)))
                {
                    keep.Add(sha);
                }
            }
        }

        var stale = new List<string>();
        await using (var all = new MySqlCommand($"SELECT sha FROM {Files}", connection))
        await using (var reader = await all.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var sha = reader.GetString(0);
                if (!keep.Contains(sha))
                {
                    stale.Add(sha);
                }
            }
        }

        foreach (var sha in stale)
        {
            await ExecAsync(connection, $"DELETE FROM {Files} WHERE sha = @sha", ("@sha", sha));
            await ExecAsync(connection, $"DELETE FROM {Chunks} WHERE sha = @sha", ("@sha", sha));
            try
            {
                var dir = Path.Combine(_cacheDir, sha);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // cache é descartável
            }
        }

        // Histórico de manifestos: os 5 últimos de cada plataforma bastam.
        await ExecAsync(connection, $"""
            DELETE FROM {Manifests} WHERE platform = @p AND published < (
                SELECT published FROM (SELECT published FROM {Manifests} WHERE platform = @p ORDER BY published DESC LIMIT 1 OFFSET 4) AS t)
            """, ("@p", platform));
    }
}

/// <summary>Lê o envelope { manifest: base64, signature } que o SondPublish envia.</summary>
public static class ManifestReader
{
    /// <summary>Arquivos (inclusive o instalador) citados no manifesto. Lança se o JSON for inválido.</summary>
    public static List<(string Sha, string Name)> Entries(string envelopeJson)
    {
        using var envelope = System.Text.Json.JsonDocument.Parse(envelopeJson);
        var payload = Convert.FromBase64String(envelope.RootElement.GetProperty("manifest").GetString()!);
        _ = envelope.RootElement.GetProperty("signature").GetString();
        using var manifest = System.Text.Json.JsonDocument.Parse(payload);

        var result = new List<(string, string)>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            result.Add(Entry(file));
        }

        if (manifest.RootElement.TryGetProperty("installer", out var installer) &&
            installer.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            result.Add(Entry(installer));
        }

        if (manifest.RootElement.TryGetProperty("downloads", out var downloads) &&
            downloads.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var file in downloads.EnumerateArray())
            {
                result.Add(Entry(file));
            }
        }

        return result;
    }

    /// <summary>Pacote extra da lista "downloads" com a extensão pedida (ex.: ".AppImage").</summary>
    public static (string Sha, string Name)? Download(string envelopeJson, string extension)
    {
        try
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(envelopeJson);
            var payload = Convert.FromBase64String(envelope.RootElement.GetProperty("manifest").GetString()!);
            using var manifest = System.Text.Json.JsonDocument.Parse(payload);
            if (!manifest.RootElement.TryGetProperty("downloads", out var downloads) ||
                downloads.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            foreach (var file in downloads.EnumerateArray())
            {
                var entry = Entry(file);
                if (entry.Item2.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (string, string) Entry(System.Text.Json.JsonElement file) =>
        (file.GetProperty("sha256").GetString() ?? "", Path.GetFileName(file.GetProperty("path").GetString() ?? ""));

    /// <summary>
    /// hash → link externo (GitHub Releases) dos arquivos que o SondPublish hospedou fora daqui.
    /// Esses o serviço não guarda: só redireciona, e a banda do download não passa pelo Render.
    /// </summary>
    public static Dictionary<string, string> ExternalUrls(string envelopeJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(envelopeJson);
            var payload = Convert.FromBase64String(envelope.RootElement.GetProperty("manifest").GetString()!);
            using var manifest = System.Text.Json.JsonDocument.Parse(payload);

            void Add(System.Text.Json.JsonElement file)
            {
                if (file.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    file.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } link &&
                    link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    file.TryGetProperty("sha256", out var sha) && sha.GetString() is { Length: > 0 } hash)
                {
                    result[hash] = link;
                }
            }

            foreach (var name in new[] { "files", "downloads" })
            {
                if (manifest.RootElement.TryGetProperty(name, out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var file in list.EnumerateArray()) Add(file);
                }
            }
            if (manifest.RootElement.TryGetProperty("installer", out var installer))
            {
                Add(installer);
            }
        }
        catch
        {
            // manifesto antigo ou inválido: sem links externos
        }
        return result;
    }

    public static IEnumerable<string> ReferencedShas(string envelopeJson)
    {
        try
        {
            return Entries(envelopeJson).Select(e => e.Sha).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static (string Sha, string Name)? Installer(string envelopeJson)
    {
        try
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(envelopeJson);
            var payload = Convert.FromBase64String(envelope.RootElement.GetProperty("manifest").GetString()!);
            using var manifest = System.Text.Json.JsonDocument.Parse(payload);
            if (!manifest.RootElement.TryGetProperty("installer", out var installer) ||
                installer.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            return ((string, string))Entry(installer);
        }
        catch
        {
            return null;
        }
    }
}