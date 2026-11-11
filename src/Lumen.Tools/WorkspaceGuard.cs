using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumen.Tools;

public sealed class WorkspaceGuard
{
    public WorkspaceGuard(string root)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new ToolException("path_unavailable");
        try
        {
            Root = Canonicalize(root, true);
            CheckAncestors(Root, true);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException)
        {
            throw new ToolException("invalid_path");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            throw new ToolException("path_unavailable");
        }
    }

    public string Root { get; }

    public string Resolve(string path) => ResolvePath(path, true);

    // Approval is the broker's responsibility; this only establishes a safe target identity.
    public string ResolveWriteTarget(string path) => ResolvePath(path, false);

    public bool Contains(string canonicalPath) =>
        string.Equals(Root, canonicalPath, StringComparison.OrdinalIgnoreCase) ||
        (canonicalPath != null && canonicalPath.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    public FileStream OpenRead(string path)
    {
        var intendedPath = Resolve(path);
        try
        {
            return ValidateReadHandle(new FileStream(intendedPath, FileMode.Open, FileAccess.Read, FileShare.Read), intendedPath);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException)
        {
            throw new ToolException("invalid_path");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            throw new ToolException("path_unavailable");
        }
    }

    private string ResolvePath(string path, bool confined)
    {
        try
        {
            CheckSyntax(path, false);
            var canonicalPath = Canonicalize(IsAbsolute(path) ? path : Path.Combine(Root, path), true);
            if (confined && !Contains(canonicalPath)) throw new ToolException("outside_workspace");
            CheckAncestors(Root, true);
            CheckAncestors(canonicalPath, false);
            return canonicalPath;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException)
        {
            throw new ToolException("invalid_path");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            throw new ToolException("path_unavailable");
        }
    }

    private FileStream ValidateReadHandle(FileStream stream, string intendedPath)
    {
        try
        {
            // Attribute checks alone have a TOCTOU gap: validate the actual opened object before any read.
            Resolve(intendedPath);
            var finalPath = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
            if (length == 0 || length >= finalPath.Capacity) throw new ToolException("path_unavailable");
            var openedPath = finalPath.ToString();
            if (!openedPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new ToolException("path_unavailable");
            var canonicalOpened = Canonicalize(openedPath.Substring(4), true);
            if (!Contains(canonicalOpened) || !string.Equals(canonicalOpened, intendedPath, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("outside_workspace");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string Canonicalize(string path, bool absolute)
    {
        CheckSyntax(path, absolute);
        var canonical = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(canonical)!;
        return canonical.Length > volume.Length ? canonical.TrimEnd('\\', '/') : canonical;
    }

    private static bool IsAbsolute(string path) => path.Length >= 3 &&
        ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) &&
        path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static void CheckSyntax(string path, bool absolute)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 ||
            path[0] == '\\' || path[0] == '/' || (absolute && !IsAbsolute(path)))
            throw new ToolException("invalid_path");
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (char.IsControl(character) || character == '"' || character == '<' || character == '>' || character == '|' ||
                character == '?' || character == '*' || (character == ':' && !(index == 1 && IsAbsolute(path))))
                throw new ToolException("invalid_path");
            if (char.IsHighSurrogate(character))
            {
                if (++index == path.Length || !char.IsLowSurrogate(path[index])) throw new ToolException("invalid_path");
            }
            else if (char.IsLowSurrogate(character)) throw new ToolException("invalid_path");
        }
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment == ".") continue;
            // Win32 aliases trailing dots/spaces and reserved device stems, even before an extension.
            if (segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                throw new ToolException("invalid_path");
            var device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CONIN$" || device == "CONOUT$" || device == "CLOCK$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "123456789¹²³".IndexOf(device[3]) >= 0))
                throw new ToolException("invalid_path");
        }
    }

    private static void CheckAncestors(string path, bool requireDirectory)
    {
        var ancestors = new List<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) ancestors.Add(current);
        // Inspect from the volume down so we never follow a known reparse ancestor to inspect its descendants.
        for (var index = ancestors.Count - 1; index >= 0; index--)
        {
            var current = ancestors[index];
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ToolException("outside_workspace");
                if ((requireDirectory || index != 0) && (attributes & FileAttributes.Directory) == 0)
                    throw new ToolException("path_unavailable");
            }
            catch (FileNotFoundException)
            {
                if (requireDirectory) throw new ToolException("path_unavailable");
            }
            catch (DirectoryNotFoundException)
            {
                if (requireDirectory) throw new ToolException("path_unavailable");
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
