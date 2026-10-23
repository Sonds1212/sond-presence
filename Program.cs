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

// Cosméticos (cor do selo): no mesmo banco quando há banco, senão em memória.
ICosmeticsStore cosmetics = !string.IsNullOrWhiteSpace(sqlConnection)
    ? new SqlCosmeticsStore(sqlConnection, "sond_")
    : new MemoryCosmeticsStore();
await cosmetics.InitializeAsync();
if (await cosmetics.IsEmptyAsync())
{
    // Primeira vez com cosméticos: as cores que o mod tinha fixas viram cores dadas e equipadas.
    foreach (var (seedName, seedColor) in new[] { ("SondS_", 0xA8284D), ("kistzinho", 0x2F0B1D) })
    {
        await cosmetics.GrantAsync(seedName, seedColor, "SondS_");
        await cosmetics.EquipAsync(seedName, seedColor);
    }
}
var equippedCache = new EquippedCache(cosmetics);

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

    // Cor do selo de cada um (só quem equipou alguma).
    var colors = new Dictionary<string, string>();
    try
    {
        var equipped = await equippedCache.ForNamesAsync(users.Select(u => u.Name));
        foreach (var user in users)
        {
            if (!string.IsNullOrEmpty(user.Name) && equipped.TryGetValue(CosmeticNames.Key(user.Name), out var color))
            {
                colors[user.Uuid] = (color & 0xFFFFFF).ToString("X6");
            }
        }
    }
    catch (Exception ex)
    {
        // cosmético nunca derruba a presença
        log.LogWarning("Cores do selo indisponíveis: {Error}", ex.Message);
    }

    return Results.Ok(new HeartbeatResponse(users.Select(u => u.Uuid).ToArray(), ttl.TotalSeconds, colors));
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

async Task<IResult> ServeManifest(HttpResponse response, string platform)
{
    var manifest = await updates.ReadManifestAsync(platform);
    if (manifest is null)
    {
        return Results.NotFound();
    }

    response.Headers.CacheControl = "no-cache";
    return Results.Text(manifest, "application/json", Encoding.UTF8);
}

// Windows fica no endereço de sempre (launchers antigos já instalados leem daqui).
app.MapMethods("/updates/manifest.json", ["GET", "HEAD"],
    (HttpResponse response) => ServeManifest(response, UpdatePlatforms.Windows));
app.MapMethods("/updates/linux/manifest.json", ["GET", "HEAD"],
    (HttpResponse response) => ServeManifest(response, UpdatePlatforms.Linux));

// Linux: `curl -fsSL https://.../linux | bash` instala o launcher (baixa do manifesto do Linux,
// confere a assinatura e cria o atalho no menu). O script vem com o endereço deste serviço.
var linuxDir = Path.Combine(AppContext.BaseDirectory, "linux");
app.MapGet("/linux", async (HttpRequest request) =>
{
    var script = await File.ReadAllTextAsync(Path.Combine(linuxDir, "install.sh"));
    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.Text(script.Replace("{{BASE_URL}}", baseUrl), "text/x-shellscript", Encoding.UTF8);
});
app.MapGet("/linux/icon.png", () => Results.File(Path.Combine(linuxDir, "icon.png"), "image/png"));

// Arquivos para baixar no PC com Linux (sem precisar de terminal):
// .deb (Ubuntu, Mint, Pop!_OS, Debian — dois cliques) e o instalador .sh para as outras distros.
app.MapGet("/download/linux", async () =>
{
    var manifest = await updates.ReadManifestAsync(UpdatePlatforms.Linux);
    var installer = manifest is null ? null : ManifestReader.Installer(manifest);
    return installer is null
        ? Results.NotFound("Nenhum pacote Linux publicado ainda.")
        : Results.Redirect($"/updates/files/{installer.Value.Sha}/{installer.Value.Name}");
});
// AppImage: um arquivo só que roda em qualquer distro (Arch, Fedora, ...), sem instalar nada.
app.MapGet("/download/appimage", async () =>
{
    var manifest = await updates.ReadManifestAsync(UpdatePlatforms.Linux);
    var appImage = manifest is null ? null : ManifestReader.Download(manifest, ".AppImage");
    return appImage is null
        ? Results.NotFound("Nenhum AppImage publicado ainda.")
        : Results.Redirect($"/updates/files/{appImage.Value.Sha}/{appImage.Value.Name}");
});
app.MapGet("/download/linux.sh", async (HttpRequest request) =>
{
    var script = await File.ReadAllTextAsync(Path.Combine(linuxDir, "install.sh"));
    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.File(Encoding.UTF8.GetBytes(script.Replace("{{BASE_URL}}", baseUrl)),
        "application/x-sh", "instalar-sond-client.sh");
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

    var platform = http.Query["platform"].ToString() is { Length: > 0 } p ? p : UpdatePlatforms.Windows;
    if (!UpdatePlatforms.IsValid(platform))
    {
        return Results.BadRequest(new { error = "plataforma inválida" });
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

    await updates.WriteManifestAsync(body, entries.Select(e => e.Sha).ToList(), platform);
    log.LogInformation("Novo manifesto publicado para {Platform} ({Count} arquivos)", platform, entries.Count);
    return Results.Ok();
});

// ---------------------------------------------------------------------------------------
// Cosméticos: cor do ícone (selo) na tab list
// ---------------------------------------------------------------------------------------

// Quem pode dar/tirar cores. Padrão: o dono do client.
var cosmeticAdmins = (builder.Configuration["Cosmetics:Admins"] is { Length: > 0 } admins ? admins : "SondS_")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(CosmeticNames.Key)
    .ToHashSet();
var cosmeticsAuth = new CosmeticsAuth(builder.Configuration["Cosmetics:TokenSecret"], log);
var nickPattern = new Regex("^[A-Za-z0-9_]{1,16}$", RegexOptions.Compiled);

string ColorHex(int rgb) => (rgb & 0xFFFFFF).ToString("X6");

int? ParseColor(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return null;
    }

    var t = text.Trim().TrimStart('#');
    if (t.Length == 3)
    {
        t = string.Concat(t.Select(c => $"{c}{c}"));
    }

    return t.Length == 6 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
}

string? SessionName(HttpRequest http) => cosmeticsAuth.ReadToken(http.Headers["X-Sond-Session"].ToString());

bool IsCosmeticAdmin(string? name) => name is not null && cosmeticAdmins.Contains(CosmeticNames.Key(name));

async Task<object> CosmeticsView(string name)
{
    var owned = await cosmetics.OwnedAsync(name);
    var equipped = await cosmetics.EquippedAsync(name);
    var coins = await cosmetics.CoinsAsync(name);
    return new
    {
        name,
        owned = owned.Select(ColorHex).ToArray(),
        equipped = equipped is null ? null : ColorHex(equipped.Value),
        coins
    };
}

app.MapGet("/cosmetics/challenge", () => Results.Ok(new { serverId = cosmeticsAuth.NewChallenge() }))
    .RequireRateLimiting("presence");

app.MapPost("/cosmetics/auth", async (CosmeticsAuthRequest request) =>
{
    if (!nickPattern.IsMatch(request.Name ?? "") || string.IsNullOrWhiteSpace(request.ServerId))
    {
        return Results.BadRequest(new { error = "pedido inválido" });
    }

    var verified = await cosmeticsAuth.VerifyAsync(request.Name!, request.ServerId!);
    if (verified is null)
    {
        return Results.Unauthorized();
    }

    var name = verified.Value.Name;
    return Results.Ok(new { token = cosmeticsAuth.IssueToken(name), name, admin = IsCosmeticAdmin(name) });
}).RequireRateLimiting("presence");

// Público: as cores de um jogador (o mod mostra só as do próprio jogador; o admin consulta qualquer um).
app.MapGet("/cosmetics/user/{name}", async (string name) =>
        nickPattern.IsMatch(name) ? Results.Ok(await CosmeticsView(name)) : Results.BadRequest(new { error = "nick inválido" }))
    .RequireRateLimiting("presence");

app.MapPost("/cosmetics/equip", async (CosmeticsEquipRequest request, HttpRequest http) =>
{
    var name = SessionName(http);
    if (name is null)
    {
        return Results.Unauthorized();
    }

    int? color = null;
    if (!string.IsNullOrWhiteSpace(request.Color))
    {
        color = ParseColor(request.Color);
        if (color is null)
        {
            return Results.BadRequest(new { error = "cor inválida" });
        }
    }

    if (!await cosmetics.EquipAsync(name, color))
    {
        return Results.BadRequest(new { error = "você não tem essa cor" });
    }

    equippedCache.Invalidate();
    return Results.Ok(await CosmeticsView(name));
}).RequireRateLimiting("presence");

app.MapPost("/cosmetics/admin/grant", async (CosmeticsGrantRequest request, HttpRequest http) =>
{
    var admin = SessionName(http);
    if (!IsCosmeticAdmin(admin))
    {
        return Results.Unauthorized();
    }

    var color = ParseColor(request.Color);
    if (!nickPattern.IsMatch(request.Target ?? "") || color is null)
    {
        return Results.BadRequest(new { error = "nick ou cor inválidos" });
    }

    await cosmetics.GrantAsync(request.Target!, color.Value, admin!);
    log.LogInformation("{Admin} deu a cor #{Color} para {Target}", admin, ColorHex(color.Value), request.Target);
    return Results.Ok(await CosmeticsView(request.Target!));
}).RequireRateLimiting("presence");

app.MapPost("/cosmetics/admin/revoke", async (CosmeticsGrantRequest request, HttpRequest http) =>
{
    var admin = SessionName(http);
    if (!IsCosmeticAdmin(admin))
    {
        return Results.Unauthorized();
    }

    var color = ParseColor(request.Color);
    if (!nickPattern.IsMatch(request.Target ?? "") || color is null)
    {
        return Results.BadRequest(new { error = "nick ou cor inválidos" });
    }

    await cosmetics.RevokeAsync(request.Target!, color.Value);
    equippedCache.Invalidate();
    log.LogInformation("{Admin} tirou a cor #{Color} de {Target}", admin, ColorHex(color.Value), request.Target);
    return Results.Ok(await CosmeticsView(request.Target!));
}).RequireRateLimiting("presence");

// ---------------------------------------------------------------------------------------
// Loja: itens pagos com as coins do client. O admin dá coins; o jogador compra.
// ---------------------------------------------------------------------------------------

app.MapGet("/shop", () => Results.Ok(new
{
    items = ShopCatalog.Items.Select(i => new { id = i.Id, name = i.Name, kind = i.Kind, color = ColorHex(i.Color), price = i.Price })
})).RequireRateLimiting("presence");

app.MapPost("/shop/buy", async (ShopBuyRequest request, HttpRequest http) =>
{
    var name = SessionName(http);
    if (name is null)
    {
        return Results.Unauthorized();
    }

    var item = ShopCatalog.Find(request.Item);
    if (item is null)
    {
        return Results.BadRequest(new { error = "item não existe" });
    }

    var result = await cosmetics.BuyColorAsync(name, item.Color, item.Price, item.Id);
    if (result == PurchaseResult.AlreadyOwned)
    {
        return Results.BadRequest(new { error = "você já tem esse item" });
    }

    if (result == PurchaseResult.NotEnoughCoins)
    {
        return Results.BadRequest(new { error = "coins insuficientes" });
    }

    log.LogInformation("{Name} comprou {Item} por {Price} coins", name, item.Id, item.Price);
    return Results.Ok(await CosmeticsView(name));
}).RequireRateLimiting("presence");

app.MapPost("/cosmetics/admin/coins", async (CosmeticsCoinsRequest request, HttpRequest http) =>
{
    var admin = SessionName(http);
    if (!IsCosmeticAdmin(admin))
    {
        return Results.Unauthorized();
    }

    if (!nickPattern.IsMatch(request.Target ?? "") || request.Amount == 0 || Math.Abs(request.Amount) > 1_000_000)
    {
        return Results.BadRequest(new { error = "nick ou quantidade inválidos" });
    }

    var balance = await cosmetics.AddCoinsAsync(request.Target!, request.Amount, "admin:" + admin);
    log.LogInformation("{Admin} mudou {Amount} coins de {Target} (saldo {Balance})", admin, request.Amount, request.Target, balance);
    return Results.Ok(await CosmeticsView(request.Target!));
}).RequireRateLimiting("presence");

app.MapGet("/", () => Results.Text("Sond Client — ok"));

app.Run();

internal sealed class ShopBuyRequest
{
    [JsonPropertyName("item")]
    public string? Item { get; set; }
}

internal sealed class CosmeticsCoinsRequest
{
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>Quanto somar (negativo tira).</summary>
    [JsonPropertyName("amount")]
    public long Amount { get; set; }
}

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
    [property: JsonPropertyName("ttlSeconds")] double TtlSeconds,
    // uuid → "RRGGBB": cor do selo de quem equipou uma.
    [property: JsonPropertyName("colors")] Dictionary<string, string> Colors);

internal sealed class CosmeticsAuthRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }
}

internal sealed class CosmeticsEquipRequest
{
    /// <summary>"RRGGBB"; vazio/null = volta ao selo padrão.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

internal sealed class CosmeticsGrantRequest
{
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}
