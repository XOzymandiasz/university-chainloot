namespace ChainLoot.Backend.Models;

public record ItemEvent(
    string Contract,
    string TxHash,
    int LogIndex,
    long BlockNumber,
    DateTimeOffset Timestamp,
    string Type,
    string TokenId,
    string Sender,
    string Recipient)
{
    public string OperationId => TxHash + ":" + LogIndex;
}

public record Checkpoint(long BlockNumber, string BlockHash);
public record EventFilters(string Contract, string Type, string Token, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize);
public record EventSummary(string Type, long Count);
public record DailySummary(string Day, long Count);
public record HistoryResult(IReadOnlyList<ItemEvent> Rows, long Total, IReadOnlyList<EventSummary> Summary,
    IReadOnlyList<DailySummary> Daily, int Page, int PageSize);
