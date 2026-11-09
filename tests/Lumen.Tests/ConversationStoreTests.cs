using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;
using Lumen.Providers;
using Xunit;
using Xunit.Abstractions;

namespace Lumen.Tests;

public sealed class ConversationStoreTests
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private const string ConfigurationId = "ef1c0d2a-9d3f-4b6a-8a1e-2c5f7b9d0e11";
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly ITestOutputHelper output;

    public ConversationStoreTests(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public async Task RoundTripUsesExactWrapperAndDoesNotNeedAnExistingProvider()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(folder.File(original.Conversation.Id)));
        Assert.Equal(new[] { "version", "configurationId", "conversation", "turnStates" }, json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(ConfigurationId, json.RootElement.GetProperty("configurationId").GetString());
        Assert.Equal(ConversationSerializer.ToJson(original.Conversation), json.RootElement.GetProperty("conversation").GetRawText());
        var restored = await new ConversationStore(folder.Path).ReadAsync(original.Conversation.Id, CancellationToken.None);
        Assert.Equal(ConfigurationId, restored.ConfigurationId);
        Assert.Equal(original.TurnStates, restored.TurnStates);
        Assert.Equal(ConversationSerializer.ToJson(original.Conversation), ConversationSerializer.ToJson(restored.Conversation));
        Assert.Equal(new[] { folder.File(original.Conversation.Id) }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task RenamePreservesImagesSignedPartsUnknownBytesAndCreationTime()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var renamed = await store.RenameAsync(original.Conversation.Id, "Renamed 漢字", CancellationToken.None);
        Assert.Equal("Renamed 漢字", renamed.Conversation.Title);
        Assert.Equal(original.ConfigurationId, renamed.ConfigurationId);
        Assert.Equal(original.TurnStates, renamed.TurnStates);
        Assert.Equal(original.Conversation.CreatedAtUtc, renamed.Conversation.CreatedAtUtc);
        Assert.InRange(renamed.Conversation.UpdatedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(original.Conversation.Mode, renamed.Conversation.Mode);
        Assert.Equal(original.Conversation.ModelId, renamed.Conversation.ModelId);
        Assert.Equal(original.Conversation.ReasoningIntent, renamed.Conversation.ReasoningIntent);
        var restored = await store.ReadAsync(original.Conversation.Id, CancellationToken.None);
        using var before = JsonDocument.Parse(ConversationSerializer.ToJson(original.Conversation));
        using var after = JsonDocument.Parse(ConversationSerializer.ToJson(restored.Conversation));
        Assert.Equal(before.RootElement.GetProperty("messages").GetRawText(), after.RootElement.GetProperty("messages").GetRawText());
        Assert.Equal(Pixel, Assert.IsType<ImagePart>(restored.Conversation.Messages[0].Parts[1]).Base64Data);
        Assert.Equal("signed-reasoning-\0-漢字", Assert.IsType<ReasoningPart>(restored.Conversation.Messages[1].Parts[0]).ProviderState);
        Assert.Equal("signed-tool-part", Assert.IsType<ToolCallPart>(restored.Conversation.Messages[1].Parts[1]).ProviderState);
        var unknown = Assert.IsType<UnknownPart>(restored.Conversation.Messages[1].Parts[3]);
        Assert.Equal("{\"kind\":\"future\",\"number\":1e+01,\"payload\":\"opaque\"}", unknown.Raw.GetRawText());
    }

    [Fact]
    public async Task SearchFindsTitleAndBothMessageRolesButNotOpaqueReasoningOrTools()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        foreach (var query in new[] { "TITLE", "USER NEEDLE", "ASSISTANT NEEDLE", "漢字" })
            Assert.Equal(original.Conversation.Id, Assert.Single(await store.ListAsync(query, CancellationToken.None)).Conversation.Id);
        foreach (var query in new[] { "absent text", "signed-reasoning", "signed-tool-part", "opaque", Pixel })
            Assert.Empty(await store.ListAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task ListIsNewestFirstWithStableIdTiesAndIgnoresUnrelatedFilesAndTemporaryFiles()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var first = Sample("11111111-1111-4111-8111-111111111111");
        var second = Sample("22222222-2222-4222-8222-222222222222");
        var newest = Sample("33333333-3333-4333-8333-333333333333", updated: Created.AddDays(1));
        foreach (var value in new[] { second, newest, first }) await store.SaveAsync(value, CancellationToken.None);
        foreach (var name in new[] { "unrelated.json", "providers.json", "notes.txt", first.Conversation.Id + ".json.owned.tmp", "EF1C0D2A-9D3F-4B6A-8A1E-2C5F7B9D0E11.json" })
            await File.WriteAllTextAsync(Path.Combine(folder.Path, name), "not history");
        Directory.CreateDirectory(Path.Combine(folder.Path, "44444444-4444-4444-8444-444444444444.json"));
        var listed = await store.ListAsync("", CancellationToken.None);
        Assert.Equal(new[] { newest.Conversation.Id, first.Conversation.Id, second.Conversation.Id }, listed.Select(value => value.Conversation.Id));
        Assert.Equal(8, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task DeleteOnlyRemovesTargetAndAllMissingOperationsHaveControlledErrors()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var first = Sample();
        var second = Sample();
        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);
        var secondBytes = await File.ReadAllBytesAsync(folder.File(second.Conversation.Id));
        var temporary = folder.File(first.Conversation.Id) + ".unrelated.tmp";
        await File.WriteAllTextAsync(temporary, "keep");
        await store.DeleteAsync(first.Conversation.Id, CancellationToken.None);
        Assert.False(File.Exists(folder.File(first.Conversation.Id)));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(folder.File(second.Conversation.Id)));
        Assert.Equal("keep", await File.ReadAllTextAsync(temporary));
        await Error("conversation_not_found", () => store.ReadAsync(first.Conversation.Id, CancellationToken.None));
        await Error("conversation_not_found", () => store.RenameAsync(first.Conversation.Id, "Missing", CancellationToken.None));
        await Error("conversation_not_found", () => store.DeleteAsync(first.Conversation.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../../outside")]
    [InlineData("..\\..\\outside")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ef1c0d2a9d3f4b6a8a1e2c5f7b9d0e11")]
    [InlineData("{ef1c0d2a-9d3f-4b6a-8a1e-2c5f7b9d0e11}")]
    [InlineData("EF1C0D2A-9D3F-4B6A-8A1E-2C5F7B9D0E11")]
    [InlineData(" ef1c0d2a-9d3f-4b6a-8a1e-2c5f7b9d0e11")]
    [InlineData("ef1c0d2a-9d3f-4b6a-8a1e-2c5f7b9d0e11 ")]
    [InlineData("ef1c0d2a-9d3f-4b6a-8a1e-2c5f7b9d0e11.json")]
    public async Task NoncanonicalIdsAndTraversalAreRejectedBeforeStorage(string? id)
    {
        using var folder = new Folder();
        var unused = Path.Combine(folder.Path, "not-created");
        var store = new ConversationStore(unused);
        await Error("invalid_message", () => store.ReadAsync(id!, CancellationToken.None));
        await Error("invalid_message", () => store.RenameAsync(id!, "Title", CancellationToken.None));
        await Error("invalid_message", () => store.DeleteAsync(id!, CancellationToken.None));
        Assert.Equal("invalid_message", Assert.Throws<ProviderException>(() => new StoredConversation(id!, Sample().Conversation, new[] { "completed" })).Code);
        if (!string.IsNullOrEmpty(id))
        {
            var badConversation = new Conversation(id, ChatMode.Ask, Array.Empty<Message>(), modelId: "model");
            Assert.Equal("invalid_message", Assert.Throws<ProviderException>(() => new StoredConversation(ConfigurationId, badConversation, Array.Empty<string>())).Code);
        }
        Assert.False(Directory.Exists(unused));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("bad\0path")]
    public void InvalidBasePathsAreRejected(string path) =>
        Assert.Equal("invalid_message", Assert.Throws<ProviderException>(() => new ConversationStore(path)).Code);

    [Fact]
    public void TraversalInBasePathIsRejected() =>
        Assert.Equal("invalid_message", Assert.Throws<ProviderException>(() => new ConversationStore(Path.Combine(Path.GetTempPath(), "history", "..", "outside"))).Code);

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task EveryTerminalStateAndNullTitleRoundTrips(string state)
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample(title: null, state: state);
        await store.SaveAsync(original, CancellationToken.None);
        var restored = await store.ReadAsync(original.Conversation.Id, CancellationToken.None);
        Assert.Null(restored.Conversation.Title);
        Assert.Equal(state, Assert.Single(restored.TurnStates));
    }

    [Fact]
    public async Task EmptyConversationAndMaximumTurnCountAreSupported()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var empty = new StoredConversation(ConfigurationId, new Conversation(Guid.NewGuid().ToString("D"), ChatMode.Plan,
            Array.Empty<Message>(), modelId: "model"), Array.Empty<string>());
        await store.SaveAsync(empty, CancellationToken.None);
        Assert.Empty((await store.ReadAsync(empty.Conversation.Id, CancellationToken.None)).TurnStates);
        var pair = Sample().Conversation.Messages;
        var messages = Enumerable.Range(0, 10_000).SelectMany(_ => pair).ToArray();
        var full = new StoredConversation(ConfigurationId, new Conversation(Guid.NewGuid().ToString("D"), ChatMode.Agent,
            messages, new string('t', 160), new string('m', 512)), Enumerable.Repeat("completed", 10_000).ToArray());
        await store.SaveAsync(full, CancellationToken.None);
        Assert.Equal(10_000, (await store.ReadAsync(full.Conversation.Id, CancellationToken.None)).TurnStates.Count);
        Assert.Equal("history_too_large", Assert.Throws<ProviderException>(() => new StoredConversation(ConfigurationId,
            new Conversation(Guid.NewGuid().ToString("D"), ChatMode.Agent, messages.Concat(pair).ToArray(), modelId: "model"),
            Enumerable.Repeat("completed", 10_001).ToArray())).Code);
    }

    [Fact]
    public void InvalidPairsStatesModelsAndTitlesAreRejectedAndStateInputIsCopied()
    {
        var original = Sample();
        var conversation = original.Conversation;
        var states = new[] { "completed" };
        var value = new StoredConversation(ConfigurationId, conversation, states);
        states[0] = "failed";
        Assert.Equal("completed", value.TurnStates[0]);
        foreach (var badStates in new[] { Array.Empty<string>(), new[] { "running" }, new[] { "Completed" }, new[] { "completed", "failed" }, new[] { (string)null! } })
            Assert.Equal("history_corrupt", Assert.Throws<ProviderException>(() => new StoredConversation(ConfigurationId, conversation, badStates)).Code);
        foreach (var messages in new[]
        {
            new[] { conversation.Messages[0] },
            new[] { conversation.Messages[1], conversation.Messages[0] },
            new[] { new Message(MessageRole.System, new[] { new TextPart("bad") }), conversation.Messages[1] },
            new[] { conversation.Messages[0], new Message(MessageRole.Tool, new[] { new TextPart("bad") }) },
            new[] { conversation.Messages[0], (Message)null! }
        })
            Assert.Equal("history_corrupt", Assert.Throws<ProviderException>(() => new StoredConversation(ConfigurationId,
                new Conversation(conversation.Id, ChatMode.Ask, messages, modelId: "model"), new[] { "completed" })).Code);
        foreach (var model in new[] { null, "", " ", new string('m', 513), "model\uD800" })
            Assert.Equal("history_corrupt", Assert.Throws<ProviderException>(() => new StoredConversation(ConfigurationId,
                new Conversation(conversation.Id, ChatMode.Ask, conversation.Messages, modelId: model), new[] { "completed" })).Code);
        foreach (var title in new[] { "", " ", new string('t', 161), "title\uD800" })
            Assert.Equal("history_corrupt", Assert.Throws<ProviderException>(() => Sample(title: title)).Code);
    }

    [Theory]
    [InlineData("version_missing")]
    [InlineData("version_wrong")]
    [InlineData("version_string")]
    [InlineData("configuration_missing")]
    [InlineData("configuration_invalid")]
    [InlineData("conversation_missing")]
    [InlineData("states_missing")]
    [InlineData("states_null")]
    [InlineData("states_invalid")]
    [InlineData("states_count")]
    [InlineData("states_type")]
    [InlineData("id_mismatch")]
    [InlineData("id_invalid")]
    [InlineData("mode_missing")]
    [InlineData("mode_numeric")]
    [InlineData("effort_missing")]
    [InlineData("effort_numeric")]
    [InlineData("messages_missing")]
    [InlineData("messages_null")]
    [InlineData("messages_odd")]
    [InlineData("messages_wrong_role")]
    [InlineData("parts_null")]
    [InlineData("parts_empty")]
    [InlineData("model_missing")]
    [InlineData("model_null")]
    [InlineData("model_empty")]
    [InlineData("model_long")]
    [InlineData("title_empty")]
    [InlineData("title_long")]
    [InlineData("created_missing")]
    [InlineData("created_invalid")]
    [InlineData("created_default")]
    [InlineData("updated_missing")]
    [InlineData("updated_invalid")]
    [InlineData("updated_before_created")]
    [InlineData("not_utc")]
    [InlineData("extra_wrapper_field")]
    [InlineData("reasoning_state_invalid")]
    [InlineData("tool_state_invalid")]
    public async Task InvalidMetadataIsCorruptAndCannotBeOverwrittenOrSilentlySkipped(string variant)
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var path = folder.File(original.Conversation.Id);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var data = root["conversation"]!.AsObject();
        switch (variant)
        {
            case "version_missing": root.Remove("version"); break;
            case "version_wrong": root["version"] = 2; break;
            case "version_string": root["version"] = "1"; break;
            case "configuration_missing": root.Remove("configurationId"); break;
            case "configuration_invalid": root["configurationId"] = "../outside"; break;
            case "conversation_missing": root.Remove("conversation"); break;
            case "states_missing": root.Remove("turnStates"); break;
            case "states_null": root["turnStates"] = null; break;
            case "states_invalid": root["turnStates"]![0] = "running"; break;
            case "states_count": root["turnStates"] = new JsonArray(); break;
            case "states_type": root["turnStates"]![0] = 7; break;
            case "id_mismatch": data["id"] = Guid.NewGuid().ToString("D"); break;
            case "id_invalid": data["id"] = original.Conversation.Id.ToUpperInvariant(); break;
            case "mode_missing": data.Remove("mode"); break;
            case "mode_numeric": data["mode"] = 1; break;
            case "effort_missing": data.Remove("reasoningIntent"); break;
            case "effort_numeric": data["reasoningIntent"] = 1; break;
            case "messages_missing": data.Remove("messages"); break;
            case "messages_null": data["messages"] = null; break;
            case "messages_odd": data["messages"]!.AsArray().RemoveAt(1); break;
            case "messages_wrong_role": data["messages"]![0]!["role"] = "Assistant"; break;
            case "parts_null": data["messages"]![0]!["parts"]![0] = null; break;
            case "parts_empty": data["messages"]![0]!["parts"] = new JsonArray(); break;
            case "model_missing": data.Remove("modelId"); break;
            case "model_null": data["modelId"] = null; break;
            case "model_empty": data["modelId"] = " "; break;
            case "model_long": data["modelId"] = new string('m', 513); break;
            case "title_empty": data["title"] = ""; break;
            case "title_long": data["title"] = new string('t', 161); break;
            case "created_missing": data.Remove("createdAtUtc"); break;
            case "created_invalid": data["createdAtUtc"] = "invalid"; break;
            case "created_default": data["createdAtUtc"] = "0001-01-01T00:00:00+00:00"; break;
            case "updated_missing": data.Remove("updatedAtUtc"); break;
            case "updated_invalid": data["updatedAtUtc"] = false; break;
            case "updated_before_created": data["updatedAtUtc"] = Created.AddDays(-1).ToString("O"); break;
            case "not_utc": data["createdAtUtc"] = Created.ToOffset(TimeSpan.FromHours(1)).ToString("O"); break;
            case "extra_wrapper_field": root["secret"] = "not allowed"; break;
            case "reasoning_state_invalid": data["messages"]![1]!["parts"]![0]!["providerState"] = 7; break;
            case "tool_state_invalid": data["messages"]![1]!["parts"]![1]!["providerState"] = false; break;
            default: throw new InvalidOperationException(variant);
        }
        await File.WriteAllTextAsync(path, root.ToJsonString());
        var corruptBytes = await File.ReadAllBytesAsync(path);
        await Error("history_corrupt", () => store.ReadAsync(original.Conversation.Id, CancellationToken.None));
        await Error("history_corrupt", () => store.ListAsync("unmatched query", CancellationToken.None));
        await Error("history_corrupt", () => store.SaveAsync(original, CancellationToken.None));
        await Error("history_corrupt", () => store.RenameAsync(original.Conversation.Id, "Valid", CancellationToken.None));
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("duplicate")]
    [InlineData("duplicate_nested")]
    [InlineData("surrogate_title")]
    [InlineData("surrogate_unknown")]
    [InlineData("surrogate_property")]
    [InlineData("invalid_utf8")]
    public async Task MalformedJsonUnicodeAndDuplicatePropertiesHaveControlledCorruptionErrors(string variant)
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var path = folder.File(original.Conversation.Id);
        var json = await File.ReadAllTextAsync(path);
        json = variant switch
        {
            "truncated" => json.Substring(0, json.Length - 3),
            "empty" => "",
            "null" => "null",
            "array" => "[]",
            "duplicate" => json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            "duplicate_nested" => json.Replace("\"number\":1e+01", "\"number\":0,\"number\":1e+01", StringComparison.Ordinal),
            "surrogate_title" => json.Replace("Title 漢字", "\\uD800", StringComparison.Ordinal).Replace("Title \\u6F22\\u5B57", "\\uD800", StringComparison.Ordinal),
            "surrogate_unknown" => json.Replace("\"opaque\"", "\"\\uD800\"", StringComparison.Ordinal),
            "surrogate_property" => json.Replace("\"payload\"", "\"\\uD800\"", StringComparison.Ordinal),
            _ => json
        };
        await File.WriteAllTextAsync(path, json);
        if (variant == "invalid_utf8") await File.WriteAllBytesAsync(path, new byte[] { 0x7b, 0xff, 0x7d });
        var bytes = await File.ReadAllBytesAsync(path);
        await Error("history_corrupt", () => store.ReadAsync(original.Conversation.Id, CancellationToken.None));
        await Error("history_corrupt", () => store.SaveAsync(original, CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task OversizedFileIsRejectedBeforeParsingAndOriginalIsNeverOverwritten()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        var path = folder.File(original.Conversation.Id);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.SetLength(MaxBytes + 1L);
        await Error("history_corrupt", () => store.ReadAsync(original.Conversation.Id, CancellationToken.None));
        await Error("history_corrupt", () => store.ListAsync("", CancellationToken.None));
        await Error("history_corrupt", () => store.SaveAsync(original, CancellationToken.None));
        Assert.Equal(MaxBytes + 1L, new FileInfo(path).Length);
        Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task SerializedByteLimitRejectsOversizedSaveAndPreservesPriorBytes()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var path = folder.File(original.Conversation.Id);
        var priorBytes = await File.ReadAllBytesAsync(path);
        var oversized = TextSample(original.Conversation.Id, new string('é', MaxBytes / 6));
        await Error("history_too_large", () => store.SaveAsync(oversized, CancellationToken.None));
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CancellationBeforeIoLeavesDirectoriesAndExistingFilesUntouched()
    {
        using var folder = new Folder();
        var unused = Path.Combine(folder.Path, "not-created");
        var store = new ConversationStore(unused);
        var original = Sample();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(original, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(original.Conversation.Id, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListAsync("", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RenameAsync(original.Conversation.Id, "New", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(original.Conversation.Id, cancellation.Token));
        Assert.False(Directory.Exists(unused));
        await store.SaveAsync(original, CancellationToken.None);
        var path = Path.Combine(unused, original.Conversation.Id + ".json");
        var bytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(TextSample(original.Conversation.Id, "replacement"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RenameAsync(original.Conversation.Id, "New", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(original.Conversation.Id, cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(unused));
    }

    [Fact]
    public async Task CancellationDuringTemporaryWriteKeepsOriginalBytesAndCleansOnlyOwnTemp()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var path = folder.File(original.Conversation.Id);
        var bytes = await File.ReadAllBytesAsync(path);
        var unrelated = path + ".unrelated.tmp";
        await File.WriteAllTextAsync(unrelated, "keep");
        using var cancellation = new CancellationTokenSource();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(folder.Path, "*.tmp") { NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, args) =>
        {
            if (args.FullPath == unrelated) return;
            cancellation.Cancel();
            observed.TrySetResult(true);
        };
        watcher.EnableRaisingEvents = true;
        var replacement = TextSample(original.Conversation.Id, new string('x', 48 * 1024 * 1024));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(replacement, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(new[] { path, unrelated }.OrderBy(value => value), Directory.GetFiles(folder.Path).OrderBy(value => value));
    }

    [Fact]
    public async Task FailedReplacementKeepsPriorBytesAndDoesNotLeaveTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var path = folder.File(original.Conversation.Id);
        var bytes = await File.ReadAllBytesAsync(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(TextSample(original.Conversation.Id, "replacement"), CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void InvalidUnicodeInTextSignaturesOrOpaqueJsonIsNeverLossilyReplaced()
    {
        using var invalidJson = JsonDocument.Parse("{\"kind\":\"future\",\"value\":\"\\uD800\"}");
        using var invalidProperty = JsonDocument.Parse("{\"\\uD800\":1}");
        foreach (var part in new ContentPart[]
        {
            new TextPart("bad\uD800"), new ReasoningPart("", "bad\uD800"),
            new ToolCallPart("call", "tool", invalidProperty.RootElement),
            new UnknownPart("future", invalidJson.RootElement)
        })
        {
            var conversation = new Conversation(Guid.NewGuid().ToString("D"), ChatMode.Ask, new[]
            {
                new Message(MessageRole.User, new[] { new TextPart("valid") }),
                new Message(MessageRole.Assistant, new[] { part })
            }, modelId: "model");
            Assert.Equal("history_corrupt", Assert.Throws<ProviderException>(() =>
                new StoredConversation(ConfigurationId, conversation, new[] { "completed" })).Code);
        }
    }

    [Fact]
    public async Task MissingDirectoryListsEmptyAndRepeatedMessagesAreLegitimateTurns()
    {
        using var folder = new Folder();
        var unused = Path.Combine(folder.Path, "not-created");
        var store = new ConversationStore(unused);
        Assert.Empty(await store.ListAsync("", CancellationToken.None));
        Assert.False(Directory.Exists(unused));
        var repeated = Sample();
        var conversation = new Conversation(repeated.Conversation.Id, ChatMode.Ask,
            repeated.Conversation.Messages.Concat(repeated.Conversation.Messages).ToArray(), modelId: "model");
        await store.SaveAsync(new StoredConversation(ConfigurationId, conversation, new[] { "cancelled", "failed" }), CancellationToken.None);
        var restored = await store.ReadAsync(conversation.Id, CancellationToken.None);
        Assert.Equal(new[] { "cancelled", "failed" }, restored.TurnStates);
        Assert.Equal(4, restored.Conversation.Messages.Count);
    }

    [Fact]
    public async Task InvalidTitleQueryOrMutatedDomainCannotModifyHistory()
    {
        using var folder = new Folder();
        var store = new ConversationStore(folder.Path);
        var original = Sample();
        await store.SaveAsync(original, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(folder.File(original.Conversation.Id));
        foreach (var title in new[] { null, "", " ", new string('t', 161), "invalid\uD800" })
            await Error("invalid_message", () => store.RenameAsync(original.Conversation.Id, title!, CancellationToken.None));
        foreach (var query in new[] { null, new string('q', 257), "invalid\uD800" })
            await Error("invalid_message", () => store.ListAsync(query!, CancellationToken.None));
        Assert.Empty(await store.ListAsync(new string('q', 256), CancellationToken.None));
        var mutableMessages = original.Conversation.Messages.ToList();
        var mutable = new StoredConversation(ConfigurationId, new Conversation(original.Conversation.Id, ChatMode.Ask,
            mutableMessages, modelId: "model"), new[] { "completed" });
        mutableMessages.Clear();
        await Error("history_corrupt", () => store.SaveAsync(mutable, CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(folder.File(original.Conversation.Id)));
        Assert.Equal(new[] { folder.File(original.Conversation.Id) }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task ReparseDirectoryAndAncestorAreRejectedWithoutTouchingOutsideHistory()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var original = Sample();
        await new ConversationStore(outside.Path).SaveAsync(original, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(outside.File(original.Conversation.Id));
        var link = Path.Combine(folder.Path, "link");
        if (!TryDirectoryLink(link, outside.Path))
        {
            output.WriteLine("Directory symlink/junction creation unavailable on this platform.");
            return;
        }
        output.WriteLine("Directory and ancestor reparse rejection exercised.");
        try
        {
            foreach (var directory in new[] { link, Path.Combine(link, "child") })
            {
                var store = new ConversationStore(directory);
                await Error("invalid_message", () => store.ReadAsync(original.Conversation.Id, CancellationToken.None));
                await Error("invalid_message", () => store.SaveAsync(original, CancellationToken.None));
                await Error("invalid_message", () => store.ListAsync("", CancellationToken.None));
                await Error("invalid_message", () => store.RenameAsync(original.Conversation.Id, "New", CancellationToken.None));
                await Error("invalid_message", () => store.DeleteAsync(original.Conversation.Id, CancellationToken.None));
            }
            Assert.Equal(bytes, await File.ReadAllBytesAsync(outside.File(original.Conversation.Id)));
            Assert.False(Directory.Exists(Path.Combine(outside.Path, "child")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ReparseFileIsRejectedForEveryOperationWithoutTouchingItsTarget()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var original = Sample();
        await new ConversationStore(outside.Path).SaveAsync(original, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(outside.File(original.Conversation.Id));
        var link = folder.File(original.Conversation.Id);
        try { File.CreateSymbolicLink(link, outside.File(original.Conversation.Id)); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is PlatformNotSupportedException || exception is IOException)
        {
            output.WriteLine("File symlink creation unavailable on this platform.");
            return;
        }
        output.WriteLine("File symlink rejection exercised.");
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            var store = new ConversationStore(folder.Path);
            await Error("invalid_message", () => store.ReadAsync(original.Conversation.Id, CancellationToken.None));
            await Error("invalid_message", () => store.SaveAsync(original, CancellationToken.None));
            await Error("invalid_message", () => store.ListAsync("", CancellationToken.None));
            await Error("invalid_message", () => store.RenameAsync(original.Conversation.Id, "New", CancellationToken.None));
            await Error("invalid_message", () => store.DeleteAsync(original.Conversation.Id, CancellationToken.None));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(outside.File(original.Conversation.Id)));
        }
        finally { File.Delete(link); }
    }

    private static async Task Error(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<ProviderException>(action)).Code);

    private static StoredConversation Sample(string? id = null, string? title = "Title 漢字", string state = "completed", DateTimeOffset? updated = null)
    {
        using var arguments = JsonDocument.Parse("{\"path\":\"a.cs\",\"flags\":[true,1e+01]}");
        using var unknown = JsonDocument.Parse("{\"kind\":\"future\",\"number\":1e+01,\"payload\":\"opaque\"}");
        return new StoredConversation(ConfigurationId, new Conversation(id ?? Guid.NewGuid().ToString("D"), ChatMode.Agent,
            new[]
            {
                new Message(MessageRole.User, new ContentPart[] { new TextPart("user needle"), new ImagePart("image/png", Pixel, ImageDetail.High) }),
                new Message(MessageRole.Assistant, new ContentPart[]
                {
                    new ReasoningPart("", "signed-reasoning-\0-漢字"),
                    new ToolCallPart("call", "read_file", arguments.RootElement, "signed-tool-part"),
                    new ToolResultPart("call", true, "result"),
                    new UnknownPart("future", unknown.RootElement.Clone()),
                    new TextPart("assistant needle")
                }, "provider/model")
            }, title, "provider/model", ReasoningIntent.High, Created, updated ?? Created.AddMinutes(1)), new[] { state });
    }

    private static StoredConversation TextSample(string id, string text) => new(ConfigurationId,
        new Conversation(id, ChatMode.Ask, new[]
        {
            new Message(MessageRole.User, new[] { new TextPart(text) }),
            new Message(MessageRole.Assistant, new[] { new TextPart("") }, "model")
        }, modelId: "model", createdAtUtc: Created, updatedAtUtc: Created.AddMinutes(1)), new[] { "completed" });

    private static bool TryDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is PlatformNotSupportedException || exception is IOException)
        {
            if (!OperatingSystem.IsWindows() || !CreateJunction(link, target)) return false;
        }
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        return true;
    }

    private static bool CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        var targetName = "\\??\\" + target;
        var substitute = Encoding.Unicode.GetBytes(targetName);
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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class Folder : IDisposable
    {
        internal Folder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.ConversationStoreTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        internal string File(string id) => System.IO.Path.Combine(Path, id + ".json");
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
