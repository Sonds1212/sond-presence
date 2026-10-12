using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using SondPresence;
using StackExchange.Redis;

// Serviço do Sond Client: presença (quem está usando o client em cada servidor) e
// distribuição de atualizações do launcher/mod.
//
// Presença: cada cliente manda, de tempos em tempos, "estou no servidor X com o UUID Y" e
// recebe de volta quem mais está no mesmo servidor usando o client. É assim que o selo
// aparece em servidores de terceiros, onde não dá para instalar plugin — mesma ideia do Lunar.
//
// Atualizações: o publish (tools/SondPublish) sobe os arquivos e um manifesto assinado.
// O launcher baixa, confere a assinatura e os hashes, e se atualiza sozinho.

var builder = WebApplication.CreateBuilder(args);

// Senhas ficam fora do appsettings.json (que vai para o GitHub): localmente em
// appsettings.Secrets.json (ignorado pelo git); na hospedagem, em variáveis de ambiente.
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true);
builder.Configuration.AddEnvironmentVariables();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Render/Railway/Heroku dizem a porta em $PORT. Sem isso, 8080.
var port = Environment.GetEnvironmentVariable("PORT");
builder.WebHost.UseUrls(builder.Configuration["Urls"] is { Length: > 0 } urls
    ? urls
    : $"http://0.0.0.0:{(string.IsNullOrEmpty(port) ? "8080" : port)}");

// Uploads do publish carregam o launcher inteiro (~80 MB).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024L * 1024 * 1024);

// Atrás do proxy da hospedagem, o IP de verdade vem em X-Forwarded-For. Sem isso o limite
// por IP valeria para todos os jogadores juntos.
var behindProxy = builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false) ||
                  Environment.GetEnvironmentVariable("RENDER") is not null;
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Quanto tempo uma presença vale sem receber novo heartbeat.
var ttl = TimeSpan.FromSeconds(
    int.TryParse(builder.Configuration["Presence:TtlSeconds"], out var t) ? t : 45);

// Opcional: se definido, o cliente precisa mandar o mesmo valor no header X-Sond-Key.
var apiKey = builder.Configuration["Presence:ApiKey"];

// Onde guardar a presença, em ordem de preferência: banco MySQL/TiDB, Redis, memória.
var sqlConnection = builder.Configuration["Database:ConnectionString"];
var redisConnection = builder.Configuration["Redis:ConnectionString"];
var storeName = "memória";
if (!string.IsNullOrWhiteSpace(sqlConnection))
{
    var table = builder.Configuration["Database:Table"] is { Length: > 0 } tb ? tb : "sond_presence";
    var sql = new SqlPresenceStore(sqlConnection, ttl, table);
    await sql.InitializeAsync();
    builder.Services.AddSingleton<IPresenceStore>(sql);
    storeName = "MySQL/TiDB";
}
else if (!string.IsNullOrWhiteSpace(redisConnection))
{
    storeName = "Redis";
    var prefix = builder.Configuration["Redis:Prefix"] is { Length: > 0 } p ? p : "sond:";
    var options = ConfigurationOptions.Parse(redisConnection);
    options.AbortOnConnectFail = false;
    var multiplexer = ConnectionMultiplexer.Connect(options);
    builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
    builder.Services.AddSingleton<IPresenceStore>(new RedisPresenceStore(multiplexer, ttl, prefix));
}
else
{
    builder.Services.AddSingleton<IPresenceStore>(new MemoryPresenceStore(ttl));
}

// Atualizações: no banco quando há banco (hospedagem grátis apaga o disco), senão em disco.
var updatesDir = Path.GetFullPath(builder.Configuration["Updates:Directory"] is { Length: > 0 } d
    ? d
    : Path.Combine(builder.Environment.ContentRootPath, "updates"));
IUpdateStore updates = !string.IsNullOrWhiteSpace(sqlConnection)
    ? new SqlUpdateStore(sqlConnection, "sond_", Path.Combine(Path.GetTempPath(), "sond-update-cache"))
    : new DiskUpdateStore(updatesDir);
await updates.InitializeAsync();

// Heartbeat é a cada 20 s; 30/min por IP sobra para várias contas atrás do mesmo NAT
// e ainda corta quem tentar encher o banco.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("presence", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "-",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
if (behindProxy)
{
    app.UseForwardedHeaders();
}

app.UseRateLimiter();

var log = app.Logger;
log.LogInformation("Presença usando {Store}; atualizações em {Updates}", storeName,
    updates is SqlUpdateStore ? "banco" : updatesDir);

var store = app.Services.GetRequiredService<IPresenceStore>();

string NormalizeServer(string? server) =>
    string.IsNullOrWhiteSpace(server) ? "-" : server.Trim().ToLowerInvariant();

bool HasKey(HttpRequest http) =>
    string.IsNullOrEmpty(apiKey) || http.Headers["X-Sond-Key"] == apiKey;

app.MapPost("/presence", async (HeartbeatRequest request, HttpRequest http) =>
{
    if (!HasKey(http))
    {
        return Results.Unauthorized();
    }

    // UUID e servidor viram chave no banco: só aceita formato conhecido.
    if (!Guid.TryParse(request.Uuid, out var uuid))
    {
        return Results.BadRequest(new { error = "uuid inválido" });
    }

    var server = NormalizeServer(request.Server);
    if (server.Length > 255)
    {
        return Results.BadRequest(new { error = "servidor inválido" });
    }

    var name = (request.Name ?? "").Trim();
    if (name.Length > 32)
    {
        name = name[..32];
    }

    var users = await store.HeartbeatAsync(uuid.ToString(), name, server);
    return Results.Ok(new HeartbeatResponse(users, ttl.TotalSeconds));
}).RequireRateLimiting("presence");

app.MapPost("/presence/leave", async (LeaveRequest request, HttpRequest http) =>
{
    if (!HasKey(http))
    {
        return Results.Unauthorized();
    }

    if (Guid.TryParse(request.Uuid, out var uuid))
    {
        await store.LeaveAsync(uuid.ToString());
    }

    return Results.Ok();
}).RequireRateLimiting("presence");

app.MapGet("/stats", async () =>
{
    var stats = await store.StatsAsync();
    return Results.Ok(new { total = stats.Total, porServidor = stats.PorServidor });
});

// ---------------------------------------------------------------------------------------
// Atualizações
// ---------------------------------------------------------------------------------------

var adminToken = builder.Configuration["Updates:AdminToken"];
var shaPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);
var namePattern = new Regex(@"^[A-Za-z0-9._+\-]{1,128}$", RegexOptions.Compiled);

bool IsAdmin(HttpRequest http)
{
    if (string.IsNullOrEmpty(adminToken))
    {
        return false;
    }

    var sent = Encoding.UTF8.GetBytes(http.Headers["X-Sond-Admin"].ToString());
    var expected = Encoding.UTF8.GetBytes(adminToken);
    return CryptographicOperations.FixedTimeEquals(sent, expected);
}

bool IsValidFile(string sha, string name) =>
    shaPattern.IsMatch(sha) && namePattern.IsMatch(name) && name != "." && name != "..";

app.MapMethods("/updates/manifest.json", ["GET", "HEAD"], async (HttpResponse response) =>
{
    var manifest = await updates.ReadManifestAsync();
    if (manifest is null)
    {
        return Results.NotFound();
    }

    response.Headers.CacheControl = "no-cache";
    return Results.Text(manifest, "application/json", Encoding.UTF8);
});

app.MapMethods("/updates/files/{sha}/{name}", ["GET", "HEAD"], async (string sha, string name, HttpRequest request, HttpResponse response, CancellationToken ct) =>
{
    if (!IsValidFile(sha, name))
    {
        return Results.NotFound();
    }

    // HEAD só pergunta se existe (o publish usa para não reenviar) — não precisa materializar.
    if (HttpMethods.IsHead(request.Method))
    {
        return await updates.HasFileAsync(sha) ? Results.Ok() : Results.NotFound();
    }

    var path = await updates.GetLocalPathAsync(sha, name, ct);
    if (path is null)
    {
        return Results.NotFound();
    }

    // O caminho tem o hash: o conteúdo nunca muda, pode cachear para sempre.
    response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(path, "application/octet-stream", name, enableRangeProcessing: true);
});

// Link fixo para mandar aos amigos: sempre o instalador mais recente.
app.MapGet("/download", async () =>
{
    var manifest = await updates.ReadManifestAsync();
    var installer = manifest is null ? null : ManifestReader.Installer(manifest);
    return installer is null
        ? Results.NotFound("Nenhum instalador publicado ainda.")
        : Results.Redirect($"/updates/files/{installer.Value.Sha}/{installer.Value.Name}");
});

app.MapPut("/admin/files/{sha}/{name}", async (string sha, string name, HttpRequest http, CancellationToken ct) =>
{
    if (!IsAdmin(http))
    {
        return Results.Unauthorized();
    }

    if (!IsValidFile(sha, name))
    {
        return Results.BadRequest(new { error = "nome ou hash inválido" });
    }

    var error = await updates.SaveFileAsync(sha, name, http.Body, ct);
    return error is null ? Results.Ok() : Results.BadRequest(new { error });
});

app.MapPut("/admin/manifest", async (HttpRequest http) =>
{
    if (!IsAdmin(http))
    {
        return Results.Unauthorized();
    }

    using var reader = new StreamReader(http.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();

    // O servidor não confere a assinatura (quem confere é o launcher, com a chave pública
    // embutida) — só garante que não vai publicar algo malformado ou com arquivo faltando.
    List<(string Sha, string Name)> entries;
    try
    {
        entries = ManifestReader.Entries(body);
    }
    catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = "manifesto inválido: " + ex.Message });
    }

    foreach (var (sha, name) in entries)
    {
        if (!IsValidFile(sha, name) || !await updates.HasFileAsync(sha))
        {
            return Results.BadRequest(new { error = $"arquivo não enviado: {name}" });
        }
    }

    await updates.WriteManifestAsync(body, entries.Select(e => e.Sha).ToList());
    log.LogInformation("Novo manifesto publicado ({Count} arquivos)", entries.Count);
    return Results.Ok();
});

app.MapGet("/", () => Results.Text("Sond Client — ok"));

app.Run();

internal sealed class HeartbeatRequest
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Endereço do servidor onde o jogador está (ex.: bawmc.net).</summary>
    [JsonPropertyName("server")]
    public string? Server { get; set; }
}

internal sealed class LeaveRequest
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";
}

internal sealed record HeartbeatResponse(
    [property: JsonPropertyName("users")] string[] Users,
    [property: JsonPropertyName("ttlSeconds")] double TtlSeconds);
