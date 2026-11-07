using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;

namespace Lumen.Vsix;

#if NET5_0_OR_GREATER
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumBlobBytes = 2560;
    private static readonly Encoding SecretEncoding = new UnicodeEncoding(false, false, true);
    // Coordinate separate store instances so replacing a fallback cannot race a read or delete.
    private static readonly SemaphoreSlim StorageGate = new(1, 1);
    private readonly string fallbackFolderPath;
    private readonly bool forceDpapi;

    public WindowsCredentialStore(string? fallbackFolderPath = null, bool forceDpapi = false)
    {
        var folder = fallbackFolderPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen", "Credentials");
        try
        {
            if (!Path.IsPathRooted(folder))
                throw new ArgumentException();
            this.fallbackFolderPath = Path.GetFullPath(folder);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException ||
            exception is IOException || exception is SecurityException)
        {
            throw new ArgumentException("An absolute credential folder path is required.", nameof(fallbackFolderPath));
        }
        this.forceDpapi = forceDpapi;
    }

    public Task<string?> ReadAsync(string configurationId, CancellationToken cancellationToken)
    {
        var id = ValidateId(configurationId);
        return ExecuteAsync(() =>
        {
            if (forceDpapi || File.Exists(GetFallbackPath(id)))
                return ReadDpapi(id);
            if (TryReadNative("Lumen/" + id, out var secret))
                return secret;
            return ReadDpapi(id);
        }, cancellationToken);
    }

    public Task WriteAsync(string configurationId, string secret, CancellationToken cancellationToken)
    {
        var id = ValidateId(configurationId);
        ValidateSecret(secret);
        return ExecuteAsync(() =>
        {
            // Keep a previously written fallback authoritative across restarts; do not split updates between stores.
            if (forceDpapi || File.Exists(GetFallbackPath(id)) || !TryWriteNative("Lumen/" + id, secret))
                WriteDpapi(id, secret, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(string configurationId, CancellationToken cancellationToken)
    {
        var id = ValidateId(configurationId);
        return ExecuteAsync(() =>
        {
            if (!forceDpapi)
                TryDeleteNative("Lumen/" + id);
            try
            {
                File.Delete(GetFallbackPath(id));
            }
            catch (DirectoryNotFoundException)
            {
            }
            return true;
        }, cancellationToken);
    }

    private static string ValidateId(string configurationId)
    {
        if (configurationId == null)
            throw new ArgumentNullException(nameof(configurationId));
        if (configurationId.Length != 36 || !Guid.TryParseExact(configurationId, "D", out var id))
            throw new ArgumentException("A configuration id in GUID D format is required.", nameof(configurationId));
        return id.ToString("D");
    }

    private static void ValidateSecret(string secret)
    {
        if (secret == null)
            throw new ArgumentNullException(nameof(secret));
        if (secret.Length == 0 || secret.Length > MaximumBlobBytes / 2)
            throw new ArgumentException("A non-empty credential of at most 2560 UTF-16 bytes is required.", nameof(secret));
        try
        {
            SecretEncoding.GetByteCount(secret);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The credential must contain valid Unicode.", nameof(secret));
        }
    }

    private static async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                    throw new PlatformNotSupportedException("Windows credential storage requires Windows.");
                try
                {
                    return operation();
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                    exception is SecurityException || exception is CryptographicException ||
                    exception is DecoderFallbackException || exception is EncoderFallbackException ||
                    exception is NotSupportedException)
                {
                    // IO and crypto exceptions can include paths or input. Never retain their message or inner exception.
                    throw new IOException("Windows credential storage failed.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StorageGate.Release();
        }
    }

    // Access denied and malformed input must surface, not silently move credentials to another store.
    internal static bool IsNativeFallbackError(int errorCode) =>
        errorCode == 1 || errorCode == 50 || errorCode == 120 || errorCode == 1312;

    private static bool IsNativeUnavailable(Exception exception) =>
        exception is DllNotFoundException || exception is EntryPointNotFoundException ||
        exception is PlatformNotSupportedException;

    private static bool TryReadNative(string target, out string? secret)
    {
        secret = null;
        var pointer = IntPtr.Zero;
        NativeCredential credential = default;
        byte[]? bytes = null;
        try
        {
            if (!NativeMethods.CredRead(target, CredentialTypeGeneric, 0, out pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                    return true;
                if (IsNativeFallbackError(error))
                    return false;
                throw NativeFailure(error);
            }
            credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.Type != CredentialTypeGeneric || credential.CredentialBlobSize > MaximumBlobBytes ||
                credential.CredentialBlobSize % 2 != 0 ||
                (credential.CredentialBlobSize != 0 && credential.CredentialBlob == IntPtr.Zero))
                throw new IOException("The stored credential is invalid.");
            bytes = new byte[(int)credential.CredentialBlobSize];
            if (bytes.Length != 0)
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            secret = SecretEncoding.GetString(bytes);
            return true;
        }
        catch (Exception exception) when (IsNativeUnavailable(exception))
        {
            return false;
        }
        finally
        {
            Clear(bytes);
            if (pointer != IntPtr.Zero)
            {
                if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize <= MaximumBlobBytes)
                    ZeroUnmanaged(credential.CredentialBlob, (int)credential.CredentialBlobSize);
                NativeMethods.CredFree(pointer);
            }
        }
    }

    private static bool TryWriteNative(string target, string secret)
    {
        var bytes = SecretEncoding.GetBytes(secret);
        var blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine
            };
            if (NativeMethods.CredWrite(ref credential, 0))
                return true;
            var error = Marshal.GetLastWin32Error();
            if (IsNativeFallbackError(error))
                return false;
            throw NativeFailure(error);
        }
        catch (Exception exception) when (IsNativeUnavailable(exception))
        {
            return false;
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                ZeroUnmanaged(blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }
            Clear(bytes);
        }
    }

    private static bool TryDeleteNative(string target)
    {
        try
        {
            if (NativeMethods.CredDelete(target, CredentialTypeGeneric, 0))
                return true;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return true;
            if (IsNativeFallbackError(error))
                return false;
            throw NativeFailure(error);
        }
        catch (Exception exception) when (IsNativeUnavailable(exception))
        {
            return false;
        }
    }

    private static Win32Exception NativeFailure(int error) =>
        new(error, "Windows Credential Manager failed (error " + error + ").");

    private string GetFallbackPath(string id) => Path.Combine(fallbackFolderPath, id + ".dpapi");

    private string? ReadDpapi(string id)
    {
        byte[] ciphertext;
        try
        {
            ciphertext = File.ReadAllBytes(GetFallbackPath(id));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(ciphertext, Encoding.UTF8.GetBytes("Lumen/" + id), DataProtectionScope.CurrentUser);
            if (plaintext.Length > MaximumBlobBytes || plaintext.Length % 2 != 0)
                throw new IOException("The stored credential is invalid.");
            return SecretEncoding.GetString(plaintext);
        }
        finally
        {
            Clear(plaintext);
            Clear(ciphertext);
        }
    }

    private void WriteDpapi(string id, string secret, CancellationToken cancellationToken)
    {
        var plaintext = SecretEncoding.GetBytes(secret);
        byte[]? ciphertext = null;
        var path = GetFallbackPath(id);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var createdTemporaryFile = false;
        try
        {
            ciphertext = ProtectedData.Protect(plaintext, Encoding.UTF8.GetBytes("Lumen/" + id), DataProtectionScope.CurrentUser);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(fallbackFolderPath);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                createdTemporaryFile = true;
                stream.Write(ciphertext, 0, ciphertext.Length);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
            createdTemporaryFile = false;
        }
        finally
        {
            Clear(plaintext);
            Clear(ciphertext);
            if (createdTemporaryFile)
                File.Delete(temporaryPath);
        }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes != null)
            Array.Clear(bytes, 0, bytes.Length);
    }

    private static void ZeroUnmanaged(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++)
            Marshal.WriteByte(pointer, index, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern void CredFree(IntPtr credential);
    }
}
