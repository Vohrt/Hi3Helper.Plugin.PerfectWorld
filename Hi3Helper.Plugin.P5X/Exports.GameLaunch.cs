using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.PerfectWorld.Core.Management;

namespace Hi3Helper.Plugin.P5X;

public partial class Exports
{
    /// <summary>
    ///     Shared, reusable Perfect World game-launch driver. All game-specific behaviour comes from the game's
    ///     <see cref="PerfectWorldGameConfig"/> (resolved from the <see cref="PerfectWorldGameManager"/> in the
    ///     context), so this plugin only forwards its ABI overrides to it.
    /// </summary>
    private static readonly PerfectWorldGameLauncher GameLauncher = new();

    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted,
        ProcessPriorityClass processPriority, CancellationToken token)
        => GameLauncher.LaunchGameFromGameManager(context, startArgument, isRunBoosted, processPriority, token);

    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning, out DateTime gameStartTime)
        => GameLauncher.IsGameRunning(context, out isGameRunning, out gameStartTime);

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
        => GameLauncher.WaitRunningGame(context, token);

    protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning, out DateTime gameStartTime)
        => GameLauncher.KillRunningGame(context, out wasGameRunning, out gameStartTime);
}
