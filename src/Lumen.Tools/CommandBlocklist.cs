using System;
using System.Collections.Generic;

namespace Lumen.Tools;

// Defense in depth (AGENTS.md §5): the user confirmation is the primary gate.
// This catches obviously destructive commands before they even reach the prompt,
// so the user never sees an "allow rm -rf /" dialog. It never replaces confirmation.
public static class CommandBlocklist
{
    public static string? Match(string executable, IReadOnlyList<string> args)
    {
        var exe = NormalizeName(executable);

        // Unconditional: these executables are destructive regardless of arguments.
        if (Is(exe, "format") || Is(exe, "shutdown") || Is(exe, "diskpart") || Is(exe, "cipher"))
            return "blocked_executable";

        if (Is(exe, "rm"))
        {
            var hasRecursive = Has(args, "-r", "-rf", "--recursive", "-R");
            var hasForce = Has(args, "-f", "--force");
            if (hasRecursive && hasForce && HasRootTarget(args)) return "rm_recursive_root";
            if (Has(args, "-rf", "-fr") && HasRootTarget(args)) return "rm_recursive_root";
        }

        if (Is(exe, "del") || Is(exe, "erase") || Is(exe, "rd") || Is(exe, "rmdir"))
        {
            if (Has(args, "/s") && (Has(args, "/q") || Has(args, "/f"))) return "recursive_delete";
        }

        if (Is(exe, "git"))
        {
            if (Contains(args, "push") && Has(args, "--force", "-f")) return "git_force_push";
            if (Contains(args, "reset") && Has(args, "--hard")) return "git_reset_hard";
            if (Contains(args, "clean") && Has(args, "-f", "--force", "-fd", "-df") && Has(args, "-d", "-fd", "-df")) return "git_clean_force";
        }

        // cmd.exe /c or powershell -Command with a destructive embedded command.
        if (Is(exe, "cmd") || Is(exe, "cmd.exe"))
        {
            var joined = " " + string.Join(" ", args).ToUpperInvariant() + " ";
            if (joined.Contains(" FORMAT ") || joined.Contains(" SHUTDOWN ") ||
                joined.Contains(" DEL /S ") || joined.Contains(" RD /S ") || joined.Contains(" RMDIR /S "))
                return "blocked_shell_command";
        }

        return null;
    }

    private static string NormalizeName(string executable)
    {
        var name = executable;
        var sep = Math.Max(executable.LastIndexOf('/'), executable.LastIndexOf('\\'));
        if (sep >= 0 && sep < executable.Length - 1) name = executable.Substring(sep + 1);
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && name.Substring(dot).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, dot);
        return name.ToUpperInvariant();
    }

    private static bool Is(string normalized, string expected) =>
        string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Has(IReadOnlyList<string> args, params string[] flags)
    {
        for (var i = 0; i < args.Count; i++)
            for (var j = 0; j < flags.Length; j++)
                if (string.Equals(args[i], flags[j], StringComparison.Ordinal))
                    return true;
        return false;
    }

    private static bool Contains(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], value, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool HasRootTarget(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "/" || arg == "\\" || arg == "/*" || arg == "\\*" ||
                arg.Length == 3 && arg[1] == ':' && (arg[2] == '\\' || arg[2] == '/') &&
                arg[0] >= 'A' && arg[0] <= 'z')
                return true;
        }
        return false;
    }
}
