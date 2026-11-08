using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ChainLoot.Backend.Hubs;
using ChainLoot.Backend.Cluster;
using ChainLoot.Backend.Models;
using ChainLoot.Backend.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var clusterEnabled = builder.Configuration.GetValue("Cluster:Enabled", false);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=chainloot;Username=chainloot;Password=chainloot";
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<MqttBridge>();
builder.Services.AddSingleton<ChainIndexer>();
builder.Services.AddHttpClient("blockchain", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSignalR();
if (clusterEnabled)
{
    builder.Services.AddSingleton<RaftStore>();
    builder.Services.AddSingleton<RaftNode>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RaftNode>());
    builder.Services.AddHttpClient("cluster-peer", client => client.Timeout = TimeSpan.FromMilliseconds(500));
    builder.Services.AddHttpClient("cluster-forward", client => client.Timeout = TimeSpan.FromSeconds(8));
}
else
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBridge>());
    builder.Services.AddHostedService<IndexWorker>();
}

var app = builder.Build();
if (!clusterEnabled) await app.Services.GetRequiredService<HistoryStore>().InitializeAsync(CancellationToken.None);
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
if (!clusterEnabled)
{
    app.MapHub<HistoryHub>("/hubs/history");
    app.MapGet("/api/events", async (HttpContext context, HistoryStore store, ChainIndexer indexer, ILogger<Program> logger) =>
        await GetEvents(context, store, indexer, logger, null));
    app.MapGet("/api/events/export", async (HttpContext context, HistoryStore store, ChainIndexer indexer, ILogger<Program> logger) =>
    {
        var format = context.Request.Query["format"].ToString();
        return format is "csv" or "json"
            ? await GetEvents(context, store, indexer, logger, format)
            : Results.BadRequest(new { error = "Choose csv or json format." });
    });
}

if (clusterEnabled)
{
    app.MapGet("/cluster/status", async (RaftNode node, CancellationToken ct) => Results.Ok(await node.StatusAsync(ct)));
    app.MapGet("/cluster/assets/{id}", async (string id, RaftNode node, CancellationToken ct) =>
    {
        var asset = await node.AssetAsync(id, ct);
        return asset is null ? Results.NotFound(new { error = "Asset does not exist." }) : Results.Ok(asset);
    });
    app.MapGet("/cluster/history/{id}", async (string id, RaftNode node, CancellationToken ct) =>
        Results.Ok(await node.HistoryAsync(id, ct)));
    app.MapGet("/cluster/nonces/{sender}", async (string sender, RaftNode node, CancellationToken ct) =>
        Results.Ok(new { nonce = await node.NextNonceAsync(sender.ToLowerInvariant(), ct) }));
    app.MapPost("/cluster/operations", async (SignedOperation operation, HttpContext context, RaftNode node, CancellationToken ct) =>
    {
        var result = await node.SubmitAsync(operation, context.Request.Query["forwarded"] == "1", ct);
        return Results.Json(result, statusCode: result.Status == "SUCCESS" ? 200 : result.Status == "REJECTED" ? 409 : 503);
    });
    app.MapPost("/cluster/internal/vote", async (VoteRequest request, RaftNode node, CancellationToken ct) =>
        Results.Ok(await node.VoteAsync(request, ct)));
    app.MapPost("/cluster/internal/append", async (AppendRequest request, RaftNode node, CancellationToken ct) =>
        Results.Ok(await node.AppendAsync(request, ct)));
    app.MapPost("/cluster/test/partition", async (PartitionRequest request, RaftNode node, CancellationToken ct) =>
        await node.SetPartitionAsync(request, ct) ? Results.Ok() : Results.NotFound());
}

app.Run();

static bool TryFilters(IQueryCollection query, out EventFilters? filters, out string error)
{
    filters = null;
    var contract = query["contract"].ToString().Trim().ToLowerInvariant();
    var type = query["type"].ToString();
    var token = query["token"].ToString().Trim();
    var fromText = query["from"].ToString();
    var toText = query["to"].ToString();
    error = "";
    if (!Regex.IsMatch(contract, "^0x[0-9a-f]{40}$")) error = "Enter a valid contract address.";
    else if (type.Length > 0 && type is not ("issue" or "transfer" or "redeem")) error = "Invalid event type.";
    else if (token.Length > 0 && (!Regex.IsMatch(token, "^[0-9]+$") || !BigInteger.TryParse(token, out _)))
        error = "Asset ID must be an integer.";
    if (error.Length > 0) return false;
    if (token.Length > 0) token = BigInteger.Parse(token, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    DateTimeOffset? from = null, to = null;
    if (fromText.Length > 0)
    {
        if (!DateTimeOffset.TryParse(fromText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            error = "Invalid start date.";
        else from = parsed.ToUniversalTime();
    }
    if (toText.Length > 0)
    {
        if (!DateTimeOffset.TryParse(toText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            error = "Invalid end date.";
        else to = parsed.ToUniversalTime();
    }
    if (from.HasValue && to.HasValue && from > to) error = "The start date must be before the end date.";
    var pageText = query["page"].ToString();
    var pageSizeText = query["pageSize"].ToString();
    if (!int.TryParse(pageText.Length == 0 ? "1" : pageText, out var page) || page < 1 ||
        !int.TryParse(pageSizeText.Length == 0 ? "20" : pageSizeText, out var pageSize) || pageSize < 1 || pageSize > 100)
    {
        error = "Invalid page number or page size.";
        return false;
    }
    if (error.Length > 0) return false;
    filters = new(contract, type, token, from, to, page, pageSize);
    return true;
}

static async Task<IResult> GetEvents(HttpContext context, HistoryStore store, ChainIndexer indexer,
    ILogger<Program> logger, string? format)
{
    if (!TryFilters(context.Request.Query, out var filters, out var error)) return Results.BadRequest(new { error });
    try
    {
        await indexer.SyncAsync(filters!.Contract, context.RequestAborted);
        var result = await store.QueryAsync(filters, format is not null, context.RequestAborted);
        if (format == "json") return Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result.Rows,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), "application/json", "chainloot-history.json");
        if (format == "csv")
        {
            static string Cell(object? value)
            {
                const char quote = '"';
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                return quote + text.Replace(quote.ToString(), new string(quote, 2)) + quote;
            }
            var lines = new List<string> { "timestamp,type,tokenId,sender,recipient,operationId,txHash,logIndex,blockNumber" };
            lines.AddRange(result.Rows.Select(row => string.Join(",", Cell(row.Timestamp.ToString("O")), Cell(row.Type),
                Cell(row.TokenId), Cell(row.Sender), Cell(row.Recipient), Cell(row.OperationId), Cell(row.TxHash), Cell(row.LogIndex), Cell(row.BlockNumber))));
            var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines))).ToArray();
            return Results.File(body, "text/csv", "chainloot-history.csv");
        }
        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
    catch (Exception ex)
    {
        logger.LogWarning("Error reading history: {Error}", ex.Message);
        return Results.Json(new { error = "Could not read history. Check Anvil and the contract address." }, statusCode: 503);
    }
}

public partial class Program;
