using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace CombatReconnect;

internal static class RewindSelfTest
{
    private const int EventsPerBatch = 32;
    private static bool _running;

    internal static void Request()
    {
        if (_running || RewindCoordinator.IsRewinding || RewindTransactionGate.InputLocked)
        {
            RewindUiController.ShowTransientStatus("回滚或自检正在运行，请稍候……");
            return;
        }
        if (!CombatManager.Instance.IsInProgress)
        {
            RewindUiController.ShowTransientStatus("自检只能在战斗中运行。");
            return;
        }
        if (RunManager.Instance.DebugOnlyGetState()?.Players.Count != 1)
        {
            RewindUiController.ShowTransientStatus("F6 自检仅用于单人战斗；多人房间请使用正常回滚投票。");
            return;
        }
        if (TurnReplayLedger.Anchor is not { RoundNumber: >= 2 })
        {
            RewindUiController.ShowTransientStatus("请至少进行到第 2 回合，再按 F6 运行回滚自检。");
            return;
        }

        _running = true;
        _ = Run();
    }

    private static async Task Run()
    {
        var stopwatch = Stopwatch.StartNew();
        RewindSafetySnapshot? safety = null;
        bool replayPassed = false;
        bool recoveryPassed = false;
        string detail = string.Empty;
        int eventCount = 0;
        int anchorBytes = 0;
        int targetRound = 0;

        try
        {
            RewindTransactionGate.LockInput("F6 回滚自检：正在等待安全边界……");
            using var boundaryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RewindSafety.AwaitStableBoundary("F6 pre-test boundary", boundaryCancellation.Token);

            TurnReplayAnchor target = TurnReplayLedger.Anchor
                ?? throw new InvalidOperationException("没有可用的目标回合锚点。");
            RewindReplayPlan plan = TurnReplayLedger.BuildRewindPlan(target);
            if (plan.Events.Count == 0)
                throw new InvalidOperationException("当前锚点没有跨回合事件；请再完成一个回合后测试。");

            targetRound = target.RoundNumber;
            eventCount = plan.Events.Count;
            safety = RewindSafety.Capture();
            RewindTransactionGate.BeginIsolation();
            RewindUiController.ShowTransactionStatus(
                $"F6 回滚自检：正在模拟客户端传输并重放到第 {targetRound} 回合……");

            WireReplayPackage wire = BuildWirePackage(plan);
            anchorBytes = wire.Prepare.anchorBytes;
            VerifyCounterIsolation();
            RewindSafety.ClearTransientState();

            string replayHash = await DeterministicReplayEngine.RestoreAndReplay(
                wire.AnchorState,
                wire.AnchorExtended,
                wire.Events,
                wire.Prepare.finalActionId,
                wire.Prepare.finalHookId,
                wire.Prepare.finalChecksumId,
                showReplayVisuals: true,
                cancellationToken: default,
                expectedFinalRoundNumber: wire.Prepare.finalRoundNumber,
                expectedFinalSide: wire.Prepare.finalSide,
                expectedPlayerTurns: wire.Prepare.participants
                    .Select((id, index) => (id, turn: wire.Prepare.finalPlayerTurnNumbers[index]))
                    .ToDictionary(pair => pair.id, pair => pair.turn));
            RewindTransactionGate.EnsureLocked();

            if (replayHash != wire.Prepare.finalStateHash)
                throw new InvalidOperationException(
                    $"回放 hash 不匹配：expected={wire.Prepare.finalStateHash}, actual={replayHash}");
            if (RunManager.Instance.ActionQueueSet.NextActionId != wire.Prepare.finalActionId ||
                RunManager.Instance.ActionQueueSynchronizer.NextHookId != wire.Prepare.finalHookId ||
                RunManager.Instance.ChecksumTracker.NextId != wire.Prepare.finalChecksumId)
                throw new InvalidOperationException(
                    $"回放计数器不匹配：actual={RunManager.Instance.ActionQueueSet.NextActionId}/" +
                    $"{RunManager.Instance.ActionQueueSynchronizer.NextHookId}/" +
                    $"{RunManager.Instance.ChecksumTracker.NextId}, expected={wire.Prepare.finalActionId}/" +
                    $"{wire.Prepare.finalHookId}/{wire.Prepare.finalChecksumId}");
            replayPassed = true;
        }
        catch (Exception ex)
        {
            detail = ex.ToString();
        }
        finally
        {
            if (safety != null)
            {
                RewindUiController.ShowTransactionStatus("F6 回滚自检：正在恢复测试前状态……");
                (bool success, string _, string recoveryDetail) =
                    await MultiplayerRewindCoordinator.RestoreSafetyForSelfTest(safety);
                recoveryPassed = success;
                if (!success)
                    detail += $"\nSafety recovery failed: {recoveryDetail}";
            }
            else
            {
                recoveryPassed = true;
            }

            if (recoveryPassed) RewindTransactionGate.Unlock();
            else RewindTransactionGate.EnsureLocked();
            stopwatch.Stop();
            string summary = replayPassed && recoveryPassed
                ? $"回滚自检通过\n目标：第 {targetRound} 回合 · {eventCount} 个事件 · {anchorBytes} 字节\n" +
                  $"测试前状态已恢复，用时 {stopwatch.Elapsed.TotalSeconds:F1} 秒"
                : $"回滚自检失败，但测试前状态{(recoveryPassed ? "已恢复" : "恢复失败")}\n" +
                  "详细原因已写入 CombatReconnect.log";
            RewindUiController.ShowTransientStatus(summary, 9000);
            ReconnectLog.Write(
                $"SELFTEST {(replayPassed && recoveryPassed ? "PASS" : "FAIL")}: " +
                $"round={targetRound}, events={eventCount}, anchorBytes={anchorBytes}, " +
                $"replay={replayPassed}, recovery={recoveryPassed}, elapsedMs={stopwatch.ElapsedMilliseconds}. " +
                detail);
            _running = false;
        }
    }

    private static WireReplayPackage BuildWirePackage(RewindReplayPlan plan)
    {
        const string transactionId = "local-self-test";
        TurnReplayAnchor root = plan.Root;
        TurnReplayAnchor target = plan.Target;
        VerifyCompressionCodec();
        VerifyReconnectAttemptProtocol(target.Epoch, target.PayloadHash);
        RewindPrepareMessage prepare = RoundTrip(new RewindPrepareMessage
        {
            protocolVersion = CombatReconnectMod.ProtocolVersion,
            transactionId = transactionId,
            epoch = target.Epoch,
            replayStartIndex = target.ReplayEventIndex,
            baseReplayStartIndex = root.ReplayEventIndex,
            eventCount = plan.Events.Count,
            anchorBytes = root.Payload.Length,
            encodedAnchorBytes = root.Payload.Length,
            anchorTransferId = "local-self-test-transfer",
            anchorHash = root.PayloadHash,
            encodedAnchorHash = root.PayloadHash,
            anchorCompression = ReplayAnchorCompression.None,
            finalStateHash = target.PayloadHash,
            finalActionId = target.ExtendedState.nextActionId,
            finalHookId = target.ExtendedState.nextHookId,
            finalChecksumId = target.ExtendedState.nextChecksumId,
            finalRoundNumber = target.RoundNumber,
            finalSide = target.ExtendedState.currentSide,
            generation = 1,
            participants = target.CombatState.Players.Select(player => player.playerId).ToList(),
            finalPlayerTurnNumbers = target.CombatState.Players.Select(player => player.turnNumber).ToList()
        });
        VerifyCommitProtocol(transactionId, target.Epoch, target.PayloadHash);

        byte[] payload = SidecarReplayTransport.RoundTripForSelfTest(root.Payload);
        if (payload.Length != prepare.anchorBytes || ReplayPayloadCodec.Hash(payload) != prepare.anchorHash)
            throw new InvalidDataException("模拟客户端收到的锚点长度或 SHA-256 不匹配。");

        var receivedEvents = new SortedDictionary<int, DeterministicInputEvent>();
        for (int index = 0; index < plan.Events.Count; index += EventsPerBatch)
        {
            List<DeterministicInputEvent> values = plan.Events.Skip(index).Take(EventsPerBatch).ToList();
            RewindEventBatchMessage batch = RoundTrip(new RewindEventBatchMessage
            {
                transactionId = transactionId,
                epoch = target.Epoch,
                firstSequence = index,
                isLast = index + values.Count == plan.Events.Count,
                events = values
            });
            foreach (DeterministicInputEvent input in batch.events)
            {
                if (!receivedEvents.TryAdd(input.sequence, input))
                    throw new InvalidDataException($"模拟客户端收到重复事件 {input.sequence}。");
            }
        }
        if (receivedEvents.Count != prepare.eventCount ||
            !Enumerable.Range(0, prepare.eventCount).All(receivedEvents.ContainsKey))
            throw new InvalidDataException("模拟客户端收到的事件序列不连续。");

        (NetFullCombatState state, ExtendedReconnectMessage extended) = ReplayPayloadCodec.Deserialize(payload);
        MonsterMoveStateSupport.ValidateSnapshot(extended, "F6 serialized replay root");
        VerifyEncounterSnapshotRoundTrip(root.ExtendedState, extended);
        VerifyMoveSnapshotRoundTrip(root.ExtendedState, extended);
        VerifyPotionSnapshotRoundTrip(root.ExtendedState, extended);
        VerifyCardSnapshotRoundTrip(root.ExtendedState, extended);
        VerifyHistorySnapshotRoundTrip(root.ExtendedState, extended);
        return new WireReplayPackage(
            prepare,
            state,
            extended,
            Enumerable.Range(0, prepare.eventCount).Select(sequence => receivedEvents[sequence]).ToList());
    }

    private static void VerifyHistorySnapshotRoundTrip(
        ExtendedReconnectMessage expected, ExtendedReconnectMessage actual)
    {
        string expectedHash = CombatHistoryStateSupport.Fingerprint(expected.combatHistory);
        string actualHash = CombatHistoryStateSupport.Fingerprint(actual.combatHistory);
        if (expectedHash != actualHash)
            throw new InvalidDataException(
                $"CombatHistory 序列化往返不一致：expected={expectedHash}, actual={actualHash}, " +
                $"firstDifference={CombatHistoryStateSupport.DescribeFirstDifference(expected.combatHistory, actual.combatHistory)}");

        if (actual.combatHistory.entries.Count > 0)
        {
            ExtendedCombatHistoryState changed = actual.combatHistory;
            changed.entries = actual.combatHistory.entries.ToList();
            CombatHistoryEntryState entry = changed.entries[0];
            entry.roundNumber++;
            changed.entries[0] = entry;
            string difference = CombatHistoryStateSupport.DescribeFirstDifference(actual.combatHistory, changed);
            if (!difference.StartsWith("entries[0].roundNumber:", StringComparison.Ordinal))
                throw new InvalidDataException($"CombatHistory 差异定位自检失败：{difference}");
        }
    }

    private static void VerifyCompressionCodec()
    {
        byte[] raw = Enumerable.Repeat((byte)0x5A, 16 * 1024).ToArray();
        EncodedReplayAnchor encoded = ReplayAnchorCompressionCodec.EncodeForSelfTest(raw, supportsBrotli: true);
        if (encoded.Compression != ReplayAnchorCompression.Brotli || encoded.Payload.Length >= raw.Length)
            throw new InvalidDataException("Brotli 自检未生成更小的载荷。");
        byte[] decoded = ReplayAnchorCompressionCodec.Decode(encoded.Payload, encoded.Compression, raw.Length);
        if (!decoded.AsSpan().SequenceEqual(raw))
            throw new InvalidDataException("Brotli 自检往返不一致。");
        bool rejected = false;
        try
        {
            ReplayAnchorCompressionCodec.Decode(encoded.Payload, encoded.Compression, raw.Length - 1);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        if (!rejected)
            throw new InvalidDataException("Brotli 边界自检未拒绝错误长度。");
    }

    private static void VerifyEncounterSnapshotRoundTrip(
        ExtendedReconnectMessage expected, ExtendedReconnectMessage actual)
    {
        if (expected.encounterId != actual.encounterId ||
            expected.actRoomStates.Count != actual.actRoomStates.Count)
            throw new InvalidDataException("遭遇状态序列化往返不一致。");

        for (int i = 0; i < expected.actRoomStates.Count; i++)
        {
            ExtendedActRoomState left = expected.actRoomStates[i];
            ExtendedActRoomState right = actual.actRoomStates[i];
            if (left.actIndex != right.actIndex ||
                left.normalEncountersVisited != right.normalEncountersVisited ||
                left.eliteEncountersVisited != right.eliteEncountersVisited ||
                left.bossEncountersVisited != right.bossEncountersVisited ||
                left.eventsVisited != right.eventsVisited ||
                left.mapPointHistoryCount != right.mapPointHistoryCount)
                throw new InvalidDataException($"第 {left.actIndex} 幕房间游标序列化往返不一致。");
        }
    }

    private static void VerifyPotionSnapshotRoundTrip(
        ExtendedReconnectMessage expected, ExtendedReconnectMessage actual)
    {
        foreach (ExtendedPlayerCardState saved in expected.playerCards)
        {
            int index = actual.playerCards.FindIndex(player => player.playerId == saved.playerId);
            if (index < 0)
                throw new InvalidDataException($"模拟传输后缺少玩家 {saved.playerId} 的药水槽状态。");
            List<ExtendedPotionSlotState> roundTripped = actual.playerCards[index].potionSlots;
            if (saved.potionSlots.Count != roundTripped.Count ||
                saved.potionSlots.Where((slot, slotIndex) =>
                    slot.slotIndex != roundTripped[slotIndex].slotIndex ||
                    slot.id != roundTripped[slotIndex].id).Any())
                throw new InvalidDataException($"玩家 {saved.playerId} 的药水槽状态序列化往返不一致。");
        }
    }

    private static void VerifyCardSnapshotRoundTrip(
        ExtendedReconnectMessage expected, ExtendedReconnectMessage actual)
    {
        foreach (ExtendedPlayerCardState savedPlayer in expected.playerCards)
        {
            int playerIndex = actual.playerCards.FindIndex(player => player.playerId == savedPlayer.playerId);
            if (playerIndex < 0)
                throw new InvalidDataException($"模拟传输后缺少玩家 {savedPlayer.playerId} 的卡牌运行时状态。");

            List<ExtendedCardState> roundTripped = actual.playerCards[playerIndex].cards;
            if (savedPlayer.cards.Count != roundTripped.Count)
                throw new InvalidDataException($"玩家 {savedPlayer.playerId} 的卡牌运行时状态数量不一致。");

            for (int index = 0; index < savedPlayer.cards.Count; index++)
            {
                ExtendedCardState saved = savedPlayer.cards[index];
                ExtendedCardState received = roundTripped[index];
                if (!Serialize(saved).SequenceEqual(Serialize(received)))
                    throw new InvalidDataException(
                        $"玩家 {savedPlayer.playerId} 的卡牌 {saved.combatCardId} 运行时状态序列化往返不一致。");
            }
        }
    }

    private static void VerifyMoveSnapshotRoundTrip(
        ExtendedReconnectMessage expected, ExtendedReconnectMessage actual)
    {
        foreach (ExtendedCreatureState saved in expected.creatures.Where(creature => creature.isMonster))
        {
            int index = actual.creatures.FindIndex(creature => creature.isMonster && creature.combatId == saved.combatId);
            if (index < 0)
                throw new InvalidDataException($"模拟传输后缺少怪物 {saved.monsterId} ({saved.combatId}) 的 move 状态。");
            ExtendedCreatureState roundTripped = actual.creatures[index];
            if (!MoveEquals(saved.currentMove, roundTripped.currentMove) ||
                !MoveEquals(saved.nextMove, roundTripped.nextMove) ||
                saved.currentAndNextMoveAreSame != roundTripped.currentAndNextMoveAreSame ||
                !SerializeList(saved.monsterFields).SequenceEqual(SerializeList(roundTripped.monsterFields)) ||
                !SerializeList(saved.monsterCreatureReferences)
                    .SequenceEqual(SerializeList(roundTripped.monsterCreatureReferences)))
                throw new InvalidDataException(
                    $"怪物 {saved.monsterId} ({saved.combatId}) 的运行时状态序列化往返不一致。");
        }

        static bool MoveEquals(SavedMoveState left, SavedMoveState right)
            => left.kind == right.kind && TextEquals(left.id, right.id) &&
               TextEquals(left.followUpStateId, right.followUpStateId) &&
               left.mustPerformOnce == right.mustPerformOnce &&
               left.performedAtLeastOnce == right.performedAtLeastOnce &&
               left.handlerOwner == right.handlerOwner &&
               TextEquals(left.handlerOwnerType, right.handlerOwnerType) &&
               TextEquals(left.handlerMethod, right.handlerMethod) &&
               TextEquals(left.unsupportedReason, right.unsupportedReason);

        static bool TextEquals(string? left, string? right)
            => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

        static byte[] SerializeList<T>(List<T> values) where T : IPacketSerializable, new()
        {
            var writer = new PacketWriter { WarnOnGrow = false };
            writer.WriteList(values);
            return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
        }
    }

    private static void VerifyCounterIsolation()
    {
        RunManager manager = RunManager.Instance;
        uint actionBefore = manager.ActionQueueSet.NextActionId;
        uint hookBefore = manager.ActionQueueSynchronizer.NextHookId;
        ReconnectCounterSyncMessage intrusive = RoundTrip(new ReconnectCounterSyncMessage
        {
            nextActionId = actionBefore + 100,
            nextHookId = hookBefore + 100,
            choiceIds = manager.PlayerChoiceSynchronizer.ChoiceIds.ToList(),
            rewardIds = manager.RewardsSetSynchronizer.GetNextRewardIds().ToList()
        });
        bool applied = CounterSyncExchange.TryApplyForSelfTest(intrusive);
        if (applied || manager.ActionQueueSet.NextActionId != actionBefore ||
            manager.ActionQueueSynchronizer.NextHookId != hookBefore)
            throw new InvalidOperationException("回滚事务没有隔离普通计数器同步消息。");
    }

    private static RewindPrepareMessage RoundTrip(RewindPrepareMessage value)
    {
        PacketReader reader = CreateReader(value);
        var result = new RewindPrepareMessage();
        result.Deserialize(reader);
        return result;
    }

    private static RewindEventBatchMessage RoundTrip(RewindEventBatchMessage value)
    {
        PacketReader reader = CreateReader(value);
        var result = new RewindEventBatchMessage();
        result.Deserialize(reader);
        return result;
    }

    private static ReconnectCounterSyncMessage RoundTrip(ReconnectCounterSyncMessage value)
    {
        PacketReader reader = CreateReader(value);
        var result = new ReconnectCounterSyncMessage();
        result.Deserialize(reader);
        return result;
    }

    private static void VerifyCommitProtocol(string transactionId, ulong epoch, string hash)
    {
        RewindCommitAckMessage ack = RoundTrip(new RewindCommitAckMessage
        {
            transactionId = transactionId, epoch = epoch, generation = 1,
            success = true, stateHash = hash, detail = string.Empty
        });
        RewindCommitCompleteMessage complete = RoundTrip(new RewindCommitCompleteMessage
        {
            transactionId = transactionId, epoch = epoch, generation = 1, stateHash = hash
        });
        if (!ack.success || ack.transactionId != transactionId || ack.stateHash != hash ||
            complete.transactionId != transactionId || complete.stateHash != hash)
            throw new InvalidDataException("两阶段提交消息序列化往返不一致。");
    }

    private static void VerifyReconnectAttemptProtocol(ulong epoch, string hash)
    {
        const int generation = 7;
        const string transferId = "reconnect-self-test-transfer";

        PacketReader eventReader = CreateReader(new ReplayEventBatchMessage
        {
            epoch = epoch, generation = generation, transferId = transferId,
            firstSequence = 0, isLast = true, events = new()
        });
        var eventBatch = new ReplayEventBatchMessage();
        eventBatch.Deserialize(eventReader);

        PacketReader readyReader = CreateReader(new ReplayReadyMessage
        {
            epoch = epoch, generation = generation, transferId = transferId,
            success = true, stateHash = hash, detail = string.Empty
        });
        var ready = new ReplayReadyMessage();
        ready.Deserialize(readyReader);

        PacketReader commitReader = CreateReader(new ReplayCommitMessage
        {
            epoch = epoch, generation = generation, transferId = transferId, stateHash = hash
        });
        var commit = new ReplayCommitMessage();
        commit.Deserialize(commitReader);

        PacketReader abortReader = CreateReader(new ReplayAbortMessage
        {
            epoch = epoch, generation = generation, transferId = transferId, reason = "self-test"
        });
        var abort = new ReplayAbortMessage();
        abort.Deserialize(abortReader);

        if (eventBatch.epoch != epoch || eventBatch.generation != generation || eventBatch.transferId != transferId ||
            ready.epoch != epoch || ready.generation != generation || ready.transferId != transferId ||
            commit.epoch != epoch || commit.generation != generation || commit.transferId != transferId ||
            abort.epoch != epoch || abort.generation != generation || abort.transferId != transferId)
            throw new InvalidDataException("重连事务标识在消息序列化往返后发生变化。");
    }

    private static RewindCommitAckMessage RoundTrip(RewindCommitAckMessage value)
    {
        PacketReader reader = CreateReader(value);
        var result = new RewindCommitAckMessage();
        result.Deserialize(reader);
        return result;
    }

    private static RewindCommitCompleteMessage RoundTrip(RewindCommitCompleteMessage value)
    {
        PacketReader reader = CreateReader(value);
        var result = new RewindCommitCompleteMessage();
        result.Deserialize(reader);
        return result;
    }

    private static PacketReader CreateReader(IPacketSerializable value)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        value.Serialize(writer);
        var reader = new PacketReader();
        reader.Reset(writer.Buffer.AsSpan(0, writer.BytePosition).ToArray());
        return reader;
    }

    private static byte[] Serialize(IPacketSerializable value)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        value.Serialize(writer);
        return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    private sealed record WireReplayPackage(
        RewindPrepareMessage Prepare,
        NetFullCombatState AnchorState,
        ExtendedReconnectMessage AnchorExtended,
        IReadOnlyList<DeterministicInputEvent> Events);
}
