using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Tools;

public sealed class EditRecord
{
    private readonly WorkspaceGuard workspace;
    private readonly WorkspaceGuard targetGuard;
    private readonly bool internalTarget;
    private readonly byte[]? before;
    private readonly byte[] after;

    internal EditRecord(WorkspaceGuard workspace, string path, byte[]? priorBytes, byte[] afterBytes)
    {
        this.workspace = workspace;
        Path = workspace.ResolveWriteTarget(path);
        internalTarget = workspace.Contains(Path);
        // An approved external path remains tied to its original workspace and parent,
        // rather than acquiring authority from an unguarded absolute path at undo time.
        targetGuard = internalTarget ? workspace : new WorkspaceGuard(System.IO.Path.GetDirectoryName(Path)!);
        before = priorBytes == null ? null : (byte[])priorBytes.Clone();
        after = (byte[])afterBytes.Clone();
    }

    public string Path { get; }
    public string WorkspaceRoot => workspace.Root;
    public bool ExistedBefore => before != null;
    public byte[]? PriorBytes => before == null ? null : (byte[])before.Clone();
    public byte[] AfterBytes => (byte[])after.Clone();
    internal byte[]? Before => before;
    internal byte[] After => after;
    internal long ByteCount => (before?.LongLength ?? 0) + after.LongLength;

    internal void Revalidate()
    {
        workspace.ResolveWriteTarget(Path);
        if (internalTarget) workspace.Resolve(Path);
        targetGuard.Resolve(Path);
    }

    internal FileStream OpenRead()
    {
        Revalidate();
        return targetGuard.OpenRead(Path);
    }
}

public sealed class EditJournal
{
    private readonly List<EditRecord> records = new();
    private readonly object gate = new();
    private readonly int maxRecords;
    private readonly long maxBytes;
    private long bytes;
    private readonly Action<string, Action>? commitTarget;

    public EditJournal(int maxRecords = 128, long maxBytes = 16 * 1048576, Action<string, Action>? commitTarget = null)
    {
        if (maxRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.maxRecords = maxRecords;
        this.maxBytes = maxBytes;
        this.commitTarget = commitTarget;
    }

    internal void CommitTarget(string path, Action commit)
    {
        if (commitTarget is null) commit();
        else commitTarget(path, commit);
    }

    public int Count { get { lock (gate) return records.Count; } }
    public long Bytes { get { lock (gate) return bytes; } }
    public IReadOnlyList<EditRecord> Records { get { lock (gate) return records.ToArray(); } }
    internal Action<EditRecord>? BeforeUndoCommit { get; set; }

    internal void Commit(EditRecord record, Action write)
    {
        lock (gate)
        {
            if (records.Count >= maxRecords || record.ByteCount > maxBytes - bytes)
                throw new ToolException("journal_limit");
            // Reserve storage before the irreversible commit. No cancellation or
            // fallible allocation belongs between successful write and its record.
            if (records.Capacity < records.Count + 1) records.Capacity = records.Count + 1;
            write();
            records.Add(record);
            bytes += record.ByteCount;
        }
    }

    public void Clear()
    {
        lock (gate) { records.Clear(); bytes = 0; }
    }

    public Task UndoTurnAsync(CancellationToken cancellationToken) => UndoTurnAsync(0, cancellationToken);

    public Task UndoTurnAsync(int retainRecords, CancellationToken cancellationToken) =>
        Task.Run(() => UndoTurn(retainRecords, cancellationToken), cancellationToken);

    private void UndoTurn(int retainRecords, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (retainRecords < 0 || retainRecords > records.Count) throw new ArgumentOutOfRangeException(nameof(retainRecords));
            try
            {
                // Simulate the reverse chain: repeated edits must compare against
                // the preceding record's after-state, not repeatedly against disk.
                var states = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
                for (var index = records.Count - 1; index >= retainRecords; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = records[index];
                    record.Revalidate();
                    if (!states.TryGetValue(record.Path, out var current))
                        current = ReadCurrent(record, cancellationToken);
                    if (!Equal(current, record.After)) throw new ToolException("undo_conflict");
                    states[record.Path] = record.Before;
                }

                while (records.Count > retainRecords)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = records[records.Count - 1];
                    if (record.ExistedBefore)
                        WorkspaceWriteTools.WriteAtomic(record, record.Before!, true, cancellationToken,
                            () => { BeforeUndoCommit?.Invoke(record); VerifyCurrent(record, cancellationToken); }, CommitTarget);
                    else
                    {
                        CommitTarget(record.Path, () =>
                        {
                            BeforeUndoCommit?.Invoke(record);
                            VerifyCurrent(record, cancellationToken);
                            record.Revalidate();
                            cancellationToken.ThrowIfCancellationRequested();
                            File.Delete(record.Path);
                        });
                    }
                    // A partial undo can be retried: completed records are no longer pending.
                    records.RemoveAt(records.Count - 1);
                    bytes -= record.ByteCount;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolException exception) when (exception.Code == "edit_conflict") { throw new ToolException("undo_conflict"); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
            {
                throw new ToolException("undo_failed");
            }
        }
    }

    private static void VerifyCurrent(EditRecord record, CancellationToken token)
    {
        if (!Equal(ReadCurrent(record, token), record.After)) throw new ToolException("undo_conflict");
    }

    private static byte[]? ReadCurrent(EditRecord record, CancellationToken token)
    {
        record.Revalidate();
        if (!File.Exists(record.Path)) return null;
        using var stream = record.OpenRead();
        if (stream.Length != record.After.LongLength) throw new ToolException("undo_conflict");
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (data.Length + count > record.After.LongLength) throw new ToolException("undo_conflict");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }

    internal static bool Equal(byte[]? left, byte[] right)
    {
        if (left == null || left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (left[index] != right[index]) return false;
        return true;
    }
}
