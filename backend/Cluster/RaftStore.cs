using System.Text.Json;
using Npgsql;

namespace ChainLoot.Backend.Cluster;

public sealed class RaftStore(NpgsqlDataSource source)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PersistentState> LoadAsync(CancellationToken cancellationToken)
    {
        await using var initialize = source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS raft_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                data JSONB NOT NULL
            );
            INSERT INTO raft_state (id, data) VALUES (1, CAST(@initial AS jsonb))
            ON CONFLICT (id) DO NOTHING;
            """);
        initialize.Parameters.AddWithValue("initial", JsonSerializer.Serialize(new PersistentState(), Json));
        await initialize.ExecuteNonQueryAsync(cancellationToken);
        await using var read = source.CreateCommand("SELECT data::text FROM raft_state WHERE id = 1");
        var value = (string)(await read.ExecuteScalarAsync(cancellationToken))!;
        return JsonSerializer.Deserialize<PersistentState>(value, Json)
            ?? throw new InvalidOperationException("Nie można odczytać stanu węzła.");
    }

    public async Task SaveAsync(PersistentState state, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("UPDATE raft_state SET data = CAST(@data AS jsonb) WHERE id = 1");
        command.Parameters.AddWithValue("data", JsonSerializer.Serialize(state, Json));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
