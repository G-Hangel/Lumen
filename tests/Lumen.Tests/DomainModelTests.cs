using System;
using System.Collections.Generic;
using System.Text.Json;
using Lumen.Core.Domain;
using Xunit;

namespace Lumen.Tests;

public sealed class DomainModelTests
{
    [Theory]
    [InlineData(ChatMode.Ask, ToolCategory.Read, ToolPermission.Allow)]
    [InlineData(ChatMode.Ask, ToolCategory.WriteInsideWorkspace, ToolPermission.Deny)]
    [InlineData(ChatMode.Ask, ToolCategory.WriteOutsideWorkspace, ToolPermission.Deny)]
    [InlineData(ChatMode.Ask, ToolCategory.ExecuteCommand, ToolPermission.Deny)]
    [InlineData(ChatMode.Plan, ToolCategory.Read, ToolPermission.Allow)]
    [InlineData(ChatMode.Plan, ToolCategory.WriteInsideWorkspace, ToolPermission.Deny)]
    [InlineData(ChatMode.Plan, ToolCategory.WriteOutsideWorkspace, ToolPermission.Deny)]
    [InlineData(ChatMode.Plan, ToolCategory.ExecuteCommand, ToolPermission.Deny)]
    [InlineData(ChatMode.Agent, ToolCategory.Read, ToolPermission.Allow)]
    [InlineData(ChatMode.Agent, ToolCategory.WriteInsideWorkspace, ToolPermission.AllowWithUndo)]
    [InlineData(ChatMode.Agent, ToolCategory.WriteOutsideWorkspace, ToolPermission.RequireApproval)]
    [InlineData(ChatMode.Agent, ToolCategory.ExecuteCommand, ToolPermission.RequireApproval)]
    public void PermissionMatrixMatchesTheLockedPolicy(ChatMode mode, ToolCategory category, ToolPermission expected) =>
        Assert.Equal(expected, ChatModePolicy.Evaluate(mode, category));

    [Fact]
    public void ReadOnlyModesCannotWriteOrExecute()
    {
        foreach (var mode in new[] { ChatMode.Ask, ChatMode.Plan })
        {
            Assert.False(ChatModePolicy.CanWrite(mode));
            Assert.False(ChatModePolicy.CanExecuteCommand(mode));
        }

        Assert.True(ChatModePolicy.CanWrite(ChatMode.Agent));
        Assert.True(ChatModePolicy.CanExecuteCommand(ChatMode.Agent));
    }

    [Fact]
    public void AgentModeNeverAutoApprovesShellCommands() =>
        Assert.Equal(ToolPermission.RequireApproval, ChatModePolicy.Evaluate(ChatMode.Agent, ToolCategory.ExecuteCommand));

    [Fact]
    public void PolicyRejectsUndefinedEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatModePolicy.Evaluate((ChatMode)999, ToolCategory.Read));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatModePolicy.Evaluate(ChatMode.Agent, (ToolCategory)999));
    }

    [Fact]
    public void CapabilityFlagsMatchTheArchitectureContract()
    {
        Assert.Equal(0, (int)ModelCapabilities.None);
        Assert.Equal(1, (int)ModelCapabilities.Text);
        Assert.Equal(2, (int)ModelCapabilities.Vision);
        Assert.Equal(4, (int)ModelCapabilities.Tools);
        Assert.Equal(8, (int)ModelCapabilities.ParallelTools);
        Assert.Equal(16, (int)ModelCapabilities.Reasoning);
        Assert.Equal(32, (int)ModelCapabilities.Streaming);
        Assert.Equal(64, (int)ModelCapabilities.StructuredOutput);
        Assert.Equal(128, (int)ModelCapabilities.Pdf);
    }

    [Fact]
    public void ReasoningEffortsFollowTheUnifiedScale()
    {
        var names = Enum.GetNames(typeof(ReasoningIntent));
        Assert.Equal(
            new[] { "Off", "Minimal", "Low", "Medium", "High", "Max" },
            names);
    }

    [Fact]
    public void ModelInfoReportsPerCapabilityOrigin()
    {
        var model = new ModelInfo(
            "openrouter",
            "anthropic/claude-sonnet-4.5",
            ModelCapabilities.Text | ModelCapabilities.Vision | ModelCapabilities.Tools | ModelCapabilities.Reasoning,
            displayName: "Claude Sonnet 4.5",
            reasoning: new ReasoningSupport(ReasoningKind.Effort, new[] { ReasoningIntent.Low, ReasoningIntent.High }),
            contextWindowTokens: 200_000,
            maxOutputTokens: 64_000,
            origins: new List<CapabilityOrigin>
            {
                new(ModelCapabilities.Vision, CapabilitySource.Native, Confidence.High),
                new(ModelCapabilities.Tools, CapabilitySource.Heuristic, Confidence.Low)
            });

        Assert.True(model.Has(ModelCapabilities.Vision));
        Assert.True(model.Has(ModelCapabilities.Text | ModelCapabilities.Vision));
        Assert.False(model.Has(ModelCapabilities.Pdf));
        Assert.False(model.Has(ModelCapabilities.None));
        Assert.Equal(CapabilitySource.Native, model.SourceOf(ModelCapabilities.Vision));
        Assert.Equal(CapabilitySource.Heuristic, model.SourceOf(ModelCapabilities.Tools));
        Assert.Null(model.SourceOf(ModelCapabilities.Pdf));
    }

    [Fact]
    public void ReasoningCapabilityAndSupportMustAgree()
    {
        var support = new ReasoningSupport(ReasoningKind.BudgetTokens, new[] { ReasoningIntent.Medium });

        Assert.Throws<ArgumentException>(() => new ModelInfo("p", "m", ModelCapabilities.Text, reasoning: support));
        Assert.Throws<ArgumentException>(() => new ModelInfo(
            "p", "m", ModelCapabilities.Text | ModelCapabilities.Reasoning, reasoning: null));
    }

    [Fact]
    public void ModelInfoRejectsUnknownFlagsAndNonPositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelInfo("p", "m", (ModelCapabilities)1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelInfo("p", "m", ModelCapabilities.Text, contextWindowTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelInfo("p", "m", ModelCapabilities.Text, maxOutputTokens: -1));
        Assert.Throws<ArgumentNullException>(() => new ModelInfo(string.Empty, "m", ModelCapabilities.Text));
        Assert.Throws<ArgumentNullException>(() => new ModelInfo("p", string.Empty, ModelCapabilities.Text));
    }

    [Fact]
    public void CapabilityOriginDescribesExactlyOneCapability()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityOrigin(
            ModelCapabilities.Vision | ModelCapabilities.Tools, CapabilitySource.Native, Confidence.High));
        Assert.Throws<ArgumentException>(() => new CapabilityOrigin(
            ModelCapabilities.None, CapabilitySource.Native, Confidence.High));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityOrigin(
            ModelCapabilities.Vision, (CapabilitySource)999, Confidence.High));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityOrigin(
            ModelCapabilities.Vision, CapabilitySource.Native, (Confidence)999));
    }

    [Fact]
    public void ReasoningSupportRequiresAtLeastOneKnownEffort()
    {
        Assert.Throws<ArgumentException>(() => new ReasoningSupport(ReasoningKind.Effort, Array.Empty<ReasoningIntent>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReasoningSupport(
            ReasoningKind.Effort, new[] { (ReasoningIntent)999 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReasoningSupport(
            (ReasoningKind)999, new[] { ReasoningIntent.Low }));
        Assert.Throws<ArgumentNullException>(() => new ReasoningSupport(ReasoningKind.Effort, null!));
    }

    [Theory]
    [InlineData("read file")]
    [InlineData("read;rm -rf")]
    [InlineData("")]
    [InlineData("ler.arquivo")]
    [InlineData("tool$name")]
    [InlineData("a-very-long-tool-name-that-goes-well-beyond-the-sixty-four-character-limit-allowed")]
    public void ToolSpecRejectsNamesProvidersWouldRefuse(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new ToolSpec(name, "d", JsonDocument.Parse("{}").RootElement));

    [Fact]
    public void ToolSpecAcceptsProviderSafeNamesAndRequiresObjectSchema()
    {
        var spec = new ToolSpec("search_files", "Search the workspace.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        Assert.Equal("search_files", spec.Name);

        Assert.Throws<ArgumentException>(() => new ToolSpec("search_files", "d", JsonDocument.Parse("[]").RootElement));
        Assert.Throws<ArgumentNullException>(() => new ToolSpec("search_files", null!, JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void ToolInvocationTracksTerminalStates()
    {
        var arguments = JsonDocument.Parse("""{"path":"a.cs"}""").RootElement;
        var pending = new ToolInvocation("t1", "read_file", arguments);
        Assert.Equal(ToolCallStatus.Pending, pending.Status);
        Assert.False(pending.IsTerminal);

        foreach (var status in new[] { ToolCallStatus.Completed, ToolCallStatus.Failed, ToolCallStatus.Cancelled })
        {
            Assert.True(pending.WithStatus(status).IsTerminal);
        }

        Assert.False(pending.WithStatus(ToolCallStatus.Running).IsTerminal);
        Assert.Throws<ArgumentException>(() => new ToolInvocation("t1", "read_file", JsonDocument.Parse("5").RootElement));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocation("t1", "n", arguments, (ToolCallStatus)999));
    }

    [Fact]
    public void ToolResultConvertsToAMessagePart()
    {
        var part = new ToolResult("t1", false, "ok").ToContentPart();
        Assert.Equal("t1", part.InvocationId);
        Assert.False(part.IsError);
        Assert.Equal("ok", part.Content);
    }

    [Fact]
    public void StreamEventsExposeTheNormalizedContract()
    {
        Assert.Equal(StreamEventKind.TextDelta, new TextDelta("a").Kind);
        Assert.Equal(StreamEventKind.ReasoningDelta, new ReasoningDelta("a").Kind);
        Assert.Equal(StreamEventKind.ToolCallStart, new ToolCallStart("t1", "read_file").Kind);
        Assert.Equal(StreamEventKind.ToolCallArgsDelta, new ToolCallArgsDelta("t1", "{\"p").Kind);
        Assert.Equal(StreamEventKind.Usage, new Usage(10, 20, 5).Kind);
        Assert.Equal(StreamEventKind.Done, new Done(FinishReason.ToolUse).Kind);
        Assert.Equal(StreamEventKind.Error, new StreamError("rate_limited", "429").Kind);

        var end = new ToolCallEnd("t1", JsonDocument.Parse("""{"path":"a.cs"}""").RootElement);
        Assert.Equal(StreamEventKind.ToolCallEnd, end.Kind);
        Assert.Equal("a.cs", end.ParsedArgs.GetProperty("path").GetString());
    }

    [Fact]
    public void StreamEventsRejectInvalidPayloads()
    {
        Assert.Throws<ArgumentNullException>(() => new TextDelta(null!));
        Assert.Throws<ArgumentNullException>(() => new ToolCallStart(string.Empty, "n"));
        Assert.Throws<ArgumentNullException>(() => new ToolCallArgsDelta("t1", null!));
        Assert.Throws<ArgumentException>(() => new ToolCallEnd("t1", JsonDocument.Parse("\"x\"").RootElement));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Usage(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Usage(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Usage(0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Done((FinishReason)999));
        Assert.Throws<ArgumentNullException>(() => new StreamError(string.Empty, "m"));
    }

    [Fact]
    public void AppendingAMessagePreservesIdentityAndBumpsTheTimestamp()
    {
        var created = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        var conversation = new Conversation("c1", ChatMode.Plan, new List<Message>(), createdAtUtc: created, updatedAtUtc: created);
        var updated = conversation.WithMessage(new Message(MessageRole.User, new ContentPart[] { new TextPart("hi") }));

        Assert.Empty(conversation.Messages);
        Assert.Equal("c1", updated.Id);
        Assert.Equal(ChatMode.Plan, updated.Mode);
        Assert.Single(updated.Messages);
        Assert.Equal(created, updated.CreatedAtUtc);
        Assert.True(updated.UpdatedAtUtc >= created);
        Assert.Throws<ArgumentNullException>(() => conversation.WithMessage(null!));
    }
}
