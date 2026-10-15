using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
using Xunit;

namespace WeaveCopilot.Tests;

public class JsonRpcTests
{
    [Fact]
    public async Task Requests_get_one_line_responses_with_the_same_id()
    {
        var wire = new Wire();
        wire.Rpc.OnRequest = (m, p) => Task.FromResult<JsonNode?>(new JsonObject { ["echo"] = m });
        wire.SendRaw("""{"jsonrpc":"2.0","id":"abc","method":"ping","params":{}}""");
        var reply = await wire.NextAsync();
        Assert.Equal(("2.0", "abc", "ping"), (reply.Str("jsonrpc"), reply["id"]!.GetValue<string>(), reply["result"]!.Str("echo")));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Unknown_methods_and_garbage_produce_errors_and_the_loop_survives()
    {
        var wire = new Wire();
        wire.SendRaw("{not json");
        Assert.Equal(-32700, (await wire.NextAsync())["error"]!["code"]!.GetValue<int>());
        wire.SendRaw("""{"jsonrpc":"2.0","id":7,"method":"nope"}""");
        var err = (await wire.NextAsync())["error"]!;
        Assert.Equal(-32601, err["code"]!.GetValue<int>());
        wire.SendRaw("""{"jsonrpc":"2.0","id":8,"method":"nope"}""");
        Assert.Equal(8, (await wire.NextAsync())["id"]!.GetValue<int>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Notifications_get_no_reply_and_outgoing_messages_keep_their_order()
    {
        var wire = new Wire();
        var seen = new List<string>();
        wire.Rpc.OnNotification = (m, _) => seen.Add(m);
        wire.Notify("session/cancel", new JsonObject());
        for (var i = 0; i < 50; i++) wire.Rpc.SendNotification("n", new JsonObject { ["i"] = i });
        for (var i = 0; i < 50; i++) Assert.Equal(i, (await wire.NextAsync())["params"]!["i"]!.GetValue<int>());
        await wire.CloseAsync();
        Assert.Equal(["session/cancel"], seen);
    }

    [Fact]
    public async Task Outgoing_requests_resolve_from_the_clients_response()
    {
        var wire = new Wire();
        var task = wire.Rpc.SendRequestAsync("ask", new JsonObject { ["q"] = 1 });
        var req = await wire.NextAsync();
        wire.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req["id"]!.DeepClone(), ["result"] = new JsonObject { ["a"] = 2 } });
        Assert.Equal(2, (await task)!["a"]!.GetValue<int>());
        await wire.CloseAsync();
    }
}
