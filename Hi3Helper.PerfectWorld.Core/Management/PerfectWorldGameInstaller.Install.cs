using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

    /// <summary>Concurrency for pak downloads; kept lower than file downloads because pak blobs are large.</summary>
    private const int PakDownloadParallelism = 2;

    /// <summary>Decrypted manifest split into directly content-addressed files and packed pak archives.</summary>
    internal sealed record ManifestBundle(List<PerfectWorldResEntry> Files, List<PerfectWorldPakEntry> Paks);

    private ManifestBundle? _cachedManifest;
    private string? _cachedManifestVersion;

    private readonly Lock _reportLock = new();
    private InstallProgress _progress;
    private long _lastReportTick;

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

        if (!forceRefresh && _cachedManifest != null && _cachedManifestVersion == remote.ResVersion)
            return _cachedManifest;

        PerfectWorldGameConfig config = Manager.Config;
        byte[] zipBytes = await DownloadBytesWithFallbackAsync(
            cdn => config.BuildResListZipUrl(cdn, remote.ResVersion), token).ConfigureAwait(false);

        byte[] resListBin = ExtractZipEntry(zipBytes, "ResList.bin");
        string xml = PatcherXml0.DecodeToXml(resListBin, config.AppId);
        List<PerfectWorldResEntry> files = PerfectWorldManifest.ParseResList(xml);
        List<PerfectWorldPakEntry> paks = PerfectWorldManifest.ParsePackages(xml);

        long packedCount = 0;
        foreach (PerfectWorldPakEntry pak in paks) packedCount += pak.Files.Count;

        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldInstaller] Manifest {Version}: {Res} direct files + {Paks} paks ({Packed} packed files) = {Total} total.",
            remote.ResVersion, files.Count, paks.Count, packedCount, files.Count + packedCount);

        var bundle = new ManifestBundle(files, paks);
        _cachedManifest = bundle;
        _cachedManifestVersion = remote.ResVersion;
        return bundle;
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
        progressStateDelegate?.Invoke(InstallProgressState.Verify);

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
            }).ConfigureAwait(false);

        // The vendor launcher (NTELauncher\) is required at runtime: it hosts the account-login UI and drives the
        // game process, so it is installed alongside the game itself. Classify its files here so their bytes/count
        // fold into the same progress totals as the game download.
        LauncherPlan launcherPlan = await PrepareLauncherPlanAsync(installPath, verifyHash, token).ConfigureAwait(false);
        existingBytes += launcherPlan.ExistingZipBytes;
        existingCount += launcherPlan.ExistingCount;

        long totalBytes = manifest.Sum(e => e.FileSize) + paks.Sum(p => p.FileSize) + launcherPlan.TotalZipBytes;
        int totalCount = manifest.Count + paks.Sum(p => p.Files.Count) + launcherPlan.TotalCount;

        // --- Download -----------------------------------------------------------------------------------
        progressStateDelegate?.Invoke(verifyHash ? InstallProgressState.Updating : InstallProgressState.Download);

        lock (_reportLock)
        {
            _progress = new InstallProgress
            {
                TotalBytesToDownload = totalBytes,
                DownloadedBytes      = existingBytes,
                TotalCountToDownload = totalCount,
                DownloadedCount      = existingCount
            };
            _lastReportTick = 0;
        }

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

        Manager.WriteInstalledResVersion(remote.ResVersion);
        progressStateDelegate?.Invoke(InstallProgressState.Completed);

        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldInstaller] Reconciliation to {Version} complete ({Res} files + {Paks} paks + launcher {Launcher} files).",
            remote.ResVersion, manifest.Count, paks.Count, launcherPlan.TotalCount);
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
            progressDelegate(in _progress);
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
}
