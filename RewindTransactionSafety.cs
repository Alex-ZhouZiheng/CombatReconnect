using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace CombatReconnect;

internal sealed record RewindSafetySnapshot(
    NetFullCombatState State,
    ExtendedReconnectMessage Extended,
    string Hash);

internal static class RewindTransactionGate
{
    private static readonly FieldInfo PlayerActionsDisabled =
        AccessTools.Field(typeof(CombatManager), "_playerActionsDisabled");
    private static readonly FieldInfo ReadyToEnd =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToEndTurn");

    internal static bool InputLocked { get; private set; }
    internal static bool IsolationActive { get; private set; }
    internal static string? ViolationReason { get; private set; }

    internal static void LockInput(string status)
    {
        InputLocked = true;
        ViolationReason = null;
        NPlayerHand.Instance?.CancelAllCardPlay();
        PlayerActionsDisabled.SetValue(CombatManager.Instance, true);
        RewindUiController.SetRewindControlsDisabled(true);
        RewindUiController.ShowTransactionStatus(status);
    }

    internal static void BeginIsolation()
    {
        IsolationActive = true;
        EnsureLocked();
    }

    internal static void AllowAbortDrain()
    {
        IsolationActive = false;
        PlayerActionsDisabled.SetValue(CombatManager.Instance, true);
        RunManager.Instance.ActionExecutor.Unpause();
        CombatManager.Instance.Unpause();
    }

    internal static void EnsureLocked()
    {
        if (!InputLocked) return;
        NPlayerHand.Instance?.CancelAllCardPlay();
        PlayerActionsDisabled.SetValue(CombatManager.Instance, true);
        CombatManager.Instance.Pause();
        RunManager.Instance.ActionExecutor.Pause();
    }

    internal static void ReportViolation(string operation)
    {
        ViolationReason ??= $"收到冻结边界之后的旧时间线动作：{operation}";
        ReconnectLog.Write(ViolationReason);
    }

    internal static void Unlock()
    {
        IsolationActive = false;
        InputLocked = false;
        ViolationReason = null;
        bool localReady = false;
        if (LocalContext.NetId is { } localId && ReadyToEnd.GetValue(CombatManager.Instance) is IEnumerable ready)
        {
            foreach (object item in ready)
            {
                if (item is Player player && player.NetId == localId)
                {
                    localReady = true;
                    break;
                }
            }
        }
        PlayerActionsDisabled.SetValue(CombatManager.Instance, localReady);
        RunManager.Instance.ActionExecutor.Unpause();
        CombatManager.Instance.Unpause();
        RewindUiController.SetRewindControlsDisabled(false);
        RewindUiController.HideTransactionStatus();
    }

    internal static void Reset()
    {
        IsolationActive = false;
        InputLocked = false;
        ViolationReason = null;
    }
}

internal static class RewindSafety
{
    private static readonly FieldInfo WaitingForResumption =
        AccessTools.Field(typeof(ActionQueueSet), "_actionsWaitingForResumption");
    private static readonly FieldInfo ReceivedChoices =
        AccessTools.Field(typeof(PlayerChoiceSynchronizer), "_receivedChoices");
    private static readonly FieldInfo RequestedActions =
        AccessTools.Field(typeof(ActionQueueSynchronizer), "_requestedActionsWaitingForPlayerTurn");
    private static readonly FieldInfo HookActions =
        AccessTools.Field(typeof(ActionQueueSynchronizer), "_hookActions");

    internal static async Task AwaitStableBoundary(string phase, CancellationToken cancellationToken)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(cancellationToken);
        await SyntheticStartTurnTracker.AwaitAll(phase, cancellationToken);
        await DeterministicReplayEngine.AwaitCombatQuiescence(
            phase, cancellationToken, expectedSide: CombatSide.Player);
        await DeterministicReplayEngine.AwaitLocalHandStability(phase, cancellationToken);
        // A turn transition can begin between the first task drain and the hand/UI check.
        // Require the tracked StartTurn set to remain empty once more before snapshot capture.
        await SyntheticStartTurnTracker.AwaitAll($"{phase} final check", cancellationToken);
        await DeterministicReplayEngine.AwaitCombatQuiescence(
            phase, cancellationToken, expectedSide: CombatSide.Player);
    }

    internal static RewindSafetySnapshot Capture()
    {
        ClientRejoinResponseMessage response = RunManager.Instance.GetRejoinMessage();
        if (response.combatState == null)
            throw new InvalidOperationException("安全边界处没有可用的战斗状态。");
        ExtendedReconnectMessage extended = ExtendedReconnectCapture.Capture();
        MonsterMoveStateSupport.ValidateSnapshot(extended, "safety boundary");
        string hash = ReplayPayloadCodec.Hash(ReplayPayloadCodec.Serialize(response.combatState, extended));
        return new RewindSafetySnapshot(response.combatState, extended, hash);
    }

    internal static void ClearTransientState()
    {
        NPlayerHand.Instance?.CancelAllCardPlay();
        ReplayVisualCleanup.ClearPlayedCards();
        ClearCollection(WaitingForResumption.GetValue(RunManager.Instance.ActionQueueSet));
        ClearCollection(ReceivedChoices.GetValue(RunManager.Instance.PlayerChoiceSynchronizer));
        ClearCollection(RequestedActions.GetValue(RunManager.Instance.ActionQueueSynchronizer));
        ClearCollection(HookActions.GetValue(RunManager.Instance.ActionQueueSynchronizer));
    }

    private static void ClearCollection(object? value)
    {
        if (value == null) return;
        AccessTools.Method(value.GetType(), "Clear")?.Invoke(value, null);
    }
}

[HarmonyPatch(typeof(NPlayerHand), "CanPlayCards")]
internal static class BlockCardInputDuringRewindPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!RewindTransactionGate.InputLocked) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.EnqueueWithoutSynchronizing),
    new[] { typeof(GameAction) })]
internal static class RejectOldTimelineActionPatch
{
    private static bool Prefix(GameAction gameAction)
    {
        if (!RewindTransactionGate.IsolationActive || DeterministicReplayEngine.IsReplaying) return true;
        RewindTransactionGate.ReportViolation(gameAction.GetType().Name);
        return false;
    }
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue),
    new[] { typeof(GameAction) })]
internal static class SuppressSyntheticReadyDuringReplayPatch
{
    private static bool Prefix(GameAction action)
    {
        if (!DeterministicReplayEngine.IsReplaying || action is not ReadyToBeginEnemyTurnAction)
            return true;

        ReconnectLog.Write("Suppressed synthetic ReadyToBeginEnemyTurnAction during deterministic replay.");
        return false;
    }
}

[HarmonyPatch]
internal static class UseSavedCardChoicesDuringReplayPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardSelectCmd"), "ShouldSelectLocalCard");

    private static bool Prefix(ref bool __result)
    {
        if (!DeterministicReplayEngine.IsReplaying) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class UseSavedRelicChoicesDuringReplayPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.RelicSelectCmd"), "ShouldSelectLocalRelic");

    private static bool Prefix(ref bool __result)
    {
        if (!DeterministicReplayEngine.IsReplaying) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class AllowReplayRemoteChoiceWaitInSingleplayerPatch
{
    private const string SingleplayerGuardMessage = "Cannot wait for remote choice in singleplayer!";

    private static MethodBase TargetMethod()
    {
        MethodInfo method = AccessTools.Method(
            typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice));
        Type stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new MissingMethodException("PlayerChoiceSynchronizer.WaitForRemoteChoice async state machine was not found.");
        return AccessTools.Method(stateMachine, "MoveNext");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.ToList();
        int messageIndex = codes.FindIndex(instruction =>
            instruction.opcode == OpCodes.Ldstr && Equals(instruction.operand, SingleplayerGuardMessage));
        if (messageIndex <= 0 ||
            (codes[messageIndex - 1].opcode != OpCodes.Bne_Un &&
             codes[messageIndex - 1].opcode != OpCodes.Bne_Un_S))
            throw new InvalidDataException("PlayerChoiceSynchronizer singleplayer guard IL was not recognized.");

        object continueLabel = codes[messageIndex - 1].operand;
        codes.Insert(messageIndex, new CodeInstruction(
            OpCodes.Call, AccessTools.PropertyGetter(typeof(DeterministicReplayEngine), nameof(DeterministicReplayEngine.IsReplaying))));
        codes.Insert(messageIndex + 1, new CodeInstruction(OpCodes.Brtrue, continueLabel));
        return codes;
    }
}

[HarmonyPatch(typeof(NPlayerHand), "OnCombatStateChanged")]
internal static class DeferHandRefreshDuringCombatRestorePatch
{
    private static bool Prefix() => !CombatStateRestorer.RestoreInProgress;
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice),
    new[] { typeof(GameAction) })]
internal static class SuppressSyntheticResumeDuringReplayPatch
{
    private static bool Prefix(GameAction action)
    {
        if (!DeterministicReplayEngine.IsReplaying) return true;
        ReconnectLog.Write($"Suppressed synthetic resume for replay action {action.Id}.");
        return false;
    }
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueueHookAction),
    new[] { typeof(GenericHookGameAction) })]
internal static class SuppressSyntheticHookEnqueueDuringReplayPatch
{
    private static bool Prefix(GenericHookGameAction action)
    {
        if (!DeterministicReplayEngine.IsReplaying) return true;
        ReconnectLog.Write($"Suppressed synthetic hook enqueue during replay: hook={action.HookId}, owner={action.OwnerId}.");
        return false;
    }
}

[HarmonyPatch(typeof(GameActionPlayerChoiceContext), nameof(GameActionPlayerChoiceContext.SignalPlayerChoiceEnded))]
internal static class ObserveGameActionChoiceConsumptionPatch
{
    private static void Prefix(GameActionPlayerChoiceContext __instance)
        => ReplayChoiceStateMachine.MarkChoiceConsumed(__instance.Action);
}

[HarmonyPatch(typeof(HookPlayerChoiceContext), nameof(HookPlayerChoiceContext.SignalPlayerChoiceEnded))]
internal static class ObserveHookChoiceConsumptionPatch
{
    private static void Prefix(HookPlayerChoiceContext __instance)
        => ReplayChoiceStateMachine.MarkChoiceConsumed(__instance.GameAction);
}
