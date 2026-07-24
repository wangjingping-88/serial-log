using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SerialLog.Cli;

public sealed class OtaHttpFileServer : IAsyncDisposable
{
    private const int MaximumRequestHeaderBytes = 16 * 1024;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentBag<Task> _clientTasks = [];
    private TcpListener? _listener;
    private Task? _acceptTask;
    private EcoLinkOtaHttpConfig? _config;

    public void Start(EcoLinkOtaHttpConfig config)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("HTTP文件服务器已经启动。");
        }

        ValidateConfig(config);
        Directory.CreateDirectory(config.PackageRoot);

        var bindAddress = config.BindAddress == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(config.BindAddress);
        _config = config;
        _listener = new TcpListener(bindAddress, config.Port);
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_lifetime.Token);
    }

    public Uri BuildPackageUri(string fileName)
    {
        var config = _config
            ?? throw new InvalidOperationException("HTTP文件服务器尚未启动。");
        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException("OTA文件名不能包含路径。", nameof(fileName));
        }

        return new UriBuilder(
            Uri.UriSchemeHttp,
            config.AdvertiseAddress,
            config.Port,
            $"/{NormalizeBasePath(config.BasePath)}/{Uri.EscapeDataString(fileName)}")
            .Uri;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await GetListener().AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _clientTasks.Add(HandleClientAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                await ProcessRequestAsync(stream, request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                try
                {
                    await using var stream = client.GetStream();
                    await WriteErrorAsync(
                        stream,
                        400,
                        "Bad Request",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private async Task ProcessRequestAsync(
        NetworkStream stream,
        OtaHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteErrorAsync(
                stream,
                405,
                "Method Not Allowed",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = ResolveRequestPath(request.Path);
        if (path is null || !File.Exists(path))
        {
            await WriteErrorAsync(
                stream,
                404,
                "Not Found",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var range = ParseRange(request.Headers, file.Length);
        if (range is null && request.Headers.ContainsKey("Range"))
        {
            await WriteRangeNotSatisfiableAsync(
                stream,
                file.Length,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var start = range?.Start ?? 0;
        var end = range?.End ?? (file.Length - 1);
        var contentLength = 0 == file.Length ? 0 : end - start + 1;
        var status = range is null ? "200 OK" : "206 Partial Content";
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Type: application/octet-stream\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Connection: close\r\n");
        if (range is not null)
        {
            headers.Append("Content-Range: bytes ")
                .Append(start).Append('-').Append(end)
                .Append('/').Append(file.Length).Append("\r\n");
        }

        headers.Append("\r\n");
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(headers.ToString()),
            cancellationToken).ConfigureAwait(false);
        if (request.Method == "HEAD" || contentLength == 0)
        {
            return;
        }

        file.Position = start;
        await CopyBytesAsync(
            file,
            stream,
            contentLength,
            cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveRequestPath(string requestPath)
    {
        var config = _config!;
        var pathOnly = requestPath.Split('?', 2)[0];
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(pathOnly);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var prefix = $"/{NormalizeBasePath(config.BasePath)}/";
        if (!decoded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = decoded[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var root = Path.GetFullPath(config.PackageRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static OtaHttpRange? ParseRange(
        IReadOnlyDictionary<string, string> headers,
        long fileLength)
    {
        if (!headers.TryGetValue("Range", out var value))
        {
            return null;
        }

        if (fileLength <= 0
            || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || value.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = value[6..].Split('-', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(parts[0]))
        {
            if (!long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var suffixLength)
                || suffixLength <= 0)
            {
                return null;
            }

            suffixLength = Math.Min(suffixLength, fileLength);
            return new OtaHttpRange(fileLength - suffixLength, fileLength - 1);
        }

        if (!long.TryParse(
                parts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var start)
            || start < 0
            || start >= fileLength)
        {
            return null;
        }

        var end = fileLength - 1;
        if (!string.IsNullOrWhiteSpace(parts[1])
            && (!long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out end)
                || end < start))
        {
            return null;
        }

        return new OtaHttpRange(start, Math.Min(end, fileLength - 1));
    }

    private static async Task<OtaHttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var oneByte = new byte[1];
        var matched = 0;
        var terminator = new byte[] { 0x0D, 0x0A, 0x0D, 0x0A };
        while (buffer.Length < MaximumRequestHeaderBytes)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("HTTP连接在请求头完成前关闭。");
            }

            buffer.WriteByte(oneByte[0]);
            matched = oneByte[0] == terminator[matched] ? matched + 1 : 0;
            if (matched == terminator.Length)
            {
                break;
            }
        }

        if (matched != terminator.Length)
        {
            throw new IOException("HTTP请求头过长。");
        }

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new IOException("HTTP请求行无效。");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new IOException("HTTP请求头无效。");
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return new OtaHttpRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1],
            headers);
    }

    private static async Task CopyBytesAsync(
        Stream source,
        Stream destination,
        long count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("OTA文件读取提前结束。");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static Task WriteErrorAsync(
        NetworkStream stream,
        int code,
        string reason,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes($"{code} {reason}\n");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");
        return WriteResponseAsync(stream, header, body, cancellationToken);
    }

    private static Task WriteRangeNotSatisfiableAsync(
        NetworkStream stream,
        long fileLength,
        CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 416 Range Not Satisfiable\r\n"
            + $"Content-Range: bytes */{fileLength}\r\n"
            + "Content-Length: 0\r\n"
            + "Connection: close\r\n\r\n");
        return stream.WriteAsync(response, cancellationToken).AsTask();
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        byte[] header,
        byte[] body,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateConfig(EcoLinkOtaHttpConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BindAddress)
            || string.IsNullOrWhiteSpace(config.AdvertiseAddress)
            || config.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(config.BasePath)
            || string.IsNullOrWhiteSpace(config.PackageRoot))
        {
            throw new InvalidOperationException("HTTP文件服务器配置无效。");
        }

        _ = config.BindAddress == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(config.BindAddress);
    }

    private static string NormalizeBasePath(string path)
    {
        return path.Trim().Trim('/');
    }

    private TcpListener GetListener()
    {
        return _listener
            ?? throw new InvalidOperationException("HTTP文件服务器尚未启动。");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener?.Stop();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await Task.WhenAll(_clientTasks.ToArray()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private sealed record OtaHttpRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record OtaHttpRange(long Start, long End);
}
