using System;
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
        return await Task.Run(() => SkillDiscovery.Discover(SkillDiscovery.Locations(projectRoot, string.IsNullOrEmpty(home) ? null : home), cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
