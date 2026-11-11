using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatReconnect;

internal static class TurnReplayLedger
{
    private static readonly FieldInfo ReplayField = AccessTools.Field(typeof(CombatReplayWriter), "_replay");
    private static readonly List<TurnReplayAnchor> History = new();
    private static readonly Dictionary<ulong, TurnHistoryMetadata> HistoryMetadata = new();
    private static TurnReplayAnchor? _replayRoot;
    private static ulong _nextEpoch;
    private static int _captureGeneration;
    internal static TurnReplayAnchor? Anchor => History.LastOrDefault();
    internal static FrozenReplaySession? Frozen { get; private set; }

    internal static void Reset()
    {
        _captureGeneration++;
        History.Clear();
        HistoryMetadata.Clear();
        _replayRoot = null;
        Frozen = null;
    }

    internal static Task ScheduleTurnAnchor(CombatState state)
    {
        if (RunManager.Instance.NetService.Type == NetGameType.Client || state.CurrentSide != CombatSide.Player)
            return Task.CompletedTask;
        if (Anchor?.RoundNumber == state.RoundNumber) return Task.CompletedTask;
        int generation = ++_captureGeneration;
        return CaptureAtPlayerBoundary(state.RoundNumber, generation);
    }

    private static async Task CaptureAtPlayerBoundary(int roundNumber, int generation)
    {
        try
        {
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            const int maxTransientFrames = 300;
            int transientFrames = 0;
            ClientRejoinResponseMessage response;
            ExtendedReconnectMessage extended;
            while (true)
            {
                if (generation != _captureGeneration || !CombatManager.Instance.IsInProgress) return;
                CombatState? current = CombatManager.Instance.DebugOnlyGetState();
                if (current == null || current.RoundNumber != roundNumber || current.CurrentSide != CombatSide.Player) return;
                if (Anchor?.RoundNumber == roundNumber) return;

                try
                {
                    response = RunManager.Instance.GetRejoinMessage();
                    if (response.combatState == null) return;
                    extended = ExtendedReconnectCapture.Capture();
                    break;
                }
                catch (TransientRelicSnapshotException ex) when (transientFrames < maxTransientFrames)
                {
                    if (transientFrames == 0)
                        ReconnectLog.Write($"Turn anchor capture deferred for transient relic state: {ex.Message}");
                    transientFrames++;
                    SceneTree tree = (SceneTree)Engine.GetMainLoop();
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                }
            }

            byte[] payload = ReplayPayloadCodec.Serialize(response.combatState, extended);
            TurnReplayAnchor anchor = new(++_nextEpoch, roundNumber, response.combatState, extended,
                GetReplay()?.events.Count ?? 0, payload, ReplayPayloadCodec.Hash(payload));
            ReplayPayloadCodec.LogFingerprint("anchor", response.combatState, extended);
            _replayRoot ??= anchor;
            History.Add(anchor);
            HistoryMetadata[anchor.Epoch] = new TurnHistoryMetadata
            {
                epoch = anchor.Epoch,
                roundNumber = anchor.RoundNumber,
                activeSide = extended.currentSide,
                createdAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                displayMetadata = $"Round {anchor.RoundNumber}"
            };
            int removeCount = History.Count - RewindSettingsStore.TurnHistoryLimit;
            if (removeCount > 0)
            {
                foreach (TurnReplayAnchor removed in History.Take(removeCount)) HistoryMetadata.Remove(removed.Epoch);
                History.RemoveRange(0, removeCount);
            }
            Frozen = null;
            MultiplayerRewindCoordinator.BroadcastHistory();
            ReconnectLog.Write(
                $"Captured turn replay anchor epoch={anchor.Epoch}, round={roundNumber}, replayIndex={anchor.ReplayEventIndex}, " +
                $"history={History.Count}, transientRetryFrames={transientFrames}.");
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Turn anchor capture failed: {ex}");
        }
    }

    internal static FrozenReplaySession Freeze()
    {
        TurnReplayAnchor anchor = Anchor ?? throw new InvalidOperationException("No player-turn replay anchor is available.");
        CombatReplay replay = GetReplay() ?? throw new InvalidOperationException("The native combat replay writer is not recording.");
        if (replay.events.Count < anchor.ReplayEventIndex)
            throw new InvalidOperationException("Native replay history was reset after the turn anchor.");

        var events = replay.events.Skip(anchor.ReplayEventIndex)
            .Select((evt, index) => new DeterministicInputEvent { sequence = index, replayEvent = evt })
            .ToList();
        ClientRejoinResponseMessage response = RunManager.Instance.GetRejoinMessage();
        if (response.combatState == null) throw new InvalidOperationException("Combat ended before replay freeze.");
        ExtendedReconnectMessage finalExtended = ExtendedReconnectCapture.Capture();
        byte[] finalPayload = ReplayPayloadCodec.Serialize(response.combatState, finalExtended);
        ReplayPayloadCodec.LogFingerprint("frozen-final", response.combatState, finalExtended);
        Frozen = new FrozenReplaySession(anchor, events, finalExtended, ReplayPayloadCodec.Hash(finalPayload));
        ReconnectLog.Write($"Frozen replay epoch={anchor.Epoch}, events={events.Count}, final={Frozen.FinalStateHash}.");
        return Frozen;
    }

    internal static void TruncateAfter(TurnReplayAnchor anchor)
    {
        int anchorIndex = History.FindIndex(entry => entry.Epoch == anchor.Epoch);
        if (anchorIndex < 0) throw new InvalidOperationException("The rewind anchor is no longer retained.");
        if (anchorIndex + 1 < History.Count)
        {
            foreach (TurnReplayAnchor removed in History.Skip(anchorIndex + 1)) HistoryMetadata.Remove(removed.Epoch);
            History.RemoveRange(anchorIndex + 1, History.Count - anchorIndex - 1);
        }

        TruncateNativeEventsAfter(anchor.ReplayEventIndex);
        Frozen = null;
        MultiplayerRewindCoordinator.BroadcastHistory();
    }

    internal static void TruncateNativeEventsAfter(int replayEventIndex)
    {
        CombatReplay? replay = GetReplay();
        if (replay != null && replay.events.Count > replayEventIndex)
            replay.events.RemoveRange(replayEventIndex, replay.events.Count - replayEventIndex);
    }

    internal static TurnReplayAnchor? FindAnchor(ulong epoch)
        => History.FirstOrDefault(anchor => anchor.Epoch == epoch);

    internal static RewindReplayPlan BuildRewindPlan(TurnReplayAnchor target)
    {
        TurnReplayAnchor root = _replayRoot
            ?? throw new InvalidOperationException("The first-turn replay anchor is unavailable.");
        CombatReplay replay = GetReplay()
            ?? throw new InvalidOperationException("The native combat replay writer is not recording.");
        if (root.ReplayEventIndex > target.ReplayEventIndex || replay.events.Count < target.ReplayEventIndex)
            throw new InvalidOperationException(
                $"Replay history cannot reach round {target.RoundNumber}: root={root.ReplayEventIndex}, " +
                $"target={target.ReplayEventIndex}, events={replay.events.Count}.");

        List<DeterministicInputEvent> events = replay.events
            .Skip(root.ReplayEventIndex)
            .Take(target.ReplayEventIndex - root.ReplayEventIndex)
            .Select((evt, sequence) => new DeterministicInputEvent { sequence = sequence, replayEvent = evt })
            .ToList();
        return new RewindReplayPlan(root, target, events);
    }

    internal static IReadOnlyList<TurnHistoryMetadata> GetHistory()
        => History.Select(anchor => HistoryMetadata[anchor.Epoch]).ToList();

    private static CombatReplay? GetReplay()
        => ReplayField.GetValue(RunManager.Instance.CombatReplayWriter) as CombatReplay;
}

[HarmonyPatch(typeof(CombatManager), "StartTurn", new[] { typeof(Func<Task>) })]
internal static class CaptureTurnReplayAnchorPatch
{
    private static void Postfix(CombatManager __instance, ref Task __result)
    {
        Task original = __result;
        SyntheticStartTurnTracker.Track(original);
        __result = CaptureAfterTurnStarted(__instance, original);
    }

    private static async Task CaptureAfterTurnStarted(CombatManager manager, Task original)
    {
        await original;
        if (RejoinLoader.IsLoading || DeterministicReplayEngine.IsReplaying)
            return;
        CombatState? state = manager.DebugOnlyGetState();
        if (state != null) await TurnReplayLedger.ScheduleTurnAnchor(state);
    }
}

[HarmonyPatch(typeof(CombatManager), "StartCombatInternal", new Type[] { })]
internal static class TrackSyntheticCombatStartupPatch
{
    private static void Postfix(Task __result)
    {
        RewindUiController.ScheduleAttach();
        SyntheticStartTurnTracker.Track(__result);
        _ = ReconnectDiscovery.ArmAfterCombatStarts(__result);
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal), new Type[] { })]
internal static class ClearReconnectDiscoveryAfterCombatPatch
{
    private static void Postfix(Task __result)
        => _ = ReconnectDiscovery.ClearAfterConnectedCombatEnds(__result);
}

internal static class SyntheticStartTurnTracker
{
    private static readonly object Sync = new();
    private static readonly HashSet<Task> Pending = new();

    internal static void Track(Task task)
    {
        lock (Sync) Pending.Add(task);
    }

    internal static void Reset()
    {
        lock (Sync) Pending.Clear();
    }

    internal static async Task AwaitAll(string phase, CancellationToken cancellationToken = default)
    {
        const int requiredStableFrames = 3;
        const int maxFrames = 600;
        int stableFrames = 0;
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        for (int frame = 0; frame < maxFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task[] pending;
            lock (Sync)
            {
                pending = Pending.ToArray();
                Pending.ExceptWith(pending);
            }
            if (pending.Length == 0)
            {
                stableFrames++;
                if (stableFrames >= requiredStableFrames)
                {
                    ReconnectLog.Write($"All synthetic combat startup tasks completed during {phase}.");
                    return;
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                continue;
            }

            stableFrames = 0;
            Task all = Task.WhenAll(pending);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            if (await Task.WhenAny(all, timeout) != all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"Timed out waiting for {pending.Length} synthetic combat startup task(s) during {phase}.");
            }
            await all; // Preserve the original StartTurn failure and stack trace.
        }
        throw new TimeoutException($"Synthetic combat startup tasks did not stabilize during {phase}.");
    }
}

[HarmonyPatch(typeof(CombatManager), "Reset", new[] { typeof(bool) })]
internal static class ResetTurnReplayPatch
{
    private static void Prefix()
    {
        TurnReplayLedger.Reset();
        SyntheticStartTurnTracker.Reset();
        MultiplayerRewindCoordinator.ClearRemoteHistory();
        ReconnectCoordinator.Reset();
        SidecarReplayTransport.Reset();
        RewindUiController.Remove();
    }
}

internal sealed record ReceivedReplayPackage(
    ReplayManifestMessage Manifest,
    NetFullCombatState AnchorState,
    ExtendedReconnectMessage AnchorExtended,
    IReadOnlyList<DeterministicInputEvent> Events);

internal static class ReplayClientExchange
{
    private const int EventsPerBatch = 32;
    private static TaskCompletionSource<ReceivedReplayPackage>? _package;
    private static TaskCompletionSource<ReplayCommitMessage>? _commit;
    private static ReplayManifestMessage? _manifest;
    private static byte[]? _encodedAnchor;
    private static readonly SortedDictionary<int, DeterministicInputEvent> Events = new();
    private static bool _lastBatch;
    private static INetGameService? _service;
    private static bool _sidecarSubscribed;

    internal static void Prepare(INetGameService service)
    {
        SidecarReplayTransport.PrepareClient(service);
        if (!_sidecarSubscribed)
        {
            SidecarReplayTransport.AnchorAvailable += OnSidecarAnchorAvailable;
            _sidecarSubscribed = true;
        }
        _service = service;
        _package = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _manifest = null; _encodedAnchor = null; Events.Clear(); _lastBatch = false;
        service.RegisterMessageHandler<ReplayManifestMessage>(OnManifest);
        service.RegisterMessageHandler<ReplayEventBatchMessage>(OnEvents);
        service.RegisterMessageHandler<ReplayCommitMessage>(OnCommit);
        service.RegisterMessageHandler<ReplayAbortMessage>(OnAbort);
    }

    private static void OnManifest(ReplayManifestMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (message.protocolVersion != CombatReconnectMod.ProtocolVersion)
        {
            Reject(message, $"Replay protocol mismatch: host={message.protocolVersion}, local={CombatReconnectMod.ProtocolVersion}.");
            return;
        }
        if (message.messageTableHash != MultiplayerTypeTableIdentity.MessageHash ||
            message.actionTableHash != MultiplayerTypeTableIdentity.ActionHash)
        {
            Reject(message,
                $"Multiplayer type table mismatch: messages host={message.messageTableHash} local={MultiplayerTypeTableIdentity.MessageHash}; " +
                $"actions host={message.actionTableHash} local={MultiplayerTypeTableIdentity.ActionHash}.");
            return;
        }
        int expectedChunks = Math.Max(1,
            (message.encodedAnchorBytes + ReplayPayloadCodec.ChunkSize - 1) / ReplayPayloadCodec.ChunkSize);
        if (message.anchorBytes < 0 || message.anchorBytes > ReplayPayloadCodec.MaxAnchorBytes ||
            message.encodedAnchorBytes < 0 || message.encodedAnchorBytes > ReplayPayloadCodec.MaxAnchorBytes ||
            message.anchorChunks != expectedChunks || message.eventCount < 0 ||
            message.eventCount > ReplayPayloadCodec.MaxReplayEvents || message.anchorGeneration < 0 ||
            string.IsNullOrWhiteSpace(message.anchorTransferId) || message.anchorTransferId.Length > 128)
        {
            Reject(message,
                $"Invalid replay manifest bounds: bytes={message.anchorBytes}, chunks={message.anchorChunks}, events={message.eventCount}.");
            return;
        }
        _manifest = message;
        TryTakeSidecarAnchor();
        TryComplete();
    }

    private static void OnSidecarAnchorAvailable(SidecarAnchorKey key)
    {
        if (key.Kind != SidecarAnchorKind.Replay) return;
        TryTakeSidecarAnchor();
        TryComplete();
    }

    private static void TryTakeSidecarAnchor()
    {
        if (_manifest is not { } manifest || _encodedAnchor != null || string.IsNullOrWhiteSpace(manifest.anchorTransferId)) return;
        var key = new SidecarAnchorKey(SidecarAnchorKind.Replay, manifest.anchorTransferId,
            manifest.anchorTransferId, manifest.epoch, manifest.anchorGeneration);
        if (SidecarReplayTransport.TryTakeAnchor(key, out byte[] encoded)) _encodedAnchor = encoded;
    }

    private static void OnEvents(ReplayEventBatchMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (_manifest is not { } manifest || manifest.epoch != message.epoch ||
            manifest.anchorGeneration != message.generation || manifest.anchorTransferId != message.transferId) return;

        if (message.events == null || message.events.Count > EventsPerBatch || message.firstSequence < 0 ||
            (long)message.firstSequence + message.events.Count > manifest.eventCount)
        {
            Reject(manifest, "Replay event batch bounds were invalid.");
            return;
        }

        for (int i = 0; i < message.events.Count; i++)
        {
            DeterministicInputEvent evt = message.events[i];
            int expectedSequence = message.firstSequence + i;

            if (evt.sequence != expectedSequence)
            {
                Reject(manifest,
                    $"Replay event batch sequence mismatch: expected {expectedSequence}, got {evt.sequence}.");
                return;
            }

            if (!Events.TryAdd(evt.sequence, evt))
            {
                Reject(manifest, $"Duplicate replay event sequence {evt.sequence}.");
                return;
            }
        }

        _lastBatch |= message.isLast;
        TryComplete();
    }

    private static void TryComplete()
    {
        if (_manifest is not { } manifest || _encodedAnchor == null ||
            !_lastBatch || Events.Count != manifest.eventCount) return;

        for (int sequence = 0; sequence < manifest.eventCount; sequence++)
        {
            if (!Events.ContainsKey(sequence))
            {
                Reject(manifest, $"Replay event sequence gap at {sequence}.");
                return;
            }
        }

        byte[] encoded = _encodedAnchor;
        if (encoded.Length != manifest.encodedAnchorBytes || ReplayPayloadCodec.Hash(encoded) != manifest.encodedAnchorHash)
        {
            Reject(manifest, "Encoded replay anchor length or SHA-256 did not match the manifest.");
            return;
        }
        try
        {
            byte[] payload = ReplayAnchorCompressionCodec.Decode(encoded, manifest.anchorCompression, manifest.anchorBytes);
            if (ReplayPayloadCodec.Hash(payload) != manifest.anchorHash)
                throw new InvalidDataException("Decoded replay anchor SHA-256 did not match the manifest.");
            var decoded = ReplayPayloadCodec.Deserialize(payload);
            var orderedEvents = Enumerable.Range(0, manifest.eventCount).Select(i => Events[i]).ToList();
            _package?.TrySetResult(new ReceivedReplayPackage(manifest, decoded.State, decoded.Extended, orderedEvents));
        }
        catch (Exception ex) { Reject(manifest, $"Replay anchor decode failed: {ex.Message}"); }
    }

    private static void OnCommit(ReplayCommitMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (_manifest is { } manifest && manifest.epoch == message.epoch &&
            manifest.anchorGeneration == message.generation && manifest.anchorTransferId == message.transferId)
            _commit?.TrySetResult(message);
    }

    private static void OnAbort(ReplayAbortMessage message, ulong senderId)
    {
        if (!IsActiveHost(senderId)) return;
        if (_manifest is not { } manifest || manifest.epoch != message.epoch ||
            manifest.anchorGeneration != message.generation || manifest.anchorTransferId != message.transferId) return;
        var error = new InvalidOperationException("Host aborted deterministic replay: " + message.reason);
        _package?.TrySetException(error); _commit?.TrySetException(error);
    }

    private static void Reject(ReplayManifestMessage manifest, string reason)
    {
        var error = new InvalidDataException(reason);
        _package?.TrySetException(error);
        _commit?.TrySetException(error);
        if (_service?.IsConnected == true)
            _service.SendMessage(new ReplayAbortMessage
            {
                epoch = manifest.epoch, generation = manifest.anchorGeneration,
                transferId = manifest.anchorTransferId, reason = reason
            });
        ReconnectLog.Write(reason);
    }

    internal static void SendAbort(string reason)
    {
        if (_service?.IsConnected != true || _manifest is not { } manifest) return;
        _service.SendMessage(new ReplayAbortMessage
        {
            epoch = manifest.epoch, generation = manifest.anchorGeneration,
            transferId = manifest.anchorTransferId, reason = reason
        });
    }

    private static bool IsActiveHost(ulong senderId)
        => _service is NetClientGameService client && senderId == client.HostNetId;

    internal static async Task<ReceivedReplayPackage> WaitForPackage()
        => await WithTimeout(_package?.Task ?? throw new InvalidOperationException("Replay receiver was not prepared."), 20, "replay package");

    internal static async Task<ReplayCommitMessage> WaitForCommit()
        => await WithTimeout(_commit?.Task ?? throw new InvalidOperationException("Replay receiver was not prepared."), 30, "replay commit");

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds, string operation)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task)
            throw new TimeoutException($"Timed out waiting for {operation}.");
        return await task;
    }
}

internal static class ReplayHostExchange
{
    private const int EventsPerBatch = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, HostReplayAttempt> Attempts = new();

    internal static void Register(INetGameService service)
    {
        service.RegisterMessageHandler<ReplayReadyMessage>(OnReady);
        service.RegisterMessageHandler<ReplayAbortMessage>(OnClientAbort);
    }

    private static void OnClientAbort(ReplayAbortMessage message, ulong senderId)
    {
        HostReplayAttempt? attempt;
        lock (Gate) Attempts.TryGetValue(senderId, out attempt);
        if (attempt == null || !attempt.Matches(message.epoch, message.generation, message.transferId)) return;
        ReconnectLog.Write($"Client {senderId} aborted replay epoch={message.epoch}, " +
                           $"generation={message.generation}, transfer={message.transferId}: {message.reason}");
        ReconnectCoordinator.RejectAttempt(senderId, message.generation, message.transferId, message.reason);
    }

    internal static async Task SendAsync(FrozenReplaySession session, ulong playerId, int generation,
        CancellationToken cancellationToken = default)
    {
        INetGameService service = RunManager.Instance.NetService;
        if (session.Anchor.Payload.Length > ReplayPayloadCodec.MaxAnchorBytes ||
            session.Events.Count > ReplayPayloadCodec.MaxReplayEvents)
            throw new InvalidDataException(
                $"Replay package exceeds limits: bytes={session.Anchor.Payload.Length}, events={session.Events.Count}.");
        EncodedReplayAnchor encoded = ReplayAnchorCompressionCodec.Encode(session.Anchor.Payload, new[] { playerId });
        int chunks = Math.Max(1, (encoded.Payload.Length + ReplayPayloadCodec.ChunkSize - 1) / ReplayPayloadCodec.ChunkSize);
        string transferId = $"reconnect:{session.Anchor.Epoch}:{generation}:{Guid.NewGuid():N}";
        var attempt = new HostReplayAttempt(playerId, generation, transferId, session);
        lock (Gate)
        {
            if (Attempts.TryGetValue(playerId, out HostReplayAttempt? previous))
            {
                if (previous.ReplayInFlight)
                {
                    attempt.Cancellation.Dispose();
                    throw new InvalidOperationException($"Reconnect replay for {playerId} is still active.");
                }
                Attempts.Remove(playerId);
                previous.Cancel("Superseded by a new reconnect attempt.");
                previous.Cancellation.Dispose();
            }
            Attempts[playerId] = attempt;
        }
        try
        {
            service.SendMessage(new ReplayManifestMessage
            {
                protocolVersion = CombatReconnectMod.ProtocolVersion,
                messageTableHash = MultiplayerTypeTableIdentity.MessageHash,
                actionTableHash = MultiplayerTypeTableIdentity.ActionHash,
                epoch = session.Anchor.Epoch, roundNumber = session.Anchor.RoundNumber,
                anchorBytes = session.Anchor.Payload.Length, encodedAnchorBytes = encoded.Payload.Length, anchorChunks = chunks,
                eventCount = session.Events.Count, anchorHash = session.Anchor.PayloadHash,
                anchorTransferId = transferId, anchorGeneration = generation,
                encodedAnchorHash = encoded.Hash, anchorCompression = encoded.Compression,
                finalStateHash = session.FinalStateHash,
                finalActionId = session.FinalExtendedState.nextActionId,
                finalHookId = session.FinalExtendedState.nextHookId,
                finalChecksumId = session.FinalExtendedState.nextChecksumId
            }, playerId);
            var key = new SidecarAnchorKey(SidecarAnchorKind.Replay, transferId, transferId,
                session.Anchor.Epoch, generation);
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, attempt.Cancellation.Token);
            await SidecarReplayTransport.SendAnchorAsync(service, playerId, key, encoded.Payload, encoded.Hash,
                transferCancellation.Token).WaitAsync(TimeSpan.FromSeconds(20), transferCancellation.Token);
            ReconnectLog.Write(
                $"Replay anchor transfer epoch={session.Anchor.Epoch}, generation={generation}, transfer={transferId}, " +
                $"raw={session.Anchor.Payload.Length}, encoded={encoded.Payload.Length}, compression={encoded.Compression}, parts={chunks}.");
            if (session.Events.Count == 0)
                service.SendMessage(new ReplayEventBatchMessage
                {
                    epoch = session.Anchor.Epoch, generation = generation, transferId = transferId,
                    firstSequence = 0, isLast = true, events = new()
                }, playerId);
            for (int i = 0; i < session.Events.Count; i += EventsPerBatch)
            {
                List<DeterministicInputEvent> batch = session.Events.Skip(i).Take(EventsPerBatch).ToList();
                service.SendMessage(new ReplayEventBatchMessage
                {
                    epoch = session.Anchor.Epoch, generation = generation, transferId = transferId,
                    firstSequence = i, isLast = i + batch.Count == session.Events.Count, events = batch
                }, playerId);
            }
            lock (Gate)
                if (Attempts.TryGetValue(playerId, out HostReplayAttempt? current) && ReferenceEquals(current, attempt))
                    attempt.PackageSent = true;
        }
        catch
        {
            CancelAttempt(playerId, generation, transferId, "Replay package transfer failed.");
            throw;
        }
    }

    private static void OnReady(ReplayReadyMessage message, ulong senderId)
    {
        HostReplayAttempt? attempt;
        lock (Gate)
        {
            if (!Attempts.TryGetValue(senderId, out attempt) ||
                !attempt.Matches(message.epoch, message.generation, message.transferId) ||
                attempt.Aborted || !attempt.PackageSent) return;
            if (attempt.ReplayInFlight || attempt.CommitSent) return;
            attempt.ReplayInFlight = true;
        }
        if (!message.success || message.stateHash != attempt.Session.FinalStateHash)
        {
            lock (Gate) attempt.ReplayInFlight = false;
            ReconnectCoordinator.RejectAttempt(senderId, message.generation, message.transferId,
                $"Client replay validation failed: {message.detail}; hash={message.stateHash}");
            return;
        }
        _ = CompleteHostReplay(attempt);
    }

    private static async Task CompleteHostReplay(HostReplayAttempt attempt)
    {
        try
        {
            FrozenReplaySession session = attempt.Session;
            string hostHash = await DeterministicReplayEngine.RestoreAndReplay(
                session.Anchor.CombatState, session.Anchor.ExtendedState, session.Events,
                session.FinalExtendedState.nextActionId, session.FinalExtendedState.nextHookId,
                session.FinalExtendedState.nextChecksumId, cancellationToken: attempt.Cancellation.Token);
            if (hostHash != session.FinalStateHash)
                throw new InvalidOperationException($"Host replay hash mismatch: expected {session.FinalStateHash}, got {hostHash}.");
            CombatManager.Instance.Pause();
            RunManager.Instance.ActionExecutor.Pause();
            lock (Gate)
            {
                if (!Attempts.TryGetValue(attempt.PlayerId, out HostReplayAttempt? current) ||
                    !ReferenceEquals(current, attempt) || attempt.Aborted) return;
                attempt.CommitSent = true;
            }
            RunManager.Instance.NetService.SendMessage(new ReplayCommitMessage
            {
                epoch = session.Anchor.Epoch, generation = attempt.Generation,
                transferId = attempt.TransferId, stateHash = hostHash
            }, attempt.PlayerId);
            ReconnectLog.Write($"Committed deterministic replay epoch={session.Anchor.Epoch}, " +
                               $"generation={attempt.Generation}, transfer={attempt.TransferId} for {attempt.PlayerId}.");
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Host deterministic replay failed: {ex}");
            if (IsCurrent(attempt))
                ReconnectCoordinator.RejectAttempt(attempt.PlayerId, attempt.Generation, attempt.TransferId, ex.Message);
        }
        finally
        {
            lock (Gate)
            {
                attempt.ReplayInFlight = false;
                if (attempt.Aborted && Attempts.TryGetValue(attempt.PlayerId, out HostReplayAttempt? current) &&
                    ReferenceEquals(current, attempt))
                {
                    Attempts.Remove(attempt.PlayerId);
                    attempt.Cancellation.Dispose();
                }
            }
        }
    }

    internal static void CancelAttempt(ulong playerId, int generation, string transferId, string reason)
    {
        HostReplayAttempt? attempt;
        lock (Gate)
        {
            if (!Attempts.TryGetValue(playerId, out attempt) ||
                attempt.Generation != generation || attempt.TransferId != transferId) return;
            attempt.Cancel(reason);
            if (!attempt.ReplayInFlight)
            {
                Attempts.Remove(playerId);
                attempt.Cancellation.Dispose();
            }
        }
    }

    internal static void CancelAttempt(ulong playerId, string reason)
    {
        lock (Gate)
        {
            if (!Attempts.TryGetValue(playerId, out HostReplayAttempt? attempt)) return;
            attempt.Cancel(reason);
            if (!attempt.ReplayInFlight)
            {
                Attempts.Remove(playerId);
                attempt.Cancellation.Dispose();
            }
        }
    }

    internal static bool TryGetAttemptIdentity(ulong playerId, out ulong epoch, out int generation, out string transferId)
    {
        lock (Gate)
        {
            if (Attempts.TryGetValue(playerId, out HostReplayAttempt? attempt))
            {
                epoch = attempt.Session.Anchor.Epoch;
                generation = attempt.Generation;
                transferId = attempt.TransferId;
                return true;
            }
        }
        epoch = 0; generation = 0; transferId = string.Empty;
        return false;
    }

    internal static bool TryCompleteAttempt(ulong playerId, int generation)
    {
        lock (Gate)
        {
            if (!Attempts.TryGetValue(playerId, out HostReplayAttempt? attempt) ||
                attempt.Generation != generation || !attempt.CommitSent || attempt.Aborted) return false;
            Attempts.Remove(playerId);
            attempt.Cancellation.Dispose();
            return true;
        }
    }

    internal static bool CanCompleteAttempt(ulong playerId, int generation)
    {
        lock (Gate)
            return Attempts.TryGetValue(playerId, out HostReplayAttempt? attempt) &&
                   attempt.Generation == generation && attempt.CommitSent && !attempt.Aborted;
    }

    internal static async Task AwaitNoActiveHostReplay(ulong playerId, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (Gate)
                if (!Attempts.TryGetValue(playerId, out HostReplayAttempt? attempt) || !attempt.ReplayInFlight) return;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken);
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            foreach (HostReplayAttempt attempt in Attempts.Values)
            {
                attempt.Cancel("Replay host exchange reset.");
                if (!attempt.ReplayInFlight) attempt.Cancellation.Dispose();
            }
            Attempts.Clear();
        }
    }

    private static bool IsCurrent(HostReplayAttempt attempt)
    {
        lock (Gate)
            return Attempts.TryGetValue(attempt.PlayerId, out HostReplayAttempt? current) &&
                   ReferenceEquals(current, attempt) && !attempt.Aborted;
    }

    private sealed class HostReplayAttempt
    {
        internal HostReplayAttempt(ulong playerId, int generation, string transferId, FrozenReplaySession session)
        { PlayerId = playerId; Generation = generation; TransferId = transferId; Session = session; }

        internal ulong PlayerId { get; }
        internal int Generation { get; }
        internal string TransferId { get; }
        internal FrozenReplaySession Session { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal bool PackageSent { get; set; }
        internal bool ReplayInFlight { get; set; }
        internal bool CommitSent { get; set; }
        internal bool Aborted { get; private set; }

        internal bool Matches(ulong epoch, int generation, string transferId)
            => Session.Anchor.Epoch == epoch && Generation == generation && TransferId == transferId;

        internal void Cancel(string reason)
        {
            if (Aborted) return;
            Aborted = true;
            Cancellation.Cancel();
            ReconnectLog.Write($"Cancelled reconnect replay attempt player={PlayerId}, generation={Generation}, " +
                               $"transfer={TransferId}: {reason}");
        }
    }
}

internal static class DeterministicReplayEngine
{
    private static readonly PropertyInfo ChecksumNextId = AccessTools.Property(typeof(ChecksumTracker), nameof(ChecksumTracker.NextId));
    private static readonly FieldInfo TrackedChecksums = AccessTools.Field(typeof(ChecksumTracker), "_checksums");
    private static readonly FieldInfo QueuedRemoteChecksums = AccessTools.Field(typeof(ChecksumTracker), "_queuedRemoteChecksums");
    private static readonly FieldInfo HookActions = AccessTools.Field(typeof(ActionQueueSynchronizer), "_hookActions");
    internal static bool IsReplaying { get; private set; }
    internal static bool ShowReplayVisuals { get; private set; }

    internal static async Task<string> RestoreAndReplay(NetFullCombatState anchor, ExtendedReconnectMessage extended,
        IReadOnlyList<DeterministicInputEvent> events, uint finalActionId, uint finalHookId, uint finalChecksumId,
        bool showReplayVisuals = false, CancellationToken cancellationToken = default,
        int? expectedFinalRoundNumber = null, CombatSide? expectedFinalSide = null,
        IReadOnlyDictionary<ulong, int>? expectedPlayerTurns = null)
    {
        if (IsReplaying) throw new InvalidOperationException("A deterministic replay is already active.");
        IsReplaying = true;
        ShowReplayVisuals = showReplayVisuals;
        double oldScale = Engine.TimeScale;
        CombatReplayWriter writer = RunManager.Instance.CombatReplayWriter;
        bool writerEnabled = writer.IsEnabled;
        bool checksumEnabled = RunManager.Instance.ChecksumTracker.IsEnabled;
        ulong? localNetId = LocalContext.NetId;
        ReplayChoiceStateMachine.Reset();
        try
        {
            Engine.TimeScale = 10.0;
            writer.IsEnabled = false;
            RunManager.Instance.ChecksumTracker.IsEnabled = false;
            ResetChecksumTimeline(RunManager.Instance.ChecksumTracker, extended.nextChecksumId);
            CombatStateRestorer.Restore(anchor, extended);
            RunState run = RunManager.Instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("Run state is unavailable during replay.");
            SceneTree tree = (SceneTree)Engine.GetMainLoop();
            var replayActions = new Dictionary<uint, GameAction>();

            List<DeterministicInputEvent> orderedEvents = events.OrderBy(e => e.sequence).ToList();
            for (int eventIndex = 0; eventIndex < orderedEvents.Count; eventIndex++)
            {
                DeterministicInputEvent input = orderedEvents[eventIndex];
                cancellationToken.ThrowIfCancellationRequested();
                foreach (GameAction trackedAction in replayActions.Values.Distinct())
                    ReplayChoiceStateMachine.ObserveAction(trackedAction);
                CombatReplayEvent evt = input.replayEvent;
                uint? followingHookId = eventIndex + 1 < orderedEvents.Count &&
                    orderedEvents[eventIndex + 1].replayEvent.eventType == CombatReplayEventType.HookAction
                        ? orderedEvents[eventIndex + 1].replayEvent.hookId
                        : null;

                switch (evt.eventType)
                {
                    case CombatReplayEventType.GameAction:
                    {
                        if (!evt.playerId.HasValue || evt.action == null)
                            throw new InvalidDataException($"Replay action event {input.sequence} is missing action data.");
                        Player player = run.GetPlayer(evt.playerId.Value)
                            ?? throw new InvalidOperationException(
                                $"Replay event {input.sequence} references missing player {evt.playerId}.");

                        while (CombatManager.Instance.EndingPlayerTurnPhaseOne ||
                               CombatManager.Instance.EndingPlayerTurnPhaseTwo)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await tree.Root.AwaitProcessFrame();
                        }
                        if (evt.action is NetPlayCardAction playCardAction)
                            CombatCardReplayMapper.EnsureMapping(playCardAction, player, input.sequence);

                        GameAction action = evt.action.ToGameAction(player);
                        if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                        {
                            while (CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                await tree.Root.AwaitProcessFrame();
                            }
                        }

                        bool turnBoundary = action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction;
                        ReconnectLog.Write(
                            $"Replay dispatch seq={input.sequence}, event=GameAction, action={action.GetType().Name}, " +
                            $"owner={evt.playerId}, identity={localNetId}.");
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                        if (action.Id.HasValue) replayActions[action.Id.Value] = action;
                        await AwaitActionPauseOrCompletion(action, tree, cancellationToken, followingHookId);
                        ReplayChoiceStateMachine.ObserveAction(action);

                        if (turnBoundary)
                        {
                            // The enemy-turn transition continues after the Ready action itself
                            // completes. Wait with the real local identity restored so draw/UI
                            // logic can never resolve another player's hand as local.
                            await SyntheticStartTurnTracker.AwaitAll(
                                $"replay boundary after {action.GetType().Name}", cancellationToken);
                            await AwaitCombatQuiescence(
                                $"replay boundary after {action.GetType().Name}", cancellationToken);
                            if (action is ReadyToBeginEnemyTurnAction)
                                await AwaitLocalHandStability(
                                    $"replay boundary after {action.GetType().Name}", cancellationToken);
                        }
                        break;
                    }

                    case CombatReplayEventType.HookAction:
                    {
                        if (!evt.playerId.HasValue || !evt.hookId.HasValue || !evt.gameActionType.HasValue)
                            throw new InvalidDataException($"Replay hook event {input.sequence} is missing hook data.");
                        GenericHookGameAction hookAction =
                            RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                                evt.hookId.Value, evt.playerId.Value, evt.gameActionType.Value);
                        ReconnectLog.Write(
                            $"Replay dispatch seq={input.sequence}, event=HookAction, hook={evt.hookId}, " +
                            $"action={hookAction.GetType().Name}, owner={evt.playerId}, identity={localNetId}.");
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(hookAction);
                        if (hookAction.Id.HasValue) replayActions[hookAction.Id.Value] = hookAction;
                        await AwaitActionPauseOrCompletion(hookAction, tree, cancellationToken, followingHookId);
                        ReplayChoiceStateMachine.ObserveAction(hookAction);
                        break;
                    }

                    case CombatReplayEventType.ResumeAction:
                        if (!evt.actionId.HasValue)
                            throw new InvalidDataException($"Replay resume event {input.sequence} is missing an action id.");
                        if (!replayActions.TryGetValue(evt.actionId.Value, out GameAction? resumedAction))
                            throw new InvalidOperationException(
                                $"Replay resume event {input.sequence} references unknown action {evt.actionId.Value}.");
                        ReplayChoiceStateMachine.RequireReadyToResume(resumedAction, input.sequence);
                        ReconnectLog.Write(
                            $"Replay dispatch seq={input.sequence}, event=ResumeAction, actionId={evt.actionId}, " +
                            $"action={resumedAction.GetType().Name}, identity={localNetId}.");
                        RunManager.Instance.ActionQueueSet.ResumeActionWithoutSynchronizing(evt.actionId.Value);
                        if (resumedAction.Id.HasValue) replayActions[resumedAction.Id.Value] = resumedAction;
                        await AwaitActionPauseOrCompletion(resumedAction, tree, cancellationToken, followingHookId);
                        ReplayChoiceStateMachine.ObserveAction(resumedAction);
                        break;

                    case CombatReplayEventType.PlayerChoice:
                    {
                        if (!evt.playerId.HasValue || !evt.choiceId.HasValue || !evt.playerChoiceResult.HasValue)
                            throw new InvalidDataException($"Replay choice event {input.sequence} is missing choice data.");
                        Player player = run.GetPlayer(evt.playerId.Value)
                            ?? throw new InvalidOperationException(
                                $"Replay event {input.sequence} references missing player {evt.playerId}.");
                        await ReplayChoiceStateMachine.InjectAndAwaitConsumption(
                            player, evt.choiceId.Value, evt.playerChoiceResult.Value, input.sequence,
                            tree, cancellationToken);
                        ReconnectLog.Write(
                            $"Replay dispatch seq={input.sequence}, event=PlayerChoice, owner={evt.playerId}, " +
                            $"choice={evt.choiceId}, result={evt.playerChoiceResult}.");
                        break;
                    }

                    default:
                        throw new InvalidDataException(
                            $"Replay event {input.sequence} has unsupported type {evt.eventType}.");
                }
            }

            await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(cancellationToken);
            await SyntheticStartTurnTracker.AwaitAll("post-event replay", cancellationToken);
            await AwaitCombatQuiescence(
                "post-event replay", cancellationToken, expectedFinalRoundNumber, expectedFinalSide);

            if (RejoinLoader.IsLoading)
            {
                await RejoinSyntheticTurnGuard.AwaitQuiescence("post-event replay");
            }
            await AwaitLocalHandStability("post-event replay", cancellationToken);
            ReplayVisualCleanup.ClearPlayedCards();
            ReplayChoiceStateMachine.RequireNoPendingChoices();
            ValidateFinalState(localNetId, expectedFinalRoundNumber, expectedFinalSide, expectedPlayerTurns);

            uint actualActionId = RunManager.Instance.ActionQueueSet.NextActionId;
            if (actualActionId != finalActionId)
                throw new InvalidOperationException(
                    $"Replay counter mismatch: action {actualActionId}/{finalActionId}.");
            RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(finalHookId);
            ChecksumNextId.SetValue(RunManager.Instance.ChecksumTracker, finalChecksumId);
            ClientRejoinResponseMessage replayed = RunManager.Instance.GetRejoinMessage();
            if (replayed.combatState == null)
                throw new InvalidOperationException("Combat ended while validating replay.");
            ExtendedReconnectMessage replayedExtended = ExtendedReconnectCapture.Capture();
            ReplayPayloadCodec.LogFingerprint("replay-final", replayed.combatState, replayedExtended);
            return ReplayPayloadCodec.Hash(ReplayPayloadCodec.Serialize(replayed.combatState, replayedExtended));
        }
        finally
        {
            RunManager.Instance.ChecksumTracker.IsEnabled = checksumEnabled;
            writer.IsEnabled = writerEnabled;
            Engine.TimeScale = oldScale;
            ShowReplayVisuals = false;
            IsReplaying = false;
            ReplayChoiceStateMachine.Reset();
        }
    }

    private static async Task AwaitActionPauseOrCompletion(
        GameAction action, SceneTree tree, CancellationToken cancellationToken, uint? followingHookId = null)
    {
        const int maxFrames = 900;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.CompletionTask.IsFaulted || action.CompletionTask.IsCanceled)
                await action.CompletionTask;
            if (action.CompletionTask.IsCompletedSuccessfully ||
                action.State == GameActionState.GatheringPlayerChoice)
                return;
            if (followingHookId.HasValue && action.State == GameActionState.Executing &&
                HasGeneratedHookAction(followingHookId.Value))
            {
                ReconnectLog.Write(
                    $"Replay action {action.Id} yielded to following hook {followingHookId.Value} while still executing.");
                return;
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException(
            $"Replay action did not finish or pause for a saved choice: {action.GetType().Name}, state={action.State}.");
    }

    private static bool HasGeneratedHookAction(uint hookId)
        => HookActions.GetValue(RunManager.Instance.ActionQueueSynchronizer) is IEnumerable actions &&
           actions.Cast<object>().Any(action => action is GenericHookGameAction hook && hook.HookId == hookId);

    internal static async Task AwaitCombatQuiescence(string phase, CancellationToken cancellationToken,
        int? expectedRoundNumber = null, CombatSide? expectedSide = null, int maxFrames = 900)
    {
        const int stableFramesRequired = 5;
        int stableFrames = 0;
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        for (int frame = 0; frame < maxFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Task queue = RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            bool targetReached = state != null &&
                (!expectedRoundNumber.HasValue || state.RoundNumber == expectedRoundNumber.Value) &&
                (!expectedSide.HasValue || state.CurrentSide == expectedSide.Value);
            bool stable = queue.IsCompletedSuccessfully && targetReached &&
                !CombatManager.Instance.EndingPlayerTurnPhaseOne &&
                !CombatManager.Instance.EndingPlayerTurnPhaseTwo;
            stableFrames = stable ? stableFrames + 1 : 0;
            if (stableFrames >= stableFramesRequired)
            {
                ReconnectLog.Write(
                    $"Combat replay quiescent during {phase}: round={state!.RoundNumber}, side={state.CurrentSide}.");
                return;
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        CombatState? current = CombatManager.Instance.DebugOnlyGetState();
        throw new TimeoutException(
            $"Combat did not become quiescent during {phase}; " +
            $"round={current?.RoundNumber}, side={current?.CurrentSide}, " +
            $"expected={expectedRoundNumber}/{expectedSide}.");
    }

    internal static async Task AwaitLocalHandStability(string phase, CancellationToken cancellationToken,
        int maxFrames = 300)
    {
        const int stableFramesRequired = 5;
        int stableFrames = 0;
        List<CardModel>? previousCards = null;
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        for (int frame = 0; frame < maxFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? localPlayer = LocalContext.NetId is { } localId
                ? state?.Players.FirstOrDefault(player => player.NetId == localId)
                : null;
            NPlayerHand? hand = NPlayerHand.Instance;
            List<CardModel>? stateCards = localPlayer?.PlayerCombatState?.Hand.Cards.ToList();
            List<CardModel>? uiCards = hand?.ActiveHolders
                .Select(holder => holder.CardNode?.Model)
                .OfType<CardModel>()
                .ToList();
            bool unchanged = stateCards != null && previousCards != null &&
                stateCards.SequenceEqual(previousCards);
            bool matched = stateCards != null && uiCards != null &&
                stateCards.Count == uiCards.Count &&
                stateCards.All(uiCards.Contains) &&
                uiCards.All(card => card.Owner == localPlayer);
            stableFrames = unchanged && matched ? stableFrames + 1 : 0;
            previousCards = stateCards;
            if (stableFrames >= stableFramesRequired)
            {
                ReconnectLog.Write(
                    $"Local hand stable during {phase}: player={localPlayer!.NetId}, cards={stateCards!.Count}.");
                return;
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        CombatState? current = CombatManager.Instance.DebugOnlyGetState();
        Player? me = LocalContext.NetId is { } netId
            ? current?.Players.FirstOrDefault(player => player.NetId == netId)
            : null;
        int stateCount = me?.PlayerCombatState?.Hand.Cards.Count ?? -1;
        int uiCount = NPlayerHand.Instance?.ActiveHolders.Count() ?? -1;
        throw new TimeoutException(
            $"Local hand did not stabilize during {phase}: state={stateCount}, ui={uiCount}.");
    }

    private static void ResetChecksumTimeline(ChecksumTracker tracker, uint nextId)
    {
        ((IList)TrackedChecksums.GetValue(tracker)!).Clear();
        ((IList)QueuedRemoteChecksums.GetValue(tracker)!).Clear();
        ChecksumNextId.SetValue(tracker, nextId);
        ReconnectLog.Write($"Reset checksum timeline at rewind boundary; next={nextId}.");
    }

    internal static string CaptureCurrentStateHash()
    {
        ClientRejoinResponseMessage current = RunManager.Instance.GetRejoinMessage();
        if (current.combatState == null) throw new InvalidOperationException("Combat ended while validating replay.");
        byte[] payload = ReplayPayloadCodec.Serialize(current.combatState, ExtendedReconnectCapture.Capture());
        return ReplayPayloadCodec.Hash(payload);
    }

    internal static void ValidateFinalState(ulong? expectedLocalNetId, int? expectedRoundNumber,
        CombatSide? expectedSide, IReadOnlyDictionary<ulong, int>? expectedPlayerTurns)
    {
        if (!expectedLocalNetId.HasValue || LocalContext.NetId != expectedLocalNetId)
            throw new InvalidOperationException(
                $"Local identity changed during replay: expected={expectedLocalNetId}, actual={LocalContext.NetId}.");
        CombatState state = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state is unavailable during final validation.");
        if (expectedRoundNumber.HasValue && state.RoundNumber != expectedRoundNumber.Value)
            throw new InvalidOperationException(
                $"Final round mismatch: expected={expectedRoundNumber}, actual={state.RoundNumber}.");
        if (expectedSide.HasValue && state.CurrentSide != expectedSide.Value)
            throw new InvalidOperationException(
                $"Final side mismatch: expected={expectedSide}, actual={state.CurrentSide}.");
        if (expectedPlayerTurns != null)
        {
            foreach ((ulong playerId, int expectedTurn) in expectedPlayerTurns)
            {
                Player player = state.Players.FirstOrDefault(candidate => candidate.NetId == playerId)
                    ?? throw new InvalidOperationException($"Final validation is missing player {playerId}.");
                int actualTurn = player.PlayerCombatState?.TurnNumber
                    ?? throw new InvalidOperationException($"Player {playerId} has no combat state.");
                if (actualTurn != expectedTurn)
                    throw new InvalidOperationException(
                        $"Player {playerId} turn mismatch: expected={expectedTurn}, actual={actualTurn}.");
            }
        }

        Player localPlayer = state.Players.FirstOrDefault(player => player.NetId == expectedLocalNetId.Value)
            ?? throw new InvalidOperationException($"Local player {expectedLocalNetId} is absent from combat.");
        NPlayerHand hand = NPlayerHand.Instance
            ?? throw new InvalidOperationException("Local hand node is unavailable during final validation.");
        List<CardModel> expectedCards = localPlayer.PlayerCombatState?.Hand.Cards.ToList()
            ?? throw new InvalidOperationException("Local player hand state is unavailable.");
        List<CardModel> holderCards = hand.ActiveHolders
            .Select(holder => holder.CardNode?.Model)
            .OfType<CardModel>()
            .ToList();
        if (holderCards.Count != expectedCards.Count || holderCards.Any(card => card.Owner?.NetId != expectedLocalNetId) ||
            expectedCards.Any(card => !holderCards.Contains(card)))
            throw new InvalidOperationException(
                $"Local hand ownership mismatch: player={expectedLocalNetId}, state={expectedCards.Count}, ui={holderCards.Count}, " +
                $"owners=[{string.Join(",", holderCards.Select(card => card.Owner?.NetId))}].");
        ReconnectLog.Write(
            $"Final replay validation passed: local={expectedLocalNetId}, round={state.RoundNumber}, " +
            $"side={state.CurrentSide}, hand={holderCards.Count}, turns=[{string.Join(",", state.Players.Select(player => $"{player.NetId}:{player.PlayerCombatState?.TurnNumber}"))}].");
    }
}

internal static class ReplayChoiceStateMachine
{
    private sealed class PendingChoice
    {
        internal bool Injected;
        internal bool Consumed;
        internal uint ChoiceId;
    }

    private static readonly FieldInfo ReceivedChoices =
        AccessTools.Field(typeof(PlayerChoiceSynchronizer), "_receivedChoices");
    private static readonly Dictionary<GameAction, PendingChoice> Pending = new();

    internal static void Reset() => Pending.Clear();

    internal static void ObserveAction(GameAction action)
    {
        if (action.State == GameActionState.GatheringPlayerChoice)
        {
            if (!Pending.ContainsKey(action)) Pending[action] = new PendingChoice();
            return;
        }
        if (action.CompletionTask.IsCompleted) Pending.Remove(action);
    }

    internal static async Task InjectAndAwaitConsumption(Player player, uint choiceId,
        NetPlayerChoiceResult result, int sequence, SceneTree tree, CancellationToken cancellationToken)
    {
        List<KeyValuePair<GameAction, PendingChoice>> matches = Pending
            .Where(pair => pair.Key.OwnerId == player.NetId &&
                           pair.Key.State == GameActionState.GatheringPlayerChoice && !pair.Value.Injected)
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Replay choice event {sequence} expected one paused action for player {player.NetId}, found {matches.Count}.");

        GameAction action = matches[0].Key;
        PendingChoice pending = matches[0].Value;
        await AwaitCondition(
            () => HasReceivedChoiceEntry(player.NetId, choiceId), tree, cancellationToken,
            $"Replay choice event {sequence} did not find an active waiter for player {player.NetId}, choice {choiceId}.");
        pending.Injected = true;
        pending.ChoiceId = choiceId;
        RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(player, choiceId, result);
        await AwaitCondition(
            () => pending.Consumed, tree, cancellationToken,
            $"Replay choice event {sequence} was not consumed by action {action.GetType().Name} ({action.Id}).");
        if (action.State != GameActionState.GatheringPlayerChoice)
            throw new InvalidOperationException(
                $"Replay choice event {sequence} changed action {action.Id} to unexpected state {action.State} before ResumeAction.");
    }

    internal static void MarkChoiceConsumed(GameAction? action)
    {
        if (!DeterministicReplayEngine.IsReplaying || action == null || !Pending.TryGetValue(action, out PendingChoice? pending))
            return;
        if (!pending.Injected)
            throw new InvalidOperationException(
                $"Replay action {action.Id} consumed a choice before its recorded PlayerChoice event.");
        pending.Consumed = true;
        ReconnectLog.Write($"Replay choice consumed by action={action.GetType().Name}, actionId={action.Id}, choice={pending.ChoiceId}.");
    }

    internal static void RequireReadyToResume(GameAction action, int sequence)
    {
        if (!Pending.TryGetValue(action, out PendingChoice? pending) || !pending.Injected || !pending.Consumed ||
            action.State != GameActionState.GatheringPlayerChoice)
            throw new InvalidOperationException(
                $"Replay resume event {sequence} arrived before a saved choice was consumed by action {action.Id}; " +
                $"state={action.State}, tracked={pending != null}, injected={pending?.Injected}, consumed={pending?.Consumed}.");
        Pending.Remove(action);
    }

    internal static void RequireNoPendingChoices()
    {
        if (Pending.Count == 0) return;
        throw new InvalidOperationException(
            $"Replay ended with {Pending.Count} action(s) still waiting for saved player choices: " +
            string.Join(",", Pending.Keys.Select(action => $"{action.GetType().Name}:{action.Id}:{action.State}")));
    }

    private static bool HasReceivedChoiceEntry(ulong playerId, uint choiceId)
    {
        if (ReceivedChoices.GetValue(RunManager.Instance.PlayerChoiceSynchronizer) is not IEnumerable entries)
            return false;
        foreach (object entry in entries)
        {
            Type type = entry.GetType();
            ulong senderId = (ulong)AccessTools.Field(type, "senderId").GetValue(entry)!;
            uint receivedChoiceId = (uint)AccessTools.Field(type, "choiceId").GetValue(entry)!;
            if (senderId == playerId && receivedChoiceId == choiceId) return true;
        }
        return false;
    }

    private static async Task AwaitCondition(Func<bool> condition, SceneTree tree,
        CancellationToken cancellationToken, string timeoutMessage)
    {
        const int maxFrames = 300;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException(timeoutMessage);
    }
}

internal static class CombatCardReplayMapper
{
    private static readonly FieldInfo IdToCardField = AccessTools.Field(typeof(NetCombatCardDb), "_idToCard");
    private static readonly FieldInfo CardToIdField = AccessTools.Field(typeof(NetCombatCardDb), "_cardToId");
    private static readonly FieldInfo NextIdField = AccessTools.Field(typeof(NetCombatCardDb), "_nextId");

    internal static bool TryGetExistingCardId(CardModel card, out uint id)
    {
        var cardToId = (Dictionary<CardModel, uint>)CardToIdField.GetValue(NetCombatCardDb.Instance)!;
        return cardToId.TryGetValue(card, out id);
    }

    internal static void RegisterAuthoritativeCardId(CardModel card, uint id, string source)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        var idToCard = (Dictionary<uint, CardModel>)IdToCardField.GetValue(db)!;
        var cardToId = (Dictionary<CardModel, uint>)CardToIdField.GetValue(db)!;

        if (idToCard.TryGetValue(id, out CardModel? mapped) && !ReferenceEquals(mapped, card))
            throw new InvalidDataException(
                $"Cannot restore {source} combat-card ID {id}: it is already mapped to {mapped.Id}.");
        if (cardToId.TryGetValue(card, out uint mappedId) && mappedId != id)
            throw new InvalidDataException(
                $"Cannot restore {source} combat-card ID {id}: card {card.Id} is already mapped to {mappedId}.");

        uint nextId = (uint)NextIdField.GetValue(db)!;
        if (id >= nextId && id == uint.MaxValue)
            throw new InvalidDataException($"Cannot restore {source} combat-card ID {id}: no next ID is available.");
        idToCard[id] = card;
        cardToId[card] = id;
        if (id >= nextId)
            NextIdField.SetValue(db, id + 1);
        ReconnectLog.Write($"Restored authoritative combat-card mapping from {source}: id={id}, card={card.Id}.");
    }

    internal static void RestoreAnchorMappings(CombatState combat, ExtendedReconnectMessage extended)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        db.ClearCardsForTesting();
        var idToCard = (Dictionary<uint, CardModel>)IdToCardField.GetValue(db)!;
        var cardToId = (Dictionary<CardModel, uint>)CardToIdField.GetValue(db)!;

        foreach (ExtendedPlayerCardState savedPlayer in extended.playerCards)
        {
            Player player = combat.Players.FirstOrDefault(p => p.NetId == savedPlayer.playerId)
                ?? throw new InvalidOperationException($"Card anchor references unknown player {savedPlayer.playerId}.");
            PlayerCombatState state = player.PlayerCombatState
                ?? throw new InvalidOperationException($"Card anchor player {savedPlayer.playerId} has no combat state.");
            foreach (ExtendedCardState saved in savedPlayer.cards)
            {
                CardPile pile = state.AllPiles.FirstOrDefault(p => p.Type == saved.pileType)
                    ?? throw new InvalidOperationException($"Card anchor references missing {saved.pileType} pile.");
                if (saved.indexInPile < 0 || saved.indexInPile >= pile.Cards.Count)
                    throw new InvalidOperationException($"Card anchor index {saved.indexInPile} is invalid for {saved.pileType}.");
                CardModel card = pile.Cards[saved.indexInPile];
                if (idToCard.ContainsKey(saved.combatCardId))
                    throw new InvalidOperationException($"Card anchor contains duplicate combat ID {saved.combatCardId}.");
                idToCard[saved.combatCardId] = card;
                cardToId[card] = saved.combatCardId;
            }
        }

        uint nextId = idToCard.Count == 0 ? 0 : idToCard.Keys.Max() + 1;
        NextIdField.SetValue(db, nextId);
        ReconnectLog.Write($"Restored {idToCard.Count} authoritative combat-card ID mapping(s), next={nextId}.");
    }

    internal static void EnsureMapping(NetPlayCardAction action, Player player, int sequence)
    {
        uint recordedId = action.card.CombatCardIndex;
        NetCombatCardDb db = NetCombatCardDb.Instance;
        PlayerCombatState combatState = player.PlayerCombatState
            ?? throw new InvalidOperationException($"Replay card event {sequence} has no combat state for player {player.NetId}.");
        if (db.TryGetCard(recordedId, out CardModel? mapped) && IsValid(mapped, action, player, combatState))
            return;

        List<CardModel> matching = combatState.AllPiles.SelectMany(p => p.Cards)
            .Where(card => IsValid(card, action, player, combatState))
            .ToList();
        List<CardModel> handMatching = matching.Where(card => card.Pile?.Type == PileType.Hand).ToList();
        CardModel candidate;
        if (handMatching.Count == 1)
            candidate = handMatching[0];
        else if (handMatching.Count > 1)
            throw Ambiguous(sequence, recordedId, action, "hand", handMatching.Count);
        else if (matching.Count == 1)
            candidate = matching[0];
        else if (matching.Count == 0)
            throw new InvalidOperationException(
                $"Replay card event {sequence} cannot map combat ID {recordedId} ({action.modelId.Entry}) for player {player.NetId}.");
        else
            throw Ambiguous(sequence, recordedId, action, "all piles", matching.Count);

        var idToCard = (Dictionary<uint, CardModel>)IdToCardField.GetValue(db)!;
        var cardToId = (Dictionary<CardModel, uint>)CardToIdField.GetValue(db)!;
        if (idToCard.TryGetValue(recordedId, out CardModel? displaced) &&
            cardToId.TryGetValue(displaced, out uint displacedId) && displacedId == recordedId)
            cardToId.Remove(displaced);
        if (cardToId.TryGetValue(candidate, out uint previousId))
            idToCard.Remove(previousId);
        idToCard[recordedId] = candidate;
        cardToId[candidate] = recordedId;
        uint nextId = (uint)NextIdField.GetValue(db)!;
        if (recordedId >= nextId) NextIdField.SetValue(db, recordedId + 1);
        ReconnectLog.Write(
            $"Repaired replay combat-card mapping event={sequence}, id={recordedId}, card={candidate.Id.Entry}, pile={candidate.Pile?.Type}.");
    }

    private static bool IsValid(CardModel? card, NetPlayCardAction action, Player player,
        PlayerCombatState combatState)
        => card != null && card.Id == action.modelId && card.Owner?.NetId == player.NetId &&
            combatState.AllPiles.Any(pile => pile.Cards.Contains(card));

    private static InvalidOperationException Ambiguous(int sequence, uint id, NetPlayCardAction action,
        string scope, int count)
        => new($"Replay card event {sequence} has {count} ambiguous {scope} matches for combat ID {id} ({action.modelId.Entry}).");
}

internal static class RejoinSyntheticTurnGuard
{
    internal static bool IsActive { get; private set; }

    internal static void Begin() => IsActive = true;

    internal static void Complete()
    {
        IsActive = false;
        if (RunManager.Instance.IsInProgress)
            RunManager.Instance.ChecksumTracker.IsEnabled = true;
    }

    internal static async Task AwaitQuiescence(string phase)
    {
        const int requiredStableFrames = 3;
        const int maxFrames = 300;
        int stableFrames = 0;
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        for (int frame = 0; frame < maxFrames; frame++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            bool quiet = RunManager.Instance.ActionQueueSet.IsEmpty &&
                RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null;
            stableFrames = quiet ? stableFrames + 1 : 0;
            if (stableFrames >= requiredStableFrames)
            {
                ReconnectLog.Write($"Synthetic turn pipeline quiescent during {phase}.");
                return;
            }
        }
        throw new TimeoutException($"Synthetic turn pipeline did not become quiescent during {phase}.");
    }
}

[HarmonyPatch(typeof(ChecksumTracker), nameof(ChecksumTracker.GenerateChecksum),
    new[] { typeof(string), typeof(GameAction) })]
internal static class SuppressSyntheticTurnChecksumPatch
{
    private static bool Prefix(string context, GameAction? action, ref NetChecksumData __result)
    {
        if (!RejoinSyntheticTurnGuard.IsActive || action != null ||
            !context.StartsWith("After player turn start", StringComparison.Ordinal))
            return true;
        __result = default;
        ReconnectLog.Write("Suppressed synthetic turn-start checksum during deterministic rejoin.");
        return false;
    }
}
