using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Lumen.Providers;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioContextSource : IContextSource
{
    private const int MaxText = 65536;
    private const int MaxNodes = 1024;
    private readonly JoinableTaskFactory taskFactory;

    internal VisualStudioContextSource(JoinableTaskFactory taskFactory)
    {
        this.taskFactory = taskFactory ?? throw new ArgumentNullException(nameof(taskFactory));
    }

    public async Task<ContextSnapshot> CaptureAsync(string kind, string? path, CancellationToken cancellationToken)
    {
        try
        {
            if (kind != "file" && kind != "solution" && kind != "selection" && kind != "output")
                throw new ProviderException("invalid_message");
            if (kind != "file" && !string.IsNullOrEmpty(path)) throw new ProviderException("invalid_message");
            await taskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var root = GetWorkspaceRoot(out var solutionPath);
            ContextSnapshot result;
            if (kind == "selection") result = CaptureSelection(root);
            else if (kind == "solution") result = CaptureSolution(root, solutionPath, cancellationToken);
            else if (kind == "output")
            {
                var pane = GetDte().ToolWindows.OutputWindow.ActivePane;
                if (pane == null) throw new ProviderException("context_unavailable");
                result = new ContextSnapshot(root, kind, pane.Name, ReadDocument(pane.TextDocument));
            }
            else if (string.IsNullOrEmpty(path))
            {
                var document = GetDte().ActiveDocument;
                if (document == null) throw new ProviderException("context_unavailable");
                var source = ContextPathGuard.Validate(root, document.FullName);
                result = new ContextSnapshot(root, kind, Relative(root, source), ReadDocument(document.Object("TextDocument") as TextDocument ?? throw new ProviderException("context_unavailable")), source);
            }
            else
            {
                var source = ContextPathGuard.Validate(root, path!);
                var openText = TryReadOpenDocument(source, cancellationToken);
                var text = openText ?? await Task.Run(() => ReadDisk(root, source, cancellationToken), cancellationToken).ConfigureAwait(false);
                result = new ContextSnapshot(root, kind, Relative(root, source), text, source);
            }
            await ValidateAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw new ProviderException("context_unavailable");
        }
    }

    public async Task ValidateAsync(ContextSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot == null) throw new ProviderException("invalid_message");
        try
        {
            await taskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var root = GetWorkspaceRoot(out _);
            if (!string.Equals(root, snapshot.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                throw new ProviderException("context_stale");
            if (snapshot.SourcePath != null) ContextPathGuard.Validate(root, snapshot.SourcePath);
            // Validate identity and confinement, not content: sending must preserve the explicit capture.
        }
        catch (ProviderException exception) when (exception.Code == "context_unavailable" || exception.Code == "context_outside_workspace")
        {
            throw new ProviderException("context_stale");
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw new ProviderException("context_stale");
        }
    }

    private static bool IsUnavailable(Exception exception) => exception is COMException || exception is IOException ||
        exception is UnauthorizedAccessException || exception is SecurityException || exception is ArgumentException ||
        exception is InvalidOperationException || exception is NotSupportedException;

    private static DTE2 GetDte()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Package.GetGlobalService(typeof(SDTE)) as DTE2 ?? throw new ProviderException("context_unavailable");
    }

    internal static string GetWorkspaceRoot(out string solutionPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
        if (solution == null || !GetDte().Solution.IsOpen ||
            solution.GetSolutionInfo(out var directory, out var file, out _) < 0 ||
            string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(file))
            throw new ProviderException("context_unavailable");
        var root = ContextPathGuard.Validate(directory, directory);
        solutionPath = ContextPathGuard.Validate(root, file);
        if (!File.Exists(solutionPath)) throw new ProviderException("context_unavailable");
        return root;
    }

    private static ContextSnapshot CaptureSelection(string root)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var document = GetDte().ActiveDocument;
        if (document == null) throw new ProviderException("context_unavailable");
        var source = ContextPathGuard.Validate(root, document.FullName);
        // The text manager's last view may be Output rather than the active editor document.
        var selection = document.Selection as TextSelection;
        if (selection == null || selection.IsEmpty) throw new ProviderException("context_unavailable");
        var top = selection.TopPoint;
        var bottom = selection.BottomPoint;
        CheckLength((long)bottom.AbsoluteCharOffset - top.AbsoluteCharOffset + bottom.Line - top.Line);
        var text = selection.Text;
        CheckLength(text.Length);
        var endLine = bottom.Line > top.Line && bottom.LineCharOffset == 1 ? bottom.Line - 1 : bottom.Line;
        var label = Relative(root, source) + ":" + top.Line.ToString(CultureInfo.InvariantCulture) + "-" + endLine.ToString(CultureInfo.InvariantCulture);
        return new ContextSnapshot(root, "selection", label, text, source);
    }

    private static string? TryReadOpenDocument(string source, CancellationToken cancellationToken)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var documents = GetDte().Documents;
        var count = documents.Count;
        if (count > MaxNodes) throw new ProviderException("context_too_large");
        for (var index = 1; index <= count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents.Item(index);
            var name = document.FullName;
            if (!string.IsNullOrEmpty(name) && string.Equals(System.IO.Path.GetFullPath(name), source, StringComparison.OrdinalIgnoreCase))
            {
                // A known open document must never fall back to stale disk content.
                var textDocument = document.Object("TextDocument") as TextDocument;
                if (textDocument == null) throw new ProviderException("context_unavailable");
                return ReadDocument(textDocument);
            }
        }
        return null;
    }

    private static string ReadDocument(TextDocument document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (document == null) throw new ProviderException("context_unavailable");
        var start = document.StartPoint;
        var end = document.EndPoint;
        // DTE counts each newline once. Add a character per line break for a safe CRLF upper bound.
        var upperBound = (long)end.AbsoluteCharOffset - start.AbsoluteCharOffset + end.Line - start.Line;
        CheckLength(upperBound);
        if (upperBound <= 0) throw new ProviderException("context_unavailable");
        var text = start.CreateEditPoint().GetText(end);
        CheckLength(text.Length);
        return text;
    }

    private static ContextSnapshot CaptureSolution(string root, string solutionPath, CancellationToken cancellationToken)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var inventory = new StringBuilder();
        AppendInventory(inventory, "solution: " + Relative(root, solutionPath));
        var pending = new Stack<object>();
        var projects = GetDte().Solution.Projects;
        if (projects.Count > MaxNodes) throw new ProviderException("context_too_large");
        for (var index = projects.Count; index >= 1; index--) pending.Push(projects.Item(index));
        var inspected = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > MaxNodes) throw new ProviderException("context_too_large");
            var node = pending.Pop();
            ProjectItems? children;
            if (node is Project project)
            {
                if (string.Equals(project.Kind, EnvDTE.Constants.vsProjectKindUnmodeled, StringComparison.OrdinalIgnoreCase)) continue;
                var name = string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase) ? null : project.FullName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var local = InventoryPath(root, name!);
                    if (local != null) AppendInventory(inventory, "project: " + Relative(root, local));
                }
                children = project.ProjectItems;
            }
            else
            {
                var item = (ProjectItem)node;
                var subProject = item.SubProject;
                if (subProject != null)
                {
                    pending.Push(subProject);
                    children = null;
                }
                else
                {
                    var count = item.FileCount;
                    if (count > MaxNodes - inspected) throw new ProviderException("context_too_large");
                    for (short index = 1; index <= count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++inspected > MaxNodes) throw new ProviderException("context_too_large");
                        var local = InventoryPath(root, item.FileNames[index]);
                        if (local == null || !files.Add(local)) continue;
                        if (files.Count > 256) throw new ProviderException("context_too_large");
                        AppendInventory(inventory, "file: " + Relative(root, local));
                    }
                    children = item.ProjectItems;
                }
            }
            if (children == null) continue;
            var childCount = children.Count;
            if ((long)inspected + pending.Count + childCount > MaxNodes) throw new ProviderException("context_too_large");
            for (var index = childCount; index >= 1; index--) pending.Push(children.Item(index));
        }
        return new ContextSnapshot(root, "solution", Relative(root, solutionPath), inventory.ToString(), solutionPath);
    }

    private static string? InventoryPath(string root, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var canonical = ContextPathGuard.Validate(root, path);
            return File.Exists(canonical) ? canonical : null;
        }
        catch (ProviderException exception) when (exception.Code == "context_outside_workspace" || exception.Code == "context_unavailable")
        {
            // Linked files and folders are not workspace file content or inventory entries.
            return null;
        }
    }

    private static void AppendInventory(StringBuilder builder, string line)
    {
        CheckLength((long)builder.Length + line.Length + 1);
        builder.Append(line).Append('\n');
    }

    private static string Relative(string root, string source) => source.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');

    private static void CheckLength(long length)
    {
        if (length > MaxText) throw new ProviderException("context_too_large");
        if (length < 0) throw new ProviderException("context_unavailable");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    private static string ReadDisk(string root, string source, CancellationToken cancellationToken)
    {
        const int maxBytes = MaxText * 4 + 4;
        cancellationToken.ThrowIfCancellationRequested();
        ContextPathGuard.Validate(root, source);
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        ContextPathGuard.Validate(root, source);
        var finalPath = new StringBuilder(32768);
        var finalLength = GetFinalPathNameByHandle(stream.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
        if (finalLength == 0 || finalLength >= finalPath.Capacity) throw new ProviderException("context_unavailable");
        var openedPath = finalPath.ToString();
        if (!openedPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new ProviderException("context_unavailable");
        var canonicalOpened = ContextPathGuard.Validate(root, openedPath.Substring(4));
        if (!string.Equals(canonicalOpened, source, StringComparison.OrdinalIgnoreCase)) throw new ProviderException("context_outside_workspace");
        if (stream.Length > maxBytes) throw new ProviderException("context_too_large");
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length + count > maxBytes) throw new ProviderException("context_too_large");
            bytes.Write(buffer, 0, count);
        }
        ContextPathGuard.Validate(root, source);
        var data = bytes.ToArray();
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
        if (data.Length >= 4 && ((data[0] == 0xff && data[1] == 0xfe && data[2] == 0 && data[3] == 0) ||
            (data[0] == 0 && data[1] == 0 && data[2] == 0xfe && data[3] == 0xff)))
            throw new ProviderException("context_unavailable");
        if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) offset = 3;
        else if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        CheckLength(encoding.GetCharCount(data, offset, data.Length - offset));
        var text = encoding.GetString(data, offset, data.Length - offset);
        CheckLength(text.Length);
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("context_unavailable");
        foreach (var character in text)
            if (char.IsControl(character) && character != '\t' && character != '\r' && character != '\n')
                throw new ProviderException("context_unavailable");
        return text;
    }
}
