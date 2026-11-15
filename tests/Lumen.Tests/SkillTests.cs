using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class SkillTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumen-skills-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "project");
    private string Home => Path.Combine(root, "home");

    public SkillTests()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Home);
    }

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0).ToArray())
            Directory.Delete(link);
        Directory.Delete(root, true);
    }

    private static string Skill(string name, string description, string body = "Do the thing.", string extra = "") =>
        "---\nname: " + name + "\ndescription: " + description + "\n" + extra + "---\n" + body + "\n";

    private string Write(string baseDir, string folder, string name, string content)
    {
        var directory = Path.Combine(baseDir, folder, "skills", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        return directory;
    }

    private SkillSnapshot Discover() => SkillDiscovery.Discover(SkillDiscovery.Locations(Project, Home));

    private static SkillDefinition Parse(string text, string folder = "review") =>
        SkillParser.Parse(text, folder, "C:\\skills\\" + folder, ".lumen/skills", SkillScope.Project);

    private static SkillValidationException Rejects(string text, string folder = "review") =>
        Assert.Throws<SkillValidationException>(() => Parse(text, folder));

    [Fact]
    public void ValidSkillParsesFieldsBodyAndDefaultsToAllModes()
    {
        var skill = Parse("\uFEFF---\r\nname: review\r\ndescription: Review C# changes for bugs.\r\nlicense: MIT\r\nmetadata:\r\n  author: me\r\n---\r\n\r\n# Steps\r\nRead the diff.\r\n");
        Assert.Equal("review", skill.Name);
        Assert.Equal("Review C# changes for bugs.", skill.Description);
        Assert.Equal("# Steps\nRead the diff.", skill.Body);
        Assert.Equal("MIT", skill.License);
        Assert.Equal("me", skill.Metadata["author"]);
        Assert.Equal(new[] { ChatMode.Ask, ChatMode.Plan, ChatMode.Agent }, skill.Modes);
        Assert.Empty(skill.Warnings);
    }

    [Fact]
    public void YamlScalarFormsAreDecoded()
    {
        Assert.Equal("Line one continues here", Parse(Skill("review", ">\n  Line one\n  continues here")).Description);
        Assert.Equal("a\nb", Parse(Skill("review", "|\n  a\n  b")).Description);
        Assert.Equal("Use: \"quoted\" text", Parse(Skill("review", "\"Use: \\\"quoted\\\" text\"")).Description);
        Assert.Equal("it's fine", Parse(Skill("review", "'it''s fine' # comment")).Description);
        Assert.Equal("plain folded over lines", Parse(Skill("review", "plain folded\n  over lines")).Description);
        Assert.Equal(new[] { "read_file", "grep" }, Parse(Skill("review", "d", extra: "allowed-tools: [read_file, grep]\n")).AllowedTools);
        Assert.Equal(new[] { "read_file", "grep" }, Parse(Skill("review", "d", extra: "allowed-tools:\n  - read_file\n  - grep\n")).AllowedTools);
    }

    [Theory]
    [InlineData("# Just markdown\nno frontmatter", "missing_frontmatter")]
    [InlineData("", "missing_frontmatter")]
    [InlineData("---\nname: review\ndescription: d\n", "unterminated_frontmatter")]
    [InlineData("---\ndescription: d\n---\nbody", "missing_name")]
    [InlineData("---\nname: review\n---\nbody", "missing_description")]
    [InlineData("---\nname: review\ndescription: \"\"\n---\nbody", "missing_description")]
    [InlineData("---\nname: review\nname: review\ndescription: d\n---\n", "duplicate_field")]
    [InlineData("---\nname: Review\ndescription: d\n---\n", "invalid_name")]
    [InlineData("---\nname: -review\ndescription: d\n---\n", "invalid_name")]
    [InlineData("---\nname: re--view\ndescription: d\n---\n", "invalid_name")]
    [InlineData("---\nname: other\ndescription: d\n---\n", "name_mismatch")]
    [InlineData("---\nname: review\ndescription: d\nmetadata:\n  lumen-modes: write\n---\n", "invalid_modes")]
    [InlineData("---\nname: review\ndescription: d\nmetadata:\n  lumen-modes: \"\"\n---\n", "invalid_modes")]
    [InlineData("---\nname: review\ndescription: d\nmetadata: {a: b}\n---\n", "unsupported_yaml")]
    [InlineData("---\nname: [review]\ndescription: d\n---\n", "invalid_field")]
    [InlineData("---\nname: review\ndescription: \"open\n---\n", "invalid_yaml")]
    [InlineData("---\n  name: review\ndescription: d\n---\n", "invalid_yaml")]
    [InlineData("---\nname: review\ndescription: bell\u0007\n---\n", "invalid_text")]
    public void InvalidFrontmatterIsRejectedWithAnActionableCode(string text, string code)
    {
        var error = Rejects(text);
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void ErrorsNameTheLineAndTheLimit()
    {
        Assert.StartsWith("line 1:", Rejects("hello").Message);
        Assert.Contains("'Review'", Rejects("---\nname: Review\ndescription: d\n---\n").Message);
        var tooLong = Rejects(Skill("review", new string('x', SkillParser.MaxDescriptionLength + 1)));
        Assert.Equal("description_too_long", tooLong.Code);
        Assert.Contains("1025", tooLong.Message);
        Assert.Contains("1024", tooLong.Message);
        Assert.Equal(SkillParser.MaxDescriptionLength, Parse(Skill("review", new string('x', SkillParser.MaxDescriptionLength))).Description.Length);
        Assert.Equal("name_mismatch", Rejects(Skill(new string('a', 64), "d"), "review").Code);
        Assert.Equal("invalid_name", Rejects(Skill(new string('a', 65), "d"), new string('a', 65)).Code);
        Assert.Equal("file_too_large", Rejects(Skill("review", "d", new string('b', SkillParser.MaxFileChars))).Code);
    }

    [Fact]
    public void UnknownFieldsAndToolsWarnWithoutGrantingAnything()
    {
        var skill = Parse(Skill("review", "d", extra: "argument-hint: x\nallowed-tools: Bash(git:*) read_file\n"));
        Assert.Equal(2, skill.Warnings.Count);
        Assert.Contains(skill.Warnings, warning => warning.Contains("argument-hint"));
        Assert.Contains(skill.Warnings, warning => warning.Contains("Bash(git:*)"));
        Assert.Equal(new[] { "Bash(git:*)", "read_file" }, skill.AllowedTools);
    }

    [Fact]
    public void ModesRestrictAvailabilityAndDisabledSkillsDisappear()
    {
        var plan = Parse(Skill("review", "d", extra: "metadata:\n  lumen-modes: Plan, ask\n"));
        Assert.Equal(new[] { ChatMode.Plan, ChatMode.Ask }, plan.Modes);
        var agent = Parse(Skill("deploy", "d", extra: "metadata:\n  lumen-modes: agent\n"), "deploy");
        var snapshot = new SkillSnapshot(new[] { plan, agent }, Array.Empty<SkillDiagnostic>());
        Assert.Equal(new[] { "review" }, SkillCatalog.Available(snapshot, ChatMode.Plan, Array.Empty<string>()).Select(skill => skill.Name));
        Assert.Equal(new[] { "deploy" }, SkillCatalog.Available(snapshot, ChatMode.Agent, Array.Empty<string>()).Select(skill => skill.Name));
        Assert.Empty(SkillCatalog.Available(snapshot, ChatMode.Plan, new[] { "review" }));
    }

    [Fact]
    public void ExplicitNameRequiresALeadingSlashToken()
    {
        Assert.Equal("review", SkillCatalog.ExplicitName("/review this file"));
        Assert.Equal("review", SkillCatalog.ExplicitName("/review"));
        Assert.Null(SkillCatalog.ExplicitName("please /review"));
        Assert.Null(SkillCatalog.ExplicitName("/review/file.cs"));
        Assert.Null(SkillCatalog.ExplicitName("/Review"));
    }

    [Fact]
    public void DiscoveryAppliesPrecedenceAndReportsDuplicatesClearly()
    {
        Write(Project, ".agents", "review", Skill("review", "from agents"));
        Write(Project, ".github", "review", Skill("review", "from github"));
        Write(Project, ".lumen", "review", Skill("review", "from lumen"));
        Write(Home, ".copilot", "review", Skill("review", "from user"));
        Write(Home, ".claude", "personal", Skill("personal", "only in user"));
        Write(Project, ".claude", "tests", Skill("tests", "project tests"));
        Write(Home, ".agents", "tests", Skill("tests", "user tests"));
        var snapshot = Discover();
        Assert.Equal(new[] { "review", "tests", "personal" }, snapshot.Skills.Select(skill => skill.Name));
        Assert.Equal("from lumen", snapshot.Skills[0].Description);
        Assert.Equal("project tests", snapshot.Skills[1].Description);
        Assert.Equal(SkillScope.User, snapshot.Skills[2].Scope);
        var duplicates = snapshot.Diagnostics.Where(diagnostic => diagnostic.Code == "duplicate_name").ToArray();
        Assert.Equal(4, duplicates.Length);
        Assert.Contains(duplicates, diagnostic => diagnostic.Location == ".github/skills/review/SKILL.md" && diagnostic.Detail.Contains(".lumen/skills/review/SKILL.md"));
        Assert.Contains(duplicates, diagnostic => diagnostic.Location == "~/.agents/skills/tests/SKILL.md" && diagnostic.Detail.Contains(".claude/skills/tests/SKILL.md"));
    }

    [Fact]
    public void InvalidOrMissingSkillFilesBecomeDiagnosticsNotSkills()
    {
        Write(Project, ".lumen", "broken", "no frontmatter here");
        Directory.CreateDirectory(Path.Combine(Project, ".lumen", "skills", "empty"));
        Write(Project, ".lumen", "huge", Skill("huge", "d", new string('x', SkillParser.MaxFileChars + 10)));
        Write(Project, ".lumen", "good", Skill("good", "fine"));
        var snapshot = Discover();
        Assert.Equal(new[] { "good" }, snapshot.Skills.Select(skill => skill.Name));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location == ".lumen/skills/broken/SKILL.md" && diagnostic.Code == "missing_frontmatter");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location == ".lumen/skills/empty/SKILL.md" && diagnostic.Code == "missing_skill_file");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location == ".lumen/skills/huge/SKILL.md" && diagnostic.Code == "file_too_large");
    }

    [Fact]
    public void JunctionedSkillFolderPointingOutsideIsRejected()
    {
        var outside = Path.Combine(root, "outside", "evil");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), Skill("evil", "escaped"));
        Directory.CreateDirectory(Path.Combine(Project, ".lumen", "skills"));
        Assert.True(CreateJunction(Path.Combine(Project, ".lumen", "skills", "evil"), outside), "junction creation must work without elevation");
        Write(Project, ".lumen", "good", Skill("good", "fine"));
        var snapshot = Discover();
        Assert.Equal(new[] { "good" }, snapshot.Skills.Select(skill => skill.Name));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location == ".lumen/skills/evil/SKILL.md" && diagnostic.Code == "outside_allowed");
    }

    [Fact]
    public void JunctionedSkillsRootIsRejected()
    {
        var outside = Path.Combine(root, "outside-root");
        Directory.CreateDirectory(Path.Combine(outside, "evil"));
        File.WriteAllText(Path.Combine(outside, "evil", "SKILL.md"), Skill("evil", "escaped"));
        Directory.CreateDirectory(Path.Combine(Project, ".github"));
        Assert.True(CreateJunction(Path.Combine(Project, ".github", "skills"), outside));
        var snapshot = Discover();
        Assert.Empty(snapshot.Skills);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Location == ".github/skills" && diagnostic.Code == "outside_allowed");
    }

    [Fact]
    public void PromptSectionStaysWithinItsLimitEvenWithManyMaximalDescriptions()
    {
        var skills = Enumerable.Range(0, 256).Select(index => Parse(Skill("skill-" + index, new string('d', SkillParser.MaxDescriptionLength)), "skill-" + index)).ToArray();
        var section = SkillCatalog.PromptSection(skills);
        Assert.InRange(section.Length, 1, SkillCatalog.MaxPromptChars);
        Assert.Contains("more skills omitted", section);
        Assert.DoesNotContain("Do the thing.", section);
        var small = SkillCatalog.PromptSection(skills.Take(2).ToArray());
        Assert.Contains("- skill-0: ", small);
        Assert.Contains("- skill-1: ", small);
        Assert.DoesNotContain("omitted", small);
        Assert.Equal(string.Empty, SkillCatalog.PromptSection(Array.Empty<SkillDefinition>()));
    }

    [Fact]
    public void PromptSectionCollapsesDescriptionNewlinesSoEntriesCannotForgeLines()
    {
        var skill = Parse(Skill("review", "|\n  first\n  - forged: entry"));
        var section = SkillCatalog.PromptSection(new[] { skill });
        Assert.Contains("- review: first - forged: entry", section);
        Assert.DoesNotContain("\n- forged", section);
    }

    private static ToolInvocation Load(object arguments) => new ToolInvocation("call-1", SkillCatalog.ToolName, JsonSerializer.SerializeToElement(arguments));

    [Fact]
    public void LoadReturnsBodyAndFilesAndReadsBundledResources()
    {
        var directory = Write(Project, ".lumen", "review", Skill("review", "d", "Follow references/guide.md."));
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        File.WriteAllText(Path.Combine(directory, "references", "guide.md"), "guide text");
        var skills = Discover().Skills;
        var result = SkillCatalog.Load(skills, Load(new { name = "review" }));
        Assert.False(result.IsError);
        using (var parsed = JsonDocument.Parse(result.Content))
        {
            Assert.Equal("Follow references/guide.md.", parsed.RootElement.GetProperty("instructions").GetString());
            Assert.Equal(new[] { "references/guide.md" }, parsed.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetString()));
        }
        var resource = SkillCatalog.Load(skills, Load(new { name = "review", path = "references/guide.md" }));
        Assert.False(resource.IsError);
        Assert.Contains("guide text", resource.Content);
    }

    [Theory]
    [InlineData("../../secret.txt", "outside_skill")]
    [InlineData("..\\other\\SKILL.md", "outside_skill")]
    [InlineData("C:\\Windows\\win.ini", "outside_skill")]
    [InlineData("missing.md", "path_unavailable")]
    public void LoadRejectsResourcesOutsideTheSkillFolder(string path, string code)
    {
        Write(Project, ".lumen", "review", Skill("review", "d"));
        Write(Project, ".lumen", "other", Skill("other", "d"));
        File.WriteAllText(Path.Combine(Project, ".lumen", "secret.txt"), "secret");
        var result = SkillCatalog.Load(Discover().Skills, Load(new { name = "review", path }));
        Assert.True(result.IsError);
        Assert.Contains(code, result.Content);
        Assert.DoesNotContain("secret", result.Content.Replace("\"code\"", ""));
    }

    [Fact]
    public void LoadRejectsUnknownOrUnavailableSkills()
    {
        var skills = new[] { Parse(Skill("review", "d")) };
        Assert.Contains("skill_unavailable", SkillCatalog.Load(skills, Load(new { name = "deploy" })).Content);
        Assert.Contains("invalid_arguments", SkillCatalog.Load(skills, Load(new { name = "review", extra = "x" })).Content);
        Assert.Contains("invalid_arguments", SkillCatalog.Load(skills, Load(new { name = 1 })).Content);
    }

    // Session-level checks drive ByokSession with a fake OpenAI-compatible provider; no network or credits.
    private sealed class Source : ISkillSource
    {
        private readonly Func<SkillSnapshot> discover;
        public Source(Func<SkillSnapshot> discover) => this.discover = discover;
        public Task<SkillSnapshot> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult(discover());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public readonly Queue<string> Responses = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\"}]}") };
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var data = Responses.Count > 0 ? Responses.Dequeue() : "{\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\ndata: [DONE]\n\n") };
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token) => Task.FromResult<string?>("synthetic-skill-key");
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Reader : IReadToolExecutor
    {
        public IReadOnlyList<ToolSpec> Specs => ReadToolCatalog.Specs;
        public Task<IReadToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IReadToolExecutor>(this);
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) => Task.FromResult(new ToolResult(call.Id, false, "read data"));
    }

    private sealed class Writer : IWriteToolExecutor
    {
        public int Calls;
        public IReadOnlyList<ToolSpec> Specs => WriteToolCatalog.Specs;
        public Task<IWriteToolExecutor> CaptureAsync(CancellationToken token) => Task.FromResult<IWriteToolExecutor>(this);
        public Task BeginTurnAsync(bool automatic, CancellationToken token) => Task.CompletedTask;
        public Task<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; return Task.FromResult(new ToolResult(call.Id, false, "{}")); }
        public Task UndoTurnAsync(CancellationToken token) => Task.CompletedTask;
        public int PendingChanges => 0;
    }

    private sealed class Session : IDisposable
    {
        private readonly HttpClient client;
        private readonly string id = Guid.NewGuid().ToString("D");
        public readonly Handler Handler = new();
        public readonly Writer Writer = new();
        public readonly ByokSession Byok;
        public readonly List<JsonElement> Replies = new();

        public Session(string storage, ISkillSource skills)
        {
            client = new HttpClient(Handler);
            Byok = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(),
                new SettingsStore(Path.Combine(storage, "providers.json")), readTools: new Reader(), writeTools: Writer, skills: skills);
        }

        public async Task Send(string type, object payload)
        {
            await Byok.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("D"), type, payload }), reply =>
            {
                using var parsed = JsonDocument.Parse(reply);
                Replies.Add(parsed.RootElement.Clone());
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        public async Task Prepare()
        {
            await Send("byok.configure", new { id, providerId = "fixture", kind = "OpenAI", baseUrl = "https://provider.test/v1/", key = "synthetic-skill-key" });
            await Send("byok.select", new { configurationId = id, modelId = "fixture" });
            await Send("byok.override", new { configurationId = id, modelId = "fixture", vision = false, tools = true, reasoning = false });
        }

        public Task Chat(string text, string mode, bool readTools, bool writeTools = false, string[]? skills = null)
        {
            var payload = new Dictionary<string, object> { ["configurationId"] = id, ["text"] = text, ["effort"] = "Off", ["mode"] = mode, ["readTools"] = readTools };
            if (writeTools) payload["writeTools"] = true;
            if (skills is not null) payload["skills"] = skills;
            return Send("byok.chat", payload);
        }

        public JsonElement Last => Replies.Last();
        public string System(int body) => JsonDocument.Parse(Handler.Bodies[body]).RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        public string User(int body) => JsonDocument.Parse(Handler.Bodies[body]).RootElement.GetProperty("messages").EnumerateArray()
            .Last(message => message.GetProperty("role").GetString() == "user").GetRawText();
        public string[] Tools(int body) => JsonDocument.Parse(Handler.Bodies[body]).RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray() : Array.Empty<string>();
        public void Dispose() { Byok.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task DisabledSkillsPersistAndCannotBeActivatedOrAdvertised()
    {
        Write(Project, ".lumen", "review", Skill("review", "Review changes.", "REVIEW-BODY"));
        using (var session = new Session(root, new Source(Discover)))
        {
            await session.Send("byok.skills.list", new { });
            var listed = session.Last.GetProperty("payload").GetProperty("skills").EnumerateArray().Single();
            Assert.Equal("review", listed.GetProperty("name").GetString());
            Assert.True(listed.GetProperty("enabled").GetBoolean());
            Assert.DoesNotContain("REVIEW-BODY", session.Last.GetRawText());
            await session.Send("byok.skills.set", new { name = "review", enabled = false });
            Assert.False(session.Last.GetProperty("payload").GetProperty("enabled").GetBoolean());
            await session.Send("byok.skills.set", new { name = "not-found", enabled = true });
            Assert.Equal("skill_unavailable", session.Last.GetProperty("payload").GetProperty("code").GetString());
        }
        using (var session = new Session(root, new Source(Discover)))
        {
            await session.Send("byok.skills.list", new { });
            Assert.False(session.Last.GetProperty("payload").GetProperty("skills")[0].GetProperty("enabled").GetBoolean());
            await session.Prepare();
            await session.Chat("hello", "Ask", true);
            Assert.DoesNotContain("review", session.System(0));
            await session.Chat("/review file.cs", "Ask", false);
            Assert.Equal("skill_unavailable", session.Last.GetProperty("payload").GetProperty("code").GetString());
            await session.Chat("review this", "Ask", false, skills: new[] { "review" });
            Assert.Equal("skill_unavailable", session.Last.GetProperty("payload").GetProperty("code").GetString());
            Assert.Single(session.Handler.Bodies);
            await session.Send("byok.skills.set", new { name = "review", enabled = true });
            await session.Chat("/review file.cs", "Ask", false);
            Assert.Contains("REVIEW-BODY", session.User(1));
        }
    }

    [Fact]
    public async Task SkillListReloadsAndReturnsDiagnosticsWithoutProviderCalls()
    {
        using var session = new Session(root, new Source(Discover));
        await session.Send("byok.skills.list", new { });
        Assert.Empty(session.Last.GetProperty("payload").GetProperty("skills").EnumerateArray());
        Write(Project, ".lumen", "review", Skill("review", "New skill."));
        Write(Project, ".lumen", "broken", "Missing frontmatter");
        await session.Send("byok.skills.list", new { });
        var payload = session.Last.GetProperty("payload");
        Assert.Equal("review", payload.GetProperty("skills")[0].GetProperty("name").GetString());
        Assert.Contains(payload.GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("code").GetString() == "missing_frontmatter");
        Assert.Empty(session.Handler.Bodies);
        await session.Send("byok.skills.set", new { name = "review", enabled = "false" });
        Assert.Equal("invalid_message", session.Last.GetProperty("payload").GetProperty("code").GetString());
    }

    private static string ToolCall(string name, string arguments) =>
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call-skill", function = new { name, arguments } } } }, finish_reason = "tool_calls" } } });

    [Fact]
    public async Task CatalogAdvertisesOnlyNamesAndDescriptionsUntilTheSkillIsLoaded()
    {
        Write(Project, ".lumen", "review", Skill("review", "Review C# diffs.", "SECRET-BODY-MARKER"));
        using var session = new Session(root, new Source(Discover));
        await session.Prepare();
        await session.Chat("hello", "Ask", true);
        Assert.True(session.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Contains("- review: Review C# diffs.", session.System(0));
        Assert.DoesNotContain("SECRET-BODY-MARKER", session.Handler.Bodies[0]);
        Assert.Contains("load_skill", session.Tools(0));

        session.Handler.Responses.Enqueue(ToolCall("load_skill", "{\"name\":\"review\"}"));
        await session.Chat("review this", "Ask", true);
        Assert.True(session.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.DoesNotContain("SECRET-BODY-MARKER", session.Handler.Bodies[1]);
        Assert.Contains("SECRET-BODY-MARKER", session.Handler.Bodies[2]);
    }

    [Fact]
    public async Task SlashNameAndManualSelectionInjectTheBodyIntoTheUserMessage()
    {
        Write(Project, ".lumen", "review", Skill("review", "Review C# diffs.", "REVIEW-BODY"));
        Write(Project, ".lumen", "tests", Skill("tests", "Write tests.", "TESTS-BODY"));
        using var session = new Session(root, new Source(Discover));
        await session.Prepare();
        await session.Chat("/review Program.cs", "Ask", false);
        Assert.True(session.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.Contains("[Skill: review]\\nREVIEW-BODY", session.User(0));
        Assert.DoesNotContain("TESTS-BODY", session.Handler.Bodies[0]);
        Assert.Empty(session.Tools(0));
        Assert.DoesNotContain("Skills:", session.System(0));

        await session.Chat("please", "Ask", false, skills: new[] { "tests" });
        Assert.Contains("TESTS-BODY", session.User(1));

        await session.Chat("/usr/bin is a path", "Ask", false);
        Assert.True(session.Last.GetProperty("payload").GetProperty("completed").GetBoolean());
        Assert.DoesNotContain("[Skill:", session.User(2));
    }

    [Fact]
    public async Task EditedAndRemovedSkillsTakeEffectOnTheNextTurnWithoutRestart()
    {
        using var session = new Session(root, new Source(Discover));
        await session.Prepare();
        await session.Chat("hello", "Ask", true);
        Assert.DoesNotContain("Skills:", session.System(0));
        var directory = Write(Project, ".lumen", "review", Skill("review", "First description."));
        await session.Chat("hello", "Ask", true);
        Assert.Contains("First description.", session.System(1));
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), Skill("review", "Second description."));
        await session.Chat("hello", "Ask", true);
        Assert.Contains("Second description.", session.System(2));
        Directory.Delete(directory, true);
        await session.Chat("/review", "Ask", true);
        Assert.DoesNotContain("Skills:", session.System(3));
        Assert.DoesNotContain("[Skill:", session.User(3));
    }

    [Fact]
    public async Task SkillDeclaringPlanAndWriteToolsCannotEnableWritesInPlan()
    {
        Write(Project, ".lumen", "writer", Skill("writer", "Writes files.", "Always call create_file.",
            "allowed-tools: create_file edit_file run_command\nmetadata:\n  lumen-modes: plan agent\n"));
        Write(Project, ".lumen", "deploy", Skill("deploy", "Deploys.", "DEPLOY-BODY", "metadata:\n  lumen-modes: agent\n"));
        using var session = new Session(root, new Source(Discover));
        await session.Prepare();

        await session.Chat("plan it", "Plan", true, writeTools: true);
        Assert.Equal("mode_denied", session.Last.GetProperty("payload").GetProperty("code").GetString());

        session.Handler.Responses.Enqueue(ToolCall("create_file", "{\"path\":\"a.cs\",\"content\":\"x\"}"));
        await session.Chat("/writer plan it", "Plan", true);
        Assert.Equal("byok.chat.result", session.Last.GetProperty("type").GetString());
        var tools = session.Tools(0);
        Assert.Contains("load_skill", tools);
        Assert.DoesNotContain("create_file", tools);
        Assert.DoesNotContain("edit_file", tools);
        Assert.DoesNotContain("run_command", tools);
        Assert.Equal(0, session.Writer.Calls);
        Assert.Contains("unknown_tool", session.Handler.Bodies[1]);
        Assert.DoesNotContain("deploy", session.System(0));

        var bodies = session.Handler.Bodies.Count;
        await session.Chat("/deploy now", "Plan", true);
        Assert.Equal("skill_unavailable", session.Last.GetProperty("payload").GetProperty("code").GetString());
        await session.Chat("now", "Plan", true, skills: new[] { "deploy" });
        Assert.Equal("skill_unavailable", session.Last.GetProperty("payload").GetProperty("code").GetString());
        Assert.Equal(bodies, session.Handler.Bodies.Count);

        session.Handler.Responses.Enqueue(ToolCall("load_skill", "{\"name\":\"deploy\"}"));
        await session.Chat("load it anyway", "Plan", true);
        Assert.Contains("skill_unavailable", session.Handler.Bodies.Last());
        Assert.DoesNotContain("DEPLOY-BODY", session.Handler.Bodies.Last());
    }

    [Fact]
    public async Task InvalidManualSelectionIsRejectedBeforeTheProvider()
    {
        Write(Project, ".lumen", "review", Skill("review", "d"));
        using var session = new Session(root, new Source(Discover));
        await session.Prepare();
        await session.Chat("x", "Ask", false, skills: new[] { "review", "review" });
        Assert.Equal("invalid_message", session.Last.GetProperty("payload").GetProperty("code").GetString());
        await session.Chat("x", "Ask", false, skills: Enumerable.Range(0, 9).Select(index => "s" + index).ToArray());
        Assert.Equal("invalid_message", session.Last.GetProperty("payload").GetProperty("code").GetString());
        await session.Chat("x", "Ask", false, skills: new[] { "missing" });
        Assert.Equal("skill_unavailable", session.Last.GetProperty("payload").GetProperty("code").GetString());
        Assert.Empty(session.Handler.Bodies);
    }

    private static bool CreateJunction(string link, string target)
    {
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
        if (handle == new IntPtr(-1)) { Directory.Delete(link); return false; }
        try
        {
            if (DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) return true;
        }
        finally { CloseHandle(handle); }
        Directory.Delete(link);
        return false;
    }

    [Fact]
    public void BundledExampleSkillsAreValidAndMatchTheDocumentedSet()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lumen.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var bundled = Path.Combine(directory!.FullName, "src", "Lumen.Vsix", "skills");
        Assert.True(Directory.Exists(bundled), "The VSIX must ship its example skills.");
        var folders = Directory.GetDirectories(bundled).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "code-review", "explain-selection", "write-tests" }, folders);
        foreach (var folder in folders)
        {
            var skill = SkillParser.Parse(File.ReadAllText(Path.Combine(bundled, folder, "SKILL.md")), folder,
                Path.Combine(bundled, folder), "bundled/skills", SkillScope.User);
            Assert.Equal(folder, skill.Name);
            Assert.False(string.IsNullOrWhiteSpace(skill.Description));
            Assert.False(string.IsNullOrWhiteSpace(skill.Body));
            Assert.Empty(skill.Warnings);
            Assert.Equal(new[] { ChatMode.Ask, ChatMode.Plan, ChatMode.Agent }, skill.Modes);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
