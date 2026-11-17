using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumen.Providers;

public interface IDiagnosticLog
{
    string Level { get; }
    string FilePath { get; }
    void SetLevel(string level);
    void Open();
}

public static class DiagnosticLevels
{
    public static readonly string[] All = { "Error", "Warning", "Info", "Debug" };
    public static bool IsValid(string value) => Array.IndexOf(All, value) >= 0;
}

// Structured JSONL log. Detail text is redacted before it is written: callers
// pass codes and exception types, never raw provider responses or credentials.
public sealed class LumenDiagnosticLog : IDiagnosticLog, IDisposable
{
    private static readonly (Regex Pattern, string Replacement)[] Redactions =
    {
        (new Regex(@"sk-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant), "[redacted]"),
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant), "[redacted]"),
        (new Regex(@"AIza[0-9A-Za-z_-]{30,}", RegexOptions.CultureInvariant), "[redacted]"),
        (new Regex(@"Bearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "Bearer [redacted]"),
        (new Regex(@"(?i)(api[_-]?key|x-api-key|x-goog-api-key)(\s*[:=]\s*)[^,\s}]+", RegexOptions.CultureInvariant), "$1$2[redacted]")
    };

    private readonly object gate = new object();
    private readonly string path;
    private string level = "Error";
    private bool disposed;

    public LumenDiagnosticLog(string dataFolder, string initialLevel)
    {
        if (string.IsNullOrWhiteSpace(dataFolder)) throw new ArgumentException("dataFolder is required.", nameof(dataFolder));
        path = Path.Combine(dataFolder, "logs", "lumen.log");
        SetLevel(initialLevel);
    }

    public string FilePath => path;

    public string Level { get { lock (gate) return level; } }

    public void SetLevel(string value)
    {
        if (!DiagnosticLevels.IsValid(value)) throw new ProviderException("invalid_configuration");
        lock (gate) level = value;
    }

    public void Write(string eventLevel, string source, string code, string? detail = null)
    {
        if (!DiagnosticLevels.IsValid(eventLevel)) return;
        if (Array.IndexOf(DiagnosticLevels.All, eventLevel) > Array.IndexOf(DiagnosticLevels.All, Level)) return;
        var entry = new Dictionary<string, string>
        {
            ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
            ["level"] = eventLevel,
            ["source"] = source ?? "Lumen",
            ["code"] = code ?? "unknown"
        };
        if (!string.IsNullOrEmpty(detail)) entry["detail"] = Redact(detail!);
        var line = JsonSerializer.Serialize(entry);
        lock (gate)
        {
            if (disposed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = value;
        foreach (var redaction in Redactions) result = redaction.Pattern.Replace(result, redaction.Replacement);
        return result;
    }

    public void Open()
    {
        lock (gate)
        {
            if (disposed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
            var start = new ProcessStartInfo("notepad.exe")
            {
                Arguments = "\"" + path + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
    }
}
