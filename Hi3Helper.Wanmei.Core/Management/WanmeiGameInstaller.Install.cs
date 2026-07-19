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
using Hi3Helper.Wanmei.Core.Management.Api;
using Hi3Helper.Wanmei.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Wanmei.Core.Management;

public partial class WanmeiGameInstaller
{
    private const int ProgressThrottleMs = 500;

    private List<WanmeiResEntry>? _cachedManifest;
    private string? _cachedManifestVersion;

    private readonly Lock _reportLock = new();
    private InstallProgress _progress;
    private long _lastReportTick;

    /// <summary>
    ///     Fetches and decodes the <c>ResList</c> manifest for the current remote resource version (cached).
    /// </summary>
    private async Task<List<WanmeiResEntry>> GetManifestAsync(CancellationToken token, bool forceRefresh = false)
    {
        WanmeiRemoteConfig? remote = await Manager.GetRemoteConfigAsync(forceRefresh, token).ConfigureAwait(false);
        if (remote == null || string.IsNullOrEmpty(remote.ResVersion))
            throw new IOException("Unable to obtain remote config.xml for manifest.");

        if (!forceRefresh && _cachedManifest != null && _cachedManifestVersion == remote.ResVersion)
            return _cachedManifest;

        WanmeiGameConfig config = Manager.Config;
        byte[] zipBytes = await DownloadBytesWithFallbackAsync(
            cdn => config.BuildResListZipUrl(cdn, remote.ResVersion), token).ConfigureAwait(false);

        byte[] resListBin = ExtractZipEntry(zipBytes, "ResList.bin");
        string xml = PatcherXml0.DecodeToXml(resListBin, config.AppId);
        List<WanmeiResEntry> entries = WanmeiManifest.ParseResList(xml);

        SharedStatic.InstanceLogger.LogInformation(
            "[WanmeiInstaller] Manifest {Version}: {Count} entries.", remote.ResVersion, entries.Count);

        _cachedManifest = entries;
        _cachedManifestVersion = remote.ResVersion;
        return entries;
    }

    /// <summary>
    ///     Brings the local install into exact agreement with the newest manifest: missing/mismatched files are
    ///     (re)downloaded from the content-addressed CDN and verified by MD5.
    /// </summary>
    private async Task ReconcileToManifestAsync(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, bool verifyHash, CancellationToken token)
    {
        string installPath = EnsureAndGetGamePath();

        // --- Preparing: obtain remote config + manifest -------------------------------------------------
        progressStateDelegate?.Invoke(InstallProgressState.Preparing);

        WanmeiRemoteConfig? remote = await Manager.GetRemoteConfigAsync(true, token).ConfigureAwait(false);
        if (remote == null || string.IsNullOrEmpty(remote.ResVersion))
            throw new IOException("Unable to obtain remote config.xml.");

        List<WanmeiResEntry> manifest = await GetManifestAsync(token, forceRefresh: true).ConfigureAwait(false);
        if (manifest.Count == 0)
            throw new IOException("Manifest contained no file entries.");

        // --- Verify: decide which files need downloading ------------------------------------------------
        progressStateDelegate?.Invoke(InstallProgressState.Verify);

        var toDownload = new ConcurrentBag<WanmeiResEntry>();
        long existingBytes = 0;

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
                    Interlocked.Add(ref existingBytes, entry.FileSize);
                else
                    toDownload.Add(entry);
            }).ConfigureAwait(false);

        long totalBytes = manifest.Sum(e => e.FileSize);
        int alreadyHaveCount = manifest.Count - toDownload.Count;

        // --- Download -----------------------------------------------------------------------------------
        progressStateDelegate?.Invoke(verifyHash ? InstallProgressState.Updating : InstallProgressState.Download);

        lock (_reportLock)
        {
            _progress = new InstallProgress
            {
                TotalBytesToDownload = totalBytes,
                DownloadedBytes      = existingBytes,
                TotalCountToDownload = manifest.Count,
                DownloadedCount      = alreadyHaveCount
            };
            _lastReportTick = 0;
        }

        long downloadedBytes = existingBytes;
        int downloadedCount = alreadyHaveCount;

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

        // --- Completion ---------------------------------------------------------------------------------
        ReportProgress(totalBytes, manifest.Count, progressDelegate, force: true);

        Manager.WriteInstalledResVersion(remote.ResVersion);
        progressStateDelegate?.Invoke(InstallProgressState.Completed);

        SharedStatic.InstanceLogger.LogInformation(
            "[WanmeiInstaller] Reconciliation to {Version} complete ({Count} files).",
            remote.ResVersion, manifest.Count);
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
    private async Task<byte[]> DownloadBytesWithFallbackAsync(Func<string, string> urlBuilder,
        CancellationToken token)
    {
        Exception? lastError = null;
        foreach (string cdnRoot in Manager.Config.GameResCdnUrls)
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
                    "[WanmeiInstaller] Failed to download {Url}: {Msg}", url, ex.Message);
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
