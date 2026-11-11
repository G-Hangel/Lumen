using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Tools;

public sealed class WorkspaceShellTools
{
    private const int MaxOutputChars = 65536;
    private const int DefaultTimeoutMs = 30000;
    private readonly WorkspaceGuard workspace;
    private readonly ApprovalBroker approvals;
    private readonly Func<CancellationToken, Task>? beforeStart;

    public WorkspaceShellTools(WorkspaceGuard workspace, ApprovalBroker approvals, Func<CancellationToken, Task>? beforeStart = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.beforeStart = beforeStart;
    }

    public async Task<ToolResult> ExecuteAsync(ChatMode mode, ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            var (command, args, cwd, timeoutMs) = CommandToolCatalog.Validate(invocation);

            var blocked = CommandBlocklist.Match(command, args);
            if (blocked != null) return Error(invocation, "command_blocked");

            // Every command requires confirmation (AGENTS.md §1). The broker
            // allows a session-level opt-in so the user can grant "always" per session.
            await approvals.AuthorizeCommandAsync(mode, invocation, requiresExplicitApproval: false, cancellationToken).ConfigureAwait(false);

            var workingDir = cwd != null ? workspace.Resolve(cwd) : workspace.Root;
            if (!Directory.Exists(workingDir)) return Error(invocation, "path_unavailable");

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = BuildArguments(args),
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            var (exitCode, stdout, stderr, truncated) = await RunProcessAsync(startInfo, timeoutMs, cancellationToken).ConfigureAwait(false);
            var content = JsonSerializer.Serialize(new { exitCode, stdout, stderr, truncated });
            if (content.Length > MaxOutputChars)
                content = JsonSerializer.Serialize(new { exitCode, stdout = Truncate(stdout), stderr = Truncate(stderr), truncated = true });
            return new ToolResult(invocation.Id, false, content);
        }
        catch (ToolException exception) { return Error(invocation, exception.Code); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException || exception is ArgumentException)
        {
            return Error(invocation, "invalid_arguments");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException || exception is System.ComponentModel.Win32Exception)
        {
            return Error(invocation, "command_failed");
        }
    }

    private async Task<(int exitCode, string stdout, string stderr, bool truncated)> RunProcessAsync(
        ProcessStartInfo startInfo, int timeoutMs, CancellationToken cancellationToken)
    {
        var stdoutBuilder = new BoundedStringBuilder(MaxOutputChars);
        var stderrBuilder = new BoundedStringBuilder(MaxOutputChars);

        cancellationToken.ThrowIfCancellationRequested();
        if (beforeStart != null) await beforeStart(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = WindowsJobProcess.Start(startInfo);
        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutBuilder, cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrBuilder, cancellationToken);
        var reads = Task.WhenAll(stdoutTask, stderrTask);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= timeoutMs) throw new ToolException("command_timeout");
                await Task.Delay(Math.Min(50, timeoutMs), cancellationToken).ConfigureAwait(false);
            }

            // A shell can exit while descendants still hold its stdout/stderr open.
            process.KillTree();
            cancellationToken.ThrowIfCancellationRequested();
            if (await Task.WhenAny(reads, Task.Delay(2000, cancellationToken)).ConfigureAwait(false) != reads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ToolException("command_output_timeout");
            }
            await reads.ConfigureAwait(false);
            return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString(), stdoutBuilder.Truncated || stderrBuilder.Truncated);
        }
        finally
        {
            process.KillTree();
            // Cleanup never awaits a pipe or replaces the original timeout/cancellation.
            // Observe late read faults when closing the pipe interrupts pending reads.
            _ = reads.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task ReadStreamAsync(StreamReader reader, BoundedStringBuilder builder, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            if (cancellationToken.IsCancellationRequested) break;
            builder.Append(buffer, count);
        }
    }

    // Never concatenate the model's strings into a command line. Each argument is
    // escaped individually per the Windows CommandLineToArgvW convention so the
    // process receives exactly the strings the model intended, not a re-parsed
    // shell command (AGENTS.md §5).
    internal static string BuildArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(EscapeArgument(args[i]));
        }
        return sb.ToString();
    }

    private static string EscapeArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";

        var needsQuotes = false;
        foreach (var c in argument)
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '"') { needsQuotes = true; break; }
        if (!needsQuotes) return argument;

        var sb = new StringBuilder(argument.Length * 2);
        sb.Append('"');

        var backslashes = 0;
        for (var i = 0; i < argument.Length; i++)
        {
            var c = argument[i];
            if (c == '\\') { backslashes++; continue; }

            if (c == '"')
            {
                for (var j = 0; j < backslashes * 2; j++) sb.Append('\\');
                sb.Append('\\');
            }
            else
            {
                for (var j = 0; j < backslashes; j++) sb.Append('\\');
            }
            backslashes = 0;
            sb.Append(c);
        }

        for (var j = 0; j < backslashes; j++) sb.Append('\\');
        for (var j = 0; j < backslashes; j++) sb.Append('\\');

        sb.Append('"');
        return sb.ToString();
    }

    private static string Truncate(string text) =>
        text.Length <= 1024 ? text : text.Substring(0, 1021) + "...";

    private static ToolResult Error(ToolInvocation invocation, string code) =>
        new(invocation.Id, true, JsonSerializer.Serialize(new { code }));

    private sealed class BoundedStringBuilder
    {
        private readonly StringBuilder sb = new();
        private readonly int maxChars;
        private int chars;
        public bool Truncated { get; private set; }

        internal BoundedStringBuilder(int maxChars) { this.maxChars = maxChars; }

        internal void Append(char[] buffer, int count)
        {
            if (Truncated) return;
            var remaining = maxChars - chars;
            if (count > remaining) { count = remaining; Truncated = true; }
            if (count > 0) { sb.Append(buffer, 0, count); chars += count; }
        }

        public override string ToString() => sb.ToString();
    }
}
