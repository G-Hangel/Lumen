using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioReadTools : IReadToolExecutor, IApprovalPrompt
{
    private readonly JoinableTaskFactory taskFactory;
    private readonly string? root;
    private readonly string? solutionPath;

    internal VisualStudioReadTools(JoinableTaskFactory taskFactory) => this.taskFactory = taskFactory;

    private VisualStudioReadTools(JoinableTaskFactory taskFactory, string root, string solutionPath)
    {
        this.taskFactory = taskFactory;
        this.root = root;
        this.solutionPath = solutionPath;
    }

    public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;

    public async Task<IReadToolExecutor> CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await taskFactory.SwitchToMainThreadAsync(cancellationToken);
            var capturedRoot = VisualStudioContextSource.GetWorkspaceRoot(out var capturedSolution);
            await Task.Run(() => new WorkspaceGuard(capturedRoot), cancellationToken).ConfigureAwait(false);
            return new VisualStudioReadTools(taskFactory, capturedRoot, capturedSolution);
        }
        catch (ProviderException) { throw new ProviderException("workspace_unavailable"); }
        catch (ToolException) { throw new ProviderException("path_unavailable"); }
        catch (Exception exception) when (IsWorkspaceException(exception)) { throw new ProviderException("path_unavailable"); }
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            if (root is null)
            {
                var captured = await CaptureAsync(cancellationToken).ConfigureAwait(false);
                return await captured.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
            await ValidateAsync(cancellationToken);
            var result = await Task.Run(async () =>
            {
                var guard = new WorkspaceGuard(root);
                using var approvals = new ApprovalBroker(guard, this);
                return await new WorkspaceReadTools(guard, approvals).ExecuteAsync(ChatMode.Ask, invocation, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            await ValidateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (ToolException exception) { return Error(invocation, exception.Code); }
        catch (ProviderException exception) { return Error(invocation, exception.Code); }
        catch (Exception exception) when (IsWorkspaceException(exception)) { return Error(invocation, "path_unavailable"); }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            var currentRoot = VisualStudioContextSource.GetWorkspaceRoot(out var currentSolution);
            if (!string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(solutionPath, currentSolution, StringComparison.OrdinalIgnoreCase))
                throw new ProviderException("context_stale");
        }
        catch (ProviderException) { throw new ProviderException("context_stale"); }
    }

    private static bool IsWorkspaceException(Exception exception) => exception is COMException || exception is IOException ||
        exception is UnauthorizedAccessException || exception is SecurityException || exception is ArgumentException ||
        exception is InvalidOperationException || exception is NotSupportedException;

    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) => Task.FromResult(ApprovalResponse.Deny);

    private static ToolResult Error(ToolInvocation invocation, string code) => new(invocation.Id, true, System.Text.Json.JsonSerializer.Serialize(new { code }));
}
