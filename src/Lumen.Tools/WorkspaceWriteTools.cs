using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public sealed class WorkspaceWriteTools
{
    private const int MaxFileChars = 1048576;
    private const int MaxFileBytes = 1048576;
    private const int MaxDiffLines = 1024;
    private const int MaxChanges = 64;
    private const int MaxChangeText = 96;
    private const int MaxOutput = 65536;
    private readonly WorkspaceGuard workspace;
    private readonly ApprovalBroker approvals;
    private readonly EditJournal journal;

    public WorkspaceWriteTools(WorkspaceGuard workspace, ApprovalBroker approvals, EditJournal journal)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    internal Action? BeforeCommit { get; set; }
    internal Action? AfterCommit { get; set; }

    public Task<ToolResult> ExecuteAsync(ChatMode mode, ToolInvocation invocation, CancellationToken cancellationToken) =>
        ExecuteAsyncCore(mode, invocation, cancellationToken);

    private async Task<ToolResult> ExecuteAsyncCore(ChatMode mode, ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = WriteToolCatalog.Validate(invocation);
            var requestedPath = arguments.Path;
            var path = await approvals.AuthorizeWriteAsync(mode, invocation, requestedPath, cancellationToken).ConfigureAwait(false);
            object result;
            if (invocation.Name == "create_file") result = CreateFile(path, arguments.Content, cancellationToken);
            else result = EditFile(path, arguments.Content, cancellationToken);
            var content = JsonSerializer.Serialize(result);
            if (content.Length > MaxOutput)
            {
                using var document = JsonDocument.Parse(content);
                var diff = document.RootElement.GetProperty("diff");
                var displayPath = Relative(path);
                var cut = Math.Min(displayPath.Length, 2048);
                if (char.IsHighSurrogate(displayPath[cut - 1])) cut--;
                content = JsonSerializer.Serialize(new
                {
                    path = displayPath.Substring(0, cut), pathTruncated = cut != displayPath.Length,
                    created = invocation.Name == "create_file", edited = invocation.Name == "edit_file", truncated = true,
                    diff = new
                    {
                        truncated = true, tooLarge = true, changes = Array.Empty<object>(),
                        linesBefore = diff.GetProperty("linesBefore").GetInt32(),
                        linesAfter = diff.GetProperty("linesAfter").GetInt32()
                    }
                });
            }
            return new ToolResult(invocation.Id, false, content);
        }
        catch (ToolException exception) { return Error(invocation, exception.Code); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException || exception is ArgumentException)
        {
            return Error(invocation, "invalid_arguments");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            return Error(invocation, "path_unavailable");
        }
    }

    private object CreateFile(string path, string content, CancellationToken cancellationToken)
    {
        if (TooLarge(content)) throw new ToolException("file_too_large");
        if (File.Exists(path)) throw new ToolException("file_exists");
        var record = new EditRecord(workspace, path, null, new UTF8Encoding(false).GetBytes(content));
        var result = new { path = Relative(path), created = true, diff = BuildDiff(string.Empty, content) };
        journal.Commit(record, () => WriteAtomic(record, record.After, false, cancellationToken, BeforeCommit, journal.CommitTarget));
        AfterCommit?.Invoke();
        return result;
    }

    private object EditFile(string path, string content, CancellationToken cancellationToken)
    {
        if (TooLarge(content)) throw new ToolException("file_too_large");
        if (!File.Exists(path)) throw new ToolException("path_unavailable");
        var priorBytes = ReadAllBytes(path, cancellationToken);
        var priorText = DecodeText(priorBytes);
        var record = new EditRecord(workspace, path, priorBytes, new UTF8Encoding(false).GetBytes(content));
        var result = new { path = Relative(path), edited = true, diff = BuildDiff(priorText, content) };
        journal.Commit(record, () => WriteAtomic(record, record.After, true, cancellationToken, () =>
        {
            BeforeCommit?.Invoke();
            if (!EditJournal.Equal(ReadAllBytes(path, cancellationToken), priorBytes)) throw new ToolException("edit_conflict");
        }, journal.CommitTarget));
        AfterCommit?.Invoke();
        return result;
    }

    // Cap bytes, not just chars: a 1 MiB string of CJK is 3 MiB of UTF-8, which
    // would then be unreadable by read_file. A file we just wrote must stay
    // within the read cap.
    private static bool TooLarge(string content) =>
        content.Length > MaxFileChars || new UTF8Encoding(false).GetByteCount(content) > MaxFileBytes;

    // Write to a sibling temp file first; a cancelled or failing write never
    // touches the target. Replace is atomic on NTFS for an existing file; for a
    // new file Move refuses an existing target, which is the create_file contract.
    internal static void WriteAtomic(EditRecord record, byte[] bytes, bool overwrite, CancellationToken cancellationToken, Action? beforeCommit = null, Action<string, Action>? commitTarget = null)
    {
        record.Revalidate();
        cancellationToken.ThrowIfCancellationRequested();
        var path = record.Path;
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + ".lumen-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = Math.Min(4096, bytes.Length - offset);
                    stream.Write(bytes, offset, chunk);
                    offset += chunk;
                }
            }
            void Commit()
            {
                beforeCommit?.Invoke();
                record.Revalidate();
                cancellationToken.ThrowIfCancellationRequested();
                if (overwrite) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            if (commitTarget is null) Commit();
            else commitTarget(path, Commit);
        }
        catch
        {
            try
            {
                record.Revalidate();
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception exception) when (exception is ToolException || exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException) { }
            throw;
        }
    }

    private byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        workspace.ResolveWriteTarget(path);
        var guard = workspace.Contains(path) ? workspace : new WorkspaceGuard(Path.GetDirectoryName(path)!);
        using var stream = guard.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new ToolException("file_too_large");
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length + count > MaxFileBytes) throw new ToolException("file_too_large");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }

    // Mirrors WorkspaceReadTools: reject anything that is not UTF-8/UTF-16 text, so
    // edit_file can never silently corrupt a binary file into decoded text.
    private static string DecodeText(byte[] content)
    {
        var offset = 0;
        Encoding encoding = new UTF8Encoding(false, true);
        if (content.Length >= 4 && ((content[0] == 0xff && content[1] == 0xfe && content[2] == 0 && content[3] == 0) ||
            (content[0] == 0 && content[1] == 0 && content[2] == 0xfe && content[3] == 0xff))) throw new ToolException("binary_file");
        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf) offset = 3;
        else if (content.Length >= 2 && content[0] == 0xff && content[1] == 0xfe) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (content.Length >= 2 && content[0] == 0xfe && content[1] == 0xff) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        string text;
        try { text = encoding.GetString(content, offset, content.Length - offset); }
        catch (DecoderFallbackException) { throw new ToolException("binary_file"); }
        foreach (var character in text)
            if (char.IsControl(character) && character != '\r' && character != '\n' && character != '\t') throw new ToolException("binary_file");
        return text;
    }

    public static object BuildDiff(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        if (oldLines.Length > MaxDiffLines || newLines.Length > MaxDiffLines)
            return new { truncated = true, tooLarge = true, changes = Array.Empty<object>(), linesBefore = oldLines.Length, linesAfter = newLines.Length };
        var n = oldLines.Length;
        var m = newLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var changes = new List<object>();
        var truncated = false;
        var a = 0;
        var b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b]) { a++; b++; }
            else if (lcs[a + 1, b] >= lcs[a, b + 1]) { truncated |= AddChange(changes, "remove", oldLines[a]); a++; }
            else { truncated |= AddChange(changes, "add", newLines[b]); b++; }
        }
        while (a < n) { truncated |= AddChange(changes, "remove", oldLines[a]); a++; }
        while (b < m) { truncated |= AddChange(changes, "add", newLines[b]); b++; }
        return new { truncated, changes, linesBefore = oldLines.Length, linesAfter = newLines.Length };
    }

    private static bool AddChange(List<object> changes, string op, string text)
    {
        if (changes.Count < MaxChanges)
        {
            var ending = text.EndsWith("\r\n", StringComparison.Ordinal) ? "CRLF" :
                text.EndsWith("\n", StringComparison.Ordinal) ? "LF" :
                text.EndsWith("\r", StringComparison.Ordinal) ? "CR" : "none";
            var line = text.Substring(0, text.Length - (ending == "CRLF" ? 2 : ending == "none" ? 0 : 1));
            changes.Add(new { op, text = Truncate(line), ending });
            return line.Length > MaxChangeText;
        }
        // A change was present but dropped: that is what "truncated" means.
        return true;
    }

    private static string Truncate(string line)
    {
        if (line.Length <= MaxChangeText) return line;
        var cut = MaxChangeText - 3;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line.Substring(0, cut) + "...";
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\r' && text[index] != '\n') continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            lines.Add(text.Substring(start, index + 1 - start));
            start = index + 1;
        }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines.ToArray();
    }

    private string Relative(string path) => !workspace.Contains(path) ? path.Replace('\\', '/') :
        path.Length == workspace.Root.Length ? "." :
        path.Substring(workspace.Root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');

    private static ToolResult Error(ToolInvocation invocation, string code) => new(invocation.Id, true, JsonSerializer.Serialize(new { code }));
}
