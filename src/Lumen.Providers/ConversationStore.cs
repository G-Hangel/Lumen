using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Domain;

namespace Lumen.Providers;

public sealed class StoredConversation
{
    public StoredConversation(string configurationId, Conversation conversation, IReadOnlyList<string> turnStates)
    {
        ConversationStore.RequireId(configurationId);
        if (conversation is null || turnStates is null) throw new ProviderException("history_corrupt");
        ConfigurationId = configurationId;
        Conversation = conversation;
        TurnStates = Array.AsReadOnly(turnStates.ToArray());
        Validate();
    }

    public string ConfigurationId { get; }
    public Conversation Conversation { get; }
    public IReadOnlyList<string> TurnStates { get; }

    internal void Validate()
    {
        ConversationStore.RequireId(ConfigurationId);
        ConversationStore.RequireId(Conversation.Id);
        if (Conversation.Messages.Count > 20_000) throw new ProviderException("history_too_large");
        if (Conversation.Messages.Count != TurnStates.Count * 2 ||
            Conversation.CreatedAtUtc.Offset != TimeSpan.Zero || Conversation.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            Conversation.UpdatedAtUtc < Conversation.CreatedAtUtc)
            throw new ProviderException("history_corrupt");
        ConversationStore.RequireTitle(Conversation.Title);
        RequireModel(Conversation.ModelId);
        for (var index = 0; index < Conversation.Messages.Count; index++)
        {
            var message = Conversation.Messages[index];
            if (message is null || message.Role != (index % 2 == 0 ? MessageRole.User : MessageRole.Assistant) ||
                message.Parts.Count == 0 || message.Parts.Any(part => part is null))
                throw new ProviderException("history_corrupt");
            if (message.ModelId is not null) RequireModel(message.ModelId);
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart text: ConversationStore.RequireUnicode(text.Text); break;
                    case ImagePart image:
                        ConversationStore.RequireUnicode(image.MediaType);
                        ConversationStore.RequireUnicode(image.Base64Data);
                        break;
                    case ReasoningPart reasoning:
                        ConversationStore.RequireUnicode(reasoning.Text);
                        if (reasoning.ProviderState is not null) ConversationStore.RequireUnicode(reasoning.ProviderState);
                        break;
                    case ToolCallPart call:
                        ConversationStore.RequireUnicode(call.Id);
                        ConversationStore.RequireUnicode(call.Name);
                        if (call.ProviderState is not null) ConversationStore.RequireUnicode(call.ProviderState);
                        ConversationStore.ValidateJson(call.Arguments);
                        break;
                    case ToolResultPart result:
                        ConversationStore.RequireUnicode(result.InvocationId);
                        ConversationStore.RequireUnicode(result.Content);
                        break;
                    case UnknownPart unknown: ConversationStore.ValidateJson(unknown.Raw); break;
                    default: throw new ProviderException("history_corrupt");
                }
            }
        }
        if (TurnStates.Any(state => state != "completed" && state != "cancelled" && state != "failed" && state != "paused"))
            throw new ProviderException("history_corrupt");
    }

    private static void RequireModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model!.Length > 512) throw new ProviderException("history_corrupt");
        ConversationStore.RequireUnicode(model);
    }
}

public sealed class ConversationStore
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ConversationStore(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory) ||
                directory.StartsWith("\\\\", StringComparison.Ordinal) ||
                directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == "..") ||
                !Path.GetPathRoot(directory)!.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                throw new ProviderException("invalid_message");
            if (Path.DirectorySeparatorChar == '\\')
            {
                var root = Path.GetPathRoot(directory)!;
                if (root.Length != 3 || root[1] != ':' || !char.IsLetter(root[0])) throw new ProviderException("invalid_message");
            }
            try { RequireUnicode(directory); }
            catch (ProviderException) { throw new ProviderException("invalid_message"); }
            this.directory = Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            throw new ProviderException("invalid_message");
        }
    }

    public Task SaveAsync(StoredConversation value, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        if (value is null) throw new ProviderException("invalid_message");
        await SaveCoreAsync(value, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task<StoredConversation> ReadAsync(string id, CancellationToken cancellationToken) =>
        RunAsync(() => ReadCoreAsync(id, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<StoredConversation>> ListAsync(string query, CancellationToken cancellationToken) => RunAsync<IReadOnlyList<StoredConversation>>(async () =>
    {
        if (query is null || query.Length > 256) throw new ProviderException("invalid_message");
        try { RequireUnicode(query); }
        catch (ProviderException) { throw new ProviderException("invalid_message"); }
        CheckDirectory();
        if (!Directory.Exists(directory)) return Array.Empty<StoredConversation>();
        var values = new List<StoredConversation>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal)) continue;
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsId(id)) continue;
            var value = await ReadCoreAsync(id, cancellationToken).ConfigureAwait(false);
            if ((value.Conversation.Title?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                value.Conversation.Messages.SelectMany(message => message.Parts).OfType<TextPart>()
                    .Any(text => text.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) || query.Length == 0)
                values.Add(value);
        }
        return values.OrderByDescending(value => value.Conversation.UpdatedAtUtc)
            .ThenBy(value => value.Conversation.Id, StringComparer.Ordinal).ToArray();
    }, cancellationToken);

    public Task<StoredConversation> RenameAsync(string id, string title, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        RequireId(id);
        if (title is null) throw new ProviderException("invalid_message");
        try { RequireTitle(title); }
        catch (ProviderException) { throw new ProviderException("invalid_message"); }
        var original = await ReadCoreAsync(id, cancellationToken).ConfigureAwait(false);
        var conversation = original.Conversation;
        var renamed = new StoredConversation(original.ConfigurationId, new Conversation(id, conversation.Mode,
            conversation.Messages, title, conversation.ModelId, conversation.ReasoningIntent,
            conversation.CreatedAtUtc, DateTimeOffset.UtcNow), original.TurnStates);
        await SaveCoreAsync(renamed, cancellationToken).ConfigureAwait(false);
        return renamed;
    }, cancellationToken);

    public Task DeleteAsync(string id, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await ReadCoreAsync(id, cancellationToken).ConfigureAwait(false);
        var path = GetPath(id);
        if (!CheckFile(path)) throw new ProviderException("conversation_not_found");
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return true;
    }, cancellationToken);

    private Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }, cancellationToken);

    private async Task SaveCoreAsync(StoredConversation value, CancellationToken cancellationToken)
    {
        value.Validate();
        string json;
        try
        {
            var conversationJson = ConversationSerializer.ToJson(value.Conversation);
            json = "{\"version\":1,\"configurationId\":" + JsonSerializer.Serialize(value.ConfigurationId) +
                ",\"conversation\":" + conversationJson + ",\"turnStates\":" + JsonSerializer.Serialize(value.TurnStates) + "}";
            if (Utf8.GetByteCount(json) > MaxBytes) throw new ProviderException("history_too_large");
            Parse(json, value.Conversation.Id);
        }
        catch (Exception exception) when (IsJsonError(exception)) { throw new ProviderException("history_corrupt"); }
        var path = GetPath(value.Conversation.Id);
        if (CheckFile(path)) await ReadCoreAsync(value.Conversation.Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CheckDirectory();
        Directory.CreateDirectory(directory);
        CheckDirectory();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Utf8.GetBytes(json);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var exists = CheckFile(path);
            if (exists) await ReadCoreAsync(value.Conversation.Id, cancellationToken).ConfigureAwait(false);
            CheckDirectory();
            CheckFile(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (exists) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally
        {
            // Never follow a directory exchanged for a junction while cleaning our temporary file.
            try { if (CheckFile(temporary)) File.Delete(temporary); }
            catch (ProviderException) { }
        }
    }

    private async Task<StoredConversation> ReadCoreAsync(string id, CancellationToken cancellationToken)
    {
        var path = GetPath(id);
        if (!CheckFile(path)) throw new ProviderException("conversation_not_found");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaxBytes) throw new ProviderException("history_corrupt");
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await file.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (content.Length + count > MaxBytes) throw new ProviderException("history_corrupt");
                content.Write(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(Utf8.GetString(content.GetBuffer(), 0, (int)content.Length), id);
        }
        catch (FileNotFoundException) { throw new ProviderException("conversation_not_found"); }
        catch (DirectoryNotFoundException) { throw new ProviderException("conversation_not_found"); }
        catch (Exception exception) when (IsJsonError(exception)) { throw new ProviderException("history_corrupt"); }
    }

    private static StoredConversation Parse(string json, string id)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            ValidateJson(root);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                root.GetProperty("version").GetInt32() != 1) throw new ProviderException("history_corrupt");
            var configurationId = root.GetProperty("configurationId").GetString()!;
            var data = root.GetProperty("conversation");
            foreach (var name in new[] { "id", "mode", "messages", "modelId", "reasoningIntent", "createdAtUtc", "updatedAtUtc" })
                if (!data.TryGetProperty(name, out _)) throw new ProviderException("history_corrupt");
            if (!data.GetProperty("createdAtUtc").TryGetDateTimeOffset(out var created) || created == default ||
                !data.GetProperty("updatedAtUtc").TryGetDateTimeOffset(out var updated) || updated == default ||
                !ConversationSerializer.TryParse(data.GetRawText(), out var conversation, out _) ||
                conversation!.Id != id ||
                data.GetProperty("mode").GetString() != conversation.Mode.ToString() ||
                data.GetProperty("reasoningIntent").GetString() != conversation.ReasoningIntent.ToString())
                throw new ProviderException("history_corrupt");
            foreach (var message in data.GetProperty("messages").EnumerateArray())
            {
                foreach (var part in message.GetProperty("parts").EnumerateArray())
                {
                    if (part.GetProperty("kind").GetString() is "reasoning" or "toolCall" &&
                        part.TryGetProperty("providerState", out var state) &&
                        state.ValueKind != JsonValueKind.Null && state.ValueKind != JsonValueKind.String)
                        throw new ProviderException("history_corrupt");
                }
            }
            var states = root.GetProperty("turnStates").EnumerateArray().Select(state => state.GetString()!).ToArray();
            return new StoredConversation(configurationId, conversation, states);
        }
        catch (ProviderException) { throw new ProviderException("history_corrupt"); }
        catch (Exception exception) when (IsJsonError(exception) || exception is KeyNotFoundException)
        {
            throw new ProviderException("history_corrupt");
        }
    }

    internal static void ValidateJson(JsonElement value)
    {
        try { ValidateJsonCore(value); }
        catch (Exception exception) when (IsJsonError(exception)) { throw new ProviderException("history_corrupt"); }
    }

    private static void ValidateJsonCore(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                RequireUnicode(property.Name);
                if (!names.Add(property.Name)) throw new ProviderException("history_corrupt");
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
        }
        else if (value.ValueKind == JsonValueKind.String) RequireUnicode(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Undefined) throw new ProviderException("history_corrupt");
    }

    internal static void RequireUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || ++index == value.Length || !char.IsLowSurrogate(value[index]))
                throw new ProviderException("history_corrupt");
        }
    }

    internal static void RequireTitle(string? title)
    {
        if (title is null) return;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160) throw new ProviderException("history_corrupt");
        RequireUnicode(title);
    }

    private static bool IsJsonError(Exception exception) => exception is JsonException ||
        exception is InvalidOperationException || exception is ArgumentException || exception is FormatException;

    private static bool IsId(string? id) => Guid.TryParseExact(id, "D", out var guid) && guid != Guid.Empty &&
        string.Equals(id, guid.ToString("D"), StringComparison.Ordinal);

    internal static void RequireId(string id)
    {
        if (!IsId(id)) throw new ProviderException("invalid_message");
    }

    private string GetPath(string id)
    {
        RequireId(id);
        CheckDirectory();
        return Path.Combine(directory, id + ".json");
    }

    private void CheckDirectory()
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var attributes = Attributes(current.FullName);
            if (attributes.HasValue && ((attributes.Value & FileAttributes.ReparsePoint) != 0 ||
                (attributes.Value & FileAttributes.Directory) == 0)) throw new ProviderException("invalid_message");
        }
    }

    private bool CheckFile(string path)
    {
        CheckDirectory();
        var attributes = Attributes(path);
        if (!attributes.HasValue) return false;
        if ((attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ProviderException("invalid_message");
        return true;
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
