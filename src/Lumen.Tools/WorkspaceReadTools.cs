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

public sealed class WorkspaceReadTools
{
    private const int MaxFileBytes = 1048576;
    private const int MaxScanBytes = 4194304;
    private const int MaxOutput = 65536;
    private readonly WorkspaceGuard workspace;
    private readonly ApprovalBroker approvals;

    public WorkspaceReadTools(WorkspaceGuard workspace, ApprovalBroker approvals)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
    }

    public Task<ToolResult> ExecuteAsync(ChatMode mode, ToolInvocation invocation, CancellationToken cancellationToken) =>
        Task.Run(() => Execute(mode, invocation, cancellationToken), cancellationToken);

    private ToolResult Execute(ChatMode mode, ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = ReadToolCatalog.Validate(invocation);
            approvals.AuthorizeRead(mode, cancellationToken);
            var path = workspace.Resolve(arguments.GetProperty("path").GetString()!);
            object result;
            if (invocation.Name == "read_file") result = ReadFile(path, arguments, cancellationToken);
            else
            {
                var maximum = arguments.TryGetProperty("maxResults", out var limit) ? limit.GetInt32() : 100;
                result = invocation.Name == "list_dir" ? ListDirectory(path, maximum, cancellationToken) : Search(path, invocation.Name, arguments, maximum, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var content = JsonSerializer.Serialize(result);
            if (content.Length > MaxOutput) throw new ToolException("output_too_large");
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

    private static ToolResult Error(ToolInvocation invocation, string code) => new(invocation.Id, true, JsonSerializer.Serialize(new { code }));

    private object ReadFile(string path, JsonElement arguments, CancellationToken cancellationToken)
    {
        var text = ReadText(path, cancellationToken, out _);
        var first = arguments.TryGetProperty("startLine", out var start) ? start.GetInt32() : 1;
        var last = arguments.TryGetProperty("endLine", out var end) ? end.GetInt32() : int.MaxValue;
        var lines = new List<object>();
        var lineNumber = 0;
        var characters = 0;
        var truncated = false;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lineNumber < first || lineNumber > last || truncated) continue;
            if (lines.Count == 256 || characters + line.Length > 32768) { truncated = true; continue; }
            lines.Add(new { line = lineNumber, text = line });
            characters += line.Length;
        }
        return new { path = Relative(path), lines, totalLines = lineNumber, truncated };
    }

    private object ListDirectory(string path, int maximum, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) throw new ToolException("path_unavailable");
        var entries = new List<object>();
        var truncated = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = workspace.Resolve(entry);
            if (entries.Count == maximum) { truncated = true; break; }
            entries.Add(new { path = Relative(canonical), kind = Directory.Exists(canonical) ? "directory" : "file" });
        }
        workspace.Resolve(path);
        return new { path = Relative(path), entries, truncated };
    }

    private object Search(string path, string name, JsonElement arguments, int maximum, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) throw new ToolException("path_unavailable");
        var glob = arguments.TryGetProperty("glob", out var globValue) ? globValue.GetString()! : "**/*";
        var pattern = name == "grep" ? arguments.GetProperty("pattern").GetString()! : null;
        var results = new List<object>();
        var pending = new Stack<Tuple<string, int>>();
        pending.Push(Tuple.Create(path, 0));
        var visited = 0;
        var scannedBytes = 0;
        var skippedFiles = 0;
        var truncated = false;
        while (pending.Count != 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            workspace.Resolve(current.Item1);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Item1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > 1024) { truncated = true; break; }
                var canonical = workspace.Resolve(entry);
                if (Directory.Exists(canonical))
                {
                    if (current.Item2 == 32) { truncated = true; break; }
                    pending.Push(Tuple.Create(canonical, current.Item2 + 1));
                    continue;
                }
                var relative = canonical.Substring(path.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                if (!GlobMatches(glob.Replace('\\', '/'), relative)) continue;
                if (pattern == null)
                {
                    if (results.Count == maximum) { truncated = true; break; }
                    results.Add(new { path = Relative(canonical) });
                    continue;
                }
                string text;
                try
                {
                    using var stream = workspace.OpenRead(canonical);
                    if (stream.Length > MaxFileBytes) { skippedFiles++; continue; }
                    if (scannedBytes + stream.Length > MaxScanBytes) { truncated = true; break; }
                    scannedBytes += checked((int)stream.Length);
                    text = ReadText(stream, cancellationToken, out _);
                }
                catch (ToolException exception) when (exception.Code == "binary_file" || exception.Code == "file_too_large")
                {
                    skippedFiles++;
                    continue;
                }
                using var reader = new StringReader(text);
                string? line;
                var number = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    number++;
                    if (line.IndexOf(pattern, StringComparison.Ordinal) < 0) continue;
                    if (results.Count == maximum) { truncated = true; break; }
                    if (line.Length > 4096) throw new ToolException("output_too_large");
                    results.Add(new { path = Relative(canonical), line = number, text = line });
                }
                if (truncated) break;
            }
        }
        workspace.Resolve(path);
        return new { path = Relative(path), results, truncated, skippedFiles };
    }

    private string ReadText(string path, CancellationToken cancellationToken, out int bytes)
    {
        using var stream = workspace.OpenRead(path);
        return ReadText(stream, cancellationToken, out bytes);
    }

    private static string ReadText(FileStream stream, CancellationToken cancellationToken, out int bytes)
    {
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
        var content = data.ToArray();
        bytes = content.Length;
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
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

    private string Relative(string path) => path.Length == workspace.Root.Length ? "." : path.Substring(workspace.Root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');

    internal static bool GlobMatches(string glob, string path)
    {
        var patterns = glob.Split('/');
        var segments = path.Split('/');
        var matches = new bool[patterns.Length + 1, segments.Length + 1];
        matches[0, 0] = true;
        for (var p = 1; p <= patterns.Length; p++)
        {
            if (patterns[p - 1] == "**") matches[p, 0] = matches[p - 1, 0];
            for (var s = 1; s <= segments.Length; s++)
                matches[p, s] = patterns[p - 1] == "**" ? matches[p - 1, s] || matches[p, s - 1] :
                    matches[p - 1, s - 1] && SegmentMatches(patterns[p - 1], segments[s - 1]);
        }
        return matches[patterns.Length, segments.Length];
    }

    private static bool SegmentMatches(string pattern, string value)
    {
        var matches = new bool[value.Length + 1];
        matches[0] = true;
        foreach (var character in pattern)
        {
            var previous = matches[0];
            matches[0] = character == '*' && previous;
            for (var index = 1; index <= value.Length; index++)
            {
                var before = matches[index];
                matches[index] = character == '*' ? matches[index] || matches[index - 1] : previous &&
                    (character == '?' || char.ToUpperInvariant(character) == char.ToUpperInvariant(value[index - 1]));
                previous = before;
            }
        }
        return matches[value.Length];
    }
}
