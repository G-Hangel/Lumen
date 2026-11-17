using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Core.Agents;
using Lumen.Providers;
using Lumen.Tools;
using Microsoft.VisualStudio.Threading;

namespace Lumen.Vsix;

internal sealed class VisualStudioAgentSource : IAgentSource
{
    private readonly JoinableTaskFactory taskFactory;

    internal VisualStudioAgentSource(JoinableTaskFactory taskFactory) => this.taskFactory = taskFactory;

    public async Task<AgentSnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        await taskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? projectRoot;
        try { projectRoot = VisualStudioContextSource.GetWorkspaceRoot(out _); }
        catch (ProviderException) { projectRoot = null; }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var locations = new List<AgentLocation>(AgentCatalog.Locations(projectRoot, string.IsNullOrEmpty(home) ? null : home));
        return await Task.Run(() => AgentCatalog.Discover(locations, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
