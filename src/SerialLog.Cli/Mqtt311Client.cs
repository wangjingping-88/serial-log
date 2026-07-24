using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace SerialLog.Cli;

public sealed record Mqtt311Message(string Topic, byte[] Payload)
{
    public string Text => Encoding.UTF8.GetString(Payload);
}

public sealed class Mqtt311Client : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<Mqtt311Message> _messages =
        Channel.CreateUnbounded<Mqtt311Message>();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private ushort _packetId;

    public string ClientId { get; private set; } = string.Empty;

    public ChannelReader<Mqtt311Message> Messages => _messages.Reader;

    public async Task ConnectAsync(
        EcoLinkOtaMqttConfig config,
        CancellationToken cancellationToken)
    {
        if (_tcpClient is not null)
        {
            throw new InvalidOperationException("MQTT客户端已经连接。");
        }

        ValidateConfig(config);
        ClientId = BuildClientId(config.ClientIdPrefix);
        _tcpClient = new TcpClient();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectTimeoutSeconds));
        await _tcpClient.ConnectAsync(config.Host, config.Port, timeout.Token)
            .ConfigureAwait(false);
        _stream = _tcpClient.GetStream();

        await WritePacketAsync(
            Mqtt311PacketCodec.BuildConnect(
                ClientId,
                config.Username,
                config.Password,
                config.KeepAliveSeconds),
            timeout.Token).ConfigureAwait(false);
        var connAck = await Mqtt311PacketCodec.ReadPacketAsync(
            _stream,
            timeout.Token).ConfigureAwait(false);
        ValidateConnAck(connAck);

        var subscribeId = NextPacketId();
        await WritePacketAsync(
            Mqtt311PacketCodec.BuildSubscribe(
                subscribeId,
                config.UpTopic,
                config.Qos),
            timeout.Token).ConfigureAwait(false);
        await WaitForSubAckAsync(subscribeId, timeout.Token).ConfigureAwait(false);

        _receiveTask = ReceiveLoopAsync(_lifetime.Token);
        _keepAliveTask = KeepAliveLoopAsync(
            config.KeepAliveSeconds,
            _lifetime.Token);
    }

    public Task PublishAsync(
        string topic,
        string payload,
        int qos,
        CancellationToken cancellationToken)
    {
        if (qos != 0)
        {
            throw new NotSupportedException("当前自动测试客户端仅支持QoS 0发布。");
        }

        return WritePacketAsync(
            Mqtt311PacketCodec.BuildPublish(
                topic,
                Encoding.UTF8.GetBytes(payload)),
            cancellationToken);
    }

    private async Task WaitForSubAckAsync(
        ushort expectedPacketId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await Mqtt311PacketCodec.ReadPacketAsync(
                GetStream(),
                cancellationToken).ConfigureAwait(false);
            var packetType = packet.Header >> 4;
            if (packetType == 3)
            {
                await DispatchPublishAsync(packet, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (packetType != 9 || packet.Payload.Length < 3)
            {
                throw new IOException(
                    $"等待SUBACK时收到异常MQTT报文，type={packetType}。");
            }

            var packetId = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload);
            if (packetId != expectedPacketId || packet.Payload[2] == 0x80)
            {
                throw new IOException("MQTT主题订阅被拒绝或报文标识不匹配。");
            }

            return;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await Mqtt311PacketCodec.ReadPacketAsync(
                    GetStream(),
                    cancellationToken).ConfigureAwait(false);
                if ((packet.Header >> 4) == 3)
                {
                    await DispatchPublishAsync(packet, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _messages.Writer.TryComplete(ex);
            _lifetime.Cancel();
        }
    }

    private async Task DispatchPublishAsync(
        Mqtt311Packet packet,
        CancellationToken cancellationToken)
    {
        var message = Mqtt311PacketCodec.ParsePublish(packet);
        await _messages.Writer.WriteAsync(message, cancellationToken)
            .ConfigureAwait(false);

        var qos = (packet.Header >> 1) & 0x03;
        if (qos == 1)
        {
            var packetId = Mqtt311PacketCodec.ReadPublishPacketId(packet);
            await WritePacketAsync(
                Mqtt311PacketCodec.BuildPubAck(packetId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task KeepAliveLoopAsync(
        int keepAliveSeconds,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Max(5, keepAliveSeconds / 2)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                await WritePacketAsync(
                    [0xC0, 0x00],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WritePacketAsync(
        byte[] packet,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await GetStream().WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            await GetStream().FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private NetworkStream GetStream()
    {
        return _stream ?? throw new InvalidOperationException("MQTT客户端尚未连接。");
    }

    private ushort NextPacketId()
    {
        _packetId++;
        if (_packetId == 0)
        {
            _packetId = 1;
        }

        return _packetId;
    }

    private static void ValidateConfig(EcoLinkOtaMqttConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Host)
            || config.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(config.UpTopic)
            || config.Qos is < 0 or > 1
            || config.ConnectTimeoutSeconds <= 0
            || config.KeepAliveSeconds <= 0)
        {
            throw new InvalidOperationException("MQTT配置无效。");
        }
    }

    private static void ValidateConnAck(Mqtt311Packet packet)
    {
        if ((packet.Header >> 4) != 2 || packet.Payload.Length != 2)
        {
            throw new IOException("MQTT服务器返回了无效的CONNACK。");
        }

        if (packet.Payload[1] != 0)
        {
            throw new IOException(
                $"MQTT服务器拒绝连接，返回码={packet.Payload[1]}。");
        }
    }

    private static string BuildClientId(string prefix)
    {
        var normalized = string.IsNullOrWhiteSpace(prefix)
            ? "eco-ota"
            : new string(prefix.Trim().Take(8).ToArray());
        return $"{normalized}-{Guid.NewGuid():N}"[..21];
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _tcpClient?.Close();

        var tasks = new[] { _receiveTask, _keepAliveTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _messages.Writer.TryComplete();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}

public readonly record struct Mqtt311Packet(byte Header, byte[] Payload);

public static class Mqtt311PacketCodec
{
    public static byte[] BuildConnect(
        string clientId,
        string username,
        string password,
        int keepAliveSeconds)
    {
        using var body = new MemoryStream();
        WriteUtf8(body, "MQTT");
        body.WriteByte(0x04);

        var flags = 0x02;
        if (!string.IsNullOrEmpty(username))
        {
            flags |= 0x80;
        }

        if (!string.IsNullOrEmpty(password))
        {
            flags |= 0x40;
        }

        body.WriteByte((byte)flags);
        WriteUInt16(body, (ushort)Math.Clamp(keepAliveSeconds, 1, ushort.MaxValue));
        WriteUtf8(body, clientId);
        if (!string.IsNullOrEmpty(username))
        {
            WriteUtf8(body, username);
        }

        if (!string.IsNullOrEmpty(password))
        {
            WriteUtf8(body, password);
        }

        return AddFixedHeader(0x10, body.ToArray());
    }

    public static byte[] BuildSubscribe(ushort packetId, string topic, int qos)
    {
        using var body = new MemoryStream();
        WriteUInt16(body, packetId);
        WriteUtf8(body, topic);
        body.WriteByte((byte)qos);
        return AddFixedHeader(0x82, body.ToArray());
    }

    public static byte[] BuildPublish(string topic, byte[] payload)
    {
        using var body = new MemoryStream();
        WriteUtf8(body, topic);
        body.Write(payload);
        return AddFixedHeader(0x30, body.ToArray());
    }

    public static byte[] BuildPubAck(ushort packetId)
    {
        return [
            0x40,
            0x02,
            (byte)(packetId >> 8),
            (byte)packetId
        ];
    }

    public static Mqtt311Message ParsePublish(Mqtt311Packet packet)
    {
        if ((packet.Header >> 4) != 3 || packet.Payload.Length < 2)
        {
            throw new IOException("MQTT PUBLISH报文无效。");
        }

        var topicLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload);
        var offset = 2;
        if (topicLength == 0 || offset + topicLength > packet.Payload.Length)
        {
            throw new IOException("MQTT PUBLISH主题长度无效。");
        }

        var topic = Encoding.UTF8.GetString(
            packet.Payload,
            offset,
            topicLength);
        offset += topicLength;

        var qos = (packet.Header >> 1) & 0x03;
        if (qos > 0)
        {
            if (offset + 2 > packet.Payload.Length)
            {
                throw new IOException("MQTT PUBLISH报文缺少报文标识。");
            }

            offset += 2;
        }

        return new Mqtt311Message(topic, packet.Payload[offset..]);
    }

    public static ushort ReadPublishPacketId(Mqtt311Packet packet)
    {
        var topicLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload);
        var offset = 2 + topicLength;
        if (offset + 2 > packet.Payload.Length)
        {
            throw new IOException("MQTT PUBLISH报文缺少报文标识。");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(offset, 2));
    }

    public static async Task<Mqtt311Packet> ReadPacketAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var oneByte = new byte[1];
        await stream.ReadExactlyAsync(oneByte, cancellationToken).ConfigureAwait(false);
        var header = oneByte[0];

        var remainingLength = 0;
        var multiplier = 1;
        for (var index = 0; index < 4; index++)
        {
            await stream.ReadExactlyAsync(oneByte, cancellationToken).ConfigureAwait(false);
            var encoded = oneByte[0];
            remainingLength += (encoded & 0x7F) * multiplier;
            if ((encoded & 0x80) == 0)
            {
                if (remainingLength > 16 * 1024 * 1024)
                {
                    throw new IOException("MQTT报文超过自动测试客户端限制。");
                }

                var payload = new byte[remainingLength];
                if (payload.Length > 0)
                {
                    await stream.ReadExactlyAsync(payload, cancellationToken)
                        .ConfigureAwait(false);
                }

                return new Mqtt311Packet(header, payload);
            }

            multiplier *= 128;
        }

        throw new IOException("MQTT剩余长度字段无效。");
    }

    private static byte[] AddFixedHeader(byte header, byte[] body)
    {
        var remainingLength = EncodeRemainingLength(body.Length);
        var packet = new byte[1 + remainingLength.Length + body.Length];
        packet[0] = header;
        remainingLength.CopyTo(packet, 1);
        body.CopyTo(packet, 1 + remainingLength.Length);
        return packet;
    }

    private static byte[] EncodeRemainingLength(int length)
    {
        if (length < 0 || length > 268_435_455)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var result = new List<byte>(4);
        do
        {
            var digit = length % 128;
            length /= 128;
            if (length > 0)
            {
                digit |= 0x80;
            }

            result.Add((byte)digit);
        }
        while (length > 0);

        return result.ToArray();
    }

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        WriteUInt16(stream, (ushort)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
