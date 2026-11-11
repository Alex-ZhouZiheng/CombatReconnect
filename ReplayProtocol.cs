using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace CombatReconnect;

public struct ReplayManifestMessage : INetMessage, IPacketSerializable
{
    public string protocolVersion;
    public string messageTableHash;
    public string actionTableHash;
    public ulong epoch;
    public int roundNumber;
    public int anchorBytes;
    public int encodedAnchorBytes;
    public int anchorChunks;
    public int eventCount;
    public string anchorTransferId;
    public int anchorGeneration;
    public uint finalActionId;
    public uint finalHookId;
    public uint finalChecksumId;
    public string anchorHash;
    public string encodedAnchorHash;
    public ReplayAnchorCompression anchorCompression;
    public string finalStateHash;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(protocolVersion ?? string.Empty);
        writer.WriteString(messageTableHash ?? string.Empty);
        writer.WriteString(actionTableHash ?? string.Empty);
        writer.WriteULong(epoch); writer.WriteInt(roundNumber); writer.WriteInt(anchorBytes); writer.WriteInt(encodedAnchorBytes);
        writer.WriteInt(anchorChunks); writer.WriteInt(eventCount); writer.WriteString(anchorTransferId ?? string.Empty); writer.WriteInt(anchorGeneration);
        writer.WriteUInt(finalActionId); writer.WriteUInt(finalHookId); writer.WriteUInt(finalChecksumId);
        writer.WriteString(anchorHash ?? string.Empty); writer.WriteString(encodedAnchorHash ?? string.Empty);
        writer.WriteByte((byte)anchorCompression); writer.WriteString(finalStateHash ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        protocolVersion = reader.ReadString();
        messageTableHash = reader.ReadString();
        actionTableHash = reader.ReadString();
        epoch = reader.ReadULong(); roundNumber = reader.ReadInt(); anchorBytes = reader.ReadInt(); encodedAnchorBytes = reader.ReadInt();
        anchorChunks = reader.ReadInt(); eventCount = reader.ReadInt(); anchorTransferId = reader.ReadString(); anchorGeneration = reader.ReadInt();
        finalActionId = reader.ReadUInt(); finalHookId = reader.ReadUInt(); finalChecksumId = reader.ReadUInt();
        anchorHash = reader.ReadString(); encodedAnchorHash = reader.ReadString();
        anchorCompression = (ReplayAnchorCompression)reader.ReadByte(); finalStateHash = reader.ReadString();
    }
}

public struct ReplayAnchorChunkMessage : INetMessage, IPacketSerializable
{
    public ulong epoch;
    public int index;
    public int count;
    public byte[] payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(epoch); writer.WriteInt(index); writer.WriteInt(count);
        byte[] bytes = payload ?? Array.Empty<byte>();
        writer.WriteInt(bytes.Length); writer.WriteBytes(bytes, bytes.Length);
    }

    public void Deserialize(PacketReader reader)
    {
        epoch = reader.ReadULong(); index = reader.ReadInt(); count = reader.ReadInt();
        int length = reader.ReadInt();
        if (length < 0 || length > ReplayPayloadCodec.ChunkSize)
            throw new InvalidDataException($"Replay chunk length {length} is outside the allowed range.");
        payload = new byte[length]; reader.ReadBytes(payload, payload.Length);
    }
}

public struct DeterministicInputEvent : IPacketSerializable
{
    public int sequence;
    public CombatReplayEvent replayEvent;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(sequence);
        writer.Write(replayEvent);
    }

    public void Deserialize(PacketReader reader)
    {
        sequence = reader.ReadInt();
        replayEvent = new CombatReplayEvent();
        replayEvent.Deserialize(reader);
    }
}

public struct ReplayEventBatchMessage : INetMessage, IPacketSerializable
{
    public ulong epoch;
    public int generation;
    public string transferId;
    public int firstSequence;
    public bool isLast;
    public List<DeterministicInputEvent> events;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(epoch); writer.WriteInt(generation); writer.WriteString(transferId ?? string.Empty);
        writer.WriteInt(firstSequence); writer.WriteBool(isLast);
        List<DeterministicInputEvent> values = events ?? new List<DeterministicInputEvent>();
        writer.WriteInt(values.Count);
        foreach (DeterministicInputEvent evt in values) writer.Write(evt);
    }

    public void Deserialize(PacketReader reader)
    {
        epoch = reader.ReadULong(); generation = reader.ReadInt(); transferId = reader.ReadString();
        firstSequence = reader.ReadInt(); isLast = reader.ReadBool();
        int count = reader.ReadInt();
        if (count < 0 || count > 32)
            throw new InvalidDataException($"Replay event batch count {count} is outside the allowed range.");
        events = new List<DeterministicInputEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var evt = new DeterministicInputEvent();
            evt.Deserialize(reader);
            events.Add(evt);
        }
    }
}

public struct ReplayReadyMessage : INetMessage, IPacketSerializable
{
    public ulong epoch;
    public int generation;
    public string transferId;
    public bool success;
    public string stateHash;
    public string detail;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;
    public void Serialize(PacketWriter w) { w.WriteULong(epoch); w.WriteInt(generation); w.WriteString(transferId ?? string.Empty); w.WriteBool(success); w.WriteString(stateHash ?? string.Empty); w.WriteString(detail ?? string.Empty); }
    public void Deserialize(PacketReader r) { epoch = r.ReadULong(); generation = r.ReadInt(); transferId = r.ReadString(); success = r.ReadBool(); stateHash = r.ReadString(); detail = r.ReadString(); }
}

public struct ReplayCommitMessage : INetMessage, IPacketSerializable
{
    public ulong epoch;
    public int generation;
    public string transferId;
    public string stateHash;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;
    public void Serialize(PacketWriter w) { w.WriteULong(epoch); w.WriteInt(generation); w.WriteString(transferId ?? string.Empty); w.WriteString(stateHash ?? string.Empty); }
    public void Deserialize(PacketReader r) { epoch = r.ReadULong(); generation = r.ReadInt(); transferId = r.ReadString(); stateHash = r.ReadString(); }
}

public struct ReplayAbortMessage : INetMessage, IPacketSerializable
{
    public ulong epoch;
    public int generation;
    public string transferId;
    public string reason;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Warn;
    public bool ShouldBuffer => true;
    public void Serialize(PacketWriter w) { w.WriteULong(epoch); w.WriteInt(generation); w.WriteString(transferId ?? string.Empty); w.WriteString(reason ?? string.Empty); }
    public void Deserialize(PacketReader r) { epoch = r.ReadULong(); generation = r.ReadInt(); transferId = r.ReadString(); reason = r.ReadString(); }
}

public struct RewindRequestMessage : INetMessage, IPacketSerializable
{
    public string protocolVersion;
    public string reason;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { w.WriteString(protocolVersion ?? string.Empty); w.WriteString(reason ?? string.Empty); }
    public void Deserialize(PacketReader r) { protocolVersion = r.ReadString(); reason = r.ReadString(); }
}

public struct RewindVoteStartMessage : INetMessage, IPacketSerializable
{
    public string voteId;
    public ulong requesterId;
    public ulong epoch;
    public int roundNumber;
    public int timeoutSeconds;
    public List<ulong> participants;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(voteId ?? string.Empty); w.WriteULong(requesterId); w.WriteULong(epoch); w.WriteInt(roundNumber); w.WriteInt(timeoutSeconds);
        List<ulong> ids = participants ?? new List<ulong>(); w.WriteInt(ids.Count);
        foreach (ulong id in ids) w.WriteULong(id);
    }
    public void Deserialize(PacketReader r)
    {
        voteId = r.ReadString(); requesterId = r.ReadULong(); epoch = r.ReadULong(); roundNumber = r.ReadInt(); timeoutSeconds = r.ReadInt();
        int count = r.ReadInt(); participants = new List<ulong>(count);
        for (int i = 0; i < count; i++) participants.Add(r.ReadULong());
    }
}

public struct RewindAbortRecoveredMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public int generation;
    public bool success;
    public string stateHash;
    public string detail;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteInt(generation); w.WriteBool(success);
        w.WriteString(stateHash ?? string.Empty); w.WriteString(detail ?? string.Empty);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); generation = r.ReadInt(); success = r.ReadBool();
        stateHash = r.ReadString(); detail = r.ReadString();
    }
}

public struct RewindAbortCompleteMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public int generation;
    public bool success;
    public string reason;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Warn;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteInt(generation); w.WriteBool(success);
        w.WriteString(reason ?? string.Empty);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); generation = r.ReadInt(); success = r.ReadBool(); reason = r.ReadString();
    }
}

public struct RewindVoteResponseMessage : INetMessage, IPacketSerializable
{
    public string voteId;
    public bool approved;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { w.WriteString(voteId ?? string.Empty); w.WriteBool(approved); }
    public void Deserialize(PacketReader r) { voteId = r.ReadString(); approved = r.ReadBool(); }
}

public struct RewindVoteResultMessage : INetMessage, IPacketSerializable
{
    public string voteId;
    public bool approved;
    public string reason;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { w.WriteString(voteId ?? string.Empty); w.WriteBool(approved); w.WriteString(reason ?? string.Empty); }
    public void Deserialize(PacketReader r) { voteId = r.ReadString(); approved = r.ReadBool(); reason = r.ReadString(); }
}

public struct RewindFreezeMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public int generation;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { w.WriteString(transactionId ?? string.Empty); w.WriteInt(generation); }
    public void Deserialize(PacketReader r) { transactionId = r.ReadString(); generation = r.ReadInt(); }
}

public struct RewindFreezeAckMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public int generation;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { w.WriteString(transactionId ?? string.Empty); w.WriteInt(generation); }
    public void Deserialize(PacketReader r) { transactionId = r.ReadString(); generation = r.ReadInt(); }
}

public struct RewindDrainMarkerMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public int generation;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) { w.WriteString(transactionId ?? string.Empty); w.WriteInt(generation); }
    public void Deserialize(PacketReader r) { transactionId = r.ReadString(); generation = r.ReadInt(); }
}

public struct RewindBoundaryReadyMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public int generation;
    public bool success;
    public string safetyHash;
    public string detail;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteInt(generation); w.WriteBool(success);
        w.WriteString(safetyHash ?? string.Empty); w.WriteString(detail ?? string.Empty);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); generation = r.ReadInt(); success = r.ReadBool(); safetyHash = r.ReadString(); detail = r.ReadString();
    }
}

public struct RewindPrepareMessage : INetMessage, IPacketSerializable
{
    public string protocolVersion;
    public string transactionId;
    public ulong epoch;
    public int replayStartIndex;
    public int baseReplayStartIndex;
    public int eventCount;
    public int anchorBytes;
    public int encodedAnchorBytes;
    public string anchorTransferId;
    public string anchorHash;
    public string encodedAnchorHash;
    public ReplayAnchorCompression anchorCompression;
    public string finalStateHash;
    public uint finalActionId;
    public uint finalHookId;
    public uint finalChecksumId;
    public int finalRoundNumber;
    public CombatSide finalSide;
    public int generation;
    public List<ulong> participants;
    public List<int> finalPlayerTurnNumbers;

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter w)
    {
        w.WriteString(protocolVersion ?? string.Empty); w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteInt(replayStartIndex);
        w.WriteInt(baseReplayStartIndex); w.WriteInt(eventCount);
        w.WriteInt(anchorBytes); w.WriteInt(encodedAnchorBytes); w.WriteString(anchorTransferId ?? string.Empty); w.WriteString(anchorHash ?? string.Empty);
        w.WriteString(encodedAnchorHash ?? string.Empty); w.WriteByte((byte)anchorCompression);
        w.WriteString(finalStateHash ?? string.Empty);
        w.WriteUInt(finalActionId); w.WriteUInt(finalHookId); w.WriteUInt(finalChecksumId);
        w.WriteInt(finalRoundNumber); w.WriteEnum(finalSide); w.WriteInt(generation);
        List<ulong> ids = participants ?? new List<ulong>();
        w.WriteInt(ids.Count);
        foreach (ulong id in ids) w.WriteULong(id);
        List<int> turns = finalPlayerTurnNumbers ?? new List<int>();
        w.WriteInt(turns.Count);
        foreach (int turn in turns) w.WriteInt(turn);
    }

    public void Deserialize(PacketReader r)
    {
        protocolVersion = r.ReadString(); transactionId = r.ReadString(); epoch = r.ReadULong(); replayStartIndex = r.ReadInt();
        baseReplayStartIndex = r.ReadInt(); eventCount = r.ReadInt();
        anchorBytes = r.ReadInt(); encodedAnchorBytes = r.ReadInt(); anchorTransferId = r.ReadString(); anchorHash = r.ReadString();
        encodedAnchorHash = r.ReadString(); anchorCompression = (ReplayAnchorCompression)r.ReadByte();
        finalStateHash = r.ReadString();
        finalActionId = r.ReadUInt(); finalHookId = r.ReadUInt(); finalChecksumId = r.ReadUInt();
        finalRoundNumber = r.ReadInt(); finalSide = r.ReadEnum<CombatSide>(); generation = r.ReadInt();
        int count = r.ReadInt();
        if (count < 0 || count > 32) throw new InvalidDataException($"Rewind participant count {count} is outside the allowed range.");
        participants = new List<ulong>(count);
        for (int i = 0; i < count; i++) participants.Add(r.ReadULong());
        int turnCount = r.ReadInt();
        if (turnCount < 0 || turnCount > 32) throw new InvalidDataException($"Rewind turn count {turnCount} is outside the allowed range.");
        finalPlayerTurnNumbers = new List<int>(turnCount);
        for (int i = 0; i < turnCount; i++) finalPlayerTurnNumbers.Add(r.ReadInt());
    }
}

public struct RewindEventBatchMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public int firstSequence;
    public bool isLast;
    public List<DeterministicInputEvent> events;

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch);
        w.WriteInt(firstSequence); w.WriteBool(isLast);
        List<DeterministicInputEvent> values = events ?? new List<DeterministicInputEvent>();
        w.WriteInt(values.Count);
        foreach (DeterministicInputEvent evt in values) w.Write(evt);
    }

    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong();
        firstSequence = r.ReadInt(); isLast = r.ReadBool();
        int count = r.ReadInt();
        if (count < 0 || count > 32)
            throw new InvalidDataException($"Rewind event batch count {count} is outside the allowed range.");
        events = new List<DeterministicInputEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var evt = new DeterministicInputEvent();
            evt.Deserialize(r);
            events.Add(evt);
        }
    }
}

public struct RewindAnchorChunkMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public int index;
    public int count;
    public byte[] payload;

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteInt(index); w.WriteInt(count);
        byte[] bytes = payload ?? Array.Empty<byte>();
        w.WriteInt(bytes.Length); w.WriteBytes(bytes, bytes.Length);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong(); index = r.ReadInt(); count = r.ReadInt();
        int length = r.ReadInt();
        if (length < 0 || length > ReplayPayloadCodec.ChunkSize)
            throw new InvalidDataException($"Rewind chunk length {length} is outside the allowed range.");
        payload = new byte[length]; r.ReadBytes(payload, payload.Length);
    }
}

public struct RewindReadyMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public bool success;
    public string stateHash;
    public string detail;
    public uint actionId;
    public uint hookId;
    public uint checksumId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteBool(success);
        w.WriteString(stateHash ?? string.Empty); w.WriteString(detail ?? string.Empty);
        w.WriteUInt(actionId); w.WriteUInt(hookId); w.WriteUInt(checksumId);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong(); success = r.ReadBool();
        stateHash = r.ReadString(); detail = r.ReadString();
        actionId = r.ReadUInt(); hookId = r.ReadUInt(); checksumId = r.ReadUInt();
    }
}

public struct RewindCommitMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public string stateHash;
    public int generation;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteString(stateHash ?? string.Empty); w.WriteInt(generation);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong(); stateHash = r.ReadString(); generation = r.ReadInt();
    }
}

public struct RewindCommitAckMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public int generation;
    public bool success;
    public string stateHash;
    public string detail;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteInt(generation);
        w.WriteBool(success); w.WriteString(stateHash ?? string.Empty); w.WriteString(detail ?? string.Empty);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong(); generation = r.ReadInt();
        success = r.ReadBool(); stateHash = r.ReadString(); detail = r.ReadString();
    }
}

public struct RewindCommitCompleteMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public int generation;
    public string stateHash;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteInt(generation);
        w.WriteString(stateHash ?? string.Empty);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong(); generation = r.ReadInt(); stateHash = r.ReadString();
    }
}

public struct RewindAbortMessage : INetMessage, IPacketSerializable
{
    public string transactionId;
    public ulong epoch;
    public int generation;
    public string reason;
    public int failedEventSequence;
    public string counterDetail;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Warn;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w)
    {
        w.WriteString(transactionId ?? string.Empty); w.WriteULong(epoch); w.WriteInt(generation); w.WriteString(reason ?? string.Empty);
        w.WriteInt(failedEventSequence); w.WriteString(counterDetail ?? string.Empty);
    }
    public void Deserialize(PacketReader r)
    {
        transactionId = r.ReadString(); epoch = r.ReadULong(); generation = r.ReadInt(); reason = r.ReadString();
        failedEventSequence = r.ReadInt(); counterDetail = r.ReadString();
    }
}

public struct TurnHistoryMetadata : IPacketSerializable
{
    public ulong epoch;
    public int roundNumber;
    public CombatSide activeSide;
    public long createdAtUnixMilliseconds;
    public string displayMetadata;

    public void Serialize(PacketWriter w)
    {
        w.WriteULong(epoch); w.WriteInt(roundNumber); w.WriteEnum(activeSide);
        w.WriteLong(createdAtUnixMilliseconds); w.WriteString(displayMetadata ?? string.Empty);
    }

    public void Deserialize(PacketReader r)
    {
        epoch = r.ReadULong(); roundNumber = r.ReadInt(); activeSide = r.ReadEnum<CombatSide>();
        createdAtUnixMilliseconds = r.ReadLong(); displayMetadata = r.ReadString();
    }
}

public struct TurnHistorySyncMessage : INetMessage, IPacketSerializable
{
    public List<TurnHistoryMetadata> entries;
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter w) => w.WriteList(entries ?? new List<TurnHistoryMetadata>());
    public void Deserialize(PacketReader r) => entries = r.ReadList<TurnHistoryMetadata>();
}

internal sealed record TurnReplayAnchor(
    ulong Epoch,
    int RoundNumber,
    NetFullCombatState CombatState,
    ExtendedReconnectMessage ExtendedState,
    int ReplayEventIndex,
    byte[] Payload,
    string PayloadHash);

internal sealed record FrozenReplaySession(
    TurnReplayAnchor Anchor,
    IReadOnlyList<DeterministicInputEvent> Events,
    ExtendedReconnectMessage FinalExtendedState,
    string FinalStateHash);

internal sealed record RewindReplayPlan(
    TurnReplayAnchor Root,
    TurnReplayAnchor Target,
    IReadOnlyList<DeterministicInputEvent> Events);

internal static class ReplayPayloadCodec
{
    // Application-level parts are sent through RitsuLib's 16 KiB CRC-protected chunk stream.
    // Keep each completed sidecar payload comfortably below RitsuLib's 4 MiB logical limit.
    internal const int ChunkSize = 1024 * 1024;
    internal const int MaxAnchorBytes = 16 * 1024 * 1024;
    internal const int MaxReplayEvents = 100_000;

    internal static byte[] Serialize(NetFullCombatState state, ExtendedReconnectMessage extended)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(state);
        writer.Write(extended);
        return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    internal static void LogFingerprint(
        string label, NetFullCombatState state, ExtendedReconnectMessage extended)
    {
        ReconnectLog.Write($"State fingerprint {label}: {FingerprintSummary(state, extended)}.");
        LogRngCheckpoint(label, state, extended);
        ReconnectLog.Write(
            $"Combat history fingerprint {label}: entries={extended.combatHistory.entries.Count}, " +
            $"cards={extended.combatHistory.cards.Count}, plays={extended.combatHistory.cardPlays.Count}, " +
            $"hash={CombatHistoryStateSupport.Fingerprint(extended.combatHistory)[..12]}.");

        foreach (NetFullCombatState.PlayerState player in state.Players)
        {
            ReconnectLog.Write(
                $"State fingerprint {label} player={player.playerId}: " +
                $"scalar={HashPart(writer => { writer.WriteULong(player.playerId); writer.WriteModelEntry(player.characterId); writer.WriteInt(player.turnNumber); writer.WriteEnum(player.phase); writer.WriteInt(player.energy); writer.WriteInt(player.stars); writer.WriteInt(player.maxPotionCount); writer.WriteInt(player.gold); })}, " +
                $"piles={HashPart(writer => writer.WriteList(player.piles))}, " +
                $"potions={HashPart(writer => writer.WriteList(player.potions))}, " +
                $"relics={HashPart(writer => writer.WriteList(player.relics))}, " +
                $"orbs={HashPart(writer => writer.WriteList(player.orbs))}, " +
                $"rng={HashPart(writer => writer.Write(player.rngSet))}, " +
                $"relicBag={HashPart(writer => writer.Write(player.relicGrabBag))}.");
        }

        for (int index = 0; index < state.Creatures.Count; index++)
        {
            NetFullCombatState.CreatureState creature = state.Creatures[index];
            ReconnectLog.Write(
                $"State fingerprint {label} creature[{index}]: monster={creature.monsterId}, " +
                $"player={creature.playerId}, hp={creature.currentHp}/{creature.maxHp}, " +
                $"block={creature.block}, powers={HashPart(writer => writer.WriteList(creature.powers))}.");
        }
    }

    private static void LogRngCheckpoint(
        string label,
        NetFullCombatState state,
        ExtendedReconnectMessage extended)
    {
        string[] names =
        {
            "UpFront", "Shuffle", "UnknownMapPoint", "CombatCardGeneration",
            "CombatPotionGeneration", "CombatCardSelection", "CombatEnergyCosts",
            "CombatTargets", "MonsterAi", "Niche", "CombatOrbGeneration", "TreasureRoomRelics"
        };
        object rngSet = state.Rng;
        var counters = new List<string>();
        foreach (string name in names)
        {
            object? rng = rngSet.GetType().GetProperty(name)?.GetValue(rngSet) ??
                          rngSet.GetType().GetField(name)?.GetValue(rngSet);
            if (rng == null) continue;
            object? counter = rng.GetType().GetProperty("Counter")?.GetValue(rng) ??
                              rng.GetType().GetProperty("counter")?.GetValue(rng) ??
                              rng.GetType().GetField("Counter")?.GetValue(rng) ??
                              rng.GetType().GetField("counter")?.GetValue(rng);
            counters.Add($"{name}={counter ?? "?"}");
        }
        string encounterRng = HashPart(writer =>
        {
            foreach (ExtendedCreatureState creature in extended.creatures)
            {
                writer.WriteInt(creature.monsterRngCounter);
                writer.WriteULong(creature.monsterRngState0);
                writer.WriteULong(creature.monsterRngState1);
                writer.WriteULong(creature.monsterRngState2);
                writer.WriteULong(creature.monsterRngState3);
            }
        });
        ReconnectLog.Write(
            $"RNG checkpoint {label}: {string.Join(",", counters)}, encounterRng={encounterRng}, " +
            $"totalFloor={extended.actRoomStates.Sum(act => act.mapPointHistoryCount)}, " +
            $"mapHistory=[{string.Join(",", extended.actRoomStates.Select(act => act.mapPointHistoryCount))}].");
    }

    internal static string FingerprintSummary(
        NetFullCombatState state, ExtendedReconnectMessage extended)
        =>
            $"netCore={HashPart(writer => { writer.Write(state.Rng); WriteUInts(writer, state.nextChoiceIds); WriteInts(writer, state.nextRewardIds); writer.WriteBool(state.lastExecutedActionId.HasValue); if (state.lastExecutedActionId.HasValue) writer.WriteUInt(state.lastExecutedActionId.Value); writer.WriteBool(state.lastExecutedHookId.HasValue); if (state.lastExecutedHookId.HasValue) writer.WriteUInt(state.lastExecutedHookId.Value); })}, " +
            $"creatures={HashPart(writer => writer.WriteList(state.Creatures))}, " +
            $"extendedCore={HashPart(writer => { writer.WriteUInt(extended.nextActionId); writer.WriteUInt(extended.nextHookId); writer.WriteUInt(extended.nextChecksumId); writer.WriteInt(extended.roundNumber); writer.WriteEnum(extended.currentSide); writer.WriteEnum(extended.actionSynchronizerState); WriteULongs(writer, extended.playersReadyToEndTurn); WriteULongs(writer, extended.playersReadyToBeginEnemyTurn); WriteULongs(writer, extended.playersTakingExtraTurn); writer.WriteModelEntry(extended.encounterId); writer.WriteList(extended.actRoomStates); })}, " +
            $"extendedCreatures={HashPart(writer => writer.WriteList(extended.creatures))}, " +
            $"extendedRelics={HashPart(writer => writer.WriteList(extended.relicRuntimeStates))}, " +
            $"extendedCards={HashPart(writer => writer.WriteList(extended.playerCards))}, " +
            $"combatHistory={HashPart(writer => writer.Write(extended.combatHistory))}";

    internal static string CardFingerprintSummary(ExtendedReconnectMessage extended)
        => string.Join(";", extended.playerCards.Select(player =>
            $"{player.playerId}[{string.Join(",", player.cards.Select(card =>
                $"{card.combatCardId}:{HashPart(writer => writer.Write(card))}"))}]"));

    private static string HashPart(Action<PacketWriter> write)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        write(writer);
        return Convert.ToHexString(SHA256.HashData(
            writer.Buffer.AsSpan(0, writer.BytePosition)))[..12];
    }

    private static void WriteUInts(PacketWriter writer, IReadOnlyList<uint> values)
    {
        writer.WriteInt(values.Count);
        foreach (uint value in values) writer.WriteUInt(value);
    }

    private static void WriteInts(PacketWriter writer, IReadOnlyList<int> values)
    {
        writer.WriteInt(values.Count);
        foreach (int value in values) writer.WriteInt(value);
    }

    private static void WriteULongs(PacketWriter writer, IReadOnlyList<ulong> values)
    {
        writer.WriteInt(values.Count);
        foreach (ulong value in values) writer.WriteULong(value);
    }

    internal static (NetFullCombatState State, ExtendedReconnectMessage Extended) Deserialize(byte[] payload)
    {
        var reader = new PacketReader();
        reader.Reset(payload);
        var state = new NetFullCombatState();
        state.Deserialize(reader);
        var extended = new ExtendedReconnectMessage();
        extended.Deserialize(reader);
        return (state, extended);
    }

    internal static string Hash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));
}
