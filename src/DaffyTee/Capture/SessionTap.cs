using System.Buffers;
using System.Collections.Concurrent;
using DaffyTee.Proxy;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// The tee itself, for one session: watches both directions of the relay, pairs every request with
/// its response, copies what the settings say to copy, and hands each finished pair to the pipeline.
///
/// It runs on the relay's own threads and so is held to the relay's rules: no waiting, no I/O, no
/// allocation beyond one small object per request, and memory copies only for what is actually
/// captured. With the tee switched off it still follows packet boundaries (so that switching it on
/// mid-session starts cleanly at the next request) but copies nothing.
///
/// Pairing relies on TDS being strictly request/response on a connection: one response message per
/// request message. That holds per <see cref="Conversation"/>. A plain session is one conversation;
/// a MARS session is one per SMP channel, each with the same rhythm, multiplexed on one socket - so
/// under MARS the bytes are first split by channel, then paired exactly as before.
/// </summary>
internal sealed class SessionTap : IRelayObserver
{
    private readonly CapturePipeline _pipeline;
    private readonly TeeSettings _settings;
    private readonly SessionSnapshot _session;
    private readonly bool _sessionSampled;
    private readonly Conversation? _single;
    private readonly ConcurrentDictionary<ushort, Conversation>? _channels;
    private readonly SmpReader? _clientFrames, _serverFrames;
    private readonly ChannelRouter? _clientRouter, _serverRouter;
    private long _seq;
    private (string Name, bool Always)? _gatewayCommand;

    public SessionTap(CapturePipeline pipeline, SessionSnapshot session, bool mars)
    {
        _pipeline = pipeline;
        _settings = pipeline.Settings;
        _session = session;
        _sessionSampled = _settings.SampleThisRequest();

        if (mars)
        {
            _channels = new ConcurrentDictionary<ushort, Conversation>();
            _clientFrames = new SmpReader();
            _serverFrames = new SmpReader();
            _clientRouter = new ChannelRouter(this, fromClient: true);
            _serverRouter = new ChannelRouter(this, fromClient: false);
        }
        else
        {
            _single = new Conversation(this, channel: null);
        }
    }

    /// <summary>
    /// Both directions are shown to the tap before the relay forwards them, so what it sees is in
    /// causal order - see <see cref="IRelayObserver"/>. Pairing depends on it.
    /// </summary>
    public void OnClientBytes(ReadOnlySpan<byte> data, long timestamp)
    {
        if (_single is not null)
        {
            _single.ClientBytes(data, timestamp);
            return;
        }

        _clientRouter!.Timestamp = timestamp;
        _clientFrames!.Feed(data, _clientRouter);
    }

    public void OnServerBytes(ReadOnlySpan<byte> data, long timestamp)
    {
        if (_single is not null)
        {
            _single.ServerBytes(data, timestamp);
            return;
        }

        _serverRouter!.Timestamp = timestamp;
        _serverFrames!.Feed(data, _serverRouter);
    }

    /// <summary>
    /// Hands one direction's SMP payloads to the right channel's conversation. There is one per
    /// direction, each fed only by its own pump's thread, so neither needs a lock.
    /// </summary>
    private sealed class ChannelRouter(SessionTap tap, bool fromClient) : ISmpListener
    {
        public long Timestamp { get; set; }

        public void OnFrame(ushort channel, byte flags)
        {
            if ((flags & SmpReader.Fin) != 0 && tap._channels!.TryRemove(channel, out var closed)) closed.Close();
        }

        public void OnData(ushort channel, ReadOnlySpan<byte> payload)
        {
            var conversation = tap._channels!.GetOrAdd(channel, c => new Conversation(tap, c));
            if (fromClient) conversation.ClientBytes(payload, Timestamp);
            else conversation.ServerBytes(payload, Timestamp);
        }
    }

    /// <summary>
    /// The relay's word that the next request is a daffytee.* command, answered by DaffyTee itself.
    /// Told on the client pump's thread just before the request's bytes, and taken by the request.
    /// </summary>
    public void OnGatewayCommand(string command, bool always) => _gatewayCommand = (command, always);

    public void OnClosed(string reason)
    {
        _single?.Close();
        if (_channels is null) return;
        foreach (var conversation in _channels.Values) conversation.Close();
        _channels.Clear();
    }

    private CapturedExchange BeginRequest(in PacketHeader header, long timestamp, ushort? channel)
    {
        var command = _gatewayCommand;
        _gatewayCommand = null;

        bool sampled = _settings.SampleBy == SampleBy.Session ? _sessionSampled : _settings.SampleThisRequest();
        bool known = header.Type is TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc
                                 or TdsConstants.PacketType.TransactionManager or TdsConstants.PacketType.BulkLoad;

        // An admin command is on the record whatever the tee is set to: who looked, who changed what.
        bool emit = known && (command is { Always: true } ||
                              _settings.Enabled && sampled && !(header.Type == TdsConstants.PacketType.BulkLoad && _settings.Bulk == BulkMode.Off));
        bool copy = emit && (header.Type != TdsConstants.PacketType.BulkLoad || _settings.Bulk == BulkMode.Full);

        return new CapturedExchange
        {
            Session = _session,
            Seq = Interlocked.Increment(ref _seq),
            Type = header.Type,
            ResetConnection = header.ResetsConnection,
            StartUtc = DateTime.UtcNow,
            StartTimestamp = timestamp,
            Emit = emit,
            Capturing = copy,
            MarsChannel = channel,
            GatewayCommand = command?.Name,
        };
    }

    private void Finish(CapturedExchange exchange)
    {
        if (!exchange.Emit)
        {
            exchange.Release(_pipeline.Budget);
            return;
        }

        if (exchange.Dropped)
        {
            _pipeline.Budget.RecordDrop(DropReason.QueueFull, exchange.RequestBytes);
            exchange.Release(_pipeline.Budget);
            return;
        }

        _pipeline.Submit(exchange);
    }

    /// <summary>
    /// One strict request/response exchange stream: a whole plain session, or one MARS channel.
    /// Attention (cancel) is its one wrinkle - it can end the current response early, or arrive
    /// after it and earn an acknowledgement of its own - and is handled as such.
    /// </summary>
    private sealed class Conversation
    {
        private readonly SessionTap _tap;
        private readonly ushort? _channel;
        private readonly PacketTracker _requestTracker = new();
        private readonly PacketTracker _responseTracker = new();
        private readonly RequestListener _requests;
        private readonly ResponseListener _responses;
        private readonly object _gate = new();
        private CapturedExchange? _awaitingResponse;
        private bool _attentionOutstanding;

        public Conversation(SessionTap tap, ushort? channel)
        {
            _tap = tap;
            _channel = channel;
            _requests = new RequestListener(this);
            _responses = new ResponseListener(this);
        }

        public void ClientBytes(ReadOnlySpan<byte> data, long timestamp)
        {
            _requests.Timestamp = timestamp;
            _requestTracker.Feed(data, _requests);
        }

        public void ServerBytes(ReadOnlySpan<byte> data, long timestamp)
        {
            _responses.Timestamp = timestamp;
            _responseTracker.Feed(data, _responses);
        }

        public void Close()
        {
            CapturedExchange? unanswered;
            lock (_gate)
            {
                unanswered = _awaitingResponse;
                _awaitingResponse = null;
            }

            if (unanswered is not null) _tap.Finish(unanswered);
            if (_requests.TakeCurrent() is { } partial) _tap.Finish(partial);
            _responses.DiscardBuffer();
        }

        private void RequestComplete(CapturedExchange exchange)
        {
            CapturedExchange? superseded;
            lock (_gate)
            {
                superseded = _awaitingResponse;
                _awaitingResponse = exchange;
            }

            // Only possible if a response went missing - finish the old one rather than pair it wrongly.
            if (superseded is not null) _tap.Finish(superseded);
        }

        /// <summary>Only worth keeping a response whole if the request it answers is becoming an event.</summary>
        private bool AwaitingWantsResponse()
        {
            lock (_gate) return _awaitingResponse is { Emit: true, Dropped: false };
        }

        private void Attention()
        {
            lock (_gate)
            {
                _attentionOutstanding = true;
                if (_awaitingResponse is not null) _awaitingResponse.Attention = true;
            }
        }

        private void ResponseComplete(ResponseListener response)
        {
            CapturedExchange? exchange;
            bool attentionExpected;
            lock (_gate)
            {
                exchange = _awaitingResponse;
                _awaitingResponse = null;

                attentionExpected = _attentionOutstanding;
                if ((response.DoneStatus & TdsConstants.DoneStatus.Attention) != 0) _attentionOutstanding = false;
            }

            if (exchange is null)
            {
                // Normally the acknowledgement of an attention that arrived after its request had
                // already been answered. Anything else means pairing has gone astray - not fatal (the
                // next request starts afresh), but worth knowing about.
                if (!attentionExpected)
                    Log.Debug("tee.unpaired_response", $"a {response.Bytes}-byte response arrived with no request waiting for it", _tap._session.Id);
                response.DiscardBuffer();
                return;
            }

            exchange.Completed = true;
            exchange.ResponseStartTimestamp = response.StartTimestamp;
            exchange.ResponseEndTimestamp = response.Timestamp;
            exchange.ResponseBytes = response.Bytes;
            exchange.ResponsePackets = response.Packets;
            exchange.ReadFinalDone(response.Tail, _tap._session.TdsVersion);
            if ((exchange.DoneStatus & TdsConstants.DoneStatus.Attention) != 0) exchange.Attention = true;

            if (exchange.Emit && !exchange.Dropped) response.HandBufferTo(exchange);
            else response.DiscardBuffer();

            _tap.Finish(exchange);
        }

        /// <summary>The client's half: requests, and attentions.</summary>
        private sealed class RequestListener(Conversation conversation) : IPacketListener
        {
            private CapturedExchange? _current;
            private bool _inAttention;

            public long Timestamp { get; set; }

            public CapturedExchange? TakeCurrent()
            {
                var current = _current;
                _current = null;
                return current;
            }

            public void OnPacketStart(in PacketHeader header)
            {
                if (header.Type == TdsConstants.PacketType.Attention)
                {
                    _inAttention = true;
                    conversation.Attention();
                    return;
                }

                _current ??= conversation._tap.BeginRequest(header, Timestamp, conversation._channel);
                _current.RequestPackets++;
            }

            public void OnPacketPayload(ReadOnlySpan<byte> payload)
            {
                if (_inAttention || _current is null) return;
                _current.RequestBytes += payload.Length;
                _current.Append(payload, conversation._tap._settings.MaxEventBytes, conversation._tap._pipeline.Budget);
            }

            public void OnPacketEnd(in PacketHeader header)
            {
                if (_inAttention)
                {
                    _inAttention = false;
                    return;
                }

                if (!header.IsEndOfMessage || _current is null) return;

                _current.RequestEndTimestamp = Timestamp;
                var finished = _current;
                _current = null;
                conversation.RequestComplete(finished);
            }
        }

        /// <summary>
        /// The target's half: where each response starts and ends, its size, its last 13 bytes (the
        /// final DONE), and - when it is small - the whole of it, for the worker to decode.
        /// </summary>
        private sealed class ResponseListener(Conversation conversation) : IPacketListener
        {
            private const int TailLength = 13;
            private readonly byte[] _tail = new byte[TailLength];
            private int _tailFill;
            private bool _inResponse;

            private byte[]? _buffer;
            private int _bufferLength, _bufferReserved;
            private bool _buffering;

            private CaptureBudget Budget => conversation._tap._pipeline.Budget;
            private int Limit => conversation._tap._settings.ResponseCaptureBytes;

            public long Timestamp { get; set; }
            public long StartTimestamp { get; private set; }
            public long Bytes { get; private set; }
            public int Packets { get; private set; }
            public ushort DoneStatus { get; private set; }
            public ReadOnlySpan<byte> Tail => _tail.AsSpan(0, _tailFill);

            public void OnPacketStart(in PacketHeader header)
            {
                if (!_inResponse)
                {
                    _inResponse = true;
                    StartTimestamp = Timestamp;
                    Bytes = 0;
                    Packets = 0;
                    _tailFill = 0;
                    _buffering = Limit > 0 && conversation.AwaitingWantsResponse();
                    _bufferLength = 0;
                }
                Packets++;
            }

            public void OnPacketPayload(ReadOnlySpan<byte> payload)
            {
                Bytes += payload.Length;
                KeepTail(payload);
                if (_buffering) Buffer(payload);
            }

            public void OnPacketEnd(in PacketHeader header)
            {
                if (!header.IsEndOfMessage) return;

                _inResponse = false;

                // The final DONE is the last 13 bytes, or the last 9 before TDS 7.2 (a 4-byte row count).
                int done = TdsConstants.DoneLength(conversation._tap._session.TdsVersion) + 1;
                int at = _tailFill - done;
                DoneStatus = at >= 0 && _tail[at] is TdsConstants.Token.Done or TdsConstants.Token.DoneProc or TdsConstants.Token.DoneInProc
                    ? (ushort)(_tail[at + 1] | (_tail[at + 2] << 8))
                    : (ushort)0;

                conversation.ResponseComplete(this);
            }

            public void HandBufferTo(CapturedExchange exchange)
            {
                if (_buffering && _buffer is not null)
                {
                    exchange.AdoptResponse(_buffer, _bufferLength, _bufferReserved);
                    _buffer = null;
                    _bufferReserved = 0;
                    _bufferLength = 0;
                }
                else
                {
                    DiscardBuffer();
                }
            }

            public void DiscardBuffer()
            {
                if (_buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                }
                Budget.ReleaseBytes(_bufferReserved);
                _bufferReserved = 0;
                _bufferLength = 0;
            }

            private void KeepTail(ReadOnlySpan<byte> payload)
            {
                if (payload.Length >= TailLength)
                {
                    payload[^TailLength..].CopyTo(_tail);
                    _tailFill = TailLength;
                    return;
                }

                int keep = Math.Min(_tailFill, TailLength - payload.Length);
                _tail.AsSpan(_tailFill - keep, keep).CopyTo(_tail);
                payload.CopyTo(_tail.AsSpan(keep));
                _tailFill = keep + payload.Length;
            }

            /// <summary>Keeps the response whole while it stays under the limit; one byte over and it is let go.</summary>
            private void Buffer(ReadOnlySpan<byte> payload)
            {
                int needed = _bufferLength + payload.Length;
                if (needed > Limit)
                {
                    _buffering = false;
                    DiscardBuffer();
                    return;
                }

                if (_buffer is null || needed > _bufferReserved)
                {
                    int size = Math.Min(Limit, Math.Max(needed, Math.Max(2048, _bufferReserved * 2)));
                    if (!Budget.TryReserveBytes(size - _bufferReserved))
                    {
                        _buffering = false;
                        DiscardBuffer();
                        return;
                    }

                    _bufferReserved = size;
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(size);
                    if (_buffer is not null)
                    {
                        _buffer.AsSpan(0, _bufferLength).CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(_buffer);
                    }
                    _buffer = bigger;
                }

                payload.CopyTo(_buffer.AsSpan(_bufferLength));
                _bufferLength += payload.Length;
            }
        }
    }
}
