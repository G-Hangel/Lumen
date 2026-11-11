using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumen.Tools;

internal sealed class WindowsJobProcess : IDisposable
{
    private IntPtr job;
    private IntPtr process;
    internal StreamReader StandardOutput { get; private set; } = null!;
    internal StreamReader StandardError { get; private set; } = null!;

    internal static WindowsJobProcess Start(ProcessStartInfo info, Func<IntPtr, IntPtr, bool>? assignProcess = null)
    {
        var result = new WindowsJobProcess();
        var handles = new IntPtr[5];
        var attributes = IntPtr.Zero;
        var attributesInitialized = false;
        var handleList = IntPtr.Zero;
        var thread = IntPtr.Zero;
        try
        {
            result.job = CreateJobObjectW(IntPtr.Zero, null);
            Check(result.job != IntPtr.Zero);
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            Check(SetInformationJobObject(result.job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()));
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Check(CreatePipe(out handles[0], out handles[1], ref security, 0));
            Check(SetHandleInformation(handles[0], 1, 0));
            Check(CreatePipe(out handles[2], out handles[3], ref security, 0));
            Check(SetHandleInformation(handles[2], 1, 0));
            handles[4] = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
            Check(handles[4] != new IntPtr(-1));

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handleList, 0, handles[1]);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, handles[3]);
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, handles[4]);
            // Only these pipes are inherited, never another invocation's job or pipe ends.
            Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handleList,
                new IntPtr(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StandardInput = handles[4], StandardOutput = handles[1], StandardError = handles[3]
                },
                Attributes = attributes
            };
            var executable = ResolveExecutable(info.FileName);
            var commandLine = new StringBuilder("\"" + executable + "\" " + info.Arguments);
            Check(CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x08000000 | 0x00080000 | 0x00000004, IntPtr.Zero, info.WorkingDirectory, ref startup, out var created));
            result.process = created.Process;
            thread = created.Thread;
            // No user code runs before containment; assignment failure cannot spawn children.
            Check((assignProcess ?? AssignProcessToJobObject)(result.job, result.process));
            result.StandardOutput = CreateReader(ref handles[0]);
            result.StandardError = CreateReader(ref handles[2]);
            Check(ResumeThread(thread) != uint.MaxValue);
            return result;
        }
        catch
        {
            if (result.process != IntPtr.Zero) TerminateProcess(result.process, 1);
            result.Dispose();
            throw;
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            foreach (var handle in handles)
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
            if (attributes != IntPtr.Zero)
            {
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
        }
    }

    internal bool WaitForExit(int milliseconds)
    {
        var status = WaitForSingleObject(process, (uint)milliseconds);
        if (status == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return status == 0;
    }

    internal int ExitCode
    {
        get { Check(GetExitCodeProcess(process, out var code)); return unchecked((int)code); }
    }

    internal void KillTree()
    {
        if (job == IntPtr.Zero) return;
        CloseHandle(job);
        job = IntPtr.Zero;
    }

    public void Dispose()
    {
        KillTree();
        StandardOutput?.Dispose();
        StandardError?.Dispose();
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
    }

    private static StreamReader CreateReader(ref IntPtr handle)
    {
        var safeHandle = new SafeFileHandle(handle, true);
        handle = IntPtr.Zero;
        try { return new StreamReader(new FileStream(safeHandle, FileAccess.Read, 4096, false), new UTF8Encoding(false)); }
        catch { safeHandle.Dispose(); throw; }
    }

    internal static string? FindExecutable(string name)
    {
        var path = new StringBuilder(32768);
        var length = SearchPathW(null, name, ".exe", path.Capacity, path, IntPtr.Zero);
        return length != 0 && length < path.Capacity ? path.ToString() : null;
    }

    private static string ResolveExecutable(string name)
    {
        if (Path.IsPathRooted(name)) return Path.GetFullPath(name);
        var path = new StringBuilder(32768);
        var length = SearchPathW(null, name, ".exe", path.Capacity, path, IntPtr.Zero);
        Check(length != 0 && length < path.Capacity);
        return path.ToString();
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint SearchPathW(string? path, string name, string extension, int length, StringBuilder buffer, IntPtr filePart);
}
