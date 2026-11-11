using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;
using Xunit.Sdk;

namespace Lumen.Tests;

public sealed class VsixPackageTests
{
    private static readonly string[] Required =
    {
        "Lumen.Vsix.dll", "Lumen.Vsix.pkgdef", "Lumen.Core.dll", "Lumen.Providers.dll", "Lumen.Tools.dll", "System.Text.Json.dll",
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll",
        "runtimes/win-x64/native/WebView2Loader.dll", "pt-BR/Lumen.Vsix.resources.dll",
        "wwwroot/index.html", "wwwroot/app.mjs", "wwwroot/byok.mjs", "wwwroot/byok-bridge.mjs",
        "wwwroot/locales/en.json", "wwwroot/locales/pt-BR.json",
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

    [Fact]
    public void CoreProvidersAndToolsShareJsonTypeIdentity()
    {
        using var zip = ZipFile.OpenRead(PackagePath());
        var core = JsonReference(zip, "Lumen.Core.dll");
        Assert.Equal(core, JsonReference(zip, "Lumen.Providers.dll"));
        Assert.Equal(core, JsonReference(zip, "Lumen.Tools.dll"));
    }

    [Fact]
    public void NativeHostDoesNotAccessInvocationJsonAcrossFrameworks()
    {
        using var zip = ZipFile.OpenRead(PackagePath());
        using var stream = zip.GetEntry("Lumen.Vsix.dll")!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes); bytes.Position = 0;
        using var pe = new PEReader(bytes);
        var metadata = pe.GetMetadataReader();
        var calls = metadata.MemberReferences.Select(metadata.GetMemberReference)
            .Where(member => member.Parent.Kind == HandleKind.TypeReference)
            .Select(member =>
            {
                var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) + "." + metadata.GetString(member.Name);
            }).ToArray();
        AssertNativeHostCallsSafe(calls);
    }

    [Theory]
    [InlineData("Lumen.Core.Domain.ToolInvocation.get_Arguments")]
    [InlineData("Microsoft.VisualStudio.Text.Operations.ITextUndoHistory.get_UndoStack")]
    [InlineData("Microsoft.VisualStudio.Text.Operations.ITextUndoHistory.get_LastUndoTransaction")]
    public void NativeHostJsonIdentityVerifierRejectsForbiddenGetter(string getter)
    {
        var safeShellCalls = new[] { "Lumen.Tools.CommandToolCatalog.Validate" };
        Assert.ThrowsAny<XunitException>(() => AssertNativeHostCallsSafe(safeShellCalls.Append(getter)));
        AssertNativeHostCallsSafe(safeShellCalls);
    }

    private static void AssertNativeHostCallsSafe(IEnumerable<string> calls)
    {
        var forbidden = new[]
        {
            "Lumen.Core.Domain.ToolInvocation.get_Arguments",
            "Microsoft.VisualStudio.Text.Operations.ITextUndoHistory.get_UndoStack",
            "Microsoft.VisualStudio.Text.Operations.ITextUndoHistory.get_LastUndoTransaction"
        };
        Assert.All(forbidden, member => Assert.DoesNotContain(member, calls));
    }

    private static string JsonReference(ZipArchive zip, string assembly)
    {
        using var stream = zip.GetEntry(assembly)!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes); bytes.Position = 0;
        using var pe = new PEReader(bytes);
        var metadata = pe.GetMetadataReader();
        var reference = Assert.Single(metadata.AssemblyReferences.Select(metadata.GetAssemblyReference),
            value => metadata.GetString(value.Name) == "System.Text.Json");
        return metadata.GetString(reference.Name) + "," + reference.Version + "," + Convert.ToHexString(metadata.GetBlobBytes(reference.PublicKeyOrToken));
    }

    [Theory]
    [InlineData("Lumen.Vsix.pkgdef")]
    [InlineData("System.Text.Json.dll")]
    [InlineData("Lumen.Providers.dll")]
    [InlineData("runtimes/win-x64/native/WebView2Loader.dll")]
    [InlineData("wwwroot/index.html")]
    [InlineData("wwwroot/byok.mjs")]
    [InlineData("wwwroot/byok-bridge.mjs")]
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
