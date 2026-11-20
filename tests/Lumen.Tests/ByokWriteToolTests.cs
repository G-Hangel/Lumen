using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class ByokWriteToolTests
{
    private sealed class Credentials : ICredentialStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token) => throw new InvalidOperationException("Unexpected credentials");
        public Task WriteAsync(string id, string value, CancellationToken token) => throw new InvalidOperationException("Unexpected credentials");
        public Task DeleteAsync(string id, CancellationToken token) => throw new InvalidOperationException("Unexpected credentials");
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Unexpected HTTP");
    }
    private sealed class Executor : IWriteToolExecutor
    {
        public int Calls;
        public int Captures;
        public IWriteToolExecutor? Captured;
        public int Undos;
        public readonly List<string> Lifecycle = new();
        public Func<bool, CancellationToken, Task> Begin = (_, _) => Task.CompletedTask;
        public int PendingChanges { get; set; }
        public IReadOnlyList<ToolSpec> Specs => Array.Empty<ToolSpec>();
        public Func<ToolInvocation, CancellationToken, Task<ToolResult>> Run = (call, _) => Task.FromResult(new ToolResult(call.Id, false, "{}"));
        public Task<IWriteToolExecutor> CaptureAsync(CancellationToken token) { Captures++; Lifecycle.Add("capture"); return Task.FromResult(Captured ?? this); }
        public IWriteToolExecutor ForMode(ChatMode mode, PermissionLevel permission) => this;
        public Task BeginTurnAsync(bool automatic, CancellationToken token) { Lifecycle.Add(automatic ? "auto" : "manual"); return Begin(automatic, token); }
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; Lifecycle.Add("execute"); PendingChanges++; return Run(call, token); }
        public Task UndoTurnAsync(CancellationToken token) { Undos++; PendingChanges = 0; return Task.CompletedTask; }
    }
    private static ByokSession Session(Executor executor) => new(new ProviderFactory(new HttpTransport(new HttpClient(new NoHttp())), new CapabilityResolver()),
        new Credentials(), new SettingsStore(Path.Combine(Path.GetTempPath(), "lumen-unread-" + Guid.NewGuid().ToString("N"), "providers.json")), writeTools: executor);
    private static async Task<JsonElement> Send(ByokSession session, string type, object payload, string source = "https://lumen.local/index.html")
    {
        var replies = new List<string>();
        await session.HandleAsync(source, JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("D"), type, payload }),
            reply => { replies.Add(reply); return Task.CompletedTask; }, CancellationToken.None);
        if (replies.Count == 0) return default;
        using var json = JsonDocument.Parse(Assert.Single(replies)); return json.RootElement.Clone();
    }
    [Fact]
    public async Task ManualWritesCaptureAndReportPendingWithoutProviderOrSettings()
    {
        var executor = new Executor(); using var session = Session(executor);
        foreach (var name in new[] { "create_file", "edit_file" })
        {
            var reply = await Send(session, "byok.tool.write", new { name, arguments = new { path = "Sample.cs", content = "new\ntext" } });
            Assert.Equal("byok.tool.write.result", reply.GetProperty("type").GetString());
            Assert.Equal(executor.Calls, reply.GetProperty("payload").GetProperty("pending").GetInt32());
        }
        Assert.Equal(2, executor.Captures);
        Assert.Equal(new[] { "capture", "manual", "execute", "capture", "manual", "execute" }, executor.Lifecycle);
        var status = await Send(session, "byok.tool.write.status", new { });
        Assert.Equal(2, status.GetProperty("payload").GetProperty("pending").GetInt32());
        var undo = await Send(session, "byok.tool.write.undo", new { });
        Assert.True(undo.GetProperty("payload").GetProperty("undone").GetBoolean());
        Assert.Equal(0, undo.GetProperty("payload").GetProperty("pending").GetInt32());
        Assert.Equal(1, executor.Undos);
    }
    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("C:/outside.cs")]
    [InlineData("/outside.cs")]
    [InlineData("\\outside.cs")]
    [InlineData("file.cs:secret")]
    [InlineData("bad\u0000.cs")]
    public async Task UnsafePathsRejectedBeforeCapture(string path)
    {
        var executor = new Executor(); using var session = Session(executor);
        var reply = await Send(session, "byok.tool.write", new { name = "create_file", arguments = new { path, content = "x" } });
        Assert.Equal("error", reply.GetProperty("type").GetString());
        Assert.Equal(0, executor.Captures); Assert.Equal(0, executor.Calls);
    }
    [Fact]
    public async Task AuthorityToolsExtraFieldsAndContentBoundariesRejectBeforeMutation()
    {
        var executor = new Executor(); using var session = Session(executor);
        foreach (var payload in new object[] {
            new { name = "run_command", arguments = new { path = "x", content = "x" } },
            new { name = "edit_file", arguments = new { path = "x", content = "x", mode = "Agent" } },
            new { name = "edit_file", arguments = new { path = "x", content = new string('x', 65537) } },
            new { name = "edit_file", arguments = new { path = "x", content = "bad\u0000" } },
            new { name = "edit_file", arguments = new { path = "x", content = "x" }, root = "elsewhere" } })
            Assert.Equal("error", (await Send(session, "byok.tool.write", payload)).GetProperty("type").GetString());
        Assert.Equal(0, executor.Captures); Assert.Equal(0, executor.Calls);
        Assert.Equal("error", (await Send(session, "byok.tool.write.undo", new { mode = "Agent" })).GetProperty("type").GetString());
        Assert.Equal(0, executor.Undos);
        Assert.Equal(JsonValueKind.Undefined, (await Send(session, "byok.tool.write", new { name = "create_file", arguments = new { path = "x", content = "x" } }, "https://outside.test")).ValueKind);
        Assert.Equal(0, executor.Calls);
    }
    [Fact]
    public async Task SuccessfulCommitIsNotHiddenByLateCancellationAndGateRecovers()
    {
        var executor = new Executor(); using var session = Session(executor);
        executor.Run = async (call, _) => {
            await Send(session, "byok.cancel", new { });
            return new ToolResult(call.Id, false, "{}");
        };
        var reply = await Send(session, "byok.tool.write", new { name = "edit_file", arguments = new { path = "x", content = "x" } });
        Assert.Equal("byok.tool.write.result", reply.GetProperty("type").GetString());
        Assert.Equal(1, reply.GetProperty("payload").GetProperty("pending").GetInt32());
        Assert.Equal("byok.tool.write.status.result", (await Send(session, "byok.tool.write.status", new { })).GetProperty("type").GetString());
    }
    [Fact]
    public async Task ManualBeginAndExecutionUseCapturedExecutor()
    {
        var captured = new Executor();
        var executor = new Executor { Captured = captured };
        using var session = Session(executor);
        var reply = await Send(session, "byok.tool.write", new { name = "edit_file", arguments = new { path = "x", content = "x" } });
        Assert.Equal("byok.tool.write.result", reply.GetProperty("type").GetString());
        Assert.Equal(new[] { "capture" }, executor.Lifecycle);
        Assert.Equal(new[] { "manual", "execute" }, captured.Lifecycle);
    }

    [Fact]
    public async Task CancelledManualBeginDoesNotExecuteAndGateRecovers()
    {
        var executor = new Executor(); using var session = Session(executor);
        executor.Begin = async (automatic, token) =>
        {
            Assert.False(automatic);
            await Send(session, "byok.cancel", new { });
            token.ThrowIfCancellationRequested();
        };
        var reply = await Send(session, "byok.tool.write", new { name = "edit_file", arguments = new { path = "x", content = "x" } });
        Assert.Equal("cancelled", reply.GetProperty("payload").GetProperty("code").GetString());
        Assert.Equal(new[] { "capture", "manual" }, executor.Lifecycle);
        Assert.Equal(0, executor.Calls);
        executor.Begin = (_, _) => Task.CompletedTask;
        Assert.Equal("byok.tool.write.result", (await Send(session, "byok.tool.write", new { name = "edit_file", arguments = new { path = "x", content = "x" } })).GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvalidExecutorCorrelationAndOversizedResultReject()
    {
        var executor = new Executor(); using var session = Session(executor);
        foreach (var run in new Func<ToolInvocation, CancellationToken, Task<ToolResult>>[] {
            (_, _) => Task.FromResult(new ToolResult("other", false, "{}")),
            (call, _) => Task.FromResult(new ToolResult(call.Id, false, new string('x', 65537))) })
        {
            executor.Run = run;
            var reply = await Send(session, "byok.tool.write", new { name = "edit_file", arguments = new { path = "x", content = "x" } });
            Assert.Equal("invalid_response", reply.GetProperty("payload").GetProperty("code").GetString());
        }
    }
}
