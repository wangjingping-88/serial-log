using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Firmware;

public enum YmodemRuleKind { Send, Wait, RequestResponse, ModifyBytes, Assert, Delay }
public enum RuleDataKind { Text, Hex, Variable, Frame }
public enum RuleAssertionKind { Length, Hex, Text }
public enum FrameFieldKind { Fixed, Integer, PayloadLength, Payload, Checksum }
public enum FrameEndian { Little, Big }
public enum FrameChecksum { Sum8, Xor8, Crc16Xmodem }

public sealed class YmodemRuleProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新方案";
    public int RequiredBaudRate { get; set; }
    public List<YmodemRuleStep> Before { get; set; } = [];
    public List<YmodemRuleStep> After { get; set; } = [];
    public List<SerialFrameTemplate> Templates { get; set; } = [];
}

public sealed class YmodemRuleStep
{
    public string Name { get; set; } = "新步骤";
    public bool Enabled { get; set; } = true;
    public YmodemRuleKind Kind { get; set; }
    public RuleSendData Send { get; set; } = new();
    public RuleReceiveData Receive { get; set; } = new();
    public int TimeoutMilliseconds { get; set; } = 5000;
    public string InputVariable { get; set; } = "";
    public string OutputVariable { get; set; } = "";
    public int Offset { get; set; }
    public string ReplacementHex { get; set; } = "";
    public RuleAssertionKind Assertion { get; set; }
    public int ExpectedLength { get; set; }
    public string ExpectedValue { get; set; } = "";
    public bool TrimTrailingZeros { get; set; }
    public int DelayMilliseconds { get; set; } = 1000;
}

public sealed class RuleSendData
{
    public RuleDataKind Kind { get; set; }
    public string Value { get; set; } = "";
    public string LineEnding { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public RuleDataKind PayloadKind { get; set; } = RuleDataKind.Hex;
    public string PayloadValue { get; set; } = "";
    public List<FrameIntegerValue> Integers { get; set; } = [];
}

public sealed class RuleReceiveData
{
    public RuleDataKind Kind { get; set; }
    public string Value { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<FrameIntegerValue> Integers { get; set; } = [];
    public string SaveVariable { get; set; } = "";
}

public sealed class FrameIntegerValue
{
    public string Field { get; set; } = "";
    public uint Value { get; set; }
}

public sealed class SerialFrameTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新帧模板";
    public List<SerialFrameField> Fields { get; set; } = [];
}

public sealed class SerialFrameField
{
    public string Name { get; set; } = "字段";
    public FrameFieldKind Kind { get; set; }
    public string Hex { get; set; } = "";
    public int Width { get; set; } = 1;
    public FrameEndian Endian { get; set; }
    public uint Value { get; set; }
    public int FixedLength { get; set; }
    public string LengthField { get; set; } = "";
    public FrameChecksum Checksum { get; set; }
    public string ChecksumFrom { get; set; } = "";
    public string ChecksumTo { get; set; } = "";
}

public static class YmodemRuleProfiles
{
    public const int FormatVersion = 1;
    public const int MaxBufferBytes = 65536;
    public const int MaxVariableBytes = 1048576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private sealed class RulePackage
    {
        public int FormatVersion { get; set; } = YmodemRuleProfiles.FormatVersion;
        public YmodemRuleProfile Profile { get; set; } = new();
    }
    public static YmodemRuleProfile Clone(YmodemRuleProfile profile) =>
        JsonSerializer.Deserialize<YmodemRuleProfile>(JsonSerializer.Serialize(profile, JsonOptions), JsonOptions)!;
    public static string Export(YmodemRuleProfile profile)
    { YmodemRuleValidator.Validate(profile); return JsonSerializer.Serialize(new RulePackage { Profile = profile }, JsonOptions); }
    public static YmodemRuleProfile Import(string json)
    {
        if (json.Length > 4 * MaxVariableBytes) throw new InvalidDataException("规则文件超过 4 MiB。");
        var package = JsonSerializer.Deserialize<RulePackage>(json, JsonOptions) ?? throw new InvalidDataException("规则文件为空。");
        if (package.FormatVersion != FormatVersion) throw new InvalidDataException("不支持的规则文件格式版本。");
        YmodemRuleValidator.Validate(package.Profile);
        var result = Clone(package.Profile);
        result.Id = Guid.NewGuid().ToString("N");
        var ids = result.Templates.ToDictionary(t => t.Id, _ => Guid.NewGuid().ToString("N"));
        foreach (var template in result.Templates) template.Id = ids[template.Id];
        foreach (var step in result.Before.Concat(result.After))
        {
            step.Send.TemplateId = ids.GetValueOrDefault(step.Send.TemplateId, step.Send.TemplateId);
            step.Receive.TemplateId = ids.GetValueOrDefault(step.Receive.TemplateId, step.Receive.TemplateId);
        }
        return result;
    }

    public static YmodemRuleProfile CreateUcchipExample()
    {
        var template = new SerialFrameTemplate { Name = "UCCHIP 网关帧", Fields =
        [
            new() { Name = "header", Kind = FrameFieldKind.Fixed, Hex = "AA 01" },
            new() { Name = "address", Kind = FrameFieldKind.Integer, Width = 4 },
            new() { Name = "control", Kind = FrameFieldKind.Integer },
            new() { Name = "length", Kind = FrameFieldKind.PayloadLength, Width = 2 },
            new() { Name = "payload", Kind = FrameFieldKind.Payload, LengthField = "length" },
            new() { Name = "sum", Kind = FrameFieldKind.Checksum, ChecksumFrom = "header", ChecksumTo = "payload" },
            new() { Name = "tail", Kind = FrameFieldKind.Fixed, Hex = "EE" }
        ] };
        YmodemRuleStep Request(string name, uint control, RuleDataKind kind, string data, string variable) => new()
        {
            Name = name, Kind = YmodemRuleKind.RequestResponse,
            Send = new() { Kind = RuleDataKind.Frame, TemplateId = template.Id, PayloadKind = kind, PayloadValue = data,
                Integers = [new() { Field = "control", Value = control }] },
            Receive = new() { Kind = RuleDataKind.Frame, TemplateId = template.Id, SaveVariable = variable,
                Integers = [new() { Field = "address", Value = 0 }, new() { Field = "control", Value = control }] }
        };
        return new YmodemRuleProfile { Name = "UCCHIP-小网关", RequiredBaudRate = 460800, Templates = [template], Before =
        [
            Request("查询 UBOOT 配置", 55, RuleDataKind.Hex, "00", "uboot_config"),
            new() { Name = "验证配置长度", Kind = YmodemRuleKind.Assert, InputVariable = "uboot_config", ExpectedLength = 32 },
            new() { Name = "设置下载模式", Kind = YmodemRuleKind.ModifyBytes, InputVariable = "uboot_config",
                OutputVariable = "download_config", Offset = 0, ReplacementHex = "02" },
            Request("写回 UBOOT 配置", 56, RuleDataKind.Variable, "download_config", "write_result"),
            new() { Name = "验证写入结果", Kind = YmodemRuleKind.Assert, InputVariable = "write_result",
                Assertion = RuleAssertionKind.Text, ExpectedValue = "suc", TrimTrailingZeros = true }
        ], After =
        [new() { Name = "确认设备重启", Kind = YmodemRuleKind.Wait, TimeoutMilliseconds = 60000,
            Receive = new() { Kind = RuleDataKind.Text, Value = "+SYSTEM:START" } }] };
    }
}

public static class YmodemRuleValidator
{
    public static byte[] ParseHex(string text)
    {
        var compact = string.Concat((text ?? "").Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length > YmodemRuleProfiles.MaxBufferBytes * 2) throw new InvalidDataException("Hex 数据超过 64 KiB。");
        try { return Convert.FromHexString(compact); }
        catch (FormatException) { throw new InvalidDataException("Hex 必须为完整的十六进制字节，例如 AA 01。"); }
    }
    public static void ValidateTemplate(SerialFrameTemplate template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Id) || string.IsNullOrWhiteSpace(template.Name) || template.Fields is null ||
            template.Fields.Count is < 1 or > 128 || template.Fields.Count(f => f.Kind == FrameFieldKind.Payload) != 1 ||
            template.Fields.Any(f => string.IsNullOrWhiteSpace(f.Name) || !Enum.IsDefined(f.Kind)) ||
            template.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != template.Fields.Count)
            throw new InvalidDataException("帧模板必须有唯一字段名和恰好一个载荷字段。");
        for (var i = 0; i < template.Fields.Count; i++)
        {
            var field = template.Fields[i];
            if (!Enum.IsDefined(field.Endian)) throw new InvalidDataException("无效端序。");
            if (field.Kind == FrameFieldKind.Fixed && ParseHex(field.Hex).Length == 0) throw new InvalidDataException("固定字段不能为空。");
            if (field.Kind is FrameFieldKind.Integer or FrameFieldKind.PayloadLength)
            {
                if (field.Width is not (1 or 2 or 4) || field.Width < 4 && field.Value >= 1UL << (field.Width * 8))
                    throw new InvalidDataException("整数字段宽度或数值无效。");
                if (field.Kind == FrameFieldKind.PayloadLength && i > template.Fields.FindIndex(f => f.Kind == FrameFieldKind.Payload))
                    throw new InvalidDataException("长度字段必须在载荷之前。");
            }
            if (field.Kind == FrameFieldKind.Payload)
            {
                if (field.FixedLength is < 0 or > YmodemRuleProfiles.MaxBufferBytes ||
                    field.LengthField.Length > 0 && !template.Fields.Take(i).Any(f => f.Kind == FrameFieldKind.PayloadLength && f.Name == field.LengthField))
                    throw new InvalidDataException("载荷长度必须为定长或引用前面的长度字段。");
                if (template.Fields.Where(f => f.Kind == FrameFieldKind.PayloadLength).Any(f => f.Name != field.LengthField))
                    throw new InvalidDataException("载荷长度字段必须由载荷引用。");
            }
            if (field.Kind == FrameFieldKind.Checksum)
            {
                var from = template.Fields.FindIndex(f => f.Name == field.ChecksumFrom);
                var to = template.Fields.FindIndex(f => f.Name == field.ChecksumTo);
                if (!Enum.IsDefined(field.Checksum) || from < 0 || to < from || to >= i)
                    throw new InvalidDataException("校验覆盖范围必须为校验字段之前的连续字段。");
            }
        }
        var size = template.Fields.Sum(f => f.Kind switch {
            FrameFieldKind.Fixed => ParseHex(f.Hex).Length, FrameFieldKind.Payload => f.FixedLength,
            FrameFieldKind.Checksum => f.Checksum == FrameChecksum.Crc16Xmodem ? 2 : 1, _ => f.Width });
        if (size > YmodemRuleProfiles.MaxBufferBytes) throw new InvalidDataException("帧固定部分超过 64 KiB。");
    }
    public static void Validate(YmodemRuleProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) || profile.RequiredBaudRate < 0 ||
            profile.Before is null || profile.After is null || profile.Templates is null || profile.Templates.Count > 64 ||
            profile.Before.Any(s => s is null) || profile.After.Any(s => s is null) || profile.Templates.Any(t => t is null) ||
            profile.Before.Count + profile.After.Count > 256 || profile.Templates.Select(t => t.Id).Distinct().Count() != profile.Templates.Count)
            throw new InvalidDataException("规则方案的名称、步骤或模板无效。");
        foreach (var template in profile.Templates) ValidateTemplate(template);
        var variables = new HashSet<string>(StringComparer.Ordinal);
        void RequireVariable(string name) { if (!variables.Contains(name)) throw new InvalidDataException($"变量“{name}”在引用前尚未定义。"); }
        void Define(string name)
        {
            if (name.Length == 0) return;
            if (name.Length > 128 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.'))) throw new InvalidDataException("变量名只能包含字母、数字、下划线及点。");
            variables.Add(name);
        }
        SerialFrameTemplate Frame(string id, List<FrameIntegerValue> values)
        {
            var frame = profile.Templates.SingleOrDefault(t => t.Id == id) ?? throw new InvalidDataException("引用的帧模板不存在。");
            if (values is null || values.Select(v => v.Field).Distinct().Count() != values.Count) throw new InvalidDataException("整数值字段重复。");
            foreach (var value in values)
            {
                var field = frame.Fields.SingleOrDefault(f => f.Name == value.Field && f.Kind == FrameFieldKind.Integer)
                    ?? throw new InvalidDataException($"整数值字段“{value.Field}”不存在。");
                if (field.Width < 4 && value.Value >= 1UL << (field.Width * 8)) throw new InvalidDataException("整数值超出字段宽度。");
            }
            return frame;
        }
        foreach (var step in profile.Before.Concat(profile.After).Where(s => s.Enabled))
        {
            if (!Enum.IsDefined(step.Kind) || string.IsNullOrWhiteSpace(step.Name) || step.Send is null || step.Receive is null)
                throw new InvalidDataException("步骤名称或类型无效。");
            if (step.Kind is YmodemRuleKind.Send or YmodemRuleKind.RequestResponse)
            {
                var send = step.Send;
                if (!Enum.IsDefined(send.Kind)) throw new InvalidDataException("发送类型无效。");
                if (send.Kind == RuleDataKind.Variable) RequireVariable(send.Value);
                if (send.Kind == RuleDataKind.Hex) ParseHex(send.Value);
                if (send.Kind == RuleDataKind.Text && System.Text.Encoding.UTF8.GetByteCount(send.Value) > YmodemRuleProfiles.MaxBufferBytes)
                    throw new InvalidDataException("发送文本超过 64 KiB。");
                if (send.Kind == RuleDataKind.Text && send.LineEnding is not ("" or "CR" or "LF" or "CRLF")) throw new InvalidDataException("换行符应为无、CR、LF 或 CRLF。");
                if (send.Kind == RuleDataKind.Frame)
                {
                    Frame(send.TemplateId, send.Integers);
                    if (send.PayloadKind == RuleDataKind.Variable) RequireVariable(send.PayloadValue);
                    else if (send.PayloadKind == RuleDataKind.Hex) ParseHex(send.PayloadValue);
                    else if (send.PayloadKind != RuleDataKind.Text) throw new InvalidDataException("载荷只支持文本、Hex 或变量。");
                }
            }
            if (step.Kind is YmodemRuleKind.Wait or YmodemRuleKind.RequestResponse)
            {
                if (step.TimeoutMilliseconds is < 1 or > 86400000) throw new InvalidDataException("等待超时范围为 1～86400000 ms。");
                var receive = step.Receive;
                if (receive.Kind == RuleDataKind.Frame)
                {
                    var frame = Frame(receive.TemplateId, receive.Integers);
                    if (receive.SaveVariable.Length > 0) foreach (var field in frame.Fields) Define(receive.SaveVariable + "." + field.Name);
                }
                else if (receive.Kind is RuleDataKind.Text or RuleDataKind.Hex)
                {
                    var size = receive.Kind == RuleDataKind.Hex ? ParseHex(receive.Value).Length : System.Text.Encoding.UTF8.GetByteCount(receive.Value);
                    if (size is < 1 or > YmodemRuleProfiles.MaxBufferBytes) throw new InvalidDataException("等待匹配内容必须为 1～65536 字节。");
                }
                else throw new InvalidDataException("响应只支持文本、Hex 或帧模板。");
                Define(receive.SaveVariable);
            }
            if (step.Kind is YmodemRuleKind.ModifyBytes or YmodemRuleKind.Assert)
            {
                RequireVariable(step.InputVariable);
                if (step.Kind == YmodemRuleKind.ModifyBytes)
                {
                    if (step.Offset < 0 || ParseHex(step.ReplacementHex).Length == 0 || string.IsNullOrWhiteSpace(step.OutputVariable))
                        throw new InvalidDataException("修改字节的偏移、替换内容或输出变量无效。");
                    Define(step.OutputVariable);
                }
                else
                {
                    if (!Enum.IsDefined(step.Assertion) || step.ExpectedLength is < 0 or > YmodemRuleProfiles.MaxVariableBytes) throw new InvalidDataException("断言内容无效。");
                    if (step.Assertion == RuleAssertionKind.Hex) ParseHex(step.ExpectedValue);
                }
            }
            if (step.Kind == YmodemRuleKind.Delay && step.DelayMilliseconds is < 0 or > 86400000) throw new InvalidDataException("延时范围为 0～86400000 ms。");
        }
    }
}
