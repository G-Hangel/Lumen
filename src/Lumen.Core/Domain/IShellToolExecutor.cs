using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Core.Domain;

public interface IShellToolExecutor
{
    IReadOnlyList<ToolSpec> Specs { get; }
    Task<IShellToolExecutor> CaptureAsync(CancellationToken cancellationToken);
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}
