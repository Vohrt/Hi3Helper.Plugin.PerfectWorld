using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.PerfectWorld.Core.Management.Api;
using Hi3Helper.PerfectWorld.Core.Utils;
using Microsoft.Extensions.Logging;
using SharpHDiffPatch.Core;

namespace Hi3Helper.PerfectWorld.Core.Management;

public partial class PerfectWorldGameInstaller
{
    private List<PerfectWorldPatchEntry>? _cachedPatches;
    private string? _cachedPatchesVersion;

    /// <summary>Serialises lastdiff (re)fetches so concurrent callers don't each download+decode the same patch list.</summary>
    private readonly SemaphoreSlim _patchManifestLock = new(1, 1);

    // The managed HDiffPatch applier keeps some process-wide static state and each apply can allocate large
    // buffers for multi-GB paks, so applies are serialised while downloads stay parallel.
    private readonly SemaphoreSlim _patchApplyLock = new(1, 1);

    /// <summary>
    ///     A per-file update action: either apply a binary <see cref="PerfectWorldPatchEntry"/> delta on top of the local
    ///     file, or fully (re)download the content-addressed target.
    /// </summary>
    private readonly struct UpdatePlan
    {
        public required PerfectWorldResEntry Entry { get; init; }
        public PerfectWorldPatchEntry? Patch { get; init; }
        public bool IsDelta => Patch != null;

        public static UpdatePlan Delta(PerfectWorldResEntry entry, PerfectWorldPatchEntry patch) =>
            new() { Entry = entry, Patch = patch };

        public static UpdatePlan Full(PerfectWorldResEntry entry) =>
            new() { Entry = entry };
    }

    /// <summary>
    ///     Fetches and decodes the incremental <c>lastdiff</c> manifest for the current remote resource version
    ///     (cached). The blob is served raw (PatcherXML0) but a zip-wrapped variant is tolerated too.
    /// </summary>
    private async Task<List<PerfectWorldPatchEntry>> GetPatchManifestAsync(PerfectWorldRemoteConfig remote,
        CancellationToken token)
    {
        if (_cachedPatches != null && _cachedPatchesVersion == remote.ResVersion)
            return _cachedPatches;

        await _patchManifestLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: a concurrent caller may have populated the cache while we waited.
            if (_cachedPatches != null && _cachedPatchesVersion == remote.ResVersion)
                return _cachedPatches;

            PerfectWorldGameConfig config = Manager.Config;
            byte[] raw = await DownloadBytesWithFallbackAsync(
                cdn => config.BuildLastDiffUrl(cdn, remote.ResVersion), token).ConfigureAwait(false);

            byte[] bin = LooksLikeZip(raw) ? ExtractZipEntry(raw, "lastdiff.bin") : raw;
            string xml = PatcherXml0.DecodeToXml(bin, config.AppId);
            List<PerfectWorldPatchEntry> entries = PerfectWorldManifest.ParsePatchList(xml);

            SharedStatic.InstanceLogger.LogInformation(
                "[PerfectWorldInstaller] lastdiff {Version}: {Count} patch entries.", remote.ResVersion, entries.Count);

            _cachedPatches = entries;
            _cachedPatchesVersion = remote.ResVersion;
            return entries;
        }
        finally
        {
            _patchManifestLock.Release();
        }
    }

    /// <summary>
    ///     Builds a best-effort lookup used by the update <em>size estimate</em>: for each target file (keyed by its
    ///     content id <c>NewMd5</c>) it records, per source file size (<c>OldSize</c>), the smallest available
    ///     HDiffPatch delta size. This lets <see cref="GetGameDownloadedSizeAsyncInner"/> cost a changed multi-GB pak
    ///     at its small patch size instead of a full re-download, matching what <see cref="DeltaUpdateAsync"/> really
    ///     transfers. Returns <see langword="null"/> if the patch manifest can't be obtained (caller then falls back
    ///     to plain size-only accounting).
    /// </summary>
    private async Task<Dictionary<string, Dictionary<long, long>>?> TryBuildPatchSavingsIndexAsync(
        CancellationToken token)
    {
        try
        {
            PerfectWorldRemoteConfig? remote = await Manager.GetRemoteConfigAsync(false, token).ConfigureAwait(false);
            if (remote == null || string.IsNullOrEmpty(remote.ResVersion))
                return null;

            List<PerfectWorldPatchEntry> patches = await GetPatchManifestAsync(remote, token).ConfigureAwait(false);

            var index = new Dictionary<string, Dictionary<long, long>>(StringComparer.OrdinalIgnoreCase);
            foreach (PerfectWorldPatchEntry p in patches)
            {
                if (!index.TryGetValue(p.NewMd5, out Dictionary<long, long>? byOldSize))
                    index[p.NewMd5] = byOldSize = new Dictionary<long, long>();

                if (!byOldSize.TryGetValue(p.OldSize, out long existing) || p.PatchSize < existing)
                    byOldSize[p.OldSize] = p.PatchSize;
            }

            return index;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PerfectWorldInstaller] Patch index for size estimate unavailable ({Msg}); reporting full transfer sizes.",
                ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Incremental update path. Every changed file is brought to the target manifest state by applying an
    ///     HDiffPatch delta when a matching (oldMd5 → newMd5) patch smaller than a full download exists; otherwise
    ///     the target is downloaded whole. Any per-file delta failure transparently falls back to a full download,
    ///     and if the whole <c>lastdiff</c> manifest is unavailable the classic full-file reconcile is used.
    /// </summary>
    private async Task DeltaUpdateAsync(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        string installPath = EnsureAndGetGamePath();

        // --- Preparing: remote config + target manifest + patch manifest --------------------------------
        progressStateDelegate?.Invoke(InstallProgressState.Preparing);

        PerfectWorldRemoteConfig? remote = await Manager.GetRemoteConfigAsync(true, token).ConfigureAwait(false);
        if (remote == null || string.IsNullOrEmpty(remote.ResVersion))
            throw new IOException("Unable to obtain remote config.xml.");

        ManifestBundle bundle = await GetManifestBundleAsync(token, forceRefresh: true).ConfigureAwait(false);
        List<PerfectWorldResEntry> manifest = bundle.Files;
        List<PerfectWorldPakEntry> paks = bundle.Paks;
        if (manifest.Count == 0 && paks.Count == 0)
            throw new IOException("Manifest contained no file entries.");

        List<PerfectWorldPatchEntry> patches;
        try
        {
            patches = await GetPatchManifestAsync(remote, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PerfectWorldInstaller] lastdiff unavailable ({Msg}); using full-file reconcile.", ex.Message);
            await ReconcileToManifestAsync(progressDelegate, progressStateDelegate, verifyHash: true, token)
                .ConfigureAwait(false);
            return;
        }

        // Index candidate patches by target content id, then by source content id.
        var patchByNew = new Dictionary<string, Dictionary<string, PerfectWorldPatchEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (PerfectWorldPatchEntry p in patches)
        {
            if (!patchByNew.TryGetValue(p.NewMd5, out Dictionary<string, PerfectWorldPatchEntry>? byOld))
                patchByNew[p.NewMd5] = byOld = new Dictionary<string, PerfectWorldPatchEntry>(StringComparer.OrdinalIgnoreCase);
            byOld[p.OldMd5] = p;
        }

        // --- Verify: classify each manifest entry (up-to-date / delta / full) ---------------------------
        // Fetch the launcher self-update manifest up-front so the verify total is fixed before hashing begins. The
        // delta-update verify hashes every existing file (potentially tens of GB), so a live "Verifying: X / Y" count
        // is essential feedback here rather than a count that stays frozen for the whole phase.
        PerfectWorldLauncherManifest launcherManifest =
            await GetLauncherManifestAsync(token).ConfigureAwait(false);

        int verifyTotalCount = manifest.Count + paks.Sum(p => p.Files.Count) + launcherManifest.Files.Count;
        BeginVerifyPhase(verifyTotalCount, progressDelegate, progressStateDelegate);

        int verifiedCount = 0;
        void ReportVerified(int delta) =>
            ReportProgress(0, Interlocked.Add(ref verifiedCount, delta), progressDelegate, force: false);

        var plans = new ConcurrentBag<UpdatePlan>();
        await Parallel.ForEachAsync(manifest,
            new ParallelOptions { MaxDegreeOfParallelism = VerifyParallelism, CancellationToken = token },
            async (entry, ct) =>
            {
                try
                {
                    string localPath = Path.Combine(installPath,
                        entry.Filename.Replace('/', Path.DirectorySeparatorChar));

                    bool exists = File.Exists(localPath);
                    if (exists && new FileInfo(localPath).Length == entry.FileSize &&
                        await CheckMd5Async(localPath, entry.Md5, ct).ConfigureAwait(false))
                    {
                        return; // already at target
                    }

                    if (exists &&
                        patchByNew.TryGetValue(entry.Md5, out Dictionary<string, PerfectWorldPatchEntry>? byOld) &&
                        byOld.Count > 0)
                    {
                        string localMd5 = await ComputeMd5Async(localPath, ct).ConfigureAwait(false);
                        if (byOld.TryGetValue(localMd5, out PerfectWorldPatchEntry? patch) &&
                            patch.NewSize == entry.FileSize &&
                            patch.PatchSize < entry.FileSize) // only worthwhile if the patch is smaller than a full fetch
                        {
                            plans.Add(UpdatePlan.Delta(entry, patch));
                            return;
                        }
                    }

                    plans.Add(UpdatePlan.Full(entry));
                }
                finally
                {
                    // Report inside a finally so the verify count advances on every classification outcome
                    // (up-to-date / delta / full), each of which returns from a different point above.
                    ReportVerified(1);
                }
            }).ConfigureAwait(false);

        List<UpdatePlan> planList = plans.ToList();
        int upToDate = manifest.Count - planList.Count;
        long totalTransfer = planList.Sum(p => p.IsDelta ? p.Patch!.PatchSize : p.Entry.FileSize);

        // Classify packed (pak) archives. Their entries are not individually content-addressable, so a pak whose
        // files aren't all present+correct is simply re-downloaded whole and re-extracted (no per-entry delta).
        var paksToDownload = new ConcurrentBag<PerfectWorldPakEntry>();
        await Parallel.ForEachAsync(paks,
            new ParallelOptions { MaxDegreeOfParallelism = VerifyParallelism, CancellationToken = token },
            async (pak, ct) =>
            {
                if (!await IsPakCompleteAsync(pak, installPath, verifyHash: true, ct).ConfigureAwait(false))
                    paksToDownload.Add(pak);

                ReportVerified(pak.Files.Count);
            }).ConfigureAwait(false);

        int pakFilesTotal = paks.Sum(p => p.Files.Count);
        int pakFilesToDownload = paksToDownload.Sum(p => p.Files.Count);
        long pakTransfer = paksToDownload.Sum(p => p.FileSize);

        // The vendor launcher (NTELauncher\) is required at runtime; make sure it is present/up-to-date on updates
        // too (its self-update manifest may bump versions independently of the game resources).
        LauncherPlan launcherPlan =
            await PrepareLauncherPlanAsync(installPath, verifyHash: true, token,
                launcherManifest, () => ReportVerified(1)).ConfigureAwait(false);

        totalTransfer += pakTransfer + launcherPlan.ToDownloadZipBytes;
        int totalCount = manifest.Count + pakFilesTotal + launcherPlan.TotalCount;
        int upToDateTotalCount = upToDate + (pakFilesTotal - pakFilesToDownload) + launcherPlan.ExistingCount;

        int deltaCount = planList.Count(p => p.IsDelta);
        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldInstaller] Update {Version}: {UpToDate} up-to-date, {Delta} delta, {Full} full, {Paks} paks ({Bytes} bytes).",
            remote.ResVersion, upToDate, deltaCount, planList.Count - deltaCount, paksToDownload.Count, totalTransfer);

        // Verification finished: force a final "Verifying: Y / Y" report so the count visibly completes even when the
        // throttled per-file reports above didn't land on the last value.
        ReportProgress(0, verifyTotalCount, progressDelegate, force: true);

        // --- Update: download patches/files and apply ---------------------------------------------------
        lock (_reportLock)
        {
            _progress = new InstallProgress
            {
                TotalBytesToDownload = totalTransfer,
                DownloadedBytes      = 0,
                TotalCountToDownload = totalCount,
                DownloadedCount      = upToDateTotalCount,
                // See the note in ReconcileToManifestAsync: mirror the counts into the state fields so older Collapse
                // builds (which read StateCount/TotalStateToComplete for the "X / Y" label) show progress too.
                TotalStateToComplete = totalCount,
                StateCount           = upToDateTotalCount
            };
            _lastReportTick      = 0;
            _activeStateDelegate = progressStateDelegate;
            _activeReportState   = InstallProgressState.Updating;
        }

        // Emit one combined progress+state report up front so the host shows the correct starting "X / Y" count for
        // the update. ReportProgress drives both delegates (older builds only rebuild that label on the state
        // delegate — see ReportProgress), keeping the count live instead of frozen at the phase-entry value.
        ReportProgress(0, upToDateTotalCount, progressDelegate, force: true);

        long downloadedBytes = 0;
        int downloadedCount = upToDateTotalCount;

        void AddToTotal(long extra)
        {
            lock (_reportLock) _progress.TotalBytesToDownload += extra;
        }

        await Parallel.ForEachAsync(planList,
            new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = token },
            async (plan, ct) =>
            {
                string localPath = Path.Combine(installPath,
                    plan.Entry.Filename.Replace('/', Path.DirectorySeparatorChar));
                string? dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                void Report(long delta)
                {
                    long nb = Interlocked.Add(ref downloadedBytes, delta);
                    ReportProgress(nb, Volatile.Read(ref downloadedCount), progressDelegate, force: false);
                }

                bool done = false;
                if (plan.IsDelta)
                {
                    try
                    {
                        await ApplyDeltaAsync(plan.Entry, plan.Patch!, localPath, ct, Report).ConfigureAwait(false);
                        done = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        SharedStatic.InstanceLogger.LogWarning(
                            "[PerfectWorldInstaller] Delta failed for {File} ({Msg}); falling back to full download.",
                            plan.Entry.Filename, ex.Message);
                        // The full download transfers the whole file instead of just the patch, so widen the total.
                        AddToTotal(plan.Entry.FileSize - plan.Patch!.PatchSize);
                    }
                }

                if (!done)
                    await DownloadFullAsync(plan.Entry, localPath, ct, Report).ConfigureAwait(false);

                int nc = Interlocked.Increment(ref downloadedCount);
                ReportProgress(Volatile.Read(ref downloadedBytes), nc, progressDelegate, force: true);
            }).ConfigureAwait(false);

        // Bring packed (pak) files up to date too (full re-download + extract of any changed pak).
        await DownloadPaksAsync(paksToDownload, installPath,
            onBytes: delta =>
            {
                long nb = Interlocked.Add(ref downloadedBytes, delta);
                ReportProgress(nb, Volatile.Read(ref downloadedCount), progressDelegate, force: false);
            },
            onFilesDone: fileCount =>
            {
                int nc = Interlocked.Add(ref downloadedCount, fileCount);
                ReportProgress(Volatile.Read(ref downloadedBytes), nc, progressDelegate, force: true);
            },
            token).ConfigureAwait(false);

        // Ensure the vendor launcher is installed/updated alongside the game.
        await DownloadLauncherPlanAsync(launcherPlan, installPath,
            onBytes: delta =>
            {
                long nb = Interlocked.Add(ref downloadedBytes, delta);
                ReportProgress(nb, Volatile.Read(ref downloadedCount), progressDelegate, force: false);
            },
            onFileDone: fileCount =>
            {
                int nc = Interlocked.Add(ref downloadedCount, fileCount);
                ReportProgress(Volatile.Read(ref downloadedBytes), nc, progressDelegate, force: true);
            },
            token).ConfigureAwait(false);

        // --- Completion ---------------------------------------------------------------------------------
        ReportProgress(Volatile.Read(ref downloadedBytes), totalCount, progressDelegate, force: true);

        // Refresh the native pw_sdk patcher state to the updated version so the game client accepts the update
        // (records the new base build as installed, no voice). No-op unless the game opts in via
        // PerfectWorldGameConfig.WritePatcherState.
        await WritePatcherStateAsync(installPath, remote, bundle, token).ConfigureAwait(false);

        Manager.WriteInstalledResVersion(remote.ResVersion);
        progressStateDelegate?.Invoke(InstallProgressState.Completed);

        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldInstaller] Incremental update to {Version} complete ({Count} files, {Delta} via delta, {Paks} paks, launcher {Launcher} files).",
            remote.ResVersion, totalCount, deltaCount, paksToDownload.Count, launcherPlan.TotalCount);
    }

    /// <summary>
    ///     Downloads the HDiffPatch delta blob for <paramref name="patch"/>, applies it on top of the existing local
    ///     file and atomically replaces it. Throws on any verification failure so the caller can fall back to a full
    ///     download; the local file is left untouched until the patched result is fully verified.
    /// </summary>
    private async Task ApplyDeltaAsync(PerfectWorldResEntry entry, PerfectWorldPatchEntry patch, string localPath,
        CancellationToken token, Action<long> onProgress)
    {
        string patchPath = localPath + ".hpatch";
        string outputPath = localPath + ".hnew";

        try
        {
            await DownloadContentFileAsync(patch.PatchMd5, patch.PatchSize, patchPath, token, onProgress)
                .ConfigureAwait(false);

            if (!await CheckMd5Async(patchPath, patch.PatchMd5, token).ConfigureAwait(false))
                throw new IOException($"Patch blob MD5 mismatch for {entry.Filename}.");

            ForceDeleteFile(outputPath);

            await _patchApplyLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    var patcher = new HDiffPatch();
                    patcher.Initialize(patchPath);
                    patcher.Patch(localPath, outputPath, useBufferedPatch: true, token);
                }, token).ConfigureAwait(false);
            }
            finally
            {
                _patchApplyLock.Release();
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length != entry.FileSize)
                throw new IOException($"Patched size mismatch for {entry.Filename}.");
            if (!await CheckMd5Async(outputPath, entry.Md5, token).ConfigureAwait(false))
                throw new IOException($"Patched MD5 mismatch for {entry.Filename}.");

            ForceDeleteFile(localPath);
            File.Move(outputPath, localPath);
        }
        finally
        {
            ForceDeleteFile(patchPath);
            ForceDeleteFile(outputPath);
        }
    }

    /// <summary>
    ///     Fully downloads a content-addressed target file into place and verifies its MD5.
    /// </summary>
    private async Task DownloadFullAsync(PerfectWorldResEntry entry, string localPath, CancellationToken token,
        Action<long> onProgress)
    {
        string tempPath = localPath + ".tmp";

        await DownloadContentFileAsync(entry.Md5, entry.FileSize, tempPath, token, onProgress).ConfigureAwait(false);

        if (!await CheckMd5Async(tempPath, entry.Md5, token).ConfigureAwait(false))
        {
            ForceDeleteFile(tempPath);
            throw new IOException($"MD5 mismatch for {entry.Filename}.");
        }

        ForceDeleteFile(localPath);
        File.Move(tempPath, localPath);
    }

    private static bool LooksLikeZip(byte[] data) =>
        data.Length >= 2 && data[0] == (byte)'P' && data[1] == (byte)'K';
}
