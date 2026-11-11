using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class ByokReadToolTests
{
    private sealed class NoCredentials : ICredentialStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token) => throw new InvalidOperationException("Unexpected credential read");
        public Task WriteAsync(string id, string value, CancellationToken token) => throw new InvalidOperationException("Unexpected credential write");
        public Task DeleteAsync(string id, CancellationToken token) => throw new InvalidOperationException("Unexpected credential delete");
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected HTTP call");
        }
    }

    private sealed class Executor : IReadToolExecutor, IApprovalPrompt, IDisposable
    {
        private readonly ApprovalBroker approvals;
        private readonly WorkspaceReadTools tools;
        public int Calls;
        public Executor(string root)
        {
            var guard = new WorkspaceGuard(root);
            approvals = new ApprovalBroker(guard, this);
            tools = new WorkspaceReadTools(guard, approvals);
        }
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) { Calls++; return tools.ExecuteAsync(ChatMode.Ask, invocation, token); }
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken token) => throw new InvalidOperationException("Unexpected approval");
        public void Dispose() => approvals.Dispose();
    }

    private sealed class Stub : IReadToolExecutor
    {
        public Func<ToolInvocation, CancellationToken, Task<ToolResult>> Run = (invocation, _) => Task.FromResult(new ToolResult(invocation.Id, false, "{}"));
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => Run(invocation, token);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumen-read-rpc-" + Guid.NewGuid().ToString("N"));
        public NoHttp Http { get; } = new();
        private readonly HttpClient client;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "providers.json"), "not JSON: must never be loaded by tools");
            File.WriteAllText(Path.Combine(Root, "Sample.cs"), "first\n<script>window.executed=true</script>\nneedle.*\n");
            client = new HttpClient(Http);
        }
        public ByokSession Session(IReadToolExecutor? executor) => new(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()),
            new NoCredentials(), new SettingsStore(Path.Combine(Root, "providers.json")), readTools: executor);
        public void Dispose() { client.Dispose(); Directory.Delete(Root, true); }
    }

    private static string Request(string type, object payload, string? id = null) => JsonSerializer.Serialize(new { version = 1, id = id ?? Guid.NewGuid().ToString("D"), type, payload });
    private static async Task<JsonDocument> Send(ByokSession session, string request, string source = "https://lumen.local/index.html")
    {
        var replies = new List<string>();
        await session.HandleAsync(source, request, json => { replies.Add(json); return Task.CompletedTask; }, CancellationToken.None);
        return JsonDocument.Parse(Assert.Single(replies));
    }

    [Theory]
    [InlineData("get_edit_snapshot", "{\"path\":\"Sample.cs\"}")]
    [InlineData("read_file", "{\"path\":\"Sample.cs\",\"startLine\":2,\"endLine\":2}")]
    [InlineData("list_dir", "{\"path\":\".\"}")]
    [InlineData("find_files", "{\"path\":\".\",\"glob\":\"**/*.cs\"}")]
    [InlineData("grep", "{\"path\":\".\",\"pattern\":\"needle.*\",\"glob\":\"*.cs\"}")]
    public async Task NativeReadRpcUsesRealFilesystemWithoutProviderSettingsCredentialsOrHttp(string name, string arguments)
    {
        using var fixture = new Fixture();
        using var executor = new Executor(fixture.Root);
        using var session = fixture.Session(executor);
        using var args = JsonDocument.Parse(arguments);
        using var reply = await Send(session, Request("byok.tool.read", new { name, arguments = args.RootElement }));
        Assert.Equal("byok.tool.read.result", reply.RootElement.GetProperty("type").GetString());
        var payload = reply.RootElement.GetProperty("payload");
        Assert.Equal(name, payload.GetProperty("name").GetString());
        Assert.False(payload.GetProperty("isError").GetBoolean());
        var content = payload.GetProperty("content").GetString()!;
        if (name == "read_file")
        {
            using var result = JsonDocument.Parse(content);
            Assert.Equal("<script>window.executed=true</script>", Assert.Single(result.RootElement.GetProperty("lines").EnumerateArray()).GetProperty("text").GetString());
            Assert.DoesNotContain("first", content);
        }
        else Assert.Contains("Sample.cs", content);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(0, fixture.Http.Calls);
        Assert.Equal("not JSON: must never be loaded by tools", File.ReadAllText(Path.Combine(fixture.Root, "providers.json")));
    }

    [Theory]
    [InlineData("{\"path\":\"../outside.cs\"}", "invalid_path")]
    [InlineData("{\"path\":\"Sample.cs:secret\"}", "invalid_path")]
    [InlineData("{\"path\":\"Sample.cs\",\"root\":\"C:\\\\\"}", "invalid_arguments")]
    [InlineData("{\"path\":\"Sample.cs\",\"path\":\"providers.json\"}", "invalid_arguments")]
    [InlineData("{\"path\":\"Sample.cs\",\"startLine\":\"1\"}", "invalid_arguments")]
    public async Task NativeSchemaAndConfinementRejectBadArguments(string arguments, string code)
    {
        using var fixture = new Fixture();
        using var executor = new Executor(fixture.Root);
        using var session = fixture.Session(executor);
        var request = "{\"version\":1,\"id\":\"" + Guid.NewGuid() + "\",\"type\":\"byok.tool.read\",\"payload\":{\"name\":\"read_file\",\"arguments\":" + arguments + "}}";
        using var reply = await Send(session, request);
        var payload = reply.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("isError").GetBoolean());
        using var content = JsonDocument.Parse(payload.GetProperty("content").GetString()!);
        Assert.Equal(code, content.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, fixture.Http.Calls);
    }

    [Theory]
    [InlineData("create_file")]
    [InlineData("edit_file")]
    [InlineData("run_command")]
    public async Task ForgedWritingToolNeverReachesExecutor(string name)
    {
        using var fixture = new Fixture();
        using var executor = new Executor(fixture.Root);
        using var session = fixture.Session(executor);
        using var reply = await Send(session, Request("byok.tool.read", new { name, arguments = new { path = "created.txt" } }));
        Assert.Equal("error", reply.RootElement.GetProperty("type").GetString());
        Assert.Equal("unknown_tool", reply.RootElement.GetProperty("payload").GetProperty("code").GetString());
        Assert.Equal(0, executor.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "created.txt")));
    }

    [Fact]
    public async Task UntrustedSourceExtraAuthorityAndMalformedWireFailClosed()
    {
        using var fixture = new Fixture();
        using var executor = new Executor(fixture.Root);
        using var session = fixture.Session(executor);
        var replies = new List<string>();
        await session.HandleAsync("https://outside.test/", Request("byok.tool.read", new { name = "read_file", arguments = new { path = "Sample.cs" } }),
            json => { replies.Add(json); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Empty(replies);
        foreach (var request in new[] {
            Request("byok.tool.read", new { name = "read_file", arguments = new { path = "Sample.cs" }, mode = "Agent", root = fixture.Root }),
            Request("byok.tool.read", new { name = "read_file", arguments = new[] { "Sample.cs" } }),
            "{\"type\":\"byok.tool.read\",\"payload\":" })
        {
            using var reply = await Send(session, request);
            Assert.Equal("error", reply.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOrUncorrelatedResultIsRejected(bool wrongId)
    {
        using var fixture = new Fixture();
        var executor = new Stub { Run = (invocation, _) => Task.FromResult(new ToolResult(wrongId ? "different" : invocation.Id, false, wrongId ? "{}" : new string('x', 65537))) };
        using var session = fixture.Session(executor);
        using var reply = await Send(session, Request("byok.tool.read", new { name = "read_file", arguments = new { path = "Sample.cs" } }));
        Assert.Equal("invalid_response", reply.RootElement.GetProperty("payload").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CancelInterruptsReadAndBusyGateRecovers()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Stub { Run = async (invocation, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new ToolResult(invocation.Id, false, "{}"); } };
        using var session = fixture.Session(executor);
        var replies = new List<string>();
        var operationId = Guid.NewGuid().ToString("D");
        var running = session.HandleAsync("https://lumen.local/index.html", Request("byok.tool.read", new { name = "read_file", arguments = new { path = "Sample.cs" } }, operationId),
            json => { replies.Add(json); return Task.CompletedTask; }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var busy = await Send(session, Request("byok.tool.read", new { name = "list_dir", arguments = new { path = "." } })))
            Assert.Equal("bridge_busy", busy.RootElement.GetProperty("payload").GetProperty("code").GetString());
        using (var cancelled = await Send(session, Request("byok.cancel", new { })))
            Assert.Equal("byok.cancel.result", cancelled.RootElement.GetProperty("type").GetString());
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        using (var outcome = JsonDocument.Parse(Assert.Single(replies)))
        {
            Assert.Equal(operationId, outcome.RootElement.GetProperty("id").GetString());
            Assert.Equal("cancelled", outcome.RootElement.GetProperty("payload").GetProperty("code").GetString());
        }
        executor.Run = (invocation, _) => Task.FromResult(new ToolResult(invocation.Id, false, "{}"));
        using var recovered = await Send(session, Request("byok.tool.read", new { name = "list_dir", arguments = new { path = "." } }));
        Assert.Equal("byok.tool.read.result", recovered.RootElement.GetProperty("type").GetString());
    }
}
