using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Agents;
using Lumen.Core.Domain;

namespace Lumen.Providers;

// An agent that can call models via the provider system. Requires a provider configuration
// (provider kind and model ID) in its AGENT.md frontmatter.
public sealed class ModelAgent : AgentBase
{
    private readonly AgentDefinition definition;
    private readonly ProviderFactory providerFactory;
    private readonly ICredentialStore credentialStore;
    private readonly List<Message> history = new();
    private int turnCount;

    public ModelAgent(AgentDefinition definition, ProviderFactory providerFactory, ICredentialStore credentialStore)
        : base(definition.Name, definition.Scope)
    {
        this.definition = definition;
        this.providerFactory = providerFactory;
        this.credentialStore = credentialStore;
    }

    public override async Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (!definition.HasModelConfig)
            return CreateErrorResult("Agent has no model configuration (provider and model required in AGENT.md frontmatter).");

        if (definition.MaxTurns.HasValue && turnCount >= definition.MaxTurns.Value)
            return CreateErrorResult($"Agent turn limit ({definition.MaxTurns.Value}) reached.");

        if (!Enum.TryParse<ProviderKind>(definition.ProviderKind!, true, out var providerKind))
            return CreateErrorResult($"Unknown provider kind: {definition.ProviderKind}");

        var chatProvider = providerFactory.Chat(providerKind);
        var configuration = new ProviderConfiguration(Guid.NewGuid().ToString("D"), definition.Name, providerKind, "http://localhost", definition.ModelId!);
        var credential = await credentialStore.ReadAsync(configuration.Id, cancellationToken).ConfigureAwait(false);

        // Build model info with minimal required fields
        var modelInfo = new ModelInfo(
            providerId: configuration.ProviderId,
            modelId: definition.ModelId!,
            capabilities: ModelCapabilities.Text | ModelCapabilities.Streaming,
            displayName: definition.ModelId,
            maxOutputTokens: definition.MaxTokens ?? 4096
        );

        var systemPrompt = new Message(MessageRole.System, new[] { new TextPart(definition.Body) });
        var userMessage = new Message(MessageRole.User, new[] { new TextPart(taskDescription) });
        history.Add(systemPrompt);
        history.Add(userMessage);

        var request = new ChatRequest(configuration, modelInfo, history.ToArray(), ReasoningIntent.Off, Array.Empty<ToolSpec>(), definition.MaxTokens ?? 4096);

        var responseText = "";
        try
        {
            await chatProvider.StreamAsync(request, credential, async evt =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is TextDelta td) responseText += td.Text;
                else if (evt is StreamError se) throw new ProviderException(se.Code);
                else if (evt is Done) { /* finished */ }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return CreateErrorResult($"Model call failed: {ex.Code}", ex);
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Model call failed: {ex.Message}", ex);
        }

        history.Add(new Message(MessageRole.Assistant, new[] { new TextPart(responseText) }));
        turnCount++;

        return CreateResult(responseText, true, new Dictionary<string, object>
        {
            ["source"] = definition.Source,
            ["task"] = taskDescription,
            ["turns"] = turnCount
        });
    }
}