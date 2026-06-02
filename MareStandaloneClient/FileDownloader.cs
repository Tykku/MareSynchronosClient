using K4os.Compression.LZ4.Legacy;
using MareSynchronos.API.Dto;
using MareSynchronos.API.Dto.Files;
using MareSynchronos.API.Routes;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;

namespace MareStandaloneClient;

public class FileDownloader
{
    private readonly ILogger<FileDownloader> _logger;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>> _getToken;
    private readonly Uri _cdnUri;
    private readonly int _tzOffsetMinutes;

    public FileDownloader(ILoggerFactory loggerFactory, Uri cdnUri, Func<CancellationToken, Task<string?>> getToken)
    {
        _logger = loggerFactory.CreateLogger<FileDownloader>();
        _cdnUri = cdnUri;
        _getToken = getToken;
        _tzOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MareStandaloneClient/1.0");
    }
    
    public async Task<List<string>> DownloadAsync(IReadOnlyList<string> hashes, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        _logger.LogInformation("Requesting metadata for {count} hash(es)...", hashes.Count);
        var fileDtos = await GetFileSizesAsync(hashes, ct);

        var results = new List<string>();

        _logger.LogInformation("Server returned {count} DTO(s)", fileDtos.Count);
        foreach (var dto in fileDtos)
        {
            _logger.LogInformation("  {hash}: exists={exists} forbidden={forbidden} url={url} size={size}",
                dto.Hash, dto.FileExists, dto.IsForbidden, dto.DirectDownloadUrl ?? "<null>", dto.Size);

            if (!dto.FileExists)
            {
                _logger.LogWarning("{hash}: not found on server", dto.Hash);
                continue;
            }
            if (dto.IsForbidden)
            {
                _logger.LogWarning("{hash}: forbidden", dto.Hash);
                continue;
            }
            if (string.IsNullOrEmpty(dto.DirectDownloadUrl))
            {
                _logger.LogWarning("{hash}: no DirectDownloadUrl in response", dto.Hash);
                continue;
            }

            var outPath = await DownloadOneAsync(dto, outputDir, ct);
            if (outPath != null)
                results.Add(outPath);
        }

        return results;
    }

    private async Task<List<DownloadFileDto>> GetFileSizesAsync(IReadOnlyList<string> hashes, CancellationToken ct)
    {
        var uri = MareFiles.ServerFilesGetSizesFullPath(_cdnUri, _tzOffsetMinutes);

        var token = await _getToken(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Content = JsonContent.Create(hashes),
        };
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(req, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("GetFileSizes response {status}: {body}", response.StatusCode, body);
        response.EnsureSuccessStatusCode();

        return System.Text.Json.JsonSerializer.Deserialize<List<DownloadFileDto>>(body, JsonOpts.Default) ?? [];
    }
    
    private static string DetectExtension(string hash, ReadOnlySpan<byte> data)
    {
        // Check the plugin's local cache index first — it already has the extension
        var csvPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "MareSempiterne", "FileCache.csv");

        if (File.Exists(csvPath))
        {
            foreach (var line in File.ReadLines(csvPath))
            {
                if (!line.StartsWith(hash, StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(line.Split('|')[1]);
                if (!string.IsNullOrEmpty(ext)) return ext;
                break;
            }
        }

        // Fall back to magic bytes for files not in the local cache
        if (data.Length < 4) return ".bin";

        // Binary-header formats (version/flag bytes, not ASCII)
        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x03 && data[3] == 0x01) return ".mtrl"; // 00 00 03 01
        if (data[0] == 0x01 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x01) return ".phyb"; // 01 00 00 01
        if (data[0] is 0x05 or 0x06 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x01) return ".mdl"; // 05/06 00 00 01
        if (data[0] == 0x1F && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x00) return ".pbd";  // 1F 00 00 00
        // .tex and .atex share 00 00 80 00 — .tex is far more common so we prefer it
        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x80 && data[3] == 0x00) return ".tex";  // 00 00 80 00

        return Encoding.ASCII.GetString(data[..4]) switch
        {
            "XFVA" => ".avfx",  // 58 46 56 41
            "die\0" => ".eid",  // 64 69 65 00
            "ShPk" => ".shpk",  // 53 68 50 6B
            "blks" => ".sklb",  // 62 6C 6B 73
            "plks" => ".skp",   // 70 6C 6B 73
            "pap " => ".pap",   // 70 61 70 20
            "TMLB" => ".tmb",   // 54 4D 4C 42
            "SEDB" => ".scd",   // 53 45 44 42 (also appears in some .avfx)
            "DDS " => ".dds",   // 44 44 53 20 (some .atex embed DDS)
            "\x89PNG" => ".png", // 89 50 4E 47 (some .atex embed PNG)
            _ => ".bin",
        };
    }

    private async Task<string?> DownloadOneAsync(DownloadFileDto dto, string outputDir, CancellationToken ct)
    {
        _logger.LogInformation("{hash}: downloading from {url} (munge key: {key}, {size} bytes compressed)",
            dto.Hash, dto.DirectDownloadUrl, dto.MungeKey ?? "<none>", dto.Size);

        // Download compressed bytes — no auth needed for DirectDownloadUrl
        byte[] compressed;
        try
        {
            using var response = await _httpClient.GetAsync(dto.DirectDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            compressed = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{hash}: download failed", dto.Hash);
            return null;
        }

        // XOR munge if key is present
        if (dto.MungeKey != null)
        {
            var keyBytes = Encoding.ASCII.GetBytes(dto.MungeKey);
            for (int i = 0; i < compressed.Length; i++)
                compressed[i] ^= keyBytes[i % keyBytes.Length];
        }

        // LZ4 decompress
        byte[] decompressed;
        try
        {
            decompressed = LZ4Wrapper.Unwrap(compressed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{hash}: LZ4 decompression failed", dto.Hash);
            return null;
        }

        var ext = DetectExtension(dto.Hash, decompressed);
        var outPath = Path.Combine(outputDir, dto.Hash.ToLowerInvariant() + ext);
        await File.WriteAllBytesAsync(outPath, decompressed, ct);
        _logger.LogInformation("{hash}: saved to {path} ({size} bytes)", dto.Hash, outPath, decompressed.Length);
        return outPath;
    }
}
