using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using Xunit.Sdk;

namespace Lumen.Tests;

public sealed class VsixPackageTests
{
    private static readonly string[] Required =
    {
        "Lumen.Vsix.dll", "Lumen.Vsix.pkgdef", "Lumen.Core.dll", "System.Text.Json.dll",
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll",
        "runtimes/win-x64/native/WebView2Loader.dll", "pt-BR/Lumen.Vsix.resources.dll",
        "wwwroot/index.html", "wwwroot/app.mjs", "wwwroot/locales/en.json", "wwwroot/locales/pt-BR.json",
        "wwwroot/vendor/libraries.mjs", "wwwroot/vendor/manifest.json"
    };

    [Fact]
    public void ReleaseVsixContainsRuntimeAndLocalUi()
    {
        var package = ReadPackage();
        AssertValid(package);
        var output = Path.GetDirectoryName(PackagePath())!;
        foreach (var assembly in Directory.GetFiles(output, "*.dll"))
            Assert.Contains(Path.GetFileName(assembly), package.Keys);
    }

    [Theory]
    [InlineData("Lumen.Vsix.pkgdef")]
    [InlineData("System.Text.Json.dll")]
    [InlineData("runtimes/win-x64/native/WebView2Loader.dll")]
    [InlineData("wwwroot/index.html")]
    public void VerifierRejectsMissingRuntimeOrUi(string missing)
    {
        var package = ReadPackage();
        AssertValid(package);
        package.Remove(missing);
        Assert.ThrowsAny<XunitException>(() => AssertValid(package));
    }

    [Theory]
    [InlineData("Version=\"[18.0,)\"", "Version=\"[17.0,)\"")]
    [InlineData("amd64", "x86")]
    public void VerifierRejectsManifestMutation(string before, string after)
    {
        var package = ReadPackage();
        AssertValid(package);
        package["extension.vsixmanifest"] = package["extension.vsixmanifest"].Replace(before, after, StringComparison.Ordinal);
        Assert.ThrowsAny<XunitException>(() => AssertValid(package));
    }

    private static void AssertValid(Dictionary<string, string> package)
    {
        foreach (var name in Required)
            Assert.Contains(name, package.Keys);
        XNamespace ns = "http://schemas.microsoft.com/developer/vsx-schema/2011";
        var manifest = XDocument.Parse(package["extension.vsixmanifest"]);
        var target = Assert.Single(manifest.Descendants(ns + "InstallationTarget"));
        Assert.Equal("Microsoft.VisualStudio.Community", target.Attribute("Id")?.Value);
        Assert.Equal("[18.0,)", target.Attribute("Version")?.Value);
        Assert.Equal("amd64", target.Element(ns + "ProductArchitecture")?.Value);
        Assert.DoesNotContain(package.Keys, name => name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ReadPackage()
    {
        using var zip = ZipFile.OpenRead(PackagePath());
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            if (entry.FullName != "extension.vsixmanifest")
                return string.Empty;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }, StringComparer.Ordinal);
    }

    private static string PackagePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Lumen.sln")))
            directory = directory.Parent ?? throw new DirectoryNotFoundException("Lumen.sln");
        return Path.Combine(directory.FullName, "src", "Lumen.Vsix", "bin", "Release", "net48", "Lumen.Vsix.vsix");
    }
}
