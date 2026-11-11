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
using Microsoft.VisualStudio.Shell;
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
            if (invocation.Name == "get_errors" || invocation.Name == "get_output_logs")
            {
                ReadToolCatalog.ValidateDiagnostic(invocation);
                await ValidateAsync(cancellationToken);
                string content;
                if (invocation.Name == "get_output_logs")
                {
                    var output = await new VisualStudioContextSource(taskFactory).CaptureAsync("output", null, cancellationToken);
                    content = System.Text.Json.JsonSerializer.Serialize(new { pane = output.Label, text = output.Text });
                }
                else
                {
                    await taskFactory.SwitchToMainThreadAsync(cancellationToken);
                    var dte = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as EnvDTE80.DTE2
                        ?? throw new ProviderException("context_unavailable");
                    var items = dte.ToolWindows.ErrorList.ErrorItems;
                    var errors = new List<object>();
                    var count = items.Count;
                    var guard = new WorkspaceGuard(root);
                    for (var index = 1; index <= Math.Min(count, 256); index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = items.Item(index);
                        string? file = item.FileName;
                        if (!string.IsNullOrEmpty(file))
                        {
                            try { file = guard.Resolve(file); }
                            catch (ToolException) { continue; }
                            file = file.Substring(guard.Root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                        }
                        var description = item.Description ?? string.Empty;
                        if (description.Length > 1024) description = description.Substring(0, 1024);
                        errors.Add(new { description, file, line = item.Line, column = item.Column, severity = item.ErrorLevel.ToString() });
                    }
                    content = System.Text.Json.JsonSerializer.Serialize(new { errors, truncated = count > 256 });
                }
                if (content.Length > 65536) throw new ToolException("output_too_large");
                await ValidateAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new ToolResult(invocation.Id, false, content);
            }
            if (invocation.Name == "get_edit_snapshot")
            {
                var requestedPath = ReadToolCatalog.ValidateEditSnapshot(invocation);
                await ValidateAsync(cancellationToken);
                var guard = new WorkspaceGuard(root);
                var path = guard.Resolve(requestedPath);
                await taskFactory.SwitchToMainThreadAsync(cancellationToken);
                var buffer = VisualStudioWriteTools.FindOpenBuffer(path);
                if (buffer is not null)
                {
                    var snapshot = buffer.CurrentSnapshot;
                    if (snapshot.Length > 65536) throw new ToolException("output_too_large");
                    var text = snapshot.GetText();
                    if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot)) throw new ToolException("edit_conflict");
                    var relative = path.Length == guard.Root.Length ? "." : path.Substring(guard.Root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                    var snapshotResult = WorkspaceReadTools.CreateEditSnapshotResult(invocation.Id, relative, text, "buffer");
                    await ValidateAsync(cancellationToken);
                    if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot)) throw new ToolException("edit_conflict");
                    cancellationToken.ThrowIfCancellationRequested();
                    return snapshotResult;
                }
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
