using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Core.Domain;

public interface IReadToolExecutor
{
    IReadOnlyList<ToolSpec> Specs { get; }
    Task<IReadToolExecutor> CaptureAsync(CancellationToken cancellationToken);
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}
