using System;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Lumen.Core.Bridge;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Lumen.Vsix;

internal sealed class LumenControl : UserControl, IDisposable
{
    private static readonly ResourceManager Strings = new("Lumen.Vsix.Properties.Strings", typeof(LumenControl).Assembly);
    private readonly JoinableTaskFactory taskFactory;
    private readonly WebView2 browser;
    private readonly TextBlock status;
    private bool started;
    private bool disposed;

    internal LumenControl(JoinableTaskFactory taskFactory)
    {
        this.taskFactory = taskFactory;
        browser = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen", "WebView2")
            },
            Visibility = Visibility.Hidden
        };
        status = new TextBlock { Text = Strings.GetString("Loading"), Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
        var grid = new Grid();
        grid.Children.Add(browser);
        grid.Children.Add(status);
        Content = grid;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (started || disposed)
            return;
        started = true;
        taskFactory.RunAsync(InitializeAsync).FileAndForget("Lumen/InitializeWebView");
    }

    private async Task InitializeAsync()
    {
        try
        {
            await taskFactory.SwitchToMainThreadAsync();
            await browser.EnsureCoreWebView2Async();
            if (disposed)
                return;
            var core = browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("lumen.local",
                Path.Combine(Path.GetDirectoryName(typeof(LumenControl).Assembly.Location)!, "wwwroot"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            VSColorTheme.ThemeChanged += OnThemeChanged;
            core.Navigate("https://lumen.local/index.html");
        }
        catch (Exception exception)
        {
            await taskFactory.SwitchToMainThreadAsync();
            if (!disposed)
            {
                status.Text = Strings.GetString("StartupError");
                ActivityLog.LogError("Lumen", "WebView2 initialization: " + exception.GetType().Name);
            }
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) =>
        e.Cancel = !BridgeProtocol.IsTrustedSource(e.Uri);

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var uri = new Uri(e.Request.Uri);
        if (uri.Scheme != "https" || uri.Host != "lumen.local" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            e.Response = browser.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        status.Visibility = e.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
        browser.Visibility = e.IsSuccess ? Visibility.Visible : Visibility.Hidden;
        if (!e.IsSuccess)
            status.Text = Strings.GetString("StartupError");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var reply = BridgeProtocol.Handle(e.Source, e.WebMessageAsJson, ReadTheme());
        if (reply != null)
            browser.CoreWebView2.PostWebMessageAsJson(reply.ToJson());
    }

    private void OnThemeChanged(ThemeChangedEventArgs e) =>
        taskFactory.RunAsync(async () =>
        {
            await taskFactory.SwitchToMainThreadAsync();
            if (!disposed && browser.CoreWebView2 != null)
                browser.CoreWebView2.PostWebMessageAsJson(new BridgeMessage
                {
                    Id = Guid.NewGuid().ToString("D"), Type = "theme.changed", Payload = ReadTheme()
                }.ToJson());
        }).FileAndForget("Lumen/ThemeChanged");

    private static BridgePayload ReadTheme()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new BridgePayload
        {
            Culture = CultureInfo.CurrentUICulture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en",
            Background = ToCss(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey)),
            Foreground = ToCss(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey)),
            Accent = ToCss(VSColorTheme.GetThemedColor(EnvironmentColors.SystemHighlightColorKey))
        };
    }

    private static string ToCss(System.Drawing.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed)
            return;
        disposed = true;
        Loaded -= OnLoaded;
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        if (browser.CoreWebView2 != null)
        {
            browser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            browser.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            browser.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            browser.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
        }
        browser.Dispose();
        GC.SuppressFinalize(this);
    }
}
