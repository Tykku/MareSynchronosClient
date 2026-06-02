using DnsClient;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace MareStandaloneClient;

public static class GatewayManager
{
    private const string GatewaySubDomain = "gateways";
    private const string GatewayStatus = "gateway-status";
    private const int DelayVarianceMs = 100;

    public static async Task<Uri?> GetServiceGatewayUriAsync(Uri serviceUri, ILogger logger, CancellationToken ct = default)
    {
        string host = serviceUri.Host;
        string domain = string.Join('.', host.Split('.').Skip(1));

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(2000) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MareStandaloneClient");

        var hosts = await TryGetTxtRecordPartsAsync($"{GatewaySubDomain}.{domain}", ct);
        if (hosts is null || hosts.Count == 0)
        {
            logger.LogWarning("DNS TXT gateway lookup failed, trying HTTPS fallback");
            hosts = await TryGetHostsFromWebAsync(httpClient, new($"https://{GatewaySubDomain}.{domain}/csv"), ct);
        }
        if (hosts is null || hosts.Count == 0)
        {
            logger.LogError("Could not discover any gateways");
            return null;
        }

        var gateways = hosts.Select(h => new Uri($"https://{h}.{domain}")).ToList();
        var results = await Task.WhenAll(gateways.Select(g => CheckGatewayAsync(httpClient, g, logger, ct)));
        var valid = results.Where(r => r != null).ToList();

        if (valid.Count == 0) return null;

        int lowest = valid.Min(r => r!.TotalMs);
        var best = valid
            .Where(r => r!.TotalMs <= lowest + DelayVarianceMs)
            .OrderBy(r => r!.Priority)
            .ThenBy(r => r!.TotalMs)
            .First();

        logger.LogInformation("Best gateway: {gateway} ({ms}ms)", best!.GatewayUri.Host, best.TotalMs);
        return new Uri($"wss://{best.GatewayUri.Host}");
    }

    private static async Task<List<string>?> TryGetTxtRecordPartsAsync(string hostName, CancellationToken ct)
    {
        try
        {
            var dns = new LookupClient();
            var result = await dns.QueryAsync(hostName, QueryType.TXT, cancellationToken: ct);
            return result.Answers.TxtRecords()
                .SelectMany(r => r.Text)
                .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
        }
        catch { return null; }
    }

    private static async Task<List<string>?> TryGetHostsFromWebAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(uri, ct);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            return [.. body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch { return null; }
    }

    private static async Task<GatewayResult?> CheckGatewayAsync(HttpClient client, Uri gateway, ILogger logger, CancellationToken ct)
    {
        var statusUri = new Uri(gateway, $"/{GatewayStatus}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(2000);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(statusUri, cts.Token);
            sw.Stop();
            if (response.StatusCode != HttpStatusCode.OK) return null;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var statusEl)) return null;
            if (!root.TryGetProperty("priority", out var priorityEl)) return null;

            int statusMs = statusEl.GetInt32();
            int priority = priorityEl.GetInt32();

            if (priority <= 0 || statusMs is -1 or 9999 or < 0) return null;
            if (statusMs > 2000) return null;

            return new GatewayResult(gateway, statusMs, (int)sw.ElapsedMilliseconds, priority);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Gateway {uri} unavailable", statusUri);
            return null;
        }
    }

    private record GatewayResult(Uri GatewayUri, int StatusMs, int RequestMs, int Priority)
    {
        public int TotalMs => StatusMs + RequestMs;
    }
}
