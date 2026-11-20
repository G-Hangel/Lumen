using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class WorkspaceWriteToolsTests
{
    [Theory]
    [InlineData("create_file")]
    [InlineData("edit_file")]
    public async Task HostCommitGateRejectsPreparedWriteAndUndoWithoutChangingTarget(string name)
    {
        var deny = true;
        var visits = 0;
        using var fixture = new Fixture(commitTarget: (path, commit) =>
        {
            visits++;
            Assert.Equal("target.cs", System.IO.Path.GetFileName(path));
            if (deny) throw new ToolException("edit_conflict");
            commit();
        });
        var target = Path.Combine(fixture.Path, "target.cs");
        if (name == "edit_file") File.WriteAllText(target, "before");
        var arguments = "{\"path\":\"target.cs\",\"content\":\"after\"}";
        var rejected = await fixture.Execute(name, arguments);
        Assert.True(rejected.IsError);
        Assert.Equal("edit_conflict", Code(rejected));
        Assert.Equal(name == "edit_file" ? "before" : null, File.Exists(target) ? File.ReadAllText(target) : null);
        Assert.Equal(0, fixture.Journal.Count);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
        deny = false;
        Assert.False((await fixture.Execute(name, arguments)).IsError);
        deny = true;
        var conflict = await Assert.ThrowsAsync<ToolException>(() => fixture.Journal.UndoTurnAsync(CancellationToken.None));
        Assert.Equal("undo_conflict", conflict.Code);
        Assert.Equal("after", File.ReadAllText(target));
        Assert.Equal(1, fixture.Journal.Count);
        deny = false;
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal(name == "edit_file" ? "before" : null, File.Exists(target) ? File.ReadAllText(target) : null);
        Assert.Equal(0, fixture.Journal.Count);
        Assert.Equal(4, visits);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    private static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task ApplyDiffUsesOriginalTextAndUndoRestoresBytes()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        var bytes = new byte[] { 0xff, 0xfe, 65, 0, 66, 0 };
        File.WriteAllBytes(target, bytes);
        var arguments = JsonSerializer.Serialize(new { path = "target", base_sha256 = Hash("AB"), replacements = new[] { new { old = "A", @new = "B" }, new { old = "B", @new = "C" } } });
        var result = await fixture.Execute("apply_diff", arguments);
        Assert.False(result.IsError);
        Assert.Equal("BC", File.ReadAllText(target));
        Assert.Contains("diff", result.Content);
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData("abc", "bad", "a", "x", "b", "y")]
    [InlineData("aaa", "valid", "aa", "x", "a", "y")]
    [InlineData("abcd", "valid", "abc", "x", "bcd", "y")]
    [InlineData("abc", "valid", "z", "x", "b", "y")]
    [InlineData("abc", "valid", "", "x", "b", "y")]
    public async Task ApplyDiffRejectsStaleAmbiguousOverlapMissingAndEmpty(string original, string hashMode, string old1, string new1, string old2, string new2)
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        File.WriteAllText(target, original);
        var arguments = JsonSerializer.Serialize(new { path = "target", base_sha256 = Hash(hashMode == "bad" ? "other" : original), replacements = new[] { new { old = old1, @new = new1 }, new { old = old2, @new = new2 } } });
        Assert.True((await fixture.Execute("apply_diff", arguments)).IsError);
        Assert.Equal(original, File.ReadAllText(target));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task ApplyDiffRequiresExternalApprovalBeforeReadingAndRejectsCommitRace()
    {
        using var outside = new TempFolder();
        using var fixture = new Fixture();
        var target = Path.Combine(outside.Path, "missing");
        fixture.Response = ApprovalResponse.Deny;
        var args = JsonSerializer.Serialize(new { path = target, base_sha256 = Hash("old"), replacements = new[] { new { old = "old", @new = "new" } } });
        var denied = await fixture.Execute("apply_diff", args);
        Assert.Equal("approval_denied", Code(denied));
        Assert.Single(fixture.Requests);

        var local = Path.Combine(fixture.Path, "target");
        File.WriteAllText(local, "old");
        fixture.Tools.BeforeCommit = () => File.WriteAllText(local, "raced");
        var raced = await fixture.Execute("apply_diff", JsonSerializer.Serialize(new { path = "target", base_sha256 = Hash("old"), replacements = new[] { new { old = "old", @new = "new" } } }));
        Assert.Equal("edit_conflict", Code(raced));
        Assert.Equal("raced", File.ReadAllText(local));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task CreateFileWritesUtf8WithoutBomAndRecordsUndo()
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute("create_file", "{\"path\":\"new.cs\",\"content\":\"hello\\nworld\"}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(json.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal("new.cs", json.RootElement.GetProperty("path").GetString());
        var written = File.ReadAllBytes(Path.Combine(fixture.Path, "new.cs"));
        Assert.Equal(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x0A, 0x77, 0x6F, 0x72, 0x6C, 0x64 }, written);
        Assert.Equal(1, fixture.Journal.Count);
    }

    [Fact]
    public async Task CreateFileRejectsExistingFileWithoutOverwriting()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "exists.cs");
        File.WriteAllText(target, "keep");
        var result = await fixture.Execute("create_file", "{\"path\":\"exists.cs\",\"content\":\"changed\"}");
        Assert.True(result.IsError);
        Assert.Equal("file_exists", Code(result));
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task EditFileReplacesContentProducesDiffAndRestoresExactPriorBytes()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "code.cs");
        // UTF-16 LE with BOM: undo must restore these exact bytes, not a re-encoded form.
        File.WriteAllBytes(target, new byte[] { 0xFF, 0xFE, (byte)'o', 0, (byte)'n', 0, (byte)'e', 0 });
        var result = await fixture.Execute("edit_file", "{\"path\":\"code.cs\",\"content\":\"one\\ntwo\"}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(json.RootElement.GetProperty("edited").GetBoolean());
        Assert.Equal("one\ntwo", File.ReadAllText(target));
        var diff = json.RootElement.GetProperty("diff");
        Assert.Equal(1, diff.GetProperty("linesBefore").GetInt32());
        Assert.Equal(2, diff.GetProperty("linesAfter").GetInt32());
        Assert.Equal(2, diff.GetProperty("changes").EnumerateArray().Count(c => c.GetProperty("op").GetString() == "add"));
        Assert.Equal(1, fixture.Journal.Count);

        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 0xFF, 0xFE, (byte)'o', 0, (byte)'n', 0, (byte)'e', 0 }, File.ReadAllBytes(target));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task EditFileRejectsMissingAndBinaryFilesWithoutChangingThem()
    {
        using var fixture = new Fixture();
        var missing = await fixture.Execute("edit_file", "{\"path\":\"absent.cs\",\"content\":\"x\"}");
        Assert.True(missing.IsError);
        Assert.Equal("path_unavailable", Code(missing));

        var binary = Path.Combine(fixture.Path, "bin");
        File.WriteAllBytes(binary, new byte[] { 0, 1, 2 });
        var binaryResult = await fixture.Execute("edit_file", "{\"path\":\"bin\",\"content\":\"x\"}");
        Assert.True(binaryResult.IsError);
        Assert.Equal("binary_file", Code(binaryResult));
        Assert.Equal(new byte[] { 0, 1, 2 }, File.ReadAllBytes(binary));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task BatchUndoRevertsATurnInReverseOrderAcrossFiles()
    {
        using var fixture = new Fixture();
        var existing = Path.Combine(fixture.Path, "old.cs");
        File.WriteAllText(existing, "before");
        await fixture.Execute("create_file", "{\"path\":\"new.cs\",\"content\":\"created\"}");
        await fixture.Execute("edit_file", "{\"path\":\"old.cs\",\"content\":\"after\"}");
        Assert.True(File.Exists(Path.Combine(fixture.Path, "new.cs")));
        Assert.Equal("after", File.ReadAllText(existing));
        Assert.Equal(2, fixture.Journal.Count);

        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "new.cs")));
        Assert.Equal("before", File.ReadAllText(existing));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task OutsideWorkspaceWritePromptsAndOnlyProceedsOnApproval()
    {
        using var fixture = new Fixture();
        using var outside = new TempFolder();
        var target = Path.Combine(outside.Path, "out.cs");
        fixture.Response = ApprovalResponse.Deny;
        var denied = await fixture.Execute("create_file", "{\"path\":\"" + target.Replace('\\', '/') + "\",\"content\":\"x\"}");
        Assert.True(denied.IsError);
        Assert.Equal("approval_denied", Code(denied));
        Assert.Single(fixture.Requests);
        Assert.False(File.Exists(target));

        fixture.Response = ApprovalResponse.AllowOnce;
        fixture.Requests.Clear();
        var allowed = await fixture.Execute("create_file", "{\"path\":\"" + target.Replace('\\', '/') + "\",\"content\":\"x\"}");
        Assert.False(allowed.IsError);
        Assert.Single(fixture.Requests);
        Assert.Equal("x", File.ReadAllText(target));
        Assert.True(fixture.Journal.Count == 1);
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task AskDeniesWritesBeforePromptOrPathAccess()
    {
        using var fixture = new Fixture();
        foreach (var tool in new[] { "create_file", "edit_file" })
        {
            var result = await fixture.Execute(tool, "{\"path\":\"x.cs\",\"content\":\"y\"}", ChatMode.Ask);
            Assert.True(result.IsError);
            Assert.Equal("mode_denied", Code(result));
        }
        Assert.Empty(fixture.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    [Fact]
    public async Task PlanWritesOnlyPlanDocuments()
    {
        using var fixture = new Fixture();
        foreach (var tool in new[] { "create_file", "edit_file" })
        {
            var denied = await fixture.Execute(tool, "{\"path\":\"x.cs\",\"content\":\"y\"}", ChatMode.Plan);
            Assert.True(denied.IsError);
            Assert.Equal("mode_denied", Code(denied));
        }
        var diff = await fixture.Execute("apply_diff", JsonSerializer.Serialize(new
        {
            path = "plan.md", base_sha256 = new string('a', 64),
            replacements = new[] { new { old = "a", @new = "b" } }
        }), ChatMode.Plan);
        Assert.True(diff.IsError);
        Assert.Equal("mode_denied", Code(diff));
        Assert.Empty(fixture.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));

        var created = await fixture.Execute("create_file", "{\"path\":\"plan.md\",\"content\":\"# Plan\"}", ChatMode.Plan);
        Assert.False(created.IsError);
        Assert.Equal("# Plan", File.ReadAllText(Path.Combine(fixture.Path, "plan.md")));
    }

    [Theory]
    [InlineData("create_file", "{}", "invalid_arguments")]
    [InlineData("create_file", "{\"path\":\"x\"}", "invalid_arguments")]
    [InlineData("create_file", "{\"path\":\"x\",\"content\":1}", "invalid_arguments")]
    [InlineData("create_file", "{\"path\":\"x\",\"content\":\"y\",\"extra\":1}", "invalid_arguments")]
    [InlineData("create_file", "{\"path\":\"x\",\"path\":\"y\",\"content\":\"z\"}", "invalid_arguments")]
    [InlineData("create_file", "{\"path\":\"../escape\",\"content\":\"y\"}", "invalid_path")]
    [InlineData("edit_file", "{\"path\":\"x\",\"content\":\"y\\u0000\"}", "invalid_arguments")]
    [InlineData("run_command", "{}", "unknown_tool")]
    public async Task InvalidRequestsFailBeforeAnyWrite(string name, string arguments, string code)
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute(name, arguments);
        Assert.True(result.IsError);
        Assert.Equal(code, Code(result));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task CancelledWriteLeavesNoFileAndNoJournalRecord()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Tools.ExecuteAsync(ChatMode.Agent, Call("create_file", "{\"path\":\"c.cs\",\"content\":\"x\"}"), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task MultibyteContentExceedingByteCapIsRejectedWithoutWriting()
    {
        using var fixture = new Fixture();
        // 350k chars of a 3-byte UTF-8 char: passes the char cap (1_048_576)
        // but exceeds the byte cap (350k * 3 = 1_050_000 > 1_048_576). A file we
        // just wrote must stay within the read cap.
        var big = new string('\u3042', 350_000);
        var result = await fixture.Execute("create_file", "{\"path\":\"big.txt\",\"content\":\"" + big + "\"}");
        Assert.True(result.IsError);
        Assert.Equal("file_too_large", Code(result));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "big.txt")));
    }

    [Fact]
    public async Task OversizedFileOnDiskIsRejectedWhenEditing()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "big.txt");
        File.WriteAllBytes(target, new byte[1_048_577]);
        var result = await fixture.Execute("edit_file", "{\"path\":\"big.txt\",\"content\":\"x\"}");
        Assert.True(result.IsError);
        Assert.Equal("file_too_large", Code(result));
        Assert.Equal(1_048_577L, new FileInfo(target).Length);
    }

    [Fact]
    public void DiffIsLcsBasedBoundedAndMarksTruncation()
    {
        // "one" kept, "two" removed, "two!" added, "three" kept.
        var diff = (JsonElement)JsonSerializer.SerializeToDocument(
            WorkspaceWriteTools.BuildDiff("one\ntwo\nthree", "one\ntwo!\nthree")).RootElement;
        Assert.Equal(3, diff.GetProperty("linesBefore").GetInt32());
        Assert.Equal(3, diff.GetProperty("linesAfter").GetInt32());
        var changes = diff.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("remove", changes[0].GetProperty("op").GetString());
        Assert.Equal("two", changes[0].GetProperty("text").GetString());
        Assert.Equal("add", changes[1].GetProperty("op").GetString());
        Assert.Equal("two!", changes[1].GetProperty("text").GetString());
        Assert.False(diff.GetProperty("truncated").GetBoolean());

        var many = WorkspaceWriteTools.BuildDiff(string.Join("\n", Enumerable.Range(0, 200)), "z");
        var manyDoc = (JsonElement)JsonSerializer.SerializeToDocument(many).RootElement;
        Assert.True(manyDoc.GetProperty("truncated").GetBoolean());
        Assert.True(manyDoc.GetProperty("changes").GetArrayLength() <= 64);
    }

    [Fact]
    public void PublishedSchemasRemainReadableAndValidateTheirOwnFields()
    {
        Assert.Equal(new[] { "create_file", "edit_file", "apply_diff" }, WriteToolCatalog.Specs.Select(spec => spec.Name));
        foreach (var spec in WriteToolCatalog.Specs)
        {
            Assert.False(spec.ParametersSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("object", spec.ParametersSchema.GetProperty("type").GetString());
            Assert.Contains(spec.Name == "apply_diff" ? "replacements" : "content", spec.ParametersSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoConflictPreservesEveryFileBeforeAnyMutation(bool delete)
    {
        using var fixture = new Fixture();
        var first = Path.Combine(fixture.Path, "first");
        var second = Path.Combine(fixture.Path, "second");
        await fixture.Execute("create_file", "{\"path\":\"first\",\"content\":\"one\"}");
        await fixture.Execute("create_file", "{\"path\":\"second\",\"content\":\"two\"}");
        if (delete) File.Delete(first);
        else File.WriteAllText(first, "ONE");
        var error = await Assert.ThrowsAsync<ToolException>(() => fixture.Journal.UndoTurnAsync(CancellationToken.None));
        Assert.Equal("undo_conflict", error.Code);
        Assert.Equal("two", File.ReadAllText(second));
        Assert.Equal(2, fixture.Journal.Count);
        Assert.Equal(6L, fixture.Journal.Bytes);
        if (delete) Assert.False(File.Exists(first));
        else Assert.Equal("ONE", File.ReadAllText(first));
    }

    [Fact]
    public async Task RepeatedEditsAndCreateThenEditUndoInReverse()
    {
        using var fixture = new Fixture();
        var old = Path.Combine(fixture.Path, "old");
        var prior = new byte[] { 0xff, 0xfe, 65, 0 };
        File.WriteAllBytes(old, prior);
        foreach (var content in new[] { "B", "C", "D" })
            Assert.False((await fixture.Execute("edit_file", JsonSerializer.Serialize(new { path = "old", content }))).IsError);
        Assert.False((await fixture.Execute("create_file", "{\"path\":\"new\",\"content\":\"one\"}")).IsError);
        Assert.False((await fixture.Execute("edit_file", "{\"path\":\"new\",\"content\":\"two\"}")).IsError);
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal(prior, File.ReadAllBytes(old));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "new")));
        Assert.Equal(0, fixture.Journal.Count);
        Assert.Equal(0L, fixture.Journal.Bytes);
    }

    [Fact]
    public async Task SuffixUndoRestoresTwoTurnsOnSameFileWithoutDiscardingEarlierBytes()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        var prior = new byte[] { 0xff, 0xfe, 65, 0 };
        File.WriteAllBytes(target, prior);
        Assert.False((await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"B\"}")).IsError);
        var boundary = fixture.Journal.Count;
        var retainedBytes = fixture.Journal.Bytes;
        foreach (var content in new[] { "C", "D" })
            Assert.False((await fixture.Execute("edit_file", JsonSerializer.Serialize(new { path = "target", content }))).IsError);
        await fixture.Journal.UndoTurnAsync(boundary, CancellationToken.None);
        Assert.Equal("B", File.ReadAllText(target));
        Assert.Equal(boundary, fixture.Journal.Count);
        Assert.Equal(retainedBytes, fixture.Journal.Bytes);
        await fixture.Journal.UndoTurnAsync(boundary, CancellationToken.None);
        Assert.Equal("B", File.ReadAllText(target));
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal(prior, File.ReadAllBytes(target));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Fact]
    public async Task SuffixPreflightIgnoresOlderConflictButRejectsConflictInLatestTurn()
    {
        using var fixture = new Fixture();
        await fixture.Execute("create_file", "{\"path\":\"older\",\"content\":\"old\"}");
        var boundary = fixture.Journal.Count;
        await fixture.Execute("create_file", "{\"path\":\"latest\",\"content\":\"new\"}");
        var older = Path.Combine(fixture.Path, "older");
        var latest = Path.Combine(fixture.Path, "latest");
        File.WriteAllText(older, "external");
        File.WriteAllText(latest, "conflict");
        Assert.Equal("undo_conflict", (await Assert.ThrowsAsync<ToolException>(() => fixture.Journal.UndoTurnAsync(boundary, CancellationToken.None))).Code);
        Assert.Equal(2, fixture.Journal.Count);
        Assert.Equal("external", File.ReadAllText(older));
        Assert.Equal("conflict", File.ReadAllText(latest));
        File.WriteAllText(latest, "new");
        await fixture.Journal.UndoTurnAsync(boundary, CancellationToken.None);
        Assert.False(File.Exists(latest));
        Assert.Equal("external", File.ReadAllText(older));
        Assert.Equal(boundary, fixture.Journal.Count);
        Assert.Equal("undo_conflict", (await Assert.ThrowsAsync<ToolException>(() => fixture.Journal.UndoTurnAsync(CancellationToken.None))).Code);
    }

    [Fact]
    public async Task PartialSuffixCancellationCanRetryWithoutCrossingTurnBoundary()
    {
        using var fixture = new Fixture();
        await fixture.Execute("create_file", "{\"path\":\"older\",\"content\":\"old\"}");
        var boundary = fixture.Journal.Count;
        var retainedBytes = fixture.Journal.Bytes;
        await fixture.Execute("create_file", "{\"path\":\"first\",\"content\":\"one\"}");
        await fixture.Execute("create_file", "{\"path\":\"second\",\"content\":\"two\"}");
        using var cancellation = new CancellationTokenSource();
        fixture.Journal.BeforeUndoCommit = record =>
        {
            if (record.Path.EndsWith("first", StringComparison.Ordinal)) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Journal.UndoTurnAsync(boundary, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "second")));
        Assert.Equal(2, fixture.Journal.Count);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Path, "older")));
        fixture.Journal.BeforeUndoCommit = null;
        await fixture.Journal.UndoTurnAsync(boundary, CancellationToken.None);
        Assert.Equal(boundary, fixture.Journal.Count);
        Assert.Equal(retainedBytes, fixture.Journal.Bytes);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Path, "older")));
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    [Fact]
    public async Task PartialSuffixRetryPreservesEarlierTurnOnSameFile()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        File.WriteAllText(target, "A");
        await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"B\"}");
        var boundary = fixture.Journal.Count;
        foreach (var content in new[] { "C", "D" })
            await fixture.Execute("edit_file", JsonSerializer.Serialize(new { path = "target", content }));
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        fixture.Journal.BeforeUndoCommit = _ => { if (++visits == 2) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Journal.UndoTurnAsync(boundary, cancellation.Token));
        Assert.Equal("C", File.ReadAllText(target));
        Assert.Equal(2, fixture.Journal.Count);
        fixture.Journal.BeforeUndoCommit = null;
        await fixture.Journal.UndoTurnAsync(boundary, CancellationToken.None);
        Assert.Equal("B", File.ReadAllText(target));
        Assert.Equal(boundary, fixture.Journal.Count);
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal("A", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task InvalidSuffixBoundaryDoesNotChangeFilesOrRecords(int retainRecords)
    {
        using var fixture = new Fixture();
        await fixture.Execute("create_file", "{\"path\":\"target\",\"content\":\"old\"}");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Journal.UndoTurnAsync(retainRecords, CancellationToken.None));
        Assert.Equal(1, fixture.Journal.Count);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Path, "target")));
    }

    [Fact]
    public async Task PartialUndoRemovesCompletedRecordsAndCanBeRetried()
    {
        using var fixture = new Fixture();
        await fixture.Execute("create_file", "{\"path\":\"first\",\"content\":\"one\"}");
        await fixture.Execute("create_file", "{\"path\":\"second\",\"content\":\"two\"}");
        using var cancellation = new CancellationTokenSource();
        fixture.Journal.BeforeUndoCommit = record =>
        {
            if (record.Path.EndsWith("first", StringComparison.Ordinal)) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Journal.UndoTurnAsync(cancellation.Token));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "second")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(fixture.Path, "first")));
        Assert.Equal(1, fixture.Journal.Count);
        Assert.Equal(3L, fixture.Journal.Bytes);
        fixture.Journal.BeforeUndoCommit = null;
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Theory]
    [InlineData("create_file", "")]
    [InlineData("create_file", "new")]
    [InlineData("edit_file", "")]
    [InlineData("edit_file", "new")]
    public async Task CancellationImmediatelyBeforeCommitCleansTempAndPreservesTarget(string tool, string content)
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        if (tool == "edit_file") File.WriteAllText(target, "before");
        using var cancellation = new CancellationTokenSource();
        fixture.Tools.BeforeCommit = () =>
        {
            Assert.Single(Directory.GetFiles(fixture.Path, "*.tmp"));
            cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Tools.ExecuteAsync(ChatMode.Agent,
            Call(tool, JsonSerializer.Serialize(new { path = "target", content })), cancellation.Token));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
        if (tool == "edit_file") Assert.Equal("before", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
        Assert.Equal(0, fixture.Journal.Count);
    }

    [Theory]
    [InlineData("create_file")]
    [InlineData("edit_file")]
    public async Task CancellationAfterCommitStillReportsSuccessAndJournalsExactBytes(string tool)
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        if (tool == "edit_file") File.WriteAllText(target, "before");
        using var cancellation = new CancellationTokenSource();
        fixture.Tools.AfterCommit = cancellation.Cancel;
        var result = await fixture.Tools.ExecuteAsync(ChatMode.Agent,
            Call(tool, "{\"path\":\"target\",\"content\":\"after\"}"), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.IsError);
        Assert.Equal(Encoding.UTF8.GetBytes("after"), Assert.Single(fixture.Journal.Records).AfterBytes);
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        if (tool == "edit_file") Assert.Equal("before", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task CancelledRestoreCleansTempAndLeavesRecordPending()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        File.WriteAllText(target, "before");
        await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"after\"}");
        using var cancellation = new CancellationTokenSource();
        fixture.Journal.BeforeUndoCommit = _ =>
        {
            Assert.Single(Directory.GetFiles(fixture.Path, "*.tmp"));
            cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Journal.UndoTurnAsync(cancellation.Token));
        Assert.Equal("after", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
        Assert.Equal(1, fixture.Journal.Count);
        fixture.Journal.BeforeUndoCommit = null;
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal("before", File.ReadAllText(target));
    }

    [Fact]
    public async Task ConcurrentEditBeforeCommitIsPreservedWithoutJournalEntry()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Path, "target");
        File.WriteAllText(target, "before");
        fixture.Tools.BeforeCommit = () => File.WriteAllText(target, "external");
        var result = await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"after\"}");
        Assert.Equal("edit_conflict", Code(result));
        Assert.Equal("external", File.ReadAllText(target));
        Assert.Equal(0, fixture.Journal.Count);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public void LongLineTruncationDoesNotSplitSurrogatePair()
    {
        using var json = JsonSerializer.SerializeToDocument(WorkspaceWriteTools.BuildDiff("", new string('x', 92) + "\U0001F600" + new string('x', 20)));
        var diff = json.RootElement;
        Assert.True(diff.GetProperty("truncated").GetBoolean());
        Assert.Equal(new string('x', 92) + "...", diff.GetProperty("changes")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task JournalCountLimitRejectsBeforeTempOrTargetWriteAndUndoReleasesCapacity()
    {
        using var fixture = new Fixture(maxRecords: 1);
        await fixture.Execute("create_file", "{\"path\":\"first\",\"content\":\"one\"}");
        fixture.Tools.BeforeCommit = () => Assert.Fail("Full journal must reject before writing.");
        var result = await fixture.Execute("create_file", "{\"path\":\"second\",\"content\":\"two\"}");
        Assert.Equal("journal_limit", Code(result));
        Assert.Single(Directory.GetFileSystemEntries(fixture.Path));
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        fixture.Tools.BeforeCommit = null;
        Assert.False((await fixture.Execute("create_file", "{\"path\":\"second\",\"content\":\"two\"}")).IsError);
    }

    [Fact]
    public async Task JournalByteLimitCountsBothStatesAndClearReleasesCapacity()
    {
        using var fixture = new Fixture(maxBytes: 5);
        var target = Path.Combine(fixture.Path, "target");
        File.WriteAllText(target, "old");
        var rejected = await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"new\"}");
        Assert.Equal("journal_limit", Code(rejected));
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Equal(0, fixture.Journal.Count);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
        Assert.False((await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"ok\"}")).IsError);
        Assert.Equal(5L, fixture.Journal.Bytes);
        fixture.Journal.Clear();
        Assert.Equal(0L, fixture.Journal.Bytes);
        Assert.False((await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"new\"}")).IsError);
    }

    [Fact]
    public async Task RecordByteArraysCannotBeModifiedByConsumers()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "target"), "old");
        await fixture.Execute("edit_file", "{\"path\":\"target\",\"content\":\"new\"}");
        var record = Assert.Single(fixture.Journal.Records);
        Assert.Equal(fixture.Path, record.WorkspaceRoot);
        record.PriorBytes![0] = 0;
        record.AfterBytes[0] = 0;
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Path, "target")));
    }

    [Theory]
    [InlineData("create_file")]
    [InlineData("edit_file")]
    public async Task UndoRejectsReparseMutationAndPreservesOutsideTarget(string tool)
    {
        using var fixture = new Fixture();
        using var outside = new TempFolder();
        var folder = Path.Combine(fixture.Path, "folder");
        var saved = Path.Combine(fixture.Path, "saved");
        Directory.CreateDirectory(folder);
        if (tool == "edit_file") File.WriteAllText(Path.Combine(folder, "target"), "before");
        Assert.False((await fixture.Execute(tool, "{\"path\":\"folder/target\",\"content\":\"after\"}")).IsError);
        File.WriteAllText(Path.Combine(outside.Path, "target"), "after");
        Directory.Move(folder, saved);
        CreateDirectoryLink(folder, outside.Path);
        try
        {
            Assert.True((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0);
            var error = await Assert.ThrowsAsync<ToolException>(() => fixture.Journal.UndoTurnAsync(CancellationToken.None));
            Assert.Equal("outside_workspace", error.Code);
            Assert.Equal("after", File.ReadAllText(Path.Combine(outside.Path, "target")));
            Assert.Equal("after", File.ReadAllText(Path.Combine(saved, "target")));
            Assert.Equal(1, fixture.Journal.Count);
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public async Task ApprovedOutsideEditUsesGuardedReadAndUndoProvenance()
    {
        using var fixture = new Fixture();
        using var outside = new TempFolder();
        var target = Path.Combine(outside.Path, "target");
        var prior = new byte[] { 0xff, 0xfe, 65, 0 };
        File.WriteAllBytes(target, prior);
        var result = await fixture.Execute("edit_file", JsonSerializer.Serialize(new { path = target, content = "after" }));
        Assert.False(result.IsError);
        Assert.Single(fixture.Requests);
        Assert.Equal(fixture.Path, Assert.Single(fixture.Journal.Records).WorkspaceRoot);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(target.Replace('\\', '/'), json.RootElement.GetProperty("path").GetString());
        await fixture.Journal.UndoTurnAsync(CancellationToken.None);
        Assert.Equal(prior, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData("line", "line\n", "none", "LF")]
    [InlineData("line\r\n", "line\n", "CRLF", "LF")]
    [InlineData("line\r", "line\n", "CR", "LF")]
    public void NewlineOnlyDiffsAreVisible(string before, string after, string oldEnding, string newEnding)
    {
        using var json = JsonSerializer.SerializeToDocument(WorkspaceWriteTools.BuildDiff(before, after));
        var changes = json.RootElement.GetProperty("changes");
        Assert.Equal(2, changes.GetArrayLength());
        Assert.Equal(oldEnding, changes[0].GetProperty("ending").GetString());
        Assert.Equal(newEnding, changes[1].GetProperty("ending").GetString());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task CreateDiffAndLongLineDiffAreBoundedWithTruthfulTruncation()
    {
        using var fixture = new Fixture();
        var content = new string('x', 10000);
        var result = await fixture.Execute("create_file", JsonSerializer.Serialize(new { path = "target", content }));
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        var diff = json.RootElement.GetProperty("diff");
        Assert.True(diff.GetProperty("truncated").GetBoolean());
        Assert.Equal("add", diff.GetProperty("changes")[0].GetProperty("op").GetString());
        Assert.True(diff.GetProperty("changes")[0].GetProperty("text").GetString()!.Length <= 96);
        using var large = JsonSerializer.SerializeToDocument(WorkspaceWriteTools.BuildDiff("", new string('\n', 1025)));
        Assert.True(large.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(large.RootElement.GetProperty("tooLarge").GetBoolean());
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException || exception is PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) throw;
        }
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.NotEqual(new IntPtr(-1), handle);
        try { Assert.True(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)); }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string Code(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.GetProperty("code").GetString()!;

    private static ToolInvocation Call(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ToolInvocation("t1", name, document.RootElement.Clone());
    }

    private sealed class Fixture : IDisposable, IApprovalPrompt
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.WriteTools." + Guid.NewGuid().ToString("N"));
        internal EditJournal Journal { get; }
        internal WorkspaceWriteTools Tools { get; }
        internal ApprovalResponse Response { get; set; } = ApprovalResponse.AllowOnce;
        internal List<ApprovalRequest> Requests { get; } = new();
        private readonly ApprovalBroker broker;
        internal Fixture(int maxRecords = 128, long maxBytes = 16 * 1048576, Action<string, Action>? commitTarget = null)
        {
            Directory.CreateDirectory(Path);
            var guard = new WorkspaceGuard(Path);
            Journal = new EditJournal(maxRecords, maxBytes, commitTarget);
            broker = new ApprovalBroker(guard, this);
            Tools = new WorkspaceWriteTools(guard, broker, Journal);
        }
        internal Task<ToolResult> Execute(string name, string arguments, ChatMode mode = ChatMode.Agent) => Tools.ExecuteAsync(mode, Call(name, arguments), CancellationToken.None);
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Response);
        }
        public void Dispose() { broker.Dispose(); Directory.Delete(Path, true); }
    }

    private sealed class TempFolder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.WriteTools.Outside." + Guid.NewGuid().ToString("N"));
        internal TempFolder() { Directory.CreateDirectory(Path); }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
