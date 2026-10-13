using System.Text;
using System.Threading.Channels;
using SerialLog.Core.Firmware;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public class YmodemRulesTests
{
    [Fact]
    public void Example_is_editable_generic_data_and_import_remaps_ids_without_mutating_source()
    {
        var example = YmodemRuleProfiles.CreateUcchipExample();
        YmodemRuleValidator.Validate(example);
        Assert.Equal("UCCHIP-小网关", example.Name); Assert.Equal(460800, example.RequiredBaudRate);
        Assert.Equal(5, example.Before.Count); Assert.Single(example.After);
        var copy = YmodemRuleProfiles.Import(YmodemRuleProfiles.Export(example));
        Assert.NotEqual(example.Id, copy.Id); Assert.NotEqual(example.Templates[0].Id, copy.Templates[0].Id);
        Assert.Equal(copy.Templates[0].Id, copy.Before[0].Send.TemplateId);
        Assert.Equal(copy.Templates[0].Id, copy.Before[3].Receive.TemplateId);
        copy.Before[2].ReplacementHex = "03";
        Assert.Equal("02", example.Before[2].ReplacementHex);
    }

    [Theory]
    [InlineData(FrameChecksum.Sum8, FrameEndian.Little)]
    [InlineData(FrameChecksum.Xor8, FrameEndian.Big)]
    [InlineData(FrameChecksum.Crc16Xmodem, FrameEndian.Little)]
    [InlineData(FrameChecksum.Crc16Xmodem, FrameEndian.Big)]
    public async Task Configurable_frame_recomputes_length_checksum_and_keeps_trailing_c(FrameChecksum checksum, FrameEndian endian)
    {
        var frame = new SerialFrameTemplate { Fields = [
            new() { Name = "head", Kind = FrameFieldKind.Fixed, Hex = "55 AA" },
            new() { Name = "type", Kind = FrameFieldKind.Integer, Width = 2, Endian = endian },
            new() { Name = "len", Kind = FrameFieldKind.PayloadLength, Width = 4, Endian = endian },
            new() { Name = "data", Kind = FrameFieldKind.Payload, LengthField = "len" },
            new() { Name = "check", Kind = FrameFieldKind.Checksum, Checksum = checksum, Endian = endian, ChecksumFrom = "head", ChecksumTo = "data" },
            new() { Name = "end", Kind = FrameFieldKind.Fixed, Hex = "EE" }
        ] };
        var payload = new byte[] { 1, 2, 0x43, 4 };
        var encoded = SerialFrameCodec.Encode(frame, payload, [new() { Field = "type", Value = 1234 }]);
        var transport = new ScriptTransport(); transport.Feed(Encoding.ASCII.GetBytes("DEBUG C text\n")); transport.Feed(encoded); transport.Feed([0x43]);
        var response = await SerialFrameCodec.ReadAsync(transport, frame, [new() { Field = "type", Value = 1234 }], default);
        Assert.Equal(payload, response.Payload); Assert.Equal(4U, response.Integers["len"]); Assert.Equal(1234U, response.Integers["type"]);
        var one = new byte[1]; await transport.ReadAsync(one, default); Assert.Equal(0x43, one[0]);
        encoded[^2] ^= 1; transport.Feed(encoded);
        await Assert.ThrowsAsync<InvalidDataException>(() => SerialFrameCodec.ReadAsync(transport, frame, [], default));
    }

    [Fact]
    public async Task Fixed_length_payload_without_checksum_and_overlapping_prefix_are_supported()
    {
        var frame = new SerialFrameTemplate { Fields = [
            new() { Name = "prefix", Kind = FrameFieldKind.Fixed, Hex = "AA AA AB" },
            new() { Name = "payload", Kind = FrameFieldKind.Payload, FixedLength = 2 }
        ] };
        Assert.Throws<InvalidDataException>(() => SerialFrameCodec.Encode(frame, new byte[3]));
        var transport = new ScriptTransport(); transport.Feed([0xAA]); transport.Feed(SerialFrameCodec.Encode(frame, [0x31, 0x32]));
        Assert.Equal(new byte[] { 0x31, 0x32 }, (await SerialFrameCodec.ReadAsync(transport, frame, [], default)).Payload);
    }

    [Fact]
    public async Task Rules_execute_in_order_share_variables_between_phases_and_wait_before_send()
    {
        var profile = new YmodemRuleProfile { Before = [
            new() { Name = "请求", Kind = YmodemRuleKind.RequestResponse,
                Send = new() { Kind = RuleDataKind.Text, Value = "你好", LineEnding = "CRLF" },
                Receive = new() { Kind = RuleDataKind.Hex, Value = "01 02 03", SaveVariable = "answer" } },
            new() { Name = "修改", Kind = YmodemRuleKind.ModifyBytes, InputVariable = "answer", OutputVariable = "patched", Offset = 1, ReplacementHex = "43" },
            new() { Name = "断言", Kind = YmodemRuleKind.Assert, InputVariable = "answer", Assertion = RuleAssertionKind.Hex, ExpectedValue = "01 02 03" },
            new() { Name = "禁用", Enabled = false, Kind = YmodemRuleKind.Send, Send = new() { Kind = RuleDataKind.Text, Value = "不能发送" } }
        ], After = [new() { Name = "后置发送", Kind = YmodemRuleKind.Send, Send = new() { Kind = RuleDataKind.Variable, Value = "patched" } }] };
        YmodemRuleValidator.Validate(profile);
        var transport = new ScriptTransport();
        transport.OnWrite = bytes =>
        {
            if (transport.Writes.Count == 1)
            {
                Assert.True(transport.ActiveReads > 0); Assert.Equal("你好\r\n", Encoding.UTF8.GetString(bytes));
                transport.Feed([0, 1]); transport.Feed([2, 3]);
            }
        };
        var context = new YmodemRuleContext(); var executor = new YmodemRuleExecutor(); var stages = new List<string>();
        await executor.ExecuteAsync(transport, profile, profile.Before, context, false, stages.Add, default);
        await executor.ExecuteAsync(transport, profile, profile.After, context, true, stages.Add, default);
        Assert.Equal(new byte[] { 1, 0x43, 3 }, transport.Writes.Last());
        Assert.Equal(4, stages.Count); Assert.StartsWith("后置", stages.Last());
        Assert.Equal(new byte[] { 1, 2, 3 }, context.Get("answer"));
    }

    [Fact]
    public async Task Frame_response_payload_and_named_fields_can_be_used_by_later_steps()
    {
        var profile = YmodemRuleProfiles.CreateUcchipExample(); profile.Before = [profile.Before[0]]; profile.After.Clear();
        var transport = new ScriptTransport { OnWrite = _ => { } };
        transport.OnWrite = _ => transport.Feed(SerialFrameCodec.Encode(profile.Templates[0], new byte[32], [new() { Field = "control", Value = 55 }]));
        var context = new YmodemRuleContext();
        await new YmodemRuleExecutor().ExecuteAsync(transport, profile, profile.Before, context, false, null, default);
        Assert.Equal(32, context.Get("uboot_config").Length); Assert.Equal(new byte[] { 55 }, context.Get("uboot_config.control"));
    }

    [Fact]
    public async Task No_rules_runs_only_ymodem_and_custom_post_failure_keeps_transfer_completed()
    {
        var options = new YmodemOptions { HandshakeQuietPeriod = TimeSpan.FromMilliseconds(1) };
        var receiver = new YmodemSenderTests.Receiver();
        using var file = new MemoryStream(new byte[1100]);
        var result = await new YmodemJobRunner(options).RunAsync(receiver, file, "test.bin", new YmodemRuleProfile(), null, default);
        Assert.True(result.FileTransferred); Assert.True(result.PostCompleted); Assert.True(receiver.Finished);
        var profile = new YmodemRuleProfile { After = [new() { Name = "等待不存在的标志", Kind = YmodemRuleKind.Wait, TimeoutMilliseconds = 20,
            Receive = new() { Kind = RuleDataKind.Text, Value = "NOT_FOUND" } }] };
        receiver = new();
        result = await new YmodemJobRunner(options).RunAsync(receiver, file, "test.bin", profile, null, default);
        Assert.True(result.FileTransferred); Assert.False(result.PostCompleted); Assert.Contains("后置规则未完成", result.Message); Assert.Contains("等待不存在的标志", result.Message);
    }

    [Fact]
    public async Task Example_post_matches_startup_and_is_not_firmware_version_verification()
    {
        var receiver = new YmodemSenderTests.Receiver(gateway: true);
        using var file = new MemoryStream(new byte[1]);
        var result = await new YmodemJobRunner(new() { HandshakeQuietPeriod = TimeSpan.FromMilliseconds(1) })
            .RunAsync(receiver, file, "fw.bin", YmodemRuleProfiles.CreateUcchipExample(), null, default);
        Assert.True(result.FileTransferred); Assert.True(result.PostCompleted); Assert.Contains("版本未核验", result.Message);
    }

    [Fact]
    public async Task Pre_failure_never_transfers_and_write_failure_cancels_outstanding_receive()
    {
        var profile = new YmodemRuleProfile { Before = [new() { Name = "查询", Kind = YmodemRuleKind.RequestResponse,
            Send = new() { Kind = RuleDataKind.Hex, Value = "55" }, Receive = new() { Kind = RuleDataKind.Text, Value = "response" } }] };
        var transport = new ScriptTransport { OnWrite = _ => throw new IOException("驱动发送失败") };
        using var file = new MemoryStream(new byte[1]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new YmodemJobRunner().RunAsync(transport, file, "f.bin", profile, null, default));
        Assert.Contains("查询", error.Message); Assert.Equal(0, transport.ActiveReads);
        Assert.DoesNotContain(transport.Writes, b => b[0] == 1);
    }

    [Fact]
    public async Task Cancellation_interrupts_delay_and_wait_without_following_steps()
    {
        foreach (var kind in new[] { YmodemRuleKind.Delay, YmodemRuleKind.Wait })
        {
            var profile = new YmodemRuleProfile { Before = [new() { Name = "长等待", Kind = kind, DelayMilliseconds = 60000,
                Receive = new() { Kind = RuleDataKind.Text, Value = "never" } }, new() { Name = "不要发送", Kind = YmodemRuleKind.Send }] };
            using var cancellation = new CancellationTokenSource(20); var transport = new ScriptTransport();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new YmodemRuleExecutor().ExecuteAsync(transport, profile, profile.Before, new(), false, null, cancellation.Token));
            Assert.Empty(transport.Writes); Assert.Equal(0, transport.ActiveReads);
        }
    }

    [Fact]
    public void Validation_rejects_invalid_references_order_hex_timeout_and_frame_layout()
    {
        var profile = new YmodemRuleProfile { Before = [new() { Kind = YmodemRuleKind.Send, Send = new() { Kind = RuleDataKind.Variable, Value = "unknown" } }] };
        Assert.Throws<InvalidDataException>(() => YmodemRuleValidator.Validate(profile));
        profile.Before[0].Send.Kind = RuleDataKind.Hex; profile.Before[0].Send.Value = "0";
        Assert.Throws<InvalidDataException>(() => YmodemRuleValidator.Validate(profile));
        profile.Before[0] = new() { Kind = YmodemRuleKind.Wait, TimeoutMilliseconds = 0, Receive = new() { Value = "abc" } };
        Assert.Throws<InvalidDataException>(() => YmodemRuleValidator.Validate(profile));
        var frame = YmodemRuleProfiles.CreateUcchipExample().Templates[0]; frame.Fields[4].LengthField = "future";
        Assert.Throws<InvalidDataException>(() => YmodemRuleValidator.ValidateTemplate(frame));
        frame = YmodemRuleProfiles.CreateUcchipExample().Templates[0]; frame.Fields[5].ChecksumTo = "tail";
        Assert.Throws<InvalidDataException>(() => YmodemRuleValidator.ValidateTemplate(frame));
        frame = YmodemRuleProfiles.CreateUcchipExample().Templates[0]; frame.Fields[2].Width = 3;
        Assert.Throws<InvalidDataException>(() => YmodemRuleValidator.ValidateTemplate(frame));
    }

    [Fact]
    public async Task Variable_budget_patch_bounds_and_frame_budget_fail_explicitly()
    {
        var context = new YmodemRuleContext(); context.Save("max", new byte[YmodemRuleProfiles.MaxVariableBytes]);
        Assert.Throws<InvalidDataException>(() => context.Save("extra", [1]));
        context = new(); context.Save("data", [1]);
        var profile = new YmodemRuleProfile { Before = [new() { Name = "越界修改", Kind = YmodemRuleKind.ModifyBytes, InputVariable = "data", OutputVariable = "copy", Offset = 1, ReplacementHex = "02" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new YmodemRuleExecutor().ExecuteAsync(new ScriptTransport(), profile, profile.Before, context, false, null, default));
        Assert.Throws<InvalidDataException>(() => SerialFrameCodec.Encode(YmodemRuleProfiles.CreateUcchipExample().Templates[0], new byte[65536]));
        Assert.Throws<InvalidDataException>(() => YmodemRuleProfiles.Import("{\"FormatVersion\":999}"));
    }

    [Fact]
    public async Task Text_assertion_does_not_trim_spaces_and_option_only_trims_zero_padding()
    {
        var profile = new YmodemRuleProfile { Before = [new() { Name = "精确文本", Kind = YmodemRuleKind.Assert, InputVariable = "response", Assertion = RuleAssertionKind.Text, ExpectedValue = "suc" }] };
        var context = new YmodemRuleContext(); context.Save("response", Encoding.ASCII.GetBytes("suc "));
        var executor = new YmodemRuleExecutor();
        await Assert.ThrowsAsync<InvalidDataException>(() => executor.ExecuteAsync(new ScriptTransport(), profile, profile.Before, context, false, null, default));
        context.Save("response", Encoding.ASCII.GetBytes("suc\0\0"));
        profile.Before[0].TrimTrailingZeros = true;
        await executor.ExecuteAsync(new ScriptTransport(), profile, profile.Before, context, false, null, default);
    }

    private sealed class ScriptTransport : IBinarySerialTransport
    {
        private readonly Channel<byte> _received = Channel.CreateUnbounded<byte>();
        public List<byte[]> Writes { get; } = [];
        public Action<byte[]>? OnWrite;
        public int ActiveReads;
        public void Feed(byte[] data) { foreach (var b in data) _received.Writer.TryWrite(b); }
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken token)
        {
            Interlocked.Increment(ref ActiveReads);
            try { var b = await _received.Reader.ReadAsync(token); data.Span[0] = b; return 1; }
            finally { Interlocked.Decrement(ref ActiveReads); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        { token.ThrowIfCancellationRequested(); var bytes = data.ToArray(); Writes.Add(bytes); OnWrite?.Invoke(bytes); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
