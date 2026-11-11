using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using STS2RitsuLib.Networking.Sidecar;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace CombatReconnect;

internal static class SidecarReplayTransport
{
    private const string ModuleId = "CombatReconnect";
    private const int FrameBytes = 12 * 1024;
    private const int MaxFrames = 2048;
    private const int MaxGapIndices = 256;
    private const int MaxInboundTransfers = 4;
    private const int MaxInboundBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan OfferRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(18);
    private static readonly ulong OfferOpcode = RitsuLibSidecarOpcodes.For(ModuleId, "anchor-offer-v60");
    private static readonly ulong FrameOpcode = RitsuLibSidecarOpcodes.For(ModuleId, "anchor-frame-v60");
    private static readonly ulong GapOpcode = RitsuLibSidecarOpcodes.For(ModuleId, "anchor-gap-v60");
    private static readonly ulong CompleteOpcode = RitsuLibSidecarOpcodes.For(ModuleId, "anchor-complete-v60");
    private static readonly object Gate = new();
    private static readonly Dictionary<(ulong Peer, string TransferId), OutboundTransfer> Outbound = new();
    private static readonly Dictionary<string, InboundTransfer> Inbound = new();
    private static readonly Dictionary<string, CompletedTransfer> Completed = new();
    private static bool _initialized;
    private static INetGameService? _clientService;
    private static ulong? _expectedHost;

    internal static event Action<SidecarAnchorKey>? AnchorAvailable;

    internal static string DescriptorHash => ReplayPayloadCodec.Hash(
        System.Text.Encoding.UTF8.GetBytes(
            $"{ModuleId}|anchor-offer-v60|{OfferOpcode}|anchor-frame-v60|{FrameOpcode}|" +
            $"anchor-gap-v60|{GapOpcode}|anchor-complete-v60|{CompleteOpcode}"));

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RitsuLibSidecarProtocol.EnsureDefaultHandlers();
        RitsuLibSidecarBus.RegisterHandler(OfferOpcode, OnOffer);
        RitsuLibSidecarBus.RegisterHandler(FrameOpcode, OnFrame);
        RitsuLibSidecarBus.RegisterHandler(GapOpcode, OnGap);
        RitsuLibSidecarBus.RegisterHandler(CompleteOpcode, OnComplete);
        ReconnectLog.Write(
            $"Registered CombatReconnect sidecar anchor transport: offer={OfferOpcode}, frame={FrameOpcode}, " +
            $"gap={GapOpcode}, complete={CompleteOpcode}, descriptorHash={DescriptorHash}.");
    }

    internal static void PrepareClient(INetGameService service)
    {
        Initialize();
        RitsuLibSidecarSessionManager.ObserveNetService(service);
        if (service is NetClientGameService client)
        {
            lock (Gate)
            {
                _clientService = service;
                _expectedHost = client.HostNetId;
            }
            RitsuLibSidecarSessionManager.SetPeerReachabilityHint(
                client.HostNetId,
                RitsuLibSidecarPeerReachability.Supported);
            RitsuLibSidecarConnectionExchange.TrySendClientHelloIfReachable(service);
        }
    }

    internal static void PrepareHostPeer(INetGameService service, ulong peerNetId)
    {
        Initialize();
        lock (Gate)
            foreach (OutboundTransfer transfer in Outbound.Values.Where(transfer => transfer.PeerNetId == peerNetId))
                transfer.Service = service;
        RitsuLibSidecarSessionManager.ObserveNetService(service);
        RitsuLibSidecarSessionManager.NotePeerConnected(peerNetId);
        RitsuLibSidecarSessionManager.SetPeerReachabilityHint(
            peerNetId,
            RitsuLibSidecarPeerReachability.Supported);
        RitsuLibSidecarConnectionExchange.TrySendClientHelloIfReachable(service);
    }

    internal static Task SendAnchorAsync(
        INetGameService service,
        ulong peerNetId,
        SidecarAnchorKey key,
        byte[] encodedPayload,
        string encodedHash,
        CancellationToken cancellationToken = default)
    {
        Initialize();
        cancellationToken.ThrowIfCancellationRequested();
        if (encodedPayload.Length <= 0 || encodedPayload.Length > ReplayPayloadCodec.MaxAnchorBytes)
            throw new InvalidDataException($"Invalid encoded anchor length {encodedPayload.Length}.");
        if (encodedHash != ReplayPayloadCodec.Hash(encodedPayload))
            throw new InvalidDataException("Encoded anchor hash did not match before transfer.");

        int frameCount = (encodedPayload.Length + FrameBytes - 1) / FrameBytes;
        if (frameCount <= 0 || frameCount > MaxFrames)
            throw new InvalidDataException($"Invalid sidecar anchor frame count {frameCount}.");
        var frames = new byte[frameCount][];
        for (int index = 0; index < frameCount; index++)
        {
            int offset = index * FrameBytes;
            int length = Math.Min(FrameBytes, encodedPayload.Length - offset);
            frames[index] = encodedPayload.AsSpan(offset, length).ToArray();
        }

        var transfer = new OutboundTransfer(service, peerNetId, key, encodedPayload.Length, encodedHash, frames);
        lock (Gate)
        {
            if (!Outbound.TryAdd((peerNetId, key.TransferId), transfer))
                throw new InvalidOperationException($"Duplicate sidecar transfer {key.TransferId} for {peerNetId}.");
        }
        transfer.CancellationRegistration = cancellationToken.Register(() => CancelOutbound(transfer,
            new OperationCanceledException("Sidecar anchor transfer was cancelled.", cancellationToken), false));
        try
        {
            SendOfferAndFrames(transfer, null);
        }
        catch (Exception ex)
        {
            CancelOutbound(transfer, ex);
            throw;
        }
        _ = RetryUntilComplete(transfer);
        return transfer.Completion.Task;
    }

    internal static bool TryTakeAnchor(SidecarAnchorKey expected, out byte[] payload)
    {
        lock (Gate)
        {
            if (Completed.TryGetValue(expected.TransferId, out CompletedTransfer? completed) &&
                completed.Key == expected)
            {
                Completed.Remove(expected.TransferId);
                payload = completed.Payload;
                return true;
            }
        }
        payload = Array.Empty<byte>();
        return false;
    }

    internal static void Discard(string? transferId)
    {
        if (string.IsNullOrEmpty(transferId)) return;
        lock (Gate)
        {
            Inbound.Remove(transferId);
            Completed.Remove(transferId);
        }
    }

    internal static void Reset()
    {
        List<OutboundTransfer> outbound;
        lock (Gate)
        {
            outbound = Outbound.Values.ToList();
            Outbound.Clear();
            Inbound.Clear();
            Completed.Clear();
            _clientService = null;
            _expectedHost = null;
        }
        foreach (OutboundTransfer transfer in outbound)
        {
            transfer.CancellationRegistration.Dispose();
            transfer.Completion.TrySetCanceled();
        }
    }

    internal static byte[] RoundTripForSelfTest(byte[] payload)
    {
        string hash = ReplayPayloadCodec.Hash(payload);
        var key = new SidecarAnchorKey(SidecarAnchorKind.Rewind, "self-test-transfer", "self-test", 1, 1);
        int count = ExpectedFrameCount(payload.Length);
        OfferMessage offer = Deserialize<OfferMessage>(Serialize(new OfferMessage(key, payload.Length, count, hash)));
        if (!ValidateOffer(offer)) throw new InvalidDataException("Sidecar offer round-trip validation failed.");
        var inbound = new InboundTransfer(key, 1, payload.Length, hash, count);
        for (int index = count - 1; index >= 0; index--)
        {
            int offset = index * FrameBytes;
            int length = Math.Min(FrameBytes, payload.Length - offset);
            FrameMessage frame = Deserialize<FrameMessage>(Serialize(new FrameMessage(key, payload.Length, count,
                hash, index, payload.AsSpan(offset, length).ToArray())));
            if (!ValidateFrame(frame)) throw new InvalidDataException($"Sidecar frame {index} round-trip validation failed.");
            inbound.Frames[index] = frame.payload;
            inbound.Received++;
        }
        byte[] merged = MergeAndValidate(inbound);
        FrameMessage corrupt = new(key, payload.Length, count, hash, 0,
            payload.AsSpan(0, Math.Min(FrameBytes, payload.Length)).ToArray());
        corrupt.checksum++;
        if (ValidateFrame(corrupt)) throw new InvalidDataException("Sidecar frame checksum corruption was not rejected.");
        return merged;
    }

    private static void OnOffer(RitsuLibSidecarDispatchContext context)
    {
        Dispatch(() =>
        {
            try
            {
                OfferMessage offer = Deserialize<OfferMessage>(context.Payload.Span);
                if (!ValidateSender(context.SenderNetId) || !ValidateOffer(offer)) return;
                lock (Gate)
                {
                    if (Completed.ContainsKey(offer.transferId))
                    {
                        SendComplete_NoLock(offer.Key, offer.encodedHash);
                        return;
                    }
                    if (!Inbound.TryGetValue(offer.transferId, out InboundTransfer? transfer))
                    {
                        if (!CanAcceptInbound_NoLock(offer.encodedBytes)) return;
                        var created = new InboundTransfer(offer.Key, context.SenderNetId,
                            offer.encodedBytes, offer.encodedHash, offer.frameCount);
                        Inbound[offer.transferId] = created;
                        ScheduleInboundExpiry(created);
                    }
                    else if (!transfer.Matches(offer))
                        Inbound.Remove(offer.transferId);
                    else
                        ScheduleGap_NoLock(transfer);
                }
            }
            catch (Exception ex)
            {
                ReconnectLog.Write($"Rejected sidecar anchor offer from {context.SenderNetId}: {ex.Message}");
            }
        });
    }

    private static void OnFrame(RitsuLibSidecarDispatchContext context)
    {
        Dispatch(() =>
        {
            try
            {
                FrameMessage frame = Deserialize<FrameMessage>(context.Payload.Span);
                if (!ValidateSender(context.SenderNetId) || !ValidateFrame(frame)) return;
                InboundTransfer transfer;
                byte[]? completed = null;
                lock (Gate)
                {
                    if (Completed.ContainsKey(frame.transferId))
                    {
                        SendComplete_NoLock(frame.Key, frame.encodedHash);
                        return;
                    }
                    if (!Inbound.TryGetValue(frame.transferId, out transfer!))
                    {
                        if (!CanAcceptInbound_NoLock(frame.encodedBytes)) return;
                        transfer = new InboundTransfer(frame.Key, context.SenderNetId, frame.encodedBytes,
                            frame.encodedHash, frame.frameCount);
                        Inbound[frame.transferId] = transfer;
                        ScheduleInboundExpiry(transfer);
                    }
                    if (!transfer.Matches(frame) || transfer.Frames[frame.index] != null) return;
                    transfer.Frames[frame.index] = frame.payload;
                    transfer.Received++;
                    if (transfer.Received == transfer.Frames.Length)
                    {
                        completed = MergeAndValidate(transfer);
                        Inbound.Remove(frame.transferId);
                        Completed[frame.transferId] = new CompletedTransfer(frame.Key, completed);
                        SendComplete_NoLock(frame.Key, frame.encodedHash);
                    }
                    else
                    {
                        ScheduleGap_NoLock(transfer);
                    }
                }
                if (completed != null)
                {
                    ReconnectLog.Write($"Received complete sidecar anchor transfer={frame.transferId}, " +
                                       $"kind={frame.kind}, bytes={completed.Length}, frames={frame.frameCount}.");
                    ScheduleCompletedExpiry(frame.Key);
                    AnchorAvailable?.Invoke(frame.Key);
                }
            }
            catch (Exception ex)
            {
                ReconnectLog.Write($"Rejected sidecar anchor frame from {context.SenderNetId}: {ex.Message}");
            }
        });
    }

    private static void OnGap(RitsuLibSidecarDispatchContext context)
    {
        Dispatch(() =>
        {
            try
            {
                GapMessage gap = Deserialize<GapMessage>(context.Payload.Span);
                OutboundTransfer? transfer;
                lock (Gate)
                    Outbound.TryGetValue((context.SenderNetId, gap.transferId), out transfer);
                if (transfer == null || transfer.Key != gap.Key || gap.indices == null ||
                    gap.indices.Count == 0 || gap.indices.Count > MaxGapIndices) return;
                foreach (int index in gap.indices.Distinct())
                {
                    if (index < 0 || index >= transfer.Frames.Length) return;
                    SendFrame(transfer, index);
                }
                ReconnectLog.Write($"Resent {gap.indices.Count} missing sidecar anchor frame(s) " +
                                   $"transfer={gap.transferId}, peer={context.SenderNetId}.");
            }
            catch (Exception ex)
            {
                ReconnectLog.Write($"Rejected sidecar anchor gap from {context.SenderNetId}: {ex.Message}");
            }
        });
    }

    private static void OnComplete(RitsuLibSidecarDispatchContext context)
    {
        Dispatch(() =>
        {
            try
            {
                CompleteMessage complete = Deserialize<CompleteMessage>(context.Payload.Span);
                OutboundTransfer? transfer;
                lock (Gate)
                {
                    if (!Outbound.TryGetValue((context.SenderNetId, complete.transferId), out transfer) ||
                        transfer.Key != complete.Key || transfer.EncodedHash != complete.encodedHash) return;
                    Outbound.Remove((context.SenderNetId, complete.transferId));
                }
                transfer.CancellationRegistration.Dispose();
                transfer.Completion.TrySetResult();
                ReconnectLog.Write($"Sidecar anchor accepted transfer={complete.transferId}, peer={context.SenderNetId}.");
            }
            catch (Exception ex)
            {
                ReconnectLog.Write($"Rejected sidecar anchor complete from {context.SenderNetId}: {ex.Message}");
            }
        });
    }

    private static void SendOfferAndFrames(OutboundTransfer transfer, IEnumerable<int>? selected)
    {
        transfer.Attempts++;
        var offer = new OfferMessage(transfer.Key, transfer.EncodedBytes, transfer.Frames.Length, transfer.EncodedHash);
        SendToPeer(transfer, OfferOpcode, Serialize(offer), "offer");
        foreach (int index in selected ?? Enumerable.Range(0, transfer.Frames.Length)) SendFrame(transfer, index);
        ReconnectLog.Write($"Queued sidecar anchor transfer={transfer.Key.TransferId}, kind={transfer.Key.Kind}, " +
                           $"peer={transfer.PeerNetId}, bytes={transfer.EncodedBytes}, frames={transfer.Frames.Length}, attempt={transfer.Attempts}.");
    }

    private static void SendFrame(OutboundTransfer transfer, int index)
    {
        var frame = new FrameMessage(transfer.Key, transfer.EncodedBytes, transfer.Frames.Length,
            transfer.EncodedHash, index, transfer.Frames[index]);
        SendToPeer(transfer, FrameOpcode, Serialize(frame), $"frame {index}");
    }

    private static void SendToPeer(OutboundTransfer transfer, ulong opcode, byte[] payload, string label)
    {
        if (!RitsuLibSidecarHighLevelSend.TrySendAsHostToPeer(transfer.Service, transfer.PeerNetId, opcode,
                payload, RitsuLibSidecarDeliverySemantics.StableSync))
            throw new InvalidOperationException($"Could not queue sidecar anchor {label} for peer {transfer.PeerNetId}.");
    }

    private static async Task RetryUntilComplete(OutboundTransfer transfer)
    {
        try
        {
            long deadline = Environment.TickCount64 + (long)TransferTimeout.TotalMilliseconds;
            while (!transfer.Completion.Task.IsCompleted)
            {
                await Task.Delay(OfferRetryDelay);
                if (transfer.Completion.Task.IsCompleted) return;
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException($"Sidecar anchor transfer {transfer.Key.TransferId} was not acknowledged.");
                await RunOnMainLoop(() => SendOffer(transfer));
            }
        }
        catch (Exception ex)
        {
            CancelOutbound(transfer, ex);
        }
    }

    private static void SendOffer(OutboundTransfer transfer)
    {
        transfer.Attempts++;
        var offer = new OfferMessage(transfer.Key, transfer.EncodedBytes, transfer.Frames.Length, transfer.EncodedHash);
        SendToPeer(transfer, OfferOpcode, Serialize(offer), "offer");
        ReconnectLog.Write($"Retried sidecar anchor offer transfer={transfer.Key.TransferId}, " +
                           $"peer={transfer.PeerNetId}, attempt={transfer.Attempts}.");
    }

    private static void CancelOutbound(OutboundTransfer transfer, Exception error, bool disposeRegistration = true)
    {
        lock (Gate) Outbound.Remove((transfer.PeerNetId, transfer.Key.TransferId));
        if (disposeRegistration) transfer.CancellationRegistration.Dispose();
        transfer.Completion.TrySetException(error);
    }

    private static bool ValidateSender(ulong sender)
    {
        lock (Gate) return _clientService is NetClientGameService && _expectedHost == sender;
    }

    private static bool ValidateOffer(OfferMessage offer)
        => offer.Key.IsValid && offer.encodedBytes > 0 && offer.encodedBytes <= ReplayPayloadCodec.MaxAnchorBytes &&
           offer.frameCount == ExpectedFrameCount(offer.encodedBytes) && offer.frameCount <= MaxFrames &&
           IsSha256(offer.encodedHash);

    private static bool ValidateFrame(FrameMessage frame)
        => frame.Key.IsValid && frame.encodedBytes > 0 && frame.encodedBytes <= ReplayPayloadCodec.MaxAnchorBytes &&
           frame.frameCount == ExpectedFrameCount(frame.encodedBytes) && frame.frameCount <= MaxFrames &&
           frame.index >= 0 && frame.index < frame.frameCount && frame.payload != null &&
           frame.payload.Length == ExpectedFrameLength(frame.encodedBytes, frame.index) &&
           frame.checksum == FrameChecksum(frame.payload) && IsSha256(frame.encodedHash);

    private static int ExpectedFrameCount(int bytes) => (bytes + FrameBytes - 1) / FrameBytes;
    private static int ExpectedFrameLength(int bytes, int index) => Math.Min(FrameBytes, bytes - index * FrameBytes);
    private static bool IsSha256(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static byte[] MergeAndValidate(InboundTransfer transfer)
    {
        var payload = GC.AllocateUninitializedArray<byte>(transfer.EncodedBytes);
        int offset = 0;
        foreach (byte[]? frame in transfer.Frames)
        {
            if (frame == null) throw new InvalidDataException("Sidecar anchor frame gap remained at completion.");
            frame.CopyTo(payload, offset);
            offset += frame.Length;
        }
        if (offset != payload.Length || ReplayPayloadCodec.Hash(payload) != transfer.EncodedHash)
            throw new InvalidDataException("Completed sidecar anchor length or SHA-256 mismatch.");
        return payload;
    }

    private static bool CanAcceptInbound_NoLock(int encodedBytes)
    {
        if (Inbound.Count + Completed.Count >= MaxInboundTransfers) return false;
        long retained = Inbound.Values.Sum(transfer => (long)transfer.EncodedBytes) +
                        Completed.Values.Sum(transfer => (long)transfer.Payload.Length);
        return retained + encodedBytes <= MaxInboundBytes;
    }

    private static void ScheduleCompletedExpiry(SidecarAnchorKey key)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
        {
            lock (Gate)
                if (Completed.TryGetValue(key.TransferId, out CompletedTransfer? completed) && completed.Key == key)
                    Completed.Remove(key.TransferId);
        });
    }

    private static void ScheduleInboundExpiry(InboundTransfer transfer)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
        {
            lock (Gate)
                if (Inbound.TryGetValue(transfer.Key.TransferId, out InboundTransfer? current) &&
                    ReferenceEquals(current, transfer))
                    Inbound.Remove(transfer.Key.TransferId);
        });
    }

    private static void ScheduleGap_NoLock(InboundTransfer transfer)
    {
        if (transfer.GapScheduled) return;
        transfer.GapScheduled = true;
        _ = Task.Delay(TimeSpan.FromMilliseconds(150)).ContinueWith(_ => Dispatch(() => FlushGap(transfer.Key.TransferId)));
    }

    private static void FlushGap(string transferId)
    {
        InboundTransfer? transfer;
        List<int> missing;
        INetGameService? service;
        lock (Gate)
        {
            if (!Inbound.TryGetValue(transferId, out transfer)) return;
            transfer.GapScheduled = false;
            missing = transfer.Frames.Select((frame, index) => (frame, index)).Where(x => x.frame == null)
                .Take(MaxGapIndices).Select(x => x.index).ToList();
            service = _clientService;
        }
        if (missing.Count == 0 || service is not NetClientGameService) return;
        var gap = new GapMessage(transfer.Key, missing);
        if (!RitsuLibSidecarHighLevelSend.TrySendAsClient(service, GapOpcode, Serialize(gap),
                RitsuLibSidecarDeliverySemantics.StableSync))
            ReconnectLog.Write($"Could not queue sidecar anchor gap transfer={transferId}.");
    }

    private static void SendComplete_NoLock(SidecarAnchorKey key, string encodedHash)
    {
        INetGameService? service = _clientService;
        if (service is not NetClientGameService) return;
        var complete = new CompleteMessage(key, encodedHash);
        if (!RitsuLibSidecarHighLevelSend.TrySendAsClient(service, CompleteOpcode, Serialize(complete),
                RitsuLibSidecarDeliverySemantics.StableSync))
            ReconnectLog.Write($"Could not queue sidecar anchor complete transfer={key.TransferId}.");
    }

    internal static void Dispatch(Action action)
    {
        if (!RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(action)) action();
    }

    private static Task RunOnMainLoop(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private static uint FrameChecksum(byte[] payload)
        => BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(payload));

    private static byte[] Serialize(IPacketSerializable value)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        value.Serialize(writer);
        return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> payload) where T : struct, IPacketSerializable
    {
        var reader = new PacketReader();
        reader.Reset(payload.ToArray());
        var value = new T();
        value.Deserialize(reader);
        return value;
    }

    private sealed class OutboundTransfer
    {
        internal OutboundTransfer(INetGameService service, ulong peerNetId, SidecarAnchorKey key,
            int encodedBytes, string encodedHash, byte[][] frames)
        { Service = service; PeerNetId = peerNetId; Key = key; EncodedBytes = encodedBytes; EncodedHash = encodedHash; Frames = frames; }
        internal INetGameService Service { get; set; }
        internal ulong PeerNetId { get; }
        internal SidecarAnchorKey Key { get; }
        internal int EncodedBytes { get; }
        internal string EncodedHash { get; }
        internal byte[][] Frames { get; }
        internal int Attempts { get; set; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed class InboundTransfer
    {
        internal InboundTransfer(SidecarAnchorKey key, ulong sender, int encodedBytes, string encodedHash, int frameCount)
        { Key = key; Sender = sender; EncodedBytes = encodedBytes; EncodedHash = encodedHash; Frames = new byte[frameCount][]; }
        internal SidecarAnchorKey Key { get; }
        internal ulong Sender { get; }
        internal int EncodedBytes { get; }
        internal string EncodedHash { get; }
        internal byte[][] Frames { get; }
        internal int Received { get; set; }
        internal bool GapScheduled { get; set; }
        internal bool Matches(OfferMessage m) => Key == m.Key && EncodedBytes == m.encodedBytes && EncodedHash == m.encodedHash && Frames.Length == m.frameCount;
        internal bool Matches(FrameMessage m) => Key == m.Key && EncodedBytes == m.encodedBytes && EncodedHash == m.encodedHash && Frames.Length == m.frameCount;
    }

    private sealed record CompletedTransfer(SidecarAnchorKey Key, byte[] Payload);

    private struct OfferMessage : IPacketSerializable
    {
        internal OfferMessage(SidecarAnchorKey key, int bytes, int count, string hash)
        { kind = key.Kind; transferId = key.TransferId; transactionId = key.TransactionId; epoch = key.Epoch; generation = key.Generation; encodedBytes = bytes; frameCount = count; encodedHash = hash; }
        public SidecarAnchorKind kind; public string transferId; public string transactionId; public ulong epoch; public int generation;
        public int encodedBytes; public int frameCount; public string encodedHash;
        public SidecarAnchorKey Key => new(kind, transferId, transactionId, epoch, generation);
        public void Serialize(PacketWriter w) { WriteKey(w, Key); w.WriteInt(encodedBytes); w.WriteInt(frameCount); w.WriteString(encodedHash ?? string.Empty); }
        public void Deserialize(PacketReader r) { ReadKey(r, out kind, out transferId, out transactionId, out epoch, out generation); encodedBytes = r.ReadInt(); frameCount = r.ReadInt(); encodedHash = r.ReadString(); }
    }

    private struct FrameMessage : IPacketSerializable
    {
        internal FrameMessage(SidecarAnchorKey key, int bytes, int count, string hash, int frameIndex, byte[] data)
        { kind = key.Kind; transferId = key.TransferId; transactionId = key.TransactionId; epoch = key.Epoch; generation = key.Generation; encodedBytes = bytes; frameCount = count; encodedHash = hash; index = frameIndex; payload = data; checksum = FrameChecksum(data); }
        public SidecarAnchorKind kind; public string transferId; public string transactionId; public ulong epoch; public int generation;
        public int encodedBytes; public int frameCount; public string encodedHash; public int index; public uint checksum; public byte[] payload;
        public SidecarAnchorKey Key => new(kind, transferId, transactionId, epoch, generation);
        public void Serialize(PacketWriter w) { WriteKey(w, Key); w.WriteInt(encodedBytes); w.WriteInt(frameCount); w.WriteString(encodedHash ?? string.Empty); w.WriteInt(index); w.WriteUInt(checksum); byte[] p = payload ?? Array.Empty<byte>(); w.WriteInt(p.Length); w.WriteBytes(p, p.Length); }
        public void Deserialize(PacketReader r) { ReadKey(r, out kind, out transferId, out transactionId, out epoch, out generation); encodedBytes = r.ReadInt(); frameCount = r.ReadInt(); encodedHash = r.ReadString(); index = r.ReadInt(); checksum = r.ReadUInt(); int length = r.ReadInt(); if (length < 0 || length > FrameBytes) throw new InvalidDataException($"Invalid sidecar frame length {length}."); payload = new byte[length]; r.ReadBytes(payload, length); }
    }

    private struct GapMessage : IPacketSerializable
    {
        internal GapMessage(SidecarAnchorKey key, List<int> missing) { kind = key.Kind; transferId = key.TransferId; transactionId = key.TransactionId; epoch = key.Epoch; generation = key.Generation; indices = missing; }
        public SidecarAnchorKind kind; public string transferId; public string transactionId; public ulong epoch; public int generation; public List<int> indices;
        public SidecarAnchorKey Key => new(kind, transferId, transactionId, epoch, generation);
        public void Serialize(PacketWriter w) { WriteKey(w, Key); List<int> list = indices ?? new(); w.WriteInt(list.Count); foreach (int index in list) w.WriteInt(index); }
        public void Deserialize(PacketReader r) { ReadKey(r, out kind, out transferId, out transactionId, out epoch, out generation); int count = r.ReadInt(); if (count < 0 || count > MaxGapIndices) throw new InvalidDataException($"Invalid gap count {count}."); indices = new List<int>(count); for (int i = 0; i < count; i++) indices.Add(r.ReadInt()); }
    }

    private struct CompleteMessage : IPacketSerializable
    {
        internal CompleteMessage(SidecarAnchorKey key, string hash) { kind = key.Kind; transferId = key.TransferId; transactionId = key.TransactionId; epoch = key.Epoch; generation = key.Generation; encodedHash = hash; }
        public SidecarAnchorKind kind; public string transferId; public string transactionId; public ulong epoch; public int generation; public string encodedHash;
        public SidecarAnchorKey Key => new(kind, transferId, transactionId, epoch, generation);
        public void Serialize(PacketWriter w) { WriteKey(w, Key); w.WriteString(encodedHash ?? string.Empty); }
        public void Deserialize(PacketReader r) { ReadKey(r, out kind, out transferId, out transactionId, out epoch, out generation); encodedHash = r.ReadString(); }
    }

    private static void WriteKey(PacketWriter w, SidecarAnchorKey key)
    { w.WriteByte((byte)key.Kind); w.WriteString(key.TransferId); w.WriteString(key.TransactionId); w.WriteULong(key.Epoch); w.WriteInt(key.Generation); }

    private static void ReadKey(PacketReader r, out SidecarAnchorKind kind, out string transferId,
        out string transactionId, out ulong epoch, out int generation)
    { kind = (SidecarAnchorKind)r.ReadByte(); transferId = r.ReadString(); transactionId = r.ReadString(); epoch = r.ReadULong(); generation = r.ReadInt(); }
}

internal enum SidecarAnchorKind : byte { Replay = 1, Rewind = 2 }

internal readonly record struct SidecarAnchorKey(
    SidecarAnchorKind Kind, string TransferId, string TransactionId, ulong Epoch, int Generation)
{
    internal bool IsValid => (Kind is SidecarAnchorKind.Replay or SidecarAnchorKind.Rewind) &&
                             !string.IsNullOrWhiteSpace(TransferId) && TransferId.Length <= 128 &&
                             !string.IsNullOrWhiteSpace(TransactionId) && TransactionId.Length <= 128 && Generation >= 0;
}

public enum ReplayAnchorCompression : byte
{
    None = 0,
    Brotli = 1,
}

internal sealed record EncodedReplayAnchor(
    byte[] Payload,
    ReplayAnchorCompression Compression,
    string Hash);

internal static class ReplayAnchorCompressionCodec
{
    private const int CompressionThreshold = 1024;
    private const int MinimumSavings = 32;

    internal static EncodedReplayAnchor Encode(byte[] raw, IEnumerable<ulong> peers)
    {
        ulong[] peerIds = peers.Distinct().ToArray();
        bool supportsBrotli = peerIds.Length != 0 && peerIds.All(peer =>
            RitsuLibSidecarSessionManager.TryGetPeerFeatures(peer, out var features) &&
            (features & RitsuLibSidecarPeerFeatures.BrotliPayloadCompression) != 0);
        return Encode(raw, supportsBrotli);
    }

    internal static EncodedReplayAnchor EncodeForSelfTest(byte[] raw, bool supportsBrotli)
        => Encode(raw, supportsBrotli);

    private static EncodedReplayAnchor Encode(byte[] raw, bool supportsBrotli)
    {
        if (!supportsBrotli || raw.Length < CompressionThreshold)
            return None(raw);

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(raw);
        byte[] compressed = output.ToArray();
        return compressed.Length + MinimumSavings <= raw.Length
            ? new(compressed, ReplayAnchorCompression.Brotli, ReplayPayloadCodec.Hash(compressed))
            : None(raw);
    }

    internal static byte[] Decode(
        byte[] encoded,
        ReplayAnchorCompression compression,
        int expectedRawBytes)
    {
        if (expectedRawBytes < 0 || expectedRawBytes > ReplayPayloadCodec.MaxAnchorBytes)
            throw new InvalidDataException($"Invalid decoded anchor length {expectedRawBytes}.");
        if (compression == ReplayAnchorCompression.None)
        {
            if (encoded.Length != expectedRawBytes)
                throw new InvalidDataException("Uncompressed anchor length did not match its manifest.");
            return encoded;
        }
        if (compression != ReplayAnchorCompression.Brotli)
            throw new InvalidDataException($"Unsupported replay anchor compression {(byte)compression}.");

        using var input = new MemoryStream(encoded, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        byte[] raw = GC.AllocateUninitializedArray<byte>(expectedRawBytes);
        int offset = 0;
        while (offset < raw.Length)
        {
            int read = brotli.Read(raw, offset, raw.Length - offset);
            if (read == 0) break;
            offset += read;
        }
        if (offset != raw.Length || brotli.ReadByte() != -1)
            throw new InvalidDataException("Brotli anchor output did not match its bounded manifest length.");
        return raw;
    }

    private static EncodedReplayAnchor None(byte[] raw)
        => new(raw, ReplayAnchorCompression.None, ReplayPayloadCodec.Hash(raw));
}
