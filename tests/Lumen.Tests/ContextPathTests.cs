using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Lumen.Providers;
using Xunit;
using Xunit.Abstractions;

namespace Lumen.Tests;

public sealed class ContextPathTests
{
    private readonly ITestOutputHelper output;
    public ContextPathTests(ITestOutputHelper output) { this.output = output; }

    [Theory]
    [InlineData("file")]
    [InlineData("solution")]
    [InlineData("selection")]
    [InlineData("output")]
    public void SnapshotPreservesExactCapturedTextAndReadOnlyProperties(string kind)
    {
        using var folder = new Folder();
        var source = Path.Combine(folder.Path, "code.cs");
        File.WriteAllText(source, "old disk text");
        const string text = "unsaved 漢字 \ud83d\ude80\r\n\ttext";
        var value = new ContextSnapshot(folder.Path + Path.DirectorySeparatorChar, kind, "code.cs:2-3", text, source);
        File.WriteAllText(source, "new disk text");
        Assert.Equal(folder.Path, value.WorkspaceRoot);
        Assert.Equal(kind, value.Kind);
        Assert.Equal("code.cs:2-3", value.Label);
        Assert.Equal(text, value.Text);
        Assert.Equal(source, value.SourcePath);
        Assert.All(typeof(ContextSnapshot).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.True(typeof(ContextSnapshot).IsSealed);
        Assert.Equal(new[] { "CaptureAsync", "ValidateAsync" }, typeof(IContextSource).GetMethods().Select(method => method.Name).OrderBy(name => name));
    }

    [Fact]
    public void ExactLimitsAndOptionalSourceAreAccepted()
    {
        using var folder = new Folder();
        var value = new ContextSnapshot(folder.Path, "output", new string('l', 1024), new string('x', 65536));
        Assert.Null(value.SourcePath);
        Assert.Equal(65536, value.Text.Length);
    }

    [Theory]
    [InlineData(1025, 1)]
    [InlineData(1, 65537)]
    public void OversizedSnapshotsAreRejectedBeforeCapture(int labelLength, int textLength)
    {
        using var folder = new Folder();
        Error("context_too_large", () => new ContextSnapshot(folder.Path, "file", new string('l', labelLength), new string('t', textLength)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FILE")]
    [InlineData("tool")]
    [InlineData(null)]
    public void UnknownKindsAreControlled(string? kind)
    {
        using var folder = new Folder();
        Error("invalid_message", () => new ContextSnapshot(folder.Path, kind!, "label", "text"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("label\0")]
    [InlineData("label\n")]
    [InlineData("label\u007f")]
    public void InvalidLabelsAreControlled(string? label)
    {
        using var folder = new Folder();
        Error("invalid_message", () => new ContextSnapshot(folder.Path, "file", label!, "text"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("text\0")]
    public void EmptyNulAndInvalidUnicodeTextAreControlled(string? text)
    {
        using var folder = new Folder();
        Error("invalid_message", () => new ContextSnapshot(folder.Path, "file", "label", text!));
    }

    [Fact]
    public void RuntimeUnpairedSurrogatesAreRejectedRatherThanMetadataReplacementCharacters()
    {
        using var folder = new Folder();
        var high = new string((char)0xd800, 1);
        var low = new string((char)0xdfff, 1);
        foreach (var invalid in new[] { high, low, high + "x" + low, high + high, low + high })
        {
            Error("invalid_message", () => new ContextSnapshot(folder.Path, "file", "label", invalid));
            Error("invalid_message", () => new ContextSnapshot(folder.Path, "file", invalid, "text"));
        }
        Assert.Equal(high + low, new ContextSnapshot(folder.Path, "file", "label", high + low).Text);
    }

    [Fact]
    public void RelativeAbsoluteAndDotPathsAreCanonicalInsideBoundary()
    {
        using var folder = new Folder();
        var source = Path.Combine(folder.Path, "sub", "code.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "text");
        Assert.Equal(source, ContextPathGuard.Validate(folder.Path, "sub/code.cs"));
        Assert.Equal(source, ContextPathGuard.Validate(folder.Path + "\\", ".\\sub\\code.cs"));
        Assert.Equal(source, ContextPathGuard.Validate(folder.Path, source));
        Assert.Equal(folder.Path, ContextPathGuard.Validate(folder.Path, folder.Path));
        Assert.Equal(Path.Combine(folder.Path, "missing.cs"), ContextPathGuard.Validate(folder.Path, "missing.cs"));
        Assert.Equal(source.ToUpperInvariant(), ContextPathGuard.Validate(folder.Path.ToUpperInvariant(), source.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("sub/../inside.cs")]
    [InlineData("sub\\..\\inside.cs")]
    [InlineData("sub/.. /inside.cs")]
    [InlineData("sub/.../inside.cs")]
    [InlineData("sub/inside.cs ")]
    [InlineData("sub/inside.cs.")]
    [InlineData("C:inside.cs")]
    [InlineData("\\inside.cs")]
    [InlineData("/inside.cs")]
    [InlineData("\\\\server\\share\\code.cs")]
    [InlineData("//server/share/code.cs")]
    [InlineData("\\\\?\\C:\\code.cs")]
    [InlineData("\\\\.\\NUL")]
    [InlineData("code.cs:secret")]
    [InlineData("code.cs::$DATA")]
    [InlineData("code\0.cs")]
    [InlineData("code\n.cs")]
    [InlineData("*.cs")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT².txt")]
    public void DangerousPathsAreRejectedEvenIfTheyNormalizeInside(string path)
    {
        using var folder = new Folder();
        Error("context_outside_workspace", () => ContextPathGuard.Validate(folder.Path, path));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("\\\\server\\share")]
    [InlineData("/relative")]
    [InlineData("C:/workspace/../workspace")]
    public void InvalidRootsAreRejected(string root)
    {
        Error("context_outside_workspace", () => ContextPathGuard.Validate(root, "code.cs"));
    }

    [Fact]
    public void SiblingPrefixAndOutsideAbsoluteFilesAreRejected()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        foreach (var path in new[] { folder.Path + "-sibling\\code.cs", Path.Combine(outside.Path, "code.cs"), "Z:\\code.cs" })
            Error("context_outside_workspace", () => ContextPathGuard.Validate(folder.Path, path));
        Error("context_outside_workspace", () => new ContextSnapshot(folder.Path, "file", "label", "text", Path.Combine(outside.Path, "code.cs")));
    }

    [Fact]
    public void DirectoriesMissingRootAndNonDirectoryAncestorsAreUnavailable()
    {
        using var folder = new Folder();
        var directory = Path.Combine(folder.Path, "directory");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(folder.Path, "file");
        File.WriteAllText(file, "text");
        Error("context_unavailable", () => ContextPathGuard.Validate(folder.Path, directory));
        Error("context_unavailable", () => ContextPathGuard.Validate(file, file));
        Error("context_unavailable", () => ContextPathGuard.Validate(folder.Path, Path.Combine(file, "child.cs")));
        Error("context_unavailable", () => ContextPathGuard.Validate(directory + "-missing", directory + "-missing"));
        Error("context_unavailable", () => ContextPathGuard.Validate(folder.Path, ""));
        Error("context_unavailable", () => ContextPathGuard.Validate(null!, "file.cs"));
    }

    [Fact]
    public void ReparseDirectoryRootAncestorAndMissingDescendantsAreRejected()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var target = Path.Combine(outside.Path, "code.cs");
        File.WriteAllText(target, "outside text");
        var link = Path.Combine(folder.Path, "link");
        CreateDirectoryLink(link, outside.Path);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            foreach (var path in new[] { link, Path.Combine(link, "code.cs"), Path.Combine(link, "missing", "child.cs") })
                Error("context_outside_workspace", () => ContextPathGuard.Validate(folder.Path, path));
            Error("context_outside_workspace", () => ContextPathGuard.Validate(link, link));
            Error("context_outside_workspace", () => ContextPathGuard.Validate(Path.Combine(link, "missing"), Path.Combine(link, "missing", "child.cs")));
            Error("context_outside_workspace", () => new ContextSnapshot(folder.Path, "file", "label", "text", Path.Combine(link, "code.cs")));
            Assert.Equal("outside text", File.ReadAllText(target));
            Assert.False(Directory.Exists(Path.Combine(outside.Path, "missing")));
            output.WriteLine("Real directory junction/symlink negative controls exercised.");
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ReparseFilesAreRejectedWhenLinkCreationIsAvailable()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var target = Path.Combine(outside.Path, "code.cs");
        File.WriteAllText(target, "outside text");
        var link = Path.Combine(folder.Path, "code.cs");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException || exception is PlatformNotSupportedException)
        {
            output.WriteLine("GAP: file symlink creation unavailable; file reparse negative control not exercised.");
            return;
        }
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Error("context_outside_workspace", () => ContextPathGuard.Validate(folder.Path, link));
            Assert.Equal("outside text", File.ReadAllText(target));
            output.WriteLine("Real file symlink negative control exercised.");
        }
        finally { File.Delete(link); }
    }

    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<ProviderException>(action).Code);

    private static void CreateDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException || exception is PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) throw;
        }
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.NotEqual(new IntPtr(-1), handle);
        try { Assert.True(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)); }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class Folder : IDisposable
    {
        internal Folder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.ContextPathTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
