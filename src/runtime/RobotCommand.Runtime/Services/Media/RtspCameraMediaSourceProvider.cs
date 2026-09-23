using System.Net.Sockets;
using System.Text;
using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class RtspCameraMediaSourceProvider : ICameraMediaSourceProvider
{
    public string Key => "rtsp";
    public string DisplayName => "RTSP";

    public void Validate(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Enter an unauthenticated rtsp:// URL. Credentials in URLs are not supported.", nameof(endpoint));
        }
        if (uri.Port is < 1 or > 65535) throw new ArgumentException("The RTSP port is invalid.", nameof(endpoint));
    }

    public async Task<CameraMediaSourceProbeResult> ProbeAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        Validate(endpoint);
        var uri = new Uri(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            await using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"OPTIONS {uri.AbsoluteUri} RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: RobotCommand\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token);
            var response = await ReadResponseHeaderAsync(stream, timeout.Token);
            var statusLine = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode))
                return new(false, "The endpoint did not return a valid RTSP response.", DateTimeOffset.UtcNow);
            var success = statusCode is >= 200 and < 300;
            return new(success, success ? "RTSP endpoint responded." : $"RTSP endpoint returned status {statusCode}.", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "RTSP endpoint did not respond within 4 seconds.", DateTimeOffset.UtcNow);
        }
        catch (SocketException)
        {
            return new(false, "RTSP endpoint could not be reached. Check the address, port, and network route.", DateTimeOffset.UtcNow);
        }
        catch (IOException)
        {
            return new(false, "RTSP endpoint closed the probe connection before responding.", DateTimeOffset.UtcNow);
        }
    }

    public CameraSourceRecord CreateSource(string id, string name, string endpoint, bool available, string message)
        => new($"media:{id}", id, $"media:{id}", null, name, Key,
            available ? AvailabilityState.Online : AvailabilityState.Offline,
            available ? "RTSP reachable" : "RTSP unavailable", available ? "Ready" : "Unavailable",
            available, available, false, 0, 0, 0, 0, string.Empty,
            available ? "RTSP_READY" : "RTSP_UNAVAILABLE", message, DateTimeOffset.UtcNow,
            SupportsPhoto: false, SupportsVideo: false, SupportsGimbal: false);

    public Task<CameraStreamRecord> OpenAsync(string id, string name, string endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CameraStreamRecord($"media-stream:{id}:{Guid.NewGuid():N}", id, id, $"media:{id}", null,
            "RTSP", "Live", endpoint, string.Empty, string.Empty, 0, 0, 0, 0,
            DateTimeOffset.UtcNow, null, "RTSP_OPEN", "Opening RTSP stream.", DateTimeOffset.UtcNow));
    }

    public Task CloseAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static async Task<string> ReadResponseHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var buffer = new byte[1];
        while (bytes.Count < 4096)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            bytes.Add(buffer[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
