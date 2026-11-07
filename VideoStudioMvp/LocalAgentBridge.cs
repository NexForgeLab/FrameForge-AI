using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VideoStudioMvp;

public sealed record AgentHttpRequest(string Method, string Path, string Body);
public sealed record AgentBridgeResponse(int StatusCode, object Body);

/// <summary>Loopback-only HTTP bridge for an external agent adapter such as OpenMausBot.</summary>
public sealed class LocalAgentBridge : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<AgentHttpRequest, Task<AgentBridgeResponse>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private Task? _loop;

    public LocalAgentBridge(int port, Func<AgentHttpRequest, Task<AgentBridgeResponse>> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _handler = handler;
    }

    public bool IsRunning => _loop is not null && !_loop.IsCompleted;

    public bool Start()
    {
        try { _listener.Start(); _loop = Task.Run(ListenAsync); return true; }
        catch { return false; }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch when (_stop.IsCancellationRequested) { break; }
            catch { continue; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var clientScope = client;
        try
        {
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream);
            if (request is null) return;
            AgentBridgeResponse response;
            try { response = await _handler(request); }
            catch (Exception ex) { response = new(500, new { error = ex.Message }); }
            await WriteResponseAsync(stream, response);
        }
        catch { }
    }

    private static async Task<AgentHttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;
        while (buffer.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) return null;
            buffer.Write(chunk, 0, read);
            var bytes = buffer.ToArray();
            headerEnd = FindHeaderEnd(bytes);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return null;
        var all = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(separator + 1)..].Trim(), out contentLength);
        }
        var bodyStart = headerEnd + 4;
        while (all.Length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            all = buffer.ToArray();
        }
        var bodyLength = Math.Min(contentLength, Math.Max(0, all.Length - bodyStart));
        var body = Encoding.UTF8.GetString(all, bodyStart, bodyLength);
        return new AgentHttpRequest(requestLine[0], requestLine[1], body);
    }

    private async Task WriteResponseAsync(NetworkStream stream, AgentBridgeResponse response)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response.Body, _json));
        var reason = response.StatusCode switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", _ => "Internal Server Error" };
        var header = $"HTTP/1.1 {response.StatusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var i = 3; i < bytes.Length; i++)
            if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' && bytes[i - 1] == '\r' && bytes[i] == '\n') return i - 3;
        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
