using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SondPresence;

/// <summary>
/// Prova de que quem chama é dono da conta Minecraft, sem senha nossa: o mesmo esquema que um
/// servidor online-mode usa.
///
/// 1. O mod pede um desafio (<c>serverId</c> aleatório).
/// 2. O mod chama a Mojang com o token da própria sessão ("entrei no servidor serverId").
/// 3. O serviço pergunta à Mojang (<c>hasJoined</c>) se aquele nick entrou com aquele serverId.
///
/// Deu certo: devolve um token de sessão assinado (HMAC) que vale por algumas horas.
/// </summary>
public sealed class CosmeticsAuth
{
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _challenges = new();
    private readonly byte[] _secret;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public CosmeticsAuth(string? secret, ILogger log)
    {
        // Sem segredo configurado: um por processo (reiniciar o serviço só pede novo login).
        _secret = string.IsNullOrWhiteSpace(secret) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(secret);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _log = log;
    }

    public string NewChallenge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, created) in _challenges)
        {
            if (now - created > ChallengeTtl)
            {
                _challenges.TryRemove(id, out _);
            }
        }

        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _challenges[challenge] = now;
        return challenge;
    }

    /// <summary>Confere com a Mojang. Devolve (nick oficial, uuid) ou null.</summary>
    public async Task<(string Name, string Uuid)?> VerifyAsync(string name, string serverId)
    {
        // Desafio só vale uma vez e só se fomos nós que emitimos.
        if (!_challenges.TryRemove(serverId, out var created) || DateTimeOffset.UtcNow - created > ChallengeTtl)
        {
            return null;
        }

        var url = "https://sessionserver.mojang.com/session/minecraft/hasJoined?username="
                  + Uri.EscapeDataString(name) + "&serverId=" + Uri.EscapeDataString(serverId);
        try
        {
            using var response = await _http.GetAsync(url);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var officialName = root.GetProperty("name").GetString();
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(officialName) || string.IsNullOrEmpty(id))
            {
                return null;
            }

            return (officialName, id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            _log.LogWarning("Mojang hasJoined falhou para {Name}: {Error}", name, ex.Message);
            return null;
        }
    }

    public string IssueToken(string name)
    {
        var expires = DateTimeOffset.UtcNow.Add(SessionTtl).ToUnixTimeSeconds();
        var payload = Base64Url(Encoding.UTF8.GetBytes($"{name}|{expires}"));
        return payload + "." + Base64Url(Sign(payload));
    }

    /// <summary>Nick dono do token, ou null se inválido/expirado.</summary>
    public string? ReadToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        byte[] sent;
        try
        {
            sent = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(sent, Sign(parts[0])))
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
        }
        catch (FormatException)
        {
            return null;
        }

        var sep = text.LastIndexOf('|');
        if (sep <= 0 || !long.TryParse(text[(sep + 1)..], out var expires)
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
        {
            return null;
        }

        return text[..sep];
    }

    private byte[] Sign(string payload) => HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
