using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public enum ApprovalResponse
{
    Deny,
    AllowOnce,
    AllowCommandsForSession
}

public sealed class ApprovalRequest
{
    internal ApprovalRequest(ToolInvocation invocation, ToolCategory category, string? targetPath)
    {
        InvocationId = invocation.Id;
        ToolName = invocation.Name;
        Arguments = invocation.Arguments.Clone();
        Category = category;
        TargetPath = targetPath;
    }

    public string InvocationId { get; }
    public string ToolName { get; }
    public JsonElement Arguments { get; }
    public ToolCategory Category { get; }
    public string? TargetPath { get; }
    public bool CanAllowCommandsForSession => Category == ToolCategory.ExecuteCommand;
}

public interface IApprovalPrompt
{
    Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

public sealed class ApprovalBroker : IDisposable
{
    private readonly WorkspaceGuard workspace;
    private readonly IApprovalPrompt prompt;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool allowCommands;
    public PermissionLevel Permission { get; set; } = PermissionLevel.Interactive;
    private bool disposed;

    public ApprovalBroker(WorkspaceGuard workspace, IApprovalPrompt prompt)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    public void AuthorizeRead(ChatMode mode, CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        if (ChatModePolicy.Evaluate(mode, ToolCategory.Read, Permission) != ToolPermission.Allow)
            throw new ToolException("mode_denied");
    }

    public async Task<string> AuthorizeWriteAsync(ChatMode mode, ToolInvocation invocation, string path, CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        // Deny before touching the filesystem or showing a prompt in a read-only mode.
        if (!ChatModePolicy.CanWrite(mode)) throw new ToolException("mode_denied");
        var target = workspace.ResolveWriteTarget(path);
        var category = workspace.Contains(target) ? ToolCategory.WriteInsideWorkspace : ToolCategory.WriteOutsideWorkspace;
        var permission = ChatModePolicy.Evaluate(mode, category, Permission);
        if (permission == ToolPermission.Deny) throw new ToolException("mode_denied");
        if (permission == ToolPermission.RequireApproval)
            await RequestAsync(invocation, category, target, false, cancellationToken).ConfigureAwait(false);
        CheckActive(cancellationToken);
        return workspace.ResolveWriteTarget(target);
    }

    public Task AuthorizeCommandAsync(ChatMode mode, ToolInvocation invocation, bool requiresExplicitApproval, CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        if (!ChatModePolicy.CanExecuteCommand(mode)) throw new ToolException("mode_denied");
        return RequestAsync(invocation, ToolCategory.ExecuteCommand, null, requiresExplicitApproval, cancellationToken);
    }

    private async Task RequestAsync(ToolInvocation invocation, ToolCategory category, string? target, bool explicitApproval, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckActive(cancellationToken);
            if (category == ToolCategory.ExecuteCommand && !explicitApproval && (allowCommands || Permission == PermissionLevel.FullAccess)) return;
            var response = await prompt.RequestAsync(new ApprovalRequest(invocation, category, target), cancellationToken).ConfigureAwait(false);
            CheckActive(cancellationToken);
            if (response == ApprovalResponse.AllowCommandsForSession && category == ToolCategory.ExecuteCommand && !explicitApproval)
                allowCommands = true;
            else if (response != ApprovalResponse.AllowOnce)
                throw new ToolException("approval_denied");
        }
        finally { gate.Release(); }
    }

    private void CheckActive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) throw new ObjectDisposedException(nameof(ApprovalBroker));
    }

    public void Dispose()
    {
        // Outstanding prompts may still hold the gate; dispose revokes the session, not their semaphore.
        disposed = true;
        allowCommands = false;
    }
}
