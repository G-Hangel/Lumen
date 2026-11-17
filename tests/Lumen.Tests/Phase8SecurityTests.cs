using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

// Phase 8a: the extension must stay silent on the network until the user
// explicitly configures a provider. No key is needed for any request below.
public sealed class Phase8SecurityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumen-phase8-" + Guid.NewGuid().ToString("N"));

    public Phase8SecurityTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        public readonly List<Uri> Uris = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Uris.Add(request.RequestUri!);
            throw new InvalidOperationException("No HTTP request may leave the process before configuration.");
        }
    }

    private static string Request(string type, object payload) =>
        JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("D"), type, payload });

    [Fact]
    public async Task OnboardingRequestsNeverReachTheNetworkWithoutAProvider()
    {
        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()),
            new Phase8Credentials(), new SettingsStore(Path.Combine(root, "providers.json")));
        var replies = new List<string>();
        Task Send(string json) { replies.Add(json); return Task.CompletedTask; }

        // Everything the UI does on startup and in the settings panels before BYOK is configured.
        foreach (var type in new[] { "byok.list", "byok.theme.get", "byok.skills.list", "byok.history.list", "byok.tool.write.status" })
            await session.HandleAsync("https://lumen.local/index.html", Request(type, new { }), Send, CancellationToken.None);

        // Requests that would need a provider must fail locally, not attempt a call.
        foreach (var request in new[]
        {
            Request("byok.chat", new { configurationId = Guid.NewGuid().ToString("D"), text = "hello", effort = "Off" }),
            Request("byok.discover", new { configurationId = Guid.NewGuid().ToString("D") }),
            Request("byok.probe", new { configurationId = Guid.NewGuid().ToString("D"), capability = "Vision", consent = true }),
            Request("byok.configure", new { id = Guid.NewGuid().ToString("D"), providerId = "x", kind = "Unknown", baseUrl = "https://provider.test/", key = "x" })
        })
            await session.HandleAsync("https://lumen.local/index.html", request, Send, CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        Assert.Empty(handler.Uris);
        Assert.NotEmpty(replies);
        Assert.Contains(replies, json => json.Contains("\"error\"", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, "providers.json")), "No settings file may be created by read-only onboarding RPCs.");
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://provider.test/v1/")]
    [InlineData("https://user:pass@provider.test/v1/")]
    [InlineData("https://provider.test/v1/?key=x")]
    [InlineData("https://provider.test/v1/#x")]
    [InlineData("https://provider.test/v1/ with space")]
    public async Task ConfigureRejectsUnsafeBaseUrlsBeforeAnyNetworkCall(string baseUrl)
    {
        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()),
            new Phase8Credentials(), new SettingsStore(Path.Combine(root, "providers.json")));
        string? reply = null;
        await session.HandleAsync("https://lumen.local/index.html", Request("byok.configure", new
        {
            id = Guid.NewGuid().ToString("D"), providerId = "fixture", kind = "OpenAI",
            baseUrl, key = "synthetic-key-that-must-not-leak"
        }), json => { reply = json; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, handler.Calls);
        Assert.NotNull(reply);
        using var parsed = JsonDocument.Parse(reply!);
        Assert.Equal("invalid_url", parsed.RootElement.GetProperty("payload").GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-key-that-must-not-leak", reply!);
    }

    [Fact]
    public async Task TamperedSettingsWithUnsafeBaseUrlAreRejectedOnRead()
    {
        var path = Path.Combine(root, "providers.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[]
        {
            new { id = Guid.NewGuid().ToString("D"), providerId = "fixture", kind = "OpenAI", baseUrl = "file:///C:/Windows/win.ini", selectedModelId = (string?)null, deploymentNames = Array.Empty<string>() }
        }));
        // ProviderConfiguration itself refuses an unsafe base URL, so a tampered settings file
        // cannot become a usable provider configuration.
        await Assert.ThrowsAsync<ArgumentException>(() => new SettingsStore(path).ReadAsync(CancellationToken.None));
    }

    private sealed class Phase8Credentials : ICredentialStore
    {
        public int Reads;
        public Task<string?> ReadAsync(string id, CancellationToken token) { Reads++; return Task.FromResult<string?>(null); }
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }
}
