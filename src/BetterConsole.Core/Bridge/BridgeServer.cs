using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BetterConsole.Core.Bridge;

/// <summary>
/// The app end of the channel to the companion Lua addon. BetterConsole creates a named pipe and
/// passes its name to srcds in the <c>BETTERCONSOLE_PIPE</c> environment variable; the native module
/// gmsv_betterconsole connects to it. Messages are newline-delimited JSON objects with a "t" field.
/// The server side reconnects after every map change (the Lua state is rebuilt), so the pipe accepts
/// any number of sequential connections.
/// </summary>
public sealed class BridgeServer : IAsyncDisposable
{
    public const string EnvironmentVariable = "BETTERCONSOLE_PIPE";

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private Task? _loop;
    private volatile bool _connected;

    public BridgeServer()
    {
        PipeName = $"betterconsole-{Environment.ProcessId}-{Guid.NewGuid():N}"[..48];
    }

    public string PipeName { get; }

    public bool IsConnected => _connected;

    /// <summary>Raised on a background thread with the message type and the whole JSON object.</summary>
    public event Action<string, JsonElement>? MessageReceived;

    /// <summary>Raised on a background thread.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start() => _loop ??= Task.Run(() => AcceptLoop(_cts.Token));

    /// <summary>Queues a message; it is dropped when nobody is connected.</summary>
    public void Send(string type, object? data = null)
    {
        if (!_connected) return;
        var json = data == null
            ? JsonSerializer.Serialize(new Dictionary<string, object?> { ["t"] = type })
            : SerializeWithType(type, data);
        _outgoing.Writer.TryWrite(json);
    }

    private static string SerializeWithType(string type, object data)
    {
        var element = JsonSerializer.SerializeToElement(data, JsonOptions);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", type);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in element.EnumerateObject())
                {
                    if (p.NameEquals("t")) continue;
                    p.WriteTo(w);
                }
            }
            else
            {
                w.WritePropertyName("data");
                element.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1 << 20, 1 << 20);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                // Anything queued for an old connection is stale.
                while (_outgoing.Reader.TryRead(out _)) { }
                _connected = true;
                ConnectionChanged?.Invoke(true);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var reader = ReadLoop(pipe, linked.Token);
                var writer = WriteLoop(pipe, linked.Token);
                await Task.WhenAny(reader, writer).ConfigureAwait(false);
                linked.Cancel();
                try { await Task.WhenAll(reader, writer).ConfigureAwait(false); } catch { }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("bridge: " + ex);
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { }
            }
            finally
            {
                if (_connected)
                {
                    _connected = false;
                    ConnectionChanged?.Invoke(false);
                }
                if (pipe != null) await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReadLoop(Stream pipe, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            int n = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) return;
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                start = i + 1;
                Dispatch(line.GetBuffer().AsSpan(0, (int)line.Length));
                line.SetLength(0);
            }
            line.Write(buffer, start, n - start);
            if (line.Length > 64 << 20) line.SetLength(0); // garbage without newlines: drop it
        }
    }

    private void Dispatch(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return;
        try
        {
            // Lua strings are bytes: an addon may print text that is not valid UTF-8. Decoding first
            // turns such bytes into U+FFFD instead of failing the whole message.
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(json));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return;
            MessageReceived?.Invoke(t.GetString()!, root.Clone());
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine("bridge: bad json: " + ex.Message);
        }
    }

    private async Task WriteLoop(Stream pipe, CancellationToken ct)
    {
        var reader = _outgoing.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            using var batch = new MemoryStream();
            while (reader.TryRead(out var msg))
            {
                var bytes = Encoding.UTF8.GetBytes(msg);
                batch.Write(bytes);
                batch.WriteByte((byte)'\n');
                if (batch.Length > 256 * 1024) break;
            }
            await pipe.WriteAsync(batch.GetBuffer().AsMemory(0, (int)batch.Length), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
