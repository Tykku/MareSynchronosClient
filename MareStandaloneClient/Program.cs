using MareStandaloneClient;
using Microsoft.Extensions.Logging;

// Usage:
//   dotnet run
//   dotnet run -- <hash> [hash ...] / [-o <outputDir>]  -- download specific files by hash

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Debug));

var logger = loggerFactory.CreateLogger<Program>();

List<string> hashes = [];
string outputDir = "downloads";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length)
        outputDir = args[++i];
    else
        hashes.Add(args[i]);
}

var configDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "XIVLauncher", "pluginConfigs", "MareSempiterne");

var configPath = Path.Combine(configDir, "server.json");
if (!File.Exists(configPath))
{
    Console.WriteLine($"Could not find server.json at:\n  {configPath}");
    Console.WriteLine("Make sure the PlayerSync plugin has been loaded at least once in Dalamud.");
    return 1;
}

ServerConfig config;
try
{
    config = ServerConfig.Load(configPath);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to parse server.json: {ex.Message}");
    return 1;
}

if (config.ServerStorage.Count == 0)
{
    Console.WriteLine("No servers configured in server.json.");
    return 1;
}

var serverStorage = config.ServerStorage[config.CurrentServer < config.ServerStorage.Count ? config.CurrentServer : 0];
Console.WriteLine($"Server: {serverStorage.ServerName} ({serverStorage.ServerUri})");

if (serverStorage.Authentications.Count == 0)
{
    Console.WriteLine("No characters configured for this server.");
    return 1;
}

Authentication auth;
if (serverStorage.Authentications.Count == 1)
{
    auth = serverStorage.Authentications[0];
}
else
{
    Console.WriteLine("\nAvailable characters:");
    for (int i = 0; i < serverStorage.Authentications.Count; i++)
    {
        var a = serverStorage.Authentications[i];
        Console.WriteLine($"  [{i}] {a.CharacterName} (World {a.WorldId})");
    }
    Console.Write("Select character index: ");
    var input = Console.ReadLine();
    if (!int.TryParse(input, out var idx) || idx < 0 || idx >= serverStorage.Authentications.Count)
    {
        Console.WriteLine("Invalid selection.");
        return 1;
    }
    auth = serverStorage.Authentications[idx];
}

Console.WriteLine($"Character: {auth.CharacterName} (World {auth.WorldId})");

if (auth.LastSeenCID == null || auth.LastSeenCID == 0)
{
    Console.WriteLine("Character has no stored ContentID (LastSeenCID). Log in with this character in FFXIV first.");
    return 1;
}

var charaIdent = auth.LastSeenCID.Value.ToString().GetHash256();

var connector = new MareConnector(loggerFactory, serverStorage, auth, charaIdent, config.EnableGatewayDiscovery);

if (hashes.Count > 0)
{
    await connector.ConnectAsync(cts.Token);
    var written = await connector.DownloadFilesAsync(hashes, outputDir, cts.Token);
    await connector.DisconnectAsync();

    Console.WriteLine($"\nDownloaded {written.Count}/{hashes.Count} file(s) to: {Path.GetFullPath(outputDir)}");
    foreach (var path in written)
        Console.WriteLine($"  {path}");
}
else
{
    await connector.RunAsync(cts.Token);
}

return 0;
