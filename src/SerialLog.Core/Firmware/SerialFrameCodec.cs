using SerialLog.Core.Serial;

namespace SerialLog.Core.Firmware;

public sealed record SerialFrameResponse(byte[] Payload, IReadOnlyDictionary<string, byte[]> Fields,
    IReadOnlyDictionary<string, uint> Integers);

/// <summary>不预读响应尾部，保证同一接收流中的后续 Ymodem 握手不被吞掉。</summary>
public static class SerialFrameCodec
{
    public static byte[] Encode(SerialFrameTemplate template, ReadOnlySpan<byte> payload, IReadOnlyList<FrameIntegerValue>? values = null)
    {
        YmodemRuleValidator.ValidateTemplate(template);
        var bytes = new List<byte>();
        var ranges = new Dictionary<string, (int Offset, int Count)>(StringComparer.Ordinal);
        var fixedPayload = template.Fields.Single(f => f.Kind == FrameFieldKind.Payload);
        if (fixedPayload.LengthField.Length == 0 && payload.Length != fixedPayload.FixedLength)
            throw new InvalidDataException("发送载荷与模板定长不符。");
        foreach (var field in template.Fields)
        {
            byte[] data = field.Kind switch
            {
                FrameFieldKind.Fixed => YmodemRuleValidator.ParseHex(field.Hex),
                FrameFieldKind.Integer => EncodeInteger(values?.SingleOrDefault(v => v.Field == field.Name)?.Value ?? field.Value, field.Width, field.Endian),
                FrameFieldKind.PayloadLength => EncodeInteger(checked((uint)payload.Length), field.Width, field.Endian),
                FrameFieldKind.Payload => payload.ToArray(),
                FrameFieldKind.Checksum => Checksum(bytes, ranges, field),
                _ => throw new InvalidDataException("未知帧字段类型。")
            };
            if (bytes.Count + data.Length > YmodemRuleProfiles.MaxBufferBytes) throw new InvalidDataException("帧长度超过 64 KiB。");
            ranges[field.Name] = (bytes.Count, data.Length); bytes.AddRange(data);
        }
        return bytes.ToArray();
    }

    public static async Task<SerialFrameResponse> ReadAsync(IBinarySerialTransport transport, SerialFrameTemplate template,
        IReadOnlyList<FrameIntegerValue> matches, CancellationToken cancellationToken)
    {
        YmodemRuleValidator.ValidateTemplate(template);
        var first = template.Fields[0];
        var prefix = first.Kind == FrameFieldKind.Fixed ? YmodemRuleValidator.ParseHex(first.Hex) : [];
        var one = new byte[1];
        async Task<byte> ReadByte()
        {
            if (await transport.ReadAsync(one, cancellationToken).ConfigureAwait(false) != 1) throw new EndOfStreamException("串口接收中断。");
            return one[0];
        }
        async Task<byte[]> ReadBytes(int count)
        {
            var data = new byte[count];
            for (var i = 0; i < count; i++) data[i] = await ReadByte().ConfigureAwait(false);
            return data;
        }
        var prefixTable = PrefixTable(prefix);
        while (true)
        {
            if (prefix.Length > 0)
            {
                var matched = 0;
                while (matched < prefix.Length)
                {
                    var b = await ReadByte().ConfigureAwait(false);
                    while (matched > 0 && b != prefix[matched]) matched = prefixTable[matched - 1];
                    if (b == prefix[matched]) matched++;
                }
            }
            var bytes = new List<byte>();
            var fields = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var integers = new Dictionary<string, uint>(StringComparer.Ordinal);
            var ranges = new Dictionary<string, (int Offset, int Count)>(StringComparer.Ordinal);
            byte[] payload = [];
            for (var i = 0; i < template.Fields.Count; i++)
            {
                var field = template.Fields[i];
                var count = field.Kind switch
                {
                    FrameFieldKind.Fixed => YmodemRuleValidator.ParseHex(field.Hex).Length,
                    FrameFieldKind.Payload => field.LengthField.Length > 0 ? (integers[field.LengthField] <= YmodemRuleProfiles.MaxBufferBytes
                        ? (int)integers[field.LengthField] : throw new InvalidDataException("响应载荷长度超过 64 KiB。")) : field.FixedLength,
                    FrameFieldKind.Checksum => field.Checksum == FrameChecksum.Crc16Xmodem ? 2 : 1,
                    _ => field.Width
                };
                if (count < 0 || count > YmodemRuleProfiles.MaxBufferBytes - bytes.Count) throw new InvalidDataException("响应帧超过 64 KiB。");
                var data = i == 0 && prefix.Length > 0 ? prefix.ToArray() : await ReadBytes(count).ConfigureAwait(false);
                if (field.Kind == FrameFieldKind.Fixed && !data.AsSpan().SequenceEqual(YmodemRuleValidator.ParseHex(field.Hex)))
                    throw new InvalidDataException($"响应固定字段“{field.Name}”不符。");
                if (field.Kind == FrameFieldKind.Checksum && !data.AsSpan().SequenceEqual(Checksum(bytes, ranges, field)))
                    throw new InvalidDataException($"响应校验字段“{field.Name}”错误。");
                if (field.Kind is FrameFieldKind.Integer or FrameFieldKind.PayloadLength) integers[field.Name] = DecodeInteger(data, field.Endian);
                if (field.Kind == FrameFieldKind.Payload) payload = data;
                ranges[field.Name] = (bytes.Count, data.Length); fields[field.Name] = data; bytes.AddRange(data);
            }
            if (matches.All(m => integers.TryGetValue(m.Field, out var value) && value == m.Value)) return new(payload, fields, integers);
        }
    }

    internal static int[] PrefixTable(byte[] bytes)
    {
        var result = new int[bytes.Length];
        for (var i = 1; i < bytes.Length; i++)
        {
            var matched = result[i - 1];
            while (matched > 0 && bytes[i] != bytes[matched]) matched = result[matched - 1];
            if (bytes[i] == bytes[matched]) matched++;
            result[i] = matched;
        }
        return result;
    }

    private static byte[] EncodeInteger(uint value, int width, FrameEndian endian)
    {
        if (width < 4 && value >= 1UL << (8 * width)) throw new InvalidDataException("整数或载荷长度超出字段宽度。");
        var data = new byte[width];
        for (var i = 0; i < width; i++) data[endian == FrameEndian.Little ? i : width - i - 1] = (byte)(value >> (8 * i));
        return data;
    }
    private static uint DecodeInteger(byte[] data, FrameEndian endian)
    {
        uint value = 0;
        for (var i = 0; i < data.Length; i++) value |= (uint)data[endian == FrameEndian.Little ? i : data.Length - i - 1] << (8 * i);
        return value;
    }
    private static byte[] Checksum(List<byte> bytes, Dictionary<string, (int Offset, int Count)> ranges, SerialFrameField field)
    {
        var from = ranges[field.ChecksumFrom]; var to = ranges[field.ChecksumTo];
        var data = bytes.GetRange(from.Offset, to.Offset + to.Count - from.Offset).ToArray();
        uint value = field.Checksum switch
        {
            FrameChecksum.Sum8 => (byte)data.Sum(b => (int)b),
            FrameChecksum.Xor8 => data.Aggregate((byte)0, (a, b) => (byte)(a ^ b)),
            FrameChecksum.Crc16Xmodem => YmodemSender.Crc16(data),
            _ => throw new InvalidDataException("不支持的校验类型。")
        };
        return EncodeInteger(value, field.Checksum == FrameChecksum.Crc16Xmodem ? 2 : 1, field.Endian);
    }
}
