using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;
using Lumen.Vsix;
using Xunit;

namespace Lumen.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public async Task NativeCredentialRoundTripsThroughCredReadAndDeletesOnlyItsFreshTarget()
    {
        using var folder = new OwnedFolder();
        var id = Guid.NewGuid().ToString("D");
        AssertNativeMissing(id);
        ICredentialStore store = new WindowsCredentialStore(folder.Path);
        var secret = CreateSecret() + "-ação-漢字-\0-\uD834\uDD1E";
        var replacement = CreateSecret();
        var ownsEntry = false;
        try
        {
            Assert.Null(await store.ReadAsync(id, CancellationToken.None));
            await store.WriteAsync(id, secret, CancellationToken.None);
            ownsEntry = true;
            Assert.Equal(secret, ReadNativeCredential(id));
            Assert.Equal(secret, await store.ReadAsync(id, CancellationToken.None));
            Assert.Equal(secret, await store.ReadAsync(id.ToUpperInvariant(), CancellationToken.None));
            Assert.Empty(Directory.GetFiles(folder.Path));

            await store.WriteAsync(id, replacement, CancellationToken.None);
            Assert.Equal(replacement, ReadNativeCredential(id));
            await store.DeleteAsync(id, CancellationToken.None);
            AssertNativeMissing(id);
            Assert.Null(await store.ReadAsync(id, CancellationToken.None));
            await store.DeleteAsync(id, CancellationToken.None);
        }
        finally
        {
            if (ownsEntry)
                await store.DeleteAsync(id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForcedFallbackPersistsOnlyCurrentUserDpapiCiphertextAndDeletesOnlyItsFile()
    {
        using var folder = new OwnedFolder();
        var id = Guid.NewGuid().ToString("D");
        var store = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        var secret = CreateSecret() + "-ação-漢字-\0-\uD834\uDD1E";
        var path = FallbackPath(folder.Path, id);
        Assert.False(File.Exists(path));
        AssertNativeMissing(id);
        var unrelatedPath = System.IO.Path.Combine(folder.Path, Guid.NewGuid().ToString("D") + ".dpapi");
        var unrelatedCiphertext = RandomNumberGenerator.GetBytes(128);
        File.WriteAllBytes(unrelatedPath, unrelatedCiphertext);

        Assert.Null(await store.ReadAsync(id, CancellationToken.None));
        await store.WriteAsync(id, secret, CancellationToken.None);
        Assert.Equal(secret, await store.ReadAsync(id, CancellationToken.None));
        var ciphertext = File.ReadAllBytes(path);
        AssertCiphertextDoesNotContainSecret(ciphertext, secret);
        var plaintext = ProtectedData.Unprotect(ciphertext, Encoding.UTF8.GetBytes("Lumen/" + id), DataProtectionScope.CurrentUser);
        try
        {
            Assert.Equal(secret, Encoding.Unicode.GetString(plaintext));
        }
        finally
        {
            Array.Clear(plaintext);
        }
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(
            ciphertext, Encoding.UTF8.GetBytes("Lumen/" + Guid.NewGuid().ToString("D")), DataProtectionScope.CurrentUser));
        AssertNativeMissing(id);

        await store.DeleteAsync(id, CancellationToken.None);
        Assert.False(File.Exists(path));
        Assert.Null(await store.ReadAsync(id, CancellationToken.None));
        await store.DeleteAsync(id, CancellationToken.None);
        Assert.Equal(unrelatedCiphertext, File.ReadAllBytes(unrelatedPath));
        Assert.Equal(new[] { unrelatedPath }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task PersistedFallbackRemainsReadableAndUpdatableWhenNativeStorageIsAvailableAgain()
    {
        using var folder = new OwnedFolder();
        var id = Guid.NewGuid().ToString("D");
        AssertNativeMissing(id);
        var forcedStore = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        var secret = CreateSecret();
        await forcedStore.WriteAsync(id, secret, CancellationToken.None);

        var store = new WindowsCredentialStore(folder.Path);
        Assert.Equal(secret, await store.ReadAsync(id, CancellationToken.None));
        var replacement = CreateSecret();
        await store.WriteAsync(id, replacement, CancellationToken.None);
        Assert.Equal(replacement, await forcedStore.ReadAsync(id, CancellationToken.None));
        Assert.Equal(replacement, await store.ReadAsync(id, CancellationToken.None));
        AssertNativeMissing(id);
        AssertCiphertextDoesNotContainSecret(File.ReadAllBytes(FallbackPath(folder.Path, id)), replacement);
        await store.DeleteAsync(id, CancellationToken.None);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task FailedAtomicReplacementPreservesPreviousCredentialAndRemovesTemporaryCiphertext()
    {
        using var folder = new OwnedFolder();
        var id = Guid.NewGuid().ToString("D");
        var store = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        var firstSecret = CreateSecret();
        var nextSecret = CreateSecret();
        await store.WriteAsync(id, firstSecret, CancellationToken.None);
        var path = FallbackPath(folder.Path, id);
        var originalCiphertext = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(id, nextSecret, CancellationToken.None));
            AssertSecretIsAbsent(exception, nextSecret);
        }
        Assert.Equal(originalCiphertext, File.ReadAllBytes(path));
        Assert.Equal(firstSecret, await store.ReadAsync(id, CancellationToken.None));
        Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));

        await store.WriteAsync(id, nextSecret, CancellationToken.None);
        Assert.Equal(nextSecret, await store.ReadAsync(id, CancellationToken.None));
        AssertCiphertextDoesNotContainSecret(File.ReadAllBytes(path), nextSecret);
        Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CorruptFallbackIsRejectedWithoutReturningCiphertextOrLeakingTheSecret()
    {
        using var folder = new OwnedFolder();
        var id = Guid.NewGuid().ToString("D");
        var store = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        var secret = CreateSecret();
        await store.WriteAsync(id, secret, CancellationToken.None);
        var path = FallbackPath(folder.Path, id);
        var ciphertext = File.ReadAllBytes(path);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var exception = await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(id, CancellationToken.None));
        AssertSecretIsAbsent(exception, secret);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        File.WriteAllBytes(path, ciphertext);
        Assert.Equal(secret, await store.ReadAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task FallbackCiphertextCannotBeReusedForADifferentConfigurationId()
    {
        using var folder = new OwnedFolder();
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        var secret = CreateSecret();
        var store = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        await store.WriteAsync(firstId, secret, CancellationToken.None);
        File.Copy(FallbackPath(folder.Path, firstId), FallbackPath(folder.Path, secondId));
        var exception = await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(secondId, CancellationToken.None));
        AssertSecretIsAbsent(exception, secret);
        Assert.Equal(secret, await store.ReadAsync(firstId, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../../outside")]
    [InlineData("..\\..\\outside")]
    [InlineData("Lumen/00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    [InlineData(" 00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-0000-0000-000000000000 ")]
    [InlineData("00000000-0000-0000-0000-00000000000z")]
    public async Task InvalidIdsAreRejectedBeforeAnyStorageAccess(string? id)
    {
        using var folder = new OwnedFolder();
        var unusedFolder = System.IO.Path.Combine(folder.Path, "not-created");
        var store = new WindowsCredentialStore(unusedFolder, forceDpapi: true);
        var secret = CreateSecret();
        var readError = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(id!, CancellationToken.None));
        var writeError = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(id!, secret, CancellationToken.None));
        var deleteError = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(id!, CancellationToken.None));
        foreach (var exception in new[] { readError, writeError, deleteError })
        {
            Assert.Equal("configurationId", exception.ParamName);
            AssertSecretIsAbsent(exception, secret);
        }
        Assert.False(Directory.Exists(unusedFolder));
    }

    [Fact]
    public async Task ValidationErrorsDoNotEchoSecretsPassedAsIdsOrInvalidUnicode()
    {
        using var folder = new OwnedFolder();
        var store = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        var secret = CreateSecret();
        var idError = await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(secret, secret, CancellationToken.None));
        AssertSecretIsAbsent(idError, secret);
        var invalidSecret = secret + "\uD800";
        var unicodeError = await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(
            Guid.NewGuid().ToString("D"), invalidSecret, CancellationToken.None));
        AssertSecretIsAbsent(unicodeError, secret);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task NullEmptyAndOversizedSecretsAreRejectedWithoutStorageSideEffects()
    {
        using var folder = new OwnedFolder();
        var store = new WindowsCredentialStore(folder.Path, forceDpapi: true);
        var id = Guid.NewGuid().ToString("D");
        var oversizedSecret = CreateSecret().PadRight(1281, 'x');
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.WriteAsync(id, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(id, "", CancellationToken.None));
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(id, oversizedSecret, CancellationToken.None));
        AssertSecretIsAbsent(exception, oversizedSecret);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CancellationDoesNotCreateReplaceOrDeleteCredentials()
    {
        using var folder = new OwnedFolder();
        var unusedFolder = System.IO.Path.Combine(folder.Path, "not-created");
        var id = Guid.NewGuid().ToString("D");
        var store = new WindowsCredentialStore(unusedFolder, forceDpapi: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var secret = CreateSecret();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(id, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(id, secret, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(id, cancellation.Token));
        Assert.False(Directory.Exists(unusedFolder));

        await store.WriteAsync(id, secret, CancellationToken.None);
        var originalCiphertext = File.ReadAllBytes(FallbackPath(unusedFolder, id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(id, CreateSecret(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(id, cancellation.Token));
        Assert.Equal(originalCiphertext, File.ReadAllBytes(FallbackPath(unusedFolder, id)));
        Assert.Equal(secret, await store.ReadAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task FilesystemAndPathValidationErrorsHaveNoSecretOrInnerException()
    {
        using var folder = new OwnedFolder();
        var secret = CreateSecret();
        var blockedFolder = System.IO.Path.Combine(folder.Path, Guid.NewGuid().ToString("D"));
        File.WriteAllBytes(blockedFolder, RandomNumberGenerator.GetBytes(32));
        var store = new WindowsCredentialStore(blockedFolder, forceDpapi: true);
        var exception = await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Guid.NewGuid().ToString("D"), secret, CancellationToken.None));
        AssertSecretIsAbsent(exception, secret);
        Assert.DoesNotContain(blockedFolder, exception.ToString(), StringComparison.Ordinal);

        var invalidPathError = Assert.Throws<ArgumentException>(() => new WindowsCredentialStore(
            System.IO.Path.Combine(folder.Path, secret) + "\0"));
        AssertSecretIsAbsent(invalidPathError, secret);
        var relativePathError = Assert.Throws<ArgumentException>(() => new WindowsCredentialStore(secret));
        AssertSecretIsAbsent(relativePathError, secret);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(120, true)]
    [InlineData(1312, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(87, false)]
    [InlineData(112, false)]
    [InlineData(1168, false)]
    public void NativeFallbackPolicyRejectsAccessDeniedInvalidInputAndUnrelatedFailures(int error, bool expected) =>
        Assert.Equal(expected, WindowsCredentialStore.IsNativeFallbackError(error));

    private static string CreateSecret() => "synthetic-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string FallbackPath(string folder, string id) => System.IO.Path.Combine(folder, id + ".dpapi");

    private static void AssertSecretIsAbsent(Exception exception, string secret)
    {
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private static void AssertCiphertextDoesNotContainSecret(byte[] ciphertext, string secret)
    {
        var utf16 = Encoding.Unicode.GetBytes(secret);
        var utf8 = Encoding.UTF8.GetBytes(secret);
        // The detector must reject a plaintext sample before it is used to approve ciphertext.
        Assert.True(ContainsBytes(utf16, utf16));
        Assert.True(ContainsBytes(utf8, utf8));
        Assert.True(ContainsBytes(new byte[] { 1, 2, 3, 4 }, new byte[] { 2, 3 }));
        Assert.False(ContainsBytes(new byte[] { 1, 2, 3, 4 }, new byte[] { 2, 4 }));
        Assert.False(ContainsBytes(ciphertext, utf16));
        Assert.False(ContainsBytes(ciphertext, utf8));
        Array.Clear(utf16);
        Array.Clear(utf8);
    }

    private static bool ContainsBytes(byte[] content, byte[] pattern)
    {
        for (var index = 0; index <= content.Length - pattern.Length; index++)
        {
            if (content.AsSpan(index, pattern.Length).SequenceEqual(pattern))
                return true;
        }
        return false;
    }

    private static void AssertNativeMissing(string id)
    {
        var exists = CredRead("Lumen/" + id, 1, 0, out var pointer);
        var error = Marshal.GetLastWin32Error();
        if (exists)
            CredFree(pointer);
        // Never decode, overwrite, or clean up a pre-existing entry, even on an improbable GUID collision.
        Assert.False(exists, "The newly generated credential target must not already exist.");
        Assert.Equal(1168, error);
    }

    private static string ReadNativeCredential(string id)
    {
        var success = CredRead("Lumen/" + id, 1, 0, out var pointer);
        var error = Marshal.GetLastWin32Error();
        Assert.True(success, "CredRead must succeed for the synthetic test entry (error " + error + ").");
        NativeCredential credential = default;
        byte[]? bytes = null;
        try
        {
            credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            Assert.Equal(1u, credential.Type);
            Assert.Equal(2u, credential.Persist);
            Assert.Equal("Lumen/" + id, Marshal.PtrToStringUni(credential.TargetName));
            Assert.InRange(credential.CredentialBlobSize, 1u, 2560u);
            bytes = new byte[(int)credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            if (bytes != null)
                Array.Clear(bytes);
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize <= 2560)
            {
                for (var index = 0; index < credential.CredentialBlobSize; index++)
                    Marshal.WriteByte(credential.CredentialBlob, index, 0);
            }
            CredFree(pointer);
        }
    }

    private sealed class OwnedFolder : IDisposable
    {
        internal OwnedFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lumen.CredentialStoreTests." + Guid.NewGuid().ToString("N"));
            Assert.False(Directory.Exists(Path));
            Assert.False(File.Exists(Path));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
