using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;

namespace CombatReconnect;

internal sealed class TransientRelicSnapshotException : InvalidOperationException
{
    internal TransientRelicSnapshotException(string message) : base(message) { }
}

public struct ExtendedReconnectMessage : INetMessage, IPacketSerializable
{
    public uint nextActionId;
    public uint nextHookId;
    public uint nextChecksumId;
    public uint nextCreatureId;
    public int roundNumber;
    public CombatSide currentSide;
    public ActionSynchronizerCombatState actionSynchronizerState;
    public List<ulong> playersReadyToEndTurn;
    public List<ulong> playersReadyToBeginEnemyTurn;
    public List<ulong> playersTakingExtraTurn;
    public ModelId encounterId;
    public List<ExtendedActRoomState> actRoomStates;
    public List<ExtendedCreatureState> creatures;
    public List<ExtendedRelicRuntimeState> relicRuntimeStates;
    public List<ExtendedPlayerCardState> playerCards;
    public ExtendedCombatHistoryState combatHistory;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(nextActionId);
        writer.WriteUInt(nextHookId);
        writer.WriteUInt(nextChecksumId);
        writer.WriteUInt(nextCreatureId);
        writer.WriteInt(roundNumber);
        writer.WriteEnum(currentSide);
        writer.WriteEnum(actionSynchronizerState);
        WriteUlongs(writer, playersReadyToEndTurn);
        WriteUlongs(writer, playersReadyToBeginEnemyTurn);
        WriteUlongs(writer, playersTakingExtraTurn);
        writer.WriteModelEntry(encounterId);
        writer.WriteList(actRoomStates);
        writer.WriteList(creatures);
        writer.WriteList(relicRuntimeStates);
        writer.WriteList(playerCards);
        writer.Write(combatHistory);
    }

    public void Deserialize(PacketReader reader)
    {
        nextActionId = reader.ReadUInt();
        nextHookId = reader.ReadUInt();
        nextChecksumId = reader.ReadUInt();
        nextCreatureId = reader.ReadUInt();
        roundNumber = reader.ReadInt();
        currentSide = reader.ReadEnum<CombatSide>();
        actionSynchronizerState = reader.ReadEnum<ActionSynchronizerCombatState>();
        playersReadyToEndTurn = ReadUlongs(reader);
        playersReadyToBeginEnemyTurn = ReadUlongs(reader);
        playersTakingExtraTurn = ReadUlongs(reader);
        encounterId = reader.ReadModelIdAssumingType<EncounterModel>();
        actRoomStates = reader.ReadList<ExtendedActRoomState>();
        creatures = reader.ReadList<ExtendedCreatureState>();
        relicRuntimeStates = reader.ReadList<ExtendedRelicRuntimeState>();
        playerCards = reader.ReadList<ExtendedPlayerCardState>();
        combatHistory = reader.Read<ExtendedCombatHistoryState>();
    }

    private static void WriteUlongs(PacketWriter writer, List<ulong>? values)
    {
        writer.WriteInt(values?.Count ?? 0);
        if (values == null) return;
        foreach (ulong value in values) writer.WriteULong(value);
    }

    private static List<ulong> ReadUlongs(PacketReader reader)
    {
        int count = reader.ReadInt();
        var result = new List<ulong>(count);
        for (int i = 0; i < count; i++) result.Add(reader.ReadULong());
        return result;
    }
}

public struct ExtendedActRoomState : IPacketSerializable
{
    public int actIndex;
    public int normalEncountersVisited;
    public int eliteEncountersVisited;
    public int bossEncountersVisited;
    public int eventsVisited;
    public int mapPointHistoryCount;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(actIndex);
        writer.WriteInt(normalEncountersVisited);
        writer.WriteInt(eliteEncountersVisited);
        writer.WriteInt(bossEncountersVisited);
        writer.WriteInt(eventsVisited);
        writer.WriteInt(mapPointHistoryCount);
    }

    public void Deserialize(PacketReader reader)
    {
        actIndex = reader.ReadInt();
        normalEncountersVisited = reader.ReadInt();
        eliteEncountersVisited = reader.ReadInt();
        bossEncountersVisited = reader.ReadInt();
        eventsVisited = reader.ReadInt();
        mapPointHistoryCount = reader.ReadInt();
    }
}

public struct ReconnectCounterSyncMessage : INetMessage, IPacketSerializable
{
    public uint nextActionId;
    public uint nextHookId;
    public List<uint> choiceIds;
    public List<int> rewardIds;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(nextActionId);
        writer.WriteUInt(nextHookId);
        writer.WriteInt(choiceIds.Count);
        foreach (uint id in choiceIds) writer.WriteUInt(id);
        writer.WriteInt(rewardIds.Count);
        foreach (int id in rewardIds) writer.WriteInt(id);
    }

    public void Deserialize(PacketReader reader)
    {
        nextActionId = reader.ReadUInt();
        nextHookId = reader.ReadUInt();
        int choiceCount = reader.ReadInt();
        choiceIds = new List<uint>(choiceCount);
        for (int i = 0; i < choiceCount; i++) choiceIds.Add(reader.ReadUInt());
        int rewardCount = reader.ReadInt();
        rewardIds = new List<int>(rewardCount);
        for (int i = 0; i < rewardCount; i++) rewardIds.Add(reader.ReadInt());
    }
}

internal static class RunEncounterStateSupport
{
    private static readonly PropertyInfo RunActs = AccessTools.Property(typeof(RunState), "Acts");
    private static readonly FieldInfo ActRooms = AccessTools.Field(typeof(ActModel), "_rooms");
    private static readonly FieldInfo NormalEncounters = AccessTools.Field(typeof(RoomSet), "normalEncounters");
    private static readonly FieldInfo EliteEncounters = AccessTools.Field(typeof(RoomSet), "eliteEncounters");
    private static readonly FieldInfo BossEncounter = AccessTools.Field(typeof(RoomSet), "_boss");
    private static readonly FieldInfo SecondBossEncounter =
        AccessTools.Field(typeof(RoomSet), "<SecondBoss>k__BackingField");
    private static readonly FieldInfo MapPointHistory =
        AccessTools.Field(typeof(RunState), "_mapPointHistory");

    internal static List<ExtendedActRoomState> Capture(IRunState run)
    {
        List<object> acts = GetActs(run);
        var result = new List<ExtendedActRoomState>(acts.Count);
        for (int i = 0; i < acts.Count; i++)
        {
            object rooms = GetRooms(acts[i]);
            result.Add(new ExtendedActRoomState
            {
                actIndex = i,
                normalEncountersVisited = GetCounter(rooms, "NormalEncountersVisited"),
                eliteEncountersVisited = GetCounter(rooms, "EliteEncountersVisited"),
                bossEncountersVisited = GetCounter(rooms, "BossEncountersVisited"),
                eventsVisited = GetCounter(rooms, "EventsVisited"),
                mapPointHistoryCount = GetMapPointHistoryCount(run, i)
            });
        }
        return result;
    }

    internal static void Restore(
        RunState run,
        CombatState combat,
        ExtendedReconnectMessage extended)
    {
        if (combat.Encounter?.Id != extended.encounterId)
            throw new InvalidDataException(
                $"Encounter mismatch before restore: anchor={extended.encounterId}, " +
                $"live={combat.Encounter?.Id}. Native combat entry did not rebuild the anchor encounter.");

        RestoreRoomStates(run, extended.actRoomStates);

        List<ExtendedActRoomState> restored = Capture(run);
        if (!RoomStatesEqual(extended.actRoomStates, restored))
            throw new InvalidDataException("Encounter metadata restore validation failed.");

        ReconnectLog.Write(
            $"Restored encounter metadata: encounter={extended.encounterId}, " +
            $"acts=[{string.Join(",", restored.Select(Describe))}].");
    }

    internal static void PrepareForSyntheticCombatEntry(
        RunState run,
        ExtendedReconnectMessage extended)
    {
        ValidateMapPointHistory(run, extended.actRoomStates, "before synthetic combat entry");
        RestoreRoomStates(run, extended.actRoomStates);
        int savedIndex = extended.actRoomStates.FindIndex(
            state => state.actIndex == run.CurrentActIndex);
        if (savedIndex < 0)
            throw new InvalidDataException(
                $"Anchor contains no room state for current act {run.CurrentActIndex}.");

        RoomSet rooms = GetRooms(GetActs(run)[run.CurrentActIndex]);
        ExtendedActRoomState current = extended.actRoomStates[savedIndex];
        string? category = null;
        if (MatchesPreviousEncounter(
                (IEnumerable<EncounterModel>)NormalEncounters.GetValue(rooms)!,
                current.normalEncountersVisited,
                extended.encounterId))
        {
            SetCounter(rooms, "NormalEncountersVisited", current.normalEncountersVisited - 1);
            category = "normal";
        }
        else if (MatchesPreviousEncounter(
                     (IEnumerable<EncounterModel>)EliteEncounters.GetValue(rooms)!,
                     current.eliteEncountersVisited,
                     extended.encounterId))
        {
            SetCounter(rooms, "EliteEncountersVisited", current.eliteEncountersVisited - 1);
            category = "elite";
        }
        else if (MatchesPreviousBoss(rooms, current.bossEncountersVisited, extended.encounterId))
        {
            SetCounter(rooms, "BossEncountersVisited", current.bossEncountersVisited - 1);
            category = "boss";
        }

        if (category == null)
        {
            ReconnectLog.Write(
                $"Synthetic combat entry did not rewind a room cursor for encounter {extended.encounterId}; " +
                "the native room loader must preserve this encounter directly.");
            return;
        }

        ReconnectLog.Write(
            $"Prepared synthetic combat entry for encounter={extended.encounterId}, " +
            $"act={run.CurrentActIndex}, category={category}.");
    }

    internal static void RemoveSyntheticMapPointHistoryEntry(
        RunState run,
        ExtendedReconnectMessage extended)
    {
        int savedIndex = extended.actRoomStates.FindIndex(
            state => state.actIndex == run.CurrentActIndex);
        if (savedIndex < 0)
            throw new InvalidDataException(
                $"Anchor contains no map history state for current act {run.CurrentActIndex}.");

        IList histories = GetMapPointHistories(run);
        if (run.CurrentActIndex >= histories.Count || histories[run.CurrentActIndex] is not IList current)
            throw new InvalidDataException(
                $"Synthetic combat entry created no map history list for act {run.CurrentActIndex}.");

        ExtendedActRoomState saved = extended.actRoomStates[savedIndex];
        for (int i = 0; i < extended.actRoomStates.Count; i++)
        {
            ExtendedActRoomState act = extended.actRoomStates[i];
            int expected = act.mapPointHistoryCount + (act.actIndex == run.CurrentActIndex ? 1 : 0);
            int actual = GetMapPointHistoryCount(run, act.actIndex);
            if (actual != expected)
                throw new InvalidDataException(
                    $"Unexpected map history growth after synthetic combat entry: " +
                    $"act={act.actIndex}, expected={expected}, actual={actual}.");
        }

        object duplicate = current[current.Count - 1]
            ?? throw new InvalidDataException("Synthetic map history entry was null.");
        if (!HistoryEntryContainsEncounter(duplicate, extended.encounterId))
            throw new InvalidDataException(
                $"Synthetic map history entry does not reference encounter {extended.encounterId}.");

        int totalFloorBefore = run.TotalFloor;
        current.RemoveAt(current.Count - 1);
        ValidateMapPointHistory(run, extended.actRoomStates, "after synthetic map history removal");
        ReconnectLog.Write(
            $"Removed synthetic map history entry: act={run.CurrentActIndex}, " +
            $"history={saved.mapPointHistoryCount + 1}->{saved.mapPointHistoryCount}, " +
            $"totalFloor={totalFloorBefore}->{run.TotalFloor}.");
    }

    private static void RestoreRoomStates(
        IRunState run,
        IReadOnlyList<ExtendedActRoomState> savedStates)
    {
        List<object> acts = GetActs(run);
        if (acts.Count != savedStates.Count)
            throw new InvalidDataException(
                $"Act room-state count mismatch: anchor={savedStates.Count}, live={acts.Count}.");

        foreach (ExtendedActRoomState saved in savedStates)
        {
            if (saved.actIndex < 0 || saved.actIndex >= acts.Count)
                throw new InvalidDataException($"Anchor references invalid act index {saved.actIndex}.");
            RoomSet rooms = GetRooms(acts[saved.actIndex]);
            SetCounter(rooms, "NormalEncountersVisited", saved.normalEncountersVisited);
            SetCounter(rooms, "EliteEncountersVisited", saved.eliteEncountersVisited);
            SetCounter(rooms, "BossEncountersVisited", saved.bossEncountersVisited);
            SetCounter(rooms, "EventsVisited", saved.eventsVisited);
        }
    }

    private static bool MatchesPreviousEncounter(
        IEnumerable<EncounterModel> encounters,
        int visited,
        ModelId expected)
    {
        if (visited <= 0) return false;
        List<EncounterModel> values = encounters.ToList();
        return visited <= values.Count && values[visited - 1].Id == expected;
    }

    private static bool MatchesPreviousBoss(RoomSet rooms, int visited, ModelId expected)
    {
        if (visited == 1)
            return ((EncounterModel?)BossEncounter.GetValue(rooms))?.Id == expected;
        if (visited == 2)
            return ((EncounterModel?)SecondBossEncounter.GetValue(rooms))?.Id == expected;
        return false;
    }

    private static List<object> GetActs(IRunState run)
    {
        if (RunActs.GetValue(run) is not IEnumerable values)
            throw new InvalidOperationException("RunState.Acts is unavailable.");
        return values.Cast<object>().ToList();
    }

    private static RoomSet GetRooms(object act)
        => (RoomSet?)(ActRooms.GetValue(act))
           ?? throw new InvalidOperationException($"{act.GetType().FullName}.Rooms is unavailable.");

    private static int GetCounter(object rooms, string name)
        => (int)(AccessTools.Field(rooms.GetType(), LowerFirst(name))?.GetValue(rooms)
                 ?? throw new InvalidOperationException($"{rooms.GetType().FullName}.{name} is unavailable."));

    private static void SetCounter(object rooms, string name, int value)
    {
        FieldInfo field = AccessTools.Field(rooms.GetType(), LowerFirst(name))
            ?? throw new InvalidOperationException($"{rooms.GetType().FullName}.{name} cannot be restored.");
        field.SetValue(rooms, value);
    }

    private static IList GetMapPointHistories(IRunState run)
        => (IList?)(MapPointHistory.GetValue(run))
           ?? throw new InvalidOperationException("RunState map point history is unavailable.");

    private static int GetMapPointHistoryCount(IRunState run, int actIndex)
    {
        IList histories = GetMapPointHistories(run);
        if (actIndex >= histories.Count) return 0;
        return histories[actIndex] is IList actHistory
            ? actHistory.Count
            : throw new InvalidOperationException($"RunState map history for act {actIndex} is unavailable.");
    }

    private static void ValidateMapPointHistory(
        IRunState run,
        IReadOnlyList<ExtendedActRoomState> savedStates,
        string phase)
    {
        foreach (ExtendedActRoomState saved in savedStates)
        {
            int actual = GetMapPointHistoryCount(run, saved.actIndex);
            if (actual != saved.mapPointHistoryCount)
                throw new InvalidDataException(
                    $"Map history mismatch {phase}: act={saved.actIndex}, " +
                    $"anchor={saved.mapPointHistoryCount}, live={actual}.");
        }

        int expectedTotalFloor = savedStates.Sum(state => state.mapPointHistoryCount);
        if (run.TotalFloor != expectedTotalFloor)
            throw new InvalidDataException(
                $"TotalFloor mismatch {phase}: anchor={expectedTotalFloor}, live={run.TotalFloor}.");
    }

    private static bool HistoryEntryContainsEncounter(object entry, ModelId encounterId)
    {
        PropertyInfo roomsProperty = AccessTools.Property(entry.GetType(), "Rooms")
            ?? throw new InvalidOperationException($"{entry.GetType().FullName}.Rooms is unavailable.");
        if (roomsProperty.GetValue(entry) is not IEnumerable rooms) return false;
        foreach (object room in rooms)
        {
            PropertyInfo modelIdProperty = AccessTools.Property(room.GetType(), "ModelId")
                ?? throw new InvalidOperationException($"{room.GetType().FullName}.ModelId is unavailable.");
            if (Equals(modelIdProperty.GetValue(room), encounterId)) return true;
        }
        return false;
    }

    private static string LowerFirst(string value)
        => char.ToLowerInvariant(value[0]) + value[1..];

    private static bool RoomStatesEqual(
        IReadOnlyList<ExtendedActRoomState> left,
        IReadOnlyList<ExtendedActRoomState> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.actIndex == pair.Second.actIndex &&
            pair.First.normalEncountersVisited == pair.Second.normalEncountersVisited &&
            pair.First.eliteEncountersVisited == pair.Second.eliteEncountersVisited &&
            pair.First.bossEncountersVisited == pair.Second.bossEncountersVisited &&
            pair.First.eventsVisited == pair.Second.eventsVisited &&
            pair.First.mapPointHistoryCount == pair.Second.mapPointHistoryCount);

    private static string Describe(ExtendedActRoomState state)
        => $"{state.actIndex}:n{state.normalEncountersVisited}/e{state.eliteEncountersVisited}/" +
           $"b{state.bossEncountersVisited}/v{state.eventsVisited}/h{state.mapPointHistoryCount}";
}

public struct ExtendedCreatureState : IPacketSerializable
{
    public uint combatId;
    public bool isMonster;
    public ModelId monsterId;
    public ulong? petOwnerId;
    public CombatSide side;
    public string slotName;
    public SavedMoveState currentMove;
    public SavedMoveState nextMove;
    public bool currentAndNextMoveAreSame;
    public bool performedFirstMove;
    public bool spawnedThisTurn;
    public int monsterRngCounter;
    public ulong monsterRngState0;
    public ulong monsterRngState1;
    public ulong monsterRngState2;
    public ulong monsterRngState3;
    public List<PrimitiveFieldState> monsterFields;
    public List<CreatureReferenceFieldState> monsterCreatureReferences;
    public List<string> stateLogIds;
    public List<MoveFlagState> moveFlags;
    public List<ExtendedPowerState> powers;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(combatId);
        writer.WriteBool(isMonster);
        if (isMonster) writer.WriteModelEntry(monsterId);
        writer.WriteBool(petOwnerId.HasValue);
        if (petOwnerId.HasValue) writer.WriteULong(petOwnerId.Value);
        writer.WriteEnum(side);
        writer.WriteString(slotName ?? string.Empty);
        writer.Write(currentMove);
        writer.Write(nextMove);
        writer.WriteBool(currentAndNextMoveAreSame);
        writer.WriteBool(performedFirstMove);
        writer.WriteBool(spawnedThisTurn);
        writer.WriteInt(monsterRngCounter);
        writer.WriteULong(monsterRngState0);
        writer.WriteULong(monsterRngState1);
        writer.WriteULong(monsterRngState2);
        writer.WriteULong(monsterRngState3);
        writer.WriteList(monsterFields);
        writer.WriteList(monsterCreatureReferences);
        WriteStrings(writer, stateLogIds);
        writer.WriteList(moveFlags);
        writer.WriteList(powers);
    }

    public void Deserialize(PacketReader reader)
    {
        combatId = reader.ReadUInt();
        isMonster = reader.ReadBool();
        if (isMonster) monsterId = reader.ReadModelIdAssumingType<MonsterModel>();
        if (reader.ReadBool()) petOwnerId = reader.ReadULong();
        side = reader.ReadEnum<CombatSide>();
        slotName = reader.ReadString();
        currentMove = reader.Read<SavedMoveState>();
        nextMove = reader.Read<SavedMoveState>();
        currentAndNextMoveAreSame = reader.ReadBool();
        performedFirstMove = reader.ReadBool();
        spawnedThisTurn = reader.ReadBool();
        monsterRngCounter = reader.ReadInt();
        monsterRngState0 = reader.ReadULong();
        monsterRngState1 = reader.ReadULong();
        monsterRngState2 = reader.ReadULong();
        monsterRngState3 = reader.ReadULong();
        monsterFields = reader.ReadList<PrimitiveFieldState>();
        monsterCreatureReferences = reader.ReadList<CreatureReferenceFieldState>();
        stateLogIds = ReadStrings(reader);
        moveFlags = reader.ReadList<MoveFlagState>();
        powers = reader.ReadList<ExtendedPowerState>();
    }

    internal static void WriteStrings(PacketWriter writer, List<string> values)
    {
        writer.WriteInt(values?.Count ?? 0);
        if (values == null) return;
        foreach (string value in values) writer.WriteString(value ?? string.Empty);
    }

    internal static List<string> ReadStrings(PacketReader reader)
    {
        int count = reader.ReadInt();
        var result = new List<string>(count);
        for (int i = 0; i < count; i++) result.Add(reader.ReadString());
        return result;
    }
}

public struct CreatureReferenceFieldState : IPacketSerializable
{
    public string declaringType;
    public string name;
    public bool hasCombatId;
    public uint combatId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(declaringType ?? string.Empty);
        writer.WriteString(name ?? string.Empty);
        writer.WriteBool(hasCombatId);
        if (hasCombatId) writer.WriteUInt(combatId);
    }

    public void Deserialize(PacketReader reader)
    {
        declaringType = reader.ReadString();
        name = reader.ReadString();
        hasCombatId = reader.ReadBool();
        if (hasCombatId) combatId = reader.ReadUInt();
    }
}

public enum SavedMoveKind
{
    None,
    Registered,
    UnsetSentinel,
    DynamicStunned,
    DynamicIllusionRevive,
    Unsupported
}

public enum SavedMoveHandlerOwner
{
    None,
    NoOp,
    Monster,
    Power
}

public struct SavedMoveState : IPacketSerializable
{
    public SavedMoveKind kind;
    public string id;
    public string followUpStateId;
    public bool mustPerformOnce;
    public bool performedAtLeastOnce;
    public SavedMoveHandlerOwner handlerOwner;
    public string handlerOwnerType;
    public string handlerMethod;
    public string unsupportedReason;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteEnum(kind);
        writer.WriteString(id ?? string.Empty);
        writer.WriteString(followUpStateId ?? string.Empty);
        writer.WriteBool(mustPerformOnce);
        writer.WriteBool(performedAtLeastOnce);
        writer.WriteEnum(handlerOwner);
        writer.WriteString(handlerOwnerType ?? string.Empty);
        writer.WriteString(handlerMethod ?? string.Empty);
        writer.WriteString(unsupportedReason ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        kind = reader.ReadEnum<SavedMoveKind>();
        id = reader.ReadString();
        followUpStateId = reader.ReadString();
        mustPerformOnce = reader.ReadBool();
        performedAtLeastOnce = reader.ReadBool();
        handlerOwner = reader.ReadEnum<SavedMoveHandlerOwner>();
        handlerOwnerType = reader.ReadString();
        handlerMethod = reader.ReadString();
        unsupportedReason = reader.ReadString();
    }
}

public struct MoveFlagState : IPacketSerializable
{
    public string id;
    public bool performed;
    public void Serialize(PacketWriter writer) { writer.WriteString(id ?? string.Empty); writer.WriteBool(performed); }
    public void Deserialize(PacketReader reader) { id = reader.ReadString(); performed = reader.ReadBool(); }
}

public struct ExtendedPowerState : IPacketSerializable
{
    public ModelId id;
    public int amountOnTurnStart;
    public bool skipNextDurationTick;
    public List<PrimitiveFieldState> internalFields;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteModelEntry(id);
        writer.WriteInt(amountOnTurnStart);
        writer.WriteBool(skipNextDurationTick);
        writer.WriteList(internalFields);
    }
    public void Deserialize(PacketReader reader)
    {
        id = reader.ReadModelIdAssumingType<PowerModel>();
        amountOnTurnStart = reader.ReadInt();
        skipNextDurationTick = reader.ReadBool();
        internalFields = reader.ReadList<PrimitiveFieldState>();
    }
}

public struct PrimitiveFieldState : IPacketSerializable
{
    public string declaringType;
    public string name;
    public string typeName;
    public string value;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(declaringType ?? string.Empty);
        writer.WriteString(name ?? string.Empty);
        writer.WriteString(typeName ?? string.Empty);
        writer.WriteString(value ?? string.Empty);
    }
    public void Deserialize(PacketReader reader)
    {
        declaringType = reader.ReadString(); name = reader.ReadString();
        typeName = reader.ReadString(); value = reader.ReadString();
    }
}

public enum RelicRuntimeFieldKind
{
    Value,
    NullReference,
    EmptyCollection
}

public struct RelicRuntimeFieldState : IPacketSerializable
{
    public string declaringType;
    public string name;
    public string typeName;
    public RelicRuntimeFieldKind kind;
    public string value;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(declaringType ?? string.Empty);
        writer.WriteString(name ?? string.Empty);
        writer.WriteString(typeName ?? string.Empty);
        writer.WriteEnum(kind);
        writer.WriteString(value ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        declaringType = reader.ReadString();
        name = reader.ReadString();
        typeName = reader.ReadString();
        kind = reader.ReadEnum<RelicRuntimeFieldKind>();
        value = reader.ReadString();
    }
}

public struct ExtendedRelicRuntimeState : IPacketSerializable
{
    public ulong playerId;
    public int relicIndex;
    public ModelId relicId;
    public RelicStatus status;
    public int stackCount;
    public List<RelicRuntimeFieldState> fields;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(playerId);
        writer.WriteInt(relicIndex);
        writer.WriteModelEntry(relicId);
        writer.WriteEnum(status);
        writer.WriteInt(stackCount);
        writer.WriteList(fields);
    }

    public void Deserialize(PacketReader reader)
    {
        playerId = reader.ReadULong();
        relicIndex = reader.ReadInt();
        relicId = reader.ReadModelIdAssumingType<RelicModel>();
        status = reader.ReadEnum<RelicStatus>();
        stackCount = reader.ReadInt();
        fields = reader.ReadList<RelicRuntimeFieldState>();
    }
}

public struct ExtendedPlayerCardState : IPacketSerializable
{
    public ulong playerId;
    public List<ExtendedCardState> cards;
    public List<ExtendedPotionSlotState> potionSlots;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(playerId);
        writer.WriteList(cards);
        writer.WriteList(potionSlots);
    }
    public void Deserialize(PacketReader reader)
    {
        playerId = reader.ReadULong();
        cards = reader.ReadList<ExtendedCardState>();
        potionSlots = reader.ReadList<ExtendedPotionSlotState>();
    }
}

public struct ExtendedPotionSlotState : IPacketSerializable
{
    public int slotIndex;
    public ModelId id;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(slotIndex);
        writer.WriteModelEntry(id);
    }
    public void Deserialize(PacketReader reader)
    {
        slotIndex = reader.ReadInt();
        id = reader.ReadModelIdAssumingType<PotionModel>();
    }
}

public struct ExtendedCardState : IPacketSerializable
{
    public PileType pileType;
    public int indexInPile;
    public uint combatCardId;
    public int baseCost;
    public int capturedX;
    public List<LocalCostState> modifiers;
    public List<TemporaryCardCostState> temporaryStarCosts;
    public List<CardKeyword> localKeywords;
    public List<CardRuntimeFieldState> runtimeFields;
    public List<CardDynamicVarState> dynamicVars;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteEnum(pileType);
        writer.WriteInt(indexInPile);
        writer.WriteUInt(combatCardId);
        writer.WriteInt(baseCost);
        writer.WriteInt(capturedX);
        writer.WriteList(modifiers);
        writer.WriteList(temporaryStarCosts);
        writer.WriteInt(localKeywords.Count);
        foreach (CardKeyword keyword in localKeywords) writer.WriteEnum(keyword);
        writer.WriteList(runtimeFields);
        writer.WriteList(dynamicVars);
    }

    public void Deserialize(PacketReader reader)
    {
        pileType = reader.ReadEnum<PileType>();
        indexInPile = reader.ReadInt();
        combatCardId = reader.ReadUInt();
        baseCost = reader.ReadInt();
        capturedX = reader.ReadInt();
        modifiers = reader.ReadList<LocalCostState>();
        temporaryStarCosts = reader.ReadList<TemporaryCardCostState>();
        int count = reader.ReadInt();
        localKeywords = new List<CardKeyword>(count);
        for (int i = 0; i < count; i++) localKeywords.Add(reader.ReadEnum<CardKeyword>());
        runtimeFields = reader.ReadList<CardRuntimeFieldState>();
        dynamicVars = reader.ReadList<CardDynamicVarState>();
    }
}

public struct TemporaryCardCostState : IPacketSerializable
{
    public int cost;
    public bool clearsWhenTurnEnds;
    public bool clearsWhenCardIsPlayed;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(cost);
        writer.WriteBool(clearsWhenTurnEnds);
        writer.WriteBool(clearsWhenCardIsPlayed);
    }

    public void Deserialize(PacketReader reader)
    {
        cost = reader.ReadInt();
        clearsWhenTurnEnds = reader.ReadBool();
        clearsWhenCardIsPlayed = reader.ReadBool();
    }

    internal TemporaryCardCost ToModel()
    {
        if (clearsWhenTurnEnds && clearsWhenCardIsPlayed) return TemporaryCardCost.ThisTurn(cost);
        if (!clearsWhenTurnEnds && clearsWhenCardIsPlayed) return TemporaryCardCost.UntilPlayed(cost);
        if (!clearsWhenTurnEnds && !clearsWhenCardIsPlayed) return TemporaryCardCost.ThisCombat(cost);
        throw new InvalidDataException(
            $"Unsupported temporary star cost expiration: turn={clearsWhenTurnEnds}, played={clearsWhenCardIsPlayed}.");
    }
}

public enum CardRuntimeFieldKind
{
    ScalarValue,
    JsonValue,
    NullReference,
    EmptyCollection
}

public struct CardRuntimeFieldState : IPacketSerializable
{
    public string declaringType;
    public string name;
    public string typeName;
    public CardRuntimeFieldKind kind;
    public string value;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(declaringType ?? string.Empty);
        writer.WriteString(name ?? string.Empty);
        writer.WriteString(typeName ?? string.Empty);
        writer.WriteEnum(kind);
        writer.WriteString(value ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        declaringType = reader.ReadString();
        name = reader.ReadString();
        typeName = reader.ReadString();
        kind = reader.ReadEnum<CardRuntimeFieldKind>();
        value = reader.ReadString();
    }
}

internal static class CardRuntimeFieldCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal static CardRuntimeFieldState Capture(CardModel card, Type declaringType, FieldInfo field)
    {
        string typeName = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? string.Empty;
        object? value = field.GetValue(card);
        if (value == null)
            return Create(declaringType, field, typeName, CardRuntimeFieldKind.NullReference, string.Empty);

        Type scalarType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        if (IsRuntimeScalar(scalarType))
            return Create(
                declaringType,
                field,
                typeName,
                CardRuntimeFieldKind.ScalarValue,
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

        if (IsSupported(field.FieldType))
        {
            string json = JsonSerializer.Serialize(value, field.FieldType, JsonOptions);
            object? roundTripped = JsonSerializer.Deserialize(json, field.FieldType, JsonOptions);
            string verification = JsonSerializer.Serialize(roundTripped, field.FieldType, JsonOptions);
            if (!string.Equals(json, verification, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Card {card.Id} runtime field did not round-trip deterministically: " +
                    $"{declaringType.FullName}.{field.Name} ({field.FieldType.FullName}).");
            return Create(declaringType, field, typeName, CardRuntimeFieldKind.JsonValue, json);
        }

        if (value is ICollection collection && collection.Count == 0)
            return Create(declaringType, field, typeName, CardRuntimeFieldKind.EmptyCollection, string.Empty);

        throw new InvalidOperationException(
            $"Card {card.Id} has unsupported live runtime state at the snapshot boundary: " +
            $"{declaringType.FullName}.{field.Name} ({field.FieldType.FullName}).");
    }

    internal static void Restore(CardModel card, CardRuntimeFieldState saved)
    {
        Type? declaring = card.GetType();
        while (declaring != null && declaring.FullName != saved.declaringType) declaring = declaring.BaseType;
        FieldInfo? field = declaring?.GetField(
            saved.name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field == null)
            throw new InvalidDataException(
                $"Card {card.Id} runtime snapshot references missing field {saved.declaringType}.{saved.name}.");

        string actualType = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? string.Empty;
        if (!string.Equals(actualType, saved.typeName, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Card {card.Id} runtime field type changed for {saved.declaringType}.{saved.name}: " +
                $"snapshot={saved.typeName}, local={actualType}.");

        try
        {
            switch (saved.kind)
            {
                case CardRuntimeFieldKind.ScalarValue:
                    Type scalarType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                    if (!IsRuntimeScalar(scalarType))
                        throw new InvalidDataException(
                            $"Runtime field is not a supported scalar: {field.FieldType.FullName}.");
                    object scalar = scalarType.IsEnum
                        ? Enum.Parse(scalarType, saved.value)
                        : scalarType == typeof(string)
                            ? saved.value
                            : Convert.ChangeType(
                                saved.value, scalarType, System.Globalization.CultureInfo.InvariantCulture);
                    field.SetValue(
                        card,
                        Nullable.GetUnderlyingType(field.FieldType) == null
                            ? scalar
                            : Activator.CreateInstance(field.FieldType, scalar));
                    break;
                case CardRuntimeFieldKind.JsonValue:
                    if (!IsSupported(field.FieldType))
                        throw new InvalidDataException(
                            $"Runtime field type is no longer supported: {field.FieldType.FullName}.");
                    field.SetValue(card, JsonSerializer.Deserialize(saved.value, field.FieldType, JsonOptions));
                    break;
                case CardRuntimeFieldKind.NullReference:
                    field.SetValue(card, null);
                    break;
                case CardRuntimeFieldKind.EmptyCollection:
                    field.SetValue(card, Activator.CreateInstance(field.FieldType));
                    break;
                default:
                    throw new InvalidDataException($"Unknown card runtime field kind {saved.kind}.");
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"Failed to restore card {card.Id} runtime field {saved.declaringType}.{saved.name}.", ex);
        }
    }

    private static CardRuntimeFieldState Create(
        Type declaringType, FieldInfo field, string typeName, CardRuntimeFieldKind kind, string value)
        => new()
        {
            declaringType = declaringType.FullName ?? string.Empty,
            name = field.Name,
            typeName = typeName,
            kind = kind,
            value = value
        };

    private static bool IsSupported(Type type, HashSet<Type>? visiting = null)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) return IsSupported(nullable, visiting);
        if (IsRuntimeScalar(type)) return true;
        if (type.IsArray) return IsSupported(type.GetElementType()!, visiting);

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(HashSet<>) ||
                definition == typeof(Dictionary<,>))
                return type.GetGenericArguments().All(argument => IsSupported(argument, visiting));
        }

        visiting ??= new HashSet<Type>();
        if (!visiting.Add(type)) return true;
        try
        {
            if (type.IsValueType)
                return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => !field.IsStatic)
                    .All(field => field.IsPublic && IsSupported(field.FieldType, visiting));

            if (!type.IsClass || type.IsAbstract || type.BaseType != typeof(object) ||
                type.GetConstructor(Type.EmptyTypes) == null)
                return false;

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            if (properties.Length == 0 || properties.Any(property =>
                    property.GetIndexParameters().Length != 0 || property.GetMethod == null ||
                    property.SetMethod is not { IsPublic: true } || !IsSupported(property.PropertyType, visiting)))
                return false;

            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .All(field => field.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
                              properties.Any(property => field.Name == $"<{property.Name}>k__BackingField"));
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static bool IsRuntimeScalar(Type type)
        => type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
           type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
           type == typeof(short) || type == typeof(ushort) || type == typeof(int) ||
           type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
           type == typeof(float) || type == typeof(double) || type == typeof(char);
}

public struct CardDynamicVarState : IPacketSerializable
{
    public string name;
    public string baseValue;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(name ?? string.Empty);
        writer.WriteString(baseValue ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        name = reader.ReadString();
        baseValue = reader.ReadString();
    }
}

public struct LocalCostState : IPacketSerializable
{
    public int amount;
    public LocalCostType type;
    public LocalCostModifierExpiration expiration;
    public bool reduceOnly;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(amount); writer.WriteEnum(type); writer.WriteEnum(expiration); writer.WriteBool(reduceOnly);
    }
    public void Deserialize(PacketReader reader)
    {
        amount = reader.ReadInt(); type = reader.ReadEnum<LocalCostType>();
        expiration = reader.ReadEnum<LocalCostModifierExpiration>(); reduceOnly = reader.ReadBool();
    }
}

internal static class ExtendedReconnectCapture
{
    private static readonly HashSet<string> CardModelRuntimeFields = new(StringComparer.Ordinal)
    {
        "_baseReplayCount",
        "_starCostSet",
        "_baseStarCost",
        "_exhaustOnNextPlay",
        "_hasSingleTurnRetain",
        "_hasSingleTurnSly",
        "_isDupe"
    };
    private static readonly FieldInfo ReadyToEnd = AccessTools.Field(typeof(CombatManager), "_playersReadyToEndTurn");
    private static readonly FieldInfo ReadyToBeginEnemy = AccessTools.Field(typeof(CombatManager), "_playersReadyToBeginEnemyTurn");
    private static readonly FieldInfo TakingExtraTurn = AccessTools.Field(typeof(CombatManager), "_playersTakingExtraTurn");
    private static readonly FieldInfo SmCurrent = AccessTools.Field(
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine"), "_currentState");
    private static readonly FieldInfo SmPerformedFirst = AccessTools.Field(
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine"), "_performedFirstMove");
    private static readonly Type MoveStateType = AccessTools.TypeByName(
        "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState");
    private static readonly FieldInfo MovePerformed = AccessTools.Field(MoveStateType, "_performedAtLeastOnce");
    private static readonly PropertyInfo StateId = AccessTools.Property(
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState"), "Id");
    private static readonly FieldInfo Spawned = AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");
    private static readonly FieldInfo NextCreatureId = AccessTools.Field(typeof(CombatState), "_nextCreatureId");
    private static readonly FieldInfo PowerInternal = AccessTools.Field(typeof(PowerModel), "_internalData");
    private static readonly FieldInfo PowerSkip = AccessTools.Field(typeof(PowerModel), "_skipNextDurationTick");
    private static readonly FieldInfo CostBase = AccessTools.Field(typeof(CardEnergyCost), "_base");
    private static readonly FieldInfo CostCapturedX = AccessTools.Field(typeof(CardEnergyCost), "_capturedXValue");
    private static readonly FieldInfo CostModifiers = AccessTools.Field(typeof(CardEnergyCost), "_localModifiers");
    private static readonly FieldInfo CardTemporaryStarCosts =
        AccessTools.Field(typeof(CardModel), "_temporaryStarCosts");

    internal static ExtendedReconnectMessage Capture()
    {
        CombatState combat = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Cannot capture extended reconnect state without combat.");
        EncounterModel encounter = combat.Encounter
            ?? throw new InvalidOperationException("Cannot capture extended reconnect state without an encounter.");
        var message = new ExtendedReconnectMessage
        {
            nextActionId = RunManager.Instance.ActionQueueSet.NextActionId,
            nextHookId = RunManager.Instance.ActionQueueSynchronizer.NextHookId,
            nextChecksumId = RunManager.Instance.ChecksumTracker.NextId,
            nextCreatureId = (uint)NextCreatureId.GetValue(combat)!,
            roundNumber = combat.RoundNumber,
            currentSide = combat.CurrentSide,
            actionSynchronizerState = RunManager.Instance.ActionQueueSynchronizer.CombatState,
            playersReadyToEndTurn = ((IEnumerable<Player>)ReadyToEnd.GetValue(CombatManager.Instance)!).Select(p => p.NetId).ToList(),
            playersReadyToBeginEnemyTurn = ((IEnumerable<Player>)ReadyToBeginEnemy.GetValue(CombatManager.Instance)!).Select(p => p.NetId).ToList(),
            playersTakingExtraTurn = ((IEnumerable<Player>)TakingExtraTurn.GetValue(CombatManager.Instance)!).Select(p => p.NetId).ToList(),
            encounterId = encounter.Id,
            actRoomStates = RunEncounterStateSupport.Capture(combat.RunState),
            creatures = combat.Creatures.Where(c => c.CombatId.HasValue)
                .Select(creature => CaptureCreature(combat, creature)).ToList(),
            relicRuntimeStates = combat.Players
                .SelectMany(player => player.Relics.Select((relic, index) => CaptureRelicRuntimeState(player, relic, index)))
                .ToList(),
            playerCards = combat.Players.Select(CapturePlayerCards).ToList(),
            combatHistory = CombatHistoryStateSupport.Capture(combat)
        };
        return message;
    }

    private static ExtendedCreatureState CaptureCreature(CombatState combat, Creature creature)
    {
        var result = new ExtendedCreatureState
        {
            combatId = creature.CombatId!.Value,
            isMonster = creature.Monster != null,
            monsterId = creature.Monster?.Id!,
            petOwnerId = creature.PetOwner?.NetId,
            side = creature.Side,
            slotName = creature.SlotName ?? string.Empty,
            currentMove = default,
            nextMove = default,
            monsterFields = new List<PrimitiveFieldState>(),
            monsterCreatureReferences = new List<CreatureReferenceFieldState>(),
            stateLogIds = new List<string>(), moveFlags = new List<MoveFlagState>(),
            powers = creature.Powers.Select(CapturePower).ToList()
        };
        MonsterModel? monster = creature.Monster;
        if (monster == null) return result;
        var machine = monster.MoveStateMachine;
        object? sm = machine;
        object? current = sm == null ? null : SmCurrent.GetValue(sm);
        result.currentMove = MonsterMoveStateSupport.Capture(current, machine?.States, creature);
        result.nextMove = MonsterMoveStateSupport.Capture(monster.NextMove, machine?.States, creature);
        result.currentAndNextMoveAreSame = current != null && ReferenceEquals(current, monster.NextMove);
        result.performedFirstMove = sm != null && (bool)SmPerformedFirst.GetValue(sm)!;
        result.spawnedThisTurn = (bool)Spawned.GetValue(monster)!;
        SerializableRng monsterRng = monster.Rng.ToSerializable();
        result.monsterRngCounter = monsterRng.counter;
        result.monsterRngState0 = monsterRng.state0;
        result.monsterRngState1 = monsterRng.state1;
        result.monsterRngState2 = monsterRng.state2;
        result.monsterRngState3 = monsterRng.state3;
        CaptureMonsterRuntimeFields(combat, monster, result.monsterFields, result.monsterCreatureReferences);
        if (sm != null)
        {
            foreach (object state in machine!.StateLog)
                result.stateLogIds.Add((string?)StateId.GetValue(state) ?? string.Empty);
            foreach (DictionaryEntry entry in (IDictionary)machine.States)
            {
                if (entry.Key is string id && entry.Value != null && MoveStateType.IsInstanceOfType(entry.Value))
                    result.moveFlags.Add(new MoveFlagState { id = id, performed = (bool)MovePerformed.GetValue(entry.Value)! });
            }
        }
        ReconnectLog.Write(
            $"Captured monster move state: combatId={result.combatId}, monster={result.monsterId}, " +
            $"current={MonsterMoveStateSupport.Describe(result.currentMove)}, " +
            $"next={MonsterMoveStateSupport.Describe(result.nextMove)}, same={result.currentAndNextMoveAreSame}, " +
            $"states=[{string.Join(",", ((IDictionary?)machine?.States)?.Keys.Cast<object>() ?? Array.Empty<object>())}].");
        return result;
    }

    private static void CaptureMonsterRuntimeFields(
        CombatState combat,
        MonsterModel monster,
        List<PrimitiveFieldState> scalars,
        List<CreatureReferenceFieldState> creatureReferences)
    {
        for (Type? type = monster.GetType(); type != null && type != typeof(MonsterModel); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!field.IsInitOnly && IsRuntimeScalar(field.FieldType))
                {
                    object? value = field.GetValue(monster);
                    if (value == null) continue;
                    scalars.Add(new PrimitiveFieldState
                    {
                        declaringType = type.FullName ?? string.Empty,
                        name = field.Name,
                        typeName = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? string.Empty,
                        value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                    });
                    continue;
                }

                if (!typeof(Creature).IsAssignableFrom(field.FieldType)) continue;
                Creature? referenced = field.GetValue(monster) as Creature;
                uint combatId = referenced?.CombatId ?? 0;
                bool isActive = referenced?.CombatId.HasValue == true && combat.ContainsCreature(referenced);
                creatureReferences.Add(new CreatureReferenceFieldState
                {
                    declaringType = type.FullName ?? string.Empty,
                    name = field.Name,
                    hasCombatId = isActive,
                    combatId = isActive ? combatId : 0
                });
            }
        }
    }

    internal static ExtendedRelicRuntimeState CaptureRelicRuntimeState(
        Player player,
        RelicModel relic,
        int relicIndex)
    {
        var fields = new List<RelicRuntimeFieldState>();
        for (Type? type = relic.GetType(); type != null && type != typeof(RelicModel); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(field => !field.IsStatic && !field.IsLiteral && !field.IsInitOnly)
                         .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                Type fieldType = field.FieldType;
                object? value = field.GetValue(relic);
                if (IsRuntimeScalar(fieldType))
                {
                    if (field.Name == "_isActivating" && value is true)
                        throw new TransientRelicSnapshotException(
                            $"Relic {relic.Id} is still running an activation animation at the snapshot boundary: " +
                            $"{type.FullName}.{field.Name}.");
                    fields.Add(new RelicRuntimeFieldState
                    {
                        declaringType = type.FullName ?? string.Empty,
                        name = field.Name,
                        typeName = fieldType.AssemblyQualifiedName ?? fieldType.FullName ?? string.Empty,
                        kind = value == null ? RelicRuntimeFieldKind.NullReference : RelicRuntimeFieldKind.Value,
                        value = value == null
                            ? string.Empty
                            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                    });
                    continue;
                }

                if (IsTransientRelicReference(fieldType))
                {
                    if (value != null)
                        throw new TransientRelicSnapshotException(
                            $"Relic {relic.Id} has a live transient replay reference at the snapshot boundary: " +
                            $"{type.FullName}.{field.Name} ({fieldType.FullName}).");
                    fields.Add(new RelicRuntimeFieldState
                    {
                        declaringType = type.FullName ?? string.Empty,
                        name = field.Name,
                        typeName = fieldType.AssemblyQualifiedName ?? fieldType.FullName ?? string.Empty,
                        kind = RelicRuntimeFieldKind.NullReference,
                        value = string.Empty
                    });
                    continue;
                }

                if (IsTransientRelicCollection(fieldType))
                {
                    if (value == null)
                    {
                        fields.Add(new RelicRuntimeFieldState
                        {
                            declaringType = type.FullName ?? string.Empty,
                            name = field.Name,
                            typeName = fieldType.AssemblyQualifiedName ?? fieldType.FullName ?? string.Empty,
                            kind = RelicRuntimeFieldKind.NullReference,
                            value = string.Empty
                        });
                        continue;
                    }
                    if (value is not ICollection collection)
                        throw new InvalidOperationException(
                            $"Relic {relic.Id} transient collection is not countable at the snapshot boundary: " +
                            $"{type.FullName}.{field.Name} ({fieldType.FullName}).");
                    if (collection.Count != 0)
                        throw new TransientRelicSnapshotException(
                            $"Relic {relic.Id} has {collection.Count} transient replay reference(s) at the snapshot boundary: " +
                            $"{type.FullName}.{field.Name} ({fieldType.FullName}).");
                    fields.Add(new RelicRuntimeFieldState
                    {
                        declaringType = type.FullName ?? string.Empty,
                        name = field.Name,
                        typeName = fieldType.AssemblyQualifiedName ?? fieldType.FullName ?? string.Empty,
                        kind = RelicRuntimeFieldKind.EmptyCollection,
                        value = string.Empty
                    });
                }
            }
        }

        return new ExtendedRelicRuntimeState
        {
            playerId = player.NetId,
            relicIndex = relicIndex,
            relicId = relic.Id,
            status = relic.Status,
            stackCount = relic.StackCount,
            fields = fields
        };
    }

    private static bool IsRuntimeScalar(Type type)
    {
        Type scalar = Nullable.GetUnderlyingType(type) ?? type;
        return scalar.IsEnum || scalar == typeof(string) || scalar == typeof(decimal) ||
               scalar == typeof(bool) || scalar == typeof(byte) || scalar == typeof(sbyte) ||
               scalar == typeof(short) || scalar == typeof(ushort) || scalar == typeof(int) ||
               scalar == typeof(uint) || scalar == typeof(long) || scalar == typeof(ulong) ||
               scalar == typeof(float) || scalar == typeof(double) || scalar == typeof(char);
    }

    private static bool IsTransientRelicReference(Type type)
        => typeof(CardModel).IsAssignableFrom(type) || typeof(CardPlay).IsAssignableFrom(type) ||
           typeof(Creature).IsAssignableFrom(type) || typeof(PowerModel).IsAssignableFrom(type);

    private static bool IsTransientRelicCollection(Type type)
    {
        if (!typeof(IEnumerable).IsAssignableFrom(type) || !type.IsGenericType) return false;
        return type.GetGenericArguments().Any(IsTransientRelicReference);
    }

    private static ExtendedPowerState CapturePower(PowerModel power)
    {
        var result = new ExtendedPowerState
        {
            id = power.Id,
            amountOnTurnStart = power.AmountOnTurnStart,
            skipNextDurationTick = (bool)PowerSkip.GetValue(power)!,
            internalFields = new List<PrimitiveFieldState>()
        };
        object? data = PowerInternal.GetValue(power);
        if (data == null) return result;
        for (Type? type = data.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                object? value = field.GetValue(data);
                if (value == null || (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && field.FieldType != typeof(decimal) && field.FieldType != typeof(string))) continue;
                result.internalFields.Add(new PrimitiveFieldState
                {
                    declaringType = type.FullName ?? string.Empty, name = field.Name,
                    typeName = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? string.Empty,
                    value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                });
            }
        }
        return result;
    }

    private static ExtendedPlayerCardState CapturePlayerCards(Player player)
    {
        var cards = new List<ExtendedCardState>();

        foreach (CardPile pile in player.PlayerCombatState!.AllPiles)
        {
            List<CardModel> pileCards = pile.Cards.ToList();
            for (int index = 0; index < pileCards.Count; index++)
                cards.Add(CaptureCard(pileCards[index], pile.Type, index));
        }

        return new ExtendedPlayerCardState
        {
            playerId = player.NetId,
            cards = cards,
            potionSlots = player.PotionSlots
                .Select((potion, index) => (potion, index))
                .Where(entry => entry.potion != null)
                .Select(entry => new ExtendedPotionSlotState
                {
                    slotIndex = entry.index,
                    id = entry.potion!.Id
                }).ToList()
        };
    }

    private static ExtendedCardState CaptureCard(CardModel card, PileType pileType, int indexInPile)
        => CaptureCardRuntime(card, NetCombatCardDb.Instance.GetCardId(card), pileType, indexInPile);

    internal static ExtendedCardState CaptureCardRuntime(
        CardModel card,
        uint combatCardId,
        PileType pileType = default,
        int indexInPile = -1)
    {
        if (card.CurrentTarget != null || card.CurrentPlayIndex != 0)
            throw new InvalidOperationException(
                $"Card {card.Id} is still inside a play sequence at the snapshot boundary: " +
                $"target={card.CurrentTarget?.CombatId}, playIndex={card.CurrentPlayIndex}.");

        var modifiers = (IEnumerable<LocalCostModifier>)CostModifiers.GetValue(card.EnergyCost)!;
        return new ExtendedCardState
        {
            pileType = pileType,
            indexInPile = indexInPile,
            combatCardId = combatCardId,
            baseCost = (int)CostBase.GetValue(card.EnergyCost)!,
            capturedX = (int)CostCapturedX.GetValue(card.EnergyCost)!,
            modifiers = modifiers.Select(m => new LocalCostState
            {
                amount = m.Amount,
                type = m.Type,
                expiration = m.Expiration,
                reduceOnly = m.IsReduceOnly
            }).ToList(),
            temporaryStarCosts = ((IEnumerable<TemporaryCardCost>)CardTemporaryStarCosts.GetValue(card)!)
                .Select(cost => new TemporaryCardCostState
                {
                    cost = cost.Cost,
                    clearsWhenTurnEnds = cost.ClearsWhenTurnEnds,
                    clearsWhenCardIsPlayed = cost.ClearsWhenCardIsPlayed
                }).ToList(),
            localKeywords = card.GetKeywordsWithSources(KeywordSources.Local).ToList(),
            runtimeFields = CaptureCardRuntimeFields(card),
            dynamicVars = card.DynamicVars.Values
                .OrderBy(variable => variable.Name, StringComparer.Ordinal)
                .Select(variable => new CardDynamicVarState
                {
                    name = variable.Name,
                    baseValue = variable.BaseValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }).ToList()
        };
    }

    private static List<CardRuntimeFieldState> CaptureCardRuntimeFields(CardModel card)
    {
        // These public getters canonicalize lazy model caches that UI visibility may initialize on only one peer.
        _ = card.BaseStarCost;
        _ = card.DynamicVars;

        var fields = new List<CardRuntimeFieldState>();
        for (Type? type = card.GetType(); type != null; type = type.BaseType)
        {
            if (type != typeof(CardModel) && !typeof(CardModel).IsAssignableFrom(type)) continue;
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly)
                     .Where(field => !field.IsStatic && !field.IsLiteral && !field.IsInitOnly &&
                                     (type != typeof(CardModel) || CardModelRuntimeFields.Contains(field.Name)))
                     .OrderBy(field => field.Name, StringComparer.Ordinal))
                fields.Add(CardRuntimeFieldCodec.Capture(card, type, field));
            if (type == typeof(CardModel)) break;
        }
        return fields;
    }
}
