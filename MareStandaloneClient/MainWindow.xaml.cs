using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace MareStandaloneClient;

public partial class MainWindow : Window
{
    private ILoggerFactory? _loggerFactory;
    private ServerConfig? _config;
    private CancellationTokenSource? _cts;
    private MareConnector? _connector;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loggerFactory = LoggerFactory.Create(b =>
            b.AddProvider(new LogBoxLoggerProvider(Log))
             .SetMinimumLevel(LogLevel.Warning));

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "MareSempiterne", "server.json");

        ConfigPathText.Text = configPath;

        if (!File.Exists(configPath))
        {
            Log($"Config not found: {configPath}");
            Log("Make sure the PlayerSync plugin has been loaded at least once in Dalamud.");
            ConnectButton.IsEnabled = false;
            return;
        }

        try
        {
            _config = ServerConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Log($"Failed to parse server.json: {ex.Message}");
            ConnectButton.IsEnabled = false;
            return;
        }

        if (_config.ServerStorage.Count == 0)
        {
            Log("No servers configured in server.json.");
            ConnectButton.IsEnabled = false;
            return;
        }

        var server = GetCurrentServer();
        ServerText.Text = $"{server.ServerName} ({server.ServerUri})";

        if (server.Authentications.Count == 0)
        {
            Log("No characters configured for this server.");
            ConnectButton.IsEnabled = false;
            return;
        }

        foreach (var auth in server.Authentications)
            CharacterCombo.Items.Add(new CharacterItem(auth));

        CharacterCombo.SelectedIndex = 0;
    }

    private ServerStorage GetCurrentServer()
    {
        var idx = _config!.CurrentServer < _config.ServerStorage.Count ? _config.CurrentServer : 0;
        return _config.ServerStorage[idx];
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Output Directory" };
        if (dialog.ShowDialog() == true)
            OutputDirBox.Text = dialog.FolderName;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null || CharacterCombo.SelectedItem is not CharacterItem item)
            return;

        var auth = item.Auth;
        if (auth.LastSeenCID == null || auth.LastSeenCID == 0)
        {
            Log("Character has no stored ContentID (LastSeenCID). Log in with this character in FFXIV first.");
            return;
        }

        var server = GetCurrentServer();
        var charaIdent = auth.LastSeenCID.Value.ToString().GetHash256();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetConnectionState(connecting: true, connected: false);

        _connector = new MareConnector(_loggerFactory!, server, auth, charaIdent,
            _config.EnableGatewayDiscovery, Log);

        try
        {
            await _connector.ConnectAsync(ct);
            SetConnectionState(connecting: false, connected: true);

            // Health-check loop — keeps the connection alive until Disconnect is clicked.
            // Download_Click can fire concurrently while this is running.
            await _connector.ListenAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Log("Disconnected.");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            await _connector.DisconnectAsync();
            _connector = null;
            SetConnectionState(connecting: false, connected: false);
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_connector == null || _cts == null) return;

        var hashes = HashesBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h.Length > 0)
            .ToList();

        if (hashes.Count == 0)
        {
            Log("No hashes entered.");
            return;
        }

        DownloadButton.IsEnabled = false;
        try
        {
            var written = await _connector.DownloadFilesAsync(hashes, OutputDirBox.Text, _cts.Token);
            Log($"Downloaded {written.Count}/{hashes.Count} file(s) to: {Path.GetFullPath(OutputDirBox.Text)}");
            foreach (var path in written)
                Log($"  {path}");
        }
        catch (OperationCanceledException)
        {
            Log("Download cancelled.");
        }
        catch (Exception ex)
        {
            Log($"Download error: {ex.Message}");
        }
        finally
        {
            if (_connector != null)
                DownloadButton.IsEnabled = true;
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
        => _cts?.Cancel();

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _isClosing = true;
        _cts?.Cancel();
    }

    private void SetConnectionState(bool connecting, bool connected)
    {
        if (_isClosing) return;
        ConnectButton.IsEnabled = !connecting && !connected;
        DisconnectButton.IsEnabled = connecting || connected;
        DownloadButton.IsEnabled = connected;
        CharacterCombo.IsEnabled = !connecting && !connected;

        if (connected)
        {
            StatusText.Text = "Connected";
            StatusText.Foreground = Brushes.Green;
        }
        else if (connecting)
        {
            StatusText.Text = "Connecting...";
            StatusText.Foreground = Brushes.Orange;
        }
        else
        {
            StatusText.Text = "Disconnected";
            StatusText.Foreground = Brushes.Gray;
        }
    }

    internal void Log(string message)
    {
        if (_isClosing) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        if (!Dispatcher.CheckAccess())
            Dispatcher.InvokeAsync(() => AppendLog(line));
        else
            AppendLog(line);
    }

    private void AppendLog(string line)
    {
        if (_isClosing) return;
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
    }
}

internal sealed record CharacterItem(Authentication Auth)
{
    public override string ToString() => $"{Auth.CharacterName} (World {Auth.WorldId})";
}

internal sealed class LogBoxLoggerProvider(Action<string> log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LogBoxLogger(log);
    public void Dispose() { }
}

internal sealed class LogBoxLogger(Action<string> log) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        log($"[{logLevel}] {formatter(state, exception)}");
        if (exception != null)
            log($"  Exception: {exception.Message}");
    }
}
