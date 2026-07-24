using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Wanmei.Core;
using Hi3Helper.Wanmei.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.NTE;

public partial class Exports
{
    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted,
        ProcessPriorityClass processPriority, CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetStartingProcessFromContext(context, startArgument, out var process, out var silentPlan))
                return false;

            using (process)
            {
                // Patch the vendor launcher's own settings so it auto-logs-in, auto-starts the game and quits
                // together with the game. This is what removes the manual "Start" click and stops the launcher
                // from reappearing after the game exits. It works regardless of elevation (the elevated launcher
                // reads these settings itself), so it is the primary silencing mechanism.
                if (silentPlan is not null && !string.IsNullOrEmpty(silentPlan.SettingsIniPath))
                    PatchLauncherSettings(silentPlan.SettingsIniPath);

                long launcherLogStartLength = silentPlan is not null ? GetLauncherLogLength(silentPlan.LauncherDir) : 0;

                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    InstanceLogger.LogError(e, "[NTE::LaunchGame] Failed to start the launcher/game process.");
                    return false;
                }

                try
                {
                    process.PriorityBoostEnabled = isRunBoosted;
                    process.PriorityClass = processPriority;
                }
                catch (Exception e)
                {
                    InstanceLogger.LogError(e, "[NTE::LaunchGame] Failed to set process priority.");
                }

                _ = ReadGameLog(context, token);

                if (silentPlan is not null)
                    await DriveLauncherSilentlyAsync(silentPlan, launcherLogStartLength, token);
                else
                    await process.WaitForExitAsync(token);

                return true;
            }
        }
    }

    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning, out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

        using var process = FindExecutableProcess(gameExecutablePath);
        if (process != null)
        {
            isGameRunning = true;
            gameStartTime = process.StartTime;
        }

        return true;
    }

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

            using var process = FindExecutableProcess(gameExecutablePath);
            if (process != null)
                await process.WaitForExitAsync(token);

            return true;
        }
    }

    protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning, out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

        using var process = FindExecutableProcess(gameExecutablePath);
        if (process == null) return true;

        wasGameRunning = true;
        gameStartTime = process.StartTime;
        process.Kill();
        return true;
    }

    private static Process? FindExecutableProcess(string? executablePath)
    {
        if (executablePath == null) return null;

        var executableDirPath = Path.GetDirectoryName(executablePath.AsSpan());
        var executableName = Path.GetFileNameWithoutExtension(executablePath);

        var processes = Process.GetProcessesByName(executableName);
        Process? returnProcess = null;

        foreach (var process in processes)
        {
            bool match;
            try
            {
                // MainModule of an elevated game process is not readable from a non-elevated host (the NTE game runs
                // elevated because it is spawned by the force-admin launcher). In that case fall back to a name-only
                // match so run/kill detection still works.
                match = process.MainModule?.FileName.StartsWith(executableDirPath, StringComparison.OrdinalIgnoreCase) ?? false;
            }
            catch
            {
                match = true;
            }

            if (match)
            {
                returnProcess = process;
                break;
            }
        }

        foreach (var process in processes.Where(x => x != returnProcess)) process.Dispose();

        return returnProcess;
    }

    private static bool TryGetGameExecutablePath(GameManagerExtension.RunGameFromGameManagerContext context,
        [NotNullWhen(true)] out string? gameExecutablePath)
    {
        gameExecutablePath = null;
        if (context is not
            {
                GameManager: WanmeiGameManager nteManager, PresetConfig: PluginPresetConfigBase presetConfig
            }) return false;

        nteManager.GetGamePath(out var gamePath);
        presetConfig.comGet_GameExecutableName(out var executablePath);

        gamePath?.NormalizePathInplace();
        executablePath.NormalizePathInplace();

        if (string.IsNullOrEmpty(gamePath)) return false;

        gameExecutablePath = Path.Combine(gamePath, executablePath);
        return File.Exists(gameExecutablePath);
    }

    private static bool TryGetStartingProcessFromContext(GameManagerExtension.RunGameFromGameManagerContext context,
        string? startArgument, [NotNullWhen(true)] out Process? process, out SilentLaunchPlan? silentPlan)
    {
        process = null;
        silentPlan = null;

        // Existence of the real game binary gates launch; it is also what we track for run/kill detection.
        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return false;
        if (context.GameManager is not WanmeiGameManager nteManager) return false;

        nteManager.GetGamePath(out var gamePath);
        if (string.IsNullOrEmpty(gamePath)) return false;

        // Default: launch the game binary (HTGame.exe) directly with only a user-supplied argument, if any. Its
        // working directory is its own folder, as a UE client expects.
        var startingExecutablePath = gameExecutablePath;
        var effectiveArgument = startArgument;
        var workingDirectory = Path.GetDirectoryName(gameExecutablePath);

        var config = nteManager.Config;

        // Prefer the vendor launcher (NTELauncher\NTELauncher.exe) when it is present on disk: it hosts the account
        // login UI and drives the game process (anti-cheat, pipe hand-off), so a direct HTGame.exe launch cannot log
        // in. The plugin installs the launcher alongside the game, so this is the normal path. This mirrors the
        // official "异环" shortcut exactly: launch NTELauncher\NTELauncher.exe with the working directory set to the
        // install root (NOT the launcher's own folder).
        var bootstrapperRelativePath = config.LauncherBootstrapperRelativePath;
        var usingBootstrapper = false;
        if (!string.IsNullOrEmpty(bootstrapperRelativePath))
        {
            var bootstrapperPath = Path.Combine(gamePath, bootstrapperRelativePath);
            if (File.Exists(bootstrapperPath))
            {
                usingBootstrapper = true;
                startingExecutablePath = bootstrapperPath;
                workingDirectory = gamePath;
                if (string.IsNullOrEmpty(effectiveArgument))
                    effectiveArgument = config.LaunchArguments;
            }
        }

        var startInfo = string.IsNullOrEmpty(effectiveArgument)
            ? new ProcessStartInfo(startingExecutablePath)
            : new ProcessStartInfo(startingExecutablePath, effectiveArgument);

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;

        process = new Process
        {
            StartInfo = startInfo
        };

        // A silent launch is only meaningful when going through the vendor launcher: it is the launcher (not the
        // game) whose window/flow we are hiding. When we launch HTGame.exe directly there is nothing extra to hide.
        if (usingBootstrapper && config.SilentLaunch)
        {
            var launcherDir = Path.GetDirectoryName(Path.Combine(gamePath, bootstrapperRelativePath!)) ?? gamePath;
            var settingsIniPath = string.IsNullOrEmpty(config.LauncherSettingsIniRelativePath)
                ? string.Empty
                : Path.Combine(gamePath, config.LauncherSettingsIniRelativePath);
            silentPlan = new SilentLaunchPlan(launcherDir, gameExecutablePath, settingsIniPath, config);
        }

        return true;
    }

    private static async Task ReadGameLog(GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        if (context is not { PresetConfig: PluginPresetConfigBase presetConfig }) return;

        presetConfig.comGet_GameAppDataPath(out var gameAppDataPath);
        presetConfig.comGet_GameLogFileName(out var gameLogFileName);

        if (string.IsNullOrEmpty(gameAppDataPath) || string.IsNullOrEmpty(gameLogFileName))
            return;

        var gameLogPath = Path.Combine(gameAppDataPath, gameLogFileName);

        var retry = 5;
        while (!File.Exists(gameLogPath) && retry >= 0)
        {
            await Task.Delay(1000, token);
            --retry;
        }

        if (retry <= 0) return;

        var printCallback = context.PrintGameLogCallback;

        try
        {
            await using var fileStream =
                File.Open(gameLogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream);

            fileStream.Position = 0;
            while (!token.IsCancellationRequested)
            {
                while (await reader.ReadLineAsync(token) is { } line) PassStringLineToCallback(printCallback, line);
                await Task.Delay(250, token);
            }
        }
        catch (Exception ex)
        {
            InstanceLogger.LogWarning($"[NTE::ReadGameLog] Stopped reading log: {ex.Message}");
        }

        return;

        static unsafe void PassStringLineToCallback(GameManagerExtension.PrintGameLog? invoke, string line)
        {
            var lineP = line.GetPinnableStringPointer();
            var lineLen = line.Length;
            invoke?.Invoke(lineP, lineLen, 0);
        }
    }

    /// <summary>
    /// Everything needed to silence the vendor launcher for a single launch: where the launcher lives, which game
    /// binary to track, which settings file to patch and the per-game silent-launch configuration.
    /// </summary>
    private sealed record SilentLaunchPlan(string LauncherDir, string GameExePath, string SettingsIniPath, WanmeiGameConfig Config);

    /// <summary>
    /// Drives a silent launch through the vendor launcher. The launcher's own settings (already patched before start)
    /// make it auto-login, auto-start the game and quit together with the game. On top of that, when Collapse itself
    /// runs elevated, the launcher's start-up window is hidden until the game appears (revealed early only if the log
    /// reports that an interactive login is required, or after a timeout). We always track the GAME process, because
    /// the thin bootstrapper exits within a second of spawning the elevated launcher.
    /// </summary>
    private static async Task DriveLauncherSilentlyAsync(SilentLaunchPlan plan, long launcherLogStartLength, CancellationToken token)
    {
        var baseNames = plan.Config.LauncherProcessBaseNames ?? [];
        var elevated = IsProcessElevated();

        // Window hiding is only possible when we can actually touch the launcher's (elevated) windows, i.e. when the
        // host is elevated too. Otherwise we degrade gracefully: the settings patch still removes the manual "Start"
        // click and the after-exit reappearance, the launcher merely flashes during start-up/auto-login.
        using var hideCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var hideTask = Task.CompletedTask;
        if (elevated && baseNames.Length > 0)
        {
            var revealTimeout = plan.Config.LauncherStartupRevealTimeoutSeconds > 0
                ? plan.Config.LauncherStartupRevealTimeoutSeconds
                : 120;
            hideTask = Task.Run(() => HideLauncherWindowsLoopAsync(plan.LauncherDir, baseNames, revealTimeout,
                launcherLogStartLength, hideCts.Token), CancellationToken.None);
        }

        Process? game = await WaitForGameProcessAsync(plan.GameExePath, plan.LauncherDir, baseNames, token);

        // Stop hiding once the game is up (it now covers the screen) or if we gave up waiting.
        hideCts.Cancel();
        try { await hideTask; } catch { /* best-effort */ }

        if (game is null)
        {
            // No game ever appeared (user closed the launcher, cancelled a login, or launch failed). Make sure we did
            // not leave any launcher window hidden.
            if (elevated && baseNames.Length > 0) SetLauncherWindowsVisible(plan.LauncherDir, baseNames, true);
            return;
        }

        using (game)
        {
            try { await game.WaitForExitAsync(token); }
            catch { /* cancelled or the game object went away */ }
        }

        // quitWithGame=1 makes the launcher exit on its own after the game closes. As an elevated-only safety net,
        // terminate any lingering launcher process so it cannot pop back to the foreground and so its single-instance
        // mutex is released for the next launch.
        if (elevated && baseNames.Length > 0)
            TerminateLauncherTree(plan.LauncherDir, baseNames);
    }

    /// <summary>
    /// Waits for the tracked game binary to appear. Gives up only when the whole launcher tree has disappeared and no
    /// game showed up (after a short start-up grace period), which means the launch was aborted.
    /// </summary>
    private static async Task<Process?> WaitForGameProcessAsync(string gameExePath, string launcherDir,
        string[] baseNames, CancellationToken token)
    {
        var start = DateTime.UtcNow;
        const int startupGraceSeconds = 15;

        while (!token.IsCancellationRequested)
        {
            var game = FindExecutableProcess(gameExePath);
            if (game is not null) return game;

            var launcherAlive = baseNames.Length == 0 || IsLauncherTreeAlive(launcherDir, baseNames);
            if (!launcherAlive && (DateTime.UtcNow - start).TotalSeconds > startupGraceSeconds)
                return null;

            try { await Task.Delay(500, token); }
            catch { return null; }
        }

        return null;
    }

    /// <summary>
    /// Keeps the launcher's start-up window hidden until either the game appears (loop is cancelled) or an interactive
    /// login is detected / the reveal timeout elapses, in which case the window is shown so the user can complete the
    /// first-time or expired-token login.
    /// </summary>
    private static async Task HideLauncherWindowsLoopAsync(string launcherDir, string[] baseNames,
        int revealTimeoutSeconds, long launcherLogStartLength, CancellationToken token)
    {
        var start = DateTime.UtcNow;
        var revealed = false;

        while (!token.IsCancellationRequested)
        {
            if (!revealed)
            {
                var needLogin = LauncherReportsLoginNeeded(launcherDir, launcherLogStartLength);
                var timedOut = (DateTime.UtcNow - start).TotalSeconds > revealTimeoutSeconds;

                if (needLogin || timedOut)
                {
                    revealed = true;
                    SetLauncherWindowsVisible(launcherDir, baseNames, true);
                }
                else
                {
                    SetLauncherWindowsVisible(launcherDir, baseNames, false);
                }
            }

            try { await Task.Delay(300, token); }
            catch { return; }
        }
    }

    /// <summary>
    /// Enumerates the launcher-tree processes that belong to this install (scoped by directory when the module path is
    /// readable, otherwise accepted by name — an elevated launcher's module path is not readable from a non-elevated
    /// host) and invokes <paramref name="action"/> for each. Every process handle is disposed after the callback.
    /// </summary>
    private static void ForEachLauncherProcess(string launcherDir, string[] baseNames, Action<Process> action)
    {
        foreach (var name in baseNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    bool under;
                    try { under = p.MainModule?.FileName?.StartsWith(launcherDir, StringComparison.OrdinalIgnoreCase) ?? false; }
                    catch { under = true; }

                    if (under) action(p);
                }
                catch { /* best-effort */ }
                finally { p.Dispose(); }
            }
        }
    }

    private static bool IsLauncherTreeAlive(string launcherDir, string[] baseNames)
    {
        var alive = false;
        ForEachLauncherProcess(launcherDir, baseNames, _ => alive = true);
        return alive;
    }

    private static void TerminateLauncherTree(string launcherDir, string[] baseNames)
    {
        ForEachLauncherProcess(launcherDir, baseNames, p =>
        {
            try { if (!p.HasExited) p.Kill(true); }
            catch { /* already gone or access denied */ }
        });
    }

    /// <summary>
    /// Shows or hides the main window of every launcher-tree process. The game binary is never in the tree list, so
    /// this cannot hide the game itself.
    /// </summary>
    private static void SetLauncherWindowsVisible(string launcherDir, string[] baseNames, bool visible)
    {
        ForEachLauncherProcess(launcherDir, baseNames, p =>
        {
            try
            {
                p.Refresh();
                nint handle = p.MainWindowHandle;
                if (handle == 0) return;

                if (visible)
                    NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);
                else if (NativeMethods.IsWindowVisible(handle))
                    NativeMethods.ShowWindow(handle, NativeMethods.SW_HIDE);
            }
            catch { /* window may not exist yet */ }
        });
    }

    /// <summary>
    /// Tails the launcher's NTEGame.log (from the offset captured just before launch) for markers that indicate the
    /// cached-token auto-login failed and an interactive login is required.
    /// </summary>
    private static bool LauncherReportsLoginNeeded(string launcherDir, long launcherLogStartLength)
    {
        try
        {
            var logPath = Path.Combine(launcherDir, "UserData", "Log", "NTEGame.log");
            if (!File.Exists(logPath)) return false;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var from = fs.Length < launcherLogStartLength ? 0 : launcherLogStartLength;
            fs.Seek(from, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.Latin1);
            var text = reader.ReadToEnd();

            return text.Contains("onAutoLoginFailed", StringComparison.Ordinal)
                || text.Contains("onAutoLoginTimeOut", StringComparison.Ordinal)
                || text.Contains("autoLoginTokenError", StringComparison.Ordinal)
                || text.Contains("needLoginFirst", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static long GetLauncherLogLength(string launcherDir)
    {
        try
        {
            var logPath = Path.Combine(launcherDir, "UserData", "Log", "NTEGame.log");
            return File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Patches the launcher's user-writable, non-integrity-checked settings file so the launcher silences itself:
    /// autoLogin (use the cached token), autoRun (start the game without a "Start" click), quitWithGame (exit when the
    /// game exits) and showAfterGameQuit=0 (do not reappear after the game exits). All other content is preserved
    /// byte-for-byte. The file is plain ASCII, so ISO-8859-1/Latin1 round-trips it exactly.
    /// </summary>
    private static void PatchLauncherSettings(string settingsIniPath)
    {
        try
        {
            var desired = new (string Key, string Value)[]
            {
                ("autoLogin", "1"),
                ("autoRun", "1"),
                ("quitWithGame", "1"),
                ("showAfterGameQuit", "0")
            };

            var lines = new List<string>();
            if (File.Exists(settingsIniPath))
                lines.AddRange(File.ReadAllLines(settingsIniPath, Encoding.Latin1));
            else
                Directory.CreateDirectory(Path.GetDirectoryName(settingsIniPath)!);

            // Locate the [Setting] section boundaries.
            var sectionStart = -1;
            var sectionEnd = lines.Count;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (sectionStart < 0)
                {
                    if (trimmed.Equals("[Setting]", StringComparison.OrdinalIgnoreCase)) sectionStart = i;
                }
                else if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    sectionEnd = i;
                    break;
                }
            }

            if (sectionStart < 0)
            {
                // No [Setting] section yet: append a fresh one with all desired keys.
                if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add(string.Empty);
                lines.Add("[Setting]");
                foreach (var (key, value) in desired) lines.Add($"{key}={value}");
            }
            else
            {
                foreach (var (key, value) in desired)
                {
                    var replaced = false;
                    for (var i = sectionStart + 1; i < sectionEnd; i++)
                    {
                        var trimmed = lines[i].TrimStart();
                        var eq = trimmed.IndexOf('=');
                        if (eq <= 0) continue;

                        var existingKey = trimmed[..eq].Trim();
                        if (!existingKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                        lines[i] = $"{key}={value}";
                        replaced = true;
                        break;
                    }

                    if (!replaced)
                    {
                        lines.Insert(sectionEnd, $"{key}={value}");
                        sectionEnd++;
                    }
                }
            }

            File.WriteAllLines(settingsIniPath, lines, Encoding.Latin1);
        }
        catch (Exception e)
        {
            InstanceLogger.LogWarning($"[NTE::PatchLauncherSettings] Could not patch launcher settings '{settingsIniPath}': {e.Message}");
        }
    }

    private static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static partial class NativeMethods
    {
        internal const int SW_HIDE = 0;
        internal const int SW_SHOW = 5;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(nint hWnd);
    }
}
