using System.Diagnostics;
using System.Globalization;
using System.Text;
using SerialLog.Core.Serial;

namespace SerialLog.Core.Firmware;

[System.Text.Json.Serialization.JsonConverter(typeof(YmodemModeConverter))]
public enum YmodemMode { Custom, Standard }
public sealed class YmodemModeConverter : System.Text.Json.Serialization.JsonConverter<YmodemMode>
{
    public override YmodemMode Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var number) && number is 0 or 1) return (YmodemMode)number;
        if (reader.TokenType == System.Text.Json.JsonTokenType.String) return reader.GetString() switch
        { "Gateway" or "Custom" => YmodemMode.Custom, "Standard" => YmodemMode.Standard, _ => throw new System.Text.Json.JsonException("无效 Ymodem 模式。") };
        throw new System.Text.Json.JsonException("无效 Ymodem 模式。");
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, YmodemMode value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
public sealed record YmodemProgress(string Stage, long AcknowledgedBytes, long TotalBytes, double BytesPerSecond, TimeSpan Elapsed);
public sealed class YmodemOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxRetries { get; init; } = 10;
    public TimeSpan HandshakeQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed class YmodemSender(YmodemOptions? options = null)
{
    private readonly YmodemOptions _options = options ?? new();
    private const byte Ack = 6, Nak = 0x15, Can = 0x18, Crc = 0x43, Eot = 4;

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static byte[] Packet(byte sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is not (128 or 1024)) throw new ArgumentException("Ymodem 包必须为 128 或 1024 字节。");
        var packet = new byte[payload.Length + 5];
        packet[0] = payload.Length == 128 ? (byte)1 : (byte)2;
        packet[1] = sequence; packet[2] = (byte)~sequence;
        payload.CopyTo(packet.AsSpan(3));
        var crc = Crc16(payload);
        packet[^2] = (byte)(crc >> 8); packet[^1] = (byte)crc;
        return packet;
    }

    public static byte[] FileHeader(string fileName, long length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var name = Path.GetFileName(fileName);
        var size = length.ToString(CultureInfo.InvariantCulture);
        if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7E) || name.Length + size.Length + 2 > 128)
            name = "firmware.bin";
        var payload = new byte[128];
        Encoding.ASCII.GetBytes(name + "\0" + size + "\0").CopyTo(payload, 0);
        return Packet(0, payload);
    }

    public async Task SendAsync(IBinarySerialTransport transport, Stream file, string fileName,
        IProgress<YmodemProgress>? progress, CancellationToken cancellationToken)
    {
        if (!file.CanRead || !file.CanSeek || file.Length <= 0) throw new InvalidDataException("固件文件为空或不可读。");
        file.Position = 0;
        long acknowledged = 0;
        var timer = Stopwatch.StartNew();
        void Report(string stage) => progress?.Report(new(stage, acknowledged, file.Length,
            acknowledged / Math.Max(timer.Elapsed.TotalSeconds, 0.001), timer.Elapsed));
        var one = new byte[1];
        async Task<byte> WaitControl(TimeSpan timeout)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var previousCan = false;
            try
            {
                while (true)
                {
                    if (await transport.ReadAsync(one, deadline.Token).ConfigureAwait(false) != 1)
                        throw new EndOfStreamException("串口接收中断。");
                    var b = one[0];
                    if (b == Can && previousCan) throw new IOException("设备取消了 Ymodem 传输。");
                    previousCan = b == Can;
                    if (b is Ack or Nak or Crc) return b;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("等待 Ymodem 应答超时。"); }
        }
        async Task WaitC(TimeSpan timeout)
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                var remaining = timeout - wait.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("等待 Bootloader C 握手超时。");
                if (await WaitControl(remaining).ConfigureAwait(false) == Crc) return;
            }
        }
        async Task WaitBootstrapC()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.HandshakeTimeout);
            var lineStart = true;
            var previousCan = false;
            try
            {
                while (true)
                {
                    if (await transport.ReadAsync(one, deadline.Token).ConfigureAwait(false) != 1) throw new EndOfStreamException();
                    var b = one[0];
                    if (b == Can && previousCan) throw new IOException("设备取消了 Ymodem 传输。");
                    previousCan = b == Can;
                    if (b == Crc && lineStart)
                    {
                        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        quiet.CancelAfter(_options.HandshakeQuietPeriod);
                        try
                        {
                            if (await transport.ReadAsync(one, quiet.Token).ConfigureAwait(false) != 1) throw new EndOfStreamException();
                            b = one[0];
                            if (b is Crc or 10 or 13) return;
                        }
                        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { return; }
                    }
                    lineStart = b is 10 or 13;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("等待 Bootloader C 握手超时。"); }
        }
        async Task<byte> SendUntilAck(byte[] packet, bool allowC = false)
        {
            for (var retry = 0; retry <= _options.MaxRetries; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retry > 0) Report($"重传第 {retry} 次");
                await transport.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                try
                {
                    var responseWait = Stopwatch.StartNew();
                    while (true)
                    {
                        var remaining = _options.ResponseTimeout - responseWait.Elapsed;
                        if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                        var response = await WaitControl(remaining).ConfigureAwait(false);
                        if (response == Ack || allowC && response == Crc) return response;
                        if (response == Nak) break;
                        // ACK 等待期间的重复 C 不确认数据包。
                    }
                }
                catch (TimeoutException) when (retry < _options.MaxRetries) { }
            }
            throw new IOException("Ymodem 重传次数已达上限。");
        }

        try
        {
            Report("等待 Bootloader C 握手");
            await WaitBootstrapC().ConfigureAwait(false);
            Report("发送文件头");
            await SendUntilAck(FileHeader(fileName, file.Length)).ConfigureAwait(false);
            await WaitC(_options.ResponseTimeout).ConfigureAwait(false);
            byte sequence = 1;
            var data = new byte[1024];
            while (acknowledged < file.Length)
            {
                Array.Fill(data, (byte)0x1A);
                var actual = (int)Math.Min(data.Length, file.Length - acknowledged);
                await file.ReadExactlyAsync(data.AsMemory(0, actual), cancellationToken).ConfigureAwait(false);
                Report($"传输数据包 {sequence}");
                await SendUntilAck(Packet(sequence, data)).ConfigureAwait(false);
                acknowledged += actual;
                sequence = unchecked((byte)(sequence + 1));
                Report("传输固件");
            }
            Report("结束传输，等待最终确认");
            // 兼容标准双 EOT 和接收方直接 ACK；第一次 NAK 后继续发送 EOT。
            await SendUntilAck(new byte[] { Eot }).ConfigureAwait(false);
            await WaitC(_options.ResponseTimeout).ConfigureAwait(false);
            await SendUntilAck(Packet(0, new byte[128])).ConfigureAwait(false);
            Report("文件传输完成");
        }
        catch
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await transport.WriteAsync(new byte[] { Can, Can, Can, Can }, cancel.Token).ConfigureAwait(false); }
            catch { /* 断开的驱动无法再发送取消字节。 */ }
            throw;
        }
    }
}
