using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Providers;

public sealed class ContextSnapshot
{
    public ContextSnapshot(string workspaceRoot, string kind, string label, string text, string? sourcePath = null)
    {
        if (kind != "file" && kind != "solution" && kind != "selection" && kind != "output")
            throw new ProviderException("invalid_message");
        if (label == null || text == null) throw new ProviderException("invalid_message");
        if (label.Length > 1024 || text.Length > 65536) throw new ProviderException("context_too_large");
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(text)) throw new ProviderException("invalid_message");
        ValidateUnicode(label, true);
        ValidateUnicode(text, false);
        WorkspaceRoot = ContextPathGuard.Validate(workspaceRoot, workspaceRoot);
        SourcePath = sourcePath == null ? null : ContextPathGuard.Validate(WorkspaceRoot, sourcePath);
        Kind = kind;
        Label = label;
        Text = text;
    }

    public string WorkspaceRoot { get; }
    public string Kind { get; }
    public string Label { get; }
    public string Text { get; }
    public string? SourcePath { get; }

    private static void ValidateUnicode(string value, bool label)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\0' || (label && char.IsControl(character))) throw new ProviderException("invalid_message");
            if (char.IsHighSurrogate(character))
            {
                if (++index == value.Length || !char.IsLowSurrogate(value[index])) throw new ProviderException("invalid_message");
            }
            else if (char.IsLowSurrogate(character)) throw new ProviderException("invalid_message");
        }
    }
}

public interface IContextSource
{
    Task<ContextSnapshot> CaptureAsync(string kind, string? path, CancellationToken cancellationToken);
    Task ValidateAsync(ContextSnapshot snapshot, CancellationToken cancellationToken);
}

public static class ContextPathGuard
{
    public static string Validate(string root, string path)
    {
        try
        {
            CheckSyntax(root, true);
            CheckSyntax(path, false);
            var canonicalRoot = TrimRoot(Path.GetFullPath(root));
            var canonicalPath = TrimRoot(Path.GetFullPath(IsAbsolute(path) ? path : Path.Combine(canonicalRoot, path)));
            if (!string.Equals(canonicalRoot, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
                !canonicalPath.StartsWith(canonicalRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ProviderException("context_outside_workspace");
            CheckAncestors(canonicalRoot);
            if (!Directory.Exists(canonicalRoot)) throw new ProviderException("context_unavailable");
            CheckAncestors(canonicalPath);
            if (Directory.Exists(canonicalPath) && !string.Equals(canonicalRoot, canonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new ProviderException("context_unavailable");
            return canonicalPath;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException ||
            exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            throw new ProviderException("context_unavailable");
        }
    }

    private static bool IsAbsolute(string path) => path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static void CheckSyntax(string value, bool root)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ProviderException("context_unavailable");
        if (value.StartsWith("\\", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal) || (root && !IsAbsolute(value)))
            throw new ProviderException("context_outside_workspace");
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) || character == '"' || character == '<' || character == '>' || character == '|' || character == '?' || character == '*' ||
                (character == ':' && !(index == 1 && IsAbsolute(value))))
                throw new ProviderException("context_outside_workspace");
        }
        foreach (var segment in value.Replace('\\', '/').Split('/'))
        {
            if (segment == ".") continue;
            var device = segment.Split('.')[0].ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CONIN$" || device == "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                 ("123456789¹²³".IndexOf(device[3]) >= 0)))
                throw new ProviderException("context_outside_workspace");
            // Win32 removes trailing dots/spaces; reject aliases before canonicalization.
            if (segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                throw new ProviderException("context_outside_workspace");
        }
    }

    private static string TrimRoot(string value)
    {
        var volume = Path.GetPathRoot(value)!;
        return value.Length > volume.Length ? value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : value;
    }

    private static void CheckAncestors(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ProviderException("context_outside_workspace");
                if (!string.Equals(current, path, StringComparison.OrdinalIgnoreCase) && File.Exists(current))
                    throw new ProviderException("context_unavailable");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
