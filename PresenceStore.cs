using System.Collections.Concurrent;
using StackExchange.Redis;

namespace SondPresence;

/// <summary>
/// Onde fica quem está online com o client. Redis em produção (vários processos/máquinas
/// enxergam a mesma lista e reiniciar o serviço não zera ninguém); memória quando não há
/// Redis configurado, para rodar local sem dependência.
/// </summary>
public interface IPresenceStore
{
    /// <summary>Registra o heartbeat e devolve os UUIDs de quem está no mesmo servidor.</summary>
    Task<string[]> HeartbeatAsync(string uuid, string name, string server);

    Task LeaveAsync(string uuid);

    Task<PresenceStats> StatsAsync();
}

public sealed record PresenceStats(long Total, Dictionary<string, long> PorServidor);

public sealed class RedisPresenceStore : IPresenceStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _ttl;
    private readonly string _prefix;

    public RedisPresenceStore(IConnectionMultiplexer redis, TimeSpan ttl, string prefix)
    {
        _redis = redis;
        _ttl = ttl;
        _prefix = prefix;
    }

    // user:{uuid}  -> hash { name, server }, expira sozinho depois do TTL
    // srv:{server} -> sorted set uuid => último heartbeat (ms); quem passa do TTL é podado
    // online       -> sorted set de todo mundo, para o /stats
    // servers      -> set com os servidores que já tiveram alguém, para o /stats não precisar de SCAN
    private RedisKey UserKey(string uuid) => $"{_prefix}user:{uuid}";
    private RedisKey ServerKey(string server) => $"{_prefix}srv:{server}";
    private RedisKey OnlineKey => $"{_prefix}online";
    private RedisKey ServersKey => $"{_prefix}servers";

    public async Task<string[]> HeartbeatAsync(string uuid, string name, string server)
    {
        var db = _redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = now - (long)_ttl.TotalMilliseconds;

        // Trocou de servidor: sai da lista antiga na hora, sem esperar o TTL.
        var previous = await db.HashGetAsync(UserKey(uuid), "server");
        var batch = db.CreateBatch();
        var tasks = new List<Task>();

        if (previous.HasValue && previous != server)
        {
            tasks.Add(batch.SortedSetRemoveAsync(ServerKey(previous!), uuid));
        }

        tasks.Add(batch.HashSetAsync(UserKey(uuid), [new("name", name), new("server", server)]));
        tasks.Add(batch.KeyExpireAsync(UserKey(uuid), _ttl));
        tasks.Add(batch.SortedSetAddAsync(ServerKey(server), uuid, now));
        tasks.Add(batch.SortedSetRemoveRangeByScoreAsync(ServerKey(server), double.NegativeInfinity, cutoff));
        tasks.Add(batch.KeyExpireAsync(ServerKey(server), _ttl * 2));
        tasks.Add(batch.SortedSetAddAsync(OnlineKey, uuid, now));
        tasks.Add(batch.SortedSetRemoveRangeByScoreAsync(OnlineKey, double.NegativeInfinity, cutoff));
        tasks.Add(batch.SetAddAsync(ServersKey, server));
        var members = batch.SortedSetRangeByRankAsync(ServerKey(server));
        batch.Execute();

        await Task.WhenAll(tasks);
        return (await members).Select(m => m.ToString()).ToArray();
    }

    public async Task LeaveAsync(string uuid)
    {
        var db = _redis.GetDatabase();
        var server = await db.HashGetAsync(UserKey(uuid), "server");
        if (server.HasValue)
        {
            await db.SortedSetRemoveAsync(ServerKey(server!), uuid);
        }

        await db.KeyDeleteAsync(UserKey(uuid));
        await db.SortedSetRemoveAsync(OnlineKey, uuid);
    }

    public async Task<PresenceStats> StatsAsync()
    {
        var db = _redis.GetDatabase();
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)_ttl.TotalMilliseconds;

        var total = await db.SortedSetLengthAsync(OnlineKey, cutoff, double.PositiveInfinity);
        var porServidor = new Dictionary<string, long>();
        foreach (var server in await db.SetMembersAsync(ServersKey))
        {
            var count = await db.SortedSetLengthAsync(ServerKey(server.ToString()), cutoff, double.PositiveInfinity);
            if (count > 0)
            {
                porServidor[server.ToString()] = count;
            }
            else
            {
                await db.SetRemoveAsync(ServersKey, server);
            }
        }

        return new PresenceStats(total, porServidor);
    }
}

public sealed class MemoryPresenceStore : IPresenceStore
{
    private sealed record Entry(string Uuid, string Name, string Server, DateTimeOffset LastSeen);

    private readonly ConcurrentDictionary<string, Entry> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;

    public MemoryPresenceStore(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    private void Sweep()
    {
        var limit = DateTimeOffset.UtcNow - _ttl;
        foreach (var (key, entry) in _online)
        {
            if (entry.LastSeen < limit)
            {
                _online.TryRemove(key, out _);
            }
        }
    }

    public Task<string[]> HeartbeatAsync(string uuid, string name, string server)
    {
        Sweep();
        _online[uuid] = new Entry(uuid, name, server, DateTimeOffset.UtcNow);
        return Task.FromResult(_online.Values.Where(e => e.Server == server).Select(e => e.Uuid).ToArray());
    }

    public Task LeaveAsync(string uuid)
    {
        _online.TryRemove(uuid, out _);
        return Task.CompletedTask;
    }

    public Task<PresenceStats> StatsAsync()
    {
        Sweep();
        var porServidor = _online.Values.GroupBy(e => e.Server).ToDictionary(g => g.Key, g => (long)g.Count());
        return Task.FromResult(new PresenceStats(_online.Count, porServidor));
    }
}
