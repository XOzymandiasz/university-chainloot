using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace ChainLoot.Backend.Cluster;

public sealed class RaftNode : BackgroundService
{
    private readonly RaftStore _store;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<RaftNode> _logger;
    private readonly string _id;
    private readonly Dictionary<string, string> _peers;
    private readonly bool _allowTestControls;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _proposalGate = new(1, 1);
    private readonly Dictionary<string, long> _next = new();
    private readonly Dictionary<string, long> _match = new();
    private readonly Dictionary<long, TaskCompletionSource<bool>> _waiters = new();
    private readonly Dictionary<string, AssetState> _assets = new();
    private readonly Dictionary<string, long> _nonces = new();
    private readonly HashSet<string> _seen = new();
    private readonly List<SignedOperation> _history = [];
    private readonly HashSet<string> _blocked = new();
    private PersistentState _state = new();
    private string _role = "follower";
    private string? _leader;
    private DateTimeOffset _deadline;
    private long _leaderBarrier;

    public RaftNode(RaftStore store, IHttpClientFactory clients, IConfiguration configuration, ILogger<RaftNode> logger)
    {
        _store = store;
        _clients = clients;
        _logger = logger;
        _id = configuration["Cluster:NodeId"] ?? throw new InvalidOperationException("Missing node ID.");
        _allowTestControls = configuration.GetValue("Cluster:AllowTestControls", false);
        var entries = (configuration["Cluster:Peers"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var all = entries.Select(entry => entry.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1].TrimEnd('/'));
        if (all.Count != 3 || !all.ContainsKey(_id)) throw new InvalidOperationException("Configuration requires exactly three nodes.");
        foreach (var address in all.Values)
        {
            var uri = new Uri(address);
            if (uri.Scheme != "http" || (uri.Host != "127.0.0.1" && uri.Host != "localhost" &&
                uri.Host is not ("node-a" or "node-b" or "node-c")))
                throw new InvalidOperationException("The node address must point to the local network.");
        }
        _peers = all.Where(pair => pair.Key != _id).ToDictionary(pair => pair.Key, pair => pair.Value);
        ResetDeadline();
    }

    private void ResetDeadline() => _deadline = DateTimeOffset.UtcNow.AddMilliseconds(Random.Shared.Next(1100, 2000));
    private long LastTerm => _state.Log.Count == 0 ? 0 : _state.Log[^1].Term;
    private long TermAt(long index) => index == 0 ? 0 : _state.Log[checked((int)index - 1)].Term;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _state = await _store.LoadAsync(cancellationToken);
        if (_state.CommitIndex < 0 || _state.CommitIndex > _state.Log.Count)
            throw new InvalidOperationException("Invalid stored commit index.");
        ApplyFrom(0, _state.CommitIndex);
        ResetDeadline();
        await base.StartAsync(cancellationToken);
    }

    private void ApplyFrom(long oldCommit, long newCommit)
    {
        for (var i = oldCommit; i < newCommit; i++)
        {
            var op = _state.Log[checked((int)i)].Operation;
            if (op is null) continue;
            if (Validate(op) is not null) continue;
            if (op.Type == "issue") _assets[op.AssetId] = new(op.AssetId, op.Recipient, "ACTIVE");
            if (op.Type == "transfer") _assets[op.AssetId] = new(op.AssetId, op.Recipient, "ACTIVE");
            if (op.Type == "redeem") _assets[op.AssetId] = new(op.AssetId, op.Sender, "REDEEMED");
            _nonces[op.Sender] = op.Nonce;
            _seen.Add(op.OperationId);
            _history.Add(op);
        }
    }

    private async Task StepDownAsync(long term, CancellationToken cancellationToken)
    {
        if (term > _state.Term)
        {
            _state.Term = term;
            _state.VotedFor = null;
            await _store.SaveAsync(_state, cancellationToken);
        }
        _role = "follower";
        _leader = null;
        _leaderBarrier = 0;
        ResetDeadline();
        foreach (var waiter in _waiters.Values) waiter.TrySetResult(false);
        _waiters.Clear();
    }

    public async Task<VoteResponse> VoteAsync(VoteRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_peers.ContainsKey(request.CandidateId) || _blocked.Contains(request.CandidateId)) return new(_state.Term, false);
            if (request.Term < _state.Term) return new(_state.Term, false);
            if (request.Term > _state.Term) await StepDownAsync(request.Term, cancellationToken);
            var upToDate = request.LastTerm > LastTerm ||
                (request.LastTerm == LastTerm && request.LastIndex >= _state.Log.Count);
            var granted = upToDate && (_state.VotedFor is null || _state.VotedFor == request.CandidateId);
            if (granted)
            {
                _state.VotedFor = request.CandidateId;
                ResetDeadline();
                await _store.SaveAsync(_state, cancellationToken);
            }
            return new(_state.Term, granted);
        }
        finally { _gate.Release(); }
    }

    public async Task<AppendResponse> AppendAsync(AppendRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_peers.ContainsKey(request.LeaderId) || _blocked.Contains(request.LeaderId)) return new(_state.Term, false);
            if (request.Term < _state.Term) return new(_state.Term, false);
            if (request.Term > _state.Term || _role != "follower")
                await StepDownAsync(request.Term, cancellationToken);
            _leader = request.LeaderId;
            ResetDeadline();
            if (request.PreviousIndex < 0 || request.PreviousIndex > _state.Log.Count ||
                TermAt(request.PreviousIndex) != request.PreviousTerm)
                return new(_state.Term, false);

            for (var i = 0; i < request.Entries.Count; i++)
            {
                var index = request.PreviousIndex + i + 1;
                if (index <= _state.Log.Count && TermAt(index) != request.Entries[i].Term)
                {
                    if (index <= _state.CommitIndex) return new(_state.Term, false);
                    _state.Log.RemoveRange(checked((int)index - 1), _state.Log.Count - checked((int)index) + 1);
                }
                if (index > _state.Log.Count) _state.Log.Add(request.Entries[i]);
            }
            var oldCommit = _state.CommitIndex;
            _state.CommitIndex = Math.Max(_state.CommitIndex, Math.Min(request.LeaderCommit, _state.Log.Count));
            await _store.SaveAsync(_state, cancellationToken);
            ApplyFrom(oldCommit, _state.CommitIndex);
            return new(_state.Term, true);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(180));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                string role;
                bool election;
                await _gate.WaitAsync(stoppingToken);
                try { role = _role; election = role != "leader" && DateTimeOffset.UtcNow >= _deadline; }
                finally { _gate.Release(); }
                if (election) await ElectAsync(stoppingToken);
                else if (role == "leader") await ReplicateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning("Node {Id}: {Error}", _id, ex.Message); }
        }
    }

    private async Task<T?> PostPeerAsync<T>(string peer, string path, object body, CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(peer, out var address)) return default;
        try
        {
            using var response = await _clients.CreateClient("cluster-peer")
                .PostAsJsonAsync(address + path, body, cancellationToken);
            if (!response.IsSuccessStatusCode) return default;
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return default; }
    }

    private async Task ElectAsync(CancellationToken cancellationToken)
    {
        VoteRequest request;
        string[] peers;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_role == "leader" || DateTimeOffset.UtcNow < _deadline) return;
            _role = "candidate";
            _leader = null;
            _state.Term++;
            _state.VotedFor = _id;
            ResetDeadline();
            await _store.SaveAsync(_state, cancellationToken);
            request = new(_state.Term, _id, _state.Log.Count, LastTerm);
            peers = _peers.Keys.Where(peer => !_blocked.Contains(peer)).ToArray();
        }
        finally { _gate.Release(); }

        var responses = await Task.WhenAll(peers.Select(peer => PostPeerAsync<VoteResponse>(peer, "/cluster/internal/vote", request, cancellationToken)));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var higherTerm = responses.Where(response => response is not null).Select(response => response!.Term).DefaultIfEmpty(0).Max();
            if (higherTerm > _state.Term) { await StepDownAsync(higherTerm, cancellationToken); return; }
            if (_role != "candidate" || _state.Term != request.Term) return;
            if (1 + responses.Count(response => response?.Granted == true) < 2) return;
            _role = "leader";
            _leader = _id;
            _next.Clear();
            _match.Clear();
            foreach (var peer in _peers.Keys)
            {
                _next[peer] = _state.Log.Count + 1;
                _match[peer] = 0;
            }
            _state.Log.Add(new(_state.Term, null));
            _leaderBarrier = _state.Log.Count;
            await _store.SaveAsync(_state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task ReplicateAsync(CancellationToken cancellationToken)
    {
        string[] peers;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_role != "leader") return;
            peers = _peers.Keys.Where(peer => !_blocked.Contains(peer)).ToArray();
        }
        finally { _gate.Release(); }
        await Task.WhenAll(peers.Select(peer => ReplicatePeerAsync(peer, cancellationToken)));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_role != "leader") return;
            for (var index = (long)_state.Log.Count; index > _state.CommitIndex; index--)
            {
                if (TermAt(index) != _state.Term) continue;
                if (1 + _match.Values.Count(value => value >= index) < 2) continue;
                var old = _state.CommitIndex;
                _state.CommitIndex = index;
                await _store.SaveAsync(_state, cancellationToken);
                ApplyFrom(old, index);
                foreach (var waiter in _waiters.Where(pair => pair.Key <= index).ToArray())
                {
                    waiter.Value.TrySetResult(true);
                    _waiters.Remove(waiter.Key);
                }
                break;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task ReplicatePeerAsync(string peer, CancellationToken cancellationToken)
    {
        AppendRequest request;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_role != "leader" || _blocked.Contains(peer)) return;
            var next = _next[peer];
            var previous = next - 1;
            request = new(_state.Term, _id, previous, TermAt(previous),
                _state.Log.Skip(checked((int)previous)).ToList(), _state.CommitIndex);
        }
        finally { _gate.Release(); }

        var response = await PostPeerAsync<AppendResponse>(peer, "/cluster/internal/append", request, cancellationToken);
        if (response is null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (response.Term > _state.Term) { await StepDownAsync(response.Term, cancellationToken); return; }
            if (_role != "leader" || request.Term != _state.Term) return;
            if (response.Success)
            {
                _match[peer] = request.PreviousIndex + request.Entries.Count;
                _next[peer] = _match[peer] + 1;
            }
            else _next[peer] = Math.Max(1, _next[peer] - 1);
        }
        finally { _gate.Release(); }
    }

    private string? Validate(SignedOperation op)
    {
        if (!OperationVerifier.Verify(op)) return "Invalid signature or operation data.";
        if (_seen.Contains(op.OperationId)) return "This operation has already been executed.";
        if (op.Nonce != _nonces.GetValueOrDefault(op.Sender) + 1) return "Invalid operation nonce.";
        if (op.Type == "issue")
            return _assets.ContainsKey(op.AssetId) ? "Asset already exists." : null;
        if (!_assets.TryGetValue(op.AssetId, out var asset)) return "Asset does not exist.";
        if (asset.Owner != op.Sender) return "Only the owner can perform this operation.";
        if (asset.Status != "ACTIVE") return "Asset has already been redeemed.";
        if (op.Type == "transfer" && op.Recipient == op.Sender) return "The recipient must be a different user.";
        return null;
    }

    public async Task<OperationResult> SubmitAsync(SignedOperation op, bool forwarded, CancellationToken cancellationToken)
    {
        string? forwardTo = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_role != "leader")
            {
                if (!forwarded && _leader is not null && !_blocked.Contains(_leader)) forwardTo = _leader;
                else return new("PENDING", "No leader is available. Try again.", op.OperationId);
            }
        }
        finally { _gate.Release(); }

        if (forwardTo is not null)
        {
            try
            {
                using var response = await _clients.CreateClient("cluster-forward")
                    .PostAsJsonAsync(_peers[forwardTo] + "/cluster/operations?forwarded=1", op, cancellationToken);
                return await response.Content.ReadFromJsonAsync<OperationResult>(cancellationToken)
                    ?? new("PENDING", "No operation result was received.", op.OperationId);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new("PENDING", "Could not contact the leader; check the state before retrying.", op.OperationId);
            }
        }

        await _proposalGate.WaitAsync(cancellationToken);
        try
        {
            TaskCompletionSource<bool> completion;
            long index;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_role != "leader" || _leaderBarrier == 0 || _state.CommitIndex < _leaderBarrier)
                    return new("PENDING", "The node has not confirmed leadership yet.", op.OperationId);
                var error = Validate(op);
                if (error is not null) return new("REJECTED", error, op.OperationId);
                _state.Log.Add(new(_state.Term, op));
                index = _state.Log.Count;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[index] = completion;
                await _store.SaveAsync(_state, cancellationToken);
            }
            finally { _gate.Release(); }

            bool committed;
            try { committed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (TimeoutException) { committed = false; }
            finally
            {
                await _gate.WaitAsync(CancellationToken.None);
                try { _waiters.Remove(index); }
                finally { _gate.Release(); }
            }
            return committed
                ? new("SUCCESS", "Operation committed by a majority of nodes.", op.OperationId)
                : new("PENDING", "No majority confirmation. Check the state before retrying.", op.OperationId);
        }
        finally { _proposalGate.Release(); }
    }

    public async Task<AssetState?> AssetAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _assets.GetValueOrDefault(id); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SignedOperation>> HistoryAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _history.Where(item => item.AssetId == id).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<long> NextNonceAsync(string sender, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _nonces.GetValueOrDefault(sender) + 1; }
        finally { _gate.Release(); }
    }

    public async Task<object> StatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new
            {
                id = _id,
                role = _role,
                term = _state.Term,
                leader = _leader,
                commitIndex = _state.CommitIndex,
                lastIndex = _state.Log.Count,
                ready = _role == "leader" && _leaderBarrier > 0 && _state.CommitIndex >= _leaderBarrier
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetPartitionAsync(PartitionRequest request, CancellationToken cancellationToken)
    {
        if (!_allowTestControls || request.BlockedPeers is null || request.BlockedPeers.Any(peer => !_peers.ContainsKey(peer))) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _blocked.Clear();
            foreach (var peer in request.BlockedPeers) _blocked.Add(peer);
            return true;
        }
        finally { _gate.Release(); }
    }
}
