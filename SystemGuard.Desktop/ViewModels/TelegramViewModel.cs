using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class TelegramViewModel : ViewModelBase
{
    private readonly TelegramBotService _botService = new();
    private readonly string _configPath;

    [ObservableProperty] private string _botToken = "";
    [ObservableProperty] private string _chatId = "";
    [ObservableProperty] private string _statusText = "Enter bot token and chat ID, then click Connect";
    [ObservableProperty] private string _commandInput = "";
    [ObservableProperty] private string _filePathInput = "";
    [ObservableProperty] private ObservableCollection<string> _logMessages = new();
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _newUserId = "";
    [ObservableProperty] private bool _newUserAdmin;
    [ObservableProperty] private string _liveStatus = "Live access is off";
    [ObservableProperty] private string _liveUrl = "";
    [ObservableProperty] private string _liveToken = "";
    [ObservableProperty] private string _lanUrl = "";
    [ObservableProperty] private string _macAddresses = "";
    [ObservableProperty] private string _remoteHelp = "Вне дома: ПК включён + приложение запущено + live опубликован. В WebApp вкладка Статус → ссылка + токен. Выключенный ПК без WoL-железа офлайн.";
    [ObservableProperty] private string _tunnelLog = "";
    [ObservableProperty] private bool _isLiveRunning;
    [ObservableProperty] private bool _isLiveBusy;

    public IRelayCommand ConnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IRelayCommand SaveConfigCommand { get; }
    public IRelayCommand SendStatusCommand { get; }
    public IRelayCommand SendScreenshotCommand { get; }
    public IRelayCommand ExecuteCmdCommand { get; }
    public IRelayCommand ClearLogCommand { get; }
    public IRelayCommand StartStreamCommand { get; }
    public IRelayCommand StopStreamCommand { get; }
    public IRelayCommand SendCamCommand { get; }
    public IRelayCommand SendFileCommand { get; }
    public IRelayCommand AddUserCommand { get; }
    public IRelayCommand<string> RemoveUserCommand { get; }
    public IRelayCommand PublishLiveCommand { get; }
    public IRelayCommand StopLiveCommand { get; }
    public IRelayCommand RegenerateTokenCommand { get; }

    public TelegramViewModel()
    {
        _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard", "telegram.json");

        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(Disconnect);
        SaveConfigCommand = new RelayCommand(SaveConfig);
        SendStatusCommand = new AsyncRelayCommand(SendStatus);
        SendScreenshotCommand = new AsyncRelayCommand(SendScreenshot);
        ExecuteCmdCommand = new AsyncRelayCommand(ExecuteCmd);
        ClearLogCommand = new RelayCommand(() => LogMessages.Clear());
        StartStreamCommand = new RelayCommand(StartStream);
        StopStreamCommand = new RelayCommand(StopStream);
        SendCamCommand = new AsyncRelayCommand(SendCam);
        SendFileCommand = new AsyncRelayCommand(SendFile);
        AddUserCommand = new RelayCommand(AddUser);
        RemoveUserCommand = new RelayCommand<string>(RemoveUser);
        PublishLiveCommand = new AsyncRelayCommand(PublishLiveAsync);
        StopLiveCommand = new RelayCommand(StopLive);
        RegenerateTokenCommand = new RelayCommand(RegenerateToken);

        LiveServices.Tunnel.Changed += RefreshLiveState;
        RefreshLiveState();
        LoadConfig();
        // Чтобы "в следующем запуске всё работало": бот сам коннектится,
        // live сам публикуется — руками жать ничего не надо. Вне дома ПК
        // после перезагрузки снова доступен через Telegram-relay.
        _ = Task.Run(AutoStartAsync);
    }

    private string _lastPushedUrl = "";

    private async Task AutoStartAsync()
    {
        try
        {
            await Task.Delay(1500).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId))
            {
                try
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Connect());
                }
                catch { Connect(); }
                await Task.Delay(2000).ConfigureAwait(false);
            }
            if (LiveServices.AutopublishLive)
            {
                await PublishLiveAsync().ConfigureAwait(false);
            }
        }
        catch { }
    }

    private void Disconnect()
    {
        _botService.Disconnect();
        IsConnected = false;
        IsStreaming = false;
        StatusText = "Bot disconnected";
        AddLog("Bot disconnected");
    }

    private void AddUser()
    {
        if (!long.TryParse(NewUserId.Trim(), out var id)) { StatusText = "Enter numeric chat ID"; return; }
        _botService.AddUser(id, NewUserAdmin);
        StatusText = $"User {id} added ({(NewUserAdmin ? "admin" : "read-only")})";
        AddLog(StatusText);
        NewUserId = "";
    }

    private void RemoveUser(string? id)
    {
        if (!long.TryParse(id, out var uid)) return;
        _botService.RemoveUser(uid);
        StatusText = $"User {uid} removed";
    }

    private void LoadConfig()
    {
        try
        {
            // Токен хранится шифром DPAPI (миграция открытого файла — сама).
            var json = Services.SecretStore.ReadText(_configPath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var config = JsonSerializer.Deserialize<BotConfig>(json);
                if (config != null)
                {
                    BotToken = config.Token;
                    ChatId = config.ChatId;
                    StatusText = "Config loaded. Click Connect.";
                    AddLog("Config loaded from file");
                }
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new BotConfig { Token = BotToken, ChatId = ChatId };
            Services.SecretStore.WriteText(_configPath, JsonSerializer.Serialize(config));
            StatusText = "Config saved!";
            AddLog("Config saved");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
        {
            StatusText = "Please enter bot token and chat ID";
            return;
        }
        if (!long.TryParse(ChatId.Trim(), out _))
        {
            StatusText = "Chat ID must be numeric (see @userinfobot)";
            return;
        }
        SaveConfig();
        _botService.Configure(BotToken, ChatId);
        _botService.StartListening();
        IsConnected = true;
        StatusText = "Bot connected and listening!";
        AddLog("Bot connected — use /start in Telegram");
    }

    private async Task SendStatus()
    {
        if (!IsConnected) { StatusText = "Connect bot first"; return; }
        IsSending = true;
        var status = await _botService.GetSystemStatus();
        var result = await _botService.SendMessage("System Status\n" + status);
        AddLog("Status sent");
        IsSending = false;
    }

    private async Task SendScreenshot()
    {
        if (!IsConnected) { StatusText = "Connect bot first"; return; }
        IsSending = true;
        var result = await _botService.SendScreenshot();
        AddLog($"Screenshot: {result}");
        IsSending = false;
    }

    private async Task ExecuteCmd()
    {
        if (!IsConnected) { StatusText = "Connect bot first"; return; }
        if (string.IsNullOrWhiteSpace(CommandInput)) return;
        IsSending = true;
        var result = await _botService.ExecuteCommand(CommandInput);
        AddLog($"{CommandInput}\n{result}");
        CommandInput = "";
        IsSending = false;
    }

    private void StartStream()
    {
        if (!IsConnected) { StatusText = "Connect bot first"; return; }
        StatusText = _botService.StartStreamToConfigured();
        IsStreaming = _botService.IsStreaming;
        AddLog("Live stream started");
    }

    private void StopStream()
    {
        StatusText = _botService.StopAllStreams();
        IsStreaming = false;
        AddLog("Streams stopped");
    }

    private async Task SendCam()
    {
        if (!IsConnected) { StatusText = "Connect bot first"; return; }
        IsSending = true;
        await _botService.SendWebcamToConfigured();
        AddLog("Webcam snapshot sent");
        IsSending = false;
    }

    private async Task SendFile()
    {
        if (!IsConnected) { StatusText = "Connect bot first"; return; }
        if (string.IsNullOrWhiteSpace(FilePathInput)) { StatusText = "Enter file path"; return; }
        IsSending = true;
        await _botService.SendFileToConfigured(FilePathInput.Trim().Trim('"'));
        AddLog($"File sent: {FilePathInput}");
        FilePathInput = "";
        IsSending = false;
    }

    private void AddLog(string message)
    {
        LogMessages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        if (LogMessages.Count > 50) LogMessages.RemoveAt(LogMessages.Count - 1);
    }

    // ── Live-доступ для WebApp (Cloudflare Tunnel → локальный HTTP API) ──────
    private void RefreshLiveState()
    {
        var t = LiveServices.Tunnel;
        var lan = "";
        var macs = "";
        try { lan = $"http://{TunnelService.GetLanIPv4()}:{RemoteHttpServer.Port}"; } catch { }
        try
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var mac = nic.GetPhysicalAddress()?.ToString() ?? "";
                if (mac.Length == 12)
                    list.Add(string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))));
            }
            macs = string.Join(", ", list.Distinct().Take(3));
        }
        catch { }
        // Свежая ссылка уехала владельцу в личку: после каждого реконнекта URL
        // новый, а телефон вне дома хранит старый. Без автопуша "не работает".
        try
        {
            var url = t.PublicUrl ?? "";
            if (t.IsRunning && !string.IsNullOrEmpty(url) && url != _lastPushedUrl && IsConnected)
            {
                _lastPushedUrl = url;
                _ = _botService.PushLiveUrlAsync(url, t.Provider ?? "?");
            }
            if (!t.IsRunning) _lastPushedUrl = "";
        }
        catch { }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsLiveRunning = t.IsRunning;
            LiveUrl = t.PublicUrl ?? "";
            LiveToken = LiveServices.Token;
            LanUrl = lan;
            MacAddresses = macs;
            TunnelLog = t.LastLog ?? "";
            LiveStatus = t.IsRunning
                ? $"Live via {t.Provider ?? "?"}: " + (t.PublicUrl ?? "")
                : t.Status == "Stopped" ? "Live access is off" : t.Status;
            IsLiveBusy = false;
        });
    }

    private async Task PublishLiveAsync()
    {
        if (IsLiveBusy || IsLiveRunning) return;
        IsLiveBusy = true;
        LiveStatus = "Preparing live access…";
        try
        {
            LiveServices.StartServer();
            // cloudflared качаем лениво внутри StartAsync и только если SSH-цепочка
            // не поднялась: GitHub часто висит, а кнопка из-за этого выглядит мёртвой.
            var (ok, msg2) = await LiveServices.Tunnel.StartAsync(RemoteHttpServer.Port).ConfigureAwait(false);
            if (ok) LiveServices.SetAutopublish(true);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AddLog(msg2);
                RefreshLiveState();
            });
        }
        catch (Exception ex)
        {
            var e = ex.Message;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LiveStatus = "Live error: " + e;
                IsLiveBusy = false;
            });
        }
    }

    private void StopLive()
    {
        LiveServices.SetAutopublish(false);
        LiveServices.Tunnel.Stop();
        AddLog("Live access stopped");
        RefreshLiveState();
    }

    // Лечит 401: старый токен утек/расcинхронизировался — выпускаем новый.
    // WebApp после этого надо связать заново (новый токен в то же поле).
    private void RegenerateToken()
    {
        try
        {
            var t = LiveServices.RegenerateToken();
            LiveToken = t;
            AddLog("Live token renewed — relink WebApp with the new token");
            StatusText = "Token renewed. Enter the new token in the WebApp.";
            RefreshLiveState();
        }
        catch (Exception ex)
        {
            StatusText = "Token renew failed: " + ex.Message;
        }
    }
}

internal class BotConfig
{
    public string Token { get; set; } = "";
    public string ChatId { get; set; } = "";
}