using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioShellTools : IShellToolExecutor, IApprovalPrompt, IVsSolutionEvents, IDisposable
{
    private static readonly Guid OutputPaneGuid = new("97B1E2A4-5C3D-4E8F-B6A1-0D2C4E6F8A1B");
    private readonly JoinableTaskFactory taskFactory;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> prompt;
    private readonly VisualStudioShellTools? owner;
    private readonly string? root;
    private readonly string? solutionPath;
    private readonly object consentGate = new object();
    private ApprovalBroker? broker;
    private string? brokerRoot;
    private string? brokerSolution;
    private IVsSolution? solution;
    private uint solutionEventsCookie;
    private long solutionGeneration;
    private readonly long capturedGeneration;
    private bool disposed;

    internal VisualStudioShellTools(JoinableTaskFactory taskFactory, Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> prompt)
    {
        this.taskFactory = taskFactory;
        this.prompt = prompt;
    }

    private VisualStudioShellTools(JoinableTaskFactory taskFactory, Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> prompt, VisualStudioShellTools owner, string root, string solutionPath, long capturedGeneration)
    {
        this.taskFactory = taskFactory;
        this.prompt = prompt;
        this.owner = owner;
        this.root = root;
        this.solutionPath = solutionPath;
        this.capturedGeneration = capturedGeneration;
    }

    public IReadOnlyList<ToolSpec> Specs => CommandToolCatalog.Specs;

    public async Task<IShellToolExecutor> CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await taskFactory.SwitchToMainThreadAsync(cancellationToken);
            var shared = owner ?? this;
            shared.EnsureSolutionEvents();
            var capturedRoot = VisualStudioContextSource.GetWorkspaceRoot(out var capturedSolution);
            long generation;
            lock (shared.consentGate)
            {
                if (shared.disposed) throw new ToolException("context_stale");
                generation = shared.solutionGeneration;
            }
            await Task.Run(() => new WorkspaceGuard(capturedRoot), cancellationToken).ConfigureAwait(false);
            return new VisualStudioShellTools(taskFactory, prompt, shared, capturedRoot, capturedSolution, generation);
        }
        catch (ProviderException) { throw new ProviderException("workspace_unavailable"); }
        catch (ToolException exception) { throw new ProviderException(exception.Code == "context_stale" ? "context_stale" : "path_unavailable"); }
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
            var shared = owner ?? this;
            ApprovalBroker approvals;
            // Session opt-in ("always allow") must survive across turns, so captures
            // share one broker per workspace; a new workspace revokes the consent.
            lock (shared.consentGate)
            {
                if (shared.disposed || capturedGeneration != shared.solutionGeneration) throw new ToolException("context_stale");
                if (shared.broker is null || !string.Equals(shared.brokerRoot, root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(shared.brokerSolution, solutionPath, StringComparison.OrdinalIgnoreCase))
                {
                    shared.broker?.Dispose();
                    shared.broker = new ApprovalBroker(new WorkspaceGuard(root), shared);
                    shared.brokerRoot = root;
                    shared.brokerSolution = solutionPath;
                }
                approvals = shared.broker;
            }
            var result = await Task.Run(() => new WorkspaceShellTools(new WorkspaceGuard(root), approvals, ValidateAsync).ExecuteAsync(ChatMode.Agent, invocation, cancellationToken), cancellationToken).ConfigureAwait(false);
            await ValidateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ShowOutputAsync(invocation, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (ToolException exception) { return Error(invocation, exception.Code); }
        catch (ProviderException exception) { return Error(invocation, exception.Code); }
        catch (ObjectDisposedException) { return Error(invocation, "context_stale"); }
        catch (Exception exception) when (IsWorkspaceException(exception)) { return Error(invocation, "path_unavailable"); }
    }

    private async Task ShowOutputAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken)
    {
        if (result.IsError) return;
        string summary;
        try
        {
            using var json = JsonDocument.Parse(result.Content);
            if (!json.RootElement.TryGetProperty("exitCode", out var exitCode) || !exitCode.TryGetInt32(out var exit)) return;
            var (command, args, _, _) = CommandToolCatalog.Validate(invocation);
            var builder = new StringBuilder();
            builder.Append("Lumen command: ").Append(command);
            foreach (var argument in args) builder.Append(' ').Append(argument);
            builder.AppendLine();
            builder.Append("exit ").Append(exit).AppendLine();
            var stdout = json.RootElement.TryGetProperty("stdout", out var stdoutValue) && stdoutValue.ValueKind == JsonValueKind.String ? stdoutValue.GetString() : null;
            var stderr = json.RootElement.TryGetProperty("stderr", out var stderrValue) && stderrValue.ValueKind == JsonValueKind.String ? stderrValue.GetString() : null;
            if (!string.IsNullOrEmpty(stdout)) builder.Append(stdout).AppendLine();
            if (!string.IsNullOrEmpty(stderr)) builder.Append(stderr).AppendLine();
            summary = builder.ToString();
            if (summary.Length > 16384) summary = summary.Substring(0, 16381) + "...";
        }
        catch (JsonException) { return; }
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow output) return;
            var guid = OutputPaneGuid;
            if (!Microsoft.VisualStudio.ErrorHandler.Succeeded(output.GetPane(ref guid, out var pane)) || pane is null)
            {
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(output.CreatePane(ref guid, "Lumen", 1, 0));
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(output.GetPane(ref guid, out pane));
            }
            if (pane is not null) pane.OutputStringThreadSafe(summary);
        }
        catch (COMException) { }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            var shared = owner ?? this;
            lock (shared.consentGate)
                if (shared.disposed || capturedGeneration != shared.solutionGeneration) throw new ProviderException("context_stale");
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

    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) => prompt(request, cancellationToken);

    private void EnsureSolutionEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        lock (consentGate)
            if (disposed) throw new ToolException("context_stale");
        if (solution is not null) return;
        var service = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution ?? throw new ToolException("context_stale");
        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(service.AdviseSolutionEvents(this, out solutionEventsCookie));
        solution = service;
    }

    private void RevokeConsent()
    {
        lock (consentGate)
        {
            // Paths alone cannot distinguish closing A, opening B, then reopening A.
            solutionGeneration++;
            broker?.Dispose();
            broker = null;
            brokerRoot = brokerSolution = null;
        }
    }

    public int OnAfterOpenSolution(object reserved, int newSolution) { RevokeConsent(); return Microsoft.VisualStudio.VSConstants.S_OK; }
    public int OnBeforeCloseSolution(object reserved) { RevokeConsent(); return Microsoft.VisualStudio.VSConstants.S_OK; }
    public int OnAfterCloseSolution(object reserved) { RevokeConsent(); return Microsoft.VisualStudio.VSConstants.S_OK; }
    public int OnAfterOpenProject(IVsHierarchy hierarchy, int added) => Microsoft.VisualStudio.VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy hierarchy, int removing, ref int cancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy hierarchy, int removed) => Microsoft.VisualStudio.VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy stubHierarchy, IVsHierarchy realHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy realHierarchy, ref int cancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy realHierarchy, IVsHierarchy stubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    public int OnQueryCloseSolution(object reserved, ref int cancel) => Microsoft.VisualStudio.VSConstants.S_OK;

    public void Dispose()
    {
        lock (consentGate)
        {
            if (disposed) return;
            disposed = true;
            RevokeConsent();
        }
        taskFactory.Run(async () =>
        {
            await taskFactory.SwitchToMainThreadAsync();
            if (solution is null) return;
            try { Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(solution.UnadviseSolutionEvents(solutionEventsCookie)); }
            finally { solution = null; solutionEventsCookie = 0; }
        });
    }

    private static ToolResult Error(ToolInvocation invocation, string code) => new(invocation.Id, true, JsonSerializer.Serialize(new { code }));
}
