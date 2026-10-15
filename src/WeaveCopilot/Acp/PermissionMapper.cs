using System.Text.Json.Nodes;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace WeaveCopilot.Acp;

/// <summary>Translates SDK permission requests to ACP `session/request_permission` params and the client's answer back to an SDK decision.</summary>
public static class PermissionMapper
{
    public static JsonArray Options() => new(
        new JsonObject { ["optionId"] = "allow_once", ["kind"] = "allow_once", ["name"] = "Allow once" },
        new JsonObject { ["optionId"] = "allow_always", ["kind"] = "allow_always", ["name"] = "Always allow" },
        new JsonObject { ["optionId"] = "reject_once", ["kind"] = "reject_once", ["name"] = "Deny" });

    public static JsonObject Params(string sessionId, PermissionRequest r, string? cwd) => new()
    {
        ["sessionId"] = sessionId, ["toolCall"] = ToolCall(r, cwd), ["options"] = Options(),
    };

    public static JsonObject ToolCall(PermissionRequest r, string? cwd)
    {
        JsonObject Base(string? id, string title, string kind, JsonObject? raw, string? path = null)
        {
            var o = new JsonObject { ["toolCallId"] = id, ["title"] = title, ["kind"] = kind, ["status"] = "pending", ["rawInput"] = raw };
            if (path is not null) o["locations"] = new JsonArray(new JsonObject { ["path"] = path });
            return o;
        }
        switch (r)
        {
            case PermissionRequestWrite w:
                var tc = Base(w.ToolCallId, "Write " + Paths.Relative(w.FileName ?? "file", cwd), "edit",
                    new JsonObject { ["fileName"] = w.FileName, ["diff"] = w.Diff }, w.FileName);
                if (w.NewFileContents is { } text && w.FileName is { } fn)
                    tc["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = fn, ["oldText"] = null, ["newText"] = text });
                return tc;
            case PermissionRequestShell s:
                return Base(s.ToolCallId, s.Intention ?? s.FullCommandText ?? "Run command", "execute", new JsonObject
                {
                    ["command"] = s.FullCommandText,
                    ["commands"] = new JsonArray((s.Commands ?? []).Select(c => (JsonNode?)c.Identifier).ToArray()),
                });
            case PermissionRequestRead rd:
                return Base(rd.ToolCallId, "Read " + Paths.Relative(rd.Path ?? "file", cwd), "read", new JsonObject { ["path"] = rd.Path }, rd.Path);
            case PermissionRequestMcp m:
                return Base(m.ToolCallId, $"{m.ServerName}: {m.ToolTitle ?? m.ToolName}", "other", Json.FromElement(m.Args) as JsonObject);
            case PermissionRequestUrl u:
                return Base(u.ToolCallId, "Fetch " + u.Url, "fetch", new JsonObject { ["url"] = u.Url });
            default:
                return Base(null, r.Kind ?? "Permission", "other", null);
        }
    }

    /// <summary>Maps the client's `outcome` ({outcome:"selected",optionId} | {outcome:"cancelled"}) to an SDK decision. Anything unclear denies.</summary>
    public static PermissionDecision ToDecision(PermissionRequest r, JsonNode? outcome)
    {
        if (outcome is not JsonObject o || o.Str("outcome") != "selected") return PermissionDecision.Reject("Cancelled");
        return o.Str("optionId") switch
        {
            "allow_once" => PermissionDecision.ApproveOnce(),
            "allow_always" => ForSession(r),
            _ => PermissionDecision.Reject("Denied by user"),
        };
    }

    private static PermissionDecision ForSession(PermissionRequest r)
    {
        PermissionDecisionApproveForSessionApproval? approval = r switch
        {
            PermissionRequestWrite => new PermissionDecisionApproveForSessionApprovalWrite(),
            PermissionRequestRead => new PermissionDecisionApproveForSessionApprovalRead(),
            PermissionRequestShell s => new PermissionDecisionApproveForSessionApprovalCommands { CommandIdentifiers = (s.Commands ?? []).Select(c => c.Identifier).ToList() },
            PermissionRequestMcp m => new PermissionDecisionApproveForSessionApprovalMcp { ServerName = m.ServerName, ToolName = m.ToolName },
            _ => null,
        };
        return approval is null ? PermissionDecision.ApproveOnce() : new PermissionDecisionApproveForSession { Approval = approval };
    }
}
