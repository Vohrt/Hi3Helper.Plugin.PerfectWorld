using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.PerfectWorld.Core.Management.Api;
using Hi3Helper.PerfectWorld.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management;

public partial class PerfectWorldGameInstaller
{
    private const int ProgressThrottleMs = 500;

    /// <summary>Sub-directory (under the install root) used to stage downloaded pak blobs before extraction.</summary>
    private const string PakStagingDirName = ".perfectworld_pak_cache";

    /// <summary>UTF-8 without a BOM, matching the vendor's plaintext <c>config.xml</c> byte format exactly.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Concurrency for pak downloads; kept lower than file downloads because pak blobs are large.</summary>
    private const int PakDownloadParallelism = 2;

    /// <summary>Decrypted manifest split into directly content-addressed files and packed pak archives.</summary>
    internal sealed record ManifestBundle(List<PerfectWorldResEntry> Files, List<PerfectWorldPakEntry> Paks);

    private ManifestBundle? _cachedManifest;
    private string? _cachedManifestVersion;

    // The raw decoded catalog XML (ResList.bin) for the cached manifest version. Retained so the finalized
    // patcher-state writer can copy the large files' <Block> checksums without re-fetching/decoding the catalog.
    private string? _cachedCatalogXml;

    /// <summary>Serialises manifest (re)fetches so concurrent callers don't each download+decode the same bundle.</summary>
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    private readonly Lock _reportLock = new();
    private InstallProgress _progress;
    private long _lastReportTick;

    // The active install phase and its status delegate, captured when the download/patch phase begins. Every
    // progress report re-invokes this state delegate so the host rebuilds its on-ring "X / Y" file-count label with
    // the current count. Older Collapse builds only (re)build that label inside their status callback — which is
    // driven by the state delegate — so without re-asserting the state on each report the count would freeze at the
    // value captured when the phase began (a fresh install would sit at "0 / N" for the whole download).
    private InstallProgressStateDelegate? _activeStateDelegate;
    private InstallProgressState _activeReportState;

    /// <summary>
    ///     Fetches and decodes the <c>ResList</c> manifest for the current remote resource version (cached),
    ///     returning both the directly content-addressed <c>&lt;Res&gt;</c> files and the packed <c>&lt;Pak&gt;</c>
    ///     archives that bundle the remaining (small) files.
    /// </summary>
    private async Task<ManifestBundle> GetManifestBundleAsync(CancellationToken token, bool forceRefresh = false)
    {
        PerfectWorldRemoteConfig? remote = await Manager.GetRemoteConfigAsync(forceRefresh, token).ConfigureAwait(false);
        if (remote == null || string.IsNullOrEmpty(remote.ResVersion))
            throw new IOException("Unable to obtain remote config.xml for manifest.");

        // Fast path: return the published cache without taking the lock when it already matches.
        if (!forceRefresh && _cachedManifest is { } fast && _cachedManifestVersion == remote.ResVersion)
            return fast;

        await _manifestLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: a concurrent caller may have populated the cache while we waited.
            if (!forceRefresh && _cachedManifest is { } cached && _cachedManifestVersion == remote.ResVersion)
                return cached;

            PerfectWorldGameConfig config = Manager.Config;
            byte[] zipBytes = await DownloadBytesWithFallbackAsync(
                cdn => config.BuildResListZipUrl(cdn, remote.ResVersion), token).ConfigureAwait(false);

            byte[] resListBin = ExtractZipEntry(zipBytes, "ResList.bin");
            string xml = PatcherXml0.DecodeToXml(resListBin, config.AppId);
            List<PerfectWorldResEntry> files = PerfectWorldManifest.ParseResList(xml);
            List<PerfectWorldPakEntry> paks = PerfectWorldManifest.ParsePackages(xml);

            // Drop content the game client fetches on demand (configured per game via DeferredContentPathMarkers). For
            // 异环/NTE this removes the non-default per-language voice packs under Content/TagPatchPaks/, mirroring the
            // official launcher: it ships the base game plus exactly one default (Chinese) voice — kept here via
            // DeferredContentKeepMarkers — and offers the other languages for on-demand download in-game. Filtering
            // here keeps the size query, install and update paths mutually consistent because all of them read the
            // manifest exclusively through this (cached) method.
            ManifestBundle bundle = ApplyDeferredContentFilter(
                files, paks, config.DeferredContentPathMarkers, config.DeferredContentKeepMarkers);

            long packedCount = 0;
            foreach (PerfectWorldPakEntry pak in bundle.Paks) packedCount += pak.Files.Count;

            SharedStatic.InstanceLogger.LogInformation(
                "[PerfectWorldInstaller] Manifest {Version}: {Res} direct files + {Paks} paks ({Packed} packed files) = {Total} total.",
                remote.ResVersion, bundle.Files.Count, bundle.Paks.Count, packedCount, bundle.Files.Count + packedCount);

            _cachedManifest = bundle;
            _cachedManifestVersion = remote.ResVersion;
            _cachedCatalogXml = xml;
            return bundle;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    ///     Removes manifest content the game client itself downloads on demand, so the plugin does not fetch it at
    ///     install time. Driven by <see cref="PerfectWorldGameConfig.DeferredContentPathMarkers"/> together with the
    ///     <see cref="PerfectWorldGameConfig.DeferredContentKeepMarkers"/> exceptions: for 异环/NTE it drops the
    ///     non-default per-language voice packs under <c>Content/TagPatchPaks/</c> while keeping the single default
    ///     (Chinese, <c>pakchunk101</c>) voice the game requires to launch — matching the official install, which
    ///     keeps exactly one voice language. A directly-addressed <c>&lt;Res&gt;</c> file is dropped when its path is
    ///     deferred (matches a marker and no keep exception); a packed <c>&lt;Pak&gt;</c> archive is dropped only when
    ///     <em>every</em> file it bundles is deferred, because a pak blob is downloaded whole and sliced, so a pak
    ///     mixing deferred and required entries must be kept.
    /// </summary>
    private static ManifestBundle ApplyDeferredContentFilter(List<PerfectWorldResEntry> files,
        List<PerfectWorldPakEntry> paks, string[] markers, string[] keepMarkers)
    {
        if (markers.Length == 0)
            return new ManifestBundle(files, paks);

        bool IsDeferred(string path)
        {
            bool matched = false;
            foreach (string marker in markers)
                if (path.Contains(marker, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
            if (!matched)
                return false;

            foreach (string keep in keepMarkers)
                if (path.Contains(keep, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        var keptFiles = new List<PerfectWorldResEntry>(files.Count);
        long deferredBytes = 0;
        foreach (PerfectWorldResEntry entry in files)
        {
            if (IsDeferred(entry.Filename)) deferredBytes += entry.FileSize;
            else keptFiles.Add(entry);
        }

        var keptPaks = new List<PerfectWorldPakEntry>(paks.Count);
        foreach (PerfectWorldPakEntry pak in paks)
        {
            if (pak.Files.Count > 0 && pak.Files.All(f => IsDeferred(f.Filename)))
                deferredBytes += pak.FileSize;
            else
                keptPaks.Add(pak);
        }

        int droppedFiles = files.Count - keptFiles.Count;
        int droppedPaks = paks.Count - keptPaks.Count;
        if (droppedFiles > 0 || droppedPaks > 0)
            SharedStatic.InstanceLogger.LogInformation(
                "[PerfectWorldInstaller] Deferred {GB:0.00} GB of on-demand content ({Files} files + {Paks} paks) the game downloads itself (e.g. per-language voice).",
                deferredBytes / (1024.0 * 1024 * 1024), droppedFiles, droppedPaks);

        return new ManifestBundle(keptFiles, keptPaks);
    }

    /// <summary>
    ///     Brings the local install into exact agreement with the newest manifest: missing/mismatched files are
    ///     (re)downloaded from the content-addressed CDN and verified by MD5. Both directly addressed
    ///     <c>&lt;Res&gt;</c> files and the many small files packed inside <c>&lt;Pak&gt;</c> archives are handled.
    /// </summary>
    private async Task ReconcileToManifestAsync(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, bool verifyHash, CancellationToken token)
    {
        string installPath = EnsureAndGetGamePath();

        // --- Preparing: obtain remote config + manifest -------------------------------------------------
        progressStateDelegate?.Invoke(InstallProgressState.Preparing);

        PerfectWorldRemoteConfig? remote = await Manager.GetRemoteConfigAsync(true, token).ConfigureAwait(false);
        if (remote == null || string.IsNullOrEmpty(remote.ResVersion))
            throw new IOException("Unable to obtain remote config.xml.");

        ManifestBundle bundle = await GetManifestBundleAsync(token, forceRefresh: true).ConfigureAwait(false);
        List<PerfectWorldResEntry> manifest = bundle.Files;
        List<PerfectWorldPakEntry> paks = bundle.Paks;
        if (manifest.Count == 0 && paks.Count == 0)
            throw new IOException("Manifest contained no file entries.");

        // --- Verify: decide which files/paks need downloading -------------------------------------------
        // Fetch the launcher self-update manifest up-front so the verify total (game files + packed pak files +
        // launcher files) is fixed before verification begins; this lets the host's "Verifying: X / Y" count run
        // live against a stable total as each file is checked, instead of sitting frozen for the whole phase.
        PerfectWorldLauncherManifest launcherManifest =
            await GetLauncherManifestAsync(token).ConfigureAwait(false);

        int verifyTotalCount = manifest.Count + paks.Sum(p => p.Files.Count) + launcherManifest.Files.Count;
        BeginVerifyPhase(verifyTotalCount, progressDelegate, progressStateDelegate);

        int verifiedCount = 0;
        void ReportVerified(int delta) =>
            ReportProgress(0, Interlocked.Add(ref verifiedCount, delta), progressDelegate, force: false);

        var toDownload = new ConcurrentBag<PerfectWorldResEntry>();
        var paksToDownload = new ConcurrentBag<PerfectWorldPakEntry>();
        long existingBytes = 0;
        int existingCount = 0;

        await Parallel.ForEachAsync(manifest,
            new ParallelOptions { MaxDegreeOfParallelism = VerifyParallelism, CancellationToken = token },
            async (entry, ct) =>
            {
                string localPath = Path.Combine(installPath,
                    entry.Filename.Replace('/', Path.DirectorySeparatorChar));

                bool ok;
                if (!File.Exists(localPath))
                    ok = false;
                else if (new FileInfo(localPath).Length != entry.FileSize)
                    ok = false;
                else if (verifyHash)
                    ok = await CheckMd5Async(localPath, entry.Md5, ct).ConfigureAwait(false);
                else
                    ok = true; // fresh install: trust a size match to avoid hashing tens of GB

                if (ok)
                {
                    Interlocked.Add(ref existingBytes, entry.FileSize);
                    Interlocked.Increment(ref existingCount);
                }
                else
                {
                    toDownload.Add(entry);
                }

                ReportVerified(1);
            }).ConfigureAwait(false);

        await Parallel.ForEachAsync(paks,
            new ParallelOptions { MaxDegreeOfParallelism = VerifyParallelism, CancellationToken = token },
            async (pak, ct) =>
            {
                if (await IsPakCompleteAsync(pak, installPath, verifyHash, ct).ConfigureAwait(false))
                {
                    Interlocked.Add(ref existingBytes, pak.FileSize);
                    Interlocked.Add(ref existingCount, pak.Files.Count);
                }
                else
                {
                    paksToDownload.Add(pak);
                }

                ReportVerified(pak.Files.Count);
            }).ConfigureAwait(false);

        // The vendor launcher (NTELauncher\) is required at runtime: it hosts the account-login UI and drives the
        // game process, so it is installed alongside the game itself. Classify its files here so their bytes/count
        // fold into the same progress totals as the game download.
        LauncherPlan launcherPlan = await PrepareLauncherPlanAsync(installPath, verifyHash, token,
            launcherManifest, () => ReportVerified(1)).ConfigureAwait(false);
        existingBytes += launcherPlan.ExistingZipBytes;
        existingCount += launcherPlan.ExistingCount;

        long totalBytes = manifest.Sum(e => e.FileSize) + paks.Sum(p => p.FileSize) + launcherPlan.TotalZipBytes;
        int totalCount = manifest.Count + paks.Sum(p => p.Files.Count) + launcherPlan.TotalCount;

        // Verification finished: force a final "Verifying: Y / Y" report so the count visibly completes even when the
        // throttled per-file reports above didn't land on the last value (a fast size-only verify may otherwise only
        // ever show "0 / Y" before the phase flips to Download).
        ReportProgress(0, verifyTotalCount, progressDelegate, force: true);

        // --- Download -----------------------------------------------------------------------------------
        InstallProgressState downloadState = verifyHash ? InstallProgressState.Updating : InstallProgressState.Download;

        lock (_reportLock)
        {
            _progress = new InstallProgress
            {
                TotalBytesToDownload = totalBytes,
                DownloadedBytes      = existingBytes,
                TotalCountToDownload = totalCount,
                DownloadedCount      = existingCount,
                // Mirror the file counts into the state fields as well: older Collapse builds render the on-ring
                // "X / Y" label from StateCount/TotalStateToComplete (newer builds prefer the asset counts), so
                // keeping both in sync — as the official plugins do — makes the count show on every host version.
                TotalStateToComplete = totalCount,
                StateCount           = existingCount
            };
            _lastReportTick      = 0;
            _activeStateDelegate = progressStateDelegate;
            _activeReportState   = downloadState;
        }

        // Emit one combined progress+state report up front so the host populates the on-ring "X / Y" label
        // immediately. ReportProgress drives both delegates (see its note); this also seeds the correct starting
        // count when resuming a partially completed install instead of leaving the label at its "-" default.
        ReportProgress(existingBytes, existingCount, progressDelegate, force: true);

        long downloadedBytes = existingBytes;
        int downloadedCount = existingCount;

        await Parallel.ForEachAsync(toDownload,
            new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = token },
            async (entry, ct) =>
            {
                string localPath = Path.Combine(installPath,
                    entry.Filename.Replace('/', Path.DirectorySeparatorChar));
                string? dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tempPath = localPath + ".tmp";

                await DownloadContentFileAsync(entry.Md5, entry.FileSize, tempPath, ct, delta =>
                {
                    long nb = Interlocked.Add(ref downloadedBytes, delta);
                    ReportProgress(nb, Volatile.Read(ref downloadedCount), progressDelegate, force: false);
                }).ConfigureAwait(false);

                if (!await CheckMd5Async(tempPath, entry.Md5, ct).ConfigureAwait(false))
                {
                    ForceDeleteFile(tempPath);
                    throw new IOException($"MD5 mismatch for {entry.Filename}.");
                }

                ForceDeleteFile(localPath);
                File.Move(tempPath, localPath);

                int nc = Interlocked.Increment(ref downloadedCount);
                ReportProgress(Volatile.Read(ref downloadedBytes), nc, progressDelegate, force: true);
            }).ConfigureAwait(false);

        // Download + extract the packed (pak) files. A pak is fetched whole (content-addressed) and each of its
        // entries is sliced out and MD5-verified; already-correct entries inside a re-fetched pak are skipped.
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

        // Download + install the vendor launcher (NTELauncher\). Each launcher file is an individual zip on the CDN,
        // so this verifies the zip MD5, inflates it and verifies the inflated MD5 before placing it.
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
        ReportProgress(totalBytes, totalCount, progressDelegate, force: true);

        // Author the native pw_sdk patcher state so the game client accepts this directly-downloaded install
        // (records the base build as installed, no voice) instead of looping on "更新失败". No-op unless the game
        // opts in via PerfectWorldGameConfig.WritePatcherState.
        await WritePatcherStateAsync(installPath, remote, bundle, token).ConfigureAwait(false);

        Manager.WriteInstalledResVersion(remote.ResVersion);
        Manager.ClearLauncherHasUpdate();
        progressStateDelegate?.Invoke(InstallProgressState.Completed);

        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldInstaller] Reconciliation to {Version} complete ({Res} files + {Paks} paks + launcher {Launcher} files).",
            remote.ResVersion, manifest.Count, paks.Count, launcherPlan.TotalCount);
    }

    /// <summary>
    ///     Seeds the shared progress state for the Verify phase and switches the host label to "Verifying: 0 / total".
    ///     Records the state delegate and the Verify phase so that every subsequent <see cref="ReportProgress"/> call
    ///     re-asserts the state — older Collapse builds only (re)build the on-ring "X / Y" label inside their status
    ///     callback (driven by the state delegate), so without re-asserting on each report the count would freeze for
    ///     the whole verification pass. Byte fields are left at 0: nothing is downloaded while verifying, so the byte
    ///     bar reads 0% until the download phase sets the real totals — the file count is the meaningful verify signal.
    /// </summary>
    private void BeginVerifyPhase(int verifyTotalCount, InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate)
    {
        lock (_reportLock)
        {
            _progress = new InstallProgress
            {
                TotalCountToDownload = verifyTotalCount,
                TotalStateToComplete = verifyTotalCount
            };
            _lastReportTick      = 0;
            _activeStateDelegate = progressStateDelegate;
            _activeReportState   = InstallProgressState.Verify;
        }

        // Set the label immediately. This also covers the progressDelegate == null case, where ReportProgress is a
        // no-op and would otherwise never switch the host into the Verify state.
        progressStateDelegate?.Invoke(InstallProgressState.Verify);
        ReportProgress(0, 0, progressDelegate, force: true);
    }

    private void ReportProgress(long downloadedBytes, int downloadedCount,
        InstallProgressDelegate? progressDelegate, bool force)
    {
        if (progressDelegate == null) return;

        long now = Environment.TickCount64;
        lock (_reportLock)
        {
            if (!force && now - _lastReportTick < ProgressThrottleMs) return;
            _lastReportTick = now;

            _progress.DownloadedBytes = downloadedBytes;
            _progress.DownloadedCount = downloadedCount;
            // Keep the state counter in lockstep with the asset counter so the host's file-count display advances
            // regardless of whether it reads DownloadedCount or StateCount (see the initializer note above).
            _progress.StateCount      = downloadedCount;
            progressDelegate(in _progress);

            // Re-assert the install phase on every report. Older Collapse builds only (re)build the on-ring
            // "X / Y" label inside their status callback, which is driven by this state delegate — without this
            // the label would freeze at the count captured when the phase began. Reporting both delegates together
            // on every progress point mirrors the official plugins and keeps the count live on every host version.
            _activeStateDelegate?.Invoke(_activeReportState);
        }
    }

    /// <summary>
    ///     Downloads a small resource fully into memory, trying every configured game-resource CDN root.
    /// </summary>
    private Task<byte[]> DownloadBytesWithFallbackAsync(Func<string, string> urlBuilder, CancellationToken token)
    {
        return DownloadBytesWithFallbackAsync(Manager.Config.GameResCdnUrls, urlBuilder, token);
    }

    /// <summary>
    ///     Downloads a small resource fully into memory, trying every CDN root in <paramref name="cdnRoots"/> in order.
    /// </summary>
    private async Task<byte[]> DownloadBytesWithFallbackAsync(string[] cdnRoots, Func<string, string> urlBuilder,
        CancellationToken token)
    {
        Exception? lastError = null;
        foreach (string cdnRoot in cdnRoots)
        {
            string url = urlBuilder(cdnRoot);
            try
            {
                return await _downloadHttpClient.GetByteArrayAsync(url, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    "[PerfectWorldInstaller] Failed to download {Url}: {Msg}", url, ex.Message);
            }
        }

        throw new IOException("All CDNs failed for a manifest download.", lastError);
    }

    private static byte[] ExtractZipEntry(byte[] zipBytes, string entryName)
    {
        using var ms = new MemoryStream(zipBytes, false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
            throw new InvalidDataException($"Zip does not contain '{entryName}'.");

        using Stream entryStream = entry.Open();
        using var output = new MemoryStream();
        entryStream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    ///     Returns the decoded remote catalog XML (<c>ResList.bin</c>) for <paramref name="resVersion"/>, reusing the
    ///     copy retained by <see cref="GetManifestBundleAsync"/> when it is for the same version and otherwise
    ///     fetching and decoding it afresh. Used by the patcher-state writer to copy per-file block checksums.
    /// </summary>
    private async Task<string> GetCatalogXmlAsync(string resVersion, CancellationToken token)
    {
        if (_cachedCatalogXml is { } cached && _cachedManifestVersion == resVersion)
            return cached;

        PerfectWorldGameConfig config = Manager.Config;
        byte[] zipBytes = await DownloadBytesWithFallbackAsync(
            cdn => config.BuildResListZipUrl(cdn, resVersion), token).ConfigureAwait(false);
        byte[] resListBin = ExtractZipEntry(zipBytes, "ResList.bin");
        return PatcherXml0.DecodeToXml(resListBin, config.AppId);
    }

    /// <summary>
    ///     Writes the finalized native <c>pw_sdk PatcherSDK</c> state (<c>config.xml</c> + <c>ResList.xml</c> +
    ///     <c>tmp/client.xml</c>) for the just-completed install/update when the game requires it (see
    ///     <see cref="PerfectWorldGameConfig.WritePatcherState"/> and <see cref="PerfectWorldPatcherState"/>). The
    ///     state records the installed base build together with the default-language voice section the plugin keeps,
    ///     so the in-game client no longer sees a zero local version (which caused the "更新失败" loop), plays the
    ///     default voice, and offers each deferred language for on-demand download. The flat <c>tmp/client.xml</c> is
    ///     the client manifest the in-game updater re-reads on return to login; without it that updater sees an empty
    ///     client list and loops on "更新失败" even when the launcher check passed. Best-effort: any failure is logged
    ///     but does not fail the install — the downloaded files are all present and the state can be regenerated by a
    ///     subsequent verify/repair — while genuine caller cancellation still propagates.
    /// </summary>
    private async Task WritePatcherStateAsync(string installPath, PerfectWorldRemoteConfig remote,
        ManifestBundle bundle, CancellationToken token)
    {
        PerfectWorldGameConfig config = Manager.Config;
        if (!config.WritePatcherState)
            return;

        try
        {
            string catalogXml = await GetCatalogXmlAsync(remote.ResVersion, token).ConfigureAwait(false);

            string resListXml = PerfectWorldPatcherState.BuildLocalResListXml(
                bundle.Files, bundle.Paks, catalogXml, remote.ResVersion, installPath,
                out int resCount, out string tag, out var voiceSections, out string clientXml);
            string configXml = PerfectWorldPatcherState.BuildLocalConfigXml(
                config.GameResBranch, remote, tag, resCount, voiceSections);

            string stateDir = Path.Combine(installPath, config.LauncherRootDirName, "UserData", "Patcher", "PatcherSDK");
            Directory.CreateDirectory(stateDir);
            string tmpDir = Path.Combine(stateDir, "tmp");
            Directory.CreateDirectory(tmpDir);

            string configPath = Path.Combine(stateDir, "config.xml");
            string resListPath = Path.Combine(stateDir, "ResList.xml");
            string clientPath = Path.Combine(tmpDir, "client.xml");

            await File.WriteAllTextAsync(configPath, configXml, Utf8NoBom, token).ConfigureAwait(false);
            byte[] resListEncoded = PatcherXml0.EncodeFromXml(resListXml, config.AppId);
            await File.WriteAllBytesAsync(resListPath, resListEncoded, token).ConfigureAwait(false);
            byte[] clientEncoded = PatcherXml0.EncodeFromXml(clientXml, config.AppId);
            await File.WriteAllBytesAsync(clientPath, clientEncoded, token).ConfigureAwait(false);

            // Remove any stale write-ahead ".tmp" backups so the patcher's crash-safe reader never prefers an
            // outdated sibling over the state we just wrote.
            ForceDeleteFile(configPath + ".tmp");
            ForceDeleteFile(resListPath + ".tmp");
            ForceDeleteFile(clientPath + ".tmp");

            SharedStatic.InstanceLogger.LogInformation(
                "[PerfectWorldInstaller] Wrote pw_sdk patcher state ({Count} base files, {Voice} installed voice section(s)) to {Dir}.",
                resCount, voiceSections.Count, stateDir);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PerfectWorldInstaller] Failed to write pw_sdk patcher state: {Msg}", ex.Message);
        }
    }
}
