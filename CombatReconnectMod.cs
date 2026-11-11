using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using STS2RitsuLib;

namespace CombatReconnect;

internal static class ReconnectLog
{
    private static readonly string Path = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "CombatReconnect.log");
    private static readonly object Gate = new();
    private const long MaxLogBytes = 5 * 1024 * 1024;

    internal static void StartSession(string protocol)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                RotateIfNeeded();
                string game = AssemblyInfo.BaseGame?.GetName().Version?.ToString() ?? "unknown";
                string ritsu = typeof(RitsuLibFramework).Assembly.GetName().Version?.ToString() ?? "unknown";
                System.IO.File.AppendAllText(
                    Path,
                    $"=== session {DateTimeOffset.Now:O} protocol={protocol} game={game} ritsulib={ritsu} ==={System.Environment.NewLine}");
            }
            catch { }
        }
    }

    internal static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                RotateIfNeeded();
                System.IO.File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{System.Environment.NewLine}");
            }
            catch { }
        }
        GD.Print($"[CombatReconnect] {message}");
    }

    private static void RotateIfNeeded()
    {
        if (System.IO.File.Exists(Path) && new System.IO.FileInfo(Path).Length >= MaxLogBytes)
            System.IO.File.Move(Path, Path + ".1", overwrite: true);
    }
}

internal static class MultiplayerTypeTableIdentity
{
    internal static string MessageHash { get; private set; } = string.Empty;
    internal static string ActionHash { get; private set; } = string.Empty;
    internal static int ActionCount { get; private set; }

    internal static void CaptureMessages()
    {
        if (MessageTypes.Count > byte.MaxValue + 1)
            throw new InvalidOperationException($"The multiplayer message table has {MessageTypes.Count} entries; the wire format supports at most 256.");
        MessageHash = HashTypes(Enumerable.Range(0, MessageTypes.Count)
            .Select(id => MessageTypes.TryGetMessageType(id, out Type? type) ? (id, type) : (id, null)));
    }

    internal static void CaptureActions()
    {
        var types = new List<(int Id, Type? Type)>();
        for (int id = 0; id <= byte.MaxValue && ActionTypes.TryGetActionType(id, out Type? type); id++)
            types.Add((id, type));
        ActionCount = types.Count;
        ActionHash = HashTypes(types);
    }

    private static string HashTypes(IEnumerable<(int Id, Type? Type)> entries)
    {
        Assembly combatAssembly = typeof(CombatReconnectMod).Assembly;
        var lines = new List<string>();
        foreach ((int id, Type? type) in entries)
        {
            if (type == null || !IsGameplayRelevant(type.Assembly, combatAssembly)) continue;
            Assembly assembly = type.Assembly;
            Mod? mod = null;
            AssemblyInfo.ModMap?.TryGetValue(assembly, out mod);
            lines.Add($"{id}|{type.FullName}|{mod?.manifest?.id}|{mod?.manifest?.version}|" +
                      $"{assembly.ManifestModule.ModuleVersionId:N}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static bool IsGameplayRelevant(Assembly assembly, Assembly combatAssembly)
    {
        if (assembly == AssemblyInfo.BaseGame || assembly == combatAssembly) return true;
        Mod? mod = null;
        return AssemblyInfo.ModMap?.TryGetValue(assembly, out mod) != true ||
               (mod?.manifest?.affectsGameplay ?? true);
    }
}

[HarmonyPatch(typeof(MessageTypes), nameof(MessageTypes.Initialize))]
internal static class MultiplayerMessageTypeInitializationPatch
{
    private static readonly FieldInfo ModTypesCache = AccessTools.Field(typeof(ReflectionHelper), "_modTypes");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix() => ModTypesCache.SetValue(null, null);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        MultiplayerTypeTableIdentity.CaptureMessages();
    }
}

[HarmonyPatch(typeof(ActionTypes), nameof(ActionTypes.Initialize))]
internal static class MultiplayerActionTypeInitializationPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        MultiplayerTypeTableIdentity.CaptureActions();
        ReconnectLog.Write(
            $"Initialized stable multiplayer type tables: messages={MessageTypes.Count}, actions={MultiplayerTypeTableIdentity.ActionCount}, " +
            $"InitialGameInfo={MessageTypes.TypeToId<InitialGameInfoMessage>()}, ReplayManifest={MessageTypes.TypeToId<ReplayManifestMessage>()}, " +
            $"RewindRequest={MessageTypes.TypeToId<RewindRequestMessage>()}, messageHash={MultiplayerTypeTableIdentity.MessageHash}, " +
            $"actionHash={MultiplayerTypeTableIdentity.ActionHash}.");
    }
}

[ModInitializer("Initialize")]
public static class CombatReconnectMod
{
    internal const string ProtocolVersion = "63-monster-runtime-state";

    public static void Initialize()
    {
        ReconnectLog.StartSession(ProtocolVersion);
        MemberInfo?[] required =
        {
            AccessTools.Method(typeof(JoinFlow), "Begin"),
            AccessTools.Method(typeof(RunLobby), "OnDisconnectedFromClientAsHost"),
            AccessTools.Method(typeof(RunLobby), "HandlePlayerRejoinedMessage"),
            AccessTools.Method(typeof(CombatManager), "StartTurn", new[] { typeof(Func<Task>) }),
            AccessTools.Method(typeof(CombatManager), "StartCombatInternal", Type.EmptyTypes),
            AccessTools.Field(typeof(ReflectionHelper), "_modTypes"),
            AccessTools.Field(typeof(MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplayWriter), "_replay"),
            AccessTools.Method(typeof(ActionQueueSet), nameof(ActionQueueSet.EnqueueWithoutSynchronizing)),
            AccessTools.Method(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice)),
            AccessTools.Method(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueueHookAction)),
            AccessTools.Method(AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardSelectCmd"), "ShouldSelectLocalCard"),
            AccessTools.Method(AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.RelicSelectCmd"), "ShouldSelectLocalRelic"),
            AccessTools.Method(typeof(NCardPlayQueue), "OnActionEnqueued", new[] { typeof(GameAction) }),
            AccessTools.Field(typeof(NetCombatCardDb), "_idToCard"),
            AccessTools.Field(typeof(NetCombatCardDb), "_cardToId"),
            AccessTools.Field(typeof(NetCombatCardDb), "_nextId")
        };
        if (required.Any(m => m == null))
        {
            ReconnectLog.Write("Disabled: this game build does not expose the expected native rejoin hooks.");
            return;
        }

        try
        {
            RitsuCompatibility.Verify();
            SidecarReplayTransport.Initialize();
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Disabled: required RitsuLib sidecar transport is unavailable: {ex}");
            return;
        }
        try
        {
            RewindSettingsStore.Initialize();
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"RitsuLib settings integration unavailable; core replay remains enabled: {ex.Message}");
        }
        new Harmony("com.combatreconnect.sts2").PatchAll(typeof(CombatReconnectMod).Assembly);
        ReconnectLog.Write($"Initialized (protocol {ProtocolVersion}). The game's native version/mod handshake is enforced.");
    }
}

internal static class ReconnectCoordinator
{
    private const int SafeBoundaryTimeoutSeconds = 15;
    private const int RewindHandoffTimeoutSeconds = 30;
    private static readonly HashSet<ulong> WaitingFor = new();
    private static readonly HashSet<ulong> SnapshotReady = new();
    private static readonly Dictionary<ulong, RunLobby> DeferredRequests = new();
    private static readonly Dictionary<ulong, string> ReplayFailures = new();
    private static readonly Dictionary<ulong, int> BoundaryGenerations = new();
    private static readonly MethodInfo NativeRejoinHandler =
        AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage");

    internal static void OnRemoteDisconnected(ulong playerId)
    {
        if (!CombatManager.Instance.IsInProgress) return;
        if (RunManager.Instance.DebugOnlyGetState()?.GetPlayer(playerId) == null) return;
        ReplayHostExchange.CancelAttempt(playerId, "Player disconnected during reconnect replay.");
        MultiplayerRewindCoordinator.OnPlayerDisconnected(playerId);
        WaitingFor.Add(playerId);
        SnapshotReady.Remove(playerId);
        DeferredRequests.Remove(playerId);
        ReplayFailures.Remove(playerId);
        int generation = BoundaryGenerations.GetValueOrDefault(playerId) + 1;
        BoundaryGenerations[playerId] = generation;
        if (RunManager.Instance.NetService is INetHostGameService host)
        {
            host.NetHost?.SetHostIsClosed(isClosed: false);
            ReconnectLog.Write("Steam lobby temporarily reopened for reserved players.");
        }
        CombatManager.Instance.Pause();
        RunManager.Instance.ActionExecutor.Pause();
        ReconnectLog.Write($"Reserved combat seat for player {playerId}; combat paused.");
        _ = MarkSnapshotReadyAtActionBoundary(playerId, generation);
    }

    private static async Task MarkSnapshotReadyAtActionBoundary(ulong playerId, int generation)
    {
        try
        {
            using (var handoffCancellation = new CancellationTokenSource(
                       TimeSpan.FromSeconds(RewindHandoffTimeoutSeconds)))
            {
                await MultiplayerRewindCoordinator.AwaitNoActiveHostOperation(handoffCancellation.Token);
                await ReplayHostExchange.AwaitNoActiveHostReplay(playerId, handoffCancellation.Token);
            }
            if (BoundaryGenerations.GetValueOrDefault(playerId) != generation) return;
            CombatManager.Instance.Pause();
            RunManager.Instance.ActionExecutor.Pause();
            ReconnectLog.Write($"Rewind transaction settled before freezing reconnect snapshot for {playerId}.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions()
                .WaitAsync(TimeSpan.FromSeconds(SafeBoundaryTimeoutSeconds));
            if (BoundaryGenerations.GetValueOrDefault(playerId) != generation) return;
            TurnReplayLedger.Freeze();
            SnapshotReady.Add(playerId);
            ReconnectLog.Write($"Safe deterministic replay boundary reached for player {playerId}.");
            if (DeferredRequests.Remove(playerId, out RunLobby? lobby))
                NativeRejoinHandler.Invoke(lobby, new object[] { default(ClientRejoinRequestMessage), playerId });
        }
        catch (Exception ex)
        {
            if (BoundaryGenerations.GetValueOrDefault(playerId) != generation) return;
            ReconnectLog.Write($"Could not reach snapshot boundary for player {playerId}: {ex}");
            ReplayFailures[playerId] = ex.Message;
            SnapshotReady.Add(playerId);
            if (DeferredRequests.Remove(playerId, out RunLobby? lobby))
                NativeRejoinHandler.Invoke(lobby, new object[] { default(ClientRejoinRequestMessage), playerId });
        }
    }

    internal static bool ShouldDeferRequest(RunLobby lobby, ulong playerId)
    {
        if (!WaitingFor.Contains(playerId) || SnapshotReady.Contains(playerId)) return false;
        DeferredRequests[playerId] = lobby;
        ReconnectLog.Write($"Rejoin request for {playerId} deferred until the current action finishes.");
        return true;
    }

    internal static bool TryGetReplayFailure(ulong playerId, out string reason)
        => ReplayFailures.TryGetValue(playerId, out reason!);

    internal static int GetBoundaryGeneration(ulong playerId)
        => BoundaryGenerations.GetValueOrDefault(playerId);

    internal static bool HasPendingReconnect => WaitingFor.Count != 0;

    internal static void Reset()
    {
        ReplayHostExchange.Reset();
        WaitingFor.Clear();
        SnapshotReady.Clear();
        DeferredRequests.Clear();
        ReplayFailures.Clear();
        BoundaryGenerations.Clear();
        SafeSnapshotBoundaryPatch.Reset();
        if (RunManager.Instance.NetService is INetHostGameService host)
            host.NetHost?.SetHostIsClosed(isClosed: true);
    }

    internal static void OnRestoreAcknowledged(ulong playerId)
    {
        int generation = BoundaryGenerations.GetValueOrDefault(playerId);
        if (!WaitingFor.Contains(playerId) || !ReplayHostExchange.TryCompleteAttempt(playerId, generation))
        {
            ReconnectLog.Write($"Ignored stale restore acknowledgement from {playerId}, generation={generation}.");
            return;
        }
        WaitingFor.Remove(playerId);
        SnapshotReady.Remove(playerId);
        DeferredRequests.Remove(playerId);
        ReplayFailures.Remove(playerId);
        BoundaryGenerations.Remove(playerId);
        ReconnectLog.Write($"Player {playerId} restored and acknowledged.");
        if (WaitingFor.Count == 0)
        {
            if (RunManager.Instance.NetService is INetHostGameService host)
            {
                host.NetHost?.SetHostIsClosed(isClosed: true);
                ReconnectLog.Write("Steam lobby closed again after successful restoration.");
            }
            CombatManager.Instance.Unpause();
            RunManager.Instance.ActionExecutor.Unpause();
            MultiplayerRewindCoordinator.BroadcastHistory();
            ReconnectLog.Write("All disconnected players restored; combat resumed.");
        }
    }

    internal static bool CanAcceptRestoreAcknowledgement(ulong playerId)
    {
        int generation = BoundaryGenerations.GetValueOrDefault(playerId);
        return WaitingFor.Contains(playerId) && ReplayHostExchange.CanCompleteAttempt(playerId, generation);
    }

    internal static bool IsWaitingFor(ulong playerId) => WaitingFor.Contains(playerId);

    internal static void RejectAttempt(ulong playerId, int generation, string transferId, string reason)
    {
        if (!WaitingFor.Contains(playerId) || BoundaryGenerations.GetValueOrDefault(playerId) != generation) return;
        ReplayHostExchange.CancelAttempt(playerId, generation, transferId, reason);
        ReconnectLog.Write($"Rejected active reconnect attempt player={playerId}, generation={generation}, " +
                           $"transfer={transferId}: {reason}");
        if (RunManager.Instance.NetService is INetHostGameService host)
            host.DisconnectClient(playerId, NetError.InternalError);
    }
}

[HarmonyPatch(typeof(RunLobby), "HandleClientRejoinRequestMessage",
    new[] { typeof(ClientRejoinRequestMessage), typeof(ulong) })]
internal static class SafeSnapshotBoundaryPatch
{
    private static readonly FieldInfo QueuedRemoteChecksums =
        AccessTools.Field(typeof(ChecksumTracker), "_queuedRemoteChecksums");
    private static readonly MethodInfo NativeHandler =
        AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage");
    private static readonly object TransferGate = new();
    private static readonly HashSet<ulong> ApprovedNativeRequests = new();
    private static readonly HashSet<ulong> TransfersInFlight = new();

    internal static void Reset()
    {
        lock (TransferGate)
        {
            ApprovedNativeRequests.Clear();
            TransfersInFlight.Clear();
        }
    }

    private static bool Prefix(RunLobby __instance, ulong senderId)
    {
        lock (TransferGate)
            if (ApprovedNativeRequests.Remove(senderId)) return true;
        if (ReconnectCoordinator.ShouldDeferRequest(__instance, senderId)) return false;
        if (ReconnectCoordinator.TryGetReplayFailure(senderId, out string failure))
        {
            AbortReconnect(senderId, failure);
            return false;
        }
        lock (TransferGate)
            if (!TransfersInFlight.Add(senderId)) return false;
        _ = TransferAnchorThenContinue(__instance, senderId);
        return false;
    }

    private static async Task TransferAnchorThenContinue(RunLobby lobby, ulong senderId)
    {
        int generation = ReconnectCoordinator.GetBoundaryGeneration(senderId);
        try
        {
            // Checksums queued by an earlier connection generation must never be compared
            // with actions from the replacement connection, even if their numeric IDs match.
            ((IList)QueuedRemoteChecksums.GetValue(RunManager.Instance.ChecksumTracker)!).Clear();
            ReconnectLog.Write("Cleared queued checksums from previous connection generation.");
            FrozenReplaySession session = TurnReplayLedger.Frozen
                ?? throw new InvalidOperationException("Reconnect requested without a frozen turn replay session.");
            SidecarReplayTransport.PrepareHostPeer(RunManager.Instance.NetService, senderId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReplayHostExchange.SendAsync(session, senderId, generation, timeout.Token);
            var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SidecarReplayTransport.Dispatch(() =>
            {
                try
                {
                    lock (TransferGate) ApprovedNativeRequests.Add(senderId);
                    NativeHandler.Invoke(lobby, new object[] { default(ClientRejoinRequestMessage), senderId });
                    continued.TrySetResult();
                }
                catch (Exception ex) { continued.TrySetException(ex); }
            });
            await continued.Task;
        }
        catch (Exception ex)
        {
            if (ReconnectCoordinator.GetBoundaryGeneration(senderId) == generation)
                AbortReconnect(senderId, ex.Message);
        }
        finally { lock (TransferGate) TransfersInFlight.Remove(senderId); }
    }

    private static void AbortReconnect(ulong senderId, string reason)
    {
        ReconnectLog.Write($"Rejected reconnect attempt for {senderId}: {reason}");
        ulong epoch = 0;
        int generation = ReconnectCoordinator.GetBoundaryGeneration(senderId);
        string transferId = string.Empty;
        if (ReplayHostExchange.TryGetAttemptIdentity(senderId, out ulong activeEpoch,
                out int activeGeneration, out string activeTransferId))
        {
            epoch = activeEpoch;
            generation = activeGeneration;
            transferId = activeTransferId;
            ReplayHostExchange.CancelAttempt(senderId, generation, transferId, reason);
        }
        try
        {
            RunManager.Instance.NetService.SendMessage(new ReplayAbortMessage
            {
                epoch = epoch, generation = generation, transferId = transferId, reason = reason
            }, senderId);
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Could not send reconnect abort to {senderId}: {ex.Message}");
        }
        finally
        {
            if (RunManager.Instance.NetService is INetHostGameService host)
                host.DisconnectClient(senderId, NetError.InternalError);
        }
    }

    private static Exception? Finalizer(Exception? __exception, ulong senderId)
    {
        if (__exception == null) return null;
        AbortReconnect(senderId, __exception.Message);
        return null;
    }
}

[HarmonyPatch(typeof(RunLobby), "OnDisconnectedFromClientAsHost",
    new[] { typeof(ulong), typeof(NetErrorInfo) })]
internal static class HostDisconnectPatch
{
    private static void Postfix(ulong playerId) => ReconnectCoordinator.OnRemoteDisconnected(playerId);
}

[HarmonyPatch(typeof(RunLobby), "HandlePlayerRejoinedMessage",
    new[] { typeof(PlayerRejoinedMessage), typeof(ulong) })]
internal static class RestoreAcknowledgementPatch
{
    private static bool Prefix(PlayerRejoinedMessage message, ulong __1)
        => !ReconnectCoordinator.IsWaitingFor(__1) ||
           (__1 == message.playerId && ReconnectCoordinator.CanAcceptRestoreAcknowledgement(message.playerId));

    private static void Postfix(PlayerRejoinedMessage message, ulong __1)
    {
        // The reconnecting client sends this only after its scene and snapshot are ready.
        if (__1 == message.playerId)
            ReconnectCoordinator.OnRestoreAcknowledged(message.playerId);
    }
}

[HarmonyPatch(typeof(JoinFlow), "Begin",
    new[] { typeof(IClientConnectionInitializer), typeof(SceneTree) })]
internal static class JoinFlowPatch
{
    private static void Postfix(JoinFlow __instance, ref Task<JoinResult> __result)
        => __result = CompleteNativeRejoin(__instance, __result);

    private static async Task<JoinResult> CompleteNativeRejoin(JoinFlow flow, Task<JoinResult> original)
    {
        JoinResult result;
        try
        {
            result = await original;
        }
        catch
        {
            ReconnectDiscovery.OnJoinFailed();
            throw;
        }
        if (result.sessionState != RunSessionState.Running || result.rejoinResponse == null)
        {
            ReconnectDiscovery.OnJoinSucceeded();
            return result;
        }

        ReconnectLog.Write("Native rejoin response received; rebuilding from the shared turn anchor.");
        ReceivedReplayPackage? package = null;
        try
        {
            package = await ReplayClientExchange.WaitForPackage();
            string stateHash = await RejoinLoader.Load(flow.NetService, result.rejoinResponse.Value, package);
            bool success = stateHash == package.Manifest.finalStateHash;
            flow.NetService.SendMessage(new ReplayReadyMessage
            {
                epoch = package.Manifest.epoch, generation = package.Manifest.anchorGeneration,
                transferId = package.Manifest.anchorTransferId, success = success, stateHash = stateHash,
                detail = success ? string.Empty : $"expected {package.Manifest.finalStateHash}"
            });
            ReplayCommitMessage commit = await ReplayClientExchange.WaitForCommit();
            if (commit.stateHash != stateHash)
                throw new InvalidOperationException($"Replay commit hash mismatch: local={stateHash}, host={commit.stateHash}.");

            // Delayed synthetic StartTurn continuations have historically fired after
            // LoadRun appeared complete. Require another stable window and validate the
            // actual state again before acknowledging restoration.
            CombatManager.Instance.Unpause();
            RunManager.Instance.ActionExecutor.Unpause();
            await RejoinSyntheticTurnGuard.AwaitQuiescence("post-commit validation");
            CombatManager.Instance.Pause();
            RunManager.Instance.ActionExecutor.Pause();
            string postCommitHash = DeterministicReplayEngine.CaptureCurrentStateHash();
            if (postCommitHash != commit.stateHash)
                throw new InvalidOperationException(
                    $"State changed after replay commit: committed={commit.stateHash}, current={postCommitHash}.");
            RejoinSyntheticTurnGuard.Complete();
            CombatManager.Instance.Unpause();
            RunManager.Instance.ActionExecutor.Unpause();
        }
        catch (Exception ex)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                CombatManager.Instance.Pause();
                RunManager.Instance.ActionExecutor.Pause();
            }
            if (flow.NetService.IsConnected)
                ReplayClientExchange.SendAbort(ex.ToString());
            RejoinSyntheticTurnGuard.Complete();
            ReconnectDiscovery.OnJoinFailed();
            throw;
        }

        // Use an existing reliable, broadcast-safe native message as the restore ACK.
        flow.NetService.SendMessage(new PlayerRejoinedMessage(flow.NetService.NetId));
        ReconnectDiscovery.OnJoinSucceeded();

        // Prevent NJoinFriendScreen's unfinished Running branch from showing RunInProgress and disconnecting.
        result.sessionState = null;
        return result;
    }
}

// Normal lobby joins must remain entirely native.  The replay receiver is only
// needed after the host's initial message explicitly identifies an in-progress
// run, and must be ready before JoinFlow sends ClientRejoinRequestMessage.
[HarmonyPatch(typeof(JoinFlow), "HandleInitialGameInfoMessage",
    new[] { typeof(InitialGameInfoMessage), typeof(ulong) })]
internal static class PrepareReplayReceiverForRunningJoinPatch
{
    private static void Prefix(JoinFlow __instance, InitialGameInfoMessage message)
    {
        if (message.sessionState == RunSessionState.Running)
            ReplayClientExchange.Prepare(__instance.NetService);
    }
}

[HarmonyPatch(typeof(NOrbManager), "EvokeOrbAnim", new[] { typeof(OrbModel) })]
internal static class SuppressOrbEvokeAnimDuringReplayPatch
{
    private static bool Prefix()
    {
        if (!DeterministicReplayEngine.IsReplaying || DeterministicReplayEngine.ShowReplayVisuals)
            return true;

        ReconnectLog.Write("Suppressed orb evoke animation during deterministic replay.");
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "OnPlayWrapper", new[]
{
    typeof(PlayerChoiceContext), typeof(Creature), typeof(bool), typeof(ResourceInfo), typeof(bool)
})]
internal static class SuppressCardPileVisualsDuringReplayPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ref bool skipCardPileVisuals)
    {
        if (DeterministicReplayEngine.IsReplaying && !DeterministicReplayEngine.ShowReplayVisuals)
            skipCardPileVisuals = true;
    }
}

[HarmonyPatch(typeof(NCardTrailVfx), nameof(NCardTrailVfx.Create),
    new[] { typeof(Control), typeof(string) })]
internal static class SuppressCardTrailDuringReplayPatch
{
    private static bool Prefix(ref NCardTrailVfx? __result)
    {
        if (!DeterministicReplayEngine.IsReplaying || DeterministicReplayEngine.ShowReplayVisuals)
            return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(NCardPlayQueue), "OnActionEnqueued", new[] { typeof(GameAction) })]
internal static class SuppressCardPlayQueueDuringReplayPatch
{
    private static bool Prefix()
    {
        if (!DeterministicReplayEngine.IsReplaying) return true;
        if (!DeterministicReplayEngine.ShowReplayVisuals) return false;
        return RunManager.Instance.DebugOnlyGetState()?.Players.Count > 1;
    }
}

internal static class ReplayVisualCleanup
{
    private static readonly FieldInfo PlayQueueItems = AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");

    internal static void ClearPlayedCards()
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        if (ui == null) return;
        int removed = 0;
        removed += ClearCardChildren(ui.PlayContainer);
        removed += ClearCardChildren(ui.PlayQueue);
        ((IList)PlayQueueItems.GetValue(ui.PlayQueue)!).Clear();
        if (removed > 0)
            ReconnectLog.Write($"Removed {removed} replay card visual(s) from the play container/queue.");
    }

    private static int ClearCardChildren(Control container)
    {
        int removed = 0;
        foreach (Node child in container.GetChildren().ToArray())
        {
            if (child is not NCard card) continue;
            card.PlayPileTween?.Kill();
            card.PlayPileTween = null;
            container.RemoveChild(card);
            card.QueueFree();
            removed++;
        }
        return removed;
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.GetRejoinMessage), new Type[] { })]
internal static class CaptureExtendedStatePatch
{
    private static void Postfix(ref ClientRejoinResponseMessage __result)
    {
        ExtendedReconnectMessage extended = ExtendedReconnectCapture.Capture();
        if (__result.combatState != null)
        {
            __result.combatState.lastExecutedActionId = extended.nextActionId == 0 ? null : extended.nextActionId - 1;
            __result.combatState.lastExecutedHookId = extended.nextHookId == 0 ? null : extended.nextHookId - 1;
        }
    }
}

internal static class CounterSyncExchange
{
    internal static void Register(INetGameService service)
        => service.RegisterMessageHandler<ReconnectCounterSyncMessage>(OnReceived);

    internal static void BroadcastFromHost()
    {
        RunManager manager = RunManager.Instance;
        if (manager.NetService.Type != NetGameType.Host) return;
        if (RewindTransactionGate.InputLocked || DeterministicReplayEngine.IsReplaying) return;
        manager.NetService.SendMessage(new ReconnectCounterSyncMessage
        {
            nextActionId = manager.ActionQueueSet.NextActionId,
            nextHookId = manager.ActionQueueSynchronizer.NextHookId,
            choiceIds = manager.PlayerChoiceSynchronizer.ChoiceIds.ToList(),
            rewardIds = manager.RewardsSetSynchronizer.GetNextRewardIds().ToList()
        });
    }

    private static void OnReceived(ReconnectCounterSyncMessage message, ulong _)
    {
        TryApplyReceived(message, requireClient: true);
    }

    internal static bool TryApplyForSelfTest(ReconnectCounterSyncMessage message)
        => TryApplyReceived(message, requireClient: false);

    private static bool TryApplyReceived(ReconnectCounterSyncMessage message, bool requireClient)
    {
        RunManager manager = RunManager.Instance;
        if (requireClient && manager.NetService.Type != NetGameType.Client) return false;
        if (RewindTransactionGate.InputLocked || DeterministicReplayEngine.IsReplaying)
        {
            ReconnectLog.Write(
                $"Ignored normal counter anchor during rewind transaction: action={message.nextActionId}, hook={message.nextHookId}.");
            return false;
        }
        manager.ActionQueueSet.FastForwardNextActionId(message.nextActionId);
        manager.ActionQueueSynchronizer.FastForwardHookId(message.nextHookId);
        manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(message.choiceIds);
        manager.RewardsSetSynchronizer.FastForwardRewardIds(message.rewardIds);
        ReconnectLog.Write($"Applied host counter anchor: action={message.nextActionId}, hook={message.nextHookId}.");
        return true;
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeShared", new[]
{
    typeof(INetGameService), typeof(PeerInputSynchronizer), typeof(bool), typeof(DateTimeOffset?),
    typeof(long), typeof(long), typeof(long), typeof(int)
})]
internal static class RegisterCounterSyncPatch
{
    private static void Postfix(INetGameService netService)
    {
        CounterSyncExchange.Register(netService);
        MultiplayerRewindCoordinator.Register(netService);
        if (netService.Type == NetGameType.Host) ReplayHostExchange.Register(netService);
    }
}

[HarmonyPatch(typeof(ChecksumTracker), nameof(ChecksumTracker.GenerateChecksum),
    new[] { typeof(string), typeof(GameAction) })]
internal static class BroadcastCounterAnchorPatch
{
    private static void Postfix(GameAction? action)
    {
        if (action == null) CounterSyncExchange.BroadcastFromHost();
    }
}

/// <summary>
/// Steam's friend-game presence can temporarily lose the lobby id after a network outage.
/// Preserve the exact lobby we were connected to, expose the old host in Join Friend, and
/// turn the button's player-based initializer into a direct lobby initializer.
/// </summary>
internal static class ReconnectDiscovery
{
    private static ulong? _hostId;
    private static ulong? _lobbyId;
    private static ulong? _pendingLobbyId;
    private static bool _directAttemptActive;

    internal static async Task ArmAfterCombatStarts(Task combatStart)
    {
        try
        {
            await combatStart;
            CaptureConnectedCombatLobby();
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Could not cache connected combat lobby: {ex.Message}");
        }
    }

    internal static async Task ClearAfterConnectedCombatEnds(Task combatEnd)
    {
        try
        {
            await combatEnd;
            if (RunManager.Instance.NetService is INetClientGameService { IsConnected: true })
            {
                Clear();
                ReconnectLog.Write("Cleared remembered combat lobby after normal combat completion.");
            }
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Could not clear completed combat lobby: {ex.Message}");
        }
    }

    private static void CaptureConnectedCombatLobby()
    {
        if (RunManager.Instance.NetService is not INetClientGameService
            {
                IsConnected: true,
                NetClient: SteamClient client
            })
            return;
        string? rawLobbyId = client.GetRawLobbyIdentifier();
        if (!ulong.TryParse(rawLobbyId, out ulong lobbyId)) return;

        _hostId = client.HostNetId;
        _lobbyId = lobbyId;
        _pendingLobbyId = null;
        _directAttemptActive = false;
        ReconnectLog.Write($"Pre-cached combat host {_hostId} and Steam lobby {_lobbyId} for reconnect discovery.");
    }

    internal static void CaptureLobbyBeforeCleanup(SteamClient client, bool selfInitiated)
    {
        // A failed normal lobby join also disconnects the Steam client.  Retaining
        // that lobby made the next ordinary Join Friend click use a stale direct
        // lobby id, which then prevented the native lobby handshake from starting.
        if (selfInitiated)
        {
            Clear();
            return;
        }
        if (_hostId.HasValue && _lobbyId.HasValue)
        {
            ReconnectLog.Write($"Preserved pre-cached Steam lobby {_lobbyId} during unexpected disconnect.");
            return;
        }
        if (!IsCombatReconnectCandidate())
        {
            Clear();
            return;
        }
        string? raw = client.GetRawLobbyIdentifier();
        if (ulong.TryParse(raw, out ulong lobbyId))
        {
            _pendingLobbyId = lobbyId;
            ReconnectLog.Write($"Remembered disconnected Steam lobby {lobbyId}.");
        }
    }

    internal static void CaptureHost(ulong hostId)
    {
        if (!_pendingLobbyId.HasValue) return;
        _hostId = hostId;
        _lobbyId = _pendingLobbyId;
        _pendingLobbyId = null;
        ReconnectLog.Write($"Pinned host {hostId} to lobby {_lobbyId} for direct rejoin.");
    }

    internal static async Task<IEnumerable<ulong>> AddRememberedHost(Task<IEnumerable<ulong>> original)
    {
        var players = (await original).ToList();
        if (_hostId.HasValue && !players.Contains(_hostId.Value))
        {
            players.Add(_hostId.Value);
            ReconnectLog.Write($"Added remembered host {_hostId} to Join Friend results.");
        }
        return players;
    }

    internal static void ReplaceWithDirectLobby(ulong playerId, ref SteamClientConnectionInitializer initializer)
    {
        _directAttemptActive = false;
        if (_hostId == playerId && _lobbyId.HasValue)
        {
            initializer = SteamClientConnectionInitializer.FromLobby(_lobbyId.Value);
            _directAttemptActive = true;
            ReconnectLog.Write($"Using direct lobby {_lobbyId} for remembered host {playerId}.");
        }
    }

    internal static void OnJoinFailed()
    {
        if (!_directAttemptActive) return;
        ReconnectLog.Write("Remembered direct-lobby join failed; clearing it so the next attempt uses native friend discovery.");
        Clear();
    }

    internal static void OnJoinSucceeded()
    {
        if (!_directAttemptActive) return;
        Clear();
        CaptureConnectedCombatLobby();
    }

    internal static void Clear()
    {
        _hostId = null;
        _lobbyId = null;
        _pendingLobbyId = null;
        _directAttemptActive = false;
    }

    private static bool IsCombatReconnectCandidate()
    {
        try
        {
            return CombatManager.Instance.IsInProgress &&
                   RunManager.Instance.NetService.Type == NetGameType.Client;
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(SteamClient), "DisconnectFromHostInternal", new[]
{
    typeof(SteamDisconnectionReason), typeof(string), typeof(bool), typeof(bool)
})]
internal static class RememberLobbyBeforeDisconnectPatch
{
    private static void Prefix(SteamClient __instance, bool selfInitiated)
        => ReconnectDiscovery.CaptureLobbyBeforeCleanup(__instance, selfInitiated);
}

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost),
    new[] { typeof(ulong), typeof(NetErrorInfo) })]
internal static class RememberHostAfterDisconnectPatch
{
    private static void Prefix(ulong hostNetId) => ReconnectDiscovery.CaptureHost(hostNetId);
}

[HarmonyPatch(typeof(PlatformUtil), nameof(PlatformUtil.GetFriendsWithOpenLobbies),
    new[] { typeof(PlatformType) })]
internal static class RememberedHostDiscoveryPatch
{
    private static void Postfix(PlatformType platformType, ref Task<IEnumerable<ulong>> __result)
    {
        if (platformType == PlatformType.Steam)
            __result = ReconnectDiscovery.AddRememberedHost(__result);
    }
}

[HarmonyPatch(typeof(SteamClientConnectionInitializer), nameof(SteamClientConnectionInitializer.FromPlayer),
    new[] { typeof(ulong) })]
internal static class DirectLobbyInitializerPatch
{
    private static void Postfix(ulong playerSteamId, ref SteamClientConnectionInitializer __result)
        => ReconnectDiscovery.ReplaceWithDirectLobby(playerSteamId, ref __result);
}

internal static class RejoinLoader
{
    private static readonly MethodInfo InitializeShared = AccessTools.Method(typeof(RunManager), "InitializeShared");
    private static readonly MethodInfo InitializeRunLobby = AccessTools.Method(typeof(RunManager), "InitializeRunLobby");
    private static readonly MethodInfo InitializeSavedRun = AccessTools.Method(typeof(RunManager), "InitializeSavedRun");
    private static readonly PropertyInfo StateProperty = AccessTools.Property(typeof(RunManager), "State");
    internal static bool IsLoading { get; private set; }
    private static readonly PropertyInfo ChecksumNextId =
        AccessTools.Property(typeof(ChecksumTracker), nameof(ChecksumTracker.NextId));

    internal static async Task<string> Load(INetGameService netService, ClientRejoinResponseMessage response, ReceivedReplayPackage package)
    {
        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("Cannot rejoin while another local run is active.");

        SerializableRun save = response.serializableRun;
        RunState state = RunState.FromSerializable(save);
        RunManager manager = RunManager.Instance;

        IsLoading = true;
        RejoinSyntheticTurnGuard.Begin();
        PendingExtendedState = package.AnchorExtended;
        try
        {
            StateProperty.SetValue(manager, state);
            var input = new PeerInputSynchronizer(netService);
            InitializeShared.Invoke(manager, new object?[]
            {
                netService, input, true, save.DailyTime, save.StartTime,
                save.RunTime, save.WinTime, save.NumReloads
            });
            InitializeRunLobby.Invoke(manager, new object[] { netService, state });
            InitializeSavedRun.Invoke(manager, new object[] { save });
            NGame.Instance!.RemoteCursorContainer.Initialize(input, state.Players.Select(player => player.NetId));
            NGame.Instance!.ReactionContainer.InitializeNetworking(netService);

            // A newly constructed combat emits its own turn-start checksum. That event
            // already happened on the host before the disconnect, so it must not consume
            // a new checksum ID on the reconnecting client.
            manager.ChecksumTracker.IsEnabled = false;
            ChecksumNextId.SetValue(manager.ChecksumTracker, package.AnchorExtended.nextChecksumId);
            manager.CombatStateSynchronizer.IsDisabled = true;
            netService.SetBufferMessages(bufferMessages: true);
            RunEncounterStateSupport.PrepareForSyntheticCombatEntry(state, package.AnchorExtended);
            ReconnectLog.Write("Loading run with entry combat sync bypassed and messages buffered.");
            await NGame.Instance.LoadRun(state, null);

            // LoadRun returns while the freshly-created combat's opening actions can still
            // be paused in the executor. Let those synthetic actions finish with networking
            // and checksums disabled, then overwrite their result with the host snapshot.
            CombatManager.Instance.Unpause();
            manager.ActionExecutor.Unpause();
            await manager.ActionExecutor.FinishedExecutingActions();
            await SyntheticStartTurnTracker.AwaitAll("pre-anchor restore");
            await RejoinSyntheticTurnGuard.AwaitQuiescence("pre-anchor restore");
            CombatManager.Instance.Pause();
            manager.ActionExecutor.Pause();
            RunEncounterStateSupport.RemoveSyntheticMapPointHistoryEntry(state, package.AnchorExtended);
            ReconnectLog.Write("Run scene load completed; restoring the shared turn anchor and replaying inputs.");
            string hash = await DeterministicReplayEngine.RestoreAndReplay(
                package.AnchorState, package.AnchorExtended, package.Events,
                package.Manifest.finalActionId, package.Manifest.finalHookId, package.Manifest.finalChecksumId);
            if (hash != package.Manifest.finalStateHash && response.combatState is { } authoritativeState)
            {
                ReconnectLog.Write(
                    $"Replay hash differed before authoritative creature scalar correction: " +
                    $"expected={package.Manifest.finalStateHash}, actual={hash}.");
                CombatStateRestorer.RestoreAuthoritativeCreatureScalars(authoritativeState);
                hash = DeterministicReplayEngine.CaptureCurrentStateHash();
                ReconnectLog.Write($"State hash after authoritative creature scalar correction: {hash}.");
            }
            CombatManager.Instance.Pause();
            manager.ActionExecutor.Pause();
            ReconnectLog.Write($"Client replay completed with state hash {hash}; waiting for host commit.");
            return hash;
        }
        finally
        {
            if (manager.IsInProgress)
            {
                manager.CombatStateSynchronizer.IsDisabled = false;
                if (!RejoinSyntheticTurnGuard.IsActive)
                    manager.ChecksumTracker.IsEnabled = true;
            }
            IsLoading = false;
            PendingExtendedState = null;
            netService.SetBufferMessages(bufferMessages: false);
            ReconnectLog.Write("Normal combat synchronization and message delivery restored.");
        }
    }

    internal static ExtendedReconnectMessage? PendingExtendedState { get; private set; }

}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat), new[] { typeof(CombatState) })]
internal static class AlignCreatureRosterBeforeCombatSetupPatch
{
    private static void Prefix(CombatState state)
    {
        if (RejoinLoader.IsLoading && RejoinLoader.PendingExtendedState is { } extended)
            CombatStateRestorer.AlignCreatureRoster(state, extended, restorePetRegistry: false);
    }

    private static void Postfix(CombatState state)
    {
        if (RejoinLoader.IsLoading && RejoinLoader.PendingExtendedState is { } extended)
            CombatStateRestorer.RestorePetRegistry(state, extended);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch), new Type[] { })]
internal static class KeepMessagesBufferedWhileRejoiningPatch
{
    private static void Postfix(RunManager __instance)
    {
        if (RejoinLoader.IsLoading)
            __instance.NetService.SetBufferMessages(bufferMessages: true);
    }
}

internal static class CombatStateRestorer
{
    internal static bool RestoreInProgress { get; private set; }
    private static readonly FieldInfo ReadyToEnd = AccessTools.Field(typeof(CombatManager), "_playersReadyToEndTurn");
    private static readonly FieldInfo ReadyToBeginEnemy = AccessTools.Field(typeof(CombatManager), "_playersReadyToBeginEnemyTurn");
    private static readonly FieldInfo TakingExtraTurn = AccessTools.Field(typeof(CombatManager), "_playersTakingExtraTurn");
    private static readonly FieldInfo PlayerActionsDisabled = AccessTools.Field(typeof(CombatManager), "_playerActionsDisabled");
    private static readonly FieldInfo CreatureBlock = AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo TurnNumber = AccessTools.Field(typeof(PlayerCombatState), "<TurnNumber>k__BackingField");
    private static readonly FieldInfo OrbList = AccessTools.Field(typeof(OrbQueue), "_orbs");
    private static readonly FieldInfo OrbOwner = AccessTools.Field(typeof(OrbModel), "_owner");
    private static readonly FieldInfo NextCreatureId = AccessTools.Field(typeof(CombatState), "_nextCreatureId");
    private static readonly FieldInfo CombatAllies = AccessTools.Field(typeof(CombatState), "_allies");
    private static readonly FieldInfo CombatEnemies = AccessTools.Field(typeof(CombatState), "_enemies");
    private static readonly FieldInfo CreatureNodes = AccessTools.Field(typeof(NCombatRoom), "_creatureNodes");
    private static readonly FieldInfo PowerInternal = AccessTools.Field(typeof(PowerModel), "_internalData");
    private static readonly FieldInfo PowerSkip = AccessTools.Field(typeof(PowerModel), "_skipNextDurationTick");
    private static readonly FieldInfo PowerOwner = AccessTools.Field(typeof(PowerModel), "_owner");
    private static readonly FieldInfo PowerAmount = AccessTools.Field(typeof(PowerModel), "_amount");
    private static readonly FieldInfo CostBase = AccessTools.Field(typeof(CardEnergyCost), "_base");
    private static readonly FieldInfo CostCapturedX = AccessTools.Field(typeof(CardEnergyCost), "_capturedXValue");
    private static readonly FieldInfo CostModifiers = AccessTools.Field(typeof(CardEnergyCost), "_localModifiers");
    private static readonly FieldInfo CardTemporaryStarCosts =
        AccessTools.Field(typeof(CardModel), "_temporaryStarCosts");
    private static readonly Type SmType = AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine");
    private static readonly Type MoveStateType = AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState");
    private static readonly FieldInfo SmPerformedFirst = AccessTools.Field(SmType, "_performedFirstMove");
    private static readonly FieldInfo MovePerformed = AccessTools.Field(MoveStateType, "_performedAtLeastOnce");
    private static readonly FieldInfo MonsterSpawned = AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");
    private static readonly FieldInfo PlayerPets = AccessTools.Field(typeof(PlayerCombatState), "_pets");
    private static readonly MethodInfo OnPetDied = AccessTools.Method(typeof(PlayerCombatState), "OnPetDied");
    private static readonly FieldInfo CreaturePetOwner = AccessTools.Field(typeof(Creature), "_petOwner");
    private static readonly FieldInfo RelicStackCount =
        AccessTools.Field(typeof(RelicModel), "<StackCount>k__BackingField");
    private static readonly MethodInfo RelicDisplayAmountChanged =
        AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged");
    private static readonly FieldInfo EndingPhaseOne = AccessTools.Field(
        typeof(CombatManager), "<EndingPlayerTurnPhaseOne>k__BackingField");
    private static readonly FieldInfo EndingPhaseTwo = AccessTools.Field(
        typeof(CombatManager), "<EndingPlayerTurnPhaseTwo>k__BackingField");
    private static readonly FieldInfo EnemyTurnStarted = AccessTools.Field(
        typeof(CombatManager), "<IsEnemyTurnStarted>k__BackingField");
    private static readonly FieldInfo InPlayerTurnSetup = AccessTools.Field(typeof(CombatManager), "_inPlayerTurnSetup");
    private static readonly FieldInfo DeferredEndTurnTransition = AccessTools.Field(
        typeof(CombatManager), "_deferredEndTurnTransition");
    private static readonly MethodInfo? NotifyStateChanged = AccessTools.Method(
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatStateTracker"), "NotifyCombatStateChanged");

    internal static void Restore(NetFullCombatState snapshot, ExtendedReconnectMessage extended)
    {
        if (!CombatManager.Instance.IsInProgress)
        {
            ReconnectLog.Write("Snapshot contained no active combat to restore.");
            return;
        }

        MonsterMoveStateSupport.ValidateSnapshot(extended, "restore input");

        CombatManager.Instance.Pause();
        RunManager.Instance.ActionExecutor.Pause();
        RunState state = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not initialized during rejoin.");
        CombatState combat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not initialized during rejoin.");

        RestoreInProgress = true;
        try
        {
            // A targeting/queued NCardPlay owns a holder from the current hand. Cancel it while
            // that holder and its creature owner still belong to the live pre-rewind state.
            // RefreshLocalHand replaces those nodes, so cancelling afterwards leaves NCardPlay
            // pointing at a freed holder and the local player can no longer play cards.
            NPlayerHand.Instance?.CancelAllCardPlay();

            state.Rng.LoadFromSerializable(snapshot.Rng);

            ResetFailedTurnTransition();
            RunEncounterStateSupport.Restore(state, combat, extended);
            combat.RoundNumber = extended.roundNumber;
            combat.CurrentSide = extended.currentSide;
            RestoreTurnCoordination(state, extended);
            AlignCreatureRoster(combat, extended);
            // Adding a missing creature node may initialize or refresh its monster model.
            // Do that before applying the authoritative snapshot so UI reconstruction cannot
            // mutate the state after it has already been restored and hashed.
            RefreshCreatureNodes(combat);
            // CreateCreature consumes the run's niche RNG while assigning monster HP. The
            // authoritative creature state is restored below, so that setup consumption must
            // not escape into replay.
            state.Rng.LoadFromSerializable(snapshot.Rng);
            ReconnectLog.Write("Re-aligned run RNG after restoring the anchor creature roster.");
            RestoreCreatures(snapshot, extended);
            foreach (NetFullCombatState.PlayerState saved in snapshot.Players)
            {
                Player player = state.GetPlayer(saved.playerId)
                    ?? throw new InvalidOperationException($"Snapshot references unknown player {saved.playerId}.");
                int cardStateIndex = extended.playerCards.FindIndex(p => p.playerId == saved.playerId);
                ExtendedPlayerCardState? cardState = cardStateIndex >= 0 ? extended.playerCards[cardStateIndex] : null;
                RestorePlayer(player, saved, cardState,
                    extended.relicRuntimeStates.Where(relic => relic.playerId == saved.playerId).ToList());
            }

            // Restore the host's per-instance IDs. Enumeration order cannot distinguish duplicate
            // cards such as two basic Strikes after a run has been reconstructed.
            CombatCardReplayMapper.RestoreAnchorMappings(combat, extended);
            CombatHistoryStateSupport.Restore(combat, extended.combatHistory);

            RunManager.Instance.ActionQueueSet.FastForwardNextActionId(extended.nextActionId);
            RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(extended.nextHookId);
            RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(snapshot.nextChoiceIds);
            RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(snapshot.nextRewardIds);

            RefreshLocalHand();
        }
        finally
        {
            RestoreInProgress = false;
        }

        NotifyStateChanged?.Invoke(CombatManager.Instance.StateTracker, new object[] { "CombatReconnect" });
        RefreshEnemyIntents(combat);
        CombatManager.Instance.Unpause();
        RunManager.Instance.ActionExecutor.Unpause();
    }

    private static void RestoreTurnCoordination(RunState state, ExtendedReconnectMessage extended)
    {
        static void ReplacePlayers(object collection, IEnumerable<ulong> ids, RunState runState)
        {
            MethodInfo clear = AccessTools.Method(collection.GetType(), "Clear");
            MethodInfo add = AccessTools.Method(collection.GetType(), "Add");
            clear.Invoke(collection, null);
            foreach (ulong id in ids)
            {
                Player? player = runState.GetPlayer(id);
                if (player != null) add.Invoke(collection, new object[] { player });
            }
        }

        ReplacePlayers(ReadyToEnd.GetValue(CombatManager.Instance)!, extended.playersReadyToEndTurn, state);
        ReplacePlayers(ReadyToBeginEnemy.GetValue(CombatManager.Instance)!, extended.playersReadyToBeginEnemyTurn, state);
        ReplacePlayers(TakingExtraTurn.GetValue(CombatManager.Instance)!, extended.playersTakingExtraTurn, state);
        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(extended.actionSynchronizerState);
        bool localReady = LocalContext.NetId.HasValue && extended.playersReadyToEndTurn.Contains(LocalContext.NetId.Value);
        PlayerActionsDisabled.SetValue(CombatManager.Instance, localReady);
    }

    private static void RefreshCreatureNodes(CombatState combat)
    {
        MethodInfo positionEnemies = AccessTools.Method(typeof(NCombatRoom), "PositionEnemies");
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null) return;

        foreach (NCreature node in room.CreatureNodes.ToList())
        {
            Creature? replacement = node.Entity.CombatId.HasValue
                ? combat.GetCreature(node.Entity.CombatId.Value)
                : null;
            if (node.Entity.CombatState == combat && replacement == node.Entity)
                continue;

            uint? staleId = node.Entity.CombatId;
            Vector2 oldPosition = node.GlobalPosition;
            room.RemoveCreatureNode(node);
            node.QueueFree();
            if (replacement != null)
            {
                room.AddCreature(replacement);
                NCreature? rebuilt = room.GetCreatureNode(replacement);
                if (rebuilt != null)
                    rebuilt.GlobalPosition = oldPosition;
                ReconnectLog.Write($"Rebuilt stale creature node for combat ID {staleId}.");
            }
        }

        foreach (Creature creature in combat.Creatures)
        {
            if (room.GetCreatureNode(creature) != null) continue;
            room.AddCreature(creature);
            ReconnectLog.Write($"Created missing creature node for combat ID {creature.CombatId}.");
        }

        var nodes = (List<NCreature>)CreatureNodes.GetValue(room)!;
        var nodeOrder = combat.Creatures
            .Select(room.GetCreatureNode)
            .Where(n => n != null)
            .Cast<NCreature>()
            .ToList();
        nodeOrder.AddRange(nodes.Where(n => !nodeOrder.Contains(n)));
        nodes.Clear();
        nodes.AddRange(nodeOrder);
        AccessTools.Method(typeof(NCombatRoom), "UpdateCreatureNavigation").Invoke(room, null);

        // Encounters without named slots rely on a private layout pass normally run only
        // during initial room construction. Repeat it after adding authoritative monsters.
        if (combat.Encounter != null && combat.Enemies.All(c => string.IsNullOrEmpty(c.SlotName)))
        {
            List<NCreature> enemies = combat.Enemies
                .Select(room.GetCreatureNode)
                .Where(n => n != null)
                .Cast<NCreature>()
                .ToList();
            positionEnemies.Invoke(room, new object[] { enemies, combat.Encounter.GetCameraScaling() });
        }
    }

    internal static void AlignCreatureRoster(
        CombatState state,
        ExtendedReconnectMessage extended,
        bool restorePetRegistry = true)
    {
        var desired = extended.creatures.Where(c => c.isMonster).ToList();
        var desiredIds = desired.Select(c => c.combatId).ToHashSet();
        foreach (Creature existing in state.Creatures.Where(c => c.Monster != null).ToList())
        {
            MonsterModel monster = existing.Monster!;
            int matchIndex = desired.FindIndex(c => c.combatId == existing.CombatId);
            if (!desiredIds.Contains(existing.CombatId ?? uint.MaxValue) || matchIndex < 0 || desired[matchIndex].monsterId != monster.Id)
            {
                ReconnectLog.Write(
                    $"Removing post-anchor or mismatched creature: combatId={existing.CombatId}, monster={monster.Id}.");
                state.RemoveCreature(existing);
            }
        }

        foreach (ExtendedCreatureState saved in desired)
        {
            Creature? existing = state.GetCreature(saved.combatId);
            if (existing?.Monster?.Id == saved.monsterId) continue;
            MonsterModel canonical = ModelDb.GetById<MonsterModel>(saved.monsterId);
            Creature created = state.CreateCreature((MonsterModel)canonical.MutableClone(), saved.side,
                string.IsNullOrEmpty(saved.slotName) ? null : saved.slotName);
            created.CombatId = saved.combatId;
            state.AddCreature(created);
            // During a live rollback the manager already owns this CombatState, so the
            // recreated monster needs its normal combat setup now. During rejoin this method
            // also runs before SetUpCombat; that method will set up every creature itself.
            if (ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
                CombatManager.Instance.AddCreature(created);
            ReconnectLog.Write(
                $"Restored anchor creature missing from live combat: combatId={saved.combatId}, monster={saved.monsterId}.");
        }
        RestoreCreatureOrder(state, extended);
        if (restorePetRegistry)
            RestorePetRegistry(state, extended);
        uint highestReferencedId = extended.creatures
            .Select(creature => creature.combatId)
            .Concat(extended.combatHistory.creatures.Select(creature => creature.combatId))
            .DefaultIfEmpty()
            .Max();
        if ((extended.creatures.Count > 0 || extended.combatHistory.creatures.Count > 0) &&
            extended.nextCreatureId <= highestReferencedId)
            throw new InvalidDataException(
                $"Anchor next creature ID {extended.nextCreatureId} does not exceed referenced ID {highestReferencedId}.");
        NextCreatureId.SetValue(state, extended.nextCreatureId);
    }

    internal static void RestorePetRegistry(CombatState state, ExtendedReconnectMessage extended)
    {
        foreach (Player player in state.Players)
        {
            PlayerCombatState? playerState = player.PlayerCombatState;
            if (playerState == null) continue;

            var pets = (List<Creature>)PlayerPets.GetValue(playerState)!;
            var onPetDied = (Action<Creature>)Delegate.CreateDelegate(
                typeof(Action<Creature>), playerState, OnPetDied);
            foreach (Creature pet in pets.ToList()) pet.Died -= onPetDied;
            pets.Clear();
        }

        foreach (ExtendedCreatureState saved in extended.creatures.Where(creature => creature.petOwnerId.HasValue))
        {
            Creature pet = state.GetCreature(saved.combatId)
                ?? throw new InvalidDataException(
                    $"Pet snapshot references missing combat creature {saved.combatId}.");
            Player owner = state.RunState.GetPlayer(saved.petOwnerId!.Value)
                ?? throw new InvalidDataException(
                    $"Pet snapshot references missing owner {saved.petOwnerId.Value}.");
            PlayerCombatState playerState = owner.PlayerCombatState
                ?? throw new InvalidOperationException(
                    $"Pet owner {owner.NetId} has no combat state during restore.");

            if (pet.PetOwner != null && pet.PetOwner != owner)
                CreaturePetOwner.SetValue(pet, null);
            playerState.AddPetInternal(pet);
        }

        ValidatePetRegistry(state, extended);
    }

    private static void ValidatePetRegistry(CombatState state, ExtendedReconnectMessage extended)
    {
        var expected = extended.creatures
            .Where(creature => creature.petOwnerId.HasValue)
            .ToDictionary(creature => creature.combatId, creature => creature.petOwnerId!.Value);
        var registered = new Dictionary<uint, ulong>();

        foreach (Player player in state.Players)
        {
            PlayerCombatState? playerState = player.PlayerCombatState;
            if (playerState == null) continue;
            foreach (Creature pet in playerState.Pets)
            {
                if (!pet.CombatId.HasValue || !state.ContainsCreature(pet))
                    throw new InvalidOperationException(
                        $"Player {player.NetId} retains a pet outside the active combat.");
                if (pet.PetOwner != player)
                    throw new InvalidOperationException(
                        $"Pet {pet.CombatId} owner mismatch: list={player.NetId}, model={pet.PetOwner?.NetId}.");
                if (!registered.TryAdd(pet.CombatId.Value, player.NetId))
                    throw new InvalidOperationException($"Pet {pet.CombatId} is registered more than once.");
            }
        }

        if (registered.Count != expected.Count ||
            expected.Any(entry => !registered.TryGetValue(entry.Key, out ulong owner) || owner != entry.Value))
            throw new InvalidOperationException(
                $"Pet registry restore mismatch: expected=[{string.Join(",", expected.Select(entry => $"{entry.Key}:{entry.Value}"))}], " +
                $"actual=[{string.Join(",", registered.Select(entry => $"{entry.Key}:{entry.Value}"))}].");

        ReconnectLog.Write(
            $"Restored pet registry: [{string.Join(",", registered.Select(entry => $"{entry.Key}:{entry.Value}"))}].");
    }

    private static void RestoreCreatureOrder(CombatState state, ExtendedReconnectMessage extended)
    {
        static string Describe(IEnumerable<Creature> creatures) =>
            string.Join(",", creatures.Select(c => c.CombatId?.ToString() ?? "null"));

        static void Reorder(List<Creature> creatures, IEnumerable<uint> desiredIds)
        {
            var byId = creatures
                .Where(c => c.CombatId.HasValue)
                .GroupBy(c => c.CombatId!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            var ordered = desiredIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
            ordered.AddRange(creatures.Where(c => !ordered.Contains(c)));
            creatures.Clear();
            creatures.AddRange(ordered);
        }

        var allies = (List<Creature>)CombatAllies.GetValue(state)!;
        var enemies = (List<Creature>)CombatEnemies.GetValue(state)!;
        string before = $"allies=[{Describe(allies)}], enemies=[{Describe(enemies)}]";
        Reorder(allies, extended.creatures.Where(c => c.side == CombatSide.Player).Select(c => c.combatId));
        Reorder(enemies, extended.creatures.Where(c => c.side == CombatSide.Enemy).Select(c => c.combatId));
        string after = $"allies=[{Describe(allies)}], enemies=[{Describe(enemies)}]";
        ReconnectLog.Write($"Restored anchor creature order: {before} -> {after}.");
    }

    private static void RefreshEnemyIntents(CombatState combat)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null) return;

        int refreshed = 0;
        foreach (Creature enemy in combat.Enemies)
        {
            NCreature? node = room.GetCreatureNode(enemy);
            if (node == null) continue;
            TaskHelper.RunSafely(node.RefreshIntents());
            refreshed++;
        }
        ReconnectLog.Write($"Refreshed {refreshed} enemy intent node(s) after restoring monster moves.");
    }

    private static void RestoreCreatures(NetFullCombatState snapshot, ExtendedReconnectMessage extended)
    {
        CombatState combat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not initialized during rejoin.");

        foreach ((Creature creature, NetFullCombatState.CreatureState saved, ExtendedCreatureState? extra) in
                 MatchCreatures(snapshot, extended, combat))
        {
            creature.SetMaxHpInternal(saved.maxHp);
            creature.SetCurrentHpInternal(saved.currentHp);
            CreatureBlock.SetValue(creature, saved.block);
            RestorePowers(creature, saved.powers);
            if (extra.HasValue) RestoreCreatureExtras(combat, creature, extra.Value);
        }
    }

    internal static void RestoreAuthoritativeCreatureScalars(NetFullCombatState snapshot)
    {
        CombatState combat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not initialized during final creature correction.");
        ExtendedReconnectMessage current = ExtendedReconnectCapture.Capture();
        int corrected = 0;
        foreach ((Creature creature, NetFullCombatState.CreatureState saved, _) in
                 MatchCreatures(snapshot, current, combat))
        {
            bool changed = creature.CurrentHp != saved.currentHp || creature.MaxHp != saved.maxHp ||
                           creature.Block != saved.block;
            if (!changed) continue;
            ReconnectLog.Write(
                $"Correcting authoritative creature scalars: combatId={creature.CombatId}, " +
                $"model={saved.monsterId}, player={saved.playerId}, " +
                $"hp={creature.CurrentHp}/{creature.MaxHp}->{saved.currentHp}/{saved.maxHp}, " +
                $"block={creature.Block}->{saved.block}.");
            creature.SetMaxHpInternal(saved.maxHp);
            creature.SetCurrentHpInternal(saved.currentHp);
            CreatureBlock.SetValue(creature, saved.block);
            corrected++;
        }
        ReconnectLog.Write($"Applied authoritative creature scalar correction to {corrected} creature(s).");
    }

    private static List<(Creature creature, NetFullCombatState.CreatureState saved, ExtendedCreatureState? extra)>
        MatchCreatures(NetFullCombatState snapshot, ExtendedReconnectMessage extended, CombatState combat)
    {
        var matches = new List<(
            Creature creature, NetFullCombatState.CreatureState saved, ExtendedCreatureState? extra)>();
        var monsterExtrasById = extended.creatures
            .Where(c => c.isMonster)
            .GroupBy(c => c.monsterId)
            .ToDictionary(g => g.Key, g => new Queue<ExtendedCreatureState>(g));

        var extrasByCombatId = extended.creatures
            .GroupBy(c => c.combatId)
            .ToDictionary(g => g.Key, g => g.First());

        var remaining = combat.Creatures.ToList();
        foreach (NetFullCombatState.CreatureState saved in snapshot.Creatures)
        {
            ExtendedCreatureState? extra = null;
            Creature? creature;

            if (saved.playerId.HasValue)
            {
                creature = remaining.FirstOrDefault(c => c.Player?.NetId == saved.playerId.Value);
                if (creature?.CombatId is { } combatId && extrasByCombatId.TryGetValue(combatId, out ExtendedCreatureState matchedExtra))
                    extra = matchedExtra;
            }
            else if (saved.monsterId != null &&
                    monsterExtrasById.TryGetValue(saved.monsterId, out Queue<ExtendedCreatureState>? queue) &&
                    queue.Count > 0)
            {
                ExtendedCreatureState matchedExtra = queue.Dequeue();
                extra = matchedExtra;
                creature = combat.GetCreature(matchedExtra.combatId)
                    ?? remaining.FirstOrDefault(c => c.Monster?.Id == saved.monsterId);
            }
            else
            {
                creature = saved.monsterId == null
                    ? null
                    : remaining.FirstOrDefault(c => c.Monster?.Id == saved.monsterId);
            }

            if (creature == null) continue;
            remaining.Remove(creature);
            matches.Add((creature, saved, extra));
        }
        return matches;
    }

    private static void RestorePowers(Creature creature, List<NetFullCombatState.PowerState> saved)
    {
        foreach (PowerModel existing in creature.Powers.ToList()) existing.RemoveInternal();
        foreach (var powerState in saved)
        {
            PowerModel? canonical = ModelDb.GetByIdOrNull<PowerModel>(powerState.id);
            if (canonical == null) continue;
            var power = (PowerModel)canonical.MutableClone();
            if (powerState.amount != 0)
            {
                power.ApplyInternal(creature, powerState.amount, silent: true);
            }
            else
            {
                // ApplyInternal intentionally ignores zero, but some counter powers remain
                // present at zero and are represented that way in the authoritative snapshot.
                PowerOwner.SetValue(power, creature);
                PowerAmount.SetValue(power, 0);
                creature.ApplyPowerInternal(power);
            }
        }
    }

    private static void RestoreCreatureExtras(
        CombatState combat, Creature creature, ExtendedCreatureState extra)
    {
        var powerExtras = extra.powers
            .GroupBy(p => p.id)
            .ToDictionary(g => g.Key, g => new Queue<ExtendedPowerState>(g));

        foreach (PowerModel power in creature.Powers)
        {
            if (!powerExtras.TryGetValue(power.Id, out Queue<ExtendedPowerState>? queue) || queue.Count == 0)
                continue;

            ExtendedPowerState saved = queue.Dequeue();
            power.AmountOnTurnStart = saved.amountOnTurnStart;
            PowerSkip.SetValue(power, saved.skipNextDurationTick);
            RestorePrimitiveFields(PowerInternal.GetValue(power), saved.internalFields);
        }

        MonsterModel? monster = creature.Monster;
        if (monster == null) return;
        monster.Rng = new Rng(new SerializableRng
        {
            counter = extra.monsterRngCounter,
            state0 = extra.monsterRngState0,
            state1 = extra.monsterRngState1,
            state2 = extra.monsterRngState2,
            state3 = extra.monsterRngState3
        });
        MonsterSpawned.SetValue(monster, extra.spawnedThisTurn);
        RestorePrimitiveFields(monster, extra.monsterFields, "monster");
        RestoreMonsterCreatureReferences(combat, monster, extra.monsterCreatureReferences);
        if (extra.monsterFields.Count > 0 || extra.monsterCreatureReferences.Count > 0)
            ReconnectLog.Write(
                $"Restored monster runtime state: combatId={creature.CombatId}, monster={extra.monsterId}, " +
                $"fields=[{string.Join(",", extra.monsterFields.Select(field => $"{field.name}={field.value}"))}], " +
                $"creatureRefs=[{string.Join(",", extra.monsterCreatureReferences.Select(reference =>
                    $"{reference.name}={(reference.hasCombatId ? reference.combatId.ToString() : "null")}"))}].");
        var machine = monster.MoveStateMachine;
        object? sm = machine;
        if (machine == null) return;
        SmPerformedFirst.SetValue(sm, extra.performedFirstMove);
        IDictionary states = (IDictionary)machine.States;
        ReconnectLog.Write(
            $"Restoring monster move state: combatId={creature.CombatId}, monster={extra.monsterId}, " +
            $"current={MonsterMoveStateSupport.Describe(extra.currentMove)}, " +
            $"next={MonsterMoveStateSupport.Describe(extra.nextMove)}, same={extra.currentAndNextMoveAreSame}, " +
            $"states=[{string.Join(",", states.Keys.Cast<object>())}].");
        MonsterMoveStateSupport.Restore(creature, extra);
        foreach (MoveFlagState flag in extra.moveFlags)
            if (states.Contains(flag.id) && states[flag.id] is { } move && MoveStateType.IsInstanceOfType(move))
                MovePerformed.SetValue(move, flag.performed);

        if (AccessTools.Property(SmType, "StateLog")?.GetValue(sm) is IList log)
        {
            log.Clear();
            foreach (string id in extra.stateLogIds)
                if (states.Contains(id)) log.Add(states[id]);
        }
    }

    private static void RestoreMonsterCreatureReferences(
        CombatState combat,
        MonsterModel monster,
        List<CreatureReferenceFieldState> references)
    {
        foreach (CreatureReferenceFieldState saved in references)
        {
            Type? declaring = monster.GetType();
            while (declaring != null && declaring.FullName != saved.declaringType) declaring = declaring.BaseType;
            FieldInfo? field = declaring?.GetField(
                saved.name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || !typeof(Creature).IsAssignableFrom(field.FieldType))
                throw new InvalidDataException(
                    $"Monster {monster.Id} creature reference field {saved.declaringType}.{saved.name} is unavailable.");

            Creature? value = null;
            if (saved.hasCombatId)
            {
                value = combat.GetCreature(saved.combatId)
                    ?? throw new InvalidDataException(
                        $"Monster {monster.Id} references missing combat creature {saved.combatId} " +
                        $"through {saved.declaringType}.{saved.name}.");
                if (!field.FieldType.IsInstanceOfType(value))
                    throw new InvalidDataException(
                        $"Monster {monster.Id} creature reference {saved.declaringType}.{saved.name} " +
                        $"cannot accept combat creature {saved.combatId}.");
            }
            field.SetValue(monster, value);
        }
    }

    internal static void ResetFailedTurnTransition()
    {
        CombatManager manager = CombatManager.Instance;
        EndingPhaseOne.SetValue(manager, false);
        EndingPhaseTwo.SetValue(manager, false);
        EnemyTurnStarted.SetValue(manager, false);
        InPlayerTurnSetup.SetValue(manager, false);
        DeferredEndTurnTransition.SetValue(manager, null);
    }

    private static void RestorePrimitiveFields(
        object? target, List<PrimitiveFieldState> fields, string context = "power")
    {
        if (target == null) return;
        foreach (PrimitiveFieldState saved in fields)
        {
            Type? declaring = target.GetType();
            while (declaring != null && declaring.FullName != saved.declaringType) declaring = declaring.BaseType;
            FieldInfo? field = declaring?.GetField(saved.name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            try
            {
                Type targetType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                object value = targetType.IsEnum
                    ? Enum.Parse(targetType, saved.value)
                    : targetType == typeof(string)
                        ? saved.value
                        : Convert.ChangeType(
                            saved.value, targetType, System.Globalization.CultureInfo.InvariantCulture);
                if (Nullable.GetUnderlyingType(field.FieldType) != null)
                    value = Activator.CreateInstance(field.FieldType, value)!;
                field.SetValue(target, value);
            }
            catch (Exception ex)
            {
                ReconnectLog.Write(
                    $"Skipped {context} field {saved.declaringType}.{saved.name}: {ex.Message}");
            }
        }
    }

    private static void RestorePlayer(
        Player player,
        NetFullCombatState.PlayerState saved,
        ExtendedPlayerCardState? extended,
        List<ExtendedRelicRuntimeState> relicRuntimeStates)
    {
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs == null) return;
        pcs.Energy = saved.energy;
        pcs.Stars = saved.stars;
        pcs.Phase = saved.phase;
        TurnNumber.SetValue(pcs, saved.turnNumber);
        player.Gold = saved.gold;
        player.PlayerRng.LoadFromSerializable(saved.rngSet);
        player.RelicGrabBag.LoadFromSerializable(saved.relicGrabBag);
        RestoreRelics(player, saved.relics);
        RestoreRelicRuntimeStates(player, relicRuntimeStates);
        RestorePotions(player, saved, extended);

        CombatState combat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat disappeared while restoring cards.");
        var oldCards = pcs.AllPiles.SelectMany(p => p.Cards).Distinct().ToList();
        foreach (CardPile pile in pcs.AllPiles) pile.Clear(silent: true);
        foreach (CardModel old in oldCards) combat.RemoveCard(old);
        var extrasByLocation = new Dictionary<(PileType pileType, int indexInPile), ExtendedCardState>();
        if (extended.HasValue && extended.Value.cards != null)
        {
            foreach (ExtendedCardState extra in extended.Value.cards)
                extrasByLocation[(extra.pileType, extra.indexInPile)] = extra;
        }

        foreach (NetFullCombatState.CombatPileState pileState in saved.piles)
        {
            CardPile? pile = pcs.AllPiles.FirstOrDefault(p => p.Type == pileState.pileType);
            if (pile == null) continue;

            for (int indexInPile = 0; indexInPile < pileState.cards.Count; indexInPile++)
            {
                NetFullCombatState.CardState cardState = pileState.cards[indexInPile];
                CardModel card = CardModel.FromSerializable(cardState.card);
                combat.AddCard(card, player);
                if (cardState.affliction != null)
                {
                    AfflictionModel? canonical = ModelDb.GetByIdOrNull<AfflictionModel>(cardState.affliction);
                    if (canonical != null)
                        card.AfflictInternal((AfflictionModel)canonical.MutableClone(), cardState.afflictionCount);
                }
                if (extrasByLocation.TryGetValue((pileState.pileType, indexInPile), out ExtendedCardState extra))
                    RestoreCardExtras(card, extra);
                pile.AddInternal(card, silent: true);
            }
        }

        RestoreOrbs(player, pcs, saved.orbs);
    }

    private static void RestoreRelics(Player player, List<NetFullCombatState.RelicState> saved)
    {
        bool sameRoster = player.Relics.Count == saved.Count &&
            player.Relics.Select(relic => relic.Id)
                .SequenceEqual(saved.Select(relic => relic.relic.Id));

        if (!sameRoster)
        {
            foreach (RelicModel relic in player.Relics.ToList())
                player.RemoveRelicInternal(relic, silent: true);
            foreach (NetFullCombatState.RelicState relic in saved)
                player.AddRelicInternal(RelicModel.FromSerializable(relic.relic), silent: true);
        }
        else
        {
            for (int index = 0; index < saved.Count; index++)
            {
                RelicModel restored = RelicModel.FromSerializable(saved[index].relic);
                RelicModel current = player.Relics[index];
                CopySavedRelicProperties(restored, current);
                current.FloorAddedToDeck = restored.FloorAddedToDeck;
            }
        }

        string expectedHash = HashRelics(saved.Select(relic => relic.relic));
        string actualHash = HashRelics(player.Relics.Select(relic => relic.ToSerializable()));
        if (actualHash != expectedHash)
            throw new InvalidOperationException(
                $"Relic restore mismatch for player {player.NetId}: expected={expectedHash}, actual={actualHash}.");
        ReconnectLog.Write(
            $"Restored {saved.Count} relic state(s) for player {player.NetId}: hash={actualHash}.");
    }

    private static void RestoreRelicRuntimeStates(
        Player player,
        List<ExtendedRelicRuntimeState> savedStates)
    {
        if (savedStates.Count != player.Relics.Count)
            throw new InvalidOperationException(
                $"Relic runtime roster mismatch for player {player.NetId}: " +
                $"expected={savedStates.Count}, actual={player.Relics.Count}.");

        foreach (ExtendedRelicRuntimeState saved in savedStates.OrderBy(state => state.relicIndex))
        {
            if (saved.relicIndex < 0 || saved.relicIndex >= player.Relics.Count)
                throw new InvalidDataException(
                    $"Relic runtime snapshot has invalid index {saved.relicIndex} for player {player.NetId}.");
            RelicModel relic = player.Relics[saved.relicIndex];
            if (relic.Id != saved.relicId)
                throw new InvalidOperationException(
                    $"Relic runtime identity mismatch for player {player.NetId} at index {saved.relicIndex}: " +
                    $"expected={saved.relicId}, actual={relic.Id}.");

            foreach (RelicRuntimeFieldState fieldState in saved.fields)
                RestoreRelicRuntimeField(relic, fieldState);
            RelicStackCount.SetValue(relic, saved.stackCount);
            relic.Status = saved.status;
            RelicDisplayAmountChanged.Invoke(relic, null);
        }

        List<ExtendedRelicRuntimeState> actual = player.Relics
            .Select((relic, index) => ExtendedReconnectCapture.CaptureRelicRuntimeState(player, relic, index))
            .ToList();
        string expectedHash = HashRelicRuntimeStates(savedStates.OrderBy(state => state.relicIndex));
        string actualHash = HashRelicRuntimeStates(actual);
        if (actualHash != expectedHash)
            throw new InvalidOperationException(
                $"Relic runtime restore mismatch for player {player.NetId}: " +
                $"expected={expectedHash}, actual={actualHash}.");
        ReconnectLog.Write(
            $"Restored {actual.Count} relic runtime state(s) for player {player.NetId}: hash={actualHash}.");
    }

    private static void RestoreRelicRuntimeField(RelicModel relic, RelicRuntimeFieldState saved)
    {
        Type? declaring = relic.GetType();
        while (declaring != null && declaring != typeof(RelicModel) && declaring.FullName != saved.declaringType)
            declaring = declaring.BaseType;
        FieldInfo? field = declaring?.GetField(
            saved.name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field == null || field.IsStatic || field.IsLiteral || field.IsInitOnly)
            throw new MissingFieldException(saved.declaringType, saved.name);
        string actualTypeName = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? string.Empty;
        if (actualTypeName != saved.typeName)
            throw new InvalidDataException(
                $"Relic runtime field type mismatch for {relic.Id} {saved.declaringType}.{saved.name}: " +
                $"expected={saved.typeName}, actual={actualTypeName}.");

        switch (saved.kind)
        {
            case RelicRuntimeFieldKind.NullReference:
                if (field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null)
                    throw new InvalidDataException(
                        $"Relic runtime snapshot assigns null to non-nullable field {saved.declaringType}.{saved.name}.");
                field.SetValue(relic, null);
                return;

            case RelicRuntimeFieldKind.EmptyCollection:
            {
                object? collection = field.GetValue(relic);
                if (collection == null)
                {
                    if (field.FieldType.IsArray)
                        collection = Array.CreateInstance(field.FieldType.GetElementType()!, 0);
                    else
                        collection = Activator.CreateInstance(field.FieldType)
                            ?? throw new InvalidOperationException(
                                $"Could not create empty relic runtime collection {field.FieldType.FullName}.");
                    field.SetValue(relic, collection);
                }
                AccessTools.Method(collection.GetType(), "Clear")?.Invoke(collection, null);
                return;
            }

            case RelicRuntimeFieldKind.Value:
            {
                Type targetType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                object value = targetType.IsEnum
                    ? Enum.Parse(targetType, saved.value)
                    : targetType == typeof(string)
                        ? saved.value
                        : Convert.ChangeType(
                            saved.value,
                            targetType,
                            System.Globalization.CultureInfo.InvariantCulture);
                if (Nullable.GetUnderlyingType(field.FieldType) != null)
                    value = Activator.CreateInstance(field.FieldType, value)!;
                field.SetValue(relic, value);
                return;
            }

            default:
                throw new InvalidDataException(
                    $"Unsupported relic runtime field kind {saved.kind} for {saved.declaringType}.{saved.name}.");
        }
    }

    private static string HashRelicRuntimeStates(IEnumerable<ExtendedRelicRuntimeState> states)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.WriteList(states.ToList());
        return Convert.ToHexString(SHA256.HashData(
            writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    private static void CopySavedRelicProperties(RelicModel source, RelicModel target)
    {
        var properties = new List<(PropertyInfo Property, int Order)>();
        for (Type? type = source.GetType(); type != null && typeof(RelicModel).IsAssignableFrom(type); type = type.BaseType)
        {
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                SavedPropertyAttribute? attribute = property.GetCustomAttribute<SavedPropertyAttribute>();
                if (attribute != null) properties.Add((property, attribute.order));
            }
        }

        foreach ((PropertyInfo property, _) in properties.OrderBy(entry => entry.Order))
        {
            MethodInfo? setter = property.GetSetMethod(nonPublic: true);
            if (setter == null) continue;
            setter.Invoke(target, new[] { property.GetValue(source) });
        }
    }

    private static string HashRelics(IEnumerable<SerializableRelic> relics)
    {
        List<SerializableRelic> entries = relics.ToList();
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.WriteInt(entries.Count);
        foreach (SerializableRelic relic in entries) writer.Write(relic);
        return Convert.ToHexString(SHA256.HashData(
            writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    private static void RestorePotions(
        Player player,
        NetFullCombatState.PlayerState saved,
        ExtendedPlayerCardState? extended)
    {
        List<ExtendedPotionSlotState> desired = extended?.potionSlots
            ?? saved.potions.Select((potion, index) => new ExtendedPotionSlotState
            {
                slotIndex = index,
                id = potion.id
            }).ToList();

        if (desired.Any(slot => slot.slotIndex < 0 || slot.slotIndex >= saved.maxPotionCount) ||
            desired.Select(slot => slot.slotIndex).Distinct().Count() != desired.Count)
            throw new InvalidDataException($"Invalid potion slot snapshot for player {player.NetId}.");

        var desiredBySlot = desired.ToDictionary(slot => slot.slotIndex, slot => slot.id);
        foreach (PotionModel potion in player.Potions.ToList())
        {
            int slotIndex = Enumerable.Range(0, player.PotionSlots.Count)
                .First(index => ReferenceEquals(player.PotionSlots[index], potion));
            if (!desiredBySlot.TryGetValue(slotIndex, out ModelId? desiredId) || desiredId != potion.Id)
                player.DiscardPotionInternal(potion);
        }

        if (player.MaxPotionCount < saved.maxPotionCount)
            player.AddToMaxPotionCount(saved.maxPotionCount - player.MaxPotionCount);
        else if (player.MaxPotionCount > saved.maxPotionCount)
            player.SubtractFromMaxPotionCount(player.MaxPotionCount - saved.maxPotionCount);

        foreach (ExtendedPotionSlotState slot in desired.OrderBy(slot => slot.slotIndex))
        {
            if (player.PotionSlots[slot.slotIndex]?.Id == slot.id) continue;
            PotionModel canonical = ModelDb.GetById<PotionModel>(slot.id);
            PotionProcureResult result = player.AddPotionInternal(canonical.ToMutable(), slot.slotIndex);
            if (!result.success)
                throw new InvalidOperationException(
                    $"Failed to restore potion {slot.id} in slot {slot.slotIndex} for player {player.NetId}.");
        }

        ReconnectLog.Write(
            $"Restored potion slots for player {player.NetId}: max={player.MaxPotionCount}, " +
            $"slots=[{string.Join(",", player.PotionSlots.Select(potion => potion?.Id.Entry ?? "-"))}].");
    }

    internal static void RestoreCardExtras(CardModel card, ExtendedCardState saved)
    {
        CostBase.SetValue(card.EnergyCost, saved.baseCost);
        CostCapturedX.SetValue(card.EnergyCost, saved.capturedX);
        CostModifiers.SetValue(card.EnergyCost, saved.modifiers.Select(m =>
            new LocalCostModifier(m.amount, m.type, m.expiration, m.reduceOnly)).ToList());
        CardTemporaryStarCosts.SetValue(card, saved.temporaryStarCosts.Select(cost => cost.ToModel()).ToList());
        var desired = saved.localKeywords.ToHashSet();
        foreach (CardKeyword keyword in Enum.GetValues<CardKeyword>())
        {
            bool present = card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword);
            if (desired.Contains(keyword) && !present) card.AddKeyword(keyword);
            else if (!desired.Contains(keyword) && present) card.RemoveKeyword(keyword);
        }
        foreach (CardDynamicVarState savedVariable in saved.dynamicVars)
        {
            if (!card.DynamicVars.TryGetValue(savedVariable.name, out DynamicVar? variable))
                throw new InvalidDataException(
                    $"Card {card.Id} runtime snapshot references missing dynamic variable {savedVariable.name}.");
            variable.BaseValue = decimal.Parse(
                savedVariable.baseValue, System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (CardRuntimeFieldState savedField in saved.runtimeFields)
            CardRuntimeFieldCodec.Restore(card, savedField);
        if (saved.runtimeFields.Count > 0)
            ReconnectLog.Write(
                $"Restored card runtime state: card={card.Id}, combatId={saved.combatCardId}, " +
                $"fields=[{string.Join(",", saved.runtimeFields.Select(field => $"{field.name}={field.kind}:{field.value}"))}], " +
                $"vars=[{string.Join(",", saved.dynamicVars.Select(variable => $"{variable.name}={variable.baseValue}"))}].");
    }

    private static void RestoreOrbs(Player player, PlayerCombatState pcs, List<NetFullCombatState.OrbState> saved)
    {
        var orbs = (IList)OrbList.GetValue(pcs.OrbQueue)!;
        orbs.Clear();

        foreach (var orbState in saved)
        {
            OrbModel? canonical = ModelDb.GetByIdOrNull<OrbModel>(orbState.id);
            if (canonical == null) continue;

            var orb = (OrbModel)canonical.MutableClone();
            OrbOwner.SetValue(orb, player);
            // v0.109 derives most values from combat hooks; Dark Orb still stores accumulated evoke value.
            SetNumericField(orb, "passive", orbState.passive);
            SetNumericField(orb, "evoke", orbState.evoke);
            orbs.Add(orb);
        }

        ReconnectLog.Write($"Restored {orbs.Count} orb model(s) for player {player.NetId}.");
    }

    private static void SetNumericField(object target, string namePart, int value)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase)
                    && (f.FieldType == typeof(int) || f.FieldType == typeof(decimal)));
            if (field == null) continue;
            field.SetValue(target, field.FieldType == typeof(decimal) ? (decimal)value : value);
            return;
        }
    }

    private static void RefreshLocalHand()
    {
        NPlayerHand? hand = NPlayerHand.Instance;
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        if (hand == null || combat == null) return;
        Player? me = combat.Players.FirstOrDefault(LocalContext.IsMe);
        if (me?.PlayerCombatState == null) return;

        foreach (CardModel oldCard in hand.ActiveHolders
                     .Select(h => h.CardNode?.Model)
                     .OfType<CardModel>()
                     .ToList())
            hand.Remove(oldCard);
        int index = 0;
        foreach (CardModel card in me.PlayerCombatState.Hand.Cards)
        {
            NCard? node = NCard.Create(card, ModelVisibility.Visible);
            if (node == null) continue;
            node.Scale = Vector2.One;
            hand.Add(node, index++);
        }
        hand.ForceRefreshCardIndices();
    }
}
