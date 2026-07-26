using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.PerfectWorld.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management.Api;

/// <summary>
///     Background (image + video) provider for Perfect World <c>pw_sdk</c> launchers.
///     <para>
///         The official launcher stores its home background under the self-update tree:
///         <c>Version.ini → FileListURL → AllFiles.xml</c> lists a <c>bgimgs/</c> folder whose files are
///         zip-compressed and are described by a small <c>config.json</c> (which image / video to show).
///         Each asset URL is <c>{dir(FileListURL)}{File.Path}.zip</c>.
///     </para>
///     <para>
///         Collapse decides image-vs-video by the served path's file extension and opens local files
///         directly, so this provider downloads + unzips the chosen assets into a local cache during
///         <see cref="InitAsync"/> and serves absolute local <c>.jpg</c>/<c>.mp4</c> paths. Index 0 is the
///         static image (an always-visible default); when available the video is offered at index 1 and is
///         user-switchable via Collapse's background switcher.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class PerfectWorldLauncherApiMedia : LauncherApiMediaBase
{
    private readonly PerfectWorldGameConfig _config;
    private readonly bool             _enableVideo;

    private string? _backgroundImagePath;
    private string? _backgroundVideoPath;

    public PerfectWorldLauncherApiMedia(PerfectWorldGameConfig config, bool enableVideoBackground = true)
    {
        _config      = config;
        _enableVideo = enableVideoBackground;
    }

    [field: AllowNull]
    [field: MaybeNull]
    protected override HttpClient ApiResponseHttpClient { get; set; } = new PluginHttpClientBuilder()
        .SetAllowedDecompression(DecompressionMethods.None)
        .AllowRedirections()
        .AllowUntrustedCert()
        .SetUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CollapsePlugin/1.0")
        .Create();

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        try
        {
            // 1. Launcher Version.ini -> FileListURL (try every configured launcher CDN root).
            string? fileListUrl = null;
            foreach (string cdnRoot in _config.LauncherCdnUrls)
            {
                try
                {
                    string iniText = await ApiResponseHttpClient
                        .GetStringAsync(_config.BuildLauncherVersionIniUrl(cdnRoot), token)
                        .ConfigureAwait(false);

                    Match m = FileListUrlRegex().Match(iniText);
                    if (m.Success)
                    {
                        fileListUrl = m.Groups[1].Value.Trim();
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[PerfectWorldMedia] Version.ini fetch failed for {Cdn}: {Msg}", cdnRoot, ex.Message);
                }
            }

            if (string.IsNullOrEmpty(fileListUrl))
            {
                SharedStatic.InstanceLogger.LogWarning("[PerfectWorldMedia] Could not resolve FileListURL; serving no background.");
                return 0;
            }

            // The bgimgs assets are addressed relative to the directory that holds AllFiles.xml.
            int lastSlash = fileListUrl.LastIndexOf('/');
            string baseUrl = lastSlash > 0 ? fileListUrl[..lastSlash] : fileListUrl;

            // 2. AllFiles.xml -> map of bgimgs entries by file name.
            string allFilesXml = await ApiResponseHttpClient.GetStringAsync(fileListUrl, token).ConfigureAwait(false);
            Dictionary<string, BgFileEntry> bgFiles = ParseBgImgsEntries(allFilesXml);
            if (bgFiles.Count == 0)
            {
                SharedStatic.InstanceLogger.LogWarning("[PerfectWorldMedia] No bgimgs entries found in AllFiles.xml.");
                return 0;
            }

            // 3. config.json -> which image / video / static-fallback to use.
            string imageFile  = "bg_0.jpg";
            string staticFile = "bg_0.jpg";
            string videoFile  = "bg.mp4";
            if (bgFiles.TryGetValue("config.json", out BgFileEntry configEntry))
            {
                try
                {
                    byte[] configZip   = await ApiResponseHttpClient
                        .GetByteArrayAsync(BuildAssetUrl(baseUrl, configEntry.Path), token).ConfigureAwait(false);
                    byte[] configBytes = PerfectWorldZipUtility.ExtractSingleEntry(configZip, "config.json");
                    ParseBgConfig(configBytes, ref imageFile, ref staticFile, ref videoFile);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[PerfectWorldMedia] config.json parse failed, using defaults: {Msg}", ex.Message);
                }
            }

            string cacheDir = Path.Combine(Path.GetTempPath(), "CollapsePerfectWorldMedia", _config.AppId);
            Directory.CreateDirectory(cacheDir);

            // 4. Cache the static image (index 0). Fall back to the no-video image if the primary is absent.
            _backgroundImagePath =
                await CacheAssetAsync(baseUrl, bgFiles, imageFile, cacheDir, token).ConfigureAwait(false)
                ?? await CacheAssetAsync(baseUrl, bgFiles, staticFile, cacheDir, token).ConfigureAwait(false);

            // 5. Cache the video (index 1, best-effort, larger download) when enabled.
            if (_enableVideo && !string.IsNullOrEmpty(videoFile))
            {
                _backgroundVideoPath = await CacheAssetAsync(baseUrl, bgFiles, videoFile, cacheDir, token)
                    .ConfigureAwait(false);
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[PerfectWorldMedia] Background ready. Image='{Image}', Video='{Video}'",
                _backgroundImagePath ?? "<none>", _backgroundVideoPath ?? "<none>");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("[PerfectWorldMedia] Failed to init media: {Ex}", ex);
            return 0; // Best-effort: on failure Collapse falls back to the plugin's embedded poster.
        }
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        result = LauncherBackgroundFlag.None;
        if (!string.IsNullOrEmpty(_backgroundImagePath)) result |= LauncherBackgroundFlag.TypeIsImage;
        if (!string.IsNullOrEmpty(_backgroundVideoPath)) result |= LauncherBackgroundFlag.TypeIsVideo;
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        // Order matters: index 0 is the static image (reliable default shown on Home), index 1 is the
        // switchable video. Collapse classifies each entry by its path's file extension.
        string[] paths = new string[2];
        int total = 0;
        if (!string.IsNullOrEmpty(_backgroundImagePath)) paths[total++] = _backgroundImagePath!;
        if (!string.IsNullOrEmpty(_backgroundVideoPath)) paths[total++] = _backgroundVideoPath!;

        if (total == 0)
        {
            handle       = nint.Zero;
            count        = 0;
            isDisposable = false;
            isAllocated  = false;
            return;
        }

        PluginDisposableMemory<LauncherPathEntry> memory = PluginDisposableMemory<LauncherPathEntry>.Alloc(total);
        for (int i = 0; i < total; i++)
        {
            memory[i].Write(paths[i], Span<byte>.Empty);
        }

        handle       = memory.AsSafePointer();
        count        = total;
        isDisposable = true;
        isAllocated  = true;
    }

    public override void GetBackgroundSpriteFps(out float result) => result = 60f;

    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle       = nint.Zero;
        count        = 0;
        isDisposable = false;
        isAllocated  = false;
    }

    /// <summary>Downloads + unzips a bgimgs asset into the cache, returning its absolute local path (or null).</summary>
    private async Task<string?> CacheAssetAsync(string baseUrl, Dictionary<string, BgFileEntry> bgFiles,
        string fileName, string cacheDir, CancellationToken token)
    {
        if (!bgFiles.TryGetValue(fileName, out BgFileEntry entry))
        {
            return null;
        }

        string ext       = Path.GetExtension(fileName);
        string localPath = Path.Combine(cacheDir, entry.Md5 + ext);

        // Content-addressed by md5: a matching, correctly-sized file is already the right content.
        if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size)
        {
            return localPath;
        }

        try
        {
            byte[] zipBytes = await ApiResponseHttpClient
                .GetByteArrayAsync(BuildAssetUrl(baseUrl, entry.Path), token).ConfigureAwait(false);
            byte[] fileBytes = PerfectWorldZipUtility.ExtractSingleEntry(zipBytes, fileName);

            if (entry.Size > 0 && fileBytes.Length != entry.Size)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[PerfectWorldMedia] Size mismatch for {File}: expected {Expected}, got {Actual}.",
                    fileName, entry.Size, fileBytes.Length);
            }

            await File.WriteAllBytesAsync(localPath, fileBytes, token).ConfigureAwait(false);
            return localPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SharedStatic.InstanceLogger.LogWarning("[PerfectWorldMedia] Failed to cache {File}: {Msg}", fileName, ex.Message);
            return null;
        }
    }

    private static string BuildAssetUrl(string baseUrl, string path)
    {
        // File.Path is absolute-rooted (leading '/'); the on-CDN blob is zip-wrapped.
        return $"{baseUrl}{path}.zip";
    }

    private static Dictionary<string, BgFileEntry> ParseBgImgsEntries(string xml)
    {
        var result = new Dictionary<string, BgFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FileEntryRegex().Matches(xml))
        {
            string path = m.Groups["path"].Value;
            if (path.IndexOf("/bgimgs/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string name = path[(path.LastIndexOf('/') + 1)..];
            long.TryParse(m.Groups["size"].Value, out long size);
            result[name] = new BgFileEntry(path, m.Groups["md5"].Value, size);
        }

        return result;
    }

    private static void ParseBgConfig(byte[] configBytes, ref string imageFile, ref string staticFile,
        ref string videoFile)
    {
        using var doc = JsonDocument.Parse(configBytes);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("imgs", out JsonElement imgs) &&
            imgs.ValueKind == JsonValueKind.Array &&
            imgs.GetArrayLength() > 0 &&
            imgs[0].TryGetProperty("file", out JsonElement file) &&
            file.ValueKind == JsonValueKind.String)
        {
            imageFile = file.GetString() ?? imageFile;
        }

        if (root.TryGetProperty("noVideoBg", out JsonElement noVideoBg) &&
            noVideoBg.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(noVideoBg.GetString()))
        {
            staticFile = noVideoBg.GetString()!;
        }
        else
        {
            staticFile = imageFile;
        }

        if (root.TryGetProperty("video", out JsonElement video) &&
            video.ValueKind == JsonValueKind.String)
        {
            videoFile = video.GetString() ?? videoFile;
        }
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        ApiResponseHttpClient?.Dispose();
        base.Dispose();
    }

    private readonly record struct BgFileEntry(string Path, string Md5, long Size);

    [GeneratedRegex(@"FileListURL\s*=\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileListUrlRegex();

    [GeneratedRegex(@"<File\s+Checksum=""(?<md5>[0-9A-Fa-f]+)""\s+Path=""(?<path>[^""]+)""\s+Size=""(?<size>\d+)""")]
    private static partial Regex FileEntryRegex();
}
