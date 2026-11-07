namespace ChainLoot.Backend.Cluster;

public sealed record SignedOperation(
    string OperationId,
    string Type,
    string AssetId,
    string Sender,
    string Recipient,
    long Nonce,
    string PublicKey,
    string Signature);

public sealed record LogEntry(long Term, SignedOperation? Operation);
public sealed class PersistentState
{
    public long Term { get; set; }
    public string? VotedFor { get; set; }
    public long CommitIndex { get; set; }
    public List<LogEntry> Log { get; set; } = [];
}

public sealed record VoteRequest(long Term, string CandidateId, long LastIndex, long LastTerm);
public sealed record VoteResponse(long Term, bool Granted);
public sealed record AppendRequest(long Term, string LeaderId, long PreviousIndex, long PreviousTerm,
    List<LogEntry> Entries, long LeaderCommit);
public sealed record AppendResponse(long Term, bool Success);
public sealed record AssetState(string Id, string Owner, string Status);
public sealed record OperationResult(string Status, string Message, string? OperationId = null);
public sealed record PartitionRequest(string[] BlockedPeers);
