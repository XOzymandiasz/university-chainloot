using ChainLoot.Backend.Models;
using Npgsql;

namespace ChainLoot.Backend.Services;

public sealed class HistoryStore(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS item_events (
              contract TEXT NOT NULL, tx_hash TEXT NOT NULL, log_index INTEGER NOT NULL,
              block_number BIGINT NOT NULL, timestamp TIMESTAMPTZ NOT NULL,
              type TEXT NOT NULL, token_id TEXT NOT NULL, sender TEXT NOT NULL, recipient TEXT NOT NULL,
              PRIMARY KEY (contract, tx_hash, log_index)
            );
            CREATE INDEX IF NOT EXISTS item_events_contract_block
              ON item_events(contract, block_number DESC, log_index DESC);
            CREATE TABLE IF NOT EXISTS checkpoints (
              contract TEXT PRIMARY KEY, block_number BIGINT NOT NULL, block_hash TEXT NOT NULL
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Checkpoint?> GetCheckpointAsync(string contract, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT block_number, block_hash FROM checkpoints WHERE contract = @contract");
        command.Parameters.AddWithValue("contract", contract);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetInt64(0), reader.GetString(1)) : null;
    }

    public async Task ResetAsync(string contract, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM item_events WHERE contract = @contract; DELETE FROM checkpoints WHERE contract = @contract;",
            connection, transaction);
        command.Parameters.AddWithValue("contract", contract);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> SaveBatchAsync(string contract, IEnumerable<ItemEvent> events, Checkpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = 0;
        foreach (var item in events)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO item_events
                  (contract, tx_hash, log_index, block_number, timestamp, type, token_id, sender, recipient)
                VALUES (@contract, @tx, @log, @block, @time, @type, @token, @sender, @recipient)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue("contract", contract);
            command.Parameters.AddWithValue("tx", item.TxHash);
            command.Parameters.AddWithValue("log", item.LogIndex);
            command.Parameters.AddWithValue("block", item.BlockNumber);
            command.Parameters.AddWithValue("time", item.Timestamp);
            command.Parameters.AddWithValue("type", item.Type);
            command.Parameters.AddWithValue("token", item.TokenId);
            command.Parameters.AddWithValue("sender", item.Sender);
            command.Parameters.AddWithValue("recipient", item.Recipient);
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var checkpointCommand = new NpgsqlCommand("""
            INSERT INTO checkpoints (contract, block_number, block_hash) VALUES (@contract, @block, @hash)
            ON CONFLICT(contract) DO UPDATE SET block_number = EXCLUDED.block_number, block_hash = EXCLUDED.block_hash
            """, connection, transaction);
        checkpointCommand.Parameters.AddWithValue("contract", contract);
        checkpointCommand.Parameters.AddWithValue("block", checkpoint.BlockNumber);
        checkpointCommand.Parameters.AddWithValue("hash", checkpoint.BlockHash);
        await checkpointCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    private static string FilterSql(EventFilters filters)
    {
        var clauses = new List<string> { "contract = @contract" };
        if (filters.Type.Length > 0) clauses.Add("type = @type");
        if (filters.Token.Length > 0) clauses.Add("token_id = @token");
        if (filters.From.HasValue) clauses.Add("timestamp >= @from");
        if (filters.To.HasValue) clauses.Add("timestamp <= @to");
        return string.Join(" AND ", clauses);
    }

    private static void AddFilters(NpgsqlCommand command, EventFilters filters)
    {
        command.Parameters.AddWithValue("contract", filters.Contract);
        if (filters.Type.Length > 0) command.Parameters.AddWithValue("type", filters.Type);
        if (filters.Token.Length > 0) command.Parameters.AddWithValue("token", filters.Token);
        if (filters.From.HasValue) command.Parameters.AddWithValue("from", filters.From.Value.ToUniversalTime());
        if (filters.To.HasValue) command.Parameters.AddWithValue("to", filters.To.Value.ToUniversalTime());
    }

    public async Task<HistoryResult> QueryAsync(EventFilters filters, bool all, CancellationToken cancellationToken)
    {
        var where = FilterSql(filters);
        var rows = new List<ItemEvent>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand($"""
            SELECT contract, tx_hash, log_index, block_number, timestamp, type, token_id, sender, recipient
            FROM item_events WHERE {where} ORDER BY block_number ASC, log_index ASC
            {(all ? "" : "LIMIT @limit OFFSET @offset")}
            """, connection))
        {
            AddFilters(command, filters);
            if (!all)
            {
                command.Parameters.AddWithValue("limit", filters.PageSize);
                command.Parameters.AddWithValue("offset", ((long)filters.Page - 1) * filters.PageSize);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new ItemEvent(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(5) == "redeem" ? "" : reader.GetString(8)));
        }

        long total;
        await using (var command = new NpgsqlCommand($"SELECT COUNT(*) FROM item_events WHERE {where}", connection))
        {
            AddFilters(command, filters);
            total = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        var summary = new List<EventSummary>();
        await using (var command = new NpgsqlCommand($"SELECT type, COUNT(*) FROM item_events WHERE {where} GROUP BY type", connection))
        {
            AddFilters(command, filters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) summary.Add(new(reader.GetString(0), reader.GetInt64(1)));
        }

        var daily = new List<DailySummary>();
        await using (var command = new NpgsqlCommand($"""
            SELECT to_char(timestamp AT TIME ZONE 'UTC', 'YYYY-MM-DD') AS day, COUNT(*)
            FROM item_events WHERE {where} GROUP BY day ORDER BY day DESC LIMIT 14
            """, connection))
        {
            AddFilters(command, filters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) daily.Add(new(reader.GetString(0), reader.GetInt64(1)));
        }
        daily.Reverse();
        return new(rows, total, summary, daily, filters.Page, filters.PageSize);
    }
}
