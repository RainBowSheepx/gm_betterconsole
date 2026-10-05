using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BetterConsole.Core.Pty;

/// <summary>
/// Runs a console program inside a Windows pseudo console (ConPTY): the program gets a real console
/// (so srcds' console input works), but nothing is shown on screen; its output arrives as a VT
/// stream on <see cref="OutputReceived"/> and input is written with <see cref="WriteInput"/>.
/// </summary>
public sealed class PseudoConsoleProcess : IDisposable
{
    private IntPtr _hpc;
    private IntPtr _hProcess;
    private FileStream? _input;
    private FileStream? _output;
    private Thread? _reader;
    private Thread? _waiter;
    private readonly object _writeLock = new();
    private int _disposed;

    /// <summary>Raw output bytes (UTF-8 VT stream). Raised on a background thread; the buffer is reused.</summary>
    public event Action<byte[], int>? OutputReceived;

    /// <summary>The process ended. Raised on a background thread with the exit code.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }
    public IntPtr ProcessHandle => _hProcess;
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }

    /// <param name="exePath">Full path of a console-subsystem executable.</param>
    /// <param name="arguments">Command line arguments (already quoted).</param>
    /// <param name="workingDirectory">Working directory of the process.</param>
    /// <param name="environment">Extra environment variables for the child.</param>
    /// <param name="width">Console width in cells. Keep it wide so long lines are not wrapped.</param>
    /// <param name="height">Console height in rows.</param>
    /// <param name="codePage">Console code page to set before the program starts (0 = leave the default).</param>
    public static PseudoConsoleProcess Start(string exePath, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null, short width = 4096, short height = 30, int codePage = 437)
    {
        var p = new PseudoConsoleProcess();
        try
        {
            p.StartCore(exePath, arguments, workingDirectory, environment, width, height, codePage);
            return p;
        }
        catch
        {
            p.Dispose();
            throw;
        }
    }

    private void StartCore(string exePath, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, short width, short height, int codePage)
    {
        if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        int hr = Native.CreatePseudoConsole(new Native.COORD { X = width, Y = height }, inRead, outWrite, 0, out _hpc);
        // The pseudo console duplicated the ends it needs.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed (Windows 10 1809 or newer is required)");

        _input = new FileStream(inWrite, FileAccess.Write, 1, false);
        _output = new FileStream(outRead, FileAccess.Read, 1, false);

        // The output pipe must be drained from the very first byte: conhost blocks the program when
        // the pipe is full.
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty output" };
        _reader.Start();

        IntPtr attrSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        IntPtr attrList = Marshal.AllocHGlobal(attrSize);
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            // Without this the child inherits our own (possibly redirected) standard handles and
            // writes past the pseudo console.
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.lpAttributeList = attrList;

            if (codePage > 0)
            {
                // The code page belongs to the console, not to a process: set it with a short-lived
                // helper in the same pseudo console, then start the real program there.
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var helper = $"\"{Path.Combine(sys, "cmd.exe")}\" /d /c chcp {codePage} >nul";
                if (Native.CreateProcessW(null, (helper + '\0').ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
                        Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, workingDirectory, ref si, out var hpi))
                {
                    Native.WaitForSingleObject(hpi.hProcess, 5000);
                    Native.CloseHandle(hpi.hThread);
                    Native.CloseHandle(hpi.hProcess);
                }
            }

            envBlock = BuildEnvironment(environment);
            string cmdLine = $"\"{exePath}\" {arguments}";
            if (!Native.CreateProcessW(null, (cmdLine + '\0').ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
                    Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT, envBlock, workingDirectory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {exePath}");

            Native.CloseHandle(pi.hThread);
            _hProcess = pi.hProcess;
            ProcessId = pi.dwProcessId;
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }

        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "pty process wait" };
        _waiter.Start();
    }

    private static IntPtr BuildEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string?)e.Value ?? "";
        if (extra != null)
            foreach (var kv in extra) vars[kv.Key] = kv.Value;
        var sb = new StringBuilder();
        foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private static readonly byte[] Win32InputModeOn = "\x1b[?9001h"u8.ToArray();

    /// <summary>
    /// conhost asked for win32-input-mode (ESC [ ? 9001 h). Then input is sent as explicit key
    /// events instead of plain text: plain text is turned into key presses through the user's
    /// current keyboard layout, and characters that layout has no key for ({ } ' on a Russian
    /// layout) never reach the program.
    /// </summary>
    public bool Win32InputMode { get; private set; }

    private void ReadLoop()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = _output!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                if (!Win32InputMode && buf.AsSpan(0, n).IndexOf(Win32InputModeOn) >= 0) Win32InputMode = true;
                OutputReceived?.Invoke(buf, n);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitLoop()
    {
        Native.WaitForSingleObject(_hProcess, Native.INFINITE);
        Native.GetExitCodeProcess(_hProcess, out uint code);
        ExitCode = unchecked((int)code);
        HasExited = true;
        // Give the reader a moment to deliver the last lines before the console goes away.
        Thread.Sleep(150);
        Exited?.Invoke(ExitCode);
    }

    /// <summary>Writes text to the console input as if it was typed. Use "\r" for Enter.</summary>
    public void WriteInput(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(Win32InputMode ? EncodeKeys(text) : text);
        lock (_writeLock)
        {
            if (_input == null) return;
            try
            {
                _input.Write(bytes, 0, bytes.Length);
                _input.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// win32-input-mode: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _ per key event (down and up). Characters
    /// carry no virtual key (0), so no keyboard layout is involved; Enter is VK_RETURN.
    /// </summary>
    internal static string EncodeKeys(string text)
    {
        var sb = new StringBuilder(text.Length * 24);
        foreach (char c in text)
        {
            int vk = c switch { '\r' => 0x0D, '\b' => 0x08, '\t' => 0x09, '\x1b' => 0x1B, _ => 0 };
            int code = c;
            sb.Append("\x1b[").Append(vk).Append(";0;").Append(code).Append(";1;0;1_");
            sb.Append("\x1b[").Append(vk).Append(";0;").Append(code).Append(";0;0;1_");
        }
        return sb.ToString();
    }

    /// <summary>Total CPU time of the process (user + kernel), in 100 ns units, or -1.</summary>
    public long GetCpuTime()
    {
        if (_hProcess == IntPtr.Zero) return -1;
        return Native.GetProcessTimes(_hProcess, out _, out _, out long k, out long u) ? k + u : -1;
    }

    public void Kill()
    {
        if (_hProcess != IntPtr.Zero && !HasExited) Native.TerminateProcess(_hProcess, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_writeLock)
        {
            _input?.Dispose();
            _input = null;
        }
        var hpc = _hpc;
        _hpc = IntPtr.Zero;
        if (hpc != IntPtr.Zero)
        {
            // ClosePseudoConsole can block until the output pipe is drained; the reader keeps
            // draining it, so do it off the caller's thread.
            var closer = new Thread(() => Native.ClosePseudoConsole(hpc)) { IsBackground = true, Name = "pty close" };
            closer.Start();
            closer.Join(3000);
        }
        _reader?.Join(1000);
        _output?.Dispose();
        if (_hProcess != IntPtr.Zero && HasExited)
        {
            Native.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
    }
}
