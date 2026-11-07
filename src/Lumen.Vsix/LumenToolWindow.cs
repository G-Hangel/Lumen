using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Lumen.Vsix;

[Guid("2dccaf3a-5eb9-4e24-8888-512c83519b9e")]
public sealed class LumenToolWindow : ToolWindowPane
{
    public LumenToolWindow() : base(null)
    {
        Caption = "Lumen";
    }

    protected override void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        base.Initialize();
        Content = new LumenControl(((AsyncPackage)Package).JoinableTaskFactory);
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing && Content is LumenControl control)
            control.Dispose();
        base.Dispose(disposing);
    }
}
