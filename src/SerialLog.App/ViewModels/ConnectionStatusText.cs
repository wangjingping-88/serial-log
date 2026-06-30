namespace SerialLog.App.ViewModels;

public static class ConnectionStatusText
{
    public static string FromException(string? portName, Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return string.IsNullOrWhiteSpace(portName)
                ? "端口被占用"
                : $"端口 {portName} 被占用";
        }

        return $"连接失败：{exception.Message}";
    }
}
