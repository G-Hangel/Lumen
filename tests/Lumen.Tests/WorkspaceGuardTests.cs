using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Lumen.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Lumen.Tests;

public sealed class WorkspaceGuardTests
{
    private readonly ITestOutputHelper output;

    public WorkspaceGuardTests(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void ToolErrorsExposeOnlyTheirStableCode()
    {
        var exception = new ToolException("invalid_path");
        Assert.Equal("invalid_path", exception.Code);
        Assert.Equal(exception.Code, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Null(typeof(ToolException).GetProperty(nameof(ToolException.Code))!.SetMethod);
    }

    [Fact]
    public void RootRelativeAbsoluteDotAndCasePathsAreCanonical()
    {
        using var folder = new Folder();
        var directory = Path.Combine(folder.Path, "sub");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "code.cs");
        File.WriteAllText(file, "text");
        var guard = new WorkspaceGuard(folder.Path + "\\");
        Assert.Equal(folder.Path, guard.Root);
        Assert.Null(typeof(WorkspaceGuard).GetProperty(nameof(WorkspaceGuard.Root))!.SetMethod);
        Assert.Equal(file, guard.Resolve("sub/code.cs"));
        Assert.Equal(file, guard.Resolve(".\\sub\\code.cs"));
        Assert.Equal(file, guard.Resolve("sub/./code.cs"));
        Assert.Equal(file, guard.Resolve(file));
        Assert.Equal(file.ToUpperInvariant(), guard.Resolve(file.ToUpperInvariant()));
        Assert.Equal(folder.Path, guard.Resolve("."));
        Assert.Equal(folder.Path, guard.Resolve(folder.Path));
        Assert.Equal(file, guard.ResolveWriteTarget("sub/code.cs"));
        Assert.Equal(directory, guard.Resolve("sub/"));
    }

    [Fact]
    public void SafeDirectoriesAndMissingCreateTargetsAreAllowedWithoutCreatingAnything()
    {
        using var folder = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        var missingLeaf = Path.Combine(folder.Path, "new.cs");
        var missingDescendant = Path.Combine(folder.Path, "new", "nested", "new.cs");
        foreach (var path in new[] { folder.Path, missingLeaf, missingDescendant })
        {
            Assert.Equal(path, guard.Resolve(path));
            Assert.Equal(path, guard.ResolveWriteTarget(path));
        }
        Assert.False(File.Exists(missingLeaf));
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "new")));
        Error("path_unavailable", () => guard.OpenRead(missingLeaf));
        Error("path_unavailable", () => guard.OpenRead(folder.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    [InlineData(".")]
    [InlineData("C:relative")]
    [InlineData("C:")]
    [InlineData("\\relative")]
    [InlineData("/relative")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\workspace")]
    [InlineData("\\\\.\\C:\\workspace")]
    [InlineData("C:/workspace/../workspace")]
    [InlineData("C:/workspace.")]
    [InlineData("C:/workspace ")]
    [InlineData("C:/NUL.txt")]
    [InlineData("C:/workspace:stream")]
    [InlineData("é:/workspace")]
    public void InvalidRootsAreNeverResolvedAgainstTheProcessCurrentDirectory(string? root)
    {
        Error("invalid_path", () => new WorkspaceGuard(root!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("../outside.cs")]
    [InlineData("sub/../inside.cs")]
    [InlineData("sub\\..\\inside.cs")]
    [InlineData("sub/.. /inside.cs")]
    [InlineData("sub/.../inside.cs")]
    [InlineData("code.cs ")]
    [InlineData("code.cs.")]
    [InlineData("directory.\\code.cs")]
    [InlineData("directory \\code.cs")]
    [InlineData("C:code.cs")]
    [InlineData("C:")]
    [InlineData("\\code.cs")]
    [InlineData("/code.cs")]
    [InlineData("\\\\server\\share\\code.cs")]
    [InlineData("//server/share/code.cs")]
    [InlineData("\\\\?\\C:\\code.cs")]
    [InlineData("\\\\?\\UNC\\server\\share\\code.cs")]
    [InlineData("\\\\.\\NUL")]
    [InlineData("\\??\\C:\\code.cs")]
    [InlineData("code.cs:secret")]
    [InlineData("code.cs::$DATA")]
    [InlineData("sub:stream/code.cs")]
    [InlineData("code\0.cs")]
    [InlineData("code\n.cs")]
    [InlineData("code\u007f.cs")]
    [InlineData("code\u0085.cs")]
    [InlineData("*.cs")]
    [InlineData("code?.cs")]
    [InlineData("code\".cs")]
    [InlineData("code<.cs")]
    [InlineData("code>.cs")]
    [InlineData("code|.cs")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("CON .txt")]
    [InlineData("PRN.cs")]
    [InlineData("AUX")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("COM9.cs")]
    [InlineData("LPT1")]
    [InlineData("LPT9.cs")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT².txt")]
    [InlineData("COM³")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$.txt")]
    [InlineData("CLOCK$")]
    [InlineData("sub/NUL/child.cs")]
    public void DangerousSyntaxIsRejectedForReadAndWriteEvenWhenItWouldNormalizeInside(string? path)
    {
        using var folder = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        Error("invalid_path", () => guard.Resolve(path!));
        Error("invalid_path", () => guard.ResolveWriteTarget(path!));
        Error("invalid_path", () => guard.OpenRead(path!));
    }

    [Fact]
    public void AbsoluteTraversalAndUnsafeOutsideWriteSyntaxAreStillRejected()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        foreach (var path in new[] { folder.Path + "\\sub\\..\\code.cs", outside.Path + "\\sub\\..\\code.cs", outside.Path + "\\code.cs:secret" })
        {
            Error("invalid_path", () => guard.Resolve(path));
            Error("invalid_path", () => guard.ResolveWriteTarget(path));
        }
    }

    [Fact]
    public void InvalidUnicodeAndOverlongInputsAreControlled()
    {
        using var folder = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        var high = new string((char)0xd800, 1);
        var low = new string((char)0xdfff, 1);
        foreach (var path in new[] { high, low, high + "x" + low, new string('x', 32768) })
        {
            Error("invalid_path", () => guard.Resolve(path));
            Error("invalid_path", () => guard.ResolveWriteTarget(path));
            Error("invalid_path", () => new WorkspaceGuard(folder.Path + "\\" + path));
        }
        var validUnicodePath = Path.Combine(folder.Path, "漢字" + high + low + ".cs");
        Assert.Equal(validUnicodePath, guard.Resolve(validUnicodePath));
    }

    [Fact]
    public void BoundaryUsesRootPlusSeparatorNotSiblingPrefixAndWriteDoesNotAuthorizeAnything()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        Assert.True(guard.Contains(folder.Path));
        Assert.True(guard.Contains(folder.Path.ToUpperInvariant()));
        Assert.True(guard.Contains(folder.Path + "\\code.cs"));
        Assert.False(guard.Contains(null!));
        Assert.False(guard.Contains(""));
        foreach (var path in new[] { folder.Path + "-sibling\\code.cs", Path.Combine(outside.Path, "code.cs") })
        {
            Assert.False(guard.Contains(path));
            Error("outside_workspace", () => guard.Resolve(path));
            Error("outside_workspace", () => guard.OpenRead(path));
            Assert.Equal(path, guard.ResolveWriteTarget(path));
            Assert.False(File.Exists(path));
        }
        Error("outside_workspace", () => guard.Resolve("Z:\\code.cs"));
        Assert.Equal(outside.Path, guard.ResolveWriteTarget(outside.Path));
    }

    [Fact]
    public void VolumeRootPreservesItsSeparatorAndBoundary()
    {
        using var folder = new Folder();
        var volume = Path.GetPathRoot(folder.Path)!;
        var guard = new WorkspaceGuard(volume);
        Assert.Equal(volume, guard.Root);
        Assert.Equal(volume, guard.Resolve(volume));
        Assert.True(guard.Contains(folder.Path));
        Assert.False(guard.Contains(volume.Substring(0, 2) + "relative"));
        Assert.False(guard.Contains("\\\\server\\share\\code.cs"));
    }

    [Fact]
    public void RootMustExistAndBeADirectoryAndIsRecheckedForEveryOperation()
    {
        using var folder = new Folder();
        var missing = Path.Combine(folder.Path, "missing");
        var file = Path.Combine(folder.Path, "file");
        File.WriteAllText(file, "text");
        Error("path_unavailable", () => new WorkspaceGuard(missing));
        Error("path_unavailable", () => new WorkspaceGuard(file));
        Error("path_unavailable", () => new WorkspaceGuard(Path.Combine(file, "missing", "child")));
        var guard = new WorkspaceGuard(folder.Path);
        File.Delete(file);
        Directory.Delete(folder.Path);
        Error("path_unavailable", () => guard.Resolve("new.cs"));
        Error("path_unavailable", () => guard.ResolveWriteTarget("new.cs"));
        Error("path_unavailable", () => guard.OpenRead("new.cs"));
    }

    [Fact]
    public void FileAncestorsAreUnavailableIncludingMissingDescendantsOutsideTheWorkspace()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        foreach (var root in new[] { folder.Path, outside.Path })
        {
            var file = Path.Combine(root, "file");
            File.WriteAllText(file, "text");
            Assert.Equal(file, guard.ResolveWriteTarget(file));
            foreach (var path in new[] { Path.Combine(file, "child.cs"), Path.Combine(file, "missing", "child.cs") })
            {
                Error("path_unavailable", () => guard.ResolveWriteTarget(path));
                Error(root == folder.Path ? "path_unavailable" : "outside_workspace", () => guard.Resolve(path));
            }
        }
    }

    [Fact]
    public void ActualOpenReadValidatesFinalPathWithoutReadingAndUsesReadOnlySharing()
    {
        using var folder = new Folder();
        var file = Path.Combine(folder.Path, "code.cs");
        var bytes = Encoding.UTF8.GetBytes("text 漢字\r\n");
        File.WriteAllBytes(file, bytes);
        var guard = new WorkspaceGuard(folder.Path);
        using (var stream = guard.OpenRead("code.cs"))
        {
            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.Equal(0L, stream.Position);
            Assert.Equal(bytes.Length, stream.Length);
            using var otherReader = guard.OpenRead(file);
            Assert.Throws<IOException>(() => { using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
            var read = new byte[bytes.Length];
            Assert.Equal(bytes.Length, stream.Read(read, 0, read.Length));
            Assert.Equal(bytes, read);
        }
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        output.WriteLine("Real OpenRead/GetFinalPathNameByHandleW positive control exercised; no eager content read.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualFinalHandleValidationRejectsWrongIdentityAndClosesTheHandle(bool outsideWorkspace)
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var intended = Path.Combine(folder.Path, "intended.cs");
        var actual = Path.Combine(outsideWorkspace ? outside.Path : folder.Path, "actual.cs");
        File.WriteAllText(intended, "intended");
        File.WriteAllText(actual, "wrong object");
        var guard = new WorkspaceGuard(folder.Path);
        using var stream = new FileStream(actual, FileMode.Open, FileAccess.Read, FileShare.Read);
        var handle = stream.SafeFileHandle;
        // A real mismatched handle deterministically exercises the post-open race defense without a timing-dependent race or production hook.
        var validator = typeof(WorkspaceGuard).GetMethod("ValidateReadHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var exception = Assert.Throws<TargetInvocationException>(() => validator.Invoke(guard, new object[] { stream, intended }));
        Assert.Equal("outside_workspace", Assert.IsType<ToolException>(exception.InnerException).Code);
        Assert.True(handle.IsClosed);
        using var exclusive = new FileStream(actual, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        output.WriteLine("Real final-handle negative control exercised ({0}); validation failure disposed the stream.", outsideWorkspace ? "outside root" : "wrong in-root identity");
    }

    [Fact]
    public void LockedReadFailuresAreControlledAndDoNotLeakHandles()
    {
        using var folder = new Folder();
        var file = Path.Combine(folder.Path, "code.cs");
        File.WriteAllText(file, "text");
        var guard = new WorkspaceGuard(folder.Path);
        using (var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Error("path_unavailable", () => guard.OpenRead(file));
        using var stream = guard.OpenRead(file);
        Assert.Equal(0L, stream.Position);
    }

    [Fact]
    public void ReparseRootAncestorsAndMissingDescendantsAreRejectedForReadAndWrite()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var target = Path.Combine(outside.Path, "code.cs");
        File.WriteAllText(target, "outside text");
        Directory.CreateDirectory(Path.Combine(outside.Path, "existing"));
        var link = Path.Combine(folder.Path, "link");
        CreateDirectoryLink(link, outside.Path);
        try
        {
            var guard = new WorkspaceGuard(folder.Path);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            foreach (var path in new[] { link, Path.Combine(link, "code.cs"), Path.Combine(link, "missing", "child.cs") })
            {
                Error("outside_workspace", () => guard.Resolve(path));
                Error("outside_workspace", () => guard.ResolveWriteTarget(path));
                Error("outside_workspace", () => guard.OpenRead(path));
            }
            Error("outside_workspace", () => new WorkspaceGuard(link));
            Error("outside_workspace", () => new WorkspaceGuard(Path.Combine(link, "missing")));
            Error("outside_workspace", () => new WorkspaceGuard(Path.Combine(link, "existing")));
            Assert.Equal("outside text", File.ReadAllText(target));
            Assert.False(Directory.Exists(Path.Combine(outside.Path, "missing")));
            output.WriteLine("Real directory junction/symlink root/ancestor/missing-descendant negative controls exercised.");
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ReparsePointsAreRejectedEvenForInRootTargetsAndOutsideWriteTargets()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        foreach (var root in new[] { folder.Path, outside.Path })
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            var link = Path.Combine(root, "link");
            CreateDirectoryLink(link, target);
            try
            {
                Error("outside_workspace", () => guard.ResolveWriteTarget(Path.Combine(link, "missing", "code.cs")));
                if (root == folder.Path) Error("outside_workspace", () => guard.Resolve(link));
            }
            finally { Directory.Delete(link); }
        }
        output.WriteLine("Real in-root and outside-write reparse negative controls exercised.");
    }

    [Fact]
    public void RootReplacedWithAJunctionAfterConstructionFailsClosed()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var guard = new WorkspaceGuard(folder.Path);
        Directory.Delete(folder.Path);
        CreateDirectoryLink(folder.Path, outside.Path);
        try
        {
            Error("outside_workspace", () => guard.Resolve("missing.cs"));
            Error("outside_workspace", () => guard.ResolveWriteTarget(Path.Combine(outside.Path, "missing.cs")));
            Error("outside_workspace", () => guard.OpenRead("missing.cs"));
        }
        finally { Directory.Delete(folder.Path); }
    }

    [Fact]
    public void FileSymlinksIncludingDanglingLinksAreRejectedWhenCreationIsAvailable()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var target = Path.Combine(outside.Path, "code.cs");
        File.WriteAllText(target, "outside text");
        var link = Path.Combine(folder.Path, "code.cs");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException || exception is PlatformNotSupportedException)
        {
            output.WriteLine("GAP: file symlink creation unavailable; live/dangling file reparse negative controls not exercised.");
            return;
        }
        try
        {
            var guard = new WorkspaceGuard(folder.Path);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            foreach (var path in new[] { link, Path.Combine(link, "missing", "child.cs") })
            {
                Error("outside_workspace", () => guard.Resolve(path));
                Error("outside_workspace", () => guard.ResolveWriteTarget(path));
                Error("outside_workspace", () => guard.OpenRead(path));
            }
            Assert.Equal("outside text", File.ReadAllText(target));
            File.Delete(target);
            Error("outside_workspace", () => guard.Resolve(link));
            Error("outside_workspace", () => guard.ResolveWriteTarget(link));
            Error("outside_workspace", () => guard.OpenRead(link));
            output.WriteLine("Real live and dangling file symlink negative controls exercised.");
        }
        finally { File.Delete(link); }
    }

    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<ToolException>(action).Code);

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.WorkspaceGuardTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
