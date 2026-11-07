using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace Lumen.Vsix;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(LumenToolWindow))]
[Guid(PackageGuid)]
public sealed class LumenPackage : AsyncPackage
{
    public const string PackageGuid = "69e1a944-246f-4705-b303-32018d4a8df5";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        commandService?.AddCommand(new MenuCommand(OpenWindow,
            new CommandID(new Guid("96a71c4a-f4cb-4711-80de-38e40b2e6ad8"), 0x0100)));
    }

    private void OpenWindow(object sender, EventArgs e) =>
        JoinableTaskFactory.RunAsync(async () =>
        {
            await ShowToolWindowAsync(typeof(LumenToolWindow), 0, true, DisposalToken);
        }).FileAndForget("Lumen/OpenWindow");
}
