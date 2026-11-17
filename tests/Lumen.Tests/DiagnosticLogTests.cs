using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumen-diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticLogTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("key sk-abcdefgh12345678 end", "key [redacted] end")]
    [InlineData("Bearer abcdefgh12345678", "Bearer [redacted]")]
    [InlineData("api_key: abcdefgh12345678", "api_key: [redacted]")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz123456", "[redacted]")]
    [InlineData("AIzaabcdefghijklmnopqrstuvwxyz0123456789", "[redacted]")]
    public void KnownCredentialsAreRedacted(string input, string expected)
    {
        Assert.Equal(expected, LumenDiagnosticLog.Redact(input));
    }

    [Fact]
    public void LevelFiltersEntriesAndJsonLinesNeverCarryTheSecret()
    {
        using var log = new LumenDiagnosticLog(root, "Error");
        var secret = "sk-abcdefgh12345678";
        log.Write("Debug", "Test", "debug_code", "noisy");
        log.Write("Error", "Test", "failed", "key=" + secret);
        var path = Path.Combine(root, "logs", "lumen.log");
        var lines = File.ReadAllLines(path);
        Assert.Single(lines);
        using var entry = JsonDocument.Parse(lines[0]);
        Assert.Equal("Error", entry.RootElement.GetProperty("level").GetString());
        Assert.Equal("Test", entry.RootElement.GetProperty("source").GetString());
        Assert.Equal("failed", entry.RootElement.GetProperty("code").GetString());
        Assert.Contains("redacted", entry.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(secret, lines[0]);

        log.SetLevel("Debug");
        log.Write("Debug", "Test", "debug_code");
        Assert.Equal(2, File.ReadAllLines(path).Length);
        Assert.Throws<ProviderException>(() => log.SetLevel("Verbose"));
    }

    [Fact]
    public async Task LogLevelIsPersistedPerUserAndValidated()
    {
        var path = Path.Combine(root, "providers.json");
        var settings = new SettingsStore(path);
        Assert.Equal("Error", await settings.ReadLogLevelAsync(CancellationToken.None));
        await settings.WriteLogLevelAsync("Warning", CancellationToken.None);
        Assert.Equal("Warning", await settings.ReadLogLevelAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(root, "providers.log.json")));
        await Assert.ThrowsAsync<ProviderException>(() => settings.WriteLogLevelAsync("Verbose", CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(root, "providers.log.json"), "{\"level\":\"Debug\",\"extra\":1}");
        await Assert.ThrowsAsync<ProviderException>(() => settings.ReadLogLevelAsync(CancellationToken.None));
    }

    private sealed class FakeLog : IDiagnosticLog
    {
        private readonly string path;
        public FakeLog(string path) => this.path = path;
        public string Level { get; private set; } = "Error";
        public string FilePath => path;
        public int Opens;
        public void SetLevel(string level) => Level = level;
        public void Open() => Opens++;
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token) => Task.FromResult<string?>(null);
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
    }

    [Fact]
    public async Task LogRpcReadsWritesAndOpensWithoutAProvider()
    {
        var fake = new FakeLog(Path.Combine(root, "logs", "lumen.log"));
        using var client = new HttpClient(new Handler());
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(),
            new SettingsStore(Path.Combine(root, "providers.json")), diagnosticLog: fake);
        async Task<JsonElement> Send(string type, object payload)
        {
            JsonElement? reply = null;
            await session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new
            { version = 1, id = Guid.NewGuid().ToString("D"), type, payload }), message =>
            {
                using var parsed = JsonDocument.Parse(message);
                reply = parsed.RootElement.Clone();
                return Task.CompletedTask;
            }, CancellationToken.None);
            return reply!.Value;
        }
        Assert.Equal("Error", (await Send("byok.log.get", new { })).GetProperty("payload").GetProperty("level").GetString());
        Assert.Equal("Debug", (await Send("byok.log.set", new { level = "Debug" })).GetProperty("payload").GetProperty("level").GetString());
        Assert.Equal("Debug", fake.Level);
        Assert.Equal("Debug", await new SettingsStore(Path.Combine(root, "providers.json")).ReadLogLevelAsync(CancellationToken.None));
        Assert.True((await Send("byok.log.open", new { })).GetProperty("payload").GetProperty("opened").GetBoolean());
        Assert.Equal(1, fake.Opens);
        Assert.Equal("invalid_configuration", (await Send("byok.log.set", new { level = "Verbose" })).GetProperty("payload").GetProperty("code").GetString());
    }
}
