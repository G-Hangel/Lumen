using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class CapabilityResolverTests
{
    [Fact]
    public void NativeFalseDefeatsBothRegistryAndVisionHeuristic()
    {
        var native = new NativeModel("test-vision");
        native.Capabilities[ModelCapabilities.Vision] = false;
        var model = new CapabilityResolver(Registry(("vision", true))).Resolve("test", native);

        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Vision));
        Assert.Equal(Confidence.High, Assert.Single(model.Origins).Confidence);
        var withoutRegistry = new CapabilityResolver(EmptyRegistry).Resolve("test", native);
        Assert.False(withoutRegistry.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, withoutRegistry.SourceOf(ModelCapabilities.Vision));
    }

    [Fact]
    public void EmbeddedRegistryRejectsVisionForDeepSeekChatAndRetainsKnownFalseOrigin()
    {
        var model = new CapabilityResolver().Resolve("deepseek", new NativeModel("deepseek-chat"));
        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Registry, model.SourceOf(ModelCapabilities.Vision));
        Assert.True(model.Has(ModelCapabilities.Tools));
        Assert.False(model.Has(ModelCapabilities.Reasoning));
        Assert.Equal(CapabilitySource.Registry, model.SourceOf(ModelCapabilities.Reasoning));
        Assert.Null(model.Reasoning);
        Assert.Null(model.SourceOf(ModelCapabilities.Pdf));
    }

    [Fact]
    public void RegistryFalseAlsoDefeatsPositiveNameHint()
    {
        var model = new CapabilityResolver(Registry(("vision", false)))
            .Resolve("test", new NativeModel("test-vision"));
        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Registry, model.SourceOf(ModelCapabilities.Vision));
    }

    [Fact]
    public void NativeTrueDefeatsRegistryFalse()
    {
        var native = new NativeModel("deepseek-chat", "Native display");
        native.Capabilities[ModelCapabilities.Vision] = true;
        var model = new CapabilityResolver().Resolve("deepseek", native);
        Assert.True(model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Vision));
        Assert.Equal("Native display", model.DisplayName);
        Assert.Equal("deepseek", model.ProviderId);
        Assert.Equal("deepseek-chat", model.ModelId);
    }

    [Fact]
    public void UnknownIsDistinguishedFromFalseByMissingOrigin()
    {
        var model = new CapabilityResolver().Resolve("unknown", new NativeModel("unfamiliar-model"));
        Assert.Equal(ModelCapabilities.None, model.Capabilities);
        Assert.Empty(model.Origins);
        foreach (var capability in Enum.GetValues<ModelCapabilities>().Where(value => value != ModelCapabilities.None))
            Assert.Null(model.SourceOf(capability));
    }

    [Theory]
    [InlineData("custom-vision")]
    [InlineData("custom-vl-7b")]
    [InlineData("vendor/custom-vision:latest")]
    public void ExplicitVisionNameHintsAreLowConfidenceAndDoNotGuessOtherFeatures(string id)
    {
        var model = new CapabilityResolver(EmptyRegistry).Resolve("test", new NativeModel(id));
        Assert.True(model.Has(ModelCapabilities.Vision));
        var origin = Assert.Single(model.Origins);
        Assert.Equal(CapabilitySource.Heuristic, origin.Source);
        Assert.Equal(Confidence.Low, origin.Confidence);
        Assert.Null(model.SourceOf(ModelCapabilities.Tools));
        Assert.Null(model.SourceOf(ModelCapabilities.Reasoning));
    }

    [Theory]
    [InlineData("revision-model")]
    [InlineData("custom-reasoner")]
    [InlineData("custom-thinking")]
    [InlineData("r1-unfamiliar")]
    [InlineData("claude-unfamiliar")]
    public void NamesDoNotInventCapabilitiesOrReasoningWireControls(string id)
    {
        var model = new CapabilityResolver(EmptyRegistry).Resolve("test", new NativeModel(id));
        Assert.Empty(model.Origins);
        Assert.Null(model.Reasoning);
    }

    [Theory]
    [InlineData("other", "deepseek-chat")]
    [InlineData("DeepSeek", "deepseek-chat")]
    [InlineData("deepseek", "deepseek-chat-unverified")]
    [InlineData("deepseek", "prefix-deepseek-chat")]
    public void RegistryIsScopedToProviderAndExactDocumentedPatterns(string provider, string id)
    {
        var model = new CapabilityResolver().Resolve(provider, new NativeModel(id));
        Assert.Empty(model.Origins);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManualOverridesAreFinalAndAlwaysHaveAnOrigin(bool supported)
    {
        var native = new NativeModel("test-vision");
        native.Capabilities[ModelCapabilities.Vision] = !supported;
        var model = new CapabilityResolver(Registry(("vision", !supported))).Resolve("test", native,
            new Dictionary<ModelCapabilities, bool> { [ModelCapabilities.Vision] = supported });
        Assert.Equal(supported, model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.UserOverride, model.SourceOf(ModelCapabilities.Vision));
        Assert.Equal(Confidence.High, Assert.Single(model.Origins).Confidence);
    }

    [Fact]
    public void ManualOverrideCanEnableUnknownVisionOrDisableHeuristic()
    {
        var resolver = new CapabilityResolver(EmptyRegistry);
        var enabled = resolver.Resolve("test", new NativeModel("unknown"),
            new Dictionary<ModelCapabilities, bool> { [ModelCapabilities.Vision] = true });
        Assert.True(enabled.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.UserOverride, enabled.SourceOf(ModelCapabilities.Vision));
        var disabled = resolver.Resolve("test", new NativeModel("custom-vision"),
            new Dictionary<ModelCapabilities, bool> { [ModelCapabilities.Vision] = false });
        Assert.False(disabled.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.UserOverride, disabled.SourceOf(ModelCapabilities.Vision));
    }

    [Fact]
    public void ResolverCarriesNativeLimitsAndDoesNotOverwriteThemWithRegistry()
    {
        var native = new NativeModel("test") { ContextWindowTokens = 32000, MaxOutputTokens = 2048 };
        var resolver = new CapabilityResolver(Registry(("vision", false), ("contextWindow", 100000), ("maxOutput", 8000)));
        var model = resolver.Resolve("test", native);
        Assert.Equal(32000, model.ContextWindowTokens);
        Assert.Equal(2048, model.MaxOutputTokens);
        var registryModel = resolver.Resolve("test", new NativeModel("test"));
        Assert.Equal(100000, registryModel.ContextWindowTokens);
        Assert.Equal(8000, registryModel.MaxOutputTokens);
        var unknown = new CapabilityResolver(EmptyRegistry).Resolve("test", new NativeModel("unknown"));
        Assert.Null(unknown.ContextWindowTokens);
        Assert.Null(unknown.MaxOutputTokens);
    }

    [Fact]
    public void UpdatedRegistryIsInjectedWithoutNetworkOrEmbeddedRegistryFallback()
    {
        var model = new CapabilityResolver(Registry(("vision", true), ("confidence", "Low")))
            .Resolve("test", new NativeModel("anything"));
        Assert.True(model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Registry, model.SourceOf(ModelCapabilities.Vision));
        Assert.Equal(Confidence.Low, Assert.Single(model.Origins).Confidence);
        var legacy = new CapabilityResolver(EmptyRegistry).Resolve("deepseek", new NativeModel("deepseek-chat"));
        Assert.Empty(legacy.Origins);
    }

    [Fact]
    public void AllNativeTrueAndFalseFlagsHaveIndividualOrigins()
    {
        var native = new NativeModel("unknown") { Reasoning = Support() };
        foreach (var capability in Enum.GetValues<ModelCapabilities>().Where(value => value != ModelCapabilities.None))
            native.Capabilities[capability] = capability == ModelCapabilities.Text || capability == ModelCapabilities.Reasoning;
        var model = new CapabilityResolver(EmptyRegistry).Resolve("test", native);
        Assert.Equal(8, model.Origins.Count);
        Assert.Equal(8, model.Origins.Select(origin => origin.Capability).Distinct().Count());
        Assert.All(model.Origins, origin => Assert.Equal(CapabilitySource.Native, origin.Source));
        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Vision));
        Assert.True(model.Has(ModelCapabilities.Reasoning));
    }

    [Fact]
    public void DeepSeekReasoningUsesModelSelectionNotInventedEffortKnobs()
    {
        var model = new CapabilityResolver().Resolve("deepseek", new NativeModel("deepseek-reasoner"));
        Assert.True(model.Has(ModelCapabilities.Reasoning));
        Assert.Equal(CapabilitySource.Registry, model.SourceOf(ModelCapabilities.Reasoning));
        Assert.NotNull(model.Reasoning);
        Assert.Equal(ReasoningKind.ModelSelection, model.Reasoning.Kind);
        Assert.True(model.Reasoning.Mandatory);
        Assert.DoesNotContain(ReasoningIntent.Off, model.Reasoning.Efforts);
        Assert.False(model.Has(ModelCapabilities.Vision));
        Assert.Null(model.SourceOf(ModelCapabilities.Tools));
    }

    [Fact]
    public void ExplicitNativeReasoningFalseSuppressesRegistryAndSupport()
    {
        var native = new NativeModel("deepseek-reasoner") { Reasoning = Support() };
        native.Capabilities[ModelCapabilities.Reasoning] = false;
        var model = new CapabilityResolver().Resolve("deepseek", native);
        Assert.False(model.Has(ModelCapabilities.Reasoning));
        Assert.Null(model.Reasoning);
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Reasoning));
    }

    [Fact]
    public void NativeReasoningMetadataWinsAndCanSupplyTheFlag()
    {
        var support = Support();
        var model = new CapabilityResolver().Resolve("deepseek", new NativeModel("deepseek-reasoner") { Reasoning = support });
        Assert.Same(support, model.Reasoning);
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Reasoning));
        Assert.Equal(ReasoningKind.BudgetTokens, model.Reasoning!.Kind);
    }

    [Fact]
    public void ManualReasoningDisableDropsSupportAndEnableUsesOnlyKnownMechanism()
    {
        var resolver = new CapabilityResolver();
        var disabled = resolver.Resolve("deepseek", new NativeModel("deepseek-reasoner"),
            new Dictionary<ModelCapabilities, bool> { [ModelCapabilities.Reasoning] = false });
        Assert.False(disabled.Has(ModelCapabilities.Reasoning));
        Assert.Null(disabled.Reasoning);
        Assert.Equal(CapabilitySource.UserOverride, disabled.SourceOf(ModelCapabilities.Reasoning));
        var native = new NativeModel("deepseek-reasoner");
        native.Capabilities[ModelCapabilities.Reasoning] = false;
        var enabled = resolver.Resolve("deepseek", native,
            new Dictionary<ModelCapabilities, bool> { [ModelCapabilities.Reasoning] = true });
        Assert.Equal(ReasoningKind.ModelSelection, enabled.Reasoning!.Kind);
        Assert.Equal(CapabilitySource.UserOverride, enabled.SourceOf(ModelCapabilities.Reasoning));
    }

    [Fact]
    public void NativeOrManualReasoningWithoutKnownMechanismIsRejectedInsteadOfGuessed()
    {
        var resolver = new CapabilityResolver(EmptyRegistry);
        var native = new NativeModel("unknown-reasoner");
        native.Capabilities[ModelCapabilities.Reasoning] = true;
        Assert.Throws<ArgumentException>(() => resolver.Resolve("test", native));
        Assert.Throws<ArgumentException>(() => resolver.Resolve("test", new NativeModel("unknown"),
            new Dictionary<ModelCapabilities, bool> { [ModelCapabilities.Reasoning] = true }));
    }

    [Theory]
    [InlineData(ModelCapabilities.None)]
    [InlineData(ModelCapabilities.Tools | ModelCapabilities.Vision)]
    [InlineData((ModelCapabilities)256)]
    public void ResolverRejectsInvalidCapabilityKeys(ModelCapabilities capability)
    {
        var resolver = new CapabilityResolver(EmptyRegistry);
        var native = new NativeModel("unknown");
        native.Capabilities[capability] = false;
        Assert.Throws<ArgumentException>(() => resolver.Resolve("test", native));
        Assert.Throws<ArgumentException>(() => resolver.Resolve("test", new NativeModel("unknown"),
            new Dictionary<ModelCapabilities, bool> { [capability] = true }));
    }

    [Fact]
    public void ResolverRejectsInvalidNativeInput()
    {
        var resolver = new CapabilityResolver(EmptyRegistry);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve("test", null!));
        Assert.Throws<ArgumentException>(() => resolver.Resolve(" ", new NativeModel("test")));
        Assert.Throws<ArgumentException>(() => resolver.Resolve("test", new NativeModel(" ")));
        Assert.Throws<ArgumentException>(() => resolver.Resolve("test", new NativeModel(new string('a', 1025))));
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve("test", new NativeModel("test") { ContextWindowTokens = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve("test", new NativeModel("test") { MaxOutputTokens = -1 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"models\":[]}")]
    [InlineData("{\"version\":1,\"models\":{}}")]
    [InlineData("{\"version\":1,\"version\":1,\"models\":[]}")]
    [InlineData("{\"version\":1,\"models\":[],\"unknown\":true}")]
    [InlineData("{\"version\":1,\"models\":[{}]}")]
    public void InvalidRegistryIsRejectedWithControlledError(string json)
    {
        var exception = Assert.Throws<ArgumentException>(() => new CapabilityResolver(json));
        Assert.Contains("Invalid capability registry.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("source", "not a url")]
    [InlineData("source", "file:///C:/secret")]
    [InlineData("source", "https://secret@example.com/docs")]
    [InlineData("source", " ")]
    [InlineData("confidence", "Certain")]
    [InlineData("confidence", "2")]
    [InlineData("verifiedAt", "not a date")]
    [InlineData("verifiedAt", "9999-01-01")]
    [InlineData("providerId", " ")]
    [InlineData("modelIdPattern", "[")]
    [InlineData("vision", "false")]
    [InlineData("contextWindow", 0)]
    [InlineData("maxOutput", -1)]
    [InlineData("unexpectedCapability", true)]
    [InlineData("reasoning", true)]
    public void InvalidRegistryFieldsAreNotTrusted(string field, object value)
    {
        var exception = Assert.Throws<ArgumentException>(() => new CapabilityResolver(Registry(("vision", false), (field, value))));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Theory]
    [InlineData("Unknown", "Medium", true)]
    [InlineData("Effort", "Unknown", true)]
    [InlineData("Effort", "Off", true)]
    [InlineData("Effort", "Off", false)]
    public void InvalidRegistryReasoningIsRejected(string kind, string effort, bool mandatory)
    {
        var reasoning = new { kind, efforts = new[] { effort }, mandatory };
        Assert.Throws<ArgumentException>(() => new CapabilityResolver(Registry(("reasoning", reasoning))));
    }

    [Fact]
    public void RegistryReasoningRequiresNonemptyUniqueEfforts()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityResolver(Registry(("reasoning",
            new { kind = "Effort", efforts = Array.Empty<string>(), mandatory = false }))));
        Assert.Throws<ArgumentException>(() => new CapabilityResolver(Registry(("reasoning",
            new { kind = "Effort", efforts = new[] { "Low", "Low" }, mandatory = false }))));
    }

    [Fact]
    public void RegistryAcceptsTimestampAndCaseInsensitiveNamedEnums()
    {
        var reasoning = new { kind = "effort", efforts = new[] { "low", "high" }, mandatory = false };
        var model = new CapabilityResolver(Registry(("reasoning", reasoning), ("confidence", "high"),
            ("verifiedAt", "2026-01-01T00:00:00Z"))).Resolve("test", new NativeModel("test"));
        Assert.Equal(ReasoningKind.Effort, model.Reasoning!.Kind);
        Assert.Equal(new[] { ReasoningIntent.Low, ReasoningIntent.High }, model.Reasoning.Efforts);
        Assert.Equal(Confidence.High, Assert.Single(model.Origins).Confidence);
    }

    [Fact]
    public void RegistryRejectsOversizedAndDuplicateMetadata()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityResolver(new string(' ', 1_048_577)));
        Assert.Throws<ArgumentException>(() => new CapabilityResolver(Registry(("vision", true), ("modelIdPattern", new string('a', 513)))));
        var duplicate = Registry(("vision", true)).Replace("\"vision\":true", "\"vision\":true,\"vision\":false", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new CapabilityResolver(duplicate));
    }

    [Fact]
    public void CatastrophicRegistryRegexIsBoundedAndCannotCreateFalseEvidence()
    {
        var model = new CapabilityResolver(Registry(("vision", false), ("modelIdPattern", "^(a+)+$")))
            .Resolve("test", new NativeModel(new string('a', 1023) + "!"));
        Assert.Null(model.SourceOf(ModelCapabilities.Vision));
        Assert.False(model.Has(ModelCapabilities.Vision));
    }

    private const string EmptyRegistry = "{\"version\":1,\"models\":[]}";
    private static ReasoningSupport Support() => new(ReasoningKind.BudgetTokens, new[] { ReasoningIntent.Medium });

    private static string Registry(params (string Field, object Value)[] fields)
    {
        var model = new Dictionary<string, object>
        {
            ["providerId"] = "test",
            ["modelIdPattern"] = "^.*$",
            ["source"] = "https://example.com/documented-model",
            ["confidence"] = "High",
            ["verifiedAt"] = "2026-01-01"
        };
        foreach (var field in fields) model[field.Field] = field.Value;
        return JsonSerializer.Serialize(new { version = 1, models = new[] { model } });
    }
}

public sealed class CapabilityProbeTests
{
    [Fact]
    public async Task OptOutAndReasoningNeverInvokeCallbackOrConsumeBudget()
    {
        var calls = 0;
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(true); });
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, optedIn: false));
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Reasoning, optedIn: true));
        Assert.Equal(0, calls);
        Assert.Equal(5, probe.RemainingBudget);
        Assert.Null(probe.Store.Get("test", "selected", ModelCapabilities.Reasoning));
    }

    [Fact]
    public async Task CallbackReceivesOnlySelectedModelCapabilityAndCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new CapabilityProbe((provider, model, capability, token) =>
        {
            Assert.Equal("configured-provider", provider);
            Assert.Equal("selected-model", model);
            Assert.Equal(ModelCapabilities.Tools, capability);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult<bool?>(false);
        });
        Assert.False(await probe.ProbeAsync("configured-provider", "selected-model", ModelCapabilities.Tools, true, cancellation.Token));
        Assert.Equal(4, probe.RemainingBudget);
        Assert.False(probe.Store.Get("configured-provider", "selected-model", ModelCapabilities.Tools));
        Assert.Null(probe.Store.Get("configured-provider", "different-model", ModelCapabilities.Tools));
    }

    [Fact]
    public async Task UnknownAuthOrQuotaFailureIsNotCachedAsUnsupportedAndCannotRetryInSameSession()
    {
        var calls = 0;
        var store = new CapabilityProbeStore();
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(null); }, store);
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        Assert.Equal(1, calls);
        Assert.Equal(4, probe.RemainingBudget);
        Assert.Null(store.Get("test", "selected", ModelCapabilities.Vision));
        using var exported = JsonDocument.Parse(store.ExportJson());
        Assert.Equal(0, exported.RootElement.GetProperty("entries").GetArrayLength());
        var nextSession = new CapabilityProbe((_, _, _, _) => Task.FromResult<bool?>(true), store);
        Assert.True(await nextSession.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
    }

    [Fact]
    public async Task GlobalSessionBudgetStopsSixthSelectedModelEvenWhenAllResultsAreUnknown()
    {
        var calls = 0;
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(null); });
        for (var index = 0; index < 6; index++)
            Assert.Null(await probe.ProbeAsync("test", "selection-" + index, ModelCapabilities.Tools, true));
        Assert.Equal(5, calls);
        Assert.Equal(0, probe.RemainingBudget);
    }

    [Fact]
    public async Task CapabilityAndProviderArePartOfCacheAndSessionIdentity()
    {
        var calls = 0;
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(false); });
        Assert.False(await probe.ProbeAsync("a", "selected", ModelCapabilities.Tools, true));
        Assert.False(await probe.ProbeAsync("a", "selected", ModelCapabilities.Vision, true));
        Assert.False(await probe.ProbeAsync("b", "selected", ModelCapabilities.Tools, true));
        Assert.False(await probe.ProbeAsync("a", "selected", ModelCapabilities.Tools, true));
        Assert.Equal(3, calls);
        Assert.Equal(2, probe.RemainingBudget);
    }

    [Fact]
    public async Task ConcurrentDuplicateAttemptCannotSpendTwice()
    {
        var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new CapabilityProbe((_, _, _, _) => { Interlocked.Increment(ref calls); return completion.Task; });
        var first = probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true);
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true));
        Assert.Equal(1, calls);
        Assert.Equal(4, probe.RemainingBudget);
        completion.SetResult(false);
        Assert.False(await first);
        Assert.False(probe.Store.Get("test", "selected", ModelCapabilities.Tools));
    }

    [Fact]
    public async Task PrecancelledProbeDoesNotInvokeCallbackOrConsumeBudget()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(false); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, true, cancellation.Token));
        Assert.Equal(0, calls);
        Assert.Equal(5, probe.RemainingBudget);
        Assert.Null(probe.Store.Get("test", "selected", ModelCapabilities.Vision));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterCallbackStartsDoesNotCacheEvenWhenCallbackIgnoresToken(bool result)
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new CapabilityProbe((_, _, _, _) => completion.Task);
        var attempt = probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true, cancellation.Token);
        cancellation.Cancel();
        completion.SetResult(result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(4, probe.RemainingBudget);
        Assert.Null(probe.Store.Get("test", "selected", ModelCapabilities.Tools));
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true));
    }

    [Fact]
    public async Task CallbackAuthExceptionDoesNotPoisonCacheOrRefundPaidAttempt()
    {
        var probe = new CapabilityProbe((_, _, _, _) => Task.FromException<bool?>(new UnauthorizedAccessException("auth")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true));
        Assert.Null(probe.Store.Get("test", "selected", ModelCapabilities.Tools));
        Assert.Equal(4, probe.RemainingBudget);
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true));
    }

    [Fact]
    public async Task NegativeSurvivesTtlAndExportImportAcrossSessionsWithoutAnotherCallback()
    {
        var now = Epoch;
        var store = new CapabilityProbeStore(TimeSpan.FromMinutes(1), () => now);
        var probe = new CapabilityProbe((_, _, _, _) => Task.FromResult<bool?>(false), store);
        Assert.False(await probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        now = now.AddYears(10);
        Assert.False(store.Get("test", "selected", ModelCapabilities.Vision));
        var imported = new CapabilityProbeStore(TimeSpan.FromMinutes(1), () => now);
        imported.ImportJson(store.ExportJson());
        var calls = 0;
        var nextSession = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(true); }, imported);
        Assert.False(await nextSession.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        Assert.Equal(0, calls);
        Assert.Equal(5, nextSession.RemainingBudget);
        Assert.Null(await nextSession.ProbeAsync("test", "selected", ModelCapabilities.Vision, false));
    }

    [Fact]
    public async Task PositiveHasTtlAndReprobeWaitsForNewSession()
    {
        var now = Epoch;
        var calls = 0;
        var store = new CapabilityProbeStore(TimeSpan.FromMinutes(1), () => now);
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(true); }, store);
        Assert.True(await probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        now = now.AddSeconds(59);
        Assert.True(store.Get("test", "selected", ModelCapabilities.Vision));
        now = now.AddSeconds(1);
        Assert.Null(store.Get("test", "selected", ModelCapabilities.Vision));
        Assert.Null(await probe.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        Assert.Equal(1, calls);
        var nextSession = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(false); }, store);
        Assert.False(await nextSession.ProbeAsync("test", "selected", ModelCapabilities.Vision, true));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PositiveExportImportPreservesAbsoluteExpiryAndDoesNotExtendIt()
    {
        var now = Epoch;
        var store = new CapabilityProbeStore(TimeSpan.FromMinutes(5), () => now);
        var probe = new CapabilityProbe((_, _, _, _) => Task.FromResult<bool?>(true), store);
        Assert.True(await probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true));
        var json = store.ExportJson();
        now = now.AddMinutes(4);
        var imported = new CapabilityProbeStore(TimeSpan.FromMinutes(5), () => now);
        imported.ImportJson(json);
        Assert.True(imported.Get("test", "selected", ModelCapabilities.Tools));
        now = now.AddMinutes(1);
        Assert.Null(imported.Get("test", "selected", ModelCapabilities.Tools));
        using var expiredExport = JsonDocument.Parse(store.ExportJson());
        Assert.Equal(0, expiredExport.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task ImportedPositiveCannotOverridePermanentNegative()
    {
        var store = new CapabilityProbeStore(utcNow: () => Epoch);
        var probe = new CapabilityProbe((_, _, _, _) => Task.FromResult<bool?>(false), store);
        Assert.False(await probe.ProbeAsync("test", "selected", ModelCapabilities.Tools, true));
        store.ImportJson(Cache(true, Epoch.AddHours(1)));
        Assert.False(store.Get("test", "selected", ModelCapabilities.Tools));
    }

    [Fact]
    public void ImportCapsPositiveTtlAndNeverMakesPositivePermanent()
    {
        var now = Epoch;
        var store = new CapabilityProbeStore(TimeSpan.FromMinutes(1), () => now);
        store.ImportJson(Cache(true, now.AddYears(10)));
        Assert.True(store.Get("test", "selected", ModelCapabilities.Tools));
        now = now.AddMinutes(1);
        Assert.Null(store.Get("test", "selected", ModelCapabilities.Tools));
        Assert.Throws<ArgumentException>(() => store.ImportJson(Cache(true, null)));
        Assert.Throws<ArgumentException>(() => store.ImportJson(Cache(false, now)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"entries\":[]}")]
    [InlineData("{\"version\":1,\"entries\":{}}")]
    [InlineData("{\"version\":1,\"entries\":[{}]}")]
    [InlineData("{\"version\":1,\"version\":1,\"entries\":[]}")]
    public void MalformedProbeCacheIsRejectedAndImportIsAtomic(string json)
    {
        var store = new CapabilityProbeStore(utcNow: () => Epoch);
        store.ImportJson(Cache(false, null));
        var exception = Assert.Throws<ArgumentException>(() => store.ImportJson(json));
        Assert.Null(exception.InnerException);
        Assert.False(store.Get("test", "selected", ModelCapabilities.Tools));
    }

    [Fact]
    public void CacheRejectsReasoningUnknownCapabilitiesAndDuplicateEntriesBeforeChangingState()
    {
        var store = new CapabilityProbeStore(utcNow: () => Epoch);
        var valid = Cache(false, null);
        Assert.Throws<ArgumentException>(() => store.ImportJson(valid.Replace("Tools", "Reasoning", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => store.ImportJson(valid.Replace("Tools", "256", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => store.ImportJson(valid.Replace("Tools", "Tools, Vision", StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(valid);
        var entry = document.RootElement.GetProperty("entries")[0].GetRawText();
        Assert.Throws<ArgumentException>(() => store.ImportJson("{\"version\":1,\"entries\":[" + entry + "," + entry + "]}"));
        Assert.Null(store.Get("test", "selected", ModelCapabilities.Tools));
    }

    [Theory]
    [InlineData(ModelCapabilities.None)]
    [InlineData(ModelCapabilities.Vision | ModelCapabilities.Tools)]
    [InlineData((ModelCapabilities)256)]
    public async Task ProbeRejectsInvalidCapabilityBeforeCallback(ModelCapabilities capability)
    {
        var calls = 0;
        var probe = new CapabilityProbe((_, _, _, _) => { calls++; return Task.FromResult<bool?>(true); });
        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync("test", "selected", capability, true));
        Assert.Equal(0, calls);
        Assert.Equal(5, probe.RemainingBudget);
    }

    [Fact]
    public async Task ProbeRejectsInvalidIdentityAndStoreRejectsInvalidTtl()
    {
        Assert.Throws<ArgumentNullException>(() => new CapabilityProbe(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityProbeStore(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityProbeStore(TimeSpan.FromDays(366)));
        var probe = new CapabilityProbe((_, _, _, _) => Task.FromResult<bool?>(true));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync(" ", "selected", ModelCapabilities.Tools, true));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync("test", " ", ModelCapabilities.Tools, true));
        Assert.Equal(5, probe.RemainingBudget);
    }

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static string Cache(bool supported, DateTimeOffset? expiresAt) => JsonSerializer.Serialize(new
    {
        version = 1,
        entries = new[] { new { providerId = "test", modelId = "selected", capability = "Tools", supported, expiresAt } }
    });
}
