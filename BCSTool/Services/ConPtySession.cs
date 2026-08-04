using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BCSTool.Services;

/// <summary>
/// Owns one Windows ConPTY (Pseudo Console) session.
///
/// ConPTY is the Windows API used by modern terminal applications to host a
/// console program without creating a separate visible console window.
///
/// Instead of normal redirected stdout:
///
///     Server -> plain line stream
///
/// ConPTY gives BCS Tool the terminal stream:
///
///     Server <-> ConPTY <-> BCS Tool
///
/// The terminal stream contains normal text plus VT/ANSI control sequences
/// for cursor movement, screen clearing, box drawing, and other terminal UI
/// behavior. VirtualTerminalScreen consumes that stream and reconstructs the
/// two-dimensional screen.
///
/// Windows requirement:
///     Windows 10 version 1809 or newer.
/// </summary>
internal sealed class ConPtySession : IDisposable
{
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE from the Windows SDK.
    private static readonly IntPtr ProcThreadAttributePseudoConsole =
        new(0x00020016);

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private IntPtr _pseudoConsole;

    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;

    public StreamWriter InputWriter { get; }
    public StreamReader OutputReader { get; }
    public Process Process { get; }


    private ConPtySession(
        IntPtr pseudoConsole,
        FileStream inputStream,
        FileStream outputStream,
        Process process)
    {
        _pseudoConsole = pseudoConsole;
        _inputStream = inputStream;
        _outputStream = outputStream;
        Process = process;

        // ConPTY expects UTF-8 terminal input/output.
        InputWriter = new StreamWriter(
            _inputStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r"
        };

        OutputReader = new StreamReader(
            _outputStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
    }


    /// <summary>
    /// Creates the pipes, pseudo console, and child Bannerlord process.
    /// </summary>
    public static ConPtySession Start(
        string executablePath,
        string workingDirectory,
        short columns,
        short rows)
    {
        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;

        try
        {
            var securityAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = true
            };

            // Host writes commands to inputWrite.
            // ConPTY reads those commands from inputRead.
            if (!CreatePipe(
                    out inputRead,
                    out inputWrite,
                    ref securityAttributes,
                    0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create ConPTY input pipe.");
            }

            // ConPTY writes terminal output to outputWrite.
            // Host reads terminal output from outputRead.
            if (!CreatePipe(
                    out outputRead,
                    out outputWrite,
                    ref securityAttributes,
                    0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create ConPTY output pipe.");
            }

            var size = new COORD
            {
                X = columns,
                Y = rows
            };

            var hr = CreatePseudoConsole(
                size,
                inputRead,
                outputWrite,
                0,
                out pseudoConsole);

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            // CreatePseudoConsole has accepted the ConPTY-side pipe handles.
            // The host only needs inputWrite and outputRead from this point.
            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;

            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;


            // ------------------------------------------------
            // STARTUPINFOEX ATTRIBUTE LIST
            // ------------------------------------------------
            //
            // A normal CreateProcess call cannot attach a child to ConPTY.
            // STARTUPINFOEX carries the pseudo-console handle as a process
            // creation attribute.
            //
            IntPtr attributeListSize = IntPtr.Zero;

            InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeListSize);

            attributeList = Marshal.AllocHGlobal(
                attributeListSize);

            if (!InitializeProcThreadAttributeList(
                    attributeList,
                    1,
                    0,
                    ref attributeListSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not initialize ConPTY process attributes.");
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not attach ConPTY process attribute.");
            }

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>()
                },
                lpAttributeList = attributeList
            };

            // CreateProcessW requires a mutable command-line buffer.
            // Quoting protects executable paths that contain spaces.
            var commandLine = new StringBuilder(
                $"\"{executablePath}\"");

            var flags =
                ExtendedStartupInfoPresent |
                CreateUnicodeEnvironment;

            if (!CreateProcessW(
                    lpApplicationName: executablePath,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: flags,
                    lpEnvironment: IntPtr.Zero,
                    lpCurrentDirectory: workingDirectory,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out var processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not start Bannerlord inside ConPTY.");
            }

            // Process.GetProcessById creates the managed Process wrapper used
            // by the rest of BCS Tool.
            var process =
                Process.GetProcessById(
                    unchecked((int)processInformation.dwProcessId));

            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);

            // Transfer ownership of the host-side pipe handles to SafeHandles
            // and FileStreams. From here, FileStream.Dispose closes them.
            var inputSafeHandle =
                new SafeFileHandle(
                    inputWrite,
                    ownsHandle: true);

            inputWrite = IntPtr.Zero;

            var outputSafeHandle =
                new SafeFileHandle(
                    outputRead,
                    ownsHandle: true);

            outputRead = IntPtr.Zero;

            // Anonymous pipes created by CreatePipe are synchronous handles.
            // The server-output reader therefore runs on a dedicated
            // background Task in ServerProcessManager.
            var inputStream = new FileStream(
                inputSafeHandle,
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false);

            var outputStream = new FileStream(
                outputSafeHandle,
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);

            return new ConPtySession(
                pseudoConsole,
                inputStream,
                outputStream,
                process);
        }
        catch
        {
            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
            }

            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (inputRead != IntPtr.Zero)
                CloseHandle(inputRead);

            if (inputWrite != IntPtr.Zero)
                CloseHandle(inputWrite);

            if (outputRead != IntPtr.Zero)
                CloseHandle(outputRead);

            if (outputWrite != IntPtr.Zero)
                CloseHandle(outputWrite);
        }
    }



    /// <summary>
    /// Changes the live Windows pseudo-console dimensions.
    ///
    /// ConPTY forwards this resize to the hosted console application, so
    /// Bannerlord can redraw its native terminal UI for the new number of
    /// columns and rows.
    /// </summary>
    public void Resize(
        short columns,
        short rows)
    {
        if (_pseudoConsole == IntPtr.Zero)
            return;

        var size = new COORD
        {
            X = columns,
            Y = rows
        };

        var hr =
            ResizePseudoConsole(
                _pseudoConsole,
                size);

        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }


    public void Dispose()
    {
        try
        {
            InputWriter.Dispose();
        }
        catch
        {
        }

        try
        {
            OutputReader.Dispose();
        }
        catch
        {
        }

        try
        {
            _inputStream.Dispose();
        }
        catch
        {
        }

        try
        {
            _outputStream.Dispose();
        }
        catch
        {
        }

        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
    }


    // ========================================================
    // WIN32 STRUCTURES
    // ========================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
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
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }


    // ========================================================
    // WIN32 API
    // ========================================================

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern int ResizePseudoConsole(
        IntPtr hPC,
        COORD size);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern void ClosePseudoConsole(
        IntPtr hPC);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(
        IntPtr lpAttributeList);


    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(
        IntPtr hObject);
}
