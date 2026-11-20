using System;

namespace Lumen.Core.Domain;

public enum ChatMode
{
    Ask,
    Plan,
    Agent
}

public enum PermissionLevel
{
    Interactive,
    Permissive,
    FullAccess
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
    public static ToolPermission Evaluate(ChatMode mode, ToolCategory category) => Evaluate(mode, category, PermissionLevel.Interactive);

    public static ToolPermission Evaluate(ChatMode mode, ToolCategory category, PermissionLevel permission)
    {
        if (!Enum.IsDefined(typeof(ChatMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown chat mode.");
        }

        if (!Enum.IsDefined(typeof(ToolCategory), category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown tool category.");
        }

        if (!Enum.IsDefined(typeof(PermissionLevel), permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission), permission, "Unknown permission level.");
        }

        var basePermission = mode switch
        {
            ChatMode.Agent => category switch
            {
                ToolCategory.Read => ToolPermission.Allow,
                ToolCategory.WriteInsideWorkspace => ToolPermission.AllowWithUndo,
                ToolCategory.WriteOutsideWorkspace => ToolPermission.RequireApproval,
                _ => ToolPermission.RequireApproval
            },
            ChatMode.Plan => category switch
            {
                ToolCategory.Read => ToolPermission.Allow,
                ToolCategory.WriteInsideWorkspace => ToolPermission.AllowWithUndo,
                _ => ToolPermission.Deny
            },
            _ => category == ToolCategory.Read ? ToolPermission.Allow : ToolPermission.Deny
        };

        if (basePermission != ToolPermission.RequireApproval) return basePermission;

        // Permission levels only relax approval; they never widen a mode's reach.
        if (mode != ChatMode.Agent) return basePermission;
        return permission switch
        {
            PermissionLevel.Permissive when category == ToolCategory.WriteOutsideWorkspace => ToolPermission.AllowWithUndo,
            PermissionLevel.FullAccess when category == ToolCategory.WriteOutsideWorkspace => ToolPermission.AllowWithUndo,
            PermissionLevel.FullAccess when category == ToolCategory.ExecuteCommand => ToolPermission.Allow,
            _ => basePermission
        };
    }

    public static bool CanWrite(ChatMode mode) =>
        Evaluate(mode, ToolCategory.WriteInsideWorkspace) != ToolPermission.Deny;

    public static bool CanExecuteCommand(ChatMode mode) =>
        Evaluate(mode, ToolCategory.ExecuteCommand) != ToolPermission.Deny;
}