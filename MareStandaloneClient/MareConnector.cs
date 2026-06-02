using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Dto;
using MareSynchronos.API.Dto.CharaData;
using MareSynchronos.API.Dto.Emote;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Dto.User;
using MareSynchronos.API.Routes;
using MareSynchronos.API.SignalR;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;

namespace MareStandaloneClient;

public class MareConnector
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MareConnector> _logger;
    private readonly ServerStorage _server;
    private readonly Authentication _auth;
    private readonly string _charaIdent;
    private readonly bool _enableGatewayDiscovery;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _log;

    private HubConnection? _hub;
    private string? _cachedToken;
    private DateTime _tokenValidTo = DateTime.MinValue;
    private FileDownloader? _fileDownloader;

    public MareConnector(ILoggerFactory loggerFactory, ServerStorage server, Authentication auth,
        string charaIdent, bool enableGatewayDiscovery, Action<string> log)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MareConnector>();
        _server = server;
        _auth = auth;
        _charaIdent = charaIdent;
        _enableGatewayDiscovery = enableGatewayDiscovery;
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MareStandaloneClient/1.0");
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _log("Resolving gateway...");
        var hubUrl = await ResolveHubUrlAsync(ct);
        _log($"Connecting to: {hubUrl}");

        _hub = BuildHubConnection(hubUrl, ct);
        RegisterHandlers();

        _log("Connecting...");
        await _hub.StartAsync(ct);

        var connDto = await _hub.InvokeAsync<ConnectionDto>("GetConnectionDto", ct);
        _fileDownloader = new FileDownloader(_loggerFactory, connDto.ServerInfo.FileServerAddress, GetOrUpdateTokenAsync);
        _log("Connected!");
        _log($"  UID          : {connDto.User.AliasOrUID}");
        _log($"  CDN          : {connDto.ServerInfo.FileServerAddress}");
        _log($"  Server ver   : {connDto.ServerVersion} (API requires {IMareHub.ApiVersion})");
        _log($"  Client ver   : {connDto.CurrentClientVersion}");

        var healthy = await _hub.InvokeAsync<bool>("CheckClientHealth", ct);
        if (!healthy)
            _log("  Health check : outdated client version (connection still works)");
    }

    public async Task DisconnectAsync()
    {
        if (_hub != null)
        {
            _log("Disconnecting...");
            await _hub.StopAsync(CancellationToken.None);
            await _hub.DisposeAsync();
            _hub = null;
        }
        _httpClient.Dispose();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await ConnectAsync(ct);
            _log("Listening for server events. Use Disconnect to stop.");
            await HealthCheckLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection failed");
            _log($"Error: {ex.Message}");
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    private async Task<string> ResolveHubUrlAsync(CancellationToken ct)
    {
        if (_enableGatewayDiscovery)
        {
            var gatewayLogger = _loggerFactory.CreateLogger("GatewayManager");
            try
            {
                var gateway = await GatewayManager.GetServiceGatewayUriAsync(
                    new Uri(_server.ServerUri), gatewayLogger, ct);
                if (gateway != null)
                    return gateway.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gateway discovery failed, falling back to server URI");
            }
        }
        return _server.ServerUri;
    }

    private HubConnection BuildHubConnection(string baseUrl, CancellationToken ct)
    {
        var transportType = _server.HttpTransportType switch
        {
            HttpTransportType.ServerSentEvents => HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
            HttpTransportType.LongPolling => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
        };

        if (IsWine() && !_server.ForceWebSockets)
        {
            _logger.LogDebug("Wine detected, using SSE/LongPolling");
            transportType = HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
        }

        return new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + IMareHub.Path, options =>
            {
                options.AccessTokenProvider = () => GetOrUpdateTokenAsync(ct);
                options.Transports = transportType;
            })
            .AddMessagePackProtocol(opt =>
            {
                var resolver = CompositeResolver.Create(
                    StandardResolverAllowPrivate.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    DynamicEnumAsStringResolver.Instance,
                    DynamicGenericResolver.Instance,
                    DynamicUnionResolver.Instance,
                    DynamicObjectResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    StandardResolver.Instance);

                opt.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithCompression(MessagePackCompression.Lz4Block)
                    .WithResolver(resolver);
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .ConfigureLogging(lb =>
            {
                lb.ClearProviders().AddProvider(new LoggerFactoryProvider(_loggerFactory));
                lb.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();
    }

    private void RegisterHandlers()
    {
        _hub!.On<SystemInfoDto>(nameof(IMareHub.Client_UpdateSystemInfo), info =>
            _log($"[Server] Online users: {info.OnlineUsers}"));

        _hub!.On<MessageSeverity, string>(nameof(IMareHub.Client_ReceiveServerMessage), (severity, msg) =>
            _log($"[Server Message ({severity})] {msg}"));

        // Stub handlers to suppress "no handler" warnings for server-pushed events we don't act on
        _hub!.On<GroupPermissionDto>(nameof(IMareHub.Client_GroupChangePermissions), _ => { });
        _hub!.On<GroupDto>(nameof(IMareHub.Client_GroupDelete), _ => { });
        _hub!.On<GroupPairUserInfoDto>(nameof(IMareHub.Client_GroupPairChangeUserInfo), _ => { });
        _hub!.On<GroupPairFullInfoDto>(nameof(IMareHub.Client_GroupPairJoined), _ => { });
        _hub!.On<GroupPairDto>(nameof(IMareHub.Client_GroupPairLeft), _ => { });
        _hub!.On<GroupFullInfoDto>(nameof(IMareHub.Client_GroupSendFullInfo), _ => { });
        _hub!.On<GroupInfoDto>(nameof(IMareHub.Client_GroupSendInfo), _ => { });
        _hub!.On<UserDto>(nameof(IMareHub.Client_ReceivePairingMessage), _ => { });
        _hub!.On<UserPairDto>(nameof(IMareHub.Client_UserAddClientPair), _ => { });
        _hub!.On<OnlineUserCharaDataDto>(nameof(IMareHub.Client_UserReceiveCharacterData), _ => { });
        _hub!.On<UserDto>(nameof(IMareHub.Client_UserReceiveUploadStatus), _ => { });
        _hub!.On<UserDto>(nameof(IMareHub.Client_UserRemoveClientPair), _ => { });
        _hub!.On<UserDto>(nameof(IMareHub.Client_UserSendOffline), _ => { });
        _hub!.On<OnlineUserIdentDto>(nameof(IMareHub.Client_UserSendOnline), _ => { });
        _hub!.On<UserPermissionsDto>(nameof(IMareHub.Client_UserUpdateOtherPairPermissions), _ => { });
        _hub!.On<UserIndividualPairStatusDto>(nameof(IMareHub.Client_UpdateUserIndividualPairStatusDto), _ => { });
        _hub!.On<UserDto>(nameof(IMareHub.Client_UserUpdateProfile), _ => { });
        _hub!.On<UserPreferencesDto>(nameof(IMareHub.Client_UserUpdatePreferences), _ => { });
        _hub!.On<UserPermissionsDto>(nameof(IMareHub.Client_UserUpdateSelfPairPermissions), _ => { });
        _hub!.On<DefaultPermissionsDto>(nameof(IMareHub.Client_UserUpdateDefaultPermissions), _ => { });
        _hub!.On<GroupPairUserPermissionDto>(nameof(IMareHub.Client_GroupChangeUserPairPermissions), _ => { });
        _hub!.On<UserData>(nameof(IMareHub.Client_GposeLobbyJoin), _ => { });
        _hub!.On<UserData>(nameof(IMareHub.Client_GposeLobbyLeave), _ => { });
        _hub!.On<CharaDataDownloadDto>(nameof(IMareHub.Client_GposeLobbyPushCharacterData), _ => { });
        _hub!.On<UserData, PoseData>(nameof(IMareHub.Client_GposeLobbyPushPoseData), (_, _) => { });
        _hub!.On<UserData, WorldData>(nameof(IMareHub.Client_GposeLobbyPushWorldData), (_, _) => { });
        _hub!.On<bool>(nameof(IMareHub.Client_BroadcastListeningChanged), _ => { });
        _hub!.On<UserPairRequestsDto>(nameof(IMareHub.Client_UpdatePairRequests), _ => { });
        _hub!.On<GroupJoinInvitesDto>(nameof(IMareHub.Client_UpdateGroupInvites), _ => { });
        _hub!.On<EmoteResponseDto>(nameof(IMareHub.Client_UpdateEmoteSyncUsers), _ => { });
        _hub!.On<ScheduledEmoteActionDto>(nameof(IMareHub.Client_StartEmoteSyncGroup), _ => { });
        _hub!.On<JsonDataTypeDto>(nameof(IMareHub.Client_ProcessJsonDataType), _ => { });

        _hub!.Closed += ex =>
        {
            _log($"[Connection closed] {ex?.Message ?? "clean shutdown"}");
            return Task.CompletedTask;
        };
        _hub!.Reconnecting += ex =>
        {
            _log($"[Reconnecting] {ex?.Message}");
            return Task.CompletedTask;
        };
        _hub!.Reconnected += id =>
        {
            _log($"[Reconnected] ConnectionId: {id}");
            return Task.CompletedTask;
        };
    }

    public Task ListenAsync(CancellationToken ct) => HealthCheckLoopAsync(ct);

    public Task<List<string>> DownloadFilesAsync(IReadOnlyList<string> hashes, string outputDir, CancellationToken ct)
    {
        if (_fileDownloader == null)
            throw new InvalidOperationException("Not connected yet — call ConnectAsync first.");
        return _fileDownloader.DownloadAsync(hashes, outputDir, ct);
    }

    private async Task HealthCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _hub != null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _logger.LogDebug("Refreshing token and checking health");

            try
            {
                await GetOrUpdateTokenAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token refresh failed, stopping health loop");
                break;
            }

            try
            {
                _ = await _hub.InvokeAsync<bool>("CheckClientHealth", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check invocation failed");
            }
        }
    }

    private async Task<string?> GetOrUpdateTokenAsync(CancellationToken ct)
    {
        if (_cachedToken != null && _tokenValidTo.Subtract(TimeSpan.FromMinutes(5)) > DateTime.UtcNow)
            return _cachedToken;

        _logger.LogDebug("Fetching auth token...");
        var authBase = GetAuthServiceUri();
        _logger.LogDebug("Auth endpoint base: {uri}", authBase);

        HttpResponseMessage response;
        if (_server.UseOAuth2)
        {
            if (string.IsNullOrEmpty(_server.OAuthToken) || string.IsNullOrEmpty(_auth.UID))
                throw new InvalidOperationException("Server uses OAuth2 but OAuthToken or UID is missing.");

            var uri = MareAuth.AuthWithOauthFullPath(new Uri(authBase));
            var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent([
                    new("uid", _auth.UID),
                    new("charaIdent", _charaIdent),
                ]),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _server.OAuthToken);
            response = await _httpClient.SendAsync(req, ct);
        }
        else
        {
            if (!_server.SecretKeys.TryGetValue(_auth.SecretKeyIdx, out var secretKey))
                throw new InvalidOperationException($"Secret key index {_auth.SecretKeyIdx} not found.");

            var uri = MareAuth.AuthFullPath(new Uri(authBase));
            var authHash = secretKey.Key.GetHash256();
            response = await _httpClient.PostAsync(uri, new FormUrlEncodedContent([
                new("auth", authHash),
                new("charaIdent", _charaIdent),
            ]), ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Auth failed ({response.StatusCode}): {body}");
        }

        var token = await response.Content.ReadAsStringAsync(ct);
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        _tokenValidTo = jwt.ValidTo;
        _cachedToken = token;

        var uid = jwt.Claims.FirstOrDefault(c => c.Type == "uid")?.Value ?? "unknown";
        _log($"Authenticated! UID: {uid}, Token valid until: {_tokenValidTo:u}");

        return token;
    }

    private string GetAuthServiceUri()
    {
        if (!string.IsNullOrWhiteSpace(_server.ServerAuth))
            return _server.ServerAuth;

        var builder = new UriBuilder(_server.ServerUri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
        };
        var parts = builder.Host.Split('.');
        if (parts.Length > 0) parts[0] = "auth";
        builder.Host = string.Join('.', parts);
        return builder.Uri.ToString();
    }

    private static bool IsWine()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEPREFIX"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEDLLPATH"));
    }
}

file sealed class ForeverRetry : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, retryContext.PreviousRetryCount)));
}

file sealed class LoggerFactoryProvider(ILoggerFactory factory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);
    public void Dispose() { }
}