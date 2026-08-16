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
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;
using RunGameFromGameManagerContext =
    Hi3Helper.Plugin.Core.Utility.GameManagerExtension.RunGameFromGameManagerContext;

namespace Hi3Helper.PerfectWorld.Core.Management;

/// <summary>
///     Reusable game-launch driver for Perfect World pw_sdk titles. It is entirely driven by the game's
///     <see cref="PerfectWorldGameConfig"/> (which vendor launcher to run, which settings to patch, which log to
///     tail, which login markers to watch for), so a thin plugin only has to forward its ABI game-launch overrides
///     to a single shared instance of this class.
/// </summary>
/// <remarks>
///     Behaviour mirrors the official vendor shortcut: when the vendor launcher is present it is launched (so the
///     account-login UI, anti-cheat and pipe hand-off all run) with the install root as working directory; when
///     <see cref="PerfectWorldGameConfig.SilentLaunch"/> is set, the launcher is additionally driven "silently"
///     (settings patched for auto-login/auto-start/quit-with-game, and — only when the host is elevated — its
///     start-up window hidden until the game appears).
/// </remarks>
public sealed partial class PerfectWorldGameLauncher
{
    /// <summary>
    ///     True while a plugin-driven silent launch is in flight (from just before the vendor launcher is started
    ///     until the game has exited and the launcher tree has been cleaned up). Lets <see cref="IsGameRunning"/>
    ///     report the game as running throughout the launcher's lengthy start-up, before the game process appears.
    /// </summary>
    private volatile bool _silentLaunchSessionActive;

    /// <summary>Local-time ticks of the most recent silent launch, used as a fallback game start time.</summary>
    private long _silentLaunchStartTicks;

    public (bool IsSupported, Task<bool> Task) LaunchGameFromGameManager(
        RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted,
        ProcessPriorityClass processPriority, CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetStartingProcessFromContext(context, startArgument, out var process, out var silentPlan))
                return false;

            using (process)
            {
                bool silent = silentPlan is not null;

                // Mark the whole silent-launch session as active up-front (before the process is even started) so the
                // UI reports "game running" for the entire vendor-launcher start-up — which can span a UAC prompt plus
                // a lengthy auto-login before the game process finally appears. Without this the button would flip
                // back to "Start" during that window (and any host that waits on "is the game running yet?" restores
                // early).
                if (silent)
                {
                    Volatile.Write(ref _silentLaunchStartTicks, DateTime.Now.Ticks);
                    _silentLaunchSessionActive = true;
                }

                try
                {
                    // Patch the vendor launcher's own settings so it auto-logs-in and quits together with the game.
                    // The auto-click path uses its own settings set (typically autoRun=0, so only the injected click
                    // starts the game); the "/autoplay" path uses the default set. This is what removes the manual
                    // "Start" click and stops the launcher from reappearing after the game exits. It works regardless
                    // of elevation (the elevated launcher reads these settings itself), so it is the primary silencing
                    // mechanism.
                    if (silent && !string.IsNullOrEmpty(silentPlan!.SettingsIniPath))
                    {
                        var settings = silentPlan.AutoClick is not null
                                       && silentPlan.Config.LauncherAutoClickSilentSettings.Length > 0
                            ? silentPlan.Config.LauncherAutoClickSilentSettings
                            : silentPlan.Config.LauncherSilentSettings;
                        PatchLauncherSettings(silentPlan.SettingsIniPath, silentPlan.Config, settings);
                    }

                    long launcherLogStartLength =
                        silent ? GetLauncherLogLength(silentPlan!.LauncherDir, silentPlan.Config) : 0;

                    try
                    {
                        process.Start();
                    }
                    catch (Exception e)
                    {
                        SharedStatic.InstanceLogger.LogError(e,
                            "[PWLauncher::LaunchGame] Failed to start the launcher/game process.");
                        return false;
                    }

                    try
                    {
                        process.PriorityBoostEnabled = isRunBoosted;
                        process.PriorityClass = processPriority;
                    }
                    catch (Exception e)
                    {
                        SharedStatic.InstanceLogger.LogError(e, "[PWLauncher::LaunchGame] Failed to set process priority.");
                    }

                    // Inject the auto-click helper as early as possible (well before the launcher creates its QML view,
                    // so the DLL's IAT hook captures the context object). The DLL then waits until we signal readiness.
                    var autoClickInjected = false;
                    if (silent && silentPlan!.AutoClick is not null)
                        autoClickInjected = silentPlan.AutoClick.Inject(process.Id);

                    _ = ReadGameLog(context, token);

                    if (silent)
                        await DriveLauncherSilentlyAsync(silentPlan!, launcherLogStartLength, autoClickInjected, token);
                    else
                        await process.WaitForExitAsync(token);

                    return true;
                }
                finally
                {
                    if (silent)
                    {
                        _silentLaunchSessionActive = false;
                        silentPlan!.AutoClick?.Dispose();
                    }
                }
            }
        }
    }

    public bool IsGameRunning(RunGameFromGameManagerContext context, out bool isGameRunning, out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        // 1) The real game process is the source of truth whenever it exists.
        if (TryGetGameExecutablePath(context, out var gameExecutablePath))
        {
            using var process = FindExecutableProcess(gameExecutablePath);
            if (process != null)
            {
                isGameRunning = true;
                // StartTime of an elevated game process may be unreadable from a non-elevated host; fall back to the
                // recorded launch time so we never throw out of this ABI method (a throw is surfaced to the host as an
                // error HRESULT, which makes it treat the game as "not running").
                try { gameStartTime = process.StartTime; }
                catch { gameStartTime = GetSilentLaunchStartTimeOrNow(); }
                return true;
            }
        }

        // 2) While our own silent launch is mid-flight, the vendor launcher is still coming up (UAC + auto-login)
        //    before the game process appears. Report "running" for that whole window so the UI shows "game running"
        //    instead of flipping back to "Start" (and so the user cannot start a second, mutex-blocked instance).
        if (_silentLaunchSessionActive)
        {
            isGameRunning = true;
            gameStartTime = GetSilentLaunchStartTimeOrNow();
        }

        return true;
    }

    public (bool IsSupported, Task<bool> Task) WaitRunningGame(RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            // Mirror an in-flight silent launch: keep waiting through the launcher start-up until the whole session
            // ends, rather than returning immediately just because the game process has not spawned yet.
            while (!token.IsCancellationRequested && _silentLaunchSessionActive)
            {
                try { await Task.Delay(1000, token); }
                catch { return true; }
            }

            if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

            using var process = FindExecutableProcess(gameExecutablePath);
            if (process != null)
            {
                try { await process.WaitForExitAsync(token); }
                catch { /* cancelled */ }
            }

            return true;
        }
    }

    public bool KillRunningGame(RunGameFromGameManagerContext context, out bool wasGameRunning,
        out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (TryGetGameExecutablePath(context, out var gameExecutablePath))
        {
            using var process = FindExecutableProcess(gameExecutablePath);
            if (process != null)
            {
                wasGameRunning = true;
                try { gameStartTime = process.StartTime; }
                catch { gameStartTime = GetSilentLaunchStartTimeOrNow(); }

                try { process.Kill(); }
                catch (Exception e) { SharedStatic.InstanceLogger.LogWarning($"[PWLauncher::KillRunningGame] Could not kill game: {e.Message}"); }
            }
        }

        // Best-effort: also tear down the vendor launcher tree so it does not linger and so its single-instance mutex
        // is released. Killing the elevated launcher only succeeds when the host itself is elevated.
        if (TryGetSilentLaunchInfo(context, out var launcherDir, out var baseNames))
            TerminateLauncherTree(launcherDir, baseNames);

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
                // MainModule of an elevated game process is not readable from a non-elevated host (the game may run
                // elevated because it is spawned by a force-admin launcher). In that case fall back to a name-only
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

    private static bool TryGetGameExecutablePath(RunGameFromGameManagerContext context,
        [NotNullWhen(true)] out string? gameExecutablePath)
    {
        gameExecutablePath = null;
        if (context is not
            {
                GameManager: PerfectWorldGameManager manager, PresetConfig: PluginPresetConfigBase presetConfig
            }) return false;

        manager.GetGamePath(out var gamePath);
        presetConfig.comGet_GameExecutableName(out var executablePath);

        gamePath?.NormalizePathInplace();
        executablePath.NormalizePathInplace();

        if (string.IsNullOrEmpty(gamePath)) return false;

        gameExecutablePath = Path.Combine(gamePath, executablePath);
        return File.Exists(gameExecutablePath);
    }

    private static bool TryGetStartingProcessFromContext(RunGameFromGameManagerContext context,
        string? startArgument, [NotNullWhen(true)] out Process? process, out SilentLaunchPlan? silentPlan)
    {
        process = null;
        silentPlan = null;

        // Existence of the real game binary gates launch; it is also what we track for run/kill detection.
        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return false;
        if (context.GameManager is not PerfectWorldGameManager manager) return false;

        manager.GetGamePath(out var gamePath);
        if (string.IsNullOrEmpty(gamePath)) return false;

        // Default: launch the game binary directly with only a user-supplied argument, if any. Its working directory
        // is its own folder, as the client expects.
        var startingExecutablePath = gameExecutablePath;
        var effectiveArgument = startArgument;
        var workingDirectory = Path.GetDirectoryName(gameExecutablePath);

        var config = manager.Config;

        // Prefer the vendor launcher when it is present on disk: it hosts the account login UI and drives the game
        // process (anti-cheat, pipe hand-off), so a direct game launch cannot log in. The plugin installs the
        // launcher alongside the game, so this is the normal path. This mirrors the official shortcut exactly: launch
        // the vendor bootstrapper with the working directory set to the install root (NOT the launcher's own folder).
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
            }
        }

        // A silent launch is only meaningful when going through the vendor launcher: it is the launcher (not the
        // game) whose window/flow we are hiding. When we launch the game directly there is nothing extra to hide.
        var silent = usingBootstrapper && config.SilentLaunch;

        // Decide the launch path: DLL-injection auto-click (presses the launcher's real "开始游戏" button after its
        // resource check) vs. the vendor "/autoplay" flag. Auto-click needs an elevated host (the launcher is
        // force-elevated, so injecting into it requires it too) and its own non-"/autoplay" argument set. It is only
        // meaningful when we control the launcher's arguments, so a caller-supplied custom argument disables it (we
        // honour that argument verbatim instead). If the helper DLL / ready-event cannot be prepared we transparently
        // fall back to the "/autoplay" path.
        LauncherAutoClickSession? autoClick = null;
        if (silent && string.IsNullOrEmpty(effectiveArgument)
            && config.LauncherAutoClickEnabled && !string.IsNullOrEmpty(config.LaunchArgumentsAutoClick)
            && IsProcessElevated() && LauncherAutoClickSession.TryCreate(config, out autoClick) && autoClick is not null)
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[PWLauncher::LaunchGame] Auto-click launch path active (DLL injection); DLL log: {Log}",
                autoClick.DllLogPath);
        }
        else
        {
            autoClick?.Dispose();
            autoClick = null;
        }

        // Argument selection: honour a caller-supplied argument first; otherwise use the auto-click argument set
        // (no "/autoplay") when auto-click is active, or the default "/autoplay" arguments otherwise.
        if (usingBootstrapper && string.IsNullOrEmpty(effectiveArgument))
            effectiveArgument = autoClick is not null ? config.LaunchArgumentsAutoClick : config.LaunchArguments;

        var startInfo = string.IsNullOrEmpty(effectiveArgument)
            ? new ProcessStartInfo(startingExecutablePath)
            : new ProcessStartInfo(startingExecutablePath, effectiveArgument);

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;

        // Hand the auto-click configuration to the launcher-to-be through inherited environment variables.
        autoClick?.PopulateEnvironment(startInfo.Environment);

        process = new Process
        {
            StartInfo = startInfo
        };

        if (silent)
        {
            var launcherDir = Path.GetDirectoryName(Path.Combine(gamePath, bootstrapperRelativePath!)) ?? gamePath;
            var settingsIniPath = string.IsNullOrEmpty(config.LauncherSettingsIniRelativePath)
                ? string.Empty
                : Path.Combine(gamePath, config.LauncherSettingsIniRelativePath);
            silentPlan = new SilentLaunchPlan(launcherDir, gameExecutablePath, settingsIniPath, config, autoClick);
        }
        else
        {
            // Auto-click is only ever created on the silent path, but guard against leaking the session/event handle.
            autoClick?.Dispose();
        }

        return true;
    }

    private static async Task ReadGameLog(RunGameFromGameManagerContext context, CancellationToken token)
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

        // Give up only when the log truly never appeared. Keying this off the retry counter instead would drop the
        // last-chance case where the file shows up on the final poll (retry == 0), leaving the game log untailed.
        if (!File.Exists(gameLogPath)) return;

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
            SharedStatic.InstanceLogger.LogWarning($"[PWLauncher::ReadGameLog] Stopped reading log: {ex.Message}");
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
    ///     Everything needed to silence the vendor launcher for a single launch: where the launcher lives, which game
    ///     binary to track, which settings file to patch and the per-game silent-launch configuration.
    /// </summary>
    private sealed record SilentLaunchPlan(string LauncherDir, string GameExePath, string SettingsIniPath,
        PerfectWorldGameConfig Config, LauncherAutoClickSession? AutoClick);

    /// <summary>
    ///     Drives a silent launch through the vendor launcher. The launcher's own settings (already patched before
    ///     start) make it auto-login and quit together with the game. The game is started either by the vendor
    ///     "/autoplay" flag or, on the auto-click path, by the injected helper DLL pressing the real "开始游戏" button
    ///     once we signal that the launcher reached its ready state. On top of that, when Collapse itself runs elevated,
    ///     the launcher's start-up window is hidden until the game appears (revealed early only if the log reports that
    ///     an interactive login is required, or after a timeout). We always track the GAME process, because the thin
    ///     bootstrapper exits within a second of spawning the elevated launcher.
    /// </summary>
    private static async Task DriveLauncherSilentlyAsync(SilentLaunchPlan plan, long launcherLogStartLength,
        bool autoClickInjected, CancellationToken token)
    {
        var baseNames = plan.Config.LauncherProcessBaseNames ?? [];
        var elevated = IsProcessElevated();

        var autoClickActive = plan.AutoClick is not null && autoClickInjected;
        // If auto-click was requested but injection failed, the launcher was started WITHOUT "/autoplay", so it will
        // park at the "开始游戏" button. Keep its window visible so the user can press it themselves (still the
        // correct on-demand-voice flow) instead of hiding a launcher that will never auto-start.
        var autoClickInjectionFailed = plan.AutoClick is not null && !autoClickInjected;

        // Fire the injected click once the launcher's log shows it is ready to start the game.
        using var signalCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var signalTask = Task.CompletedTask;
        if (autoClickActive)
            signalTask = Task.Run(() => WatchForReadyAndSignalAsync(plan, launcherLogStartLength, signalCts.Token),
                CancellationToken.None);

        // Window hiding is only possible when we can actually touch the launcher's (elevated) windows, i.e. when the
        // host is elevated too. Otherwise we degrade gracefully: the settings patch still removes the manual "Start"
        // click and the after-exit reappearance, the launcher merely flashes during start-up/auto-login.
        using var hideCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var hideTask = Task.CompletedTask;
        if (elevated && baseNames.Length > 0 && !autoClickInjectionFailed)
        {
            var revealTimeout = plan.Config.LauncherStartupRevealTimeoutSeconds > 0
                ? plan.Config.LauncherStartupRevealTimeoutSeconds
                : 120;
            hideTask = Task.Run(() => HideLauncherWindowsLoopAsync(plan.LauncherDir, baseNames, revealTimeout,
                launcherLogStartLength, plan.Config, hideCts.Token), CancellationToken.None);
        }

        Process? game = await WaitForGameAppearOrAbortAsync(plan.GameExePath, plan.LauncherDir, baseNames, token);

        // Stop hiding/signalling once the game is up (it now covers the screen) or if we gave up waiting.
        hideCts.Cancel();
        signalCts.Cancel();
        try { await hideTask; } catch { /* best-effort */ }
        try { await signalTask; } catch { /* best-effort */ }

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
    ///     Waits for the tracked game binary to appear. Tolerates the brief gap right after launch where the thin shim
    ///     has exited but the elevated game process is still coming up behind a UAC prompt: only a *sustained* absence
    ///     of the whole launcher tree (with no game) is treated as an aborted launch. During auto-login the game
    ///     process keeps the tree alive, so the "tree dead" streak stays at zero until the game appears.
    /// </summary>
    private static async Task<Process?> WaitForGameAppearOrAbortAsync(string gameExePath, string launcherDir,
        string[] baseNames, CancellationToken token)
    {
        var start = DateTime.UtcNow;
        const int intervalMs = 1000;
        const double abortAfterTreeDeadSeconds = 60;   // launcher tree gone this long with no game => aborted
        const double hardCapSeconds = 15 * 60;         // absolute safety net
        double treeDeadForSeconds = 0;

        while (!token.IsCancellationRequested)
        {
            var game = FindExecutableProcess(gameExePath);
            if (game is not null) return game;

            var treeAlive = baseNames.Length > 0 && IsLauncherTreeAlive(launcherDir, baseNames);
            treeDeadForSeconds = treeAlive ? 0 : treeDeadForSeconds + intervalMs / 1000.0;

            if (treeDeadForSeconds >= abortAfterTreeDeadSeconds) return null;
            if ((DateTime.UtcNow - start).TotalSeconds >= hardCapSeconds) return null;

            try { await Task.Delay(intervalMs, token); }
            catch { return null; }
        }

        return null;
    }

    /// <summary>
    ///     Keeps the launcher's start-up window hidden until either the game appears (loop is cancelled) or an
    ///     interactive login is detected / the reveal timeout elapses, in which case the window is shown so the user
    ///     can complete the first-time or expired-token login.
    /// </summary>
    private static async Task HideLauncherWindowsLoopAsync(string launcherDir, string[] baseNames,
        int revealTimeoutSeconds, long launcherLogStartLength, PerfectWorldGameConfig config, CancellationToken token)
    {
        var start = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            var needLogin = LauncherReportsLoginNeeded(launcherDir, launcherLogStartLength, config);
            var timedOut = (DateTime.UtcNow - start).TotalSeconds > revealTimeoutSeconds;

            if (needLogin || timedOut)
            {
                // Reveal for an interactive login (or as a timeout fallback) and stop: revealing is terminal, so
                // there is nothing left to hide and no reason to keep polling until the caller cancels us.
                SetLauncherWindowsVisible(launcherDir, baseNames, true);
                return;
            }

            SetLauncherWindowsVisible(launcherDir, baseNames, false);

            try { await Task.Delay(300, token); }
            catch { return; }
        }
    }

    /// <summary>
    ///     Enumerates the launcher-tree processes that belong to this install (scoped by directory when the module
    ///     path is readable, otherwise accepted by name — an elevated launcher's module path is not readable from a
    ///     non-elevated host) and invokes <paramref name="action"/> for each. Every process handle is disposed after
    ///     the callback.
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
    ///     Shows or hides every top-level window owned by the launcher-tree processes. The game binary is never in the
    ///     tree list, so this cannot hide the game itself.
    /// </summary>
    /// <remarks>
    ///     Windows are located by enumerating all top-level windows (<see cref="NativeMethods.EnumWindows"/>) and
    ///     matching their owning process id against the launcher tree — deliberately NOT via
    ///     <see cref="Process.MainWindowHandle"/>. <c>MainWindowHandle</c> only ever returns a window that is currently
    ///     <em>visible</em>: once a window has been hidden with <c>SW_HIDE</c> it reports <c>0</c>, which would make
    ///     revealing an already-hidden window (e.g. the launcher's login screen) impossible and deadlock the silent
    ///     launch forever. <c>EnumWindows</c> still finds hidden windows, so the reveal path always works.
    /// </remarks>
    private static unsafe void SetLauncherWindowsVisible(string launcherDir, string[] baseNames, bool visible)
    {
        var pids = new HashSet<uint>();
        ForEachLauncherProcess(launcherDir, baseNames, p =>
        {
            try { pids.Add((uint)p.Id); }
            catch { /* process already gone */ }
        });
        if (pids.Count == 0) return;

        var context = new WindowVisibilityContext(pids, visible);
        var contextHandle = GCHandle.Alloc(context);
        try
        {
            NativeMethods.EnumWindows(&SetWindowVisibilityCallback, GCHandle.ToIntPtr(contextHandle));
        }
        catch { /* best-effort */ }
        finally
        {
            contextHandle.Free();
        }
    }

    /// <summary>State handed to <see cref="SetWindowVisibilityCallback"/> through the <c>EnumWindows</c> lParam.</summary>
    private sealed record WindowVisibilityContext(HashSet<uint> Pids, bool Visible);

    /// <summary>
    ///     <c>EnumWindows</c> callback: shows or hides the enumerated window when it belongs to one of the launcher
    ///     process ids. Must never let an exception escape into the native caller.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int SetWindowVisibilityCallback(nint hWnd, nint lParam)
    {
        try
        {
            if (GCHandle.FromIntPtr(lParam).Target is not WindowVisibilityContext context)
                return 1;

            _ = NativeMethods.GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (!context.Pids.Contains(windowPid))
                return 1;

            if (context.Visible)
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
            else if (NativeMethods.IsWindowVisible(hWnd))
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
        }
        catch { /* swallow: an exception must not cross the native boundary */ }

        return 1; // keep enumerating
    }

    /// <summary>
    ///     Tails the launcher's log (from the offset captured just before launch) for the markers that mean the
    ///     cached-token auto-login failed and an interactive login UI must be revealed to the user
    ///     (<see cref="PerfectWorldGameConfig.LoginNeededLogMarkers"/>).
    /// </summary>
    private static bool LauncherReportsLoginNeeded(string launcherDir, long launcherLogStartLength,
        PerfectWorldGameConfig config)
    {
        var text = ReadLauncherLogFrom(launcherDir, launcherLogStartLength, config);
        if (text.Length == 0) return false;

        foreach (var marker in config.LoginNeededLogMarkers)
        {
            if (!string.IsNullOrEmpty(marker) && text.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Reads the launcher log from <paramref name="launcherLogStartLength"/> to the end, tolerating truncation or
    ///     rotation (reads from 0 if the file shrank). Returns an empty string on any error.
    /// </summary>
    private static string ReadLauncherLogFrom(string launcherDir, long launcherLogStartLength,
        PerfectWorldGameConfig config)
    {
        try
        {
            var logPath = Path.Combine(launcherDir, config.LauncherLogRelativePath);
            if (!File.Exists(logPath)) return string.Empty;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var from = fs.Length < launcherLogStartLength ? 0 : launcherLogStartLength;
            fs.Seek(from, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Polls the launcher log until the "ready to start game" marker appears, then signals the injected auto-click
    ///     DLL to press the button. Returns once it has signalled, or when cancelled (the game appeared, or the launch
    ///     was aborted). If the marker never appears the DLL is never released and the reveal-timeout fallback shows the
    ///     launcher for a manual click.
    /// </summary>
    private static async Task WatchForReadyAndSignalAsync(SilentLaunchPlan plan, long launcherLogStartLength,
        CancellationToken token)
    {
        var marker = plan.Config.LauncherAutoClickReadyLogMarker;
        if (plan.AutoClick is null || string.IsNullOrEmpty(marker)) return;

        while (!token.IsCancellationRequested)
        {
            var text = ReadLauncherLogFrom(plan.LauncherDir, launcherLogStartLength, plan.Config);
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                plan.AutoClick.SignalReady();
                return;
            }

            try { await Task.Delay(500, token); }
            catch { return; }
        }
    }

    private static long GetLauncherLogLength(string launcherDir, PerfectWorldGameConfig config)
    {
        try
        {
            var logPath = Path.Combine(launcherDir, config.LauncherLogRelativePath);
            return File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     Patches the launcher's user-writable, non-integrity-checked settings file with the supplied
    ///     <paramref name="settings"/> so the launcher silences itself (auto-login, quit-with-game, no reappearance —
    ///     and, depending on the launch path, either auto-run or wait for the injected click). All other content is
    ///     preserved byte-for-byte. The file is plain ASCII, so ISO-8859-1/Latin1 round-trips it exactly.
    /// </summary>
    private static void PatchLauncherSettings(string settingsIniPath, PerfectWorldGameConfig config,
        (string Key, string Value)[] settings)
    {
        try
        {
            var desired = settings;
            if (desired.Length == 0) return;

            var sectionName = config.LauncherSettingsSectionName;

            var lines = new List<string>();
            if (File.Exists(settingsIniPath))
                lines.AddRange(File.ReadAllLines(settingsIniPath, Encoding.Latin1));
            else
                Directory.CreateDirectory(Path.GetDirectoryName(settingsIniPath)!);

            // Locate the settings section boundaries.
            var sectionStart = -1;
            var sectionEnd = lines.Count;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (sectionStart < 0)
                {
                    if (trimmed.Equals(sectionName, StringComparison.OrdinalIgnoreCase)) sectionStart = i;
                }
                else if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    sectionEnd = i;
                    break;
                }
            }

            if (sectionStart < 0)
            {
                // No section yet: append a fresh one with all desired keys.
                if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add(string.Empty);
                lines.Add(sectionName);
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
            SharedStatic.InstanceLogger.LogWarning($"[PWLauncher::PatchLauncherSettings] Could not patch launcher settings '{settingsIniPath}': {e.Message}");
        }
    }

    private DateTime GetSilentLaunchStartTimeOrNow()
    {
        var ticks = Volatile.Read(ref _silentLaunchStartTicks);
        return ticks == 0 ? DateTime.Now : new DateTime(ticks, DateTimeKind.Local);
    }

    /// <summary>
    ///     Resolves the vendor launcher directory and process-tree base names for the current game, when silent
    ///     launch is configured. Used by run detection / kill to reason about the launcher tree without starting a
    ///     launch.
    /// </summary>
    private static bool TryGetSilentLaunchInfo(RunGameFromGameManagerContext context, out string launcherDir,
        out string[] baseNames)
    {
        launcherDir = string.Empty;
        baseNames = [];

        if (context.GameManager is not PerfectWorldGameManager manager) return false;

        var config = manager.Config;
        if (!config.SilentLaunch || string.IsNullOrEmpty(config.LauncherBootstrapperRelativePath)) return false;

        manager.GetGamePath(out var gamePath);
        if (string.IsNullOrEmpty(gamePath)) return false;

        launcherDir = Path.GetDirectoryName(Path.Combine(gamePath, config.LauncherBootstrapperRelativePath)) ?? gamePath;
        baseNames = config.LauncherProcessBaseNames ?? [];
        return baseNames.Length > 0;
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

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    }
}
