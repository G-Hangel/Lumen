using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class ApprovalBrokerTests
{
    [Fact]
    public async Task AskDeniesWritesAndCommandsBeforePromptOrPathAccess()
    {
        using var folder = new Folder();
        var prompt = new Prompt();
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        broker.AuthorizeRead(ChatMode.Ask, CancellationToken.None);
        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeWriteAsync(ChatMode.Ask, Call("create_file"), "new.md", CancellationToken.None))).Code);
        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeCommandAsync(ChatMode.Ask, Call("run_command"), false, CancellationToken.None))).Code);
        Assert.Empty(prompt.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task PlanAllowsInsideWritesButDeniesOutsideWritesAndCommands()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var prompt = new Prompt();
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        broker.AuthorizeRead(ChatMode.Plan, CancellationToken.None);
        // The broker enforces the workspace boundary; the executor narrows Plan to .md/.txt.
        Assert.Equal(Path.Combine(folder.Path, "plan.md"), await broker.AuthorizeWriteAsync(ChatMode.Plan, Call("create_file"), "plan.md", CancellationToken.None));
        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeWriteAsync(ChatMode.Plan, Call("create_file"), Path.Combine(outside.Path, "plan.md"), CancellationToken.None))).Code);
        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeCommandAsync(ChatMode.Plan, Call("run_command"), false, CancellationToken.None))).Code);
        Assert.Empty(prompt.Requests);
    }

    [Fact]
    public async Task PermissionLevelsRelaxOnlyAgentApprovals()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var prompt = new Prompt();
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt) { Permission = PermissionLevel.Permissive };
        var target = Path.Combine(outside.Path, "new.cs");
        Assert.Equal(target, await broker.AuthorizeWriteAsync(ChatMode.Agent, Call("create_file"), target, CancellationToken.None));
        Assert.Empty(prompt.Requests);
        Assert.Equal("approval_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None))).Code);
        Assert.Single(prompt.Requests);

        using var full = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt) { Permission = PermissionLevel.FullAccess };
        await full.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        Assert.Single(prompt.Requests, request => request.Category == ToolCategory.ExecuteCommand);
    }

    [Fact]
    public async Task InsideWritesAreAutomaticButOutsideWritesAlwaysPrompt()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var prompt = new Prompt { Response = ApprovalResponse.AllowOnce };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        Assert.Equal(Path.Combine(folder.Path, "new.cs"), await broker.AuthorizeWriteAsync(ChatMode.Agent, Call("create_file"), "new.cs", CancellationToken.None));
        Assert.Empty(prompt.Requests);
        var target = Path.Combine(outside.Path, "new.cs");
        Assert.Equal(target, await broker.AuthorizeWriteAsync(ChatMode.Agent, Call("create_file"), target, CancellationToken.None));
        var request = Assert.Single(prompt.Requests);
        Assert.Equal(ToolCategory.WriteOutsideWorkspace, request.Category);
        Assert.Equal(target, request.TargetPath);
        Assert.False(request.CanAllowCommandsForSession);
        await broker.AuthorizeWriteAsync(ChatMode.Agent, Call("edit_file"), target, CancellationToken.None);
        Assert.Equal(2, prompt.Requests.Count);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(ApprovalResponse.Deny)]
    [InlineData(ApprovalResponse.AllowCommandsForSession)]
    [InlineData((ApprovalResponse)99)]
    public async Task OutsideWriteDenialAndInvalidSessionApprovalFailClosed(ApprovalResponse response)
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var prompt = new Prompt { Response = response };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        Assert.Equal("approval_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeWriteAsync(ChatMode.Agent, Call("edit_file"), Path.Combine(outside.Path, "x"), CancellationToken.None))).Code);
        Assert.Single(prompt.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
    }

    [Fact]
    public async Task EveryCommandNeedsApprovalUnlessSessionOptInWasGranted()
    {
        using var folder = new Folder();
        var prompt = new Prompt { Response = ApprovalResponse.AllowOnce };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        Assert.Equal(2, prompt.Requests.Count);
        prompt.Response = ApprovalResponse.AllowCommandsForSession;
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        Assert.Equal(3, prompt.Requests.Count);
        Assert.All(prompt.Requests, request => Assert.True(request.CanAllowCommandsForSession));
        using var nextSession = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        await nextSession.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        Assert.Equal(4, prompt.Requests.Count);
    }

    [Fact]
    public async Task ExplicitApprovalCannotBeBypassedBySessionOptIn()
    {
        using var folder = new Folder();
        var prompt = new Prompt { Response = ApprovalResponse.AllowCommandsForSession };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        Assert.Equal("approval_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), true, CancellationToken.None))).Code);
        prompt.Response = ApprovalResponse.AllowOnce;
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), true, CancellationToken.None);
        Assert.Equal(3, prompt.Requests.Count);
        Assert.Equal("mode_denied", (await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeCommandAsync(ChatMode.Plan, Call("run_command"), false, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task CancelledPromptCannotGrantSessionPermissionEvenIfPromptIgnoresToken()
    {
        using var folder = new Folder();
        using var cancellation = new CancellationTokenSource();
        var prompt = new Prompt { Callback = _ => { cancellation.Cancel(); return Task.FromResult(ApprovalResponse.AllowCommandsForSession); } };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, cancellation.Token));
        prompt.Callback = null;
        prompt.Response = ApprovalResponse.Deny;
        await Assert.ThrowsAsync<ToolException>(() => broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None));
        Assert.Equal(2, prompt.Requests.Count);
    }

    [Fact]
    public async Task DisposedSessionRevokesPendingAndFutureApprovals()
    {
        using var folder = new Folder();
        var pending = new TaskCompletionSource<ApprovalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new Prompt { Callback = _ => pending.Task };
        var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        var task = broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        broker.Dispose();
        pending.SetResult(ApprovalResponse.AllowCommandsForSession);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
        Assert.Throws<ObjectDisposedException>(() => broker.AuthorizeRead(ChatMode.Agent, CancellationToken.None));
    }

    [Fact]
    public async Task PromptsAreSerializedAndCancelledWaitersDoNotReleaseAnotherRequestsGate()
    {
        using var folder = new Folder();
        var pending = new TaskCompletionSource<ApprovalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new Prompt { Callback = _ => pending.Task };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        var first = broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var second = broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, cancellation.Token);
        Assert.Single(prompt.Requests);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        pending.SetResult(ApprovalResponse.AllowOnce);
        await first;
        prompt.Callback = null;
        prompt.Response = ApprovalResponse.AllowOnce;
        await broker.AuthorizeCommandAsync(ChatMode.Agent, Call("run_command"), false, CancellationToken.None);
        Assert.Equal(2, prompt.Requests.Count);
    }

    [Fact]
    public async Task PromptArgumentsAreDetachedAndNeverInterpretedAsCommands()
    {
        using var folder = new Folder();
        var prompt = new Prompt { Response = ApprovalResponse.AllowOnce };
        using var broker = new ApprovalBroker(new WorkspaceGuard(folder.Path), prompt);
        using var document = JsonDocument.Parse("{\"arguments\":[\"; rm -rf\",\"&&\",\"|\",\"`\",\"$( )\"]}");
        var invocation = new ToolInvocation("exact-id", "run_command", document.RootElement);
        await broker.AuthorizeCommandAsync(ChatMode.Agent, invocation, false, CancellationToken.None);
        document.Dispose();
        var request = Assert.Single(prompt.Requests);
        Assert.Equal("exact-id", request.InvocationId);
        Assert.Equal("run_command", request.ToolName);
        Assert.Equal("; rm -rf", request.Arguments.GetProperty("arguments")[0].GetString());
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    private static ToolInvocation Call(string name)
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolInvocation("tool-1", name, document.RootElement.Clone());
    }

    private sealed class Prompt : IApprovalPrompt
    {
        internal List<ApprovalRequest> Requests { get; } = new();
        internal ApprovalResponse Response { get; set; }
        internal Func<CancellationToken, Task<ApprovalResponse>>? Callback { get; set; }
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Callback?.Invoke(cancellationToken) ?? Task.FromResult(Response);
        }
    }

    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.Approval." + Guid.NewGuid().ToString("N"));
        internal Folder() { Directory.CreateDirectory(Path); }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
