namespace SerialLog.Cli;

public static class OtaTargetTopologyResolver
{
    public static void Apply(
        EcoLinkOtaRunConfig config,
        uint previousExtenderDeviceId = 0)
    {
        ArgumentNullException.ThrowIfNull(config);

        var nodeDevices = config.Devices
            .Where(device => device.Enabled
                && string.Equals(
                    device.Role,
                    "node",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var configuredNodeIds = nodeDevices
            .Where(device => device.NodeId != 0)
            .Select(device => device.NodeId)
            .ToArray();
        if (configuredNodeIds.Distinct().Count() != configuredNodeIds.Length)
        {
            throw new InvalidOperationException("启用的Node设备存在重复NodeId。");
        }

        foreach (var target in config.Targets.Where(target => target.Enabled))
        {
            if (UsesExtenderDeviceId(target.DevType)
                && config.ExtenderDeviceId != 0
                && (target.DeviceIds.Length == 0
                    || IsPreviousAutomaticId(
                        target.DeviceIds,
                        previousExtenderDeviceId)))
            {
                target.DeviceIds = [config.ExtenderDeviceId];
            }

            if (!string.Equals(
                    target.DevType,
                    "node",
                    StringComparison.OrdinalIgnoreCase)
                || target.NodeIds.Length != 0)
            {
                continue;
            }

            if (nodeDevices.Length == 0
                || nodeDevices.Any(device => device.NodeId == 0))
            {
                throw new InvalidOperationException(
                    $"Node OTA目标{target.Name}未配置NodeIds，且设备拓扑中的NodeId不完整。");
            }

            target.NodeIds = nodeDevices.Select(device => device.NodeId).ToArray();
        }
    }

    private static bool UsesExtenderDeviceId(string devType)
    {
        return string.Equals(devType, "iote", StringComparison.OrdinalIgnoreCase)
            || string.Equals(devType, "ex_mcu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(devType, "node", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreviousAutomaticId(
        IReadOnlyList<uint> deviceIds,
        uint previousExtenderDeviceId)
    {
        return previousExtenderDeviceId != 0
            && deviceIds.Count == 1
            && deviceIds[0] == previousExtenderDeviceId;
    }
}
