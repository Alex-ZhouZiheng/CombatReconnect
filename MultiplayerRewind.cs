using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CombatReconnect;

internal static class MultiplayerRewindCoordinator
{
    private const int VoteTimeoutSeconds = 20;
    private const int BoundaryTimeoutSeconds = 15;
    private const int TransactionTimeoutSeconds = 180;
    private const int EventsPerBatch = 32;

    private static HostVote? _hostVote;
    private static HostTransaction? _hostTransaction;
    private static string? _clientVoteId;
    private static bool _clientVoteResponded;
    private static string? _clientTransactionId;
    private static int _clientGeneration;
    private static RewindSafetySnapshot? _clientSafety;
    private static RewindPrepareMessage? _clientPrepare;
    private static byte[]? _clientEncodedAnchor;
    private static readonly SortedDictionary<int, DeterministicInputEvent> ClientEvents = new();
    private static bool _clientLastEventBatch;
    private static bool _clientRestoring;
    private static bool _clientReplayStarted;
    private static bool _clientCommitPrepared;
    private static ulong? _clientLocalNetId;
    private static CancellationTokenSource? _clientCancellation;
    private static readonly List<TurnHistoryMetadata> RemoteHistory = new();
    private static bool _sidecarSubscribed;

    internal static void Register(INetGameService service)
    {
        if (!_sidecarSubscribed)
        {
            SidecarReplayTransport.AnchorAvailable += OnSidecarAnchorAvailable;
            _sidecarSubscribed = true;
        }
        if (service.Type == NetGameType.Client)
            SidecarReplayTransport.PrepareClient(service);
        else
        {
            SidecarReplayTransport.Initialize();
            STS2RitsuLib.Networking.Sidecar.RitsuLibSidecarSessionManager.ObserveNetService(service);
        }
        service.RegisterMessageHandler<RewindRequestMessage>(OnRequest);
        service.RegisterMessageHandler<RewindVoteStartMessage>(OnVoteStart);
        service.RegisterMessageHandler<RewindVoteResponseMessage>(OnVoteResponse);
        service.RegisterMessageHandler<RewindVoteResultMessage>(OnVoteResult);
        service.RegisterMessageHandler<RewindFreezeMessage>(OnFreeze);
        service.RegisterMessageHandler<RewindFreezeAckMessage>(OnFreezeAck);
        service.RegisterMessageHandler<RewindDrainMarkerMessage>(OnDrainMarker);
        service.RegisterMessageHandler<RewindBoundaryReadyMessage>(OnBoundaryReady);
        service.RegisterMessageHandler<RewindPrepareMessage>(OnPrepare);
        service.RegisterMessageHandler<RewindEventBatchMessage>(OnEvents);
        service.RegisterMessageHandler<RewindReadyMessage>(OnReady);
        service.RegisterMessageHandler<RewindCommitMessage>(OnCommit);
        service.RegisterMessageHandler<RewindCommitAckMessage>(OnCommitAck);
        service.RegisterMessageHandler<RewindCommitCompleteMessage>(OnCommitComplete);
        service.RegisterMessageHandler<RewindAbortMessage>(OnAbort);
        service.RegisterMessageHandler<RewindAbortRecoveredMessage>(OnAbortRecovered);
        service.RegisterMessageHandler<RewindAbortCompleteMessage>(OnAbortComplete);
        service.RegisterMessageHandler<TurnHistorySyncMessage>(OnHistorySync);
    }

    internal static void RequestCurrentTurn() => RequestTurn(0);

    internal static void RequestTurn(ulong epoch)
    {
        INetGameService service = RunManager.Instance.NetService;
        if (HasActiveTransaction())
        {
            RewindUiController.ShowTransactionStatus("正在处理回滚请求，请稍候……");
            return;
        }
        if (ReconnectCoordinator.HasPendingReconnect)
        {
            ReconnectLog.Write("Rewind request ignored: a player is disconnected or reconnecting.");
            return;
        }

        if (service.Type == NetGameType.Client)
        {
            service.SendMessage(new RewindRequestMessage
            {
                protocolVersion = CombatReconnectMod.ProtocolVersion,
                reason = epoch == 0 ? "current-turn" : $"turn:{epoch}"
            });
            RewindUiController.ShowTransactionStatus("已请求回滚，等待其他玩家投票……");
            return;
        }

        ulong requester = LocalContext.NetId ?? throw new InvalidOperationException("Host net id is unavailable.");
        StartVote(epoch, requester);
    }

    private static bool HasActiveTransaction()
        => _hostVote != null || _hostTransaction != null || _clientVoteId != null || _clientTransactionId != null;

    internal static void OnPlayerDisconnected(ulong playerId)
    {
        if (_hostVote is { } vote && vote.Participants.Contains(playerId))
        {
            FinishVote(vote, false, $"玩家 {playerId} 已断线，回滚投票取消。");
            return;
        }
        if (_hostTransaction is { } transaction && transaction.Participants.Contains(playerId))
        {
            transaction.Participants.Remove(playerId);
            transaction.FreezeAcks.Remove(playerId);
            transaction.Boundaries.Remove(playerId);
            transaction.ReadyClients.Remove(playerId);
            transaction.CommitAcks.Remove(playerId);
            transaction.Recoveries.Remove(playerId);
            ReconnectLog.Write(
                $"Removed disconnected player {playerId} from rewind transaction {transaction.Id}; " +
                $"remaining=[{string.Join(",", transaction.Participants)}].");
            if (transaction.Aborting)
                TryCompleteAbort(transaction);
            else
                AbortHost(transaction, $"玩家 {playerId} 已断线，回滚事务取消。");
        }
    }

    internal static async Task AwaitNoActiveHostOperation(CancellationToken cancellationToken)
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        while (_hostVote != null || _hostTransaction != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void OnRequest(RewindRequestMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;
        if (message.protocolVersion != CombatReconnectMod.ProtocolVersion)
        {
            ReconnectLog.Write($"Ignored rewind request from {senderId}: protocol {message.protocolVersion} is incompatible.");
            return;
        }
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        if (run?.GetPlayer(senderId) == null || HasActiveTransaction()) return;
        ulong epoch = 0;
        if (message.reason.StartsWith("turn:") && !ulong.TryParse(message.reason[5..], out epoch)) return;
        StartVote(epoch, senderId);
    }

    private static void StartVote(ulong requestedEpoch, ulong requesterId)
    {
        try
        {
            if (!CombatManager.Instance.IsInProgress || ReconnectCoordinator.HasPendingReconnect)
                throw new InvalidOperationException("当前不能开始回滚投票。");
            TurnReplayAnchor target = (requestedEpoch == 0 ? TurnReplayLedger.Anchor : TurnReplayLedger.FindAnchor(requestedEpoch))
                ?? throw new InvalidOperationException("没有可用的玩家回合锚点。");
            RunState run = RunManager.Instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("Run state is unavailable.");
            List<ulong> participants = run.Players.Select(player => player.NetId).ToList();
            if (participants.Count < 2 || !participants.Contains(requesterId))
                throw new InvalidOperationException("回滚投票要求至少两名在线玩家。");

            var vote = new HostVote(Guid.NewGuid().ToString("N"), requesterId, target, participants);
            vote.Approvals.Add(requesterId);
            _hostVote = vote;
            RunManager.Instance.NetService.SendMessage(new RewindVoteStartMessage
            {
                voteId = vote.Id,
                requesterId = requesterId,
                epoch = target.Epoch,
                roundNumber = target.RoundNumber,
                timeoutSeconds = VoteTimeoutSeconds,
                participants = participants
            });
            ShowVoteLocally(vote.Id, requesterId, target.RoundNumber, VoteTimeoutSeconds);
            _ = VoteTimeout(vote);
            TryFinishVote(vote);
        }
        catch (Exception ex)
        {
            RewindUiController.HideTransactionStatus();
            ReconnectLog.Write($"Could not start rewind vote: {ex.Message}");
        }
    }

    private static void OnVoteStart(RewindVoteStartMessage message, ulong _)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientVoteId != null ||
            !message.participants.Contains(LocalContext.NetId ?? ulong.MaxValue)) return;
        _clientVoteId = message.voteId;
        _clientVoteResponded = false;
        ShowVoteLocally(message.voteId, message.requesterId, message.roundNumber, message.timeoutSeconds);
    }

    private static void ShowVoteLocally(string voteId, ulong requesterId, int roundNumber, int timeoutSeconds)
    {
        if (LocalContext.NetId == requesterId)
        {
            RewindUiController.ShowTransactionStatus("已请求回滚，等待其他玩家投票……");
            return;
        }
        RewindUiController.ShowVoteRequest(roundNumber, timeoutSeconds, approved => SubmitVote(voteId, approved));
    }

    private static void SubmitVote(string voteId, bool approved)
    {
        if (RunManager.Instance.NetService.Type == NetGameType.Host)
        {
            ProcessVote(voteId, LocalContext.NetId ?? ulong.MaxValue, approved);
            return;
        }
        if (_clientVoteId != voteId || _clientVoteResponded) return;
        _clientVoteResponded = true;
        RunManager.Instance.NetService.SendMessage(new RewindVoteResponseMessage { voteId = voteId, approved = approved });
    }

    private static void OnVoteResponse(RewindVoteResponseMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type == NetGameType.Host) ProcessVote(message.voteId, senderId, message.approved);
    }

    private static void ProcessVote(string voteId, ulong senderId, bool approved)
    {
        HostVote? vote = _hostVote;
        if (vote == null || vote.Id != voteId || !vote.Participants.Contains(senderId) || vote.Responded.Contains(senderId)) return;
        vote.Responded.Add(senderId);
        if (!approved)
        {
            FinishVote(vote, false, $"玩家 {senderId} 拒绝了回滚。");
            return;
        }
        vote.Approvals.Add(senderId);
        TryFinishVote(vote);
    }

    private static void TryFinishVote(HostVote vote)
    {
        if (_hostVote == vote && vote.Approvals.Count == vote.Participants.Count)
            FinishVote(vote, true, "所有玩家已同意。");
    }

    private static void FinishVote(HostVote vote, bool approved, string reason)
    {
        if (_hostVote != vote) return;
        _hostVote = null;
        vote.Cancellation.Cancel();
        RunManager.Instance.NetService.SendMessage(new RewindVoteResultMessage
        {
            voteId = vote.Id, approved = approved, reason = reason
        });
        if (!approved)
        {
            RewindUiController.HideTransactionStatus();
            ReconnectLog.Write($"Rewind vote cancelled: {reason}");
            return;
        }
        RewindUiController.ShowTransactionStatus("投票通过，正在冻结战斗输入……");
        BeginFreeze(vote);
    }

    private static async Task VoteTimeout(HostVote vote)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(VoteTimeoutSeconds), vote.Cancellation.Token);
            FinishVote(vote, false, "投票超时，已继续游戏。");
        }
        catch (OperationCanceledException) { }
    }

    private static void OnVoteResult(RewindVoteResultMessage message, ulong _)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientVoteId != message.voteId) return;
        _clientVoteId = null;
        _clientVoteResponded = false;
        if (message.approved) RewindUiController.ShowTransactionStatus("投票通过，正在等待主机冻结战斗……");
        else
        {
            RewindUiController.HideTransactionStatus();
            ReconnectLog.Write($"Rewind vote cancelled: {message.reason}");
        }
    }

    private static void BeginFreeze(HostVote vote)
    {
        try
        {
            RewindReplayPlan plan = TurnReplayLedger.BuildRewindPlan(vote.Target);
            var transaction = new HostTransaction(Guid.NewGuid().ToString("N"), plan, vote.Participants,
                ++HostTransaction.NextGeneration);
            _hostTransaction = transaction;
            transaction.FreezeAcks.Add(LocalContext.NetId ?? throw new InvalidOperationException("Host net id is unavailable."));
            RewindTransactionGate.LockInput("正在等待所有玩家进入安全边界……");
            RunManager.Instance.NetService.SendMessage(new RewindFreezeMessage
            {
                transactionId = transaction.Id, generation = transaction.Generation
            });
            _ = TransactionTimeout(transaction);
            TrySendDrainMarker(transaction);
        }
        catch (Exception ex)
        {
            RewindTransactionGate.Unlock();
            ReconnectLog.Write($"Could not freeze rewind: {ex}");
        }
    }

    private static void OnFreeze(RewindFreezeMessage message, ulong _)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientTransactionId != null ||
            ReconnectCoordinator.HasPendingReconnect) return;
        _clientTransactionId = message.transactionId;
        _clientGeneration = message.generation;
        _clientSafety = null;
        _clientReplayStarted = false;
        _clientCommitPrepared = false;
        _clientLocalNetId = LocalContext.NetId;
        _clientCancellation = new CancellationTokenSource();
        RewindTransactionGate.LockInput("正在等待所有玩家进入安全边界……");
        RunManager.Instance.NetService.SendMessage(new RewindFreezeAckMessage
        {
            transactionId = message.transactionId, generation = message.generation
        });
    }

    private static void OnFreezeAck(RewindFreezeAckMessage message, ulong senderId)
    {
        HostTransaction? transaction = _hostTransaction;
        if (RunManager.Instance.NetService.Type != NetGameType.Host || transaction == null ||
            transaction.Id != message.transactionId || transaction.Generation != message.generation ||
            !transaction.Participants.Contains(senderId)) return;
        transaction.FreezeAcks.Add(senderId);
        TrySendDrainMarker(transaction);
    }

    private static void TrySendDrainMarker(HostTransaction transaction)
    {
        if (_hostTransaction != transaction || transaction.DrainSent ||
            transaction.FreezeAcks.Count != transaction.Participants.Count) return;
        transaction.DrainSent = true;
        RunManager.Instance.NetService.SendMessage(new RewindDrainMarkerMessage
        {
            transactionId = transaction.Id, generation = transaction.Generation
        });
        _ = CaptureHostBoundary(transaction);
    }

    private static void OnDrainMarker(RewindDrainMarkerMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientTransactionId != message.transactionId ||
            _clientGeneration != message.generation) return;
        _ = CaptureClientBoundary(message.transactionId, message.generation);
    }

    private static async Task CaptureHostBoundary(HostTransaction transaction)
    {
        try
        {
            using var boundaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(transaction.Cancellation.Token);
            boundaryCancellation.CancelAfter(TimeSpan.FromSeconds(BoundaryTimeoutSeconds));
            await RewindSafety.AwaitStableBoundary("host pre-rewind boundary", boundaryCancellation.Token);
            transaction.Safety = RewindSafety.Capture();
            string detail = BoundaryFingerprint(transaction.Safety);
            transaction.Boundaries[LocalContext.NetId!.Value] = new BoundaryResult(true, transaction.Safety.Hash, detail);
            ReconnectLog.Write(
                $"Host rewind boundary ready: transaction={transaction.Id}, generation={transaction.Generation}, " +
                $"hash={transaction.Safety.Hash}, {detail}.");
            RewindTransactionGate.EnsureLocked();
            TryBeginReplay(transaction);
        }
        catch (Exception ex)
        {
            transaction.Boundaries[LocalContext.NetId ?? ulong.MaxValue] = new BoundaryResult(false, string.Empty, ex.Message);
            AbortHost(transaction, $"主机无法进入安全边界：{ex.Message}");
        }
    }

    private static async Task CaptureClientBoundary(string transactionId, int generation)
    {
        bool success = false;
        string hash = string.Empty;
        string detail = string.Empty;
        try
        {
            using var boundaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _clientCancellation?.Token ?? default);
            boundaryCancellation.CancelAfter(TimeSpan.FromSeconds(BoundaryTimeoutSeconds));
            await RewindSafety.AwaitStableBoundary("client pre-rewind boundary", boundaryCancellation.Token);
            _clientSafety = RewindSafety.Capture();
            hash = _clientSafety.Hash;
            detail = BoundaryFingerprint(_clientSafety);
            success = true;
            RewindTransactionGate.EnsureLocked();
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            ReconnectLog.Write(
                $"Client rewind boundary failed: transaction={transactionId}, generation={generation}: {ex}");
        }
        RunManager.Instance.NetService.SendMessage(new RewindBoundaryReadyMessage
        {
            transactionId = transactionId, generation = generation, success = success, safetyHash = hash, detail = detail
        });
    }

    private static void OnBoundaryReady(RewindBoundaryReadyMessage message, ulong senderId)
    {
        HostTransaction? transaction = _hostTransaction;
        if (RunManager.Instance.NetService.Type != NetGameType.Host || transaction == null ||
            transaction.Id != message.transactionId || transaction.Generation != message.generation ||
            !transaction.Participants.Contains(senderId)) return;
        transaction.Boundaries[senderId] = new BoundaryResult(message.success, message.safetyHash, message.detail);
        ReconnectLog.Write(
            $"Remote rewind boundary ready: transaction={transaction.Id}, generation={transaction.Generation}, " +
            $"player={senderId}, success={message.success}, hash={message.safetyHash}, detail={message.detail}.");
        TryBeginReplay(transaction);
    }

    private static void TryBeginReplay(HostTransaction transaction)
    {
        if (_hostTransaction != transaction || transaction.ReplayStarted ||
            transaction.Boundaries.Count != transaction.Participants.Count || transaction.Safety == null) return;
        BoundaryResult? failed = transaction.Boundaries.Values.FirstOrDefault(result => !result.Success);
        if (failed != null)
        {
            AbortHost(transaction, $"玩家无法进入安全边界：{failed.Detail}");
            return;
        }
        if (transaction.Boundaries.Values.Any(result => result.Hash != transaction.Safety.Hash))
        {
            foreach ((ulong playerId, BoundaryResult result) in transaction.Boundaries)
                ReconnectLog.Write(
                    $"Rewind boundary mismatch: player={playerId}, success={result.Success}, " +
                    $"hash={result.Hash}, expected={transaction.Safety.Hash}, detail={result.Detail}.");
            AbortHost(transaction, "回滚前各端状态 hash 已不一致，未修改当前战斗。");
            return;
        }

        try
        {
            MonsterMoveStateSupport.ValidateSnapshot(transaction.Plan.Root.ExtendedState, "replay root anchor");
            MonsterMoveStateSupport.ValidateSnapshot(transaction.Anchor.ExtendedState, "rewind target anchor");
        }
        catch (Exception ex)
        {
            AbortHost(transaction, $"回滚锚点包含无法安全恢复的怪物状态：{ex.Message}");
            return;
        }

        transaction.ReplayStarted = true;
        RewindUiController.ShowTransactionStatus("正在加速重放牌序与玩家选择……");
        RewindTransactionGate.BeginIsolation();
        RewindSafety.ClearTransientState();
        _ = BeginReplayAfterTransfer(transaction);
    }

    private static async Task BeginReplayAfterTransfer(HostTransaction transaction)
    {
        try
        {
            await BroadcastPrepare(transaction);
            await ReplayHost(transaction);
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Host rewind anchor transfer failed: {ex}");
            AbortHost(transaction, $"回滚锚点传输失败：{ex.Message}");
        }
    }

    private static async Task BroadcastPrepare(HostTransaction transaction)
    {
        TurnReplayAnchor root = transaction.Plan.Root;
        TurnReplayAnchor target = transaction.Anchor;
        List<ulong> remoteParticipants = transaction.Participants
            .Where(participant => participant != LocalContext.NetId)
            .ToList();
        foreach (ulong participant in remoteParticipants)
            SidecarReplayTransport.PrepareHostPeer(RunManager.Instance.NetService, participant);
        EncodedReplayAnchor encoded = ReplayAnchorCompressionCodec.Encode(root.Payload, remoteParticipants);
        string transferId = $"rewind:{transaction.Id}:{transaction.Generation}:{Guid.NewGuid():N}";
        RunManager.Instance.NetService.SendMessage(new RewindPrepareMessage
        {
            protocolVersion = CombatReconnectMod.ProtocolVersion, transactionId = transaction.Id,
            epoch = target.Epoch, replayStartIndex = target.ReplayEventIndex,
            baseReplayStartIndex = root.ReplayEventIndex, eventCount = transaction.Plan.Events.Count,
            anchorBytes = root.Payload.Length, encodedAnchorBytes = encoded.Payload.Length,
            anchorTransferId = transferId,
            anchorHash = root.PayloadHash, encodedAnchorHash = encoded.Hash,
            anchorCompression = encoded.Compression, finalStateHash = target.PayloadHash,
            finalActionId = target.ExtendedState.nextActionId, finalHookId = target.ExtendedState.nextHookId,
            finalChecksumId = target.ExtendedState.nextChecksumId,
            finalRoundNumber = target.RoundNumber, finalSide = target.ExtendedState.currentSide,
            generation = transaction.Generation,
            participants = transaction.Participants,
            finalPlayerTurnNumbers = transaction.Participants.Select(id =>
                target.CombatState.Players.First(player => player.playerId == id).turnNumber).ToList()
        });
        var key = new SidecarAnchorKey(SidecarAnchorKind.Rewind, transferId, transaction.Id,
            target.Epoch, transaction.Generation);
        Task[] transfers = remoteParticipants.Select(participant => SidecarReplayTransport.SendAnchorAsync(
            RunManager.Instance.NetService, participant, key, encoded.Payload, encoded.Hash,
            transaction.Cancellation.Token)).ToArray();
        ReconnectLog.Write(
            $"Rewind anchor transfer transaction={transaction.Id}, transfer={transferId}, raw={root.Payload.Length}, " +
            $"encoded={encoded.Payload.Length}, compression={encoded.Compression}, peers={remoteParticipants.Count}.");
        if (transaction.Plan.Events.Count == 0)
            RunManager.Instance.NetService.SendMessage(new RewindEventBatchMessage
            {
                transactionId = transaction.Id, epoch = target.Epoch, firstSequence = 0, isLast = true, events = new()
            });
        for (int index = 0; index < transaction.Plan.Events.Count; index += EventsPerBatch)
        {
            List<DeterministicInputEvent> events = transaction.Plan.Events.Skip(index).Take(EventsPerBatch).ToList();
            RunManager.Instance.NetService.SendMessage(new RewindEventBatchMessage
            {
                transactionId = transaction.Id, epoch = target.Epoch, firstSequence = index,
                isLast = index + events.Count == transaction.Plan.Events.Count, events = events
            });
        }
        await Task.WhenAll(transfers).WaitAsync(TimeSpan.FromSeconds(20), transaction.Cancellation.Token);
    }

    private static async Task ReplayHost(HostTransaction transaction)
    {
        try
        {
            string hash = await DeterministicReplayEngine.RestoreAndReplay(
                transaction.Plan.Root.CombatState, transaction.Plan.Root.ExtendedState, transaction.Plan.Events,
                transaction.Anchor.ExtendedState.nextActionId, transaction.Anchor.ExtendedState.nextHookId,
                transaction.Anchor.ExtendedState.nextChecksumId, true, transaction.Cancellation.Token,
                transaction.Anchor.RoundNumber, transaction.Anchor.ExtendedState.currentSide,
                ExpectedPlayerTurns(transaction));
            RewindTransactionGate.EnsureLocked();
            if (RewindTransactionGate.ViolationReason != null) throw new InvalidOperationException(RewindTransactionGate.ViolationReason);
            transaction.HostHash = hash;
            TryCommit(transaction);
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Host rewind failed: {ex}");
            AbortHost(transaction, $"主机回放失败：{ex.Message}");
        }
    }

    private static void OnPrepare(RewindPrepareMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientTransactionId != message.transactionId ||
            _clientGeneration != message.generation || _clientSafety == null ||
            !message.participants.Contains(LocalContext.NetId ?? ulong.MaxValue)) return;
        if (message.protocolVersion != CombatReconnectMod.ProtocolVersion || message.anchorBytes < 0 ||
            message.anchorBytes > ReplayPayloadCodec.MaxAnchorBytes || message.eventCount < 0 ||
            message.encodedAnchorBytes < 0 || message.encodedAnchorBytes > ReplayPayloadCodec.MaxAnchorBytes ||
            message.eventCount > ReplayPayloadCodec.MaxReplayEvents || message.baseReplayStartIndex < 0 ||
            message.replayStartIndex - message.baseReplayStartIndex != message.eventCount ||
            message.participants == null || message.finalPlayerTurnNumbers == null ||
            message.participants.Count != message.finalPlayerTurnNumbers.Count ||
            string.IsNullOrWhiteSpace(message.anchorTransferId) || message.anchorTransferId.Length > 128)
        {
            SendClientReady(message, false, string.Empty, "回滚 Prepare 参数无效。");
            return;
        }
        _clientPrepare = message;
        _clientEncodedAnchor = null;
        ClientEvents.Clear();
        _clientLastEventBatch = false;
        _clientRestoring = false;
        _clientReplayStarted = true;
        RewindTransactionGate.BeginIsolation();
        RewindSafety.ClearTransientState();
        RewindUiController.ShowTransactionStatus("正在加速重放牌序与玩家选择……");
        TryTakeSidecarAnchor();
        TryStartClientReplay();
    }

    private static void OnSidecarAnchorAvailable(SidecarAnchorKey key)
    {
        if (key.Kind != SidecarAnchorKind.Rewind) return;
        TryTakeSidecarAnchor();
        TryStartClientReplay();
    }

    private static void TryTakeSidecarAnchor()
    {
        if (_clientPrepare is not { } prepare || _clientEncodedAnchor != null ||
            string.IsNullOrWhiteSpace(prepare.anchorTransferId)) return;
        var key = new SidecarAnchorKey(SidecarAnchorKind.Rewind, prepare.anchorTransferId,
            prepare.transactionId, prepare.epoch, prepare.generation);
        if (SidecarReplayTransport.TryTakeAnchor(key, out byte[] encoded)) _clientEncodedAnchor = encoded;
    }

    private static void OnEvents(RewindEventBatchMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientPrepare is not { } prepare ||
            prepare.transactionId != message.transactionId || prepare.epoch != message.epoch || message.events == null ||
            message.events.Count > EventsPerBatch || message.firstSequence < 0 ||
            (long)message.firstSequence + message.events.Count > prepare.eventCount) return;
        for (int index = 0; index < message.events.Count; index++)
        {
            DeterministicInputEvent evt = message.events[index];
            int expected = message.firstSequence + index;
            if (evt.sequence != expected || !ClientEvents.TryAdd(expected, evt)) return;
        }
        _clientLastEventBatch |= message.isLast;
        TryStartClientReplay();
    }

    private static void TryStartClientReplay()
    {
        if (_clientRestoring || _clientPrepare is not { } prepare || _clientEncodedAnchor == null ||
            !_clientLastEventBatch || ClientEvents.Count != prepare.eventCount) return;
        for (int sequence = 0; sequence < prepare.eventCount; sequence++) if (!ClientEvents.ContainsKey(sequence)) return;
        _clientRestoring = true;
        _ = ReplayClient(prepare, _clientEncodedAnchor);
    }

    private static async Task ReplayClient(RewindPrepareMessage prepare, byte[] encoded)
    {
        bool success = false;
        string hash = string.Empty;
        string detail = string.Empty;
        try
        {
            if (encoded.Length != prepare.encodedAnchorBytes || ReplayPayloadCodec.Hash(encoded) != prepare.encodedAnchorHash)
                throw new InvalidDataException("编码后的回滚锚点长度或 SHA-256 不匹配。");
            byte[] payload = ReplayAnchorCompressionCodec.Decode(encoded, prepare.anchorCompression, prepare.anchorBytes);
            if (ReplayPayloadCodec.Hash(payload) != prepare.anchorHash)
                throw new InvalidDataException("解码后的回滚锚点 SHA-256 不匹配。");
            (NetFullCombatState state, ExtendedReconnectMessage extended) = ReplayPayloadCodec.Deserialize(payload);
            List<DeterministicInputEvent> events = Enumerable.Range(0, prepare.eventCount).Select(i => ClientEvents[i]).ToList();
            hash = await DeterministicReplayEngine.RestoreAndReplay(state, extended, events, prepare.finalActionId,
                prepare.finalHookId, prepare.finalChecksumId, true, _clientCancellation?.Token ?? default,
                prepare.finalRoundNumber, prepare.finalSide, ExpectedPlayerTurns(prepare));
            RewindTransactionGate.EnsureLocked();
            if (RewindTransactionGate.ViolationReason != null) throw new InvalidOperationException(RewindTransactionGate.ViolationReason);
            if (hash != prepare.finalStateHash)
                throw new InvalidOperationException($"回放 hash 不匹配：expected={prepare.finalStateHash}, actual={hash}");
            success = true;
        }
        catch (Exception ex) { detail = ex.Message; ReconnectLog.Write($"Client rewind failed: {ex}"); }
        SendClientReady(prepare, success, hash, detail);
    }

    private static void SendClientReady(RewindPrepareMessage prepare, bool success, string hash, string detail)
    {
        RunManager manager = RunManager.Instance;
        manager.NetService.SendMessage(new RewindReadyMessage
        {
            transactionId = prepare.transactionId, epoch = prepare.epoch, success = success, stateHash = hash,
            detail = detail, actionId = manager.ActionQueueSet.NextActionId,
            hookId = manager.ActionQueueSynchronizer.NextHookId, checksumId = manager.ChecksumTracker.NextId
        });
    }

    private static void OnReady(RewindReadyMessage message, ulong senderId)
    {
        HostTransaction? transaction = _hostTransaction;
        if (RunManager.Instance.NetService.Type != NetGameType.Host || transaction == null || transaction.Aborting ||
            transaction.Id != message.transactionId || transaction.Anchor.Epoch != message.epoch ||
            !transaction.Participants.Contains(senderId)) return;
        if (!message.success || message.stateHash != transaction.Anchor.PayloadHash ||
            message.actionId != transaction.Anchor.ExtendedState.nextActionId ||
            message.hookId != transaction.Anchor.ExtendedState.nextHookId ||
            message.checksumId != transaction.Anchor.ExtendedState.nextChecksumId)
        {
            ReconnectLog.Write($"Player {senderId} rewind validation failed: {message.detail}; " +
                               $"hash={message.stateHash}; counters={message.actionId}/{message.hookId}/{message.checksumId}.");
            AbortHost(transaction, $"玩家 {senderId} 回放校验失败：{message.detail}");
            return;
        }
        transaction.ReadyClients.Add(senderId);
        TryCommit(transaction);
    }

    private static void TryCommit(HostTransaction transaction)
    {
        if (_hostTransaction != transaction || transaction.Aborting || transaction.HostHash == null ||
            transaction.ReadyClients.Count != transaction.Participants.Count - 1 || transaction.CommitPrepared) return;
        if (transaction.HostHash != transaction.Anchor.PayloadHash)
        {
            AbortHost(transaction, $"主机回放 hash 不匹配：expected={transaction.Anchor.PayloadHash}, actual={transaction.HostHash}");
            return;
        }
        try
        {
            DeterministicReplayEngine.ValidateFinalState(transaction.LocalNetId, transaction.Anchor.RoundNumber,
                transaction.Anchor.ExtendedState.currentSide, ExpectedPlayerTurns(transaction));
        }
        catch (Exception ex)
        {
            AbortHost(transaction, $"主机提交准备校验失败：{ex.Message}");
            return;
        }
        transaction.CommitPrepared = true;
        RunManager.Instance.NetService.SendMessage(new RewindCommitMessage
        {
            transactionId = transaction.Id, epoch = transaction.Anchor.Epoch,
            stateHash = transaction.HostHash, generation = transaction.Generation
        });
        ReconnectLog.Write($"Prepared rewind commit {transaction.Id}; waiting for client acknowledgements.");
    }

    private static void OnCommit(RewindCommitMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientPrepare is not { } prepare ||
            prepare.transactionId != message.transactionId || prepare.epoch != message.epoch ||
            _clientGeneration != message.generation) return;
        bool success = false;
        string hash = string.Empty;
        string detail = string.Empty;
        try
        {
            DeterministicReplayEngine.ValidateFinalState(_clientLocalNetId, prepare.finalRoundNumber,
                prepare.finalSide, ExpectedPlayerTurns(prepare));
            hash = DeterministicReplayEngine.CaptureCurrentStateHash();
            if (hash != message.stateHash) throw new InvalidOperationException($"Commit hash mismatch: {hash}");
            success = true;
            _clientCommitPrepared = true;
        }
        catch (Exception ex)
        {
            RewindTransactionGate.EnsureLocked();
            detail = ex.Message;
            ReconnectLog.Write($"Client rewind commit validation failed: {ex}");
        }
        RunManager.Instance.NetService.SendMessage(new RewindCommitAckMessage
        {
            transactionId = message.transactionId, epoch = message.epoch, generation = message.generation,
            success = success, stateHash = hash, detail = detail
        });
    }

    private static void OnCommitAck(RewindCommitAckMessage message, ulong senderId)
    {
        HostTransaction? transaction = _hostTransaction;
        if (RunManager.Instance.NetService.Type != NetGameType.Host || transaction == null || transaction.Aborting ||
            !transaction.CommitPrepared || transaction.Id != message.transactionId ||
            transaction.Anchor.Epoch != message.epoch || transaction.Generation != message.generation ||
            !transaction.Participants.Contains(senderId)) return;
        if (!message.success || message.stateHash != transaction.Anchor.PayloadHash)
        {
            AbortHost(transaction, $"玩家 {senderId} 最终提交校验失败：{message.detail}");
            return;
        }
        transaction.CommitAcks.Add(senderId);
        if (transaction.CommitAcks.Count != transaction.Participants.Count - 1) return;

        try
        {
            DeterministicReplayEngine.ValidateFinalState(transaction.LocalNetId, transaction.Anchor.RoundNumber,
                transaction.Anchor.ExtendedState.currentSide, ExpectedPlayerTurns(transaction));
            string hash = DeterministicReplayEngine.CaptureCurrentStateHash();
            if (hash != transaction.Anchor.PayloadHash)
                throw new InvalidOperationException($"Host state changed before commit completion: {hash}");
            TurnReplayLedger.TruncateAfter(transaction.Anchor);
            RunManager.Instance.NetService.SendMessage(new RewindCommitCompleteMessage
            {
                transactionId = transaction.Id, epoch = transaction.Anchor.Epoch,
                generation = transaction.Generation, stateHash = hash
            });
            transaction.Cancellation.Cancel();
            _hostTransaction = null;
            RewindTransactionGate.Unlock();
            ReconnectLog.Write($"Completed rewind commit {transaction.Id} after all client acknowledgements.");
        }
        catch (Exception ex)
        {
            AbortHost(transaction, $"主机最终提交校验失败：{ex.Message}");
        }
    }

    private static void OnCommitComplete(RewindCommitCompleteMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Client || !_clientCommitPrepared ||
            _clientPrepare is not { } prepare || prepare.transactionId != message.transactionId ||
            prepare.epoch != message.epoch || _clientGeneration != message.generation ||
            message.stateHash != prepare.finalStateHash) return;
        TurnReplayLedger.TruncateNativeEventsAfter(prepare.replayStartIndex);
        ClearClientTransaction();
        RewindTransactionGate.Unlock();
        ReconnectLog.Write($"Completed rewind commit {message.transactionId} after host confirmation.");
    }

    private static Dictionary<ulong, int> ExpectedPlayerTurns(HostTransaction transaction)
        => transaction.Anchor.CombatState.Players.ToDictionary(player => player.playerId, player => player.turnNumber);

    private static Dictionary<ulong, int> ExpectedPlayerTurns(RewindPrepareMessage prepare)
        => prepare.participants.Select((id, index) => (id, turn: prepare.finalPlayerTurnNumbers[index]))
            .ToDictionary(pair => pair.id, pair => pair.turn);

    private static void AbortHost(HostTransaction transaction, string reason)
    {
        if (_hostTransaction != transaction || transaction.Aborting) return;
        ReconnectLog.Write(
            $"Aborting rewind transaction={transaction.Id}, generation={transaction.Generation}, " +
            $"epoch={transaction.Anchor.Epoch}, replayStarted={transaction.ReplayStarted}: {reason}");
        transaction.Aborting = true;
        transaction.AbortReason = reason;
        transaction.Cancellation.Cancel();
        RunManager.Instance.NetService.SendMessage(new RewindAbortMessage
        {
            transactionId = transaction.Id, epoch = transaction.Anchor.Epoch, generation = transaction.Generation,
            reason = reason, failedEventSequence = -1,
            counterDetail = $"expected={transaction.Anchor.ExtendedState.nextActionId}/" +
                            $"{transaction.Anchor.ExtendedState.nextHookId}/{transaction.Anchor.ExtendedState.nextChecksumId}"
        });
        if (!transaction.ReplayStarted)
        {
            _hostTransaction = null;
            RewindTransactionGate.Unlock();
            return;
        }
        RewindUiController.ShowTransactionStatus("回放失败，正在恢复回滚前状态……");
        _ = RecoverHost(transaction);
    }

    private static string BoundaryFingerprint(RewindSafetySnapshot safety)
        => $"parts=({ReplayPayloadCodec.FingerprintSummary(safety.State, safety.Extended)}), " +
           $"cards=({ReplayPayloadCodec.CardFingerprintSummary(safety.Extended)})";

    private static async Task RecoverHost(HostTransaction transaction)
    {
        BoundaryResult result = await RecoverSafety(transaction.Safety);
        transaction.Recoveries[LocalContext.NetId ?? ulong.MaxValue] = result;
        TryCompleteAbort(transaction);
    }

    private static void OnAbortRecovered(RewindAbortRecoveredMessage message, ulong senderId)
    {
        HostTransaction? transaction = _hostTransaction;
        if (RunManager.Instance.NetService.Type != NetGameType.Host || transaction == null || !transaction.Aborting ||
            transaction.Id != message.transactionId || transaction.Generation != message.generation ||
            !transaction.Participants.Contains(senderId)) return;
        transaction.Recoveries[senderId] = new BoundaryResult(message.success, message.stateHash, message.detail);
        TryCompleteAbort(transaction);
    }

    private static void TryCompleteAbort(HostTransaction transaction)
    {
        if (_hostTransaction != transaction || transaction.Recoveries.Count != transaction.Participants.Count ||
            transaction.Safety == null) return;
        bool success = transaction.Recoveries.Values.All(result => result.Success && result.Hash == transaction.Safety.Hash);
        if (!success)
        {
            foreach ((ulong playerId, BoundaryResult result) in transaction.Recoveries)
                ReconnectLog.Write($"Abort recovery result player={playerId}, success={result.Success}, " +
                                   $"hash={result.Hash}, expected={transaction.Safety.Hash}, detail={result.Detail}");
        }
        RunManager.Instance.NetService.SendMessage(new RewindAbortCompleteMessage
        {
            transactionId = transaction.Id, generation = transaction.Generation, success = success,
            reason = success ? transaction.AbortReason : "至少一端无法恢复回滚前安全状态，战斗保持暂停。"
        });
        if (!success)
        {
            RewindTransactionGate.EnsureLocked();
            ReconnectLog.Write("Abort recovery failed; combat remains paused.");
            return;
        }
        _hostTransaction = null;
        RewindTransactionGate.Unlock();
        RewindUiController.ShowTransientStatus($"回滚失败，已恢复回滚前状态。\n{transaction.AbortReason}", 9000);
    }

    private static void OnAbort(RewindAbortMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientTransactionId != message.transactionId ||
            _clientGeneration != message.generation) return;
        _clientCancellation?.Cancel();
        if (!_clientReplayStarted)
        {
            ClearClientTransaction();
            RewindTransactionGate.Unlock();
            return;
        }
        RewindUiController.ShowTransactionStatus("回放失败，正在恢复回滚前状态……");
        _ = RecoverClient(message);
    }

    private static async Task RecoverClient(RewindAbortMessage message)
    {
        BoundaryResult result = await RecoverSafety(_clientSafety);
        RunManager.Instance.NetService.SendMessage(new RewindAbortRecoveredMessage
        {
            transactionId = message.transactionId, generation = message.generation, success = result.Success,
            stateHash = result.Hash, detail = result.Detail
        });
    }

    internal static async Task<BoundaryResult> RecoverSafety(RewindSafetySnapshot? safety)
    {
        if (safety == null) return new BoundaryResult(false, string.Empty, "缺少回滚前安全快照。");
        try
        {
            SceneTree tree = (SceneTree)Godot.Engine.GetMainLoop();
            for (int frame = 0; frame < 600 && DeterministicReplayEngine.IsReplaying; frame++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (DeterministicReplayEngine.IsReplaying)
                throw new TimeoutException("等待失败回放停止超时。");
            RewindTransactionGate.AllowAbortDrain();
            try
            {
                await SyntheticStartTurnTracker.AwaitAll("pre-abort recovery");
                await DeterministicReplayEngine.AwaitCombatQuiescence(
                    "pre-abort recovery", default, maxFrames: 120);
            }
            catch (TimeoutException ex)
            {
                ReconnectLog.Write($"Failed timeline did not drain cleanly; forcing snapshot recovery: {ex.Message}");
                CombatStateRestorer.ResetFailedTurnTransition();
            }
            RewindTransactionGate.BeginIsolation();
            RewindSafety.ClearTransientState();
            string hash = await DeterministicReplayEngine.RestoreAndReplay(safety.State, safety.Extended,
                Array.Empty<DeterministicInputEvent>(), safety.Extended.nextActionId, safety.Extended.nextHookId,
                safety.Extended.nextChecksumId, false, default,
                safety.Extended.roundNumber, safety.Extended.currentSide,
                safety.State.Players.ToDictionary(player => player.playerId, player => player.turnNumber));
            await DeterministicReplayEngine.AwaitCombatQuiescence(
                "post-abort recovery", default, safety.Extended.roundNumber, safety.Extended.currentSide);
            hash = DeterministicReplayEngine.CaptureCurrentStateHash();
            RewindTransactionGate.EnsureLocked();
            return new BoundaryResult(hash == safety.Hash, hash, hash == safety.Hash ? string.Empty : "安全快照 hash 不匹配。");
        }
        catch (Exception ex)
        {
            RewindTransactionGate.EnsureLocked();
            return new BoundaryResult(false, string.Empty, ex.Message);
        }
    }

    internal static async Task<(bool Success, string Hash, string Detail)> RestoreSafetyForSelfTest(
        RewindSafetySnapshot safety)
    {
        BoundaryResult result = await RecoverSafety(safety);
        return (result.Success, result.Hash, result.Detail);
    }

    private static void OnAbortComplete(RewindAbortCompleteMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Client || _clientTransactionId != message.transactionId ||
            _clientGeneration != message.generation) return;
        if (!message.success)
        {
            RewindTransactionGate.EnsureLocked();
            RewindUiController.ShowTransactionStatus(message.reason);
            return;
        }
        ClearClientTransaction();
        RewindTransactionGate.Unlock();
        RewindUiController.ShowTransientStatus($"回滚失败，已恢复回滚前状态。\n{message.reason}", 9000);
    }

    private static async Task TransactionTimeout(HostTransaction transaction)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(TransactionTimeoutSeconds), transaction.Cancellation.Token);
            AbortHost(transaction, "回滚事务总超时。");
        }
        catch (OperationCanceledException) { }
    }

    private static bool IsActiveHost(ulong senderId)
        => RunManager.Instance.NetService is NetClientGameService client && senderId == client.HostNetId;

    private static void ClearClientTransaction()
    {
        _clientCancellation?.Cancel();
        _clientCancellation?.Dispose();
        _clientCancellation = null;
        _clientVoteId = null;
        _clientVoteResponded = false;
        _clientTransactionId = null;
        _clientGeneration = 0;
        _clientSafety = null;
        _clientPrepare = null;
        SidecarReplayTransport.Discard(_clientPrepare?.anchorTransferId);
        _clientEncodedAnchor = null;
        ClientEvents.Clear();
        _clientLastEventBatch = false;
        _clientRestoring = false;
        _clientReplayStarted = false;
        _clientCommitPrepared = false;
        _clientLocalNetId = null;
    }

    internal static IReadOnlyList<TurnHistoryMetadata> GetHistory()
        => RunManager.Instance.NetService.Type == NetGameType.Client ? RemoteHistory.ToList() : TurnReplayLedger.GetHistory();

    internal static void BroadcastHistory()
    {
        RewindUiController.RefreshHistory();
        if (RunManager.Instance.NetService.Type == NetGameType.Host)
            RunManager.Instance.NetService.SendMessage(new TurnHistorySyncMessage { entries = TurnReplayLedger.GetHistory().ToList() });
    }

    internal static void ClearRemoteHistory()
    {
        RemoteHistory.Clear();
        _hostVote?.Cancellation.Cancel();
        _hostVote = null;
        _hostTransaction?.Cancellation.Cancel();
        _hostTransaction = null;
        ClearClientTransaction();
        RewindTransactionGate.Reset();
    }

    private static void OnHistorySync(TurnHistorySyncMessage message, ulong _)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Client) return;
        RemoteHistory.Clear();
        RemoteHistory.AddRange(message.entries ?? new List<TurnHistoryMetadata>());
        RewindUiController.RefreshHistory();
    }

    private sealed class HostVote
    {
        internal HostVote(string id, ulong requesterId, TurnReplayAnchor target, List<ulong> participants)
        { Id = id; RequesterId = requesterId; Target = target; Participants = participants; }
        internal string Id { get; }
        internal ulong RequesterId { get; }
        internal TurnReplayAnchor Target { get; }
        internal List<ulong> Participants { get; }
        internal HashSet<ulong> Approvals { get; } = new();
        internal HashSet<ulong> Responded { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class HostTransaction
    {
        internal static int NextGeneration;
        internal HostTransaction(string id, RewindReplayPlan plan, List<ulong> participants, int generation)
        { Id = id; Plan = plan; Participants = participants; Generation = generation; LocalNetId = LocalContext.NetId; }
        internal string Id { get; }
        internal RewindReplayPlan Plan { get; }
        internal TurnReplayAnchor Anchor => Plan.Target;
        internal List<ulong> Participants { get; }
        internal int Generation { get; }
        internal ulong? LocalNetId { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal HashSet<ulong> FreezeAcks { get; } = new();
        internal Dictionary<ulong, BoundaryResult> Boundaries { get; } = new();
        internal HashSet<ulong> ReadyClients { get; } = new();
        internal HashSet<ulong> CommitAcks { get; } = new();
        internal Dictionary<ulong, BoundaryResult> Recoveries { get; } = new();
        internal bool DrainSent { get; set; }
        internal bool ReplayStarted { get; set; }
        internal bool CommitPrepared { get; set; }
        internal bool Aborting { get; set; }
        internal string AbortReason { get; set; } = "回滚失败。";
        internal RewindSafetySnapshot? Safety { get; set; }
        internal string? HostHash { get; set; }
    }

    internal sealed record BoundaryResult(bool Success, string Hash, string Detail);
}
