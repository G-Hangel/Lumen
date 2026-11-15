using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;
using Lumen.Tools;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioSkillSource : ISkillSource
{
    private readonly JoinableTaskFactory taskFactory;

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
}
