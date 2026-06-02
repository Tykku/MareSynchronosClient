using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;

namespace MareStandaloneClient;

public class ServerConfig
{
    public int CurrentServer { get; set; } = 0;
    public List<ServerStorage> ServerStorage { get; set; } = [];
    public bool EnableGatewayDiscovery { get; set; } = true;
    public string ManualGatewayServer { get; set; } = string.Empty;
    public bool OverrideGatewaySelection { get; set; } = false;

    public static ServerConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ServerConfig>(json, JsonOpts.Default)
            ?? throw new InvalidOperationException("Deserialized to null");
    }
}

public class ServerStorage
{
    public List<Authentication> Authentications { get; set; } = [];
    public bool FullPause { get; set; } = false;
    public Dictionary<int, SecretKey> SecretKeys { get; set; } = [];
    public string ServerName { get; set; } = string.Empty;
    public string ServerUri { get; set; } = string.Empty;
    public string ServerAuth { get; set; } = string.Empty;
    public bool UseOAuth2 { get; set; } = false;
    public string? OAuthToken { get; set; } = null;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HttpTransportType HttpTransportType { get; set; } = HttpTransportType.WebSockets;
    public bool ForceWebSockets { get; set; } = false;
}

public class Authentication
{
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; } = 0;
    public int SecretKeyIdx { get; set; } = -1;
    public string? UID { get; set; }
    public bool AutoLogin { get; set; } = true;
    public ulong? LastSeenCID { get; set; } = null;
}

public class SecretKey
{
    public string FriendlyName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
