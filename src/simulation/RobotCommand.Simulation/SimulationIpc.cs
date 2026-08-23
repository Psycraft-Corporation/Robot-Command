using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RobotCommand.Simulation;

public sealed class SharedSnapshotChannel : IDisposable
{
    private const int SlotHeaderSize = 24;
    private const int SlotSize = 1_048_576;
    private const int SlotCount = 2;
    private readonly string _path;
    private readonly FileStream _file;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly bool _writer;
    private long _lastSequence;

    private SharedSnapshotChannel(string path, bool writer)
    {
        _path = path;
        _writer = writer;
        if (writer)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                TryRestrict(directory, isDirectory: true);
            }
            _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            _file.SetLength((long)SlotSize * SlotCount);
            TryRestrict(path, isDirectory: false);
        }
        else
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        _mapping = MemoryMappedFile.CreateFromFile(_file, null, (long)SlotSize * SlotCount,
            writer ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read,
            HandleInheritability.None, false);
        _view = _mapping.CreateViewAccessor(0, (long)SlotSize * SlotCount,
            writer ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read);
    }

    public static SharedSnapshotChannel CreateWriter(string path) => new(path, true);
    public static SharedSnapshotChannel OpenReader(string path) => new(path, false);

    public void Write(SimulationSnapshot snapshot)
    {
        if (!_writer) throw new InvalidOperationException("This snapshot channel is read-only.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions.Default);
        if (payload.Length > SlotSize - SlotHeaderSize) throw new InvalidOperationException("Simulation snapshot exceeds the shared-memory slot.");
        var sequence = snapshot.Sequence == 0 ? _lastSequence + 1 : snapshot.Sequence;
        var slot = (int)(sequence % SlotCount);
        var offset = (long)slot * SlotSize;
        _view.Write(offset, 0L); // invalidate while the inactive slot is written
        _view.Write(offset + 8, payload.Length);
        _view.Write(offset + 16, Crc32(payload));
        _view.WriteArray(offset + SlotHeaderSize, payload, 0, payload.Length);
        _view.Flush();
        _view.Write(offset, sequence); // publish marker last
        _view.Flush();
        _lastSequence = sequence;
    }

    public bool TryRead(out SimulationSnapshot? snapshot)
    {
        snapshot = null;
        var bestSequence = _lastSequence;
        byte[]? bestPayload = null;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var offset = (long)slot * SlotSize;
            var before = _view.ReadInt64(offset);
            var length = _view.ReadInt32(offset + 8);
            var checksum = _view.ReadUInt32(offset + 16);
            if (before <= bestSequence || length is <= 0 or > SlotSize - SlotHeaderSize) continue;
            var payload = new byte[length];
            _view.ReadArray(offset + SlotHeaderSize, payload, 0, length);
            var after = _view.ReadInt64(offset);
            if (before != after || checksum != Crc32(payload)) continue;
            try
            {
                var candidate = JsonSerializer.Deserialize<SimulationSnapshot>(payload, JsonOptions.Default);
                if (candidate is not null && candidate.Sequence == before)
                {
                    bestSequence = before;
                    bestPayload = payload;
                }
            }
            catch (JsonException) { }
        }
        if (bestPayload is null) return false;
        snapshot = JsonSerializer.Deserialize<SimulationSnapshot>(bestPayload, JsonOptions.Default);
        _lastSequence = bestSequence;
        return snapshot is not null;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
        _file.Dispose();
        if (_writer)
        {
            try { File.Delete(_path); } catch { }
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static void TryRestrict(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (isDirectory) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
    }
}

public sealed class LocalControlServer : IAsyncDisposable
{
    private readonly string _endpoint;
    private readonly Func<SimulationCommand, Task<SimulationCommandResult>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private Socket? _unixListener;
    private string? _unixPath;
    private Task? _acceptLoop;

    public LocalControlServer(string endpoint, Func<SimulationCommand, Task<SimulationCommandResult>> handler)
    {
        _endpoint = endpoint;
        _handler = handler;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            _acceptLoop = Task.Run(() => NamedPipeLoopAsync(_shutdown.Token), cancellationToken);
        }
        else
        {
            _unixPath = _endpoint;
            try { File.Delete(_unixPath); } catch (FileNotFoundException) { }
            var directory = Path.GetDirectoryName(_unixPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _unixListener.Bind(new UnixDomainSocketEndPoint(_unixPath));
            try { File.SetUnixFileMode(_unixPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            _unixListener.Listen(16);
            _acceptLoop = Task.Run(() => UnixLoopAsync(_shutdown.Token), cancellationToken);
        }
        return Task.CompletedTask;
    }

    private async Task NamedPipeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(_endpoint, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken);
            await ProcessStreamAsync(pipe, cancellationToken);
        }
    }

    private async Task UnixLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _unixListener is not null)
        {
            var client = await _unixListener.AcceptAsync(cancellationToken);
            using (client) await ProcessStreamAsync(new NetworkStream(client, ownsSocket: false), cancellationToken);
        }
    }

    private async Task ProcessStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            SimulationCommandResult result;
            try
            {
                var command = JsonSerializer.Deserialize<SimulationCommand>(line, JsonOptions);
                result = command is null
                    ? new("unknown", false, "INVALID_REQUEST", "The request was empty.", 0)
                    : await _handler(command);
            }
            catch (Exception exception)
            {
                result = new("unknown", false, "INVALID_REQUEST", exception.Message, 0);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { _unixListener?.Dispose(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        if (_unixPath is not null) try { File.Delete(_unixPath); } catch { }
        _shutdown.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class LocalControlClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _endpoint;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public LocalControlClient(string endpoint) => _endpoint = endpoint;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken);
            _stream = pipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint), cancellationToken);
            _stream = new NetworkStream(socket, ownsSocket: true);
        }
        _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public async Task<SimulationCommandResult> SendAsync(SimulationCommand command, CancellationToken cancellationToken = default)
    {
        if (_writer is null || _reader is null) throw new InvalidOperationException("The simulation control channel is not connected.");
        await _writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
        var line = await _reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("The simulation worker disconnected.");
        return JsonSerializer.Deserialize<SimulationCommandResult>(line, JsonOptions)
            ?? throw new InvalidDataException("The simulation worker returned an invalid response.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null) _reader.Dispose();
        if (_writer is not null) await _writer.DisposeAsync();
        if (_stream is not null) await _stream.DisposeAsync();
    }
}
