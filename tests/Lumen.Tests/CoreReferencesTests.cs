using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using Lumen.Core.Domain;
using Xunit;

namespace Lumen.Tests;

public sealed class CoreReferencesTests
{
    private static readonly string[] ForbiddenPrefixes =
    {
        "Microsoft.VisualStudio",
        "Microsoft.Web.WebView2",
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Windows",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase"
    };

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void CoreReferencesNoVisualStudioOrHttpAssembly()
    {
        var assembly = typeof(Conversation).Assembly;
        var names = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(names, IsForbidden);
        Assert.Contains("System.Text.Json", names);
        Assert.Equal(
            ".NETStandard,Version=v2.0",
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
    }

    [Fact]
    public void ForbiddenReferenceDetectorActuallyRejects()
    {
        Assert.True(IsForbidden("Microsoft.VisualStudio.Shell.15.0"));
        Assert.True(IsForbidden("Microsoft.VisualStudio.SDK"));
        Assert.True(IsForbidden("Microsoft.Web.WebView2.Wpf"));
        Assert.True(IsForbidden("System.Net.Http"));
        Assert.True(IsForbidden("PresentationFramework"));

        Assert.False(IsForbidden("System.Text.Json"));
        Assert.False(IsForbidden("netstandard"));
        Assert.False(IsForbidden("System.Runtime"));
    }

    [Fact]
    public void TestAssemblyItselfIsNotTheSubjectOfTheCheck()
    {
        // Negative control on the negative control: the detector must be able to
        // find a forbidden reference when one exists, so the pass above is not vacuous.
        var names = new[] { "System.Runtime", "Microsoft.VisualStudio.Shell.Framework" };
        Assert.Contains(names, IsForbidden);
    }
}
