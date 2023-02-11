﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class ExportAssetDownloader
{
    private readonly string _workingDirPath;
    private readonly bool _reuse;

    // File paths of the previously downloaded assets
    private readonly Dictionary<string, string> _pathCache = new(StringComparer.Ordinal);

    public ExportAssetDownloader(string workingDirPath, bool reuse)
    {
        _workingDirPath = workingDirPath;
        _reuse = reuse;
    }

    public async ValueTask<string> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (_pathCache.TryGetValue(url, out var cachedFilePath))
            return cachedFilePath;

        var fileName = GetFileNameFromUrl(url);
        var filePath = Path.Combine(_workingDirPath, fileName);

        // Reuse existing files if we're allowed to
        if (!_reuse || !File.Exists(filePath))
        {
            Directory.CreateDirectory(_workingDirPath);

            await Http.ResiliencePolicy.ExecuteAsync(async () =>
            {
                // Download the file
                using var response = await Http.Client.GetAsync(url, cancellationToken);
                await using (var output = File.Create(filePath))
                    await response.Content.CopyToAsync(output, cancellationToken);

                // Try to set the file date according to the last-modified header
                try
                {
                    var lastModified = response.Content.Headers.TryGetValue("Last-Modified")?.Pipe(s =>
                        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                            ? date
                            : (DateTimeOffset?) null
                    );

                    if (lastModified is not null)
                    {
                        File.SetCreationTimeUtc(filePath, lastModified.Value.UtcDateTime);
                        File.SetLastWriteTimeUtc(filePath, lastModified.Value.UtcDateTime);
                        File.SetLastAccessTimeUtc(filePath, lastModified.Value.UtcDateTime);
                    }
                }
                catch
                {
                    // This can apparently fail for some reason.
                    // https://github.com/Tyrrrz/DiscordChatExporter/issues/585
                    // Updating file dates is not a critical task, so we'll just
                    // ignore exceptions thrown here.
                }
            });
        }

        return _pathCache[url] = filePath;
    }
}

internal partial class ExportAssetDownloader
{
    private static string GetUrlHash(string url) => SHA256
        .HashData(Encoding.UTF8.GetBytes(url))
        .ToHex()
        // 5 chars ought to be enough for anybody
        .Truncate(5);

    private static string GetFileNameFromUrl(string url)
    {
        var urlHash = GetUrlHash(url);

        // Try to extract file name from URL
        var fileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);

        // Probably not a file extension, just a dot in a long file name
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/708
        if (fileExtension.Length > 41)
        {
            fileNameWithoutExtension = fileName;
            fileExtension = "";
        }

        return PathEx.EscapeFileName(fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension);
    }
}