using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class WorkspaceReadToolsTests
{
    [Theory]
    [InlineData(ChatMode.Ask)]
    [InlineData(ChatMode.Plan)]
    [InlineData(ChatMode.Agent)]
    public async Task ReadsRespectLineRangesAndNeverPrompt(ChatMode mode)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "code.cs"), "one\r\ntwo\r\nthree", new UTF8Encoding(false));
        var result = await fixture.Execute("read_file", "{\"path\":\"code.cs\",\"startLine\":2,\"endLine\":2}", mode);
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        var line = Assert.Single(json.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(2, line.GetProperty("line").GetInt32());
        Assert.Equal("two", line.GetProperty("text").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("totalLines").GetInt32());
    }

    [Theory]
    [InlineData("read_file", "{}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"x\",\"unknown\":true}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"x\",\"startLine\":\"2\"}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"x\",\"startLine\":1.5}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"x\",\"startLine\":0}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"x\",\"startLine\":3,\"endLine\":2}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"x\",\"path\":\"y\"}", "invalid_arguments")]
    [InlineData("read_file", "{\"path\":\"../outside\"}", "invalid_path")]
    [InlineData("find_files", "{\"path\":\".\",\"glob\":\"../*\"}", "invalid_arguments")]
    [InlineData("list_dir", "{\"path\":\".\",\"maxResults\":257}", "invalid_arguments")]
    [InlineData("create_file", "{\"path\":\"new.cs\"}", "unknown_tool")]
    [InlineData("run_command", "{}", "unknown_tool")]
    public async Task InvalidRequestsFailBeforeAnyExecution(string name, string arguments, string code)
    {
        using var fixture = new Fixture();
        var result = await fixture.Execute(name, arguments);
        Assert.True(result.IsError);
        Assert.Equal(code, JsonDocument.Parse(result.Content).RootElement.GetProperty("code").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    [Fact]
    public async Task ListingFindingAndLiteralSearchAreBounded()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "sub"));
        File.WriteAllText(Path.Combine(fixture.Path, "a.cs"), "needle.*\nneedle.*");
        File.WriteAllText(Path.Combine(fixture.Path, "sub", "b.cs"), "needle.*");
        File.WriteAllBytes(Path.Combine(fixture.Path, "binary"), new byte[] { 0, 1, 2 });
        foreach (var name in new[] { "list_dir", "find_files", "grep" })
        {
            var arguments = name == "list_dir" ? "{\"path\":\".\",\"maxResults\":1}" : name == "find_files" ?
                "{\"path\":\".\",\"glob\":\"**/*.cs\",\"maxResults\":1}" : "{\"path\":\".\",\"pattern\":\"needle.*\",\"glob\":\"**/*.cs\",\"maxResults\":1}";
            var result = await fixture.Execute(name, arguments);
            Assert.False(result.IsError);
            using var document = JsonDocument.Parse(result.Content);
            Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Single(document.RootElement.GetProperty(name == "list_dir" ? "entries" : "results").EnumerateArray());
        }
        var grep = await fixture.Execute("grep", "{\"path\":\".\",\"pattern\":\"needle.*\"}");
        using var parsed = JsonDocument.Parse(grep.Content);
        Assert.Equal(3, parsed.RootElement.GetProperty("results").GetArrayLength());
        Assert.Equal(1, parsed.RootElement.GetProperty("skippedFiles").GetInt32());
    }

    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "sub/a.cs", false)]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "sub/deep/A.CS", true)]
    [InlineData("sub/?.cs", "sub/a.cs", true)]
    [InlineData("sub/?.cs", "sub/ab.cs", false)]
    public void GlobIsDirectoryAwareAndHasNoRegexInterpretation(string glob, string path, bool expected) =>
        Assert.Equal(expected, WorkspaceReadTools.GlobMatches(glob, path));

    [Fact]
    public async Task BinaryOversizedAndCancelledReadsAreControlled()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.Path, "binary"), new byte[] { 0, 1, 2 });
        File.WriteAllBytes(Path.Combine(fixture.Path, "large"), new byte[1048577]);
        foreach (var pair in new[] { ("binary", "binary_file"), ("large", "file_too_large") })
        {
            var result = await fixture.Execute("read_file", "{\"path\":\"" + pair.Item1 + "\"}");
            Assert.True(result.IsError);
            using var json = JsonDocument.Parse(result.Content);
            Assert.Equal(pair.Item2, json.RootElement.GetProperty("code").GetString());
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Tools.ExecuteAsync(ChatMode.Agent, Call("read_file", "{\"path\":\"binary\"}"), cancellation.Token));
    }

    [Fact]
    public void PublishedSchemasRemainReadableAndValidateTheirOwnFields()
    {
        Assert.Equal(new[] { "read_file", "list_dir", "find_files", "grep" }, ReadToolCatalog.Specs.Select(spec => spec.Name));
        foreach (var spec in ReadToolCatalog.Specs)
        {
            Assert.False(spec.ParametersSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("object", spec.ParametersSchema.GetProperty("type").GetString());
        }
    }

    private static ToolInvocation Call(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ToolInvocation("t1", name, document.RootElement.Clone());
    }

    private sealed class Fixture : IDisposable, IApprovalPrompt
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.ReadTools." + Guid.NewGuid().ToString("N"));
        internal WorkspaceReadTools Tools { get; }
        private readonly ApprovalBroker broker;
        internal Fixture()
        {
            Directory.CreateDirectory(Path);
            var guard = new WorkspaceGuard(Path);
            broker = new ApprovalBroker(guard, this);
            Tools = new WorkspaceReadTools(guard, broker);
        }
        internal Task<ToolResult> Execute(string name, string arguments, ChatMode mode = ChatMode.Agent) => Tools.ExecuteAsync(mode, Call(name, arguments), CancellationToken.None);
        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Read tools must never prompt.");
        public void Dispose() { broker.Dispose(); Directory.Delete(Path, true); }
    }
}
