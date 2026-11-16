using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Lumen.Tools;
using Xunit;

namespace Lumen.Tests;

public sealed class ThemeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumen-theme-" + Guid.NewGuid().ToString("N"));

    public ThemeTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    private string SettingsPath => Path.Combine(root, "providers.json");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ThemePreset Parse(string json) => ThemeCatalog.Parse(Json(json));

    [Fact]
    public void ValidThemeKeepsEveryFieldAndRejectsUnknownOnes()
    {
        var preset = Parse("{\"name\":\"Night\",\"background\":\"#101014\",\"foreground\":\"#f2f2f5\",\"accent\":\"#42c99a\",\"chatFont\":\"Segoe UI, sans-serif\",\"codeFont\":\"Cascadia Mono\",\"fontSize\":15,\"density\":\"compact\",\"borderRadius\":4,\"modeAccent\":{\"ask\":\"#111111\",\"plan\":\"#222222\",\"agent\":\"#333333\"}}");
        Assert.Equal("Night", preset.Name);
        Assert.Equal("#101014", preset.Background);
        Assert.Equal("Segoe UI, sans-serif", preset.ChatFont);
        Assert.Equal(15, preset.FontSize);
        Assert.Equal("compact", preset.Density);
        Assert.Equal(4, preset.BorderRadius);
        Assert.Equal("#333333", preset.ModeAccent!["agent"]);
        // Negative control: an unknown field is rejected instead of being ignored.
        var error = Assert.Throws<ThemeValidationException>(() => Parse("{\"background\":\"#101014\",\"shadow\":\"none\"}"));
        Assert.Equal("invalid_configuration", error.Code);
    }

    [Fact]
    public void ThemeFontsAcceptUnicodeAndEmptyValuesButRejectControls()
    {
        Assert.Equal("Fonte 中文", Parse("{\"chatFont\":\"Fonte 中文\"}").ChatFont);
        Assert.Equal("", Parse("{\"chatFont\":\"\"}").ChatFont);
        Assert.Throws<ThemeValidationException>(() => Parse("{\"name\":\"x\\u007fy\"}"));
        Assert.Throws<ThemeValidationException>(() => Parse("{\"chatFont\":\"x; background:red\"}"));
    }

    [Theory]
    [InlineData("{\"background\":\"#abcde\"}")]          // five digits is not a color
    [InlineData("{\"background\":\"red\"}")]
    [InlineData("{\"background\":\"#12345\"}")]
    [InlineData("{\"accent\":\"url(https://example.org)\"}")]
    [InlineData("{\"fontSize\":9}")]
    [InlineData("{\"fontSize\":25}")]
    [InlineData("{\"fontSize\":13.5}")]
    [InlineData("{\"density\":\"huge\"}")]
    [InlineData("{\"borderRadius\":-1}")]
    [InlineData("{\"borderRadius\":40}")]
    [InlineData("{\"chatFont\":\"Segoe UI; background:url(https://example.org)\"}")]
    [InlineData("{\"modeAccent\":{\"unknown\":\"#111111\"}}")]
    [InlineData("{\"modeAccent\":{\"ask\":\"not-a-color\"}}")]
    [InlineData("[]")]
    public void MalformedThemesAreRejectedWithAnActionableCode(string json)
    {
        var error = Assert.Throws<ThemeValidationException>(() => Parse(json));
        Assert.Equal("invalid_configuration", error.Code);
        Assert.NotEmpty(error.Detail);
    }

    [Fact]
    public void ContrastWarningsFlagUnreadableCombinationsOnly()
    {
        Assert.Equal(21, Math.Round(ThemeCatalog.Contrast("#000000", "#ffffff"), 3));
        Assert.Empty(ThemeCatalog.Warnings(Parse("{\"background\":\"#ffffff\",\"foreground\":\"#111111\",\"accent\":\"#0055aa\"}")));
        var warnings = ThemeCatalog.Warnings(Parse("{\"background\":\"#ffffff\",\"foreground\":\"#bbbbbb\",\"accent\":\"#dddddd\",\"modeAccent\":{\"ask\":\"#eeeeee\"}}"));
        Assert.Contains("low_contrast_text", warnings);
        Assert.Contains("low_contrast_accent", warnings);
        Assert.Contains("low_contrast_mode_accent", warnings);
        // A theme without a background cannot be measured, so it raises no warning.
        Assert.Empty(ThemeCatalog.Warnings(Parse("{\"foreground\":\"#bbbbbb\"}")));
    }

    [Fact]
    public async Task ThemeIsPersistedPerUserAndResetRemovesIt()
    {
        var settings = new SettingsStore(SettingsPath);
        var preset = Parse("{\"name\":\"Night\",\"background\":\"#101014\",\"foreground\":\"#f2f2f5\",\"accent\":\"#42c99a\",\"modeAccent\":{\"ask\":\"#66d9a6\"}}");
        await settings.WriteThemeAsync(preset, CancellationToken.None);
        // The preset lives with the per-user settings, never inside the solution.
        Assert.True(File.Exists(Path.Combine(root, "providers.theme.json")));
        var read = await settings.ReadThemeAsync(CancellationToken.None);
        Assert.Equal("Night", read!.Name);
        Assert.Equal("#66d9a6", read.ModeAccent!["ask"]);
        await settings.ClearThemeAsync(CancellationToken.None);
        Assert.Null(await settings.ReadThemeAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root, "providers.theme.json")));
    }

    [Fact]
    public async Task CorruptThemeFileIsRejectedInsteadOfBreakingTheUi()
    {
        File.WriteAllText(Path.Combine(root, "providers.theme.json"), "{ not json");
        var settings = new SettingsStore(SettingsPath);
        var error = await Assert.ThrowsAsync<ProviderException>(() => settings.ReadThemeAsync(CancellationToken.None));
        Assert.Equal("invalid_configuration", error.Code);
        // The invalid file can still be discarded, so the user recovers the default theme.
        await settings.ClearThemeAsync(CancellationToken.None);
        Assert.Null(await settings.ReadThemeAsync(CancellationToken.None));
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token) => Task.FromResult<string?>("synthetic-theme-key");
        public Task WriteAsync(string id, string key, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\"}]}") });
    }

    [Fact]
    public async Task ThemeRequestsWorkWithoutAProviderAndRejectInvalidThemes()
    {
        using var client = new HttpClient(new Handler());
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(SettingsPath));
        var replies = new List<JsonElement>();
        async Task Send(string type, object payload)
        {
            await session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new { version = 1, id = Guid.NewGuid().ToString("D"), type, payload }), reply =>
            {
                using var parsed = JsonDocument.Parse(reply);
                replies.Add(parsed.RootElement.Clone());
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        await Send("byok.theme.get", new { });
        Assert.Equal("byok.theme.get.result", replies[0].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, replies[0].GetProperty("payload").GetProperty("theme").ValueKind);

        await Send("byok.theme.set", new { theme = new { name = "Night", background = "#101014", foreground = "#f2f2f5", accent = "#42c99a", fontSize = 15, density = "compact", borderRadius = 4, modeAccent = new { ask = "#66d9a6" } } });
        var applied = replies[1].GetProperty("payload");
        Assert.Equal("Night", applied.GetProperty("theme").GetProperty("name").GetString());
        Assert.Empty(applied.GetProperty("warnings").EnumerateArray());

        await Send("byok.theme.get", new { });
        Assert.Equal("Night", replies[2].GetProperty("payload").GetProperty("theme").GetProperty("name").GetString());

        // Negative control: a malformed theme is refused and the stored preset stays untouched.
        await Send("byok.theme.set", new { theme = new { background = "not-a-color" } });
        Assert.Equal("error", replies[3].GetProperty("type").GetString());
        Assert.Equal("invalid_configuration", replies[3].GetProperty("payload").GetProperty("code").GetString());
        await Send("byok.theme.get", new { });
        Assert.Equal("Night", replies[4].GetProperty("payload").GetProperty("theme").GetProperty("name").GetString());

        await Send("byok.theme.reset", new { });
        Assert.True(replies[5].GetProperty("payload").GetProperty("reset").GetBoolean());
        await Send("byok.theme.get", new { });
        Assert.Equal(JsonValueKind.Null, replies[6].GetProperty("payload").GetProperty("theme").ValueKind);
    }

    [Fact]
    public async Task LowContrastThemeIsAcceptedWithAWarning()
    {
        using var client = new HttpClient(new Handler());
        using var session = new ByokSession(new ProviderFactory(new HttpTransport(client), new CapabilityResolver()), new Credentials(), new SettingsStore(SettingsPath));
        JsonElement? reply = null;
        await session.HandleAsync("https://lumen.local/index.html", JsonSerializer.Serialize(new
        {
            version = 1, id = Guid.NewGuid().ToString("D"), type = "byok.theme.set",
            payload = new { theme = new { background = "#ffffff", foreground = "#bbbbbb", accent = "#dddddd" } }
        }), message =>
        {
            using var parsed = JsonDocument.Parse(message);
            reply = parsed.RootElement.Clone();
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal("byok.theme.set.result", reply!.Value.GetProperty("type").GetString());
        var warnings = reply.Value.GetProperty("payload").GetProperty("warnings").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("low_contrast_text", warnings);
        Assert.Contains("low_contrast_accent", warnings);
    }
}
