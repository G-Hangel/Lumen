using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioWriteTools : IWriteToolExecutor, IApprovalPrompt, IDisposable
{
    private readonly JoinableTaskFactory taskFactory;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> prompt;
    private string? root;
    private string? solutionPath;
    private readonly EditJournal journal;
    private readonly List<BufferChange> buffers = new();
    private readonly List<TurnBoundary> turns = new();

    private sealed class TurnBoundary
    {
        internal int JournalCount;
        internal int BufferCount;
        internal bool Automatic;
    }
    private ApprovalBroker? approvals;
    private string? approvalsRoot;
    private bool changingBuffers;
    private bool disposed;

    private sealed class BufferChange : ITextUndoPrimitive
    {
        internal string Path = string.Empty;
        internal string Before = string.Empty;
        internal string After = string.Empty;
        internal ITextBuffer Buffer = null!;
        internal ITextUndoHistory History = null!;
        internal int BeforeVersion;
        internal int AfterVersion;
        internal bool Applied = true;
        internal bool Conflict;
        internal EventHandler<TextUndoTransactionCompletedEventArgs> Completed = null!;
        public ITextUndoTransaction Parent { get; set; } = null!;
        public bool CanUndo => Applied;
        public bool CanRedo => !Applied;
        public void Undo() => Applied = false;
        public void Do() => Applied = true;
        public bool CanMerge(ITextUndoPrimitive older) => false;
        public ITextUndoPrimitive Merge(ITextUndoPrimitive older) => throw new NotSupportedException();
        internal void Release()
        {
            History.UndoTransactionCompleted -= Completed;
            Path = Before = After = string.Empty;
            Completed = null!;
            Buffer = null!;
            History = null!;
        }
    }

    internal VisualStudioWriteTools(JoinableTaskFactory taskFactory, Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>> prompt)
    {
        this.taskFactory = taskFactory;
        this.prompt = prompt;
        journal = new EditJournal(commitTarget: CommitDiskTarget);
    }

    private void CommitDiskTarget(string path, Action commit) => taskFactory.Run(async () =>
    {
        await taskFactory.SwitchToMainThreadAsync();
        if (disposed) throw new ToolException("context_stale");
        var currentRoot = VisualStudioContextSource.GetWorkspaceRoot(out var currentSolution);
        if (!string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase) || !string.Equals(solutionPath, currentSolution, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("context_stale");
        if (FindOpenBuffer(path) is not null) throw new ToolException("edit_conflict");
        commit();
    });
    public IReadOnlyList<ToolSpec> Specs => WriteToolCatalog.Specs;
    public int PendingChanges => journal.Count + buffers.Count;

    public async Task<IWriteToolExecutor> CaptureAsync(CancellationToken token)
    {
        await taskFactory.SwitchToMainThreadAsync(token);
        try
        {
            var capturedRoot = VisualStudioContextSource.GetWorkspaceRoot(out var capturedSolution);
            if (PendingChanges > 0 && (!string.Equals(root, capturedRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(solutionPath, capturedSolution, StringComparison.OrdinalIgnoreCase))) throw new ProviderException("context_stale");
            await Task.Run(() => new WorkspaceGuard(capturedRoot), token).ConfigureAwait(false);
            if (PendingChanges == 0 && (!string.Equals(root, capturedRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(solutionPath, capturedSolution, StringComparison.OrdinalIgnoreCase))) turns.Clear();
            root = capturedRoot;
            solutionPath = capturedSolution;
            return this;
        }
        catch (ProviderException exception) when (exception.Code != "context_stale") { throw new ProviderException("workspace_unavailable"); }
        catch (ToolException) { throw new ProviderException("path_unavailable"); }
        catch (Exception exception) when (IsWorkspaceException(exception)) { throw new ProviderException("path_unavailable"); }
    }

    public async Task BeginTurnAsync(bool automatic, CancellationToken token)
    {
        await ValidateAsync(token);
        await taskFactory.SwitchToMainThreadAsync(token);
        if (disposed) throw new ProviderException("context_stale");
        if (turns.Count > 0)
        {
            var last = turns[turns.Count - 1];
            // Reuse an empty boundary without merging across an intervening automatic turn.
            if (last.JournalCount == journal.Count && last.BufferCount == buffers.Count)
            {
                last.Automatic = automatic;
                return;
            }
            if (!automatic && !last.Automatic) return;
        }
        turns.Add(new TurnBoundary { JournalCount = journal.Count, BufferCount = buffers.Count, Automatic = automatic });
    }

    private void RemoveEmptyTurns()
    {
        while (turns.Count > 0 && turns[turns.Count - 1].JournalCount == journal.Count && turns[turns.Count - 1].BufferCount == buffers.Count)
            turns.RemoveAt(turns.Count - 1);
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
    {
        try
        {
            var isDiff = invocation.Name == "apply_diff";
            var diffArguments = isDiff ? WriteToolCatalog.ValidateDiff(invocation) : default;
            var arguments = isDiff ? (Path: diffArguments.Path, Content: string.Empty) : WriteToolCatalog.Validate(invocation);
            await taskFactory.SwitchToMainThreadAsync(token);
            if (root is null) await CaptureAsync(token);
            await ValidateAsync(token);
            if (PendingChanges >= 64) throw new ProviderException("tool_budget_exceeded");
            var guard = new WorkspaceGuard(root!);
            var path = guard.ResolveWriteTarget(arguments.Path);
            var buffer = FindOpenBuffer(path);
            if (buffer is not null)
            {
                if (invocation.Name == "create_file") return Error(invocation, "file_exists");
                if (journal.Records.Any(record => string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase))) return Error(invocation, "edit_conflict");
                if (!guard.Contains(path))
                {
                    // Open buffers can live outside the workspace; those writes need approval too.
                    await GetApprovals(guard).AuthorizeWriteAsync(ChatMode.Agent, invocation, arguments.Path, token).ConfigureAwait(false);
                    await taskFactory.SwitchToMainThreadAsync(token);
                    await ValidateAsync(token);
                    path = guard.ResolveWriteTarget(arguments.Path);
                    buffer = FindOpenBuffer(path);
                    if (buffer is null || journal.Records.Any(record => string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase))) return Error(invocation, "edit_conflict");
                }
                var snapshot = buffer.CurrentSnapshot;
                if (isDiff && snapshot.Length > 65536) return Error(invocation, "file_too_large");
                var content = isDiff ? WriteToolCatalog.ApplyDiff(snapshot.GetText(), diffArguments.Hash, diffArguments.Replacements) : arguments.Content;
                if (!ReferenceEquals(snapshot, buffer.CurrentSnapshot)) return Error(invocation, "edit_conflict");
                return EditBuffer(invocation, path, buffer, content, token);
            }
            if (buffers.Any(change => string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase))) return Error(invocation, "edit_conflict");
            var diskApprovals = GetApprovals(guard);
            return await Task.Run(() => new WorkspaceWriteTools(guard, diskApprovals, journal).ExecuteAsync(ChatMode.Agent, invocation, token), token).ConfigureAwait(false);
        }
        catch (ToolException exception) { return Error(invocation, exception.Code); }
        catch (ProviderException exception) { return Error(invocation, exception.Code); }
        catch (Exception exception) when (IsWorkspaceException(exception)) { return Error(invocation, "path_unavailable"); }
    }

    private ToolResult EditBuffer(ToolInvocation invocation, string path, ITextBuffer buffer, string after, CancellationToken token)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var snapshot = buffer.CurrentSnapshot;
        if (snapshot.Length > 65536 || after.Length > 65536 || buffers.Sum(change => change.Before.Length + change.After.Length) + snapshot.Length + after.Length > 1048576)
            return Error(invocation, "file_too_large");
        if (!buffer.CheckEditAccess() || buffer.EditInProgress || buffer.IsReadOnly(new Span(0, snapshot.Length))) return Error(invocation, "edit_conflict");
        var before = snapshot.GetText();
        var diff = WorkspaceWriteTools.BuildDiff(before, after);
        if (before == after) return new ToolResult(invocation.Id, false, JsonSerializer.Serialize(new { path = Relative(path), edited = false, buffer = true, diff }));
        var component = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel ?? throw new ProviderException("path_unavailable");
        var registry = component.GetService<ITextUndoHistoryRegistry>();
        if (!registry.TryGetHistory(buffer, out var history) || history.CurrentTransaction is not null) return Error(invocation, "edit_conflict");
        if (buffers.Any(change => ReferenceEquals(change.Buffer, buffer) && (change.Conflict || !change.Applied))) return Error(invocation, "edit_conflict");
        changingBuffers = true;
        try
        {
            using var transaction = history.CreateTransaction("Lumen");
            using var edit = buffer.CreateEdit();
            if (!edit.Replace(new Span(0, snapshot.Length), after)) { transaction.Cancel(); return Error(invocation, "edit_conflict"); }
            token.ThrowIfCancellationRequested();
            edit.Apply();
            if (edit.Canceled) { transaction.Cancel(); return Error(invocation, "edit_conflict"); }
            var change = new BufferChange
            {
                Path = path, Before = before, After = after, Buffer = buffer, History = history,
                BeforeVersion = snapshot.Version.ReiteratedVersionNumber,
                AfterVersion = buffer.CurrentSnapshot.Version.ReiteratedVersionNumber
            };
            // VS native histories do not expose their stacks; a primitive tracks owned Undo/Redo identity.
            transaction.AddUndo(change);
            transaction.Complete();
            change.Completed = (_, _) => { if (!changingBuffers) change.Conflict = true; };
            history.UndoTransactionCompleted += change.Completed;
            buffers.Add(change);
        }
        finally { changingBuffers = false; }
        return new ToolResult(invocation.Id, false, JsonSerializer.Serialize(new { path = Relative(path), edited = true, buffer = true, diff }));
    }

    public async Task UndoTurnAsync(CancellationToken token)
    {
        if (PendingChanges == 0) return;
        try
        {
            await ValidateAsync(token);
            await taskFactory.SwitchToMainThreadAsync(token);
            RemoveEmptyTurns();
            var turn = turns.Count == 0 ? new TurnBoundary() : turns[turns.Count - 1];
            foreach (var record in journal.Records.Skip(turn.JournalCount))
                if (FindOpenBuffer(record.Path) is not null) throw new ProviderException("undo_conflict");
            ValidateBufferUndo(turn.BufferCount);
            await journal.UndoTurnAsync(turn.JournalCount, token).ConfigureAwait(false);
            await taskFactory.SwitchToMainThreadAsync(CancellationToken.None);
            ValidateBufferUndo(turn.BufferCount);
            changingBuffers = true;
            try
            {
                for (var index = buffers.Count - 1; index >= turn.BufferCount; index--)
                {
                    var change = buffers[index];
                    if (!change.Applied || change.Buffer.CurrentSnapshot.Version.ReiteratedVersionNumber != change.AfterVersion ||
                        change.Buffer.CurrentSnapshot.GetText() != change.After) throw new ProviderException("undo_conflict");
                    change.History.Undo(1);
                    if (change.Applied || change.Buffer.CurrentSnapshot.Version.ReiteratedVersionNumber != change.BeforeVersion ||
                        change.Buffer.CurrentSnapshot.GetText() != change.Before) throw new ProviderException("undo_failed");
                    change.Release();
                    buffers.RemoveAt(index);
                }
            }
            finally { changingBuffers = false; }
            RemoveEmptyTurns();
        }
        catch (ToolException exception)
        {
            ActivityLog.LogError("Lumen", "Undo guard failure: " + exception.Code);
            throw new ProviderException(exception.Code == "undo_conflict" ? "undo_conflict" : "undo_failed");
        }
        catch (Exception exception) when (IsWorkspaceException(exception))
        {
            ActivityLog.LogError("Lumen", "Undo failure: " + exception.GetType().FullName + "; HRESULT=" + exception.HResult.ToString("X8") + "; " + exception.StackTrace);
            throw new ProviderException("undo_failed");
        }
    }

    private void ValidateBufferUndo(int retainBuffers)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var guard = new WorkspaceGuard(root!);
        foreach (var group in buffers.Skip(retainBuffers).GroupBy(change => change.Buffer))
        {
            var changes = group.Reverse().ToArray();
            foreach (var change in changes)
                if (guard.ResolveWriteTarget(change.Path) != change.Path || !ReferenceEquals(FindOpenBuffer(change.Path), change.Buffer)) throw new ProviderException("undo_conflict");
            if (changes.Any(change => change.Conflict || !change.Applied) || group.Key.CurrentSnapshot.GetText() != changes[0].After ||
                group.Key.CurrentSnapshot.Version.ReiteratedVersionNumber != changes[0].AfterVersion || changes[0].History.CurrentTransaction is not null)
                throw new ProviderException("undo_conflict");
            for (var index = 0; index + 1 < changes.Length; index++)
                if (changes[index].BeforeVersion != changes[index + 1].AfterVersion || changes[index].Before != changes[index + 1].After)
                    throw new ProviderException("undo_conflict");
        }
    }

    internal static ITextBuffer? FindOpenBuffer(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable ?? throw new ProviderException("path_unavailable");
        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out var pointer, out _));
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var adapter = Marshal.GetObjectForIUnknown(pointer) as IVsTextBuffer ?? throw new ProviderException("edit_conflict");
            var component = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel ?? throw new ProviderException("path_unavailable");
            return component.GetService<IVsEditorAdaptersFactoryService>().GetDocumentBuffer(adapter) ?? throw new ProviderException("edit_conflict");
        }
        finally { Marshal.Release(pointer); }
    }

    private async Task ValidateAsync(CancellationToken token)
    {
        await taskFactory.SwitchToMainThreadAsync(token);
        var currentRoot = VisualStudioContextSource.GetWorkspaceRoot(out var currentSolution);
        if (!string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase) || !string.Equals(solutionPath, currentSolution, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("context_stale");
    }

    private string Relative(string path)
    {
        var guard = new WorkspaceGuard(root!);
        var canonical = guard.ResolveWriteTarget(path);
        return !guard.Contains(canonical) ? canonical.Replace('\\', '/') :
            canonical.Length == guard.Root.Length ? "." : canonical.Substring(guard.Root.TrimEnd('\\').Length + 1).Replace('\\', '/');
    }
    private static bool IsWorkspaceException(Exception exception) => exception is COMException || exception is IOException || exception is UnauthorizedAccessException ||
        exception is SecurityException || exception is ArgumentException || exception is InvalidOperationException || exception is NotSupportedException;
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        disposed = true;
        approvals?.Dispose();
        approvals = null;
        foreach (var change in buffers) change.Release();
        buffers.Clear();
        turns.Clear();
    }

    private ApprovalBroker GetApprovals(WorkspaceGuard guard)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed) throw new ToolException("context_stale");
        if (approvals is null || !string.Equals(approvalsRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            approvals?.Dispose();
            approvals = new ApprovalBroker(guard, this);
            approvalsRoot = root;
        }
        return approvals;
    }

    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken token) => prompt(request, token);
    private static ToolResult Error(ToolInvocation invocation, string code) => new(invocation.Id, true, JsonSerializer.Serialize(new { code }));
}
