using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Core.Domain;

public interface IShellToolExecutor
{
    IReadOnlyList<ToolSpec> Specs { get; }
    Task<IShellToolExecutor> CaptureAsync(CancellationToken cancellationToken);
    IShellToolExecutor ForMode(ChatMode mode, PermissionLevel permission);
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}
