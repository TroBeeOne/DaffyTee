using System.Text;
using DaffyTee.Capture;
using DaffyTee.Sinks;
using DaffyTee.Tds;

namespace DaffyTee.Tests;

/// <summary>
/// The tap and the pipeline together, fed synthetic TDS: pairing, attention, MARS, the budget and
/// gap markers. No sockets - the tap is driven exactly as the relay drives it.
/// </summary>
public class CaptureTests
{
    private static readonly SessionSnapshot Session = new(
        7, Guid.Empty, "bob", "svc_app", "sql:1433", 51, "Sales", "WS1", "App", "10.0.0.1:5000", "", "", "", ColumnEncryption: false);

    private static byte[] Packet(byte type, byte status, byte[] payload)
    {
        int length = payload.Length + 8;
        return [type, status, (byte)(length >> 8), (byte)length, 0, 51, 1, 0, .. payload];
    }

    /// <summary>ALL_HEADERS with a transaction descriptor, then the SQL text.</summary>
    private static byte[] Batch(string sql, ulong transaction = 0)
    {
        var w = new TdsWriter();
        w.WriteUInt32(22); w.WriteUInt32(18); w.WriteUInt16(2); w.WriteUInt64(transaction); w.WriteUInt32(1);
        w.WriteBytes(Encoding.Unicode.GetBytes(sql));
        return Packet(TdsConstants.PacketType.SqlBatch, 0x01, w.ToArray());
    }

    private static byte[] Response(ushort status, ulong rows)
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.Done); w.WriteUInt16(status); w.WriteUInt16(0xC1); w.WriteUInt64(rows);
        return Packet(TdsConstants.PacketType.TabularResult, 0x01, w.ToArray());
    }

    private static readonly byte[] Attention = Packet(TdsConstants.PacketType.Attention, 0x01, []);

    private static async Task<List<AuditEvent>> WaitForAsync(CapturePipeline pipeline, int requests)
    {
        for (int i = 0; i < 200; i++)
        {
            var events = pipeline.Recent.Tail(1000).Where(e => e.Type.StartsWith("request.")).ToList();
            if (events.Count >= requests) return events;
            await Task.Delay(10);
        }
        throw new TimeoutException($"expected {requests} request event(s)");
    }

    private static CapturePipeline NewPipeline(IEventSink? sink = null, int maxEvents = 1000) =>
        new(new TeeSettings { Enabled = true, Instance = "test", MaxQueueEvents = maxEvents }, sink, null, () => 1, TimeSpan.Zero);

    /// <summary>Feeds the bytes in pieces of the given size, as a relay reading a socket might see them.</summary>
    private static void Feed(Action<ReadOnlySpan<byte>, long> into, byte[] bytes, int chunk)
    {
        for (int i = 0; i < bytes.Length; i += chunk)
            into(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)), System.Diagnostics.Stopwatch.GetTimestamp());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task PairsEachRequestWithItsResponse(int chunk)
    {
        await using var pipeline = NewPipeline();
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: false);

        Feed(tap.OnClientBytes, Batch("SELECT 1", transaction: 0x0000000100000004), chunk);
        Feed(tap.OnServerBytes, Response(TdsConstants.DoneStatus.Count, 1), chunk);
        Feed(tap.OnClientBytes, Batch("UPDATE t SET x = 1"), chunk);
        Feed(tap.OnServerBytes, Response(TdsConstants.DoneStatus.Count, 42), chunk);

        var events = await WaitForAsync(pipeline, 2);
        Assert.Equal("SELECT 1", events[0].Request!.Sql);
        Assert.Equal(0x0000000100000004UL, events[0].Request!.TransactionDescriptor);
        Assert.Equal(1UL, events[0].Response!.RowCount);
        Assert.Equal("ok", events[0].Response!.Status);
        Assert.Equal("UPDATE t SET x = 1", events[1].Request!.Sql);
        Assert.Equal(42UL, events[1].Response!.RowCount);
        Assert.Equal([1L, 2L], events.Select(e => e.Seq!.Value));
    }

    [Fact]
    public async Task AnAttentionThatCutsAResponseShortMarksItCancelled()
    {
        await using var pipeline = NewPipeline();
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: false);

        tap.OnClientBytes(Batch("WAITFOR DELAY '00:01'"), 0);
        tap.OnClientBytes(Attention, 0);
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Attention, 0), 0);
        tap.OnClientBytes(Batch("SELECT 2"), 0);
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 1), 0);

        var events = await WaitForAsync(pipeline, 2);
        Assert.Equal("cancelled", events[0].Response!.Status);
        Assert.Equal("SELECT 2", events[1].Request!.Sql);
        Assert.Equal("ok", events[1].Response!.Status);
    }

    [Fact]
    public async Task AnAttentionAckArrivingAfterTheResponseIsNotPairedWithTheNextRequest()
    {
        await using var pipeline = NewPipeline();
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: false);

        tap.OnClientBytes(Batch("SELECT 1"), 0);
        tap.OnClientBytes(Attention, 0);
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 1), 0);        // finished before the cancel landed
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Attention, 0), 0);    // the acknowledgement, on its own
        tap.OnClientBytes(Batch("SELECT 3"), 0);
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 3), 0);

        var events = await WaitForAsync(pipeline, 2);
        Assert.Equal(2, events.Count);
        Assert.Equal(3UL, events[1].Response!.RowCount);
        Assert.Equal("ok", events[1].Response!.Status);
    }

    [Fact]
    public async Task ASessionThatEndsMidRequestReportsItIncomplete()
    {
        await using var pipeline = NewPipeline();
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: false);

        tap.OnClientBytes(Batch("SELECT slow"), 0);
        tap.OnClosed("client disconnected");

        var events = await WaitForAsync(pipeline, 1);
        Assert.Equal("incomplete", events[0].Response!.Status);
        Assert.Null(events[0].DurationMs);
    }

    [Fact]
    public async Task MarsChannelsArePairedIndependently()
    {
        await using var pipeline = NewPipeline();
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: true);

        static byte[] Smp(ushort channel, byte flags, byte[] payload)
        {
            int length = payload.Length + 16;
            return [0x53, flags, (byte)channel, (byte)(channel >> 8), (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24),
                    0, 0, 0, 0, 4, 0, 0, 0, .. payload];
        }

        byte[] client = [.. Smp(0, SmpReader.Syn, []), .. Smp(0, SmpReader.Data, Batch("SELECT a")),
                         .. Smp(1, SmpReader.Syn, []), .. Smp(1, SmpReader.Data, Batch("SELECT b"))];
        byte[] server = [.. Smp(1, SmpReader.Data, Response(TdsConstants.DoneStatus.Count, 2)),
                         .. Smp(0, SmpReader.Data, Response(TdsConstants.DoneStatus.Count, 1))];

        Feed(tap.OnClientBytes, client, 7);
        Feed(tap.OnServerBytes, server, 7);

        var events = await WaitForAsync(pipeline, 2);
        var bySql = events.ToDictionary(e => e.Request!.Sql!);
        Assert.Equal(1UL, bySql["SELECT a"].Response!.RowCount);
        Assert.Equal((ushort)0, bySql["SELECT a"].Request!.MarsChannel);
        Assert.Equal(2UL, bySql["SELECT b"].Response!.RowCount);
        Assert.Equal((ushort)1, bySql["SELECT b"].Request!.MarsChannel);
    }

    [Fact]
    public async Task WithTheTeeOffNothingIsCapturedButPairingStaysInStep()
    {
        await using var pipeline = NewPipeline();
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: false);

        pipeline.Settings.Enabled = false;
        tap.OnClientBytes(Batch("SELECT hidden"), 0);
        pipeline.Settings.Enabled = true;                  // switched on mid-request: this one stays uncaptured
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 1), 0);
        tap.OnClientBytes(Batch("SELECT seen"), 0);
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 5), 0);

        var events = await WaitForAsync(pipeline, 1);
        Assert.Equal("SELECT seen", Assert.Single(events).Request!.Sql);
        Assert.Equal(5UL, events[0].Response!.RowCount);
    }

    /// <summary>A sink that is down until told otherwise, and records what it was given once up.</summary>
    private sealed class SwitchableSink : IEventSink
    {
        public volatile bool Available;
        public List<string> Types { get; } = [];
        public List<byte[]> Bodies { get; } = [];
        public string Description => "test sink";
        public SinkState State => Available ? SinkState.Healthy : SinkState.Unavailable;
        public string? LastError => Available ? null : "down";

        public Task<int> PublishAsync(IReadOnlyList<OutboundEvent> batch, CancellationToken cancellationToken)
        {
            if (!Available) throw new SinkUnavailableException("down");
            lock (Types)
            {
                Types.AddRange(batch.Select(b => b.RoutingKey));
                Bodies.AddRange(batch.Select(b => b.Body));
            }
            return Task.FromResult(0);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static bool Contains(SwitchableSink sink, string type)
    {
        lock (sink.Types) return sink.Types.Contains(type);
    }

    [Fact]
    public async Task WhenTheSinkIsDownTheNewestAreDroppedAndAGapMarksTheSpot()
    {
        var sink = new SwitchableSink();
        await using var pipeline = NewPipeline(sink, maxEvents: 5);
        pipeline.Start();
        var tap = new SessionTap(pipeline, Session, mars: false);

        for (int i = 0; i < 20; i++)
        {
            tap.OnClientBytes(Batch($"SELECT {i}"), 0);
            tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 1), 0);
        }

        Assert.True(pipeline.Budget.DroppedTotal > 0);
        long dropped = pipeline.Budget.DroppedTotal;

        // Once the sink is back the held batch goes out, and the gap is reported straight after it
        // without waiting for another event to carry it.
        sink.Available = true;
        for (int i = 0; i < 500 && !Contains(sink, "proxy.gap"); i++) await Task.Delay(10);

        tap.OnClientBytes(Batch("SELECT after"), 0);
        tap.OnServerBytes(Response(TdsConstants.DoneStatus.Count, 1), 0);
        for (int i = 0; i < 500 && sink.Types.Count(t => t == "request.batch") < 20 - dropped + 1; i++) await Task.Delay(10);

        List<string> types;
        lock (sink.Types) types = [.. sink.Types];

        // The oldest were kept and delivered first; the gap sits after them and before what came next.
        int gap = types.IndexOf("proxy.gap");
        Assert.True(gap > 0, string.Join(",", types));
        Assert.Contains("request.batch", types.Take(gap));
        Assert.Equal("request.batch", types[gap + 1]);

        string gapBody;
        lock (sink.Types) gapBody = Encoding.UTF8.GetString(sink.Bodies[gap]);
        Assert.Contains($"\"dropped\":{dropped}", gapBody);
        Assert.Contains("queue_full", gapBody);
        Assert.Equal(20 - dropped, types.Take(gap).Count(t => t == "request.batch"));
    }
}

public class SmpReaderTests
{
    private sealed class Recorder : ISmpListener
    {
        public List<(ushort Channel, byte Flags)> Frames { get; } = [];
        public Dictionary<ushort, List<byte>> Data { get; } = [];
        public void OnFrame(ushort channel, byte flags) => Frames.Add((channel, flags));
        public void OnData(ushort channel, ReadOnlySpan<byte> payload)
        {
            if (!Data.TryGetValue(channel, out var list)) Data[channel] = list = [];
            list.AddRange(payload.ToArray());
        }
    }

    [Fact]
    public void SplitsChannelsHoweverTheBytesArrive()
    {
        static byte[] Frame(ushort channel, byte flags, byte[] payload)
        {
            int length = payload.Length + 16;
            return [0x53, flags, (byte)channel, 0, (byte)length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. payload];
        }

        byte[] stream = [.. Frame(0, SmpReader.Syn, []), .. Frame(0, SmpReader.Data, [1, 2, 3]), .. Frame(3, SmpReader.Data, [9]),
                         .. Frame(0, SmpReader.Data, [4]), .. Frame(3, SmpReader.Fin, [])];

        for (int chunk = 1; chunk <= stream.Length; chunk++)
        {
            var reader = new SmpReader();
            var recorder = new Recorder();
            for (int i = 0; i < stream.Length; i += chunk)
                reader.Feed(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), recorder);

            Assert.False(reader.Desynced);
            Assert.Equal([1, 2, 3, 4], recorder.Data[0]);
            Assert.Equal([9], recorder.Data[3]);
            Assert.Equal(5, recorder.Frames.Count);
        }
    }
}

public class CaptureBudgetTests
{
    [Fact]
    public void ReservationsAreBoundedAndGapsAccumulateUntilTaken()
    {
        var budget = new CaptureBudget(maxBytes: 100, maxEvents: 2);
        Assert.True(budget.TryReserveBytes(60));
        Assert.False(budget.TryReserveBytes(60));
        Assert.Equal(60, budget.Bytes);
        budget.ReleaseBytes(60);

        Assert.True(budget.TryReserveEvent());
        Assert.True(budget.TryReserveEvent());
        Assert.False(budget.TryReserveEvent());

        Assert.Null(budget.TakeGap());
        budget.RecordDrop(DropReason.QueueFull, 10);
        budget.RecordDrop(DropReason.QueueFull, 20);
        budget.RecordDrop(DropReason.Purged, 5, events: 3);

        var gap = budget.TakeGap();
        Assert.NotNull(gap);
        Assert.Equal(5, gap.Events);
        Assert.Equal(35, gap.Bytes);
        Assert.Equal(2, gap.Reasons["queue_full"]);
        Assert.Equal(3, gap.Reasons["purged"]);
        Assert.Null(budget.TakeGap());
        Assert.Equal(5, budget.DroppedTotal);
    }
}
