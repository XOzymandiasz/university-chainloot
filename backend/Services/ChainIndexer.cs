using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ChainLoot.Backend.Models;

namespace ChainLoot.Backend.Services;

public sealed class ChainIndexer(HistoryStore store, MqttBridge mqtt, IHttpClientFactory clients, IConfiguration configuration)
{
    private const string Transfer = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    private const string Redeem = "0xb1eb15dc4b5de6600193c9844a13fce51a803945e8af89faed0f58974b03e6c6";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte> _contracts = new();
    public IEnumerable<string> Contracts => _contracts.Keys;

    private async Task<JsonElement> RpcAsync(string method, object?[] parameters, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("blockchain");
        var url = configuration["Blockchain:RpcUrl"] ?? "http://127.0.0.1:8545";
        using var response = await client.PostAsJsonAsync(url, new { jsonrpc = "2.0", id = 1, method, @params = parameters }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException("Błąd RPC: " + error.ToString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static long HexLong(string value) => Convert.ToInt64(value[2..], 16);
    private static string Hex(long value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    private static string TopicAddress(string value) => "0x" + value[^40..].ToLowerInvariant();
    private static string TopicToken(string value) =>
        BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private async Task<(string Hash, DateTimeOffset Time)?> BlockAsync(long number, CancellationToken cancellationToken)
    {
        var result = await RpcAsync("eth_getBlockByNumber", [Hex(number), false], cancellationToken);
        if (result.ValueKind == JsonValueKind.Null) return null;
        return (result.GetProperty("hash").GetString()!,
            DateTimeOffset.FromUnixTimeSeconds(HexLong(result.GetProperty("timestamp").GetString()!)));
    }

    public async Task SyncAsync(string contract, CancellationToken cancellationToken)
    {
        contract = contract.ToLowerInvariant();
        var mutex = _locks.GetOrAdd(contract, _ => new SemaphoreSlim(1, 1));
        await mutex.WaitAsync(cancellationToken);
        try
        {
            var chain = await RpcAsync("eth_chainId", [], cancellationToken);
            if (HexLong(chain.GetString()!) != 31337) throw new InvalidOperationException("Wymagana lokalna sieć Anvil (chain ID 31337).");
            var code = await RpcAsync("eth_getCode", [contract, "latest"], cancellationToken);
            if (code.GetString() is null or "0x") throw new InvalidOperationException("Brak kontraktu pod podanym adresem.");
            _contracts.TryAdd(contract, 0);
            var latestValue = await RpcAsync("eth_blockNumber", [], cancellationToken);
            var latest = HexLong(latestValue.GetString()!);
            var previous = await store.GetCheckpointAsync(contract, cancellationToken);
            if (previous is not null)
            {
                var block = previous.BlockNumber <= latest ? await BlockAsync(previous.BlockNumber, cancellationToken) : null;
                if (block is null || block.Value.Hash != previous.BlockHash)
                {
                    await store.ResetAsync(contract, cancellationToken);
                    previous = null;
                    await mqtt.PublishAsync(contract, cancellationToken);
                }
            }

            var start = previous?.BlockNumber + 1 ?? 0;
            while (start <= latest)
            {
                var end = Math.Min(start + 1999, latest);
                var filter = new { address = contract, fromBlock = Hex(start), toBlock = Hex(end), topics = new[] { new[] { Transfer, Redeem } } };
                var logs = await RpcAsync("eth_getLogs", [filter], cancellationToken);
                var blockTimes = new Dictionary<long, (string Hash, DateTimeOffset Time)>();
                var batch = new List<ItemEvent>();
                foreach (var log in logs.EnumerateArray())
                {
                    var number = HexLong(log.GetProperty("blockNumber").GetString()!);
                    if (!blockTimes.ContainsKey(number))
                        blockTimes[number] = await BlockAsync(number, cancellationToken) ?? throw new InvalidOperationException("Nie znaleziono bloku.");
                    var topics = log.GetProperty("topics");
                    var signature = topics[0].GetString()!.ToLowerInvariant();
                    var isTransfer = signature == Transfer;
                    var sender = TopicAddress(topics[isTransfer ? 1 : 2].GetString()!);
                    var recipient = isTransfer ? TopicAddress(topics[2].GetString()!) : "";
                    var tokenId = TopicToken(topics[isTransfer ? 3 : 1].GetString()!);
                    var type = isTransfer ? (sender == ZeroAddress ? "issue" : "transfer") : "redeem";
                    batch.Add(new(contract, log.GetProperty("transactionHash").GetString()!,
                        (int)HexLong(log.GetProperty("logIndex").GetString()!), number, blockTimes[number].Time,
                        type, tokenId, sender, recipient));
                }
                var endBlock = blockTimes.TryGetValue(end, out var existing)
                    ? existing : await BlockAsync(end, cancellationToken) ?? throw new InvalidOperationException("Nie znaleziono bloku.");
                var inserted = await store.SaveBatchAsync(contract, batch, new(end, endBlock.Hash), cancellationToken);
                if (inserted > 0) await mqtt.PublishAsync(contract, cancellationToken);
                start = end + 1;
            }
        }
        finally
        {
            mutex.Release();
        }
    }
}

public sealed class IndexWorker(ChainIndexer indexer, ILogger<IndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var contract in indexer.Contracts)
            {
                try { await indexer.SyncAsync(contract, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning("Indeksowanie {Contract}: {Error}", contract, ex.Message); }
            }
        }
    }
}
