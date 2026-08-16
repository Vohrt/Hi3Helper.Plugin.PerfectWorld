using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management;

/// <summary>
///     Drives the vendor Qt launcher's "开始游戏" (Start Game) button programmatically by injecting the tiny
///     <c>PwAutoClick.dll</c> helper into the launcher process and signalling it when the launcher is ready.
///     This is the alternative to the vendor "/autoplay" flag: it lets the launcher run its normal resource /
///     voice reconciliation first and only then presses the real button (via Qt meta-object invocation),
///     exactly like a human click.
/// </summary>
/// <remarks>
///     <para>
///         Lifecycle: <see cref="TryCreate"/> extracts the embedded DLL and creates a named ready-event →
///         <see cref="PopulateEnvironment"/> passes the config to the child through inherited environment
///         variables → the caller starts the launcher → <see cref="Inject"/> loads the DLL into it →
///         <see cref="SignalReady"/> is called once the launcher log reports it is parked at the ready state,
///         which releases the DLL to fire the click → <see cref="Dispose"/> releases the event handle.
///     </para>
///     <para>
///         Injection requires the host (Collapse) to be elevated, which it already must be to start the
///         force-elevated vendor launcher at all. If anything here fails the launch still proceeds; the caller
///         simply falls back to the "/autoplay" path or the user clicks the visible launcher manually.
///     </para>
/// </remarks>
internal sealed partial class LauncherAutoClickSession : IDisposable
{
    private const string ResourceName = "PwAutoClick.dll";

    private readonly string _dllPath;
    private readonly string _eventName;
    private readonly string _logPath;
    private readonly PerfectWorldGameConfig _config;

    private nint _eventHandle;
    private int _signalled;
    private bool _disposed;

    private LauncherAutoClickSession(string dllPath, string eventName, string logPath, nint eventHandle,
        PerfectWorldGameConfig config)
    {
        _dllPath = dllPath;
        _eventName = eventName;
        _logPath = logPath;
        _eventHandle = eventHandle;
        _config = config;
    }

    /// <summary>Diagnostic log written by the injected DLL, surfaced so callers can point the user at it.</summary>
    public string DllLogPath => _logPath;

    /// <summary>
    ///     Prepares an auto-click session: extracts the helper DLL to a per-user temp location and creates the
    ///     named manual-reset ready-event. Returns <see langword="false"/> (and no session) if the DLL cannot be
    ///     extracted or the event cannot be created, in which case the caller should fall back to the
    ///     non-auto-click launch path.
    /// </summary>
    public static bool TryCreate(PerfectWorldGameConfig config, out LauncherAutoClickSession? session)
    {
        session = null;
        try
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "CollapsePwPlugin");
            Directory.CreateDirectory(baseDir);

            var dllPath = ExtractDll(baseDir);
            if (dllPath is null) return false;

            var logPath = Path.Combine(baseDir, "PwAutoClick.log");
            var eventName = $"Local\\PwAutoClick_{Guid.NewGuid():N}";

            var handle = NativeMethods.CreateEventW(nint.Zero, manualReset: true, initialState: false, eventName);
            if (handle == nint.Zero)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[PWAutoClick] CreateEvent failed (err {Err}); auto-click disabled for this launch.",
                    Marshal.GetLastPInvokeError());
                return false;
            }

            session = new LauncherAutoClickSession(dllPath, eventName, logPath, handle, config);
            return true;
        }
        catch (Exception e)
        {
            SharedStatic.InstanceLogger.LogWarning(e, "[PWAutoClick] Could not prepare auto-click session.");
            return false;
        }
    }

    /// <summary>
    ///     Passes the auto-click configuration (context-object name, method, ready-event and log path) to the
    ///     launcher-to-be through environment variables it will inherit. Must be called on the
    ///     <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> of the not-yet-started launcher process.
    /// </summary>
    public void PopulateEnvironment(System.Collections.Generic.IDictionary<string, string?> environment)
    {
        environment["PWAC_OBJ"] = _config.LauncherAutoClickContextObjectName;
        environment["PWAC_METHOD"] = _config.LauncherAutoClickMethodName;
        environment["PWAC_EVENT"] = _eventName;
        environment["PWAC_LOG"] = _logPath;
    }

    /// <summary>
    ///     Loads <c>PwAutoClick.dll</c> into the running launcher via the classic
    ///     <c>CreateRemoteThread(LoadLibraryW)</c> technique. Returns whether the remote load thread ran to
    ///     completion. Any failure is logged and swallowed so it never breaks the launch.
    /// </summary>
    public bool Inject(int processId)
    {
        if (_disposed) return false;

        nint hProc = nint.Zero;
        nint remoteMem = nint.Zero;
        nint hThread = nint.Zero;
        var remoteThreadDone = false;
        try
        {
            hProc = NativeMethods.OpenProcess(NativeMethods.ProcessInjectAccess, false, (uint)processId);
            if (hProc == nint.Zero)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[PWAutoClick] OpenProcess({Pid}) failed (err {Err}).", processId, Marshal.GetLastPInvokeError());
                return false;
            }

            var kernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
            var loadLibrary = kernel32 == nint.Zero ? nint.Zero : NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == nint.Zero)
            {
                SharedStatic.InstanceLogger.LogWarning("[PWAutoClick] Could not resolve LoadLibraryW.");
                return false;
            }

            var pathBytes = Encoding.Unicode.GetBytes(_dllPath + '\0');
            remoteMem = NativeMethods.VirtualAllocEx(hProc, nint.Zero, (nuint)pathBytes.Length,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            if (remoteMem == nint.Zero)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[PWAutoClick] VirtualAllocEx failed (err {Err}).", Marshal.GetLastPInvokeError());
                return false;
            }

            if (!NativeMethods.WriteProcessMemory(hProc, remoteMem, pathBytes, (nuint)pathBytes.Length, out _))
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[PWAutoClick] WriteProcessMemory failed (err {Err}).", Marshal.GetLastPInvokeError());
                return false;
            }

            hThread = NativeMethods.CreateRemoteThread(hProc, nint.Zero, nuint.Zero, loadLibrary, remoteMem, 0, out _);
            if (hThread == nint.Zero)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[PWAutoClick] CreateRemoteThread failed (err {Err}).", Marshal.GetLastPInvokeError());
                return false;
            }

            var wait = NativeMethods.WaitForSingleObject(hThread, 30_000);
            uint exitCode = 0;
            if (wait == NativeMethods.WAIT_OBJECT_0)
            {
                // The remote LoadLibraryW thread has returned, so it is finished reading the path buffer.
                NativeMethods.GetExitCodeThread(hThread, out exitCode);
                remoteThreadDone = true;
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[PWAutoClick] Injected {Dll} into launcher pid {Pid} (wait={Wait}, LoadLibrary handle low32={Code:X8}). Log: {Log}",
                Path.GetFileName(_dllPath), processId, wait, exitCode, _logPath);

            // exitCode is the low 32 bits of the HMODULE; zero means LoadLibraryW failed inside the target.
            return wait == NativeMethods.WAIT_OBJECT_0 && exitCode != 0;
        }
        catch (Exception e)
        {
            SharedStatic.InstanceLogger.LogWarning(e, "[PWAutoClick] Injection threw; auto-click unavailable.");
            return false;
        }
        finally
        {
            // Release the remote path buffer only when it is certain the LoadLibraryW thread is no longer
            // reading it: either no thread was created, or the thread has finished (WAIT_OBJECT_0). On a wait
            // timeout the thread may still be running, so we intentionally leak the small page rather than risk
            // the target reading freed memory (use-after-free -> possible launcher crash).
            var remoteBufferStillInUse = hThread != nint.Zero && !remoteThreadDone;
            if (remoteMem != nint.Zero && hProc != nint.Zero && !remoteBufferStillInUse)
                NativeMethods.VirtualFreeEx(hProc, remoteMem, nuint.Zero, NativeMethods.MEM_RELEASE);
            if (hThread != nint.Zero) NativeMethods.CloseHandle(hThread);
            if (hProc != nint.Zero) NativeMethods.CloseHandle(hProc);
        }
    }

    /// <summary>
    ///     Releases the injected DLL to press the button. Idempotent — only the first call signals the event.
    ///     Safe to call from any thread.
    /// </summary>
    public void SignalReady()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _signalled, 1) != 0) return;

        if (_eventHandle != nint.Zero && NativeMethods.SetEvent(_eventHandle))
            SharedStatic.InstanceLogger.LogInformation("[PWAutoClick] Signalled launcher-ready; firing button click.");
        else
            SharedStatic.InstanceLogger.LogWarning(
                "[PWAutoClick] SetEvent failed (err {Err}).", Marshal.GetLastPInvokeError());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var handle = Interlocked.Exchange(ref _eventHandle, nint.Zero);
        if (handle != nint.Zero) NativeMethods.CloseHandle(handle);
    }

    /// <summary>
    ///     Writes the embedded helper DLL to <paramref name="baseDir"/> under a content-hashed name so distinct
    ///     builds never collide and a copy already loaded by a previous launch is never overwritten. Reuses an
    ///     existing identical file. Returns the full path, or <see langword="null"/> if the resource is missing.
    /// </summary>
    private static string? ExtractDll(string baseDir)
    {
        var assembly = typeof(LauncherAutoClickSession).Assembly;
        using var resource = assembly.GetManifestResourceStream(ResourceName);
        if (resource is null)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PWAutoClick] Embedded resource '{Res}' not found; auto-click unavailable (native DLL not built?).",
                ResourceName);
            return null;
        }

        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        var dllPath = Path.Combine(baseDir, $"PwAutoClick_{hash}.dll");

        if (!File.Exists(dllPath) || new FileInfo(dllPath).Length != bytes.Length)
        {
            var tmp = dllPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try
            {
                File.Move(tmp, dllPath, overwrite: true);
            }
            catch (IOException)
            {
                // Another launch extracted the same content first (or the target is loaded): the existing file is
                // byte-identical by construction, so just use it and drop our temp copy.
                try { File.Delete(tmp); } catch { /* best-effort */ }
                if (!File.Exists(dllPath)) throw;
            }
        }

        return dllPath;
    }

    private static partial class NativeMethods
    {
        internal const uint MEM_COMMIT = 0x1000;
        internal const uint MEM_RESERVE = 0x2000;
        internal const uint MEM_RELEASE = 0x8000;
        internal const uint PAGE_READWRITE = 0x04;
        internal const uint WAIT_OBJECT_0 = 0x0;

        // PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ
        internal const uint ProcessInjectAccess = 0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(nint handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType,
            uint protect);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WriteProcessMemory(nint process, nint baseAddress, ReadOnlySpan<byte> buffer,
            nuint size, out nuint written);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint CreateRemoteThread(nint process, nint threadAttributes, nuint stackSize,
            nint startAddress, nint parameter, uint creationFlags, out uint threadId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetExitCodeThread(nint thread, out uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint GetModuleHandleW(string moduleName);

        // GetProcAddress takes an ANSI symbol name; UTF-8 marshalling is byte-identical for the ASCII export names
        // we resolve (e.g. "LoadLibraryW").
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint GetProcAddress(nint module, string procName);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
            [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetEvent(nint handle);
    }
}
