using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen.Core.Domain;

public interface IWriteToolExecutor
{
    IReadOnlyList<ToolSpec> Specs { get; }
    Task<IWriteToolExecutor> CaptureAsync(CancellationToken cancellationToken);
    Task BeginTurnAsync(bool automatic, CancellationToken cancellationToken);
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
    Task UndoTurnAsync(CancellationToken cancellationToken);
    int PendingChanges { get; }
}
