using System.Text;

namespace SerialLog.Core.Logging;

public sealed class RollingLogFileWriter
{
    private readonly string _rootDirectory;
    private readonly string _logName;
    private readonly long _maxBytes;
    private readonly IClock _clock;
    private int _fileIndex = 1;
    private string? _currentPath;

    public RollingLogFileWriter(string rootDirectory, string logName, long maxBytes, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logName);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "滚动文件大小必须大于 0。");
        }

        _rootDirectory = rootDirectory;
        _logName = SanitizeFileName(logName);
        _maxBytes = maxBytes;
        _clock = clock ?? new SystemClock();
    }

    public string CurrentPath => _currentPath ?? BuildPath();

    public void WriteLine(ReceivedLogLine line)
    {
        var text = line.FormattedText + Environment.NewLine;
        var bytes = Encoding.UTF8.GetByteCount(text);
        var path = EnsurePath(bytes);
        File.AppendAllText(path, text, Encoding.UTF8);
    }

    private string EnsurePath(int nextWriteBytes)
    {
        var path = BuildPath();
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists && fileInfo.Length > 0 && fileInfo.Length + nextWriteBytes > _maxBytes)
        {
            _fileIndex++;
            path = BuildPath();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _currentPath = path;
        return path;
    }

    private string BuildPath()
    {
        var fileDate = _clock.Now.ToString("yyyyMMdd");
        return Path.Combine(_rootDirectory, $"{_logName}_{fileDate}_{_fileIndex:000}.log");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        return reservedNames.Contains(sanitized) ? sanitized + "_" : sanitized;
    }
}
