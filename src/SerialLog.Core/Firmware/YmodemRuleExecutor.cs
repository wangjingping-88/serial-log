using System.Text;
using SerialLog.Core.Serial;

namespace SerialLog.Core.Firmware;

public sealed class YmodemRuleContext
{
    private readonly Dictionary<string, byte[]> _variables = new(StringComparer.Ordinal);
    private int _size;
    public byte[] Get(string name) => _variables.TryGetValue(name, out var value) ? value : throw new InvalidDataException($"变量“{name}”不存在。");
    public void Save(string name, byte[] value)
    {
        if (name.Length == 0) return;
        var size = (long)_size - (_variables.GetValueOrDefault(name)?.Length ?? 0) + value.Length;
        if (size > YmodemRuleProfiles.MaxVariableBytes) throw new InvalidDataException("任务变量总量超过 1 MiB。");
        _variables[name] = value.ToArray(); _size = (int)size;
    }
}

public sealed class YmodemRuleExecutor
{
    public async Task ExecuteAsync(IBinarySerialTransport transport, YmodemRuleProfile profile, IEnumerable<YmodemRuleStep> steps,
        YmodemRuleContext context, bool post, Action<string>? stage, CancellationToken cancellationToken)
    {
        var enabled = steps.Where(s => s.Enabled).ToArray();
        for (var i = 0; i < enabled.Length; i++)
        {
            var step = enabled[i]; cancellationToken.ThrowIfCancellationRequested();
            stage?.Invoke($"{(post ? "后置" : "前置")} {i + 1}/{enabled.Length} · {step.Name}");
            try
            {
                if (step.Kind is YmodemRuleKind.Wait or YmodemRuleKind.RequestResponse)
                {
                    await transport.SetReceiveModeAsync(post && step.Receive.Kind == RuleDataKind.Text
                        ? BinaryReceiveMode.ObserveText : BinaryReceiveMode.Raw, cancellationToken).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(step.TimeoutMilliseconds);
                    // 先建立唯一接收等待，随后发送；发送失败必须停止该等待，不能留有竞争读者。
                    var response = ReceiveAsync(transport, profile, step.Receive, context, deadline.Token);
                    try
                    {
                        if (step.Kind == YmodemRuleKind.RequestResponse)
                            await transport.WriteAsync(BuildSend(profile, step.Send, context), deadline.Token).ConfigureAwait(false);
                        await response.ConfigureAwait(false);
                    }
                    catch
                    {
                        deadline.Cancel();
                        try { await response.ConfigureAwait(false); } catch { }
                        throw;
                    }
                }
                else if (step.Kind == YmodemRuleKind.Send)
                {
                    if (post) await transport.SetReceiveModeAsync(BinaryReceiveMode.Raw, cancellationToken).ConfigureAwait(false);
                    await transport.WriteAsync(BuildSend(profile, step.Send, context), cancellationToken).ConfigureAwait(false);
                }
                else if (step.Kind == YmodemRuleKind.ModifyBytes)
                {
                    var input = context.Get(step.InputVariable).ToArray();
                    var replacement = YmodemRuleValidator.ParseHex(step.ReplacementHex);
                    if (step.Offset > input.Length - replacement.Length) throw new InvalidDataException("修改偏移超出变量范围。");
                    replacement.CopyTo(input, step.Offset); context.Save(step.OutputVariable, input);
                }
                else if (step.Kind == YmodemRuleKind.Assert)
                {
                    var value = context.Get(step.InputVariable);
                    var valid = step.Assertion switch
                    {
                        RuleAssertionKind.Length => value.Length == step.ExpectedLength,
                        RuleAssertionKind.Hex => value.AsSpan().SequenceEqual(YmodemRuleValidator.ParseHex(step.ExpectedValue)),
                        RuleAssertionKind.Text => (step.TrimTrailingZeros ? Encoding.UTF8.GetString(value).TrimEnd('\0') : Encoding.UTF8.GetString(value)).Equals(step.ExpectedValue, StringComparison.Ordinal),
                        _ => false
                    };
                    if (!valid) throw new InvalidDataException($"变量“{step.InputVariable}”断言失败（长度 {value.Length}）。");
                }
                else if (step.Kind == YmodemRuleKind.Delay)
                {
                    if (post) await transport.SetReceiveModeAsync(BinaryReceiveMode.LogOnly, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(step.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                if (step.Kind is YmodemRuleKind.Wait or YmodemRuleKind.RequestResponse && step.Receive.Kind == RuleDataKind.Frame)
                    try { await transport.DiscardReceiveBufferAsync().ConfigureAwait(false); } catch { }
                if (e is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                if (e is OperationCanceledException) throw new TimeoutException($"步骤“{step.Name}”等待超时（{step.TimeoutMilliseconds} ms）。", e);
                throw new InvalidDataException($"步骤“{step.Name}”：{e.Message}", e);
            }
        }
    }

    private static byte[] Data(RuleDataKind kind, string value, YmodemRuleContext context) => kind switch
    {
        RuleDataKind.Text => Encoding.UTF8.GetBytes(value),
        RuleDataKind.Hex => YmodemRuleValidator.ParseHex(value),
        RuleDataKind.Variable => context.Get(value),
        _ => throw new InvalidDataException("无效字节来源。")
    };
    private static byte[] BuildSend(YmodemRuleProfile profile, RuleSendData send, YmodemRuleContext context)
    {
        var result = send.Kind == RuleDataKind.Frame
            ? SerialFrameCodec.Encode(profile.Templates.Single(t => t.Id == send.TemplateId), Data(send.PayloadKind, send.PayloadValue, context), send.Integers)
            : Data(send.Kind, send.Value + (send.Kind == RuleDataKind.Text ? send.LineEnding switch { "CR" => "\r", "LF" => "\n", "CRLF" => "\r\n", _ => "" } : ""), context);
        if (result.Length > YmodemRuleProfiles.MaxBufferBytes) throw new InvalidDataException("发送数据超过 64 KiB。");
        return result;
    }
    private static async Task ReceiveAsync(IBinarySerialTransport transport, YmodemRuleProfile profile,
        RuleReceiveData receive, YmodemRuleContext context, CancellationToken cancellationToken)
    {
        if (receive.Kind == RuleDataKind.Frame)
        {
            var frame = await SerialFrameCodec.ReadAsync(transport, profile.Templates.Single(t => t.Id == receive.TemplateId), receive.Integers, cancellationToken).ConfigureAwait(false);
            context.Save(receive.SaveVariable, frame.Payload);
            if (receive.SaveVariable.Length > 0)
                foreach (var field in frame.Fields) context.Save(receive.SaveVariable + "." + field.Key, field.Value);
            return;
        }
        var pattern = receive.Kind == RuleDataKind.Hex ? YmodemRuleValidator.ParseHex(receive.Value) : Encoding.UTF8.GetBytes(receive.Value);
        var prefixes = SerialFrameCodec.PrefixTable(pattern);
        var matched = 0;
        var one = new byte[1];
        // 仅保留匹配状态，噪声不会无限增长；响应变量保存命中的序列而非无限日志。
        while (matched < pattern.Length)
        {
            if (await transport.ReadAsync(one, cancellationToken).ConfigureAwait(false) != 1) throw new EndOfStreamException("串口接收中断。");
            var b = one[0];
            while (matched > 0 && b != pattern[matched]) matched = prefixes[matched - 1];
            if (b == pattern[matched]) matched++;
        }
        context.Save(receive.SaveVariable, pattern);
    }
}

public sealed record YmodemJobResult(bool FileTransferred, bool PostCompleted, string Message);

public sealed class YmodemJobRunner(YmodemOptions? options = null)
{
    public async Task<YmodemJobResult> RunAsync(IBinarySerialTransport transport, Stream file, string name,
        YmodemRuleProfile? profile, IProgress<YmodemProgress>? progress, CancellationToken cancellationToken)
    {
        if (profile is not null) YmodemRuleValidator.Validate(profile);
        var context = new YmodemRuleContext();
        var executor = new YmodemRuleExecutor();
        var completed = false;
        void Stage(string stage) => progress?.Report(new(stage, completed ? file.Length : 0, file.Length, 0, TimeSpan.Zero));
        try
        {
            if (profile is not null) await executor.ExecuteAsync(transport, profile, profile.Before, context, false, Stage, cancellationToken).ConfigureAwait(false);
            await transport.SetReceiveModeAsync(BinaryReceiveMode.Raw, cancellationToken).ConfigureAwait(false);
            await new YmodemSender(options).SendAsync(transport, file, name, progress, cancellationToken).ConfigureAwait(false);
            completed = true;
            if (profile is not null)
            {
                try { await executor.ExecuteAsync(transport, profile, profile.After, context, true, Stage, cancellationToken).ConfigureAwait(false); }
                catch (Exception e) { return new(true, false, $"文件传输完成，后置规则未完成：{(e is OperationCanceledException ? "已取消" : e.Message)}"); }
            }
            return new(true, true, profile?.After.Any(s => s.Enabled) == true ? "文件传输完成，后置规则完成；版本未核验" : "文件传输完成；版本未核验");
        }
        catch
        {
            if (!completed)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await transport.WriteAsync(new byte[] { 0x18, 0x18, 0x18, 0x18 }, cancel.Token).ConfigureAwait(false); } catch { }
            }
            throw;
        }
        finally
        {
            // 释放之前交还未消费的启动文本；ObserveText 消费过的文本不会重复展示。
            if (!completed) try { await transport.DiscardReceiveBufferAsync().ConfigureAwait(false); } catch { }
            try { await transport.SetReceiveModeAsync(BinaryReceiveMode.LogOnly, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }
}
