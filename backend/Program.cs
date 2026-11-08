using ChainLoot.Backend.Cluster;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=chainloot;Username=chainloot;Password=chainloot";
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<RaftStore>();
builder.Services.AddSingleton<RaftNode>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RaftNode>());
builder.Services.AddHttpClient("cluster-peer", client => client.Timeout = TimeSpan.FromMilliseconds(500));
builder.Services.AddHttpClient("cluster-forward", client => client.Timeout = TimeSpan.FromSeconds(8));

var app = builder.Build();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
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

app.Run();

public partial class Program;
