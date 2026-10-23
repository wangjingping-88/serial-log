using System.Text;
using System.Threading.Channels;
using SerialLog.Core.Firmware;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

[CollectionDefinition("串口协议短超时", DisableParallelization = true)]
public sealed class SerialProtocolTimingCollection { }

// 本类刻意使用毫秒级超时测试重传，不与 WPF 渲染测试争用线程池。
[Collection("串口协议短超时")]
public class YmodemSenderTests
{
    private static readonly YmodemOptions Fast = new()
    {
        HandshakeTimeout = TimeSpan.FromMilliseconds(150),
        ResponseTimeout = TimeSpan.FromMilliseconds(30), HandshakeQuietPeriod = TimeSpan.FromMilliseconds(3), MaxRetries = 2
    };

    [Fact]
    public void Crc_and_packet_match_known_xmodem_vector()
    {
        Assert.Equal((ushort)0x31C3, YmodemSender.Crc16(Encoding.ASCII.GetBytes("123456789")));
        var packet = YmodemSender.Packet(255, new byte[128]);
        Assert.Equal(1, packet[0]); Assert.Equal(255, packet[1]); Assert.Equal(0, packet[2]);
        Assert.Equal(133, packet.Length);
        Assert.Equal(0, packet[^2]); Assert.Equal(0, packet[^1]);
        var header = YmodemSender.FileHeader("中文固件.bin", 123);
        Assert.StartsWith("firmware.bin\0" + "123\0", Encoding.ASCII.GetString(header, 3, 128));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfers_short_tail_retries_and_supports_both_eot_handshakes(bool directAck)
    {
        var transport = new Receiver { DirectEotAck = directAck, NakFirstData = true };
        var payload = Enumerable.Range(0, 1100).Select(i => (byte)i).ToArray();
        using var file = new MemoryStream(payload);
        var progress = new List<YmodemProgress>();
        await new YmodemSender(Fast).SendAsync(transport, file, "test.bin", new InlineProgress(progress.Add), default);
        Assert.Equal(payload, transport.Data.Take(payload.Length));
        Assert.All(transport.Data.Skip(payload.Length), b => Assert.Equal(0x1A, b));
        Assert.Equal(3, transport.DataPacketAttempts);
        Assert.Equal(1100, progress.Last().AcknowledgedBytes);
        Assert.Equal(directAck ? 1 : 2, transport.EotCount);
        Assert.True(transport.Finished);
    }

    [Fact]
    public async Task Packet_sequence_wraps_without_changing_file_contents()
    {
        var transport = new Receiver();
        using var file = new MemoryStream(new byte[257 * 1024]);
        await new YmodemSender(Fast).SendAsync(transport, file, "wrap.bin", null, default);
        Assert.Equal(new byte[] { 254, 255, 0, 1 }, transport.Sequences.TakeLast(4));
        Assert.Equal(file.Length, transport.Data.Count);
    }

    [Fact]
    public async Task Gateway_preserves_query_configuration_and_consumes_full_response_before_c()
    {
        var transport = new Receiver(gateway: true);
        transport.Configuration[16] = 0x43;
        using var file = new MemoryStream(new byte[1]);
        var profile = YmodemRuleProfiles.CreateUcchipExample(); profile.After.Clear();
        await new YmodemJobRunner(Fast).RunAsync(transport, file, "gw.bin", profile, null, default);
        Assert.Equal(2, transport.ConfigurationWritten![0]);
        Assert.Equal(transport.Configuration.Skip(1), transport.ConfigurationWritten.Skip(1));
        Assert.True(transport.Finished);
        var tail = await transport.ReadByteAsync(default);
        Assert.Equal((byte)'+', tail); // 最终 ACK 后的启动文本未被预取吞掉。
    }

    [Fact]
    public async Task Gateway_rejects_unknown_configuration_size_without_sending_set_command()
    {
        var transport = new Receiver(gateway: true) { Configuration = new byte[16] };
        using var file = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new YmodemJobRunner(Fast).RunAsync(transport, file, "gw.bin", YmodemRuleProfiles.CreateUcchipExample(), null, default));
        Assert.Null(transport.ConfigurationWritten);
        Assert.True(transport.CancelSent);
    }

    [Fact]
    public async Task Gateway_rejects_corrupt_response()
    {
        var transport = new Receiver(gateway: true) { CorruptQuery = true };
        using var file = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new YmodemJobRunner(Fast).RunAsync(transport, file, "gw.bin", YmodemRuleProfiles.CreateUcchipExample(), null, default));
        Assert.Null(transport.ConfigurationWritten);
    }

    [Fact]
    public async Task Ordinary_log_capital_c_does_not_start_transfer()
    {
        var transport = new Receiver(initial: Encoding.ASCII.GetBytes("CHECK STATE C not a handshake\r\n"));
        using var file = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<TimeoutException>(() => new YmodemSender(Fast).SendAsync(transport, file, "x.bin", null, default));
        Assert.Equal(0, transport.HeaderCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_or_peer_cancel_stops_transfer(bool peerCancel)
    {
        var transport = new Receiver { WithholdDataAck = !peerCancel, PeerCancelData = peerCancel };
        using var file = new MemoryStream(new byte[1]);
        // 本例验证数据包应答超时/取消；不要让并行 WPF 渲染先耗尽首次握手的 150 ms。
        var options = new YmodemOptions { HandshakeTimeout = TimeSpan.FromSeconds(2),
            ResponseTimeout = Fast.ResponseTimeout, HandshakeQuietPeriod = Fast.HandshakeQuietPeriod, MaxRetries = Fast.MaxRetries };
        await Assert.ThrowsAnyAsync<Exception>(() => new YmodemSender(options).SendAsync(transport, file, "x.bin", null, default));
        Assert.False(transport.Finished);
        Assert.True(transport.CancelSent);
        if (!peerCancel) Assert.Equal(3, transport.DataPacketAttempts);
    }

    [Fact]
    public async Task Cancellation_interrupts_wait_and_sends_can()
    {
        var transport = new Receiver(initial: []);
        using var file = new MemoryStream(new byte[1]);
        using var cancellation = new CancellationTokenSource(15);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new YmodemSender(Fast).SendAsync(transport, file, "x.bin", null, cancellation.Token));
        Assert.True(transport.CancelSent);
    }

    private sealed class InlineProgress(Action<YmodemProgress> report) : IProgress<YmodemProgress>
    { public void Report(YmodemProgress value) => report(value); }

    internal sealed class Receiver : IBinarySerialTransport
    {
        private readonly Channel<byte> _rx = Channel.CreateUnbounded<byte>();
        public byte[] Configuration { get; set; } = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        public byte[]? ConfigurationWritten;
        public bool CorruptQuery, DirectEotAck, NakFirstData, WithholdDataAck, PeerCancelData;
        public int DataPacketAttempts, HeaderCount, EotCount;
        public bool Finished, CancelSent;
        public List<byte> Data { get; } = [];
        public List<byte> Sequences { get; } = [];
        public Receiver(bool gateway = false, byte[]? initial = null)
        { if (!gateway) Feed(initial ?? new byte[] { 0x43 }); }
        private void Feed(byte[] bytes) { foreach (var b in bytes) _rx.Writer.TryWrite(b); }
        public ValueTask<byte> ReadByteAsync(CancellationToken ct) => _rx.Reader.ReadAsync(ct);
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        { var b = await ReadByteAsync(cancellationToken); buffer.Span[0] = b; return 1; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = memory.ToArray();
            if (bytes[0] == 0x18) { CancelSent = true; return ValueTask.CompletedTask; }
            if (bytes[0] == 0xAA)
            {
                if (bytes[6] == 55)
                {
                    Feed(Encoding.ASCII.GetBytes("DEBUG C: UBOOT query\r\n"));
                    var frame = GatewayFrame(55, Configuration);
                    if (CorruptQuery) frame[^2] ^= 1;
                    Feed(frame);
                }
                else
                {
                    ConfigurationWritten = bytes.AsSpan(9, 32).ToArray();
                    var response = new byte[64]; Encoding.ASCII.GetBytes("suc").CopyTo(response, 0);
                    Feed(GatewayFrame(56, response)); Feed(new byte[] { 0x43 });
                }
                return ValueTask.CompletedTask;
            }
            if (bytes[0] == 4)
            {
                EotCount++;
                Feed(DirectEotAck || EotCount > 1 ? new byte[] { 6, 0x43 } : new byte[] { 0x15 });
                return ValueTask.CompletedTask;
            }
            var length = bytes[0] == 1 ? 128 : 1024;
            Assert.Equal((byte)~bytes[1], bytes[2]);
            var crc = YmodemSender.Crc16(bytes.AsSpan(3, length));
            Assert.Equal((byte)(crc >> 8), bytes[^2]); Assert.Equal((byte)crc, bytes[^1]);
            if (HeaderCount == 0)
            { HeaderCount++; Feed(new byte[] { 6, 0x43 }); }
            else if (bytes[0] == 1 && bytes[1] == 0 && bytes[3] == 0)
            { Finished = true; Feed(new byte[] { 6 }); Feed(Encoding.ASCII.GetBytes("+SYSTEM:START\r\n")); }
            else
            {
                DataPacketAttempts++;
                if (PeerCancelData) Feed(new byte[] { 0x18, 0x18 });
                else if (WithholdDataAck) { }
                else if (NakFirstData && DataPacketAttempts == 1) Feed(new byte[] { 0x15 });
                else { Data.AddRange(bytes.AsSpan(3, length).ToArray()); Sequences.Add(bytes[1]); Feed(new byte[] { 6 }); }
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static byte[] GatewayFrame(uint control, byte[] payload) => SerialFrameCodec.Encode(
            YmodemRuleProfiles.CreateUcchipExample().Templates[0], payload, [new() { Field = "control", Value = control }]);
    }
}
