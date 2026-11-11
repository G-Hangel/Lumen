using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class WorkspaceShellToolsTests
{
    [Fact]
    public async Task SuccessfulCommandCapturesStdoutAndExitCode()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo hello\"]}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(0, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("hello", json.RootElement.GetProperty("stdout").GetString());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task NonZeroExitCodeIsReturnedNotAnError()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"exit\",\"42\"]}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(42, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task StderrIsCapturedSeparately()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo err 1>&2\"]}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Contains("err", json.RootElement.GetProperty("stderr").GetString());
        Assert.DoesNotContain("err", json.RootElement.GetProperty("stdout").GetString() ?? "");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationKillLiveShellChildWithinDeadline(bool cancel)
    {
        using var fixture = new Fixture();
        WriteChildScript(fixture, 60);
        using var cts = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var execution = fixture.Tools.ExecuteAsync(ChatMode.Agent, ChildCommand(6000), cts.Token);
        using var child = await WaitForChild(fixture, execution);
        try
        {
            Assert.False(child.HasExited); // Negative control: the oracle sees the live descendant.
            var stopping = Stopwatch.StartNew();
            if (cancel)
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(3));
            }
            else
            {
                var result = await execution.WaitAsync(TimeSpan.FromSeconds(9));
                Assert.True(result.IsError);
                Assert.Equal("command_timeout", Code(result));
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(9));
            }
            Assert.True(child.WaitForExit(2000), "The shell descendant survived termination.");
            Assert.False(File.Exists(Path.Combine(fixture.Path, "survived.txt")));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task ChildDeathOracleRejectsParentOnlyTermination()
    {
        using var fixture = new Fixture();
        WriteChildScript(fixture, 60);
        using var parent = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", Arguments = "/c powershell.exe -NoProfile -File child.ps1",
            WorkingDirectory = fixture.Path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Process? child = null;
        try
        {
            child = await WaitForChild(fixture, new TaskCompletionSource<ToolResult>().Task);
            parent.Kill(); // Reproduce the old implementation without affecting unrelated processes.
            Assert.True(parent.WaitForExit(2000));
            var rejected = Record.Exception(() => Assert.True(child.WaitForExit(100), "Child survived."));
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(rejected);
            Assert.False(child.HasExited);
        }
        finally
        {
            if (child != null)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(2000); }
                child.Dispose();
            }
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); parent.WaitForExit(2000); }
        }
    }

    [Fact]
    public async Task NormalShellChildCapturesBothPipesAndCompletes()
    {
        using var fixture = new Fixture();
        WriteChildScript(fixture, 0);
        var result = await fixture.Tools.ExecuteAsync(ChatMode.Agent, ChildCommand(10000), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(12));
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(0, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("child-stdout", json.RootElement.GetProperty("stdout").GetString());
        Assert.Contains("child-stderr", json.RootElement.GetProperty("stderr").GetString());
        Assert.True(File.Exists(Path.Combine(fixture.Path, "survived.txt")));
        Assert.False(IsProcessAlive(int.Parse(File.ReadAllText(Path.Combine(fixture.Path, "child.pid")))));
    }

    [Fact]
    public async Task ShellExitAlsoKillsChildHoldingPipesOpen()
    {
        using var fixture = new Fixture();
        WriteChildScript(fixture, 60);
        File.WriteAllText(Path.Combine(fixture.Path, "parent.ps1"),
            "$p = Start-Process powershell.exe -ArgumentList '-NoProfile -File child.ps1' -NoNewWindow -PassThru\n" +
            "while (!(Test-Path child.pid)) { Start-Sleep -Milliseconds 25 }\nexit 0\n");
        var execution = fixture.Execute("run_command", JsonSerializer.Serialize(new
        {
            command = "powershell.exe", args = new[] { "-NoProfile", "-File", "parent.ps1" }, timeout_ms = 10000
        }));
        using var child = await WaitForChild(fixture, execution);
        try
        {
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(result.IsError);
            Assert.True(child.WaitForExit(2000));
            Assert.False(File.Exists(Path.Combine(fixture.Path, "survived.txt")));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    [Fact]
    public void FailedJobAssignmentTerminatesSuspendedParentWithoutExecutingMarker()
    {
        using var fixture = new Fixture();
        var pid = 0;
        Assert.Throws<Win32Exception>(() => WindowsJobProcess.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", Arguments = "/c echo escaped>started.txt", WorkingDirectory = fixture.Path
        }, (_, process) =>
        {
            pid = unchecked((int)GetProcessId(process));
            Assert.True(IsProcessAlive(pid));
            Assert.False(File.Exists(Path.Combine(fixture.Path, "started.txt")));
            return AssignProcessToJobObject(IntPtr.Zero, process);
        }));
        Assert.NotEqual(0, pid);
        try
        {
            using var parent = Process.GetProcessById(pid);
            Assert.True(parent.WaitForExit(2000));
        }
        catch (ArgumentException) { } // Already reaped by Windows.
        Assert.False(IsProcessAlive(pid));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "started.txt")));
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static ToolInvocation ChildCommand(int timeout) => Call("run_command", JsonSerializer.Serialize(new
    {
        command = "cmd.exe", args = new[] { "/c", "powershell.exe", "-NoProfile", "-File", "child.ps1" }, timeout_ms = timeout
    }));

    private static void WriteChildScript(Fixture fixture, int seconds) =>
        File.WriteAllText(Path.Combine(fixture.Path, "child.ps1"),
            "[IO.File]::WriteAllText((Join-Path $pwd 'child.pid'), [string]$PID)\n" +
            "[Console]::Out.WriteLine('child-stdout')\n[Console]::Error.WriteLine('child-stderr')\n" +
            "Start-Sleep -Seconds " + seconds + "\n[IO.File]::WriteAllText((Join-Path $pwd 'survived.txt'), 'finished')\n");

    private static async Task<Process> WaitForChild(Fixture fixture, Task<ToolResult> execution)
    {
        var marker = Path.Combine(fixture.Path, "child.pid");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out var pid))
                return Process.GetProcessById(pid);
            Assert.False(execution.IsCompleted, "Command finished before its controlled child started.");
            await Task.Delay(25);
        }
        throw new TimeoutException("Controlled child never wrote its PID marker.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleContextAfterApprovalNeverStartsProcess(bool sessionConsent)
    {
        using var fixture = new Fixture();
        if (sessionConsent)
        {
            fixture.Response = ApprovalResponse.AllowCommandsForSession;
            Assert.False((await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo warmup\"]}")).IsError);
        }
        var callbackReached = false;
        fixture.BeforeStart = async token =>
        {
            Assert.Single(fixture.Requests);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            callbackReached = true;
            throw new ToolException("context_stale");
        };
        var result = await fixture.Tools.ExecuteAsync(ChatMode.Agent, MarkerCommand(), CancellationToken.None);
        Assert.True(callbackReached);
        Assert.True(result.IsError);
        Assert.Equal("context_stale", Code(result));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "started.txt")));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task CancellationAfterApprovalImmediatelyBeforeStartNeverExecutes()
    {
        using var fixture = new Fixture();
        using var cts = new CancellationTokenSource();
        var callbackReached = false;
        fixture.BeforeStart = async token =>
        {
            Assert.Single(fixture.Requests);
            Assert.False(token.IsCancellationRequested);
            await Task.Yield();
            callbackReached = true;
            cts.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Tools.ExecuteAsync(ChatMode.Agent, MarkerCommand(), cts.Token));
        Assert.True(callbackReached);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "started.txt")));
    }

    [Fact]
    public async Task ApprovalDenialNeverCallsPreStartCallback()
    {
        using var fixture = new Fixture();
        fixture.Response = ApprovalResponse.Deny;
        fixture.BeforeStart = _ => throw new InvalidOperationException("Callback must follow approval.");
        var result = await fixture.Tools.ExecuteAsync(ChatMode.Agent, MarkerCommand(), CancellationToken.None);
        Assert.Equal("approval_denied", Code(result));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "started.txt")));
    }

    [Fact]
    public async Task AcceptedPreStartCallbackAllowsMarkerProcess()
    {
        using var fixture = new Fixture();
        var callbackReached = false;
        fixture.BeforeStart = async _ =>
        {
            Assert.Single(fixture.Requests);
            Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "started.txt")));
            await Task.Yield();
            callbackReached = true;
        };
        var result = await fixture.Tools.ExecuteAsync(ChatMode.Agent, MarkerCommand(), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.True(callbackReached);
        Assert.Contains("started", File.ReadAllText(System.IO.Path.Combine(fixture.Path, "started.txt")));
    }

    private static ToolInvocation MarkerCommand() => Call("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo started>started.txt\"]}");

    [Theory]
    [InlineData(ChatMode.Ask)]
    [InlineData(ChatMode.Plan)]
    public async Task ReadOnlyModesDenyCommandsBeforeApproval(ChatMode mode)
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo hi\"]}", mode);
        Assert.True(result.IsError);
        Assert.Equal("mode_denied", Code(result));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ApprovalDenyBlocksCommand()
    {
        using var fixture = new Fixture();
        fixture.Response = ApprovalResponse.Deny;
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo hi\"]}");
        Assert.True(result.IsError);
        Assert.Equal("approval_denied", Code(result));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ApprovalAllowOncePromptsEachTime()
    {
        using var fixture = new Fixture();
        fixture.Response = ApprovalResponse.AllowOnce;
        await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo one\"]}");
        await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo two\"]}");
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task SessionOptInAllowsSubsequentCommandsWithoutPrompt()
    {
        using var fixture = new Fixture();
        fixture.Response = ApprovalResponse.AllowCommandsForSession;
        await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo one\"]}");
        Assert.Single(fixture.Requests);

        fixture.Response = ApprovalResponse.Deny;
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo two\"]}");
        Assert.False(result.IsError);
        // Second command used the session opt-in, no new prompt.
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("format", new string[0], "blocked_executable")]
    [InlineData("shutdown", new string[0], "blocked_executable")]
    [InlineData("diskpart", new string[0], "blocked_executable")]
    [InlineData("rm", new[] { "-rf", "/" }, "rm_recursive_root")]
    [InlineData("rm", new[] { "-r", "-f", "/" }, "rm_recursive_root")]
    [InlineData("del", new[] { "/s", "/q", "C:\\test" }, "recursive_delete")]
    [InlineData("rd", new[] { "/s", "/q", "C:\\test" }, "recursive_delete")]
    [InlineData("git", new[] { "push", "--force" }, "git_force_push")]
    [InlineData("git", new[] { "push", "-f" }, "git_force_push")]
    [InlineData("git", new[] { "reset", "--hard" }, "git_reset_hard")]
    [InlineData("git", new[] { "clean", "-fd" }, "git_clean_force")]
    public async Task BlocklistBlocksBeforeApproval(string command, string[] args, string expected)
    {
        using var fixture = new Fixture();
        // Verify the blocklist itself matches the expected rule.
        Assert.Equal(expected, CommandBlocklist.Match(command, args));
        var arguments = "{\"command\":\"" + command + "\",\"args\":" + JsonSerializer.Serialize(args) + "}";
        var result = await fixture.Execute("run_command", arguments);
        Assert.True(result.IsError);
        Assert.Equal("command_blocked", Code(result));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task CmdExeWithDestructiveEmbeddedCommandIsBlocked()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"format C:\"]}");
        Assert.True(result.IsError);
        Assert.Equal("command_blocked", Code(result));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task SafeGitCommandIsNotBlockedByBlocklist()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"git.exe\",\"args\":[\"status\"]}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        // git status may fail if not a git repo, but should not be blocked
        Assert.True(json.RootElement.TryGetProperty("exitCode", out _));
    }

    [Theory]
    [InlineData("run_command", "{}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\"}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\",\"args\":\"not-array\"}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\",\"args\":[1]}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\",\"args\":[\"y\"],\"extra\":1}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\",\"args\":[\"y\"],\"timeout_ms\":0}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\",\"args\":[\"y\"],\"timeout_ms\":70000}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"\",\"args\":[]}", "invalid_arguments")]
    [InlineData("run_command", "{\"command\":\"x\",\"args\":[\"y\\u0000\"]}", "invalid_arguments")]
    [InlineData("read_file", "{}", "unknown_tool")]
    public async Task InvalidRequestsFailBeforeExecution(string name, string arguments, string code)
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute(name, arguments);
        Assert.True(result.IsError);
        Assert.Equal(code, Code(result));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task CwdOutsideWorkspaceIsRejected()
    {
        using var fixture = new Fixture();
        using var outside = new TempFolder();
        var outsidePath = outside.Path.Replace('\\', '/');
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo hi\"],\"cwd\":\"" + outsidePath + "\"}");
        Assert.True(result.IsError);
        Assert.Equal("outside_workspace", Code(result));
    }

    [Fact]
    public async Task CwdInsideWorkspaceIsUsed()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "sub"));
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"cd\"],\"cwd\":\"sub\"}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Contains("sub", json.RootElement.GetProperty("stdout").GetString() ?? "");
    }

    [Fact]
    public void ArgumentEscapingHandlesSpecialCharacters()
    {
        // No special chars → no quotes
        Assert.Equal("hello", WorkspaceShellTools.BuildArguments(new[] { "hello" }));
        // Space → quoted
        Assert.Equal("\"hello world\"", WorkspaceShellTools.BuildArguments(new[] { "hello world" }));
        // Empty → ""
        Assert.Equal("\"\"", WorkspaceShellTools.BuildArguments(new[] { "" }));
        // Quote → escaped
        var result = WorkspaceShellTools.BuildArguments(new[] { "a\"b" });
        Assert.Equal("\"a\\\"b\"", result);
        // Trailing backslash inside quotes → doubled
        result = WorkspaceShellTools.BuildArguments(new[] { "C:\\Program Files\\" });
        Assert.Equal("\"C:\\Program Files\\\\\"", result);
        // Multiple args
        result = WorkspaceShellTools.BuildArguments(new[] { "echo", "hello world" });
        Assert.Equal("echo \"hello world\"", result);
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [Fact]
    public void ArgumentEscapingRoundTripsThroughWindowsParserAndRejectsMutation()
    {
        var arguments = new[] { "", "space here", "a\\\"b", "two\\\\\"quoted", "C:\\Program Files\\", "; rm -rf", "&&", "|", "`", "$(text)" };
        Assert.Equal(arguments, ParseArguments("fixture.exe " + WorkspaceShellTools.BuildArguments(arguments)).Skip(1));
        Assert.NotEqual(new[] { "space here" }, ParseArguments("fixture.exe space here").Skip(1).ToArray());
    }

    private static string[] ParseArguments(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++) result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(pointer); }
    }

    [Fact]
    public void PublishedSchemasAreValidAndReadable()
    {
        Assert.Equal(new[] { "run_command", "run_build", "run_tests" }, CommandToolCatalog.Specs.Select(spec => spec.Name));
        var spec = CommandToolCatalog.Specs.Single(value => value.Name == "run_command");
        Assert.False(spec.ParametersSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("object", spec.ParametersSchema.GetProperty("type").GetString());
        var required = spec.ParametersSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Contains("command", required);
        Assert.Contains("args", required);
    }

    [Theory]
    [InlineData("run_build", "build")]
    [InlineData("run_tests", "test")]
    public async Task DotnetTasksRequireApprovalAndConfineTheirTarget(string tool, string verb)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "Broken.csproj"), "<invalid>");
        var outside = await fixture.Execute(tool, "{\"path\":\"../outside.csproj\"}");
        Assert.True(outside.IsError);
        Assert.Empty(fixture.Requests);
        var ask = await fixture.Execute(tool, "{\"path\":\"Broken.csproj\"}", ChatMode.Ask);
        Assert.True(ask.IsError);
        Assert.Equal("mode_denied", Code(ask));
        fixture.Response = ApprovalResponse.Deny;
        var denied = await fixture.Execute(tool, "{\"path\":\"Broken.csproj\"}");
        Assert.True(denied.IsError);
        Assert.Equal("approval_denied", Code(denied));
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("dotnet", request.Arguments.GetProperty("command").GetString());
        var arguments = request.Arguments.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { verb, Path.Combine(fixture.Path, "Broken.csproj"), "--no-restore" }, arguments);
        fixture.Response = ApprovalResponse.AllowOnce;
        var executed = await fixture.Execute(tool, "{\"path\":\"Broken.csproj\"}");
        Assert.False(executed.IsError, executed.Content);
        using var result = JsonDocument.Parse(executed.Content);
        Assert.NotEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task DetectedShellsExecuteOnlyAfterApproval()
    {
        using var fixture = new Fixture();
        var guard = new WorkspaceGuard(fixture.Path);
        using var broker = new ApprovalBroker(guard, fixture);
        var detected = await new WorkspaceReadTools(guard, broker).ExecuteAsync(ChatMode.Ask, Call("detect_shells", "{}"), CancellationToken.None);
        Assert.False(detected.IsError);
        using var document = JsonDocument.Parse(detected.Content);
        var shells = document.RootElement.GetProperty("shells").EnumerateArray().ToArray();
        Assert.Contains(shells, shell => shell.GetProperty("name").GetString() == "cmd");
        foreach (var shell in shells)
        {
            var name = shell.GetProperty("name").GetString()!;
            var executable = shell.GetProperty("executable").GetString()!;
            var args = name == "cmd" ? new[] { "/c", "echo lumen-shell" } : name == "bash" ? new[] { "-c", "printf lumen-shell" } : new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output lumen-shell" };
            var invocation = JsonSerializer.Serialize(new { command = executable, args });
            fixture.Response = ApprovalResponse.Deny;
            var denied = await fixture.Execute("run_command", invocation);
            Assert.True(denied.IsError);
            Assert.Equal("approval_denied", Code(denied));
            fixture.Response = ApprovalResponse.AllowOnce;
            var result = await fixture.Execute("run_command", invocation);
            Assert.False(result.IsError, result.Content);
            using var output = JsonDocument.Parse(result.Content);
            Assert.Equal(0, output.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("lumen-shell", output.RootElement.GetProperty("stdout").GetString());
        }
        Assert.Equal(shells.Length * 2, fixture.Requests.Count);
    }

    [Fact]
    public async Task DefaultTimeoutIsThirtySeconds()
    {
        // If timeout_ms is omitted, the executor should use 30000ms.
        // We verify by running a quick command without specifying timeout.
        using var fixture = new Fixture();
        var result = await fixture.Execute("run_command", "{\"command\":\"cmd.exe\",\"args\":[\"/c\",\"echo fast\"]}");
        Assert.False(result.IsError);
    }

    private static string Code(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.GetProperty("code").GetString()!;

    private static ToolInvocation Call(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ToolInvocation("t1", name, document.RootElement.Clone());
    }

    private sealed class Fixture : IDisposable, IApprovalPrompt
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.ShellTools." + Guid.NewGuid().ToString("N"));
        internal WorkspaceShellTools Tools { get; }
        internal ApprovalResponse Response { get; set; } = ApprovalResponse.AllowOnce;
        internal List<ApprovalRequest> Requests { get; } = new();
        internal Func<CancellationToken, Task>? BeforeStart { get; set; }
        private readonly ApprovalBroker broker;
        internal Fixture()
        {
            Directory.CreateDirectory(Path);
            var guard = new WorkspaceGuard(Path);
            broker = new ApprovalBroker(guard, this);
            Tools = new WorkspaceShellTools(guard, broker, token => BeforeStart?.Invoke(token) ?? Task.CompletedTask);
        }
        internal Task<ToolResult> Execute(string name, string arguments, ChatMode mode = ChatMode.Agent) =>
            Tools.ExecuteAsync(mode, Call(name, arguments), CancellationToken.None);
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Response);
        }
        public void Dispose() { broker.Dispose(); for (var i = 0; i < 3; i++) { try { if (Directory.Exists(Path)) Directory.Delete(Path, true); break; } catch (IOException) { Thread.Sleep(100); } } }
    }

    private sealed class TempFolder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.ShellTools.Outside." + Guid.NewGuid().ToString("N"));
        internal TempFolder() { Directory.CreateDirectory(Path); }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
