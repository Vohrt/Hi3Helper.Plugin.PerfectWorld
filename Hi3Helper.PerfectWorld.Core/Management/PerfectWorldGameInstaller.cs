using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.PerfectWorld.Core.Management.Api;
using Hi3Helper.PerfectWorld.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management;

/// <summary>
///     <see cref="IGameInstaller"/> implementation for Perfect World pw_sdk titles. Downloads the
///     content-addressed game resources described by the decrypted <c>ResList</c> manifest.
/// </summary>
[GeneratedComClass]
public partial class PerfectWorldGameInstaller : GameInstallerBase
{
    private const int DownloadParallelism = 4;
    private const int VerifyParallelism = 8;
    private const int BufferSize = 1 << 20; // 1 MiB

    private readonly HttpClient _downloadHttpClient;

    public PerfectWorldGameInstaller(IGameManager? gameManager) : base(gameManager)
    {
        _downloadHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowRedirections()
            .AllowUntrustedCert()
            .AllowCookies()
            .Create();
    }

    private PerfectWorldGameManager Manager =>
        GameManager as PerfectWorldGameManager ??
        throw new InvalidOperationException("GameManager is not a PerfectWorldGameManager.");

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        return await Manager.InitAsyncInner(true, token).ConfigureAwait(false);
    }

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        // Return the manifest total (sum of every content-addressed file plus pak blob). This is the honest
        // installed size and, crucially, is derived from the SAME manifest as GetGameDownloadedSizeAsyncInner so
        // the host's "remaining = total - downloaded" is internally consistent and can never go negative. (The
        // vendor config.xml <ResSize> omits newly added sections such as pakchunk101, which previously skewed it.)
        ManifestBundle bundle = await GetManifestBundleAsync(token).ConfigureAwait(false);

        long total = 0;
        foreach (PerfectWorldResEntry entry in bundle.Files)
            total += entry.FileSize;
        foreach (PerfectWorldPakEntry pak in bundle.Paks)
            total += pak.FileSize;

        return total;
    }

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        string installPath = EnsureAndGetGamePath();
        ManifestBundle bundle = await GetManifestBundleAsync(token).ConfigureAwait(false);

        // For an update, the host displays (GetGameSize - GetGameDownloadedSize) as the amount left to transfer.
        // The vendor ships block-level HDiffPatch deltas (lastdiff.bin), so a changed multi-GB pak only needs a
        // small patch rather than a full re-download. Credit those savings here so the shown figure matches what
        // StartUpdateAsync actually downloads. Without this every changed pak is costed at full size and a small
        // incremental update looks like a tens-of-GB download. The index is best-effort: if the patch manifest is
        // unavailable it stays null and we fall back to the plain size-only accounting.
        Dictionary<string, Dictionary<long, long>>? patchByNewMd5 =
            gameInstallerKind == GameInstallerKind.Update
                ? await TryBuildPatchSavingsIndexAsync(token).ConfigureAwait(false)
                : null;

        long downloaded = 0;

        foreach (PerfectWorldResEntry entry in bundle.Files)
        {
            string localPath = Path.Combine(installPath, entry.Filename.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(localPath))
                continue;

            long localSize = new FileInfo(localPath).Length;
            if (localSize == entry.FileSize)
            {
                // Size already matches the target revision: treat as present (no hashing on the size query path).
                downloaded += entry.FileSize;
            }
            else if (patchByNewMd5 != null &&
                     patchByNewMd5.TryGetValue(entry.Md5, out Dictionary<long, long>? byOldSize) &&
                     byOldSize.TryGetValue(localSize, out long patchSize) &&
                     patchSize < entry.FileSize)
            {
                // A delta turns the local (previous) revision into this file: only the patch bytes transfer, so the
                // remainder (FileSize - patchSize) counts as already "downloaded".
                downloaded += entry.FileSize - patchSize;
            }
        }

        foreach (PerfectWorldPakEntry pak in bundle.Paks)
        {
            if (await IsPakCompleteAsync(pak, installPath, verifyHash: false, token).ConfigureAwait(false))
                downloaded += pak.FileSize;
        }

        return downloaded;
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return ReconcileToManifestAsync(progressDelegate, progressStateDelegate, verifyHash: false, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // Prefer HDiffPatch block-level deltas (download a small binary patch and apply it locally); any file that
        // has no usable patch — or whose patch fails — transparently falls back to a full content-addressed download.
        return DeltaUpdateAsync(progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // No dedicated preload channel for pw_sdk titles.
        progressStateDelegate?.Invoke(InstallProgressState.Completed);
        return Task.CompletedTask;
    }

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath)) return Task.CompletedTask;

        try
        {
            string clientDir = Path.Combine(installPath, "Client");
            if (Directory.Exists(clientDir)) Directory.Delete(clientDir, true);

            string launcherDir = Path.Combine(installPath, LauncherRootDirName);
            if (Directory.Exists(launcherDir)) Directory.Delete(launcherDir, true);

            string pakStagingDir = Path.Combine(installPath, PakStagingDirName);
            if (Directory.Exists(pakStagingDir)) Directory.Delete(pakStagingDir, true);

            string stateFile = Path.Combine(installPath, PerfectWorldGameManager.StateFileName);
            if (File.Exists(stateFile)) File.Delete(stateFile);

            string legacyStateFile = Path.Combine(installPath, PerfectWorldGameManager.LegacyStateFileName);
            if (File.Exists(legacyStateFile)) File.Delete(legacyStateFile);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("[PerfectWorldInstaller] Uninstall failed: {Msg}", ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Downloads a content-addressed file, trying every configured CDN root and resuming/retrying per CDN.
    /// </summary>
    private async Task DownloadContentFileAsync(string md5, long expectedSize, string tempPath,
        CancellationToken token, Action<long> onProgress)
    {
        PerfectWorldGameConfig config = Manager.Config;
        Exception? lastError = null;

        for (int cdnIndex = 0; cdnIndex < config.GameResCdnUrls.Length; cdnIndex++)
        {
            string url = config.BuildContentUrl(config.GameResCdnUrls[cdnIndex], md5, expectedSize);
            try
            {
                await DownloadFileAsync(url, tempPath, expectedSize, token, onProgress).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    "[PerfectWorldInstaller] CDN #{Index} failed for {Md5}: {Msg}", cdnIndex, md5, ex.Message);
            }
        }

        throw new IOException($"All CDNs failed for content {md5}.", lastError);
    }

    /// <summary>
    ///     Downloads a single URL to <paramref name="tempPath"/> with HTTP range resume and retry.
    /// </summary>
    private async Task DownloadFileAsync(string url, string tempPath, long expectedSize, CancellationToken token,
        Action<long> onProgress)
    {
        const int maxRetries = 3;
        long totalReported = 0;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                long existingLength = 0;
                if (File.Exists(tempPath))
                {
                    existingLength = new FileInfo(tempPath).Length;
                    if (existingLength > expectedSize)
                    {
                        ForceDeleteFile(tempPath);
                        existingLength = 0;
                    }
                }

                long diff = existingLength - totalReported;
                if (diff != 0)
                {
                    onProgress(diff);
                    totalReported += diff;
                }

                if (existingLength == expectedSize) return;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingLength > 0)
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);

                using HttpResponseMessage response = await _downloadHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    ForceDeleteFile(tempPath);
                    if (totalReported > 0)
                    {
                        onProgress(-totalReported);
                        totalReported = 0;
                    }
                    existingLength = 0;
                }

                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var fs = new FileStream(tempPath,
                    existingLength > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, BufferSize, true);

                var buffer = new byte[BufferSize];
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    onProgress(read);
                    totalReported += read;
                }

                await fs.FlushAsync(token).ConfigureAwait(false);

                if (fs.Length != expectedSize)
                    throw new IOException($"Size mismatch. Expected {expectedSize}, got {fs.Length}.");

                return;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<bool> CheckMd5Async(string filePath, string expectedMd5, CancellationToken token)
    {
        if (!File.Exists(filePath)) return false;

        using var md5 = MD5.Create();
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string> ComputeMd5Async(string filePath, CancellationToken token)
    {
        using var md5 = MD5.Create();
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    internal static void ForceDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try { File.SetAttributes(filePath, FileAttributes.Normal); }
        catch { /* ignored */ }

        File.Delete(filePath);
    }

    public override void Dispose()
    {
        _downloadHttpClient.Dispose();
        _patchApplyLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
