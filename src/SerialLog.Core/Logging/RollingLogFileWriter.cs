using System.Text;

namespace SerialLog.Core.Logging;

public sealed class RollingLogFileWriter : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _logName;
    private readonly long _maxBytes;
    private readonly IClock _clock;
    private int _fileIndex = 1;
    private string? _currentPath;
    private FileStream? _stream;
    private StreamWriter? _writer;
    private long _currentBytes;

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
        WriteLines([line]);
    }

    public void WriteLines(IReadOnlyList<ReceivedLogLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            var text = FormatLine(line);
            var bytes = Encoding.UTF8.GetByteCount(text);
            EnsureWriter(bytes);
            _writer!.Write(text);
            _currentBytes += bytes;
        }

        _writer?.Flush();
        _stream?.Flush(flushToDisk: false);
    }

    public void Dispose()
    {
        CloseWriter();
    }

    public void StartNewFile()
    {
        CloseWriter();
        while (File.Exists(BuildPath()))
        {
            _fileIndex++;
        }
    }

    private void EnsureWriter(int nextWriteBytes)
    {
        if (_writer is not null &&
            _currentBytes > 0 &&
            _currentBytes + nextWriteBytes > _maxBytes)
        {
            CloseWriter();
            _fileIndex++;
        }

        if (_writer is not null)
        {
            return;
        }

        var path = BuildPath();
        var fileInfo = new FileInfo(path);
        while (fileInfo.Exists && fileInfo.Length > 0 && fileInfo.Length + nextWriteBytes > _maxBytes)
        {
            _fileIndex++;
            path = BuildPath();
            fileInfo = new FileInfo(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true);
        _currentBytes = fileInfo.Exists ? fileInfo.Length : 0;
        _currentPath = path;
    }

    private void CloseWriter()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _stream?.Dispose();
        _writer = null;
        _stream = null;
        _currentBytes = 0;
    }

    private static string FormatLine(ReceivedLogLine line)
    {
        return $"[{line.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {AnsiEscapeSequenceStripper.Strip(line.Text)}{Environment.NewLine}";
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
