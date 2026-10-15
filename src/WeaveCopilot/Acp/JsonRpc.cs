using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json.Nodes;

namespace WeaveCopilot.Acp;

public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Newline-delimited JSON-RPC 2.0 over a pair of text streams (ACP stdio framing).</summary>
public sealed class JsonRpcConnection(TextReader input, TextWriter output)
{
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;

    public Func<string, JsonNode?, Task<JsonNode?>> OnRequest { get; set; } = (m, _) => throw new JsonRpcException(-32601, $"Method not found: {m}");
    public Action<string, JsonNode?> OnNotification { get; set; } = (_, _) => { };

    /// <summary>Synchronous enqueue: messages leave in exactly the order they were sent.</summary>
    public void SendNotification(string method, JsonObject @params) =>
        Enqueue(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    public async Task<JsonNode?> SendRequestAsync(string method, JsonObject @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Enqueue(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    private void Enqueue(JsonObject msg) => _outbox.Writer.TryWrite(Json.Serialize(msg));

    /// <summary>Reads until the input closes. Requests are handled concurrently so a long session/prompt never blocks session/cancel.</summary>
    public async Task RunAsync()
    {
        var writer = Task.Run(async () =>
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync()) { await output.WriteLineAsync(line); await output.FlushAsync(); }
        });
        try { await ReadLoopAsync(); }
        finally { _outbox.Writer.TryComplete(); await writer; }
    }

    private async Task ReadLoopAsync()
    {
        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? msg;
            try { msg = Json.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException) { Enqueue(Error(null, -32700, "Parse error")); continue; }
            if (msg is null) { Enqueue(Error(null, -32600, "Invalid Request")); continue; }

            var method = msg.Str("method");
            var id = msg["id"];
            if (method is null) { Complete(msg); continue; }
            if (id is null) { OnNotification(method, msg["params"]); continue; }
            _ = Task.Run(() => HandleRequestAsync(id.DeepClone(), method, msg["params"]));
        }
        foreach (var p in _pending.Values) p.TrySetException(new IOException("Connection closed"));
    }

    private void Complete(JsonObject msg)
    {
        if (msg["id"] is not JsonValue v || !v.TryGetValue<long>(out var id) || !_pending.TryRemove(id, out var tcs)) return;
        if (msg["error"] is JsonObject err) tcs.TrySetException(new JsonRpcException(err["code"]?.GetValue<int>() ?? -32000, err.Str("message") ?? "error"));
        else tcs.TrySetResult(msg["result"]);
    }

    private async Task HandleRequestAsync(JsonNode id, string method, JsonNode? @params)
    {
        JsonObject reply;
        try { var result = await OnRequest(method, @params) ?? new JsonObject(); reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }; }
        catch (JsonRpcException e) { reply = Error(id, e.Code, e.Message); }
        catch (Exception e) { reply = Error(id, -32603, e.Message); }
        Enqueue(reply);
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
