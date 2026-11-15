using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;
using Lumen.Tools;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioSkillSource : ISkillSource, ISkillEditor
{
    private readonly JoinableTaskFactory taskFactory;
    private readonly Stack<(string Path, string? Content, bool Existed)> undo = new();

    internal VisualStudioSkillSource(JoinableTaskFactory taskFactory) => this.taskFactory = taskFactory;

    public async Task<SkillSnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? projectRoot;
        try { projectRoot = VisualStudioContextSource.GetWorkspaceRoot(out _); }
        catch (ProviderException) { projectRoot = null; }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var locations = new List<SkillLocation>(SkillDiscovery.Locations(projectRoot, string.IsNullOrEmpty(home) ? null : home));
        var bundled = Path.Combine(Path.GetDirectoryName(typeof(VisualStudioSkillSource).Assembly.Location) ?? string.Empty, "skills");
        if (Directory.Exists(bundled)) locations.Add(new SkillLocation(bundled, "bundled/skills", SkillScope.User));
        return await Task.Run(() => SkillDiscovery.Discover(locations, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> WriteAsync(string name, string description, string body, bool edit, CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        var root = VisualStudioContextSource.GetWorkspaceRoot(out _);
        if (string.IsNullOrWhiteSpace(root)) throw new ProviderException("context_unavailable");
        if (!SkillParser.IsValidName(name)) throw new ProviderException("invalid_name");
        var folder = Path.GetFullPath(Path.Combine(root, ".lumen", "skills", name));
        var allowed = Path.GetFullPath(Path.Combine(root, ".lumen", "skills")) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ProviderException("outside_workspace");
        var path = Path.Combine(folder, "SKILL.md");
        if (edit && !File.Exists(path)) throw new ProviderException("path_unavailable");
        if (!edit && File.Exists(path)) throw new ProviderException("file_exists");
        var existed = File.Exists(path);
        undo.Push((path, existed ? File.ReadAllText(path) : null, existed));
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, "---\nname: " + name + "\ndescription: " + description.Replace("\r", " ").Replace("\n", " ") + "\n---\n\n" + body);
        return path;
    }

    public async Task UndoAsync(CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (undo.Count == 0) throw new ProviderException("undo_unavailable");
        var change = undo.Pop();
        if (change.Existed) File.WriteAllText(change.Path, change.Content ?? string.Empty);
        else if (File.Exists(change.Path)) File.Delete(change.Path);
    }

    public async Task<string> OpenFolderAsync(CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        var root = VisualStudioContextSource.GetWorkspaceRoot(out _);
        if (string.IsNullOrWhiteSpace(root)) throw new ProviderException("context_unavailable");
        var folder = Path.Combine(root, ".lumen", "skills");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false, CreateNoWindow = true });
        return folder;
    }
}
