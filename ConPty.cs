using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CcxShell.Core;

internal static class Native
{
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}

/// <summary>
/// A single child process running under a Windows pseudoconsole.
/// Raises <see cref="OutputReceived"/> with raw bytes — never decoded here, because a
/// read can split a UTF-8 sequence or an escape sequence in half. xterm.js reassembles.
/// </summary>
public sealed class PtyProcess : IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _attrList = IntPtr.Zero;
    private Native.PROCESS_INFORMATION _pi;
    private FileStream? _stdin;
    private FileStream? _stdout;
    private Thread? _readThread;
    private volatile bool _disposed;

    /// <summary>Raw bytes from the child. Fired on a background thread.</summary>
    public event Action<byte[]>? OutputReceived;

    /// <summary>Fired once when the child exits or the output pipe closes.</summary>
    public event Action? Exited;

    public void Start(string commandLine, string workingDirectory, short cols, short rows)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        // Two anonymous pipes. Naming is from *our* point of view:
        //   inputWrite  -> we write keystrokes here
        //   outputRead  -> we read the child's screen output here
        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");
        if (!Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");

        var size = new Native.COORD { X = cols, Y = rows };
        int hr = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

        // ConPTY duplicates the handles it was given. Our copies must go, or the
        // read loop will never see EOF when the child exits.
        Native.CloseHandle(inputRead);
        Native.CloseHandle(outputWrite);

        var si = new Native.STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();

        // Size probe: first call is expected to fail with ERROR_INSUFFICIENT_BUFFER.
        var listSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        _attrList = Marshal.AllocHGlobal(listSize);
        if (!Native.InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

        // For PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE the HPCON is passed *as* lpValue,
        // not as a pointer to it.
        if (!Native.UpdateProcThreadAttribute(
                _attrList, 0,
                (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

        si.lpAttributeList = _attrList;

        // CreateProcessW mutates the command line buffer, so hand it a private copy.
        var cmd = new string(commandLine.ToCharArray());

        if (!Native.CreateProcessW(
                null, cmd, IntPtr.Zero, IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: Directory.Exists(workingDirectory) ? workingDirectory : null,
                ref si, out _pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for: {commandLine}");

        _stdin = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, bufferSize: 1, isAsync: false);
        _stdout = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, bufferSize: 1, isAsync: false);

        // Anonymous pipes aren't opened for overlapped I/O, so ReadAsync would just
        // park a thread-pool thread anyway. Use a dedicated one and be honest about it.
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ConPTY read"
        };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_disposed)
            {
                int read = _stdout!.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                OutputReceived?.Invoke(chunk);
            }
        }
        catch (IOException) { /* pipe closed under us — normal on exit */ }
        catch (ObjectDisposedException) { /* same */ }
        finally
        {
            if (!_disposed) Exited?.Invoke();
        }
    }

    public void Write(byte[] data)
    {
        if (_disposed || _stdin is null) return;
        try
        {
            _stdin.Write(data, 0, data.Length);
            _stdin.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Write(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        if (cols < 1 || rows < 1) return;
        Native.ResizePseudoConsole(_hPC, new Native.COORD { X = cols, Y = rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_pi.hProcess != IntPtr.Zero)
                Native.TerminateProcess(_pi.hProcess, 0);
        }
        catch { }

        // Order matters. ClosePseudoConsole can block while the client is still
        // attached, so kill first, close the console, then tear down our streams.
        if (_hPC != IntPtr.Zero)
        {
            Native.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        try { _stdin?.Dispose(); } catch { }
        try { _stdout?.Dispose(); } catch { }

        if (_attrList != IntPtr.Zero)
        {
            Native.DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }

        if (_pi.hThread != IntPtr.Zero) Native.CloseHandle(_pi.hThread);
        if (_pi.hProcess != IntPtr.Zero) Native.CloseHandle(_pi.hProcess);
        _pi = default;
    }
}
