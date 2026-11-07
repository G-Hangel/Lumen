using System;

namespace Lumen.Core.Domain;

public enum ChatMode
{
    Ask,
    Plan,
    Agent
}

public enum ToolCategory
{
    Read,
    WriteInsideWorkspace,
    WriteOutsideWorkspace,
    ExecuteCommand
}

public enum ToolPermission
{
    Deny,
    Allow,
    AllowWithUndo,
    RequireApproval
}

// Encodes AGENTS.md §1 so the rule is enforced in the approval path, not only
// hidden in the UI: hiding a button is not a security boundary.
public static class ChatModePolicy
{
    public static ToolPermission Evaluate(ChatMode mode, ToolCategory category)
    {
        if (!Enum.IsDefined(typeof(ChatMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown chat mode.");
        }

        if (!Enum.IsDefined(typeof(ToolCategory), category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown tool category.");
        }

        return mode switch
        {
            ChatMode.Agent => category switch
            {
                ToolCategory.Read => ToolPermission.Allow,
                ToolCategory.WriteInsideWorkspace => ToolPermission.AllowWithUndo,
                ToolCategory.WriteOutsideWorkspace => ToolPermission.RequireApproval,
                _ => ToolPermission.RequireApproval
            },
            _ => category == ToolCategory.Read ? ToolPermission.Allow : ToolPermission.Deny
        };
    }

    public static bool CanWrite(ChatMode mode) =>
        Evaluate(mode, ToolCategory.WriteInsideWorkspace) != ToolPermission.Deny;

    public static bool CanExecuteCommand(ChatMode mode) =>
        Evaluate(mode, ToolCategory.ExecuteCommand) != ToolPermission.Deny;
}
