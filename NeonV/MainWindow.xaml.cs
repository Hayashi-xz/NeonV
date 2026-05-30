using Microsoft.Win32;
using QRCoder;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;

namespace NeonV;

public static class SystemProxyManager
{
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    public static void EnableProxy(string proxyAddress)
    {
        try
        {
            using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            registry?.SetValue("ProxyEnable", 1);
            registry?.SetValue("ProxyServer", proxyAddress);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { }
    }

    public static void DisableProxy()
    {
        try
        {
            using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            registry?.SetValue("ProxyEnable", 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { }
    }
}

public static class PingManager
{
    private static readonly ConcurrentDictionary<string, Task<long>> _tasks = new();

    public static Task<long> GetPingTask(string key, Func<Task<long>> pingAction) =>
        _tasks.GetOrAdd(key, _ => pingAction());

    public static void Clear() => _tasks.Clear();
}

public class LanguageItem
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public override string ToString() => Name;
}

public class VpnProfile : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "vless";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Uuid { get; set; } = "";
    public string Password { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Fp { get; set; } = "chrome";
    public string Alpn { get; set; } = "";
    public string Pbk { get; set; } = "";
    public string Sid { get; set; } = "";
    public string Flow { get; set; } = "";
    public int AlterId { get; set; } = 0;
    public string Security { get; set; } = "auto";
    public string Transport { get; set; } = "tcp";
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";
    public string Method { get; set; } = "chacha20-ietf-poly1305";
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string LocalAddress { get; set; } = "10.0.0.2/32";
    public int Mtu { get; set; } = 1420;
    public string Up { get; set; } = "";
    public string Down { get; set; } = "";
    public string Obfs { get; set; } = "";
    public string ObfsPassword { get; set; } = "";
    public string Link { get; set; } = "";
    public bool Insecure { get; set; }

    private long _lastPingMs = -1;
    private bool _isPingError;
    private string _pingText = "...";
    private SolidColorBrush _pingColor = new(Colors.Gray);

    [JsonIgnore] public string DisplayAddress => $"{Address}:{Port}";

    [JsonIgnore]
    public string PingText
    {
        get => _pingText;
        set { _pingText = value; OnPropertyChanged(nameof(PingText)); }
    }

    [JsonIgnore]
    public SolidColorBrush PingColor
    {
        get => _pingColor;
        set { _pingColor = value; OnPropertyChanged(nameof(PingColor)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void ForceUpdatePing()
    {
        PingColor = GetPingColor(_lastPingMs);
        if (_lastPingMs > 0)
            PingText = $"{_lastPingMs} {MainWindow.GetString("Lang_PingMs")}";
        else if (PingText != "...")
            PingText = MainWindow.GetString(_isPingError ? "Lang_PingErr" : "Lang_PingTime");
    }

    private static SolidColorBrush GetPingColor(long ms)
    {
        if (ms is < 0 or > 9999)
            return new SolidColorBrush(MainWindow.IsDarkTheme ? Color.FromRgb(248, 113, 113) : Color.FromRgb(220, 38, 38));

        ms = Math.Clamp(ms, 0, 600);
        double t; byte r, g, b;

        if (MainWindow.IsDarkTheme)
        {
            if (ms <= 200) { t = ms / 200.0; r = (byte)(74 + t * 176); g = (byte)(222 - t * 18); b = (byte)(128 - t * 107); }
            else { t = (ms - 200) / 400.0; r = (byte)(250 - t * 2); g = (byte)(204 - t * 91); b = (byte)(21 + t * 92); }
        }
        else
        {
            if (ms <= 200) { t = ms / 200.0; r = (byte)(22 + t * 180); g = (byte)(163 - t * 25); b = (byte)(4 + t * 0); }
            else { t = (ms - 200) / 400.0; r = (byte)(202 + t * 18); g = (byte)(138 - t * 100); b = (byte)(4 + t * 34); }
        }
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void UpdatePing(long ms, string text, bool isError = false)
    {
        _lastPingMs = ms;
        _isPingError = isError;
        PingText = text;
        PingColor = GetPingColor(ms);
    }

    public async Task CheckPingAsync()
    {
        try
        {
            var host = Address;
            var port = Port > 0 ? Port : 443;

            if (string.IsNullOrEmpty(host) && Uri.TryCreate(Link, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
                port = uri.Port > 0 ? uri.Port : 443;
            }

            if (string.IsNullOrEmpty(host)) { UpdatePing(-1, MainWindow.GetString("Lang_PingErr"), true); return; }

            if (!IPAddress.TryParse(host, out var targetIp))
            {
                var ips = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(2));
                targetIp = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? ips[0];
            }

            string cacheKey = $"{targetIp}:{port}_{(MainWindow.IsVpnRunning ? "active" : "inactive")}";
            long ms = await PingManager.GetPingTask(cacheKey, async () =>
            {
                long bestMs = long.MaxValue;

                try
                {
                    using var ping = new Ping();
                    for (int i = 0; i < 2; i++)
                    {
                        try
                        {
                            var reply = await ping.SendPingAsync(targetIp, 2000);
                            if (reply.Status == IPStatus.Success) bestMs = Math.Min(bestMs, reply.RoundtripTime);
                        }
                        catch { }
                    }
                }
                catch { }

                if (bestMs != long.MaxValue && bestMs > 0) return bestMs;

                if (MainWindow.IsVpnRunning && MainWindow.ActiveProfile != this) return -1;

                for (int i = 0; i < 2; i++)
                {
                    try
                    {
                        using var tcp = new TcpClient(targetIp.AddressFamily) { NoDelay = true };
                        var sw = Stopwatch.StartNew();
                        await tcp.ConnectAsync(targetIp, port).WaitAsync(TimeSpan.FromSeconds(2));
                        sw.Stop();
                        bestMs = Math.Min(bestMs, sw.ElapsedMilliseconds);
                    }
                    catch { }
                }

                return bestMs != long.MaxValue ? bestMs : -1;
            });

            if (ms > 0)
            {
                ms = Math.Max(1, ms + Random.Shared.Next(-1, 2));
                UpdatePing(ms, $"{ms} {MainWindow.GetString("Lang_PingMs")}");
            }
            else UpdatePing(-1, MainWindow.GetString("Lang_PingTime"));
        }
        catch
        {
            UpdatePing(-1, MainWindow.GetString("Lang_PingErr"), true);
        }
    }
}

public class AppSettings
{
    public List<VpnProfile> Profiles { get; set; } = new List<VpnProfile>();
    public int SelectedProfileIndex { get; set; } = -1;
    public string Mode { get; set; } = "TUN";
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "";
    public string RemoteDns { get; set; } = "8.8.8.8";
    public bool AutoStart { get; set; }
    public bool AutoConnect { get; set; }
    public bool StartMinimized { get; set; }
    public bool HasShownTrayNotification { get; set; }
    public bool EnableVisualEffects { get; set; }
    public bool DisableAnimations { get; set; }
    public bool UseCustomConfig { get; set; }
    public string RouteMode { get; set; } = "Bypass";
    public List<string> RoutingDomains { get; set; } = new List<string>();
    public List<string> RoutingApps { get; set; } = new List<string>();
    public int ProxyPort { get; set; } = 2080;
    public bool AllowLan { get; set; }
    public string LogLevel { get; set; } = "info";
    public string TunStack { get; set; } = "system";
}

public partial class MainWindow : Window
{
    public static bool IsVpnRunning;
    public static VpnProfile? ActiveProfile;
    public static bool IsDarkTheme = true;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private Process? _singboxProcess;
    private bool _isRunning;
    private readonly string _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private AppSettings _currentSettings = new();
    private bool _isUpdatingSettings;

    private readonly ObservableCollection<VpnProfile> _profiles = new();
    public ObservableCollection<string> RoutingDomainsList { get; set; } = new();
    public ObservableCollection<string> RoutingAppsList { get; set; } = new();

    private readonly DispatcherTimer _pingTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _logTimer;
    private DateTime _connectionStartTime;

    private readonly Dictionary<string, string> _manualInputValues = new(StringComparer.OrdinalIgnoreCase);
    private VpnProfile? _editingProfile;
    private string _originalIp = "";
    private string _currentQrLink = "";

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _trayNavMainItem;
    private Forms.ToolStripMenuItem? _trayNavSettingsItem;
    private Forms.ToolStripMenuItem? _trayNavLogsItem;
    private Forms.ToolStripMenuItem? _trayModeItem;
    private Forms.ToolStripMenuItem? _trayToggleItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private bool _isRealClose;

    private readonly Queue<string> _logBuffer = new();
    private readonly object _logLock = new();

    private long _lastBytesReceived;
    private long _lastBytesSent;

    private static readonly Dictionary<string, Dictionary<string, string>> Locales = new()
    {
        ["ru"] = new()
        {
            {"Lang_Protocol", "Протокол"}, {"Lang_CtxConnect", "Подключиться (Двойной клик)"},
            {"Lang_CtxEdit", "Редактировать профиль"}, {"Lang_CtxCopyLink", "Копировать ссылку"},
            {"Lang_CtxMoveUp", "Переместить вверх"}, {"Lang_CtxMoveDown", "Переместить вниз"},
            {"Lang_CtxDelete", "Удалить"}, {"Lang_NavMain", "Главная"}, {"Lang_NavSettings", "Настройки"},
            {"Lang_NavLogs", "Логи"}, {"Lang_Mode", "Режим работы:"}, {"Lang_AddProfile", "Добавить профиль"},
            {"Lang_RefreshPing", "Обновить пинг"}, {"Lang_EmptyTitle", "Нет добавленных профилей"},
            {"Lang_EmptyDesc", "Нажмите «Добавить профиль» для начала работы"}, {"Lang_SettingsTitle", "Настройки"},
            {"Lang_StartupWork", "Запуск и работа"}, {"Lang_AutoStart", "Запускать вместе с Windows"},
            {"Lang_AutoConnectVpn", "Автоматически подключать VPN при запуске"},
            {"Lang_AutoConnectProxy", "Автоматически подключать Proxy при запуске"},
            {"Lang_StartMinimized", "Запускать свернутым в трей"},
            {"Lang_CustomConfig", "Использовать собственный config.json (без перезаписи)"},
            {"Lang_Appearance", "Внешний вид"}, {"Lang_Language", "Язык интерфейса"}, {"Lang_Theme", "Тема оформления"},
            {"Lang_ThemeDark", "Темная (Dark)"}, {"Lang_ThemeLight", "Светлая (Light)"},
            {"Lang_VisualEffects", "Включить визуальные эффекты"}, {"Lang_DisableAnim", "Отключить анимации переходов"},
            {"Lang_NetRouting", "Сеть и Маршрутизация"}, {"Lang_RemoteDns", "Удаленный DNS (Remote)"},
            {"Lang_ConfigureRoutes", "Настроить исключения (Маршруты)"}, {"Lang_AdvancedSettings", "Продвинутые настройки"},
            {"Lang_ProxyPort", "Порт локального прокси"}, {"Lang_AllowLan", "Разрешить подключения по локальной сети (LAN)"},
            {"Lang_AllowLanHint", "Позволяет другим устройствам в вашей Wi-Fi сети подключаться к этому прокси."},
            {"Lang_TunStack", "Сетевой стек TUN"},
            {"Lang_CoreLogs", "Логи ядра"}, {"Lang_LogLevel", "Уровень: "}, {"Lang_AutoScroll", "Автопрокрутка"},
            {"Lang_Copy", "Скопировать"}, {"Lang_Clear", "Очистить"},
            {"Lang_AddProfileTitle", "Добавление профиля"}, {"Lang_PasteClipboard", "Вставить из буфера обмена"},
            {"Lang_OrManual", "Или добавить вручную:"}, {"Lang_Cancel", "Отмена"}, {"Lang_Save", "Сохранить"},
            {"Lang_RoutingTitle", "Исключения и правила"}, {"Lang_RoutingMode", "Режим исключений:"},
            {"Lang_RouteBypass", "Включить для всех, КРОМЕ..."}, {"Lang_RouteOnly", "Включить ТОЛЬКО ДЛЯ..."},
            {"Lang_DomainsDesc", "Сайты и домены (например: google.com)"}, {"Lang_Add", "Добавить"},
            {"Lang_AppsDesc", "Приложения (например: telegram.exe)"}, {"Lang_Done", "Готово"},
            {"Lang_QRTitle", "QR-Код профиля"}, {"Lang_CopyLink", "Скопировать ссылку"},
            {"Lang_SearchPlaceholder", "Поиск профилей..."}, {"Lang_BtnEnableVpn", "ВКЛЮЧИТЬ VPN"},
            {"Lang_BtnDisableVpn", "ВЫКЛЮЧИТЬ VPN"}, {"Lang_BtnEnableProxy", "ВКЛЮЧИТЬ PROXY"},
            {"Lang_BtnDisableProxy", "ВЫКЛЮЧИТЬ PROXY"},
            {"Lang_ErrInvalidAddress", "Укажите корректный адрес сервера"}, {"Lang_ErrInvalidPort", "Укажите корректный порт (1-65535)"},
            {"Lang_ErrSelectProfile", "Сначала выберите профиль из списка!"}, {"Lang_ErrWintun", "Ошибка: Файл wintun.dll не найден!"},
            {"Lang_ErrCrash", "Ядро Sing-box завершило работу с ошибкой. Проверьте логи."},
            {"Lang_MsgCopied", "Скопировано в буфер обмена"}, {"Lang_MsgImportOk", "Профиль успешно импортирован"},
            {"Lang_ErrImport", "Ошибка: в буфере нет профилей или они неверны"},
            {"Lang_LocUnknown", "Неизвестно"}, {"Lang_TrayRunning", "Приложение работает в фоновом режиме."},
            {"Lang_TrayUI", "Открыть интерфейс"}, {"Lang_TrayExit", "Выход"},
            {"Lang_PingErr", "Ошибка"}, {"Lang_PingTime", "Таймаут"}, {"Lang_PingMs", "мс"},
            {"Lang_FldName", "Название профиля"}, {"Lang_FldAddress", "Адрес (IP или домен)"},
            {"Lang_FldPort", "Порт"}, {"Lang_FldPassword", "Пароль (Password)"},
            {"Lang_FldUuid", "UUID / Имя пользователя"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "Fingerprint (Отпечаток)"}, {"Lang_FldInsecure", "Игнорировать ошибки TLS"},
            {"Lang_FldFlow", "Flow"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "Transport (tcp, ws...)"}, {"Lang_FldPath", "Path (Путь)"},
            {"Lang_FldHost", "Host"}, {"Lang_FldSecurity", "Security"}, {"Lang_FldAlterId", "AlterID"},
            {"Lang_FldMethod", "Метод шифрования"}, {"Lang_FldPubKey", "Public Key (Публичный)"},
            {"Lang_FldPrivKey", "Private Key (Приватный)"}, {"Lang_FldLocalAddr", "Local Address (Локальный)"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "Up Mbps (Отдача)"},
            {"Lang_FldDownMbps", "Down Mbps (Загрузка)"}, {"Lang_FldObfs", "Тип Obfs"}, {"Lang_FldObfsPass", "Пароль Obfs"},
            {"Lang_MsgRoutesChanged", "Маршруты изменены, переподключение..."},
            {"Lang_MsgLogLevelChanged", "Уровень логов изменен, переподключение..."},
            {"Lang_ErrQrTooLong", "Генерация QR-кода невозможна: превышена длина ссылки"},
            {"Lang_ErrQrGen", "Произошла ошибка при генерации QR-кода"}
        },
        ["en"] = new()
        {
            {"Lang_Protocol", "Protocol"}, {"Lang_CtxConnect", "Connect (Double Click)"},
            {"Lang_CtxEdit", "Edit Profile"}, {"Lang_CtxCopyLink", "Copy Link"},
            {"Lang_CtxMoveUp", "Move Up"}, {"Lang_CtxMoveDown", "Move Down"},
            {"Lang_CtxDelete", "Delete"}, {"Lang_NavMain", "Home"}, {"Lang_NavSettings", "Settings"},
            {"Lang_NavLogs", "Logs"}, {"Lang_Mode", "Mode:"}, {"Lang_AddProfile", "Add Profile"},
            {"Lang_RefreshPing", "Refresh Ping"}, {"Lang_EmptyTitle", "No Profiles Found"},
            {"Lang_EmptyDesc", "Click «Add Profile» to get started"}, {"Lang_SettingsTitle", "Settings"},
            {"Lang_StartupWork", "Startup & Run"}, {"Lang_AutoStart", "Run at Windows startup"},
            {"Lang_AutoConnectVpn", "Auto-connect VPN on startup"},
            {"Lang_AutoConnectProxy", "Auto-connect Proxy on startup"}, {"Lang_StartMinimized", "Start minimized in system tray"},
            {"Lang_CustomConfig", "Use custom config.json (no rewrite)"},
            {"Lang_Appearance", "Appearance"}, {"Lang_Language", "Language"}, {"Lang_Theme", "Theme"},
            {"Lang_ThemeDark", "Dark"}, {"Lang_ThemeLight", "Light"},
            {"Lang_VisualEffects", "Enable visual effects"}, {"Lang_DisableAnim", "Disable transition animations"},
            {"Lang_NetRouting", "Network & Routing"}, {"Lang_RemoteDns", "Remote DNS"},
            {"Lang_ConfigureRoutes", "Configure Routing Rules"}, {"Lang_AdvancedSettings", "Advanced Settings"},
            {"Lang_ProxyPort", "Local Proxy Port"}, {"Lang_AllowLan", "Allow connection from LAN"},
            {"Lang_AllowLanHint", "Allows other devices in your Wi-Fi network to connect to this proxy."},
            {"Lang_TunStack", "TUN Network Stack"},
            {"Lang_CoreLogs", "Core Logs"}, {"Lang_LogLevel", "Level: "}, {"Lang_AutoScroll", "Auto-scroll"},
            {"Lang_Copy", "Copy"}, {"Lang_Clear", "Clear"},
            {"Lang_AddProfileTitle", "Add Profile"}, {"Lang_PasteClipboard", "Paste from Clipboard"},
            {"Lang_OrManual", "Or add manually:"}, {"Lang_Cancel", "Cancel"}, {"Lang_Save", "Save"},
            {"Lang_RoutingTitle", "Routing Rules"}, {"Lang_RoutingMode", "Rule mode:"},
            {"Lang_RouteBypass", "Proxy everything EXCEPT..."}, {"Lang_RouteOnly", "Proxy ONLY for..."},
            {"Lang_DomainsDesc", "Domains (e.g. google.com)"}, {"Lang_Add", "Add"},
            {"Lang_AppsDesc", "Applications (e.g. telegram.exe)"}, {"Lang_Done", "Done"},
            {"Lang_QRTitle", "Profile QR-Code"}, {"Lang_CopyLink", "Copy Link"},
            {"Lang_SearchPlaceholder", "Search profiles..."}, {"Lang_BtnEnableVpn", "ENABLE VPN"},
            {"Lang_BtnDisableVpn", "DISABLE VPN"}, {"Lang_BtnEnableProxy", "ENABLE PROXY"},
            {"Lang_BtnDisableProxy", "DISABLE PROXY"},
            {"Lang_ErrInvalidAddress", "Invalid server address"}, {"Lang_ErrInvalidPort", "Invalid port (1-65535)"},
            {"Lang_ErrSelectProfile", "Select a profile first!"}, {"Lang_ErrWintun", "Error: wintun.dll not found!"},
            {"Lang_ErrCrash", "Sing-box core crashed. Check logs."},
            {"Lang_MsgCopied", "Copied to clipboard"}, {"Lang_MsgImportOk", "Imported successfully"},
            {"Lang_ErrImport", "Import error: no valid links found"},
            {"Lang_LocUnknown", "Unknown"}, {"Lang_TrayRunning", "App is running in the background."},
            {"Lang_TrayUI", "Open UI"}, {"Lang_TrayExit", "Exit"},
            {"Lang_PingErr", "Err"}, {"Lang_PingTime", "Time"}, {"Lang_PingMs", "ms"},
            {"Lang_FldName", "Profile Name"}, {"Lang_FldAddress", "Address/IP"},
            {"Lang_FldPort", "Port"}, {"Lang_FldPassword", "Password"},
            {"Lang_FldUuid", "UUID / Username"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "Fingerprint"}, {"Lang_FldInsecure", "Insecure (Ignore TLS)"},
            {"Lang_FldFlow", "Flow"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "Transport"}, {"Lang_FldPath", "Path"},
            {"Lang_FldHost", "Host"}, {"Lang_FldSecurity", "Security"}, {"Lang_FldAlterId", "AlterID"},
            {"Lang_FldMethod", "Method"}, {"Lang_FldPubKey", "Public Key"},
            {"Lang_FldPrivKey", "Private Key"}, {"Lang_FldLocalAddr", "Local Address"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "Up Mbps"},
            {"Lang_FldDownMbps", "Down Mbps"}, {"Lang_FldObfs", "Obfs Type"}, {"Lang_FldObfsPass", "Obfs Password"},
            {"Lang_MsgRoutesChanged", "Routes changed, reconnecting..."},
            {"Lang_MsgLogLevelChanged", "Log level changed, reconnecting..."},
            {"Lang_ErrQrTooLong", "QR code generation failed: link is too long"},
            {"Lang_ErrQrGen", "An error occurred while generating the QR code"}
        },
        ["uk"] = new()
        {
            {"Lang_Protocol", "Протокол"}, {"Lang_CtxConnect", "Підключитися"},
            {"Lang_CtxEdit", "Редагувати профіль"}, {"Lang_CtxCopyLink", "Копіювати посилання"},
            {"Lang_CtxMoveUp", "Вгору"}, {"Lang_CtxMoveDown", "Вниз"},
            {"Lang_CtxDelete", "Видалити"}, {"Lang_NavMain", "Головна"}, {"Lang_NavSettings", "Налаштування"},
            {"Lang_NavLogs", "Логи"}, {"Lang_Mode", "Режим:"}, {"Lang_AddProfile", "Додати профіль"},
            {"Lang_RefreshPing", "Оновити пінг"}, {"Lang_EmptyTitle", "Профілі відсутні"},
            {"Lang_EmptyDesc", "Натисніть «Додати профіль», щоб розпочати"}, {"Lang_SettingsTitle", "Налаштування"},
            {"Lang_StartupWork", "Запуск та робота"}, {"Lang_AutoStart", "Запускати разом з Windows"},
            {"Lang_AutoConnectVpn", "Автопідключення VPN"}, {"Lang_AutoConnectProxy", "Автопідключення Proxy"},
            {"Lang_StartMinimized", "Запускати згорнутим"}, {"Lang_CustomConfig", "Власний config.json"},
            {"Lang_Appearance", "Зовнішній вигляд"}, {"Lang_Language", "Мова"}, {"Lang_Theme", "Тема"},
            {"Lang_ThemeDark", "Темна"}, {"Lang_ThemeLight", "Світла"},
            {"Lang_VisualEffects", "Увімкнути візуальні ефекти"}, {"Lang_DisableAnim", "Вимкнути анімації"},
            {"Lang_NetRouting", "Мережа та Маршрутизація"}, {"Lang_RemoteDns", "Віддалений DNS"},
            {"Lang_ConfigureRoutes", "Налаштувати правила"}, {"Lang_AdvancedSettings", "Розширені налаштування"},
            {"Lang_ProxyPort", "Порт проксі"}, {"Lang_AllowLan", "Дозволити LAN"},
            {"Lang_AllowLanHint", "Дозволяє іншим пристроям у вашій Wi-Fi мережі підключатися до цього проксі."},
            {"Lang_TunStack", "Мережевий стек TUN"},
            {"Lang_CoreLogs", "Логи"}, {"Lang_LogLevel", "Рівень: "}, {"Lang_AutoScroll", "Автопрокрутка"},
            {"Lang_Copy", "Скопіювати"}, {"Lang_Clear", "Очистити"},
            {"Lang_AddProfileTitle", "Додати профіль"}, {"Lang_PasteClipboard", "Вставити з буфера"},
            {"Lang_OrManual", "Або вручну:"}, {"Lang_Cancel", "Скасувати"}, {"Lang_Save", "Зберегти"},
            {"Lang_RoutingTitle", "Правила маршрутизації"}, {"Lang_RoutingMode", "Режим винятків:"},
            {"Lang_RouteBypass", "Proxy для всіх, ОКРІМ..."}, {"Lang_RouteOnly", "Proxy ТІЛЬКИ ДЛЯ..."},
            {"Lang_DomainsDesc", "Домени"}, {"Lang_Add", "Додати"},
            {"Lang_AppsDesc", "Додатки (напр. telegram.exe)"}, {"Lang_Done", "Готово"},
            {"Lang_QRTitle", "QR-Код профілю"}, {"Lang_CopyLink", "Копіювати посилання"},
            {"Lang_SearchPlaceholder", "Пошук профілів..."}, {"Lang_BtnEnableVpn", "УВІМКНУТИ VPN"},
            {"Lang_BtnDisableVpn", "ВИМКНУТИ VPN"}, {"Lang_BtnEnableProxy", "УВІМКНУТИ PROXY"},
            {"Lang_BtnDisableProxy", "ВИМКНУТИ PROXY"},
            {"Lang_ErrInvalidAddress", "Невірна адреса сервера"}, {"Lang_ErrInvalidPort", "Невірний порт (1-65535)"},
            {"Lang_ErrSelectProfile", "Спочатку виберіть профіль!"}, {"Lang_ErrWintun", "Помилка: файл wintun.dll не знайдено!"},
            {"Lang_ErrCrash", "Ядро Sing-box завершило работу з помилкою."},
            {"Lang_MsgCopied", "Скопійовано"}, {"Lang_MsgImportOk", "Успішно імпортовано"},
            {"Lang_ErrImport", "Помилка імпорту"},
            {"Lang_LocUnknown", "Невідомо"}, {"Lang_TrayRunning", "Додаток працює у фоновому режимі."},
            {"Lang_TrayUI", "Інтерфейс"}, {"Lang_TrayExit", "Вихід"},
            {"Lang_PingErr", "Пом"}, {"Lang_PingTime", "Таймаут"}, {"Lang_PingMs", "мс"},
            {"Lang_FldName", "Назва профілю"}, {"Lang_FldAddress", "Адреса/IP"},
            {"Lang_FldPort", "Порт"}, {"Lang_FldPassword", "Пароль"},
            {"Lang_FldUuid", "UUID / Ім'я"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "Fingerprint"}, {"Lang_FldInsecure", "Ігнорувати помилки TLS"},
            {"Lang_FldFlow", "Flow"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "Transport"}, {"Lang_FldPath", "Path"},
            {"Lang_FldHost", "Host"}, {"Lang_FldSecurity", "Security"}, {"Lang_FldAlterId", "AlterID"},
            {"Lang_FldMethod", "Метод"}, {"Lang_FldPubKey", "Public Key"},
            {"Lang_FldPrivKey", "Private Key"}, {"Lang_FldLocalAddr", "Local Address"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "Up Mbps"},
            {"Lang_FldDownMbps", "Down Mbps"}, {"Lang_FldObfs", "Тип Obfs"}, {"Lang_FldObfsPass", "Пароль Obfs"},
            {"Lang_MsgRoutesChanged", "Маршруты змінено, перепідключення..."},
            {"Lang_MsgLogLevelChanged", "Рівень логів змінено, перепідключення..."},
            {"Lang_ErrQrTooLong", "Генерація QR-коду неможлива: перевищено довжину посилання"},
            {"Lang_ErrQrGen", "Виникла помилка під час генерації QR-коду"}
        },
        ["de"] = new()
        {
            {"Lang_Protocol", "Protokoll"}, {"Lang_CtxConnect", "Verbinden"},
            {"Lang_CtxEdit", "Profil bearbeiten"}, {"Lang_CtxCopyLink", "Link kopieren"},
            {"Lang_CtxMoveUp", "Nach oben"}, {"Lang_CtxMoveDown", "Nach unten"},
            {"Lang_CtxDelete", "Löschen"}, {"Lang_NavMain", "Startseite"}, {"Lang_NavSettings", "Einstellung"},
            {"Lang_NavLogs", "Protokolle"}, {"Lang_Mode", "Modus:"}, {"Lang_AddProfile", "Profil hinzufügen"},
            {"Lang_RefreshPing", "Ping aktualisieren"}, {"Lang_EmptyTitle", "Keine Profile"},
            {"Lang_EmptyDesc", "Klicke auf Profil hinzufügen"}, {"Lang_SettingsTitle", "Einstellungen"},
            {"Lang_StartupWork", "Systemstart"}, {"Lang_AutoStart", "Beim Windows-Start ausführen"},
            {"Lang_AutoConnectVpn", "VPN automatisch verbinden"}, {"Lang_AutoConnectProxy", "Proxy automatisch verbinden"},
            {"Lang_StartMinimized", "Minimiert starten"}, {"Lang_CustomConfig", "Benutzerdefinierte config.json"},
            {"Lang_Appearance", "Aussehen"}, {"Lang_Language", "Sprache"}, {"Lang_Theme", "Thema"},
            {"Lang_ThemeDark", "Dunkel"}, {"Lang_ThemeLight", "Hell"},
            {"Lang_VisualEffects", "Visuelle Effekte aktivieren"}, {"Lang_DisableAnim", "Animationen deaktivieren"},
            {"Lang_NetRouting", "Netzwerk & Routing"}, {"Lang_RemoteDns", "Remote-DNS"},
            {"Lang_ConfigureRoutes", "Regeln konfigurieren"}, {"Lang_AdvancedSettings", "Erweiterte Einstellungen"},
            {"Lang_ProxyPort", "Proxy-Port"}, {"Lang_AllowLan", "LAN-Verbindungen zulassen"},
            {"Lang_AllowLanHint", "Ermöglicht anderen Geräten in Ihrem WLAN, sich mit diesem Proxy zu verbinden."},
            {"Lang_TunStack", "TUN-Stack"},
            {"Lang_CoreLogs", "Logs"}, {"Lang_LogLevel", "Level: "}, {"Lang_AutoScroll", "Autoscroll"},
            {"Lang_Copy", "Kopieren"}, {"Lang_Clear", "Löschen"},
            {"Lang_AddProfileTitle", "Profil hinzufügen"}, {"Lang_PasteClipboard", "Aus der Zwischenablage"},
            {"Lang_OrManual", "Oder manuell:"}, {"Lang_Cancel", "Abbrechen"}, {"Lang_Save", "Speichern"},
            {"Lang_RoutingTitle", "Routing-Regeln"}, {"Lang_RoutingMode", "Modus:"},
            {"Lang_RouteBypass", "Alles Proxy AUSSER..."}, {"Lang_RouteOnly", "Proxy NUR FÜR..."},
            {"Lang_DomainsDesc", "Domains"}, {"Lang_Add", "Hinzufügen"},
            {"Lang_AppsDesc", "Anwendungen"}, {"Lang_Done", "Fertig"},
            {"Lang_QRTitle", "QR-Code"}, {"Lang_CopyLink", "Link kopieren"},
            {"Lang_SearchPlaceholder", "Profile suchen..."}, {"Lang_BtnEnableVpn", "VPN AKTIVIEREN"},
            {"Lang_BtnDisableVpn", "VPN DEAKTIVIEREN"}, {"Lang_BtnEnableProxy", "PROXY AKTIVIEREN"},
            {"Lang_BtnDisableProxy", "PROXY DEAKTIVIEREN"},
            {"Lang_ErrInvalidAddress", "Ungültige Adresse"}, {"Lang_ErrInvalidPort", "Ungültiger Port"},
            {"Lang_ErrSelectProfile", "Zuerst ein Profil auswählen!"}, {"Lang_ErrWintun", "Fehler: wintun.dll nicht gefunden!"},
            {"Lang_ErrCrash", "Sing-box abgestürzt. Logs prüfen."},
            {"Lang_MsgCopied", "Kopiert"}, {"Lang_MsgImportOk", "Erfolgreich importiert"},
            {"Lang_ErrImport", "Importfehler"},
            {"Lang_LocUnknown", "Unbekannt"}, {"Lang_TrayRunning", "App läuft im Hintergrund."},
            {"Lang_TrayUI", "UI öffnen"}, {"Lang_TrayExit", "Beenden"},
            {"Lang_PingErr", "Fehler"}, {"Lang_PingTime", "Zeit"}, {"Lang_PingMs", "ms"},
            {"Lang_FldName", "Profilname"}, {"Lang_FldAddress", "Adresse/IP"},
            {"Lang_FldPort", "Port"}, {"Lang_FldPassword", "Passwort"},
            {"Lang_FldUuid", "UUID / Name"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "Fingerprint"}, {"Lang_FldInsecure", "TLS-Fehler ignorieren"},
            {"Lang_FldFlow", "Flow"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "Transport"}, {"Lang_FldPath", "Pfad"},
            {"Lang_FldHost", "Host"}, {"Lang_FldSecurity", "Sicherheit"}, {"Lang_FldAlterId", "AlterID"},
            {"Lang_FldMethod", "Methode"}, {"Lang_FldPubKey", "Public Key"},
            {"Lang_FldPrivKey", "Private Key"}, {"Lang_FldLocalAddr", "Lokale Adresse"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "Up Mbps"},
            {"Lang_FldDownMbps", "Down Mbps"}, {"Lang_FldObfs", "Obfs-Typ"}, {"Lang_FldObfsPass", "Obfs-Passwort"},
            {"Lang_MsgRoutesChanged", "Routen geändert, neu verbinden..."},
            {"Lang_MsgLogLevelChanged", "Loglevel geändert, neu verbinden..."},
            {"Lang_ErrQrTooLong", "QR-Code Generierung fehlgeschlagen: Link ist zu lang"},
            {"Lang_ErrQrGen", "Fehler beim Generieren des QR-Codes"}
        },
        ["zh-Hans"] = new()
        {
            {"Lang_Protocol", "协议"}, {"Lang_CtxConnect", "连接"},
            {"Lang_CtxEdit", "编辑配置"}, {"Lang_CtxCopyLink", "复制链接"},
            {"Lang_CtxMoveUp", "上移"}, {"Lang_CtxMoveDown", "下移"},
            {"Lang_CtxDelete", "删除"}, {"Lang_NavMain", "首页"}, {"Lang_NavSettings", "设置"},
            {"Lang_NavLogs", "日志"}, {"Lang_Mode", "模式:"}, {"Lang_AddProfile", "添加配置"},
            {"Lang_RefreshPing", "延迟测试"}, {"Lang_EmptyTitle", "没有配置"},
            {"Lang_EmptyDesc", "点击添加配置以开始"}, {"Lang_SettingsTitle", "设置"},
            {"Lang_StartupWork", "启动与运行"}, {"Lang_AutoStart", "开机自启"},
            {"Lang_AutoConnectVpn", "启动时自动连接 VPN"}, {"Lang_AutoConnectProxy", "启动时自动连接 Proxy"},
            {"Lang_StartMinimized", "静默启动到托盘"}, {"Lang_CustomConfig", "使用自定义 config.json"},
            {"Lang_Appearance", "外观"}, {"Lang_Language", "语言"}, {"Lang_Theme", "主题"},
            {"Lang_ThemeDark", "深色"}, {"Lang_ThemeLight", "浅色"},
            {"Lang_VisualEffects", "开启视觉效果"}, {"Lang_DisableAnim", "关闭过渡动画"},
            {"Lang_NetRouting", "网络与路由"}, {"Lang_RemoteDns", "远程 DNS"},
            {"Lang_ConfigureRoutes", "配置路由规则"}, {"Lang_AdvancedSettings", "高级设置"},
            {"Lang_ProxyPort", "本地代理端口"}, {"Lang_AllowLan", "允许局域网连接"},
            {"Lang_AllowLanHint", "允许同一 Wi-Fi 网络中的其他设备连接到此代理。"},
            {"Lang_TunStack", "TUN 网络栈"},
            {"Lang_CoreLogs", "核心日志"}, {"Lang_LogLevel", "日志级别: "}, {"Lang_AutoScroll", "自动滚动"},
            {"Lang_Copy", "复制"}, {"Lang_Clear", "清空"},
            {"Lang_AddProfileTitle", "添加配置"}, {"Lang_PasteClipboard", "从剪贴板导入"},
            {"Lang_OrManual", "或手动输入:"}, {"Lang_Cancel", "取消"}, {"Lang_Save", "保存"},
            {"Lang_RoutingTitle", "路由规则"}, {"Lang_RoutingMode", "规则模式:"},
            {"Lang_RouteBypass", "代理所有，除了..."}, {"Lang_RouteOnly", "仅代理..."},
            {"Lang_DomainsDesc", "域名"}, {"Lang_Add", "添加"},
            {"Lang_AppsDesc", "应用程序"}, {"Lang_Done", "完成"},
            {"Lang_QRTitle", "配置二维码"}, {"Lang_CopyLink", "复制链接"},
            {"Lang_SearchPlaceholder", "搜索配置..."}, {"Lang_BtnEnableVpn", "开启 VPN"},
            {"Lang_BtnDisableVpn", "关闭 VPN"}, {"Lang_BtnEnableProxy", "开启 PROXY"},
            {"Lang_BtnDisableProxy", "关闭 PROXY"},
            {"Lang_ErrInvalidAddress", "无效地址"}, {"Lang_ErrInvalidPort", "无效端口"},
            {"Lang_ErrSelectProfile", "请先选择一个配置！"}, {"Lang_ErrWintun", "错误：未找到 wintun.dll！"},
            {"Lang_ErrCrash", "Sing-box 核心崩溃。请检查日志。"},
            {"Lang_MsgCopied", "已复制"}, {"Lang_MsgImportOk", "导入成功"},
            {"Lang_ErrImport", "导入错误"},
            {"Lang_LocUnknown", "未知"}, {"Lang_TrayRunning", "应用正在后台运行。"},
            {"Lang_TrayUI", "打开界面"}, {"Lang_TrayExit", "退出"},
            {"Lang_PingErr", "错误"}, {"Lang_PingTime", "超时"}, {"Lang_PingMs", "毫秒"},
            {"Lang_FldName", "配置名称"}, {"Lang_FldAddress", "地址/IP"},
            {"Lang_FldPort", "端口"}, {"Lang_FldPassword", "密码"},
            {"Lang_FldUuid", "UUID / 用户名"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "指纹 (Fingerprint)"}, {"Lang_FldInsecure", "忽略 TLS 证书错误"},
            {"Lang_FldFlow", "流控 (Flow)"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "传输方式 (Transport)"}, {"Lang_FldPath", "路径 (Path)"},
            {"Lang_FldHost", "主机 (Host)"}, {"Lang_FldSecurity", "安全 (Security)"}, {"Lang_FldAlterId", "额外ID (AlterID)"},
            {"Lang_FldMethod", "加密方法"}, {"Lang_FldPubKey", "公钥 (Public Key)"},
            {"Lang_FldPrivKey", "私钥 (Private Key)"}, {"Lang_FldLocalAddr", "本地地址"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "上传 Mbps"},
            {"Lang_FldDownMbps", "下载 Mbps"}, {"Lang_FldObfs", "混淆类型"}, {"Lang_FldObfsPass", "混淆密码"},
            {"Lang_MsgRoutesChanged", "路由已更改，重新连接..."},
            {"Lang_MsgLogLevelChanged", "日志级别已更改，重新连接..."},
            {"Lang_ErrQrTooLong", "无法生成二维码：链接过长"},
            {"Lang_ErrQrGen", "生成二维码时发生错误"}
        },
        ["zh-Hant"] = new()
        {
            {"Lang_Protocol", "協議"}, {"Lang_CtxConnect", "連接"},
            {"Lang_CtxEdit", "編輯配置"}, {"Lang_CtxCopyLink", "複製連結"},
            {"Lang_CtxMoveUp", "上移"}, {"Lang_CtxMoveDown", "下移"},
            {"Lang_CtxDelete", "刪除"}, {"Lang_NavMain", "首頁"}, {"Lang_NavSettings", "設定"},
            {"Lang_NavLogs", "日誌"}, {"Lang_Mode", "模式:"}, {"Lang_AddProfile", "新增配置"},
            {"Lang_RefreshPing", "延遲測試"}, {"Lang_EmptyTitle", "沒有配置"},
            {"Lang_EmptyDesc", "點擊新增配置以開始"}, {"Lang_SettingsTitle", "設定"},
            {"Lang_StartupWork", "啟動與運行"}, {"Lang_AutoStart", "開機自啟"},
            {"Lang_AutoConnectVpn", "啟動時自動連接 VPN"}, {"Lang_AutoConnectProxy", "啟動時自動連接 Proxy"},
            {"Lang_StartMinimized", "靜默啟動到托盤"}, {"Lang_CustomConfig", "使用自訂 config.json"},
            {"Lang_Appearance", "外觀"}, {"Lang_Language", "語言"}, {"Lang_Theme", "主題"},
            {"Lang_ThemeDark", "深色"}, {"Lang_ThemeLight", "淺色"},
            {"Lang_VisualEffects", "開啟視覺效果"}, {"Lang_DisableAnim", "關閉過渡動畫"},
            {"Lang_NetRouting", "網絡與路由"}, {"Lang_RemoteDns", "遠端 DNS"},
            {"Lang_ConfigureRoutes", "配置路由規則"}, {"Lang_AdvancedSettings", "進階設定"},
            {"Lang_ProxyPort", "本地代理端口"}, {"Lang_AllowLan", "允許區域網連接"},
            {"Lang_AllowLanHint", "允許同一 Wi-Fi 網絡中的其他設備連接到此代理。"},
            {"Lang_TunStack", "TUN 網絡棧"},
            {"Lang_CoreLogs", "核心日誌"}, {"Lang_LogLevel", "日誌級別: "}, {"Lang_AutoScroll", "自動滾動"},
            {"Lang_Copy", "複製"}, {"Lang_Clear", "清空"},
            {"Lang_AddProfileTitle", "新增配置"}, {"Lang_PasteClipboard", "從剪貼板導入"},
            {"Lang_OrManual", "或手動輸入:"}, {"Lang_Cancel", "取消"}, {"Lang_Save", "保存"},
            {"Lang_RoutingTitle", "路由規則"}, {"Lang_RoutingMode", "規則模式:"},
            {"Lang_RouteBypass", "代理所有，除了..."}, {"Lang_RouteOnly", "僅代理..."},
            {"Lang_DomainsDesc", "域名"}, {"Lang_Add", "新增"},
            {"Lang_AppsDesc", "應用程式"}, {"Lang_Done", "完成"},
            {"Lang_QRTitle", "配置 QR-Code"}, {"Lang_CopyLink", "複製連結"},
            {"Lang_SearchPlaceholder", "搜尋配置..."}, {"Lang_BtnEnableVpn", "開啟 VPN"},
            {"Lang_BtnDisableVpn", "關閉 VPN"}, {"Lang_BtnEnableProxy", "開啟 PROXY"},
            {"Lang_BtnDisableProxy", "關閉 PROXY"},
            {"Lang_ErrInvalidAddress", "無效地址"}, {"Lang_ErrInvalidPort", "無效端口"},
            {"Lang_ErrSelectProfile", "請先選擇一個配置！"}, {"Lang_ErrWintun", "錯誤：未找到 wintun.dll！"},
            {"Lang_ErrCrash", "Sing-box 核心崩潰。請檢查日誌。"},
            {"Lang_MsgCopied", "已複製"}, {"Lang_MsgImportOk", "導入成功"},
            {"Lang_ErrImport", "導入錯誤"},
            {"Lang_LocUnknown", "未知"}, {"Lang_TrayRunning", "應用正在後台運行。"},
            {"Lang_TrayUI", "打開介面"}, {"Lang_TrayExit", "退出"},
            {"Lang_PingErr", "錯誤"}, {"Lang_PingTime", "超時"}, {"Lang_PingMs", "毫秒"},
            {"Lang_FldName", "配置名稱"}, {"Lang_FldAddress", "地址/IP"},
            {"Lang_FldPort", "端口"}, {"Lang_FldPassword", "密碼"},
            {"Lang_FldUuid", "UUID / 用戶名"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "指紋 (Fingerprint)"}, {"Lang_FldInsecure", "忽略 TLS 證書錯誤"},
            {"Lang_FldFlow", "流控 (Flow)"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "傳輸方式 (Transport)"}, {"Lang_FldPath", "路徑 (Path)"},
            {"Lang_FldHost", "主機 (Host)"}, {"Lang_FldSecurity", "安全 (Security)"}, {"Lang_FldAlterId", "額外ID (AlterID)"},
            {"Lang_FldMethod", "加密方法"}, {"Lang_FldPubKey", "公鑰 (Public Key)"},
            {"Lang_FldPrivKey", "私鑰 (Private Key)"}, {"Lang_FldLocalAddr", "本地地址"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "上傳 Mbps"},
            {"Lang_FldDownMbps", "下載 Mbps"}, {"Lang_FldObfs", "混淆類型"}, {"Lang_FldObfsPass", "混淆密碼"},
            {"Lang_MsgRoutesChanged", "路由已更改，重新連接..."},
            {"Lang_MsgLogLevelChanged", "日誌級別已更改，重新連接..."},
            {"Lang_ErrQrTooLong", "無法生成 QR 碼：連結過長"},
            {"Lang_ErrQrGen", "生成 QR 碼時發生錯誤"}
        },
        ["ja"] = new()
        {
            {"Lang_Protocol", "プロトコル"}, {"Lang_CtxConnect", "接続"},
            {"Lang_CtxEdit", "プロファイル編集"}, {"Lang_CtxCopyLink", "リンクをコピー"},
            {"Lang_CtxMoveUp", "上に移動"}, {"Lang_CtxMoveDown", "下に移動"},
            {"Lang_CtxDelete", "削除"}, {"Lang_NavMain", "ホーム"}, {"Lang_NavSettings", "設定"},
            {"Lang_NavLogs", "ログ"}, {"Lang_Mode", "モード:"}, {"Lang_AddProfile", "プロファイル追加"},
            {"Lang_RefreshPing", "Ping更新"}, {"Lang_EmptyTitle", "プロファイルなし"},
            {"Lang_EmptyDesc", "プロファイルを追加してください"}, {"Lang_SettingsTitle", "設定"},
            {"Lang_StartupWork", "起動設定"}, {"Lang_AutoStart", "Windows起動時に実行"},
            {"Lang_AutoConnectVpn", "起動時にVPN自動接続"}, {"Lang_AutoConnectProxy", "起動時にProxy自動接続"},
            {"Lang_StartMinimized", "最小化して起動"}, {"Lang_CustomConfig", "カスタム config.jsonを使用"},
            {"Lang_Appearance", "外観"}, {"Lang_Language", "言語"}, {"Lang_Theme", "テーマ"},
            {"Lang_ThemeDark", "ダーク"}, {"Lang_ThemeLight", "ライト"},
            {"Lang_VisualEffects", "視覚効果を有効にする"}, {"Lang_DisableAnim", "アニメーション無効"},
            {"Lang_NetRouting", "ネットワークとルーティング"}, {"Lang_RemoteDns", "リモートDNS"},
            {"Lang_ConfigureRoutes", "ルールの設定"}, {"Lang_AdvancedSettings", "詳細設定"},
            {"Lang_ProxyPort", "Proxyポート"}, {"Lang_AllowLan", "LANからの接続を許可"},
            {"Lang_AllowLanHint", "同じWi-Fi内の他のデバイスからこのプロキシへの接続を許可します。"},
            {"Lang_TunStack", "TUNスタック"},
            {"Lang_CoreLogs", "ログ"}, {"Lang_LogLevel", "レベル: "}, {"Lang_AutoScroll", "自動スクロール"},
            {"Lang_Copy", "コピー"}, {"Lang_Clear", "クリア"},
            {"Lang_AddProfileTitle", "プロファイル追加"}, {"Lang_PasteClipboard", "クリップボードから"},
            {"Lang_OrManual", "手動追加:"}, {"Lang_Cancel", "キャンセル"}, {"Lang_Save", "保存"},
            {"Lang_RoutingTitle", "ルーティングルール"}, {"Lang_RoutingMode", "モード:"},
            {"Lang_RouteBypass", "すべてProxy（以下を除く）"}, {"Lang_RouteOnly", "以下のみProxy"},
            {"Lang_DomainsDesc", "ドメイン"}, {"Lang_Add", "追加"},
            {"Lang_AppsDesc", "アプリ"}, {"Lang_Done", "完了"},
            {"Lang_QRTitle", "QRコード"}, {"Lang_CopyLink", "リンクをコピー"},
            {"Lang_SearchPlaceholder", "検索..."}, {"Lang_BtnEnableVpn", "VPN オン"},
            {"Lang_BtnDisableVpn", "VPN オフ"}, {"Lang_BtnEnableProxy", "PROXY オン"},
            {"Lang_BtnDisableProxy", "PROXY オフ"},
            {"Lang_ErrInvalidAddress", "無効なアドレス"}, {"Lang_ErrInvalidPort", "無効なポート"},
            {"Lang_ErrSelectProfile", "最初にプロファイルを選択してください！"}, {"Lang_ErrWintun", "エラー: wintun.dll が見つかりません！"},
            {"Lang_ErrCrash", "Sing-box がクラッシュしました。"},
            {"Lang_MsgCopied", "コピーしました"}, {"Lang_MsgImportOk", "インポート成功"},
            {"Lang_ErrImport", "インポートエラー"},
            {"Lang_LocUnknown", "不明"}, {"Lang_TrayRunning", "アプリはバックグラウンドで実行中です。"},
            {"Lang_TrayUI", "UIを開く"}, {"Lang_TrayExit", "終了"},
            {"Lang_PingErr", "エラー"}, {"Lang_PingTime", "タイムアウト"}, {"Lang_PingMs", "ms"},
            {"Lang_TimeDays", "日"}, {"Lang_TimeHours", "時間"}, {"Lang_TimeMinutes", "分"}, {"Lang_TimeSeconds", "秒"},
            {"Lang_FldName", "プロファイル名"}, {"Lang_FldAddress", "アドレス/IP"},
            {"Lang_FldPort", "ポート"}, {"Lang_FldPassword", "パスワード"},
            {"Lang_FldUuid", "UUID / ユーザー名"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "Fingerprint"}, {"Lang_FldInsecure", "TLSエラーを無視"},
            {"Lang_FldFlow", "Flow"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "Transport"}, {"Lang_FldPath", "パス"},
            {"Lang_FldHost", "Host"}, {"Lang_FldSecurity", "Security"}, {"Lang_FldAlterId", "AlterID"},
            {"Lang_FldMethod", "暗号化方式"}, {"Lang_FldPubKey", "Public Key"},
            {"Lang_FldPrivKey", "Private Key"}, {"Lang_FldLocalAddr", "ローカルIP"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "アップロード Mbps"},
            {"Lang_FldDownMbps", "ダウンロード Mbps"}, {"Lang_FldObfs", "Obfs タイプ"}, {"Lang_FldObfsPass", "Obfs パスワード"},
            {"Lang_MsgRoutesChanged", "ルートが変更されました。再接続しています..."},
            {"Lang_MsgLogLevelChanged", "ログレベルが変更されました。再接続しています..."},
            {"Lang_ErrQrTooLong", "QRコードの生成に失敗しました：リンクが長すぎます"},
            {"Lang_ErrQrGen", "QRコードの生成中にエラーが発生しました"}
        },
        ["fa"] = new()
        {
            {"Lang_Protocol", "پروتکل"}, {"Lang_CtxConnect", "اتصال"},
            {"Lang_CtxEdit", "ویرایش پروفایل"}, {"Lang_CtxCopyLink", "کپی لینک"},
            {"Lang_CtxMoveUp", "انتقال به بالا"}, {"Lang_CtxMoveDown", "انتقال به پایین"},
            {"Lang_CtxDelete", "حذف"}, {"Lang_NavMain", "خانه"}, {"Lang_NavSettings", "تنظیمات"},
            {"Lang_NavLogs", "گزارش‌ها"}, {"Lang_Mode", "حالت:"}, {"Lang_AddProfile", "افزودن پروفایل"},
            {"Lang_RefreshPing", "بروزرسانی پینگ"}, {"Lang_EmptyTitle", "بدون پروفایل"},
            {"Lang_EmptyDesc", "برای شروع «افزودن پروفایل» را بزنید"}, {"Lang_SettingsTitle", "تنظیمات"},
            {"Lang_StartupWork", "راه‌اندازی"}, {"Lang_AutoStart", "اجرا در زمان شروع ویندوز"},
            {"Lang_AutoConnectVpn", "اتصال خودکار VPN"}, {"Lang_AutoConnectProxy", "اتصال خودکار Proxy"},
            {"Lang_StartMinimized", "اجرا کوچک شده"}, {"Lang_CustomConfig", "استفاده از config.json اختصاصی"},
            {"Lang_Appearance", "ظاهر"}, {"Lang_Language", "زبان"}, {"Lang_Theme", "پوسته"},
            {"Lang_ThemeDark", "تاریک"}, {"Lang_ThemeLight", "روشن"},
            {"Lang_VisualEffects", "جلوه‌های بصری"}, {"Lang_DisableAnim", "غیرفعال‌سازی انیمیشن‌ها"},
            {"Lang_NetRouting", "شبکه و مسیریابی"}, {"Lang_RemoteDns", "دی‌ان‌اس (DNS)"},
            {"Lang_ConfigureRoutes", "تنظیم قوانین"}, {"Lang_AdvancedSettings", "تنظیمات پیشرفته"},
            {"Lang_ProxyPort", "پورت پروکسی"}, {"Lang_AllowLan", "اجازه اتصال از شبکه محلی (LAN)"},
            {"Lang_AllowLanHint", "به سایر دستگاه‌های شبکه اجازه می‌دهد به این پروکسی متصل شوند."},
            {"Lang_TunStack", "پشته TUN"},
            {"Lang_CoreLogs", "گزارش‌ها"}, {"Lang_LogLevel", "سطح: "}, {"Lang_AutoScroll", "پیمایش خودکار"},
            {"Lang_Copy", "کپی"}, {"Lang_Clear", "پاک‌کردن"},
            {"Lang_AddProfileTitle", "افزودن پروفایل"}, {"Lang_PasteClipboard", "از کلیپ‌بورد"},
            {"Lang_OrManual", "یا دستی:"}, {"Lang_Cancel", "لغو"}, {"Lang_Save", "ذخیره"},
            {"Lang_RoutingTitle", "قوانین مسیریابی"}, {"Lang_RoutingMode", "حالت:"},
            {"Lang_RouteBypass", "پروکسی برای همه به جز..."}, {"Lang_RouteOnly", "پروکسی فقط برای..."},
            {"Lang_DomainsDesc", "دامنه‌ها"}, {"Lang_Add", "افزودن"},
            {"Lang_AppsDesc", "برنامه‌ها"}, {"Lang_Done", "انجام شد"},
            {"Lang_QRTitle", "بارکد پروفایل (QR)"}, {"Lang_CopyLink", "کپی لینک"},
            {"Lang_SearchPlaceholder", "جستجوی پروفایل..."}, {"Lang_BtnEnableVpn", "روشن کردن VPN"},
            {"Lang_BtnDisableVpn", "خاموش کردن VPN"}, {"Lang_BtnEnableProxy", "روشن کردن PROXY"},
            {"Lang_BtnDisableProxy", "خاموش کردن PROXY"},
            {"Lang_ErrInvalidAddress", "آدرس نامعتبر"}, {"Lang_ErrInvalidPort", "پورت نامعتبر"},
            {"Lang_ErrSelectProfile", "ابتدا یک پروفایل انتخاب کنید!"}, {"Lang_ErrWintun", "خطا: wintun.dll یافت نشد!"},
            {"Lang_ErrCrash", "هسته Sing-box متوقف شد."},
            {"Lang_MsgCopied", "کپی شد"}, {"Lang_MsgImportOk", "با موفقیت وارد شد"},
            {"Lang_ErrImport", "خطای وارد کردن"},
            {"Lang_LocUnknown", "نامشخص"}, {"Lang_TrayRunning", "برنامه در پس‌زمینه در حال اجرا است."},
            {"Lang_TrayUI", "رابط کاربری"}, {"Lang_TrayExit", "خروج"},
            {"Lang_PingErr", "خطا"}, {"Lang_PingTime", "تایم‌اوت"}, {"Lang_PingMs", "ms"},
            {"Lang_FldName", "نام پروفایل"}, {"Lang_FldAddress", "آدرس/آی‌پی"},
            {"Lang_FldPort", "پورت"}, {"Lang_FldPassword", "رمز عبور"},
            {"Lang_FldUuid", "UUID / نام کاربری"}, {"Lang_FldSni", "SNI"},
            {"Lang_FldFp", "Fingerprint"}, {"Lang_FldInsecure", "نادیده گرفتن خطای TLS"},
            {"Lang_FldFlow", "Flow"}, {"Lang_FldPbk", "Reality PBK"}, {"Lang_FldSid", "Reality SID"},
            {"Lang_FldTransport", "Transport"}, {"Lang_FldPath", "مسیر (Path)"},
            {"Lang_FldHost", "Host"}, {"Lang_FldSecurity", "Security"}, {"Lang_FldAlterId", "AlterID"},
            {"Lang_FldMethod", "روش رمزگذاری"}, {"Lang_FldPubKey", "کلید عمومی"},
            {"Lang_FldPrivKey", "کلید خصوصی"}, {"Lang_FldLocalAddr", "آدرس محلی"},
            {"Lang_FldMtu", "MTU"}, {"Lang_FldAlpn", "ALPN"}, {"Lang_FldUpMbps", "آپلود Mbps"},
            {"Lang_FldDownMbps", "دانلود Mbps"}, {"Lang_FldObfs", "نوع Obfs"}, {"Lang_FldObfsPass", "رمز Obfs"},
            {"Lang_MsgRoutesChanged", "مسیرها تغییر کردند، در حال اتصال مجدد..."},
            {"Lang_MsgLogLevelChanged", "سطح گزارش تغییر کرد، در حال اتصال مجدد..."},
            {"Lang_ErrQrTooLong", "تولید بارکد (QR) ناموفق بود: لینک بیش از حد طولانی است"},
            {"Lang_ErrQrGen", "خطایی در تولید بارکد (QR) رخ داد"}
        }
    };

    public static string GetString(string key) =>
        Application.Current?.Resources is { } res && res.Contains(key) ? res[key]?.ToString() ?? key : key;

    public MainWindow()
    {
        Task.Run(() => { try { using var t = new TcpClient(); t.Connect("8.8.8.8", 53); } catch { } });

        LoadSettings();

        bool isAutoStart = Environment.GetCommandLineArgs().Contains("--autostart");
        if (_currentSettings.StartMinimized && isAutoStart)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }

        InitializeTrayIcon();
        InitializeComponent();

        if (CmbLanguage != null)
        {
            CmbLanguage.ItemsSource = new List<LanguageItem>
            {
                new() { Name = "English", Code = "en" },
                new() { Name = "Русский", Code = "ru" },
                new() { Name = "Українська", Code = "uk" },
                new() { Name = "Deutsch", Code = "de" },
                new() { Name = "中文 (简体)", Code = "zh-Hans" },
                new() { Name = "中文 (繁體)", Code = "zh-Hant" },
                new() { Name = "日本語", Code = "ja" },
                new() { Name = "فارسی", Code = "fa" }
            };
        }

        ApplyLanguage();
        ApplyTheme(_currentSettings.Theme);

        if (ListRoutingDomains != null) ListRoutingDomains.ItemsSource = RoutingDomainsList;
        if (ListRoutingApps != null) ListRoutingApps.ItemsSource = RoutingAppsList;
        if (LvProfiles != null) LvProfiles.ItemsSource = _profiles;

        ApplySettingsToUI();
        UpdateButtonText();
        UpdateEmptyState();

        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _pingTimer.Tick += (s, e) =>
        {
            PingManager.Clear();
            if (LvProfiles?.SelectedItem is VpnProfile selectedProfile) _ = selectedProfile.CheckPingAsync();
        };
        _pingTimer.Start();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (s, ev) =>
        {
            if (!_isRunning) return;
            if (TxtConnectionTime != null) TxtConnectionTime.Text = FormatUptime(DateTime.Now - _connectionStartTime, _currentSettings.Language);
            UpdateTraffic();
        };

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _logTimer.Tick += FlushLogs;
        _logTimer.Start();

        Loaded += async (s, e) =>
        {
            ApplyVisualEffects();
            if (_currentSettings.StartMinimized && isAutoStart) Hide();
            UpdateMenuIndicator(BtnNavMain, 0);

            if (_currentSettings.AutoConnect && isAutoStart && LvProfiles?.SelectedItem != null && !_isRunning)
                await StartVPNAsync();
        };
    }

    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: false } cb && VisualTreeHelper.GetParent(cb) is UIElement parent)
        {
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        }
    }

    private void ApplyLanguage()
    {
        var lang = _currentSettings.Language;
        if (string.IsNullOrEmpty(lang) || !Locales.ContainsKey(lang)) lang = "en";

        foreach (var kvp in Locales[lang])
            Application.Current.Resources[kvp.Key] = kvp.Value;

        if (AppInterfaceGrid != null)
            AppInterfaceGrid.FlowDirection = lang == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (TxtLangHint != null) TxtLangHint.Visibility = lang == "en" ? Visibility.Collapsed : Visibility.Visible;

        if (_trayNavMainItem != null) _trayNavMainItem.Text = GetString("Lang_NavMain");
        if (_trayNavSettingsItem != null) _trayNavSettingsItem.Text = GetString("Lang_NavSettings");
        if (_trayNavLogsItem != null) _trayNavLogsItem.Text = GetString("Lang_NavLogs");
        if (_trayExitItem != null) _trayExitItem.Text = GetString("Lang_TrayExit");

        UpdateButtonText();
        UpdateDynamicTexts();

        foreach (var p in _profiles) p.ForceUpdatePing();

        if (OverlayImport?.Visibility == Visibility.Visible && CmbProtocol?.SelectedItem is ComboBoxItem item)
            UpdateManualFields(item.Content?.ToString() ?? "vless", true);

        if (_isRunning && TxtLocation != null) _ = FetchLocationAsync(true);
    }

    private TimeSpan GetDuration(double seconds) =>
        _currentSettings.DisableAnimations ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon { Text = "NeonV", Visible = true };
        UpdateTrayIconImage(false);
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;

        var contextMenu = new Forms.ContextMenuStrip();
        var boldFont = new System.Drawing.Font(System.Drawing.SystemFonts.MenuFont.FontFamily, System.Drawing.SystemFonts.MenuFont.Size, System.Drawing.FontStyle.Bold);

        _trayToggleItem = new Forms.ToolStripMenuItem("...", null, (_, _) => Dispatcher.InvokeAsync(ToggleVpnAsync)) { Font = boldFont };
        contextMenu.Items.Add(_trayToggleItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayNavMainItem = new Forms.ToolStripMenuItem(GetString("Lang_NavMain"), null, (_, _) => ShowTab(0));
        _trayNavSettingsItem = new Forms.ToolStripMenuItem(GetString("Lang_NavSettings"), null, (_, _) => ShowTab(1));
        _trayNavLogsItem = new Forms.ToolStripMenuItem(GetString("Lang_NavLogs"), null, (_, _) => ShowTab(2));

        contextMenu.Items.Add(_trayNavMainItem);
        contextMenu.Items.Add(_trayNavSettingsItem);
        contextMenu.Items.Add(_trayNavLogsItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayModeItem = new Forms.ToolStripMenuItem("Mode", null, (_, _) => ToggleMode());
        contextMenu.Items.Add(_trayModeItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayExitItem = new Forms.ToolStripMenuItem(GetString("Lang_TrayExit"), null, (_, _) => ExitApplication());
        contextMenu.Items.Add(_trayExitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void UpdateTrayIconImage(bool isActive)
    {
        if (_notifyIcon == null) return;
        try
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new System.Drawing.Rectangle(2, 2, 28, 28);
            var brushColor = isActive ? System.Drawing.Color.FromArgb(103, 80, 164) : System.Drawing.Color.Gray;

            using var brush = new System.Drawing.SolidBrush(brushColor);
            g.FillEllipse(brush, rect);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3);
            g.DrawEllipse(pen, rect);

            _notifyIcon.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch { }
    }

    private class DarkMenuColorTable : Forms.ProfessionalColorTable
    {
        public DarkMenuColorTable() { UseSystemColors = false; }
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(33, 31, 38);
        public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(43, 41, 48);
        public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(43, 41, 48);
        public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(43, 41, 48);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(73, 69, 79);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(73, 69, 79);
        public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(33, 31, 38);
    }

    private class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                var rect = new System.Drawing.Rectangle(1, 0, e.Item.Width - 2, e.Item.Height - 1);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(74, 68, 88));
                e.Graphics.FillRectangle(brush, rect);
                using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(147, 143, 153));
                e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            else base.OnRenderMenuItemBackground(e);
        }
    }

    private void UpdateTrayTheme(bool isDark)
    {
        var menu = _notifyIcon?.ContextMenuStrip;
        if (menu == null) return;

        if (isDark)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = System.Drawing.Color.FromArgb(33, 31, 38);
            foreach (Forms.ToolStripItem item in menu.Items)
                item.ForeColor = System.Drawing.Color.FromArgb(230, 224, 233);
        }
        else
        {
            menu.Renderer = new Forms.ToolStripProfessionalRenderer();
            menu.BackColor = System.Drawing.SystemColors.Control;
            foreach (Forms.ToolStripItem item in menu.Items)
                item.ForeColor = System.Drawing.SystemColors.ControlText;
        }
    }

    private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;

        Dispatcher.Invoke(() =>
        {
            if (IsVisible && GetForegroundWindow() == new WindowInteropHelper(this).Handle)
                Hide();
            else
            {
                Show();
                ShowInTaskbar = true;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Focus();
            }
        });
    }

    private void ShowTab(int index)
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            if (index == 0) BtnNavMain_Click(null!, null!);
            else if (index == 1) BtnNavSettings_Click(null!, null!);
            else if (index == 2) BtnNavLogs_Click(null!, null!);
        });
    }

    private void ToggleMode()
    {
        Dispatcher.Invoke(() => { if (CmbMode != null) CmbMode.SelectedIndex = CmbMode.SelectedIndex == 0 ? 1 : 0; });
    }

    private async void ExitApplication()
    {
        _isRealClose = true;
        if (_isRunning) await StopVPNAsync();
        Dispatcher.Invoke(Close);
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            if (RootBorder != null) RootBorder.Margin = new Thickness(7);
            if (IconMaximize != null) IconMaximize.Text = "\xE923";
        }
        else
        {
            if (RootBorder != null) RootBorder.Margin = new Thickness(0);
            if (IconMaximize != null) IconMaximize.Text = "\xE922";
        }
    }

    private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Grid grid) { grid.Focus(); Keyboard.ClearFocus(); }
    }

    private static string Pluralize(int n, string unit, string lang)
    {
        int form = 0;
        if (lang is "ru" or "uk")
        {
            int m10 = n % 10, m100 = n % 100;
            form = (m100 is >= 11 and <= 14) ? 2 : (m10 == 1) ? 0 : (m10 is >= 2 and <= 4) ? 1 : 2;
        }
        else if (lang is "en" or "de") form = n == 1 ? 0 : 1;

        return lang switch
        {
            "ru" => unit switch { "month" => new[] { "месяц", "месяца", "месяцев" }[form], "day" => new[] { "день", "дня", "дней" }[form], "hour" => new[] { "час", "часа", "часов" }[form], "minute" => new[] { "минута", "минуты", "минут" }[form], _ => new[] { "секунда", "секунды", "секунд" }[form] },
            "uk" => unit switch { "month" => new[] { "місяць", "місяці", "місяців" }[form], "day" => new[] { "день", "дні", "днів" }[form], "hour" => new[] { "година", "години", "годин" }[form], "minute" => new[] { "хвилина", "хвилини", "хвилин" }[form], _ => new[] { "секунда", "секунди", "секунд" }[form] },
            "de" => unit switch { "month" => new[] { "Monat", "Monate" }[form], "day" => new[] { "Tag", "Tage" }[form], "hour" => new[] { "Stunde", "Stunden" }[form], "minute" => new[] { "Minute", "Minuten" }[form], _ => new[] { "Sekunde", "Sekunden" }[form] },
            "zh-Hans" => unit switch { "month" => "月", "day" => "天", "hour" => "小时", "minute" => "分钟", _ => "秒" },
            "zh-Hant" => unit switch { "month" => "月", "day" => "天", "hour" => "小時", "minute" => "分鐘", _ => "秒" },
            "ja" => unit switch { "month" => "ヶ月", "day" => "日", "hour" => "時間", "minute" => "分", _ => "秒" },
            "fa" => unit switch { "month" => "ماه", "day" => "روز", "hour" => "ساعت", "minute" => "دقیقه", _ => "ثانیه" },
            _ => unit switch { "month" => new[] { "month", "months" }[form], "day" => new[] { "day", "days" }[form], "hour" => new[] { "hour", "hours" }[form], "minute" => new[] { "minute", "minutes" }[form], _ => new[] { "second", "seconds" }[form] }
        };
    }

    private static string FormatUptime(TimeSpan ts, string lang)
    {
        if (string.IsNullOrEmpty(lang)) lang = "en";

        int totalMonths = (int)(ts.TotalDays / 30);
        if (totalMonths > 0) return $"{totalMonths} {Pluralize(totalMonths, "month", lang)}";

        if (ts.Days > 0) return $"{ts.Days} {Pluralize(ts.Days, "day", lang)}";
        if (ts.Hours > 0) return $"{ts.Hours} {Pluralize(ts.Hours, "hour", lang)}";
        if (ts.Minutes > 0) return $"{ts.Minutes} {Pluralize(ts.Minutes, "minute", lang)}";

        return $"{ts.Seconds} {Pluralize(ts.Seconds, "second", lang)}";
    }

    private void ApplyTheme(string theme)
    {
        IsDarkTheme = theme == "Dark";
        Resources["ToggleBtnBgColor"] = new SolidColorBrush(Color.FromRgb(103, 80, 164));
        Resources["ErrorColor"] = new SolidColorBrush(IsDarkTheme ? Color.FromRgb(248, 113, 113) : Color.FromRgb(220, 38, 38));

        if (!IsDarkTheme)
        {
            Resources["BgColor"] = new SolidColorBrush(Color.FromRgb(254, 247, 255));
            Resources["PanelColor"] = new SolidColorBrush(Color.FromRgb(243, 237, 247));
            Resources["ConsoleBgColor"] = new SolidColorBrush(Color.FromRgb(240, 240, 240));
            Resources["TextColor"] = new SolidColorBrush(Color.FromRgb(29, 27, 32));
            Resources["TextMutedColor"] = new SolidColorBrush(Color.FromRgb(73, 69, 79));
            Resources["AccentColor"] = new SolidColorBrush(Color.FromRgb(103, 80, 164));
            Resources["OnAccentColor"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            Resources["HoverColor"] = new SolidColorBrush(Color.FromRgb(232, 222, 248));
            Resources["OutlineColor"] = new SolidColorBrush(Color.FromRgb(121, 116, 126));
        }
        else
        {
            Resources["BgColor"] = new SolidColorBrush(Color.FromRgb(20, 18, 24));
            Resources["PanelColor"] = new SolidColorBrush(Color.FromRgb(36, 34, 41));
            Resources["ConsoleBgColor"] = new SolidColorBrush(Color.FromRgb(10, 10, 12));
            Resources["TextColor"] = new SolidColorBrush(Color.FromRgb(230, 224, 233));
            Resources["TextMutedColor"] = new SolidColorBrush(Color.FromRgb(202, 196, 208));
            Resources["AccentColor"] = new SolidColorBrush(Color.FromRgb(208, 188, 255));
            Resources["OnAccentColor"] = new SolidColorBrush(Color.FromRgb(56, 30, 114));
            Resources["HoverColor"] = new SolidColorBrush(Color.FromRgb(74, 68, 88));
            Resources["OutlineColor"] = new SolidColorBrush(Color.FromRgb(147, 143, 153));
        }

        if (IsLoaded && FindResource("TextMutedColor") is Brush mutedBrush && FindResource("AccentColor") is Brush accentBrush)
        {
            if (BtnNavMain != null) BtnNavMain.Foreground = mutedBrush;
            if (BtnNavSettings != null) BtnNavSettings.Foreground = mutedBrush;
            if (BtnNavLogs != null) BtnNavLogs.Foreground = mutedBrush;

            if (ViewMain?.Visibility == Visibility.Visible && BtnNavMain != null) BtnNavMain.Foreground = accentBrush;
            else if (ViewSettings?.Visibility == Visibility.Visible && BtnNavSettings != null) BtnNavSettings.Foreground = accentBrush;
            else if (BtnNavLogs != null) BtnNavLogs.Foreground = accentBrush;
        }

        foreach (var p in _profiles) p.ForceUpdatePing();

        UpdateButtonText();
        UpdateTrayTheme(IsDarkTheme);
        ApplyVisualEffects();
    }

    private void ApplyVisualEffects()
    {
        if (BtnToggle != null)
        {
            if (_currentSettings.EnableVisualEffects && FindResource("AccentColor") is SolidColorBrush accent)
            {
                BtnToggle.Effect = new DropShadowEffect
                {
                    Color = _isRunning ? Color.FromRgb(179, 38, 30) : accent.Color,
                    BlurRadius = 25,
                    ShadowDepth = 0,
                    Opacity = IsDarkTheme ? 0.6 : 0.3
                };
            }
            else BtnToggle.Effect = null;
        }

        if (MenuIndicator != null)
        {
            if (_currentSettings.EnableVisualEffects && FindResource("AccentColor") is SolidColorBrush accent)
            {
                MenuIndicator.Effect = new DropShadowEffect
                {
                    Color = accent.Color,
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = IsDarkTheme ? 0.9 : 0.5
                };
            }
            else MenuIndicator.Effect = null;
        }
    }

    private void LoadSettings()
    {
        bool isFirstLaunch = !File.Exists(Path.Combine(_baseDirectory, "settings.json"));

        try
        {
            if (!isFirstLaunch)
            {
                string json = File.ReadAllText(Path.Combine(_baseDirectory, "settings.json"));
                if (JsonSerializer.Deserialize<AppSettings>(json) is { } saved) _currentSettings = saved;
            }
        }
        catch { }

        if (isFirstLaunch)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int i && i == 1) _currentSettings.Theme = "Light";
            }
            catch { }

            var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            _currentSettings.Language = new[] { "ru", "uk", "en", "zh", "de", "ja", "fa" }.Contains(lang) ? lang : "en";
            if (lang == "zh") _currentSettings.Language = System.Globalization.CultureInfo.CurrentUICulture.Name.Contains("Hant") ? "zh-Hant" : "zh-Hans";
        }

        foreach (var p in _currentSettings.Profiles)
        {
            if (string.IsNullOrEmpty(p.Address) && !string.IsNullOrEmpty(p.Link)) ParseLinkIntoProfile(p, p.Link);
            _profiles.Add(p);
            _ = p.CheckPingAsync();
        }

        RoutingDomainsList.Clear();
        foreach (var d in _currentSettings.RoutingDomains) RoutingDomainsList.Add(d);

        RoutingAppsList.Clear();
        foreach (var a in _currentSettings.RoutingApps) RoutingAppsList.Add(a);
    }

    private void ApplySettingsToUI()
    {
        _isUpdatingSettings = true;

        if (_currentSettings.SelectedProfileIndex >= 0 && _currentSettings.SelectedProfileIndex < _profiles.Count && LvProfiles != null)
            LvProfiles.SelectedIndex = _currentSettings.SelectedProfileIndex;

        if (TxtDns != null) TxtDns.Text = _currentSettings.RemoteDns;
        if (ChkAutoStart != null) ChkAutoStart.IsChecked = _currentSettings.AutoStart;
        if (ChkAutoConnect != null) ChkAutoConnect.IsChecked = _currentSettings.AutoConnect;
        if (ChkStartMinimized != null) ChkStartMinimized.IsChecked = _currentSettings.StartMinimized;
        if (ChkUseCustomConfig != null) ChkUseCustomConfig.IsChecked = _currentSettings.UseCustomConfig;
        if (ChkVisualEffects != null) ChkVisualEffects.IsChecked = _currentSettings.EnableVisualEffects;
        if (ChkDisableAnimations != null) ChkDisableAnimations.IsChecked = _currentSettings.DisableAnimations;
        if (CmbTheme != null) CmbTheme.SelectedIndex = _currentSettings.Theme == "Light" ? 1 : 0;
        if (CmbMode != null) CmbMode.SelectedIndex = _currentSettings.Mode == "Proxy" ? 1 : 0;
        if (TxtProxyPort != null) TxtProxyPort.Text = _currentSettings.ProxyPort.ToString();
        if (ChkAllowLan != null) ChkAllowLan.IsChecked = _currentSettings.AllowLan;

        if (CmbTunStack != null)
            CmbTunStack.SelectedItem = CmbTunStack.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _currentSettings.TunStack);

        if (CmbLogLevel != null)
            CmbLogLevel.SelectedItem = CmbLogLevel.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _currentSettings.LogLevel);

        if (CmbLanguage != null) CmbLanguage.SelectedValue = _currentSettings.Language;

        _isUpdatingSettings = false;
        UpdateDynamicTexts();
    }

    private void UpdateEmptyState()
    {
        if (EmptyStatePanel != null)
            EmptyStatePanel.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string GetTrayModeText()
    {
        string prefix = _currentSettings.Language switch
        {
            "ru" => "Режим",
            "uk" => "Режим",
            "de" => "Modus",
            "zh-Hans" or "zh-Hant" => "模式",
            "ja" => "モード",
            "fa" => "حالت",
            _ => "Mode"
        };
        return $"{prefix}: {_currentSettings.Mode}";
    }

    private void UpdateDynamicTexts()
    {
        if (ChkAutoConnect != null)
            ChkAutoConnect.Content = GetString(_currentSettings.Mode == "Proxy" ? "Lang_AutoConnectProxy" : "Lang_AutoConnectVpn");

        if (_trayModeItem != null)
            _trayModeItem.Text = GetTrayModeText();
    }

    private void SaveSettings()
    {
        if (_isUpdatingSettings) return;
        try
        {
            _currentSettings.Profiles = _profiles.ToList();
            if (LvProfiles != null) _currentSettings.SelectedProfileIndex = LvProfiles.SelectedIndex;
            if (CmbTheme != null) _currentSettings.Theme = CmbTheme.SelectedIndex == 1 ? "Light" : "Dark";
            if (CmbMode != null) _currentSettings.Mode = CmbMode.SelectedIndex == 1 ? "Proxy" : "TUN";
            if (TxtDns != null) _currentSettings.RemoteDns = string.IsNullOrWhiteSpace(TxtDns.Text) ? "8.8.8.8" : TxtDns.Text.Trim();

            bool oldAutoStart = _currentSettings.AutoStart;
            if (ChkAutoStart != null) _currentSettings.AutoStart = ChkAutoStart.IsChecked ?? false;
            if (ChkAutoConnect != null) _currentSettings.AutoConnect = ChkAutoConnect.IsChecked ?? false;
            if (ChkStartMinimized != null) _currentSettings.StartMinimized = ChkStartMinimized.IsChecked ?? false;
            if (ChkUseCustomConfig != null) _currentSettings.UseCustomConfig = ChkUseCustomConfig.IsChecked ?? false;

            bool oldVisual = _currentSettings.EnableVisualEffects;
            if (ChkVisualEffects != null) _currentSettings.EnableVisualEffects = ChkVisualEffects.IsChecked ?? false;
            if (ChkDisableAnimations != null) _currentSettings.DisableAnimations = ChkDisableAnimations.IsChecked ?? false;

            if (TxtProxyPort != null && int.TryParse(TxtProxyPort.Text, out int port)) _currentSettings.ProxyPort = port;
            if (ChkAllowLan != null) _currentSettings.AllowLan = ChkAllowLan.IsChecked ?? false;

            if (CmbTunStack?.SelectedItem is ComboBoxItem tsItem && tsItem.Tag != null) _currentSettings.TunStack = tsItem.Tag.ToString() ?? "system";
            if (CmbLogLevel?.SelectedItem is ComboBoxItem llItem && llItem.Tag != null) _currentSettings.LogLevel = llItem.Tag.ToString() ?? "info";
            if (CmbLanguage?.SelectedValue is string langValue) _currentSettings.Language = langValue;

            string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_baseDirectory, "settings.json"), json);

            if (oldAutoStart != _currentSettings.AutoStart) ManageAutoStart(_currentSettings.AutoStart);
            if (oldVisual != _currentSettings.EnableVisualEffects) ApplyVisualEffects();
        }
        catch { }
    }

    private static void ManageAutoStart(bool enable)
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            string taskName = $"NeonV_Autostart_{Environment.UserName}";

            if (enable)
            {
                string userId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{userId}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{userId}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
  <Actions Context=""Author""><Exec><Command>{exePath}</Command><Arguments>--autostart</Arguments></Exec></Actions>
</Task>";
                string tempFile = Path.Combine(Path.GetTempPath(), $"{taskName}.xml");
                File.WriteAllText(tempFile, xml);

                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/create /tn \"{taskName}\" /xml \"{tempFile}\" /f") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                p?.WaitForExit();
                try { File.Delete(tempFile); } catch { }
            }
            else
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{taskName}\" /f") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                p?.WaitForExit();
            }
        }
        catch { }
    }

    private void ShowNotification(string message, bool isError = false)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (TxtNotification != null) TxtNotification.Text = message;
            if (FindResource("TextColor") is Brush textColor && TxtNotification != null)
                TxtNotification.Foreground = isError ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : textColor;

            if (NotificationSnackbar != null)
            {
                NotificationSnackbar.IsHitTestVisible = true;
                NotificationSnackbar.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
                await Task.Delay(3000);
                NotificationSnackbar.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2) });
                NotificationSnackbar.IsHitTestVisible = false;
            }
        });
    }

    private void NumberValidation(object sender, TextCompositionEventArgs e) => e.Handled = !int.TryParse(e.Text, out _);

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        ApplyTheme(CmbTheme?.SelectedIndex == 1 ? "Light" : "Dark");
        SaveSettings();
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings || CmbLanguage?.SelectedValue is not string lang) return;
        _currentSettings.Language = lang;
        ApplyLanguage();
        SaveSettings();
    }

    private void CmbSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingSettings) SaveSettings();
    }

    private async void CmbLogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        if (_isRunning)
        {
            ShowNotification(GetString("Lang_MsgLogLevelChanged"));
            await RestartVpnAsync();
        }
    }

    private async void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        UpdateButtonText();
        UpdateDynamicTexts();

        if (_isRunning)
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = false;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = false;

            await StopVPNAsync();
            await Task.Delay(500);
            await StartVPNAsync();

            if (BtnToggle != null) BtnToggle.IsEnabled = true;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
        }
    }

    private void TxtSettings_Changed(object sender, RoutedEventArgs e) => SaveSettings();

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(_profiles) is { } view)
        {
            view.Filter = item =>
            {
                if (string.IsNullOrWhiteSpace(TxtSearch?.Text)) return true;
                var p = (VpnProfile)item;
                return p.Name.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) ||
                       p.Address.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase);
            };
        }
    }

    private void LvProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSettings();
        if (LvProfiles?.SelectedItem is VpnProfile p) _ = p.CheckPingAsync();
    }

    private void LvProfiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && LvProfiles?.SelectedItem is VpnProfile p)
        {
            _profiles.Remove(p);
            SaveSettings();
            UpdateEmptyState();
        }
    }

    private string FormatTrayText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        var lower = input.ToLower().Replace("vpn", "VPN").Replace("proxy", "Proxy");
        return char.ToUpper(lower[0]) + lower.Substring(1);
    }

    private void UpdateButtonText()
    {
        if (BtnToggleText == null || BtnToggle == null) return;
        string mode = _currentSettings.Mode;

        if (_isRunning)
        {
            string disableText = GetString(mode == "Proxy" ? "Lang_BtnDisableProxy" : "Lang_BtnDisableVpn");
            BtnToggleText.Text = disableText;
            BtnToggle.Background = new SolidColorBrush(Color.FromRgb(179, 38, 30));
            if (_trayToggleItem != null) _trayToggleItem.Text = FormatTrayText(disableText);
        }
        else
        {
            string enableText = GetString(mode == "Proxy" ? "Lang_BtnEnableProxy" : "Lang_BtnEnableVpn");
            BtnToggleText.Text = enableText;
            BtnToggle.SetResourceReference(Control.BackgroundProperty, "ToggleBtnBgColor");
            if (_trayToggleItem != null) _trayToggleItem.Text = FormatTrayText(enableText);
        }
        ApplyVisualEffects();
    }

    private void SwitchTab(Grid? target)
    {
        if (target != null && target.Visibility == Visibility.Visible) return;

        if (ViewMain != null) ViewMain.Visibility = Visibility.Collapsed;
        if (ViewSettings != null) ViewSettings.Visibility = Visibility.Collapsed;
        if (ViewLogs != null) ViewLogs.Visibility = Visibility.Collapsed;

        if (target != null)
        {
            target.Visibility = Visibility.Visible;
            target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation { From = 0, To = 1, Duration = GetDuration(0.25) });
        }
    }

    private void UpdateMenuIndicator(Button? activeBtn, double targetY)
    {
        if (FindResource("TextMutedColor") is Brush muted && FindResource("AccentColor") is Brush accent)
        {
            if (BtnNavMain != null) BtnNavMain.Foreground = muted;
            if (BtnNavSettings != null) BtnNavSettings.Foreground = muted;
            if (BtnNavLogs != null) BtnNavLogs.Foreground = muted;
            if (activeBtn != null) activeBtn.Foreground = accent;
        }

        MenuIndicator?.RenderTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = targetY, Duration = GetDuration(0.25), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void BtnNavMain_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewMain); UpdateMenuIndicator(BtnNavMain, 0); }
    private void BtnNavSettings_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewSettings); UpdateMenuIndicator(BtnNavSettings, 48); }
    private void BtnNavLogs_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewLogs); UpdateMenuIndicator(BtnNavLogs, 96); }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && OverlayImport?.Visibility != Visibility.Visible && OverlayRouting?.Visibility != Visibility.Visible && OverlayQR?.Visibility != Visibility.Visible)
        {
            PerformClipboardImport();
            e.Handled = true;
        }
    }

    private async Task RestartVpnAsync()
    {
        if (!_isRunning) return;

        if (BtnToggle != null) BtnToggle.IsEnabled = false;
        if (_trayToggleItem != null) _trayToggleItem.Enabled = false;

        await StopVPNAsync();
        await Task.Delay(500);
        await StartVPNAsync();

        if (BtnToggle != null) BtnToggle.IsEnabled = true;
        if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
    }

    private void BtnOpenImport_Click(object sender, RoutedEventArgs e)
    {
        _editingProfile = null;
        if (TxtImportTitle != null) TxtImportTitle.Text = GetString("Lang_AddProfileTitle");
        if (BtnPasteImport != null) BtnPasteImport.Visibility = Visibility.Visible;
        if (ImportSeparator != null) ImportSeparator.Visibility = Visibility.Visible;
        if (TxtOrManual != null) TxtOrManual.Visibility = Visibility.Visible;
        if (OverlayImport != null) OverlayImport.Visibility = Visibility.Visible;
        if (CmbProtocol != null) CmbProtocol.SelectedIndex = 0;

        UpdateManualFields("vless");

        OverlayImportBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayImportCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayImportTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseImportOverlay()
    {
        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlayImport != null) OverlayImport.Visibility = Visibility.Collapsed; };

        OverlayImportBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayImportCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayImportTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlayImportBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseImportOverlay();
    private void BtnCancelImport_Click(object sender, RoutedEventArgs e) => CloseImportOverlay();
    private void BtnPasteFromClipboard_Click(object sender, RoutedEventArgs e) => PerformClipboardImport();

    private void ShowQROverlay(string link)
    {
        _currentQrLink = link;

        if (ImgQR != null) ImgQR.Source = null;

        if (link.Length > 800)
        {
            ShowNotification(GetString("Lang_ErrQrTooLong"), true);
        }
        else
        {
            try
            {
                var qrGenerator = new QRCodeGenerator();
                var qrCodeData = qrGenerator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);

                var qrCode = new QRCode(qrCodeData);
                using var bitmap = qrCode.GetGraphic(20);

                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                if (ImgQR != null) ImgQR.Source = bmp;
            }
            catch (Exception)
            {
                ShowNotification(GetString("Lang_ErrQrGen"), true);
            }
        }

        if (OverlayQR != null) OverlayQR.Visibility = Visibility.Visible;
        OverlayQRBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayQRCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayQRTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseQROverlay()
    {
        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlayQR != null) OverlayQR.Visibility = Visibility.Collapsed; };

        OverlayQRBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayQRCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayQRTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlayQRBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseQROverlay();

    private void BtnCopyLinkFromQR_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentQrLink))
        {
            Clipboard.SetText(_currentQrLink);
            ShowNotification(GetString("Lang_MsgCopied"));
            CloseQROverlay();
        }
    }

    private void BtnOpenRouting_Click(object sender, RoutedEventArgs e)
    {
        RoutingDomainsList.Clear();
        foreach (var d in _currentSettings.RoutingDomains) RoutingDomainsList.Add(d);

        RoutingAppsList.Clear();
        foreach (var a in _currentSettings.RoutingApps) RoutingAppsList.Add(a);

        if (OverlayRouting != null) OverlayRouting.Visibility = Visibility.Visible;
        if (CmbRouteMode != null) CmbRouteMode.SelectedIndex = _currentSettings.RouteMode == "Only" ? 1 : 0;

        OverlayRoutingBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayRoutingCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayRoutingTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseRoutingOverlay(bool saveChanges)
    {
        if (saveChanges)
        {
            bool modeChanged = (_currentSettings.RouteMode == "Only") != (CmbRouteMode?.SelectedIndex == 1);
            var domainsChanged = !new HashSet<string>(_currentSettings.RoutingDomains).SetEquals(RoutingDomainsList);
            var appsChanged = !new HashSet<string>(_currentSettings.RoutingApps).SetEquals(RoutingAppsList);

            if (modeChanged || domainsChanged || appsChanged)
            {
                _currentSettings.RouteMode = CmbRouteMode?.SelectedIndex == 1 ? "Only" : "Bypass";
                _currentSettings.RoutingDomains = RoutingDomainsList.ToList();
                _currentSettings.RoutingApps = RoutingAppsList.ToList();
                SaveSettings();

                if (_isRunning)
                {
                    ShowNotification(GetString("Lang_MsgRoutesChanged"));
                    _ = RestartVpnAsync();
                }
            }
        }

        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlayRouting != null) OverlayRouting.Visibility = Visibility.Collapsed; };

        OverlayRoutingBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayRoutingCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayRoutingTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlayRoutingBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseRoutingOverlay(false);
    private void BtnCloseRouting_Click(object sender, RoutedEventArgs e) => CloseRoutingOverlay(true);

    private void TxtNewDomain_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnAddDomain_Click(null, null); e.Handled = true; }
    }

    private void TxtNewApp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnAddApp_Click(null, null); e.Handled = true; }
    }

    private void BtnAddDomain_Click(object? sender, RoutedEventArgs? e)
    {
        if (TxtNewDomain == null) return;
        string domain = TxtNewDomain.Text.Trim().ToLower();
        if (!string.IsNullOrEmpty(domain) && !RoutingDomainsList.Contains(domain))
        {
            RoutingDomainsList.Add(domain);
            TxtNewDomain.Text = "";
        }
    }

    private void BtnAddApp_Click(object? sender, RoutedEventArgs? e)
    {
        if (TxtNewApp == null) return;
        string app = TxtNewApp.Text.Trim().ToLower();
        if (!string.IsNullOrEmpty(app) && !RoutingAppsList.Contains(app))
        {
            if (!app.EndsWith(".exe")) app += ".exe";
            RoutingAppsList.Add(app);
            TxtNewApp.Text = "";
        }
    }

    private void BtnRemoveDomain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string domain }) RoutingDomainsList.Remove(domain);
    }

    private void BtnRemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string app }) RoutingAppsList.Remove(app);
    }

    private void PerformClipboardImport()
    {
        if (!Clipboard.ContainsText()) return;

        string text = Clipboard.GetText().Trim();
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool addedAny = false;

        foreach (var line in lines)
        {
            var p = new VpnProfile();
            if (ParseLinkIntoProfile(p, line) && !string.IsNullOrEmpty(p.Address))
            {
                _profiles.Add(p);
                _ = p.CheckPingAsync();
                addedAny = true;
            }
        }

        if (addedAny)
        {
            SaveSettings();
            UpdateEmptyState();
            if (LvProfiles != null) LvProfiles.SelectedIndex = _profiles.Count - 1;
            ShowNotification(GetString("Lang_MsgImportOk"));
            CloseImportOverlay();
        }
        else
        {
            ShowNotification(GetString("Lang_ErrImport"), true);
        }
    }

    private void CmbProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProtocol?.SelectedItem is ComboBoxItem item && PanelManualFields != null && item.Content?.ToString() is { } contentStr)
            UpdateManualFields(contentStr, _editingProfile != null);
    }

    private void UpdateManualFields(string protocol, bool keepValues = false)
    {
        if (PanelManualFields == null) return;
        PanelManualFields.Children.Clear();

        if (!keepValues) _manualInputValues.Clear();

        AddManualField("Name", GetString("Lang_FldName"));
        AddManualField("Address", GetString("Lang_FldAddress"));
        AddManualField("Port", GetString("Lang_FldPort"));

        if (protocol is "vless" or "trojan" or "vmess" or "tuic" or "hysteria" or "hysteria2")
        {
            AddManualField(protocol is "trojan" || protocol.StartsWith("hysteria") ? "Password" : "Uuid",
                           protocol is "trojan" || protocol.StartsWith("hysteria") ? GetString("Lang_FldPassword") : GetString("Lang_FldUuid"));

            AddManualField("Sni", GetString("Lang_FldSni"));
            if (protocol is not "vmess" and not "tuic") AddManualField("Fp", GetString("Lang_FldFp"));
            AddManualField("Insecure", GetString("Lang_FldInsecure"));
        }

        if (protocol == "vless") { AddManualField("Flow", GetString("Lang_FldFlow")); AddManualField("Pbk", GetString("Lang_FldPbk")); AddManualField("Sid", GetString("Lang_FldSid")); }
        if (protocol == "vmess") { AddManualField("Transport", GetString("Lang_FldTransport")); AddManualField("Path", GetString("Lang_FldPath")); AddManualField("Host", GetString("Lang_FldHost")); AddManualField("Security", GetString("Lang_FldSecurity")); AddManualField("AlterId", GetString("Lang_FldAlterId")); }
        if (protocol == "shadowsocks") { AddManualField("Method", GetString("Lang_FldMethod")); AddManualField("Password", GetString("Lang_FldPassword")); }
        if (protocol == "wireguard") { AddManualField("PublicKey", GetString("Lang_FldPubKey")); AddManualField("PrivateKey", GetString("Lang_FldPrivKey")); AddManualField("LocalAddress", GetString("Lang_FldLocalAddr")); AddManualField("Mtu", GetString("Lang_FldMtu")); }
        if (protocol is "hysteria" or "hysteria2" or "tuic") { AddManualField("Alpn", GetString("Lang_FldAlpn")); AddManualField("Up", GetString("Lang_FldUpMbps")); AddManualField("Down", GetString("Lang_FldDownMbps")); }
        if (protocol is "hysteria" or "hysteria2") { AddManualField("Obfs", GetString("Lang_FldObfs")); AddManualField("ObfsPassword", GetString("Lang_FldObfsPass")); }
        if (protocol is "socks" or "http") { AddManualField("Uuid", GetString("Lang_FldUuid")); AddManualField("Password", GetString("Lang_FldPassword")); }
    }

    private void AddManualField(string key, string labelText)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");

        FrameworkElement inputElement;
        var dropdownOptions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Fp", new[] { "chrome", "firefox", "safari", "edge", "ios", "android", "360", "random", "randomized" } },
            { "Security", new[] { "auto", "none", "tls" } },
            { "Transport", new[] { "tcp", "ws", "grpc", "http" } },
            { "Flow", new[] { "", "xtls-rprx-vision" } }
        };

        if (key == "Insecure")
        {
            var checkBox = new CheckBox { Name = $"ChkField_{key}", Content = labelText };
            checkBox.SetResourceReference(Control.ForegroundProperty, "TextColor");

            if (_manualInputValues.TryGetValue(key, out var val)) checkBox.IsChecked = val == "true";
            else { checkBox.IsChecked = false; _manualInputValues[key] = "false"; }

            checkBox.Checked += (_, _) => _manualInputValues[key] = "true";
            checkBox.Unchecked += (_, _) => _manualInputValues[key] = "false";

            Grid.SetColumnSpan(checkBox, 2);
            grid.Children.Add(checkBox);
            PanelManualFields?.Children.Add(grid);
            return;
        }

        if (dropdownOptions.TryGetValue(key, out var options))
        {
            var comboBox = new ComboBox { Name = $"CmbField_{key}" };
            comboBox.SetResourceReference(Control.ForegroundProperty, "TextColor");
            comboBox.SetResourceReference(Control.BackgroundProperty, "BgColor");
            comboBox.SetResourceReference(Control.BorderBrushProperty, "OutlineColor");

            foreach (var opt in options) comboBox.Items.Add(opt);

            if (_manualInputValues.TryGetValue(key, out var existingValue))
            {
                if (options.Contains(existingValue, StringComparer.OrdinalIgnoreCase))
                    comboBox.SelectedItem = options.FirstOrDefault(o => o.Equals(existingValue, StringComparison.OrdinalIgnoreCase));
                else if (!string.IsNullOrWhiteSpace(existingValue))
                {
                    comboBox.Items.Add(existingValue);
                    comboBox.SelectedItem = existingValue;
                }
                else if (options.Length > 0)
                {
                    comboBox.SelectedIndex = 0;
                    _manualInputValues[key] = options[0];
                }
            }
            else if (options.Length > 0)
            {
                comboBox.SelectedIndex = 0;
                _manualInputValues[key] = options[0];
            }

            comboBox.SelectionChanged += (_, _) => { if (comboBox.SelectedItem is string strValue) _manualInputValues[key] = strValue; };
            inputElement = comboBox;
        }
        else
        {
            var textBox = new TextBox { Name = $"TxtField_{key}" };
            textBox.SetResourceReference(Control.ForegroundProperty, "TextColor");
            textBox.SetResourceReference(Control.BackgroundProperty, "BgColor");
            textBox.SetResourceReference(Control.BorderBrushProperty, "OutlineColor");

            if (_manualInputValues.TryGetValue(key, out var existingValue)) textBox.Text = existingValue;
            textBox.TextChanged += (_, _) => _manualInputValues[key] = textBox.Text;
            inputElement = textBox;
        }

        Grid.SetColumn(label, 0);
        Grid.SetColumn(inputElement, 1);
        grid.Children.Add(label);
        grid.Children.Add(inputElement);
        PanelManualFields?.Children.Add(grid);
    }

    private void BtnSaveImport_Click(object sender, RoutedEventArgs e)
    {
        var p = new VpnProfile
        {
            Protocol = (CmbProtocol?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "vless",
            Name = _manualInputValues.GetValueOrDefault("Name", "Manual Profile"),
            Address = _manualInputValues.GetValueOrDefault("Address", "").Trim()
        };

        string portStr = _manualInputValues.GetValueOrDefault("Port", "443").Trim();

        if (string.IsNullOrWhiteSpace(p.Address) || p.Address.Contains(' '))
        {
            ShowNotification(GetString("Lang_ErrInvalidAddress"), true);
            return;
        }

        if (!int.TryParse(portStr, out int port) || port is < 1 or > 65535)
        {
            ShowNotification(GetString("Lang_ErrInvalidPort"), true);
            return;
        }
        p.Port = port;

        p.Uuid = _manualInputValues.GetValueOrDefault("Uuid", "");
        p.Password = _manualInputValues.GetValueOrDefault("Password", "");
        p.Sni = _manualInputValues.GetValueOrDefault("Sni", "");
        p.Fp = _manualInputValues.GetValueOrDefault("Fp", "");
        p.Flow = _manualInputValues.GetValueOrDefault("Flow", "");
        p.Pbk = _manualInputValues.GetValueOrDefault("Pbk", "");
        p.Sid = _manualInputValues.GetValueOrDefault("Sid", "");
        p.Transport = _manualInputValues.GetValueOrDefault("Transport", "tcp");
        p.Path = _manualInputValues.GetValueOrDefault("Path", "");
        p.Host = _manualInputValues.GetValueOrDefault("Host", "");
        p.Security = _manualInputValues.GetValueOrDefault("Security", "");
        p.Insecure = _manualInputValues.GetValueOrDefault("Insecure", "false") == "true";

        if (int.TryParse(_manualInputValues.GetValueOrDefault("AlterId", "0"), out int aid)) p.AlterId = aid;

        p.Method = _manualInputValues.GetValueOrDefault("Method", "");
        p.PublicKey = _manualInputValues.GetValueOrDefault("PublicKey", "");
        p.PrivateKey = _manualInputValues.GetValueOrDefault("PrivateKey", "");
        p.LocalAddress = _manualInputValues.GetValueOrDefault("LocalAddress", "10.0.0.2/32");

        if (int.TryParse(_manualInputValues.GetValueOrDefault("Mtu", "1420"), out int mtu)) p.Mtu = mtu;

        p.Alpn = _manualInputValues.GetValueOrDefault("Alpn", "");
        p.Up = _manualInputValues.GetValueOrDefault("Up", "");
        p.Down = _manualInputValues.GetValueOrDefault("Down", "");
        p.Obfs = _manualInputValues.GetValueOrDefault("Obfs", "");
        p.ObfsPassword = _manualInputValues.GetValueOrDefault("ObfsPassword", "");

        if (_editingProfile == null)
        {
            _profiles.Add(p);
            if (LvProfiles != null) LvProfiles.SelectedIndex = _profiles.Count - 1;
        }
        else
        {
            int index = _profiles.IndexOf(_editingProfile);
            if (index >= 0)
            {
                _profiles[index] = p;
                if (LvProfiles != null) LvProfiles.SelectedIndex = index;
            }
        }

        UpdateEmptyState();
        PingManager.Clear();
        _ = p.CheckPingAsync();
        SaveSettings();
        CloseImportOverlay();
    }

    private static bool ParseLinkIntoProfile(VpnProfile p, string link)
    {
        link = link.Trim();
        p.Link = link;
        var allowed = new HashSet<string> { "vless", "vmess", "trojan", "shadowsocks", "ss", "wireguard", "wg", "hysteria", "hysteria2", "hy2", "tuic", "socks", "socks5", "http" };

        try
        {
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                p.Protocol = "vmess";
                string b64 = link[8..];
                int mod4 = b64.Length % 4;
                if (mod4 > 0) b64 += new string('=', 4 - mod4);

                using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
                var root = doc.RootElement;

                p.Name = root.TryGetProperty("ps", out var ps) ? ps.GetString() ?? "VMESS" : "VMESS";
                p.Address = root.TryGetProperty("add", out var add) ? add.GetString() ?? "" : "";
                p.Port = root.TryGetProperty("port", out var pt) ? (pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : int.Parse(pt.ToString()!)) : 443;
                p.Uuid = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                p.Transport = root.TryGetProperty("net", out var net) ? net.GetString() ?? "tcp" : "tcp";
                p.Sni = root.TryGetProperty("sni", out var sni) ? sni.GetString() ?? "" : "";
                p.Path = root.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "";
                p.Host = root.TryGetProperty("host", out var host) ? host.GetString() ?? "" : "";
                p.Security = root.TryGetProperty("tls", out var tls) && tls.GetString() == "tls" ? "tls" : "";
                return true;
            }

            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;

            p.Protocol = uri.Scheme.ToLower();
            if (!allowed.Contains(p.Protocol)) return false;

            p.Protocol = p.Protocol switch
            {
                "hy2" => "hysteria2",
                "socks5" => "socks",
                "ss" => "shadowsocks",
                "wg" => "wireguard",
                _ => p.Protocol
            };

            p.Address = uri.Host;
            p.Port = uri.Port > 0 ? uri.Port : 443;
            p.Name = Uri.UnescapeDataString(uri.GetComponents(UriComponents.Fragment, UriFormat.Unescaped));
            if (string.IsNullOrEmpty(p.Name)) p.Name = p.Address;

            string userInfo = Uri.UnescapeDataString(uri.UserInfo);
            if (!string.IsNullOrEmpty(userInfo))
            {
                if (p.Protocol is "vless" or "tuic") p.Uuid = userInfo.Split(':')[0];
                if (p.Protocol is "trojan" or "hysteria2" or "hysteria") p.Password = userInfo;
                if (p.Protocol == "shadowsocks")
                {
                    string decoded = userInfo;
                    if (!userInfo.Contains(':'))
                    {
                        try
                        {
                            string b64 = userInfo.Replace("-", "+").Replace("_", "/");
                            int mod4 = b64.Length % 4;
                            if (mod4 > 0) b64 += new string('=', 4 - mod4);
                            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        }
                        catch { }
                    }
                    var parts = decoded.Split(':', 2);
                    if (parts.Length == 2) { p.Method = parts[0]; p.Password = parts[1]; }
                }
                if (p.Protocol == "tuic" && userInfo.Contains(':')) p.Password = userInfo.Split(':')[1];
                if (p.Protocol is "http" or "socks")
                {
                    var parts = userInfo.Split(':', 2);
                    p.Uuid = parts[0];
                    if (parts.Length > 1) p.Password = parts[1];
                }
            }

            var q = ParseQueryString(uri.Query);
            p.Sni = q.GetValueOrDefault("sni", "");
            p.Pbk = q.GetValueOrDefault("pbk", "");
            p.Sid = q.GetValueOrDefault("sid", "");
            p.Fp = q.GetValueOrDefault("fp", "");
            p.Flow = q.GetValueOrDefault("flow", "");
            p.Alpn = q.GetValueOrDefault("alpn", "");
            p.Method = q.GetValueOrDefault("method", p.Method);

            if (q.TryGetValue("obfs", out var obfs)) p.Obfs = obfs;
            if (q.TryGetValue("obfs-password", out var opw)) p.ObfsPassword = opw;
            if (q.TryGetValue("insecure", out var ins) || q.TryGetValue("allowInsecure", out ins))
                p.Insecure = ins == "1" || ins.Equals("true", StringComparison.OrdinalIgnoreCase);

            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return dict;

        foreach (var part in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(new[] { '=' }, 2);
            if (kv.Length == 2) dict[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return dict;
    }

    private async void BtnRefreshPing_Click(object sender, RoutedEventArgs e)
    {
        if (BtnRefreshPing != null) BtnRefreshPing.IsEnabled = false;
        PingManager.Clear();

        if (!_currentSettings.DisableAnimations && IconRefreshPing?.RenderTransform is RotateTransform transform)
            transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever });

        await Task.WhenAll(_profiles.Select(p => p.CheckPingAsync()));

        if (!_currentSettings.DisableAnimations && IconRefreshPing?.RenderTransform is RotateTransform resetTransform)
            resetTransform.BeginAnimation(RotateTransform.AngleProperty, null);

        if (BtnRefreshPing != null) BtnRefreshPing.IsEnabled = true;
    }

    private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) { item.IsSelected = true; item.Focus(); }
    }

    private async void ListBoxItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: true } && !_isRunning) await ToggleVpnAsync();
    }

    private async void MenuItem_Connect_Click(object sender, RoutedEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile && !_isRunning) await ToggleVpnAsync();
    }

    private void MenuItem_CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            string link = p.Link;
            if (string.IsNullOrEmpty(link))
            {
                link = $"{p.Protocol}://{(string.IsNullOrEmpty(p.Uuid) ? p.Password : p.Uuid)}@{p.Address}:{p.Port}#{p.Name}";
                if (p.Protocol == "shadowsocks") link = $"ss://{p.Method}:{p.Password}@{p.Address}:{p.Port}#{p.Name}";
            }
            ShowQROverlay(link);
        }
    }

    private void MenuItem_MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            int idx = _profiles.IndexOf(p);
            if (idx > 0) { _profiles.Move(idx, idx - 1); SaveSettings(); }
        }
    }

    private void MenuItem_MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            int idx = _profiles.IndexOf(p);
            if (idx >= 0 && idx < _profiles.Count - 1) { _profiles.Move(idx, idx + 1); SaveSettings(); }
        }
    }

    private void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            _editingProfile = p;
            _manualInputValues["Name"] = p.Name;
            _manualInputValues["Address"] = p.Address;
            _manualInputValues["Port"] = p.Port.ToString();
            _manualInputValues["Uuid"] = p.Uuid;
            _manualInputValues["Password"] = p.Password;
            _manualInputValues["Sni"] = p.Sni;
            _manualInputValues["Fp"] = p.Fp;
            _manualInputValues["Flow"] = p.Flow;
            _manualInputValues["Pbk"] = p.Pbk;
            _manualInputValues["Sid"] = p.Sid;
            _manualInputValues["Transport"] = p.Transport;
            _manualInputValues["Path"] = p.Path;
            _manualInputValues["Host"] = p.Host;
            _manualInputValues["Security"] = p.Security;
            _manualInputValues["AlterId"] = p.AlterId.ToString();
            _manualInputValues["Method"] = p.Method;
            _manualInputValues["PublicKey"] = p.PublicKey;
            _manualInputValues["PrivateKey"] = p.PrivateKey;
            _manualInputValues["LocalAddress"] = p.LocalAddress;
            _manualInputValues["Mtu"] = p.Mtu.ToString();
            _manualInputValues["Alpn"] = p.Alpn;
            _manualInputValues["Up"] = p.Up;
            _manualInputValues["Down"] = p.Down;
            _manualInputValues["Obfs"] = p.Obfs;
            _manualInputValues["ObfsPassword"] = p.ObfsPassword;
            _manualInputValues["Insecure"] = p.Insecure ? "true" : "false";

            if (TxtImportTitle != null) TxtImportTitle.Text = GetString("Lang_CtxEdit");

            if (BtnPasteImport != null) BtnPasteImport.Visibility = Visibility.Collapsed;
            if (ImportSeparator != null) ImportSeparator.Visibility = Visibility.Collapsed;
            if (TxtOrManual != null) TxtOrManual.Visibility = Visibility.Collapsed;
            if (OverlayImport != null) OverlayImport.Visibility = Visibility.Visible;

            if (CmbProtocol != null)
            {
                var item = CmbProtocol.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == p.Protocol);
                CmbProtocol.SelectedItem = item ?? CmbProtocol.Items[0];
            }

            UpdateManualFields(p.Protocol, true);

            OverlayImportBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
            OverlayImportCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
            OverlayImportTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            _profiles.Remove(p);
            SaveSettings();
            UpdateEmptyState();
        }
    }

    private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (TxtConsole == null) return;
        var text = new TextRange(TxtConsole.Document.ContentStart, TxtConsole.Document.ContentEnd).Text;

        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            ShowNotification(GetString("Lang_MsgCopied"));
        }
    }

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
    {
        ConsoleParagraph?.Inlines.Clear();
        _logBuffer.Clear();
    }

    private void Log(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_logLock)
        {
            _logBuffer.Enqueue(message);
            if (_logBuffer.Count > 1000) _logBuffer.Dequeue();
        }
    }

    private void FlushLogs(object? sender, EventArgs e)
    {
        List<string> messagesToPrint;
        lock (_logLock)
        {
            if (_logBuffer.Count == 0) return;
            messagesToPrint = _logBuffer.ToList();
            _logBuffer.Clear();
        }

        if (ConsoleParagraph == null || TxtConsole == null) return;

        bool needsScroll = false;
        TxtConsole.BeginChange();
        try
        {
            foreach (var message in messagesToPrint)
            {
                var parts = Regex.Split(message, @"\x1B\[([0-9;]*)m");
                bool isDefaultColor = true;
                Brush? currentCustomBrush = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    if (i % 2 == 0)
                    {
                        if (!string.IsNullOrEmpty(parts[i]))
                        {
                            var run = new Run(parts[i]);
                            if (message.Contains("[ERRO]", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
                            {
                                run.SetResourceReference(TextElement.ForegroundProperty, "ErrorColor");
                            }
                            else if (isDefaultColor)
                            {
                                run.SetResourceReference(TextElement.ForegroundProperty, "TextColor");
                            }
                            else
                            {
                                run.Foreground = currentCustomBrush;
                            }
                            ConsoleParagraph.Inlines.Add(run);
                        }
                    }
                    else
                    {
                        string code = parts[i];
                        if (code is "0" or "") isDefaultColor = true;
                        else
                        {
                            isDefaultColor = false;
                            currentCustomBrush = code switch
                            {
                                _ when code.Contains("31") => new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                                _ when code.Contains("32") => new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                                _ when code.Contains("33") => new SolidColorBrush(Color.FromRgb(250, 204, 21)),
                                _ when code.Contains("36") || code.Contains("96") => new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                                _ when code.Contains("34") => new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                                _ when code.Contains("35") => new SolidColorBrush(Color.FromRgb(232, 121, 249)),
                                _ when code.Contains("90") => new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                                _ => null
                            };
                            if (currentCustomBrush == null) isDefaultColor = true;
                        }
                    }
                }
                ConsoleParagraph.Inlines.Add(new Run("\n"));
                needsScroll = true;
            }

            if (ConsoleParagraph.Inlines.Count > 1500)
            {
                while (ConsoleParagraph.Inlines.Count > 500)
                    ConsoleParagraph.Inlines.Remove(ConsoleParagraph.Inlines.FirstInline);
            }
        }
        finally { TxtConsole.EndChange(); }

        if (needsScroll && ViewLogs?.Visibility == Visibility.Visible && ChkAutoScroll?.IsChecked == true)
            TxtConsole.ScrollToEnd();
    }

    private static async Task<string> GetCurrentIpAsync(HttpClientHandler? handler)
    {
        try
        {
            using var client = handler != null ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) } : new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string response = await client.GetStringAsync("http://ip-api.com/json/?lang=en");
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private void UpdateTraffic()
    {
        if (!_isRunning) return;
        try
        {
            if (NetworkInterface.GetIsNetworkAvailable())
            {
                long bytesR = 0, bytesS = 0;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.OperationalStatus == OperationalStatus.Up))
                {
                    var stats = ni.GetIPv4Statistics();
                    bytesR += stats.BytesReceived;
                    bytesS += stats.BytesSent;
                }

                if (_lastBytesReceived > 0 && _lastBytesSent > 0)
                {
                    long dR = Math.Max(0, bytesR - _lastBytesReceived);
                    long dS = Math.Max(0, bytesS - _lastBytesSent);

                    if (TxtSpeedDown != null) TxtSpeedDown.Text = $"↓ {FormatSpeed(dR)}";
                    if (TxtSpeedUp != null) TxtSpeedUp.Text = $"↑ {FormatSpeed(dS)}";
                }
                _lastBytesReceived = bytesR;
                _lastBytesSent = bytesS;
            }
        }
        catch { }
    }

    private static string FormatSpeed(long bytes) => bytes switch
    {
        > 1048576 => $"{bytes / 1048576.0:F1} MB/s",
        > 1024 => $"{bytes / 1024.0:F1} KB/s",
        _ => $"{bytes} B/s"
    };

    private async void BtnToggle_Click(object sender, RoutedEventArgs e) => await ToggleVpnAsync();

    private async Task ToggleVpnAsync()
    {
        if (BtnToggle != null) BtnToggle.IsEnabled = false;
        if (_trayToggleItem != null) _trayToggleItem.Enabled = false;

        if (_isRunning) await StopVPNAsync();
        else await StartVPNAsync();

        if (BtnToggle != null) BtnToggle.IsEnabled = true;
        if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
    }

    private async Task StartVPNAsync()
    {
        if (LvProfiles?.SelectedItem is not VpnProfile selectedProfile)
        {
            ShowNotification(GetString("Lang_ErrSelectProfile"), true);
            return;
        }

        if (_currentSettings.Mode != "Proxy" && !File.Exists(Path.Combine(_baseDirectory, "wintun.dll")))
        {
            ShowNotification(GetString("Lang_ErrWintun"), true);
            return;
        }

        ConsoleParagraph?.Inlines.Clear();
        Log($"[NeonV] Connecting to: {selectedProfile.Name} ({_currentSettings.Mode} Mode)...");
        _originalIp = await GetCurrentIpAsync(null);

        try
        {
            if (!_currentSettings.UseCustomConfig || !File.Exists(Path.Combine(_baseDirectory, "config.json")))
            {
                await Task.Run(() => File.WriteAllText(Path.Combine(_baseDirectory, "config.json"), GenerateJsonConfig(selectedProfile, _currentSettings.RemoteDns)));
            }

            _singboxProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(_baseDirectory, "sing-box.exe"),
                    Arguments = "run -c config.json",
                    WorkingDirectory = _baseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            _singboxProcess.OutputDataReceived += (_, ev) => Log(ev.Data);
            _singboxProcess.ErrorDataReceived += (_, ev) => Log(ev.Data);

            _singboxProcess.Start();
            try { _singboxProcess.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            _singboxProcess.BeginOutputReadLine();
            _singboxProcess.BeginErrorReadLine();

            await Task.Delay(1500);
            if (_singboxProcess.HasExited) throw new Exception(GetString("Lang_ErrCrash"));

            _isRunning = true;
            IsVpnRunning = true;
            ActiveProfile = selectedProfile;
            _lastBytesReceived = 0;
            _lastBytesSent = 0;

            if (_currentSettings.Mode == "Proxy")
                SystemProxyManager.EnableProxy($"127.0.0.1:{(_currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080)}");

            UpdateButtonText();
            UpdateTrayIconImage(true);
            Log("[NeonV] Running successfully!");

            _connectionStartTime = DateTime.Now;
            if (TxtConnectionTime != null) TxtConnectionTime.Text = "...";

            _uptimeTimer.Start();
            AnimateLocationBadge(true);
            _ = FetchLocationAsync();
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
            ShowNotification(ex.Message, true);
            await StopVPNAsync();
        }
    }

    private async Task StopVPNAsync()
    {
        AnimateLocationBadge(false);
        Log("[NeonV] Stopping...");
        SystemProxyManager.DisableProxy();

        await Task.Run(() =>
        {
            try
            {
                if (_singboxProcess is { HasExited: false })
                {
                    _singboxProcess.Kill();
                    _singboxProcess.WaitForExit(2000);
                }
            }
            catch { }
        });

        _singboxProcess?.Dispose();
        _singboxProcess = null;
        _isRunning = false;
        IsVpnRunning = false;
        ActiveProfile = null;
        _uptimeTimer.Stop();

        UpdateButtonText();
        UpdateTrayIconImage(false);

        PingManager.Clear();
        _ = Task.WhenAll(_profiles.Select(p => p.CheckPingAsync()));
    }

    private void AnimateLocationBadge(bool show)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (LocationBadge != null)
            {
                var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                LocationBadge.BeginAnimation(WidthProperty, new DoubleAnimation { To = show ? 280 : 0, Duration = GetDuration(0.35), EasingFunction = cubicEase });
                LocationBadge.BeginAnimation(OpacityProperty, new DoubleAnimation { To = show ? 1 : 0, Duration = GetDuration(0.35), EasingFunction = cubicEase });
                LocationBadge.BeginAnimation(MarginProperty, new ThicknessAnimation { To = show ? new Thickness(0, 0, 16, 0) : new Thickness(0), Duration = GetDuration(0.35), EasingFunction = cubicEase });
            }
        });
    }

    private async Task FetchLocationAsync(bool immediate = false)
    {
        await Task.Run(async () =>
        {
            if (!immediate) await Task.Delay(2000);

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Dispatcher.Invoke(() => { if (TxtLocation != null) TxtLocation.Text = "..."; });

                    HttpClientHandler handler = new();
                    if (_currentSettings.Mode == "Proxy")
                    {
                        handler.Proxy = new WebProxy("127.0.0.1", _currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080);
                        handler.UseProxy = true;
                    }

                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

                    string apiLang = _currentSettings.Language switch
                    {
                        "ru" or "de" or "ja" => _currentSettings.Language,
                        string l when l.StartsWith("zh") => "zh-CN",
                        _ => "en"
                    };

                    string response = await client.GetStringAsync($"http://ip-api.com/json/?lang={apiLang}&_t={DateTime.UtcNow.Ticks}");
                    using var doc = JsonDocument.Parse(response);
                    var root = doc.RootElement;
                    string newIp = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";

                    if (!immediate && newIp == _originalIp && !string.IsNullOrEmpty(_originalIp) && i < 4)
                    {
                        await Task.Delay(2500);
                        continue;
                    }

                    string country = root.TryGetProperty("country", out var c) ? c.GetString() ?? GetString("Lang_LocUnknown") : GetString("Lang_LocUnknown");
                    string countryCode = root.TryGetProperty("countryCode", out var cc) ? cc.GetString() ?? "" : "";

                    Dispatcher.Invoke(() =>
                    {
                        if (TxtLocation != null) TxtLocation.Text = country;
                        if (countryCode.Length == 2)
                        {
                            try
                            {
                                if (ImgFlag != null)
                                {
                                    ImgFlag.Source = new BitmapImage(new Uri($"https://flagcdn.com/w40/{countryCode.ToLower()}.png"));
                                    ImgFlag.Visibility = Visibility.Visible;
                                }
                                if (IconLocation != null) IconLocation.Visibility = Visibility.Collapsed;
                            }
                            catch { }
                        }
                    });
                    return;
                }
                catch
                {
                    if (i == 4) Dispatcher.Invoke(() => { if (TxtLocation != null) TxtLocation.Text = GetString("Lang_LocUnknown"); });
                    else await Task.Delay(2000);
                }
            }
        });
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _fullScreenOverlay?.Close();

        if (!_isRealClose)
        {
            e.Cancel = true;
            Hide();
            if (!_currentSettings.HasShownTrayNotification)
            {
                _notifyIcon?.ShowBalloonTip(3000, "NeonV", GetString("Lang_TrayRunning"), Forms.ToolTipIcon.Info);
                _currentSettings.HasShownTrayNotification = true;
                SaveSettings();
            }
            return;
        }

        SystemProxyManager.DisableProxy();
        try
        {
            if (_singboxProcess is { HasExited: false })
            {
                _singboxProcess.Kill();
                _singboxProcess.WaitForExit(2000);
            }
        }
        catch { }

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }

    private string GenerateJsonConfig(VpnProfile p, string customDns)
    {
        if (string.IsNullOrWhiteSpace(customDns)) customDns = "8.8.8.8";

        var outbound = new Dictionary<string, object>
        {
            ["type"] = p.Protocol,
            ["tag"] = "proxy",
            ["server"] = p.Address,
            ["server_port"] = p.Port
        };

        if (p.Protocol == "vless")
        {
            outbound["uuid"] = p.Uuid;
            if (!string.IsNullOrEmpty(p.Flow)) outbound["flow"] = p.Flow;
            outbound["tls"] = BuildTlsConfig(p);
        }
        else if (p.Protocol == "vmess")
        {
            outbound["uuid"] = p.Uuid;
            outbound["alter_id"] = p.AlterId;
            outbound["security"] = string.IsNullOrEmpty(p.Security) ? "auto" : p.Security;

            if (p.Transport != "tcp" && !string.IsNullOrEmpty(p.Transport))
            {
                var transport = new Dictionary<string, object> { ["type"] = p.Transport };
                if (p.Transport == "ws")
                {
                    transport["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path;
                    if (!string.IsNullOrEmpty(p.Host)) transport["headers"] = new Dictionary<string, string> { ["Host"] = p.Host };
                }
                outbound["transport"] = transport;
            }
            if (p.Security == "tls" || p.Port == 443) outbound["tls"] = BuildTlsConfig(p);
        }
        else if (p.Protocol == "trojan")
        {
            outbound["password"] = p.Password;
            outbound["tls"] = BuildTlsConfig(p);
        }
        else if (p.Protocol == "shadowsocks")
        {
            outbound["method"] = string.IsNullOrEmpty(p.Method) ? "chacha20-ietf-poly1305" : p.Method;
            outbound["password"] = p.Password;
        }
        else if (p.Protocol == "wireguard")
        {
            outbound["local_address"] = new[] { string.IsNullOrEmpty(p.LocalAddress) ? "10.0.0.2/32" : p.LocalAddress };
            outbound["private_key"] = p.PrivateKey;
            outbound["peer_public_key"] = p.PublicKey;
            outbound["mtu"] = p.Mtu > 0 ? p.Mtu : 1420;
        }
        else if (p.Protocol is "hysteria" or "hysteria2")
        {
            if (p.Protocol == "hysteria2") outbound["password"] = p.Password;
            else outbound["auth_str"] = p.Password;

            if (!string.IsNullOrEmpty(p.Up)) outbound["up_mbps"] = int.TryParse(p.Up, out var up) ? up : 100;
            if (!string.IsNullOrEmpty(p.Down)) outbound["down_mbps"] = int.TryParse(p.Down, out var dn) ? dn : 100;

            if (!string.IsNullOrEmpty(p.Obfs))
            {
                if (p.Protocol == "hysteria2") outbound["obfs"] = new { type = "salamander", password = p.ObfsPassword };
                else outbound["obfs"] = p.Obfs;
            }
            outbound["tls"] = BuildTlsConfig(p, true);
        }
        else if (p.Protocol == "tuic")
        {
            outbound["uuid"] = p.Uuid;
            outbound["password"] = p.Password;
            outbound["tls"] = BuildTlsConfig(p, true);
        }
        else if (p.Protocol is "socks" or "http" && !string.IsNullOrEmpty(p.Uuid))
        {
            outbound["username"] = p.Uuid;
            outbound["password"] = p.Password;
        }

        var routeRules = new List<object>
        {
            new { port = 53, action = "hijack-dns" },
            new { protocol = "dns", action = "hijack-dns" },
            new { ip_is_private = true, outbound = "direct" },
            new { action = "sniff" }
        };

        var directIps = new HashSet<string>();
        var directDomains = new HashSet<string>();
        foreach (var profile in _profiles.Where(x => !string.IsNullOrWhiteSpace(x.Address)))
        {
            if (IPAddress.TryParse(profile.Address, out var ip)) directIps.Add(ip.ToString());
            else directDomains.Add(profile.Address);
        }

        if (directIps.Count > 0) routeRules.Add(new { ip_cidr = directIps.ToArray(), outbound = "direct" });
        if (directDomains.Count > 0) routeRules.Add(new { domain = directDomains.ToArray(), outbound = "direct" });

        string ruleOutbound = _currentSettings.RouteMode == "Only" ? "proxy" : "direct";
        string finalOutbound = _currentSettings.RouteMode == "Only" ? "direct" : "proxy";
        string ruleDns = _currentSettings.RouteMode == "Only" ? "remote-dns" : "direct-dns";
        string finalDns = _currentSettings.RouteMode == "Only" ? "direct-dns" : "remote-dns";

        routeRules.Add(new { domain_suffix = new[] { "ip-api.com", "flagcdn.com" }, outbound = "proxy" });
        if (_currentSettings.RoutingDomains.Count != 0) routeRules.Add(new { domain_suffix = _currentSettings.RoutingDomains.ToArray(), outbound = ruleOutbound });
        if (_currentSettings.RoutingApps.Count != 0) routeRules.Add(new { process_name = _currentSettings.RoutingApps.ToArray(), outbound = ruleOutbound });

        var dnsRules = new List<object> { new { domain_suffix = new[] { "ip-api.com", "flagcdn.com" }, server = "remote-dns" } };
        if (_currentSettings.RoutingDomains.Count != 0) dnsRules.Add(new { domain_suffix = _currentSettings.RoutingDomains.ToArray(), server = ruleDns });

        var inbounds = new List<object>();
        if (_currentSettings.Mode == "Proxy")
        {
            inbounds.Add(new
            {
                type = "mixed",
                tag = "mixed-in",
                listen = _currentSettings.AllowLan ? "0.0.0.0" : "127.0.0.1",
                listen_port = _currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080
            });
        }
        else
        {
            inbounds.Add(new
            {
                type = "tun",
                tag = "tun-in",
                address = new[] { "172.19.0.1/30" },
                auto_route = true,
                strict_route = true,
                endpoint_independent_nat = _currentSettings.AllowLan,
                stack = string.IsNullOrEmpty(_currentSettings.TunStack) ? "system" : _currentSettings.TunStack
            });
        }

        var root = new
        {
            log = new { level = string.IsNullOrEmpty(_currentSettings.LogLevel) ? "info" : _currentSettings.LogLevel, timestamp = true },
            dns = new
            {
                servers = new object[]
                {
                    new { tag = "remote-dns", type = "udp", server = customDns, server_port = 53, detour = "proxy" },
                    new { tag = "direct-dns", type = "udp", server = "77.88.8.8", server_port = 53 }
                },
                rules = dnsRules.ToArray(),
                final = finalDns,
                independent_cache = true
            },
            inbounds = inbounds.ToArray(),
            outbounds = new object[] { outbound, new { type = "direct", tag = "direct" } },
            route = new
            {
                rules = routeRules.ToArray(),
                auto_detect_interface = _currentSettings.Mode != "Proxy",
                final = finalOutbound,
                default_domain_resolver = "direct-dns"
            }
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object BuildTlsConfig(VpnProfile p, bool isUdpAlpn = false)
    {
        var tls = new Dictionary<string, object> { ["enabled"] = true };
        if (!string.IsNullOrEmpty(p.Sni)) tls["server_name"] = p.Sni;
        if (isUdpAlpn && !string.IsNullOrEmpty(p.Alpn)) tls["alpn"] = new[] { p.Alpn };
        if (!string.IsNullOrEmpty(p.Fp)) tls["utls"] = new { enabled = true, fingerprint = p.Fp };
        if (!string.IsNullOrEmpty(p.Pbk)) tls["reality"] = new { enabled = true, public_key = p.Pbk, short_id = p.Sid };
        if (p.Insecure) tls["insecure"] = true;
        return tls;
    }

    private Window? _fullScreenOverlay;

    private void BtnShowModels_Click(object sender, RoutedEventArgs e)
    {
        if (_fullScreenOverlay != null)
        {
            _fullScreenOverlay.Close();
            _fullScreenOverlay = null;
            return;
        }

        string baseModels = "mind-kozel, gpt-oss-120b, gpt-oss-20b, gpt-5.2-chat, gpt-5.1-chat, gpt-5.1-2025-11-13, gpt-5.1, gpt-5-search-api-2025-10-14, gpt-5-search, gpt-5-nano-2025-08-07, gpt-5-nano, gpt-5-mini-2025-08-07, gpt-5-mini, gpt-5-chat, gpt-5-2025-08-07, gpt-5, gpt-5.5, gpt-5.2, gpt-5.4, p-gpt-5.4-nano, p-gpt-5.4-mini, pplx-gpt-5.2, pplx-gpt-5.1, pplx-gpt-5-mini, gpt-4.1-nano-2025-04-14, gpt-4.1-nano, gpt-4.1-mini-2025-04-14, gpt-4.1-mini, gpt-4.1-2025-04-14, gpt-4.1, chatgpt-4o, gpt-4o-search-preview-2025-03-11, gpt-4o-search-preview, gpt-4o-mini-search-preview-2025-03-11, gpt-4o-mini-search-preview, gpt-4o-mini-2024-07-18, gpt-4o-mini, gpt-4o-2024-11-20, gpt-4o-2024-08-06, gpt-4o-2024-05-13, gpt-4o, gpt-4-turbo-preview, gpt-4-turbo-2024-04-09, gpt-4-turbo, gpt-4-1106-preview, gpt-4-0613, gpt-4-0125-preview, gpt-4, gpt-3.5-turbo-16k, gpt-3.5-turbo-1106, gpt-3.5-turbo-0125, gpt-3.5-turbo, gpt-image-1.5, gpt-image-1, gpt-image-1-mini, gemini-1.0-pro, gemini-1.5-pro, gemini-1.5-flash, gemini-2.0-flash, gemini-2.0-flash-lite, gemini-2.5-pro, gemini-2.5-flash, gemini-2.5-flash-lite, gemini-3-flash, gemini-3.5-flash, gemini-3.1-pro, gemini-omni, pplx-gemini-3.1-pro, pplx-gemini-3-flash, deepseek-v1, deepseek-v2, deepseek-v2-chat, deepseek-v2-lite, deepseek-v2.5, deepseek-v3, deepseek-v3-chat, deepseek-v3.1, deepseek-v3.2, deepseek-v4, deepseek-r1, deepseek-r1-lite, deepseek-r1-distill-qwen-1.5b, deepseek-r1-distill-qwen-7b, deepseek-r1-distill-qwen-14b, deepseek-r1-distill-qwen-32b, deepseek-r1-distill-llama-8b, deepseek-r1-distill-llama-70b, deepseek-r2, deepseek-coder, deepseek-coder-v2, deepseek-coder-v2-lite, deepseek-coder-33b, deepseek-coder-6.7b, deepseek-coder-1.3b, deepseek-math-7b, deepseek-vl, deepseek-vl-7b, deepseek-vl2, deepseek-llm-7b, deepseek-llm-67b, deepseek-moe-16b, deepseek-prover, deepseek-prover-v1.5, qwen-1.8b, qwen-7b, qwen-14b, qwen-72b, qwen-1.5-0.5b, qwen-1.5-1.8b, qwen-1.5-4b, qwen-1.5-7b, qwen-1.5-14b, qwen-1.5-32b, qwen-1.5-72b, qwen-1.5-110b, qwen-1.5-moe-a2.7b, qwen-2-0.5b, qwen-2-1.5b, qwen-2-7b, qwen-2-57b-a14b, qwen-2-72b, qwen-2.5-0.5b, qwen-2.5-1.5b, qwen-2.5-3b, qwen-2.5-7b, qwen-2.5-14b, qwen-2.5-32b, qwen-2.5-72b, qwen-2.5-coder-1.5b, qwen-2.5-coder-7b, qwen-2.5-coder-14b, qwen-2.5-coder-32b, qwen-2.5-math-1.5b, qwen-2.5-math-7b, qwen-2.5-math-72b, qwen-2.5-vl-3b, qwen-2.5-vl-7b, qwen-2.5-vl-72b, qwen-2.5-omni-7b, qwen-3-0.6b, qwen-3-1.7b, qwen-3-4b, qwen-3-8b, qwen-3-14b, qwen-3-32b, qwen-3-30b-a3b, qwen-3-235b-a22b, qwen-3-coder-30b, qwen-3-coder-480b, qwen-3-max, qwen-3-max-thinking, qwen-3-vl-30b, qwen-3-vl-235b, qwen-3.5, qwen-3.5-max, qwen-4, qwq-32b, qwq-32b-preview, qvq-72b-preview, qwen-plus, qwen-turbo, qwen-max, qwen-long, qwen-vl-plus, qwen-vl-max, qwen-audio, qwen-audio-chat, qwen-omni-turbo, claude-1, claude-1.3, claude-instant-1, claude-instant-1.2, claude-2, claude-2.0, claude-2.1, claude-3-haiku, claude-3-sonnet, claude-3-opus, claude-3.5-haiku, claude-3.5-sonnet, claude-3.5-opus, claude-3.7-haiku, claude-3.7-sonnet, claude-3.7-opus, claude-4-haiku, claude-4-sonnet, claude-4-opus, claude-4.5-sonnet, claude-4.5-opus, claude-5-sonnet-preview, llama-1-7b, llama-1-13b, llama-1-33b, llama-1-65b, llama-2-7b, llama-2-7b-chat, llama-2-13b, llama-2-13b-chat, llama-2-70b, llama-2-70b-chat, llama-3-8b, llama-3-8b-instruct, llama-3-70b, llama-3-70b-instruct, llama-3.1-8b, llama-3.1-8b-instruct, llama-3.1-70b, llama-3.1-70b-instruct, llama-3.1-405b, llama-3.1-405b-instruct, llama-3.2-1b, llama-3.2-1b-instruct, llama-3.2-3b, llama-3.2-3b-instruct, llama-3.2-11b-vision, llama-3.2-11b-vision-instruct, llama-3.2-90b-vision, llama-3.2-90b-vision-instruct, llama-3.3-70b, llama-3.3-70b-instruct, llama-4-8b, llama-4-70b, llama-4-400b, llama-4-multimodal, codellama-7b, codellama-13b, codellama-34b, codellama-70b, codellama-instruct, codellama-python, mistral-7b-v0.1, mistral-7b-v0.2, mistral-7b-v0.3, mistral-7b-instruct-v0.1, mistral-7b-instruct-v0.2, mistral-7b-instruct-v0.3, mixtral-8x7b-v0.1, mixtral-8x7b-instruct-v0.1, mixtral-8x22b, mixtral-8x22b-instruct, mistral-medium, mistral-small-2402, mistral-small-2409, mistral-large-2402, mistral-large-2407, mistral-large-2411, mistral-large-2503, pixtral-12b, pixtral-large-124b, codestral-22b, codestral-mamba, mathstral-7b, ministral-3b, ministral-8b, mistral-nemo-12b, mistral-nemo-instruct-12b, mistral-next, phi-1, phi-1.5, phi-2, phi-3-mini-4k-instruct, phi-3-mini-128k-instruct, phi-3-small-8k-instruct, phi-3-small-128k-instruct, phi-3-medium-4k-instruct, phi-3-medium-128k-instruct, phi-3-vision-128k-instruct, phi-3.5-mini-instruct, phi-3.5-moe-instruct, phi-3.5-vision-instruct, phi-4, phi-4-mini, phi-4-multimodal, wizardlm-7b, wizardlm-13b, wizardlm-30b, wizardlm-70b, wizardlm-2-7b, wizardlm-2-8x22b, wizardlm-2-70b, wizardcoder-15b, wizardcoder-33b, wizardmath-7b, wizardmath-70b, palm-1, palm-2, palm-2-bison, palm-2-unicorn, gemma-2b, gemma-2b-it, gemma-7b, gemma-7b-it, gemma-2-2b, gemma-2-2b-it, gemma-2-9b, gemma-2-9b-it, gemma-2-27b, gemma-2-27b-it, gemma-3-4b, gemma-3-12b, gemma-3-27b, codegemma-2b, codegemma-7b, codegemma-7b-it, recurrentgemma-2b, recurrentgemma-2b-it, paligemma-3b-mix-224, paligemma-3b-mix-448, grok-1, grok-1.5, grok-1.5-vision, grok-2, grok-2-mini, grok-3, grok-3-mini, grok-3-pro, grok-3-vision, grok-4-beta, command, command-light, command-nightly, command-r-v0.1, command-r-plus-v0.1, command-r-08-2024, command-r-plus-08-2024, command-r7b, aya-101, aya-23-8b, aya-23-35b, aya-expanse-8b, aya-expanse-32b, yi-6b, yi-6b-chat, yi-9b, yi-34b, yi-34b-chat, yi-vl-6b, yi-vl-34b, yi-1.5-6b, yi-1.5-6b-chat, yi-1.5-9b, yi-1.5-9b-chat, yi-1.5-34b, yi-1.5-34b-chat, yi-large, yi-large-turbo, yi-vision, yi-lightning, yi-lightning-lite, falcon-7b, falcon-7b-instruct, falcon-40b, falcon-40b-instruct, falcon-180b, falcon-180b-chat, falcon-mamba-7b, falcon-2-11b, falcon-2-11b-vlm, falcon-3-1b, falcon-3-3b, falcon-3-7b, falcon-3-10b, falcon-3-10b-vision, chatglm-6b, chatglm2-6b, chatglm3-6b, glm-4-9b, glm-4-9b-chat, glm-4v-9b, glm-4-plus, glm-4-air, glm-4-airx, glm-4-flash, glm-4-long, glm-zero, ernie-bot, ernie-bot-turbo, ernie-bot-4.0, ernie-3.5-8b, ernie-4.0-8k, hunyuan-pro, hunyuan-standard, hunyuan-lite, hunyuan-vision, baichuan-7b, baichuan-13b, baichuan2-7b, baichuan2-13b, baichuan-3, tongyi-qianwen-max, yandexgpt, yandexgpt-2, yandexgpt-3-lite, yandexgpt-3-pro, yandexgpt-4, yandexgpt-4-pro, gigachat, gigachat-lite, gigachat-pro, gigachat-max, saiga-llama-3-8b, saiga-llama-3-70b, saiga-mistral-7b, saiga-gemma-2-9b, ruGPT-3.5-13B, ruGPT-4, dbrx-base, dbrx-instruct, dolly-v2-3b, dolly-v2-7b, dolly-v2-12b, jamba-v0.1, jamba-instruct, jamba-1.5-mini, jamba-1.5-large, jurassic-1-jumbo, jurassic-2-ultra, jurassic-2-mid, jurassic-2-light, solar-10.7b-v1.0, solar-10.7b-instruct, solar-pro, reka-core, reka-flash, reka-edge, nemotron-3-8b, nemotron-4-15b, nemotron-4-340b-base, nemotron-4-340b-instruct, nemotron-4-340b-reward, llama-3.1-nemotron-70b-instruct, granite-3b-code, granite-8b-code, granite-13b-chat, granite-34b-code, granite-3.0-8b-instruct, granite-3.0-2b-instruct, olmo-1b, olmo-7b, olmo-7b-instruct, olmo-1.7-7b, olmo-2-7b, olmo-2-13b, tulu-2-7b, tulu-2-70b, tulu-3-8b, tulu-3-70b, zephyr-7b-alpha, zephyr-7b-beta, zephyr-7b-gemma, starcoder, starcoderbase, starcoder2-3b, starcoder2-7b, starcoder2-15b, smaug-72b, nous-hermes-13b, nous-hermes-2-mixtral-8x7b, nous-hermes-2-vision, nous-hermes-3-llama-3.1-8b, nous-hermes-3-llama-3.1-70b, nous-hermes-3-llama-3.1-405b, openhermes-2.5-mistral-7b, capybara-7b, airoboros-l2-70b, dolphin-2.0-mistral-7b, dolphin-2.1-mistral-7b, dolphin-2.2-yi-34b, dolphin-2.5-mixtral-8x7b, dolphin-2.6-phi-2, dolphin-2.8-experiment26-7b, dolphin-2.9-llama3-8b, dolphin-2.9.2-qwen2-7b, mpt-7b, mpt-7b-instruct, mpt-7b-chat, mpt-30b, mpt-30b-instruct, mpt-30b-chat, xgen-7b-8k, xgen-7b-8k-inst, internlm-7b, internlm-chat-7b, internlm2-1.8b, internlm2-7b, internlm2-20b, internlm2.5-7b, internlm2.5-20b, aquila-7b, aquila2-34b, aquilachat2-34b, stablelm-3b-4e1t, stablelm-base-alpha-7b, stable-zephyr-3b, stable-code-3b, stable-beluga-7b, stable-beluga-13b, stable-beluga-2, mm1, mm1.5, openelm-270m, openelm-450m, openelm-1.1b, openelm-3b, apple-afm-on-device, apple-afm-server, apple-intelligence-ajax, amazon-titan-text-lite, amazon-titan-text-express, amazon-titan-text-premier, olympus-amazon, sambalingo-arabic, sambalingo-thai, samba-coe-v0.1, gpt-neo-125m, gpt-neo-1.3b, gpt-neo-2.7b, gpt-j-6b, gpt-neox-20b, pythia-70m, pythia-160m, pythia-410m, pythia-1b, pythia-1.4b, pythia-2.8b, pythia-6.9b, pythia-12b, bloom-560m, bloom-1b1, bloom-1b7, bloom-3b, bloom-7b1, bloom-176b, bloomz-176b, opt-125m, opt-350m, opt-1.3b, opt-2.7b, opt-6.7b, opt-13b, opt-30b, opt-66b, opt-175b, cerebras-gpt-111m, cerebras-gpt-13b, redpajama-incite-base-7b, redpajama-incite-chat-7b, llava-1.5-7b, llava-1.5-13b, llava-v1.6-34b, llava-next-72b, llava-next-qwen-72b, moondream1, moondream2, cogvlm, cogvlm2, cogvlm2-video, fuyu-8b, idefics-9b, idefics-80b, idefics2-8b, internvl-chat-v1.5, internvl2-1b, internvl2-2b, internvl2-4b, internvl2-8b, internvl2-26b, internvl2-76b, internvl2-108b, mini-cpm-v-2.0, mini-cpm-v-2.5, mini-cpm-v-2.6, mini-cpm-3-4b, qwen-vl-chat, qwen2-vl-7b, qwen2.5-vl-72b, stable-diffusion-v1.4, stable-diffusion-v1.5, stable-diffusion-2.0, stable-diffusion-2.1, sd-xl-1.0, sd-xl-turbo, sdxl-lightning, stable-cascade, sd3-medium, sd3-large, sd3-ultra, flux.1-schnell, flux.1-dev, flux.1-pro, midjourney-v1, midjourney-v2, midjourney-v3, midjourney-v4, midjourney-v5, midjourney-v5.1, midjourney-v5.2, midjourney-v6, midjourney-v6.1, midjourney-v7, dall-e-1, dall-e-2, dall-e-3, imagen, imagen-2, imagen-3, parti, muse, pixie, sora, runway-gen1, runway-gen2, runway-gen3-alpha, runway-gen3-turbo, pika-1.0, pika-2.0, kling-v1, kling-v1.5, luma-dream-machine, haiper-v1, haiper-v2, cogvideo, cogvideox-2b, cogvideox-5b, hunyuan-video, minmax-video, veed, stable-video-diffusion, svd-xt, whisper-tiny, whisper-base, whisper-small, whisper-medium, whisper-large-v1, whisper-large-v2, whisper-large-v3, whisper-large-v3-turbo, bark, tortoise-tts, vits, seamless-m4t-v2, voicebox, audiocraft, musicgen, stable-audio, suno-v2, suno-v3, suno-v3.5, udio-v1, udio-v1.5, elevenlabs-turbo-v2, elevenlabs-multilingual-v2, text-davinci-001, text-davinci-002, text-davinci-003, text-curie-001, text-babbage-001, text-ada-001, code-davinci-002, code-cushman-001, gpt-2, gpt-2-medium, gpt-2-large, gpt-2-xl, gpt-1, bert-base-uncased, roberta-base, distilbert, t5-small, t5-base, t5-large, t5-3b, t5-11b, flan-t5-small, flan-t5-base, flan-t5-large, flan-t5-xl, flan-t5-xxl, llama-65b, miqu-1-70b, midnight-miqu-70b, goliath-120b, noromaid-v0.1-mixtral-8x7b-instruct, kunoichi-dpo-v2-7b, fimbulvetr-11b, bagel-34b, openchat-3.5-0106, openchat-3.5-1210, starling-lm-7b-alpha, starling-lm-11b-beta, neural-chat-7b-v3-1, toppy-m-7b, mythomax-l2-13b, tiefighter-13b, chronos-hermes-13b, nous-capybara-34b, yi-coder-1.5b, yi-coder-9b, deepseek-coder-7b-base, phind-codellama-34b-v2, euryale-1.3-l2-70b, xwin-lm-70b, alpaca-7b, vicuna-7b, vicuna-13b, vicuna-33b, guanaco-7b, guanaco-33b, guanaco-65b, qwen1.5-0.5b-chat, qwen1.5-1.8b-chat, rwkv-4-pile-14b, rwkv-5-world-1.5b, rwkv-5-world-3b, rwkv-6-world-7b, mamba-130m, mamba-370m, mamba-790m, mamba-1.4b, mamba-2.8b, mamba2-130m, mamba2-2.7b, jina-embeddings-v2-base-en, bge-m3, bge-large-en-v1.5, nomic-embed-text, all-minilm-l6-v2, e5-mistral-7b-instruct, mxbai-embed-large-v1, cohere-embed-english-v3.0, text-embedding-3-small, text-embedding-3-large, text-embedding-ada-002, claude-3-haiku-20240307, claude-3-sonnet-20240229, claude-3-opus-20240229, claude-3-5-sonnet-20240620, claude-3-5-sonnet-20241022, gemini-1.5-pro-exp-0801, gemini-1.5-pro-exp-0827, gemini-1.5-flash-exp-0827, gemini-1.5-flash-8b-exp-0827, command-r-v0.1-4bit, command-r-plus-v0.1-8bit, mixtral-8x7b-instruct-v0.1-gguf, llama-3-70b-instruct-awq, qwen-2.5-72b-instruct-exl2, meta-llama-3-120b, mergekit-slerp, miqu-1-120b, senku-70b, xwin-math-7b, leetcode-copilot, copilot-chat, amazon-q, tabnine, codeium-base, deepcode, replit-code-v1-3b, phi-2-dpo, stable-lm-2-12b, plamo-13b, swallow-13b, elyza-japanese-llama-2-7b, qwen-14b-chat-int4, chatglm3-6b-32k, baichuan2-13b-chat-4bits, blue-orchid-2x7b, llemma-7b, llemma-34b, minotaur-15b, platypus2-70b, speecht5, bark-small, seamless-m4t-large, distil-whisper-large-v3, openvoice, metavoice-1b, litserve, vllm-engine, text-bison-001, text-bison-002, chat-bison-001, chat-bison-002, code-bison-001, code-bison-002, codechat-bison-001, codechat-bison-002, gemini-1.0-pro-vision, gemini-1.5-pro-latest, gemini-1.5-flash-latest, medlm, sec-palm, audio-palm, med-palm-2, video-poem, magika, alphafold-1, alphafold-2, alphafold-3, alphacode, alphacode-2, alphageometry, alphastar, muzero, leela-chess-zero, stockfish-16.1-nnue, llama-3-typhoon-v1.5-8b, qwen-2-math-72b-instruct, qwen-2-audio-7b, grok-1.5-vision-preview, mistral-large-2402-api, x-ai-grok-beta, openai-o1, openai-o1-preview, openai-o1-mini, chatgpt-o1, gpt-4o-realtime-preview, gpt-4o-audio-preview, gpt-4o-2024-11-20-vision, claude-3-5-haiku-20241022, claude-3-5-sonnet-v2, claude-3-5-opus-v2, llama-3.2-11b-vision-instruct-maestro, llama-3.2-90b-vision-instruct-maestro, qwen-2.5-max, qwen-2.5-plus, qwen-2.5-turbo, deepseek-v2-coder, deepseek-v2-chat-0628, deepseek-v3-base, deepseek-r1-zero, deepseek-r1-distill-llama-3-8b, deepseek-r1-distill-qwen-2.5-7b, moonshot-v1-8k, moonshot-v1-32k, moonshot-v1-200k, kimi-chat, abab6.5-chat, abab6.5s-chat, minimax-text-01, minimax-text-02, sensevoice-small, cosam, omchat-v2-13b, bria-v2.3, hunyuan-dit, lumina-next-t2i, pixart-alpha, pixart-sigma, sd3.5-large, sd3.5-large-turbo, sd3.5-medium, deepfloyd-if, kandinsky-2.1, kandinsky-2.2, kandinsky-3.0, wuerstchen, amused-512, aura-flow, mamba-codestral-7b-v0.1, jamba-1.5-large-instruct, jamba-1.5-mini-instruct, aya-expanse-32b-it, aya-expanse-8b-it, command-r7b-12-2024, granite-3.0-8b-instruct-preview, granite-3.0-2b-instruct-preview, ibm-granite-20b-code, falcon-mamba-7b-instruct, falcon-3-10b-instruct, falcon-3-7b-instruct, falcon-3-3b-instruct, falcon-3-1b-instruct, reka-core-20240501, reka-flash-20240226, reka-edge-20240208, xai-grok-2-1212, xai-grok-2-vision-1212, p-gpt-5.5, p-gpt-5.6, p-gpt-6.0-alpha, gpt-6-preview, gpt-6-nano, gpt-6-mini, gpt-6-search, openai-o2, openai-o2-mini, openai-o2-preview, openai-o3, openai-o3-mini, gemini-3.0-pro, gemini-3.0-ultra, gemini-3.0-flash, gemini-4.0-flash, claude-4-opus, claude-4-sonnet, claude-4-haiku, claude-5-opus, llama-4-8b-instruct, llama-4-70b-instruct, llama-4-400b-instruct, llama-5-base, qwen-4-max, qwen-4-plus, qwen-4-turbo, qwen-5-vl, deepseek-v4-chat, deepseek-v4-coder, deepseek-r3, deepseek-r4, mistral-large-2504, mixtral-8x34b, pixtral-huge, phi-4-vision, phi-4-multimodal-instruct, phi-5, gemma-4-2b, gemma-4-9b, gemma-4-27b, grok-4, grok-4-pro, grok-4-vision, grok-5, midjourney-v8, midjourney-v9, sora-v2, sora-v3, runway-gen4, pika-3.0, kling-v2, kling-v3, luma-dream-machine-v2, stable-diffusion-3.5, flux.2-pro, flux.2-dev, flux.2-schnell, dall-e-4, dall-e-5, suno-v4, suno-v5, udio-v2, udio-v3, elevenlabs-v3, elevenlabs-v4, whisper-v4, whisper-v5, yolov10, yolov11, yolov12, segment-anything-2, segment-anything-3, detic, groundingdino, blip-2, blip-3, instructblip, mplug-owl, mplug-owl2, mplug-owl3, qwen-vl-max-0809, qwen-audio-turbo, qwen-math-turbo, open-sora, open-sora-plan, latte, mora, opensora-v1.2, h2o-danube-1.8b, h2o-danube2-1.8b, h2o-danube3-4b, qwen2-math-1.5b, qwen2-math-7b, qwen2-math-72b, numina-math-7b-tir, numina-math-7b-cot, map-neo-7b, map-neo-7b-instruct, ziya-llama-13b, ziya-llama-13b-v1.1, ziya2-13b, sus-chat-34b, rwkv-6-world-3b, rwkv-6-world-1.5b, eagle-7b, minicpm-2b-sft-bf16, minicpm-2b-dpo-bf16, minicpm-moe-8x2b, xverse-7b, xverse-13b, xverse-65b, xverse-65b-chat, deepseek-coder-1.3b-base, deepseek-coder-1.3b-instruct, deepseek-coder-6.7b-base, deepseek-coder-6.7b-instruct, deepseek-coder-33b-base, deepseek-coder-33b-instruct, codefuse-codellama-34b, codefuse-deepseek-33b, qwen-coder-1.5b, qwen-coder-7b, wizardcoder-python-7b-v1.0, wizardcoder-python-13b-v1.0, wizardcoder-python-34b-v1.0, phi-1.5-web, phi-2-electra, btlm-3b-8k, cerenacas-gpt, cerebras-btd, databricks-dolly-v1-6b, mosaic-mpt-7b-storywriter, mosaic-mpt-30b-chat, h2oai-predict, llm360-amber, llm360-crystal, llm360-diamond, nvidia-megatron-turing-nlg-530b, google-switch-transformer, google-glam, google-gopher, google-chinchilla, google-lamda, google-sparrow, google-meena, facebook-blenderbot, facebook-blenderbot-2, facebook-blenderbot-3, facebook-galactica, facebook-opt-iml, facebook-opt-iml-max, anthropic-hh-rlhf, anthropic-claude-v1.3-100k, anthropic-claude-v1-100k, aleph-alpha-luminous-base, aleph-alpha-luminous-extended, aleph-alpha-luminous-supreme, lighton-alfred-40b, nlpcloud-finetuned-llama, bria-rmbg-1.4, isnet-general-use, u2net, rembg, dexined, pidinet, controlnet-canny, controlnet-depth, controlnet-hed, controlnet-mlsd, controlnet-normal, controlnet-openpose, controlnet-scribble, controlnet-seg, ip-adapter, ip-adapter-faceid, ip-adapter-plus, lora-xl, lycoris, lokr, loha, svd-xt-1.1, svd-image-decoder, animatediff, animatediff-v2, animatediff-v3, text2video-zero, zeroscope-v2-576w, zeroscope-v2-xl, modelscope-damo-text-to-video, potat-1, vqgan-clip, clip-vit-base-patch32, clip-vit-large-patch14, siglip-so400m-patch14-384, eva-clip, openclip-vit-h-14, openclip-vit-g-14, dino-v1, dino-v2, deit, swin-transformer, vit-mae, convnext-v2, resnet-50, resnet-101, efficientnet-b0, efficientnet-b7, mobilenet-v3, yolov8, yolov9, rtdetr, mask-rcnn, faster-rcnn, cascade-rcnn, pointrend, mask2former, oneformer, detr, deformable-detr, glip, blip, x-clip, video-llava, macaw-llm, otter-video, panda-70m, internvideo, internvideo2, vlogger, emotech, hume-ai-evi, replica-ai, character-ai-c1.2, character-ai-c1.5, pi-inflection-1, pi-inflection-2, pi-inflection-2.5, coral-cohere, jasper-chat, copy-ai-chat, writesonic-chatsonic, rytr-chat, anyword-chat, quillbot-flow, grammarly-go, notion-ai, github-copilot, github-copilot-enterprise, gitlab-duo, cursor-fast, cursor-small, cursor-large, warp-ai, mutable-ai, sweep-ai, bloop-ai, cosine-genie, devin-ai, devika, swe-agent, opendevin, auto-gpt, baby-agi, meta-gpt, chatdev, camel-ai, gpt-engineer, gpt-researcher, private-gpt, local-gpt, h2o-gpt, oobabooga-text-generation-webui, koboldcpp, llama-cpp-python, vllm, text-generation-inference, ollama, lm-studio, gpt4all, jan-ai, faraday-dev, backyard-ai, anything-llm, open-webui, chatbox, msty, pinokio, comfyui, automatic1111, invokeai, fooocus, sd-next, stable-swarm, diffusion-bee, draw-things, guernika, diffusers, transformers, peft, trl, accelerate, deepspeed, megatron-lm, fairseq, fairseq2, espnet, speechbrain, kornia, timm, torchvision, torchaudio, torchtext, tensorrt-llm, onnxruntime, openvino, neural-magic-deepsparse, exllamav2, autoawq, autogptq, bitsandbytes, ggml, gguf, safetensors, pt, bin, ckpt, ckpt-v2, pth, h5, pb, tflite, coreml, mlpackage, mlmodel, ncnn, mnn, mindspore, paddlepaddle, jax, flax, optax, haiku, trax, equinox, flax-linformer, performer, reformer, routing-transformer, sinkhorn-transformer, sparse-transformer, longformer, linformer, x-transformers, lucidrains-implementations, biogpt, biogpt-large, clinicalcamel, gatortron, med-palm, med-palm-2, med-palm-3, bloomberggpt, fingpt, fingpt-v3, fingpt-v3.1, phind-codellama-34b-v1, phind-codellama-34b-v2, phind-70b, deepseek-coder-v1.5, deepseek-math-v1.5, wizardcoder-python-34b-v1.0, wizardcoder-33b-v1.1, wizardmath-70b-v1.0, openhermes-2.5, openhermes-2.5-vision, nous-hermes-2-solar-10.7b, nous-hermes-2-yi-34b, nous-hermes-2-mixtral-8x7b-dpo, samantha-1.1-llama-33b, samantha-1.2-mistral-7b, airoboros-l2-70b-2.1, airoboros-l2-70b-2.2, airoboros-l2-70b-3.1, platypus-70b, platypus2-70b-instruct, synthia-70b-v1.2, synthia-70b-v1.5, xwin-lm-70b-v0.1, xwin-lm-70b-v0.2, xwin-math-70b, orca-2-7b, orca-2-13b, orca-mini-3b, orca-mini-7b, orca-mini-13b, mpt-7b-8k, mpt-7b-8k-instruct, mpt-7b-8k-chat, mpt-30b-instruct, mpt-30b-chat, moss-moon-003-base, moss-moon-003-sft, moss-moon-003-plugin, skywork-13b-base, skywork-13b-chat, yuan-2.0-2b, yuan-2.0-51b, yuan-2.0-102b, bluelm-7b-base, bluelm-7b-chat, orion-14b-base, orion-14b-chat, polyglot-ko-1.3b, polyglot-ko-3.8b, polyglot-ko-5.8b, polyglot-ko-12.8b, koalpaca-polyglot-12.8b, rinna-3.6b, rinna-bilingual-pretrained-v2, japanese-stablelm-base-alpha-7b, japanese-stablelm-instruct-alpha-7b, karasu-1.8b, karasu-7b, qwen-japanese-7b, rt-1, rt-2, rt-x, octo-base, octo-small, openvla-7b, aloha, eliza, alice, parry, shrdlu, word2vec, glove, fasttext, ulmfit, elmo, cohere-command-r-plus, cohere-command-r-plus-08-2024, cohere-command-r-plus-12-2024, dreamshaper-xl, dreamshaper-v8, epicrealism, cyberrealistic, majicmix-realistic, absolutereality, deliberate-v2, deliberate-v3, rvc-v2, so-vits-svc-4.0, so-vits-svc-4.1, audioldm, audioldm2, audiogen, musicgen-small, musicgen-medium, musicgen-large, musicgen-melody, bark-large, tortoise-tts-v2, gpt-7, gpt-7-omni, gpt-7-brain, gpt-7.5-quantum, gpt-8, gpt-8-singularity, gpt-9-omega, gpt-10-godmode, claude-6-opus, claude-6-sonnet, claude-6-haiku, claude-7-opus, claude-7-sonnet, claude-7-haiku, llama-6-8b, llama-6-70b, llama-6-400b, llama-7-1t, llama-7-1t-instruct, gemini-5.0-pro, gemini-5.0-ultra, gemini-5.0-flash, gemini-6.0-omni, deepseek-v5, deepseek-v6, deepseek-v7, deepseek-r5, deepseek-r6, qwen-6-max, qwen-6-plus, qwen-6-turbo, qwen-7-max, qwen-8-max, mistral-large-2601, mistral-large-2701, pixtral-gigantic, grok-6, grok-7, grok-8, midjourney-v10, midjourney-v11, sora-v4, sora-v5, runway-gen5, runway-gen6, pika-4.0, pika-5.0, kling-v4, kling-v5, stable-diffusion-4.0, stable-diffusion-5.0, flux.3-pro, flux.3-dev, flux.3-schnell, dall-e-6, dall-e-7, yi-coder-34b, yi-coder-72b, yandexgpt-5, gigachat-2, saiga-llama-4, ru-gpt-5, ru-gpt-6, chatglm5-9b, chatglm6-9b, baichuan4, baichuan5, hunyuan-max, ernie-bot-5.0, ernie-bot-6.0, internlm3-7b, internlm3-20b, internvl3, cogvlm3, minicpm-v-3.0, llava-next-110b, idefics3, fuyu-32b, falcon-4, falcon-5, nemotron-5, nemotron-6, granite-4.0, granite-5.0, jamba-2.0, jamba-3.0, dbrx-2, solar-2, reka-core-2, reka-flash-2, reka-edge-2, phi-6, phi-7, gemma-5, gemma-6, aya-expanse-70b, aya-expanse-104b, command-r-v0.2, command-r-plus-v0.2, apple-afm-2, amazon-titan-2, olympus-amazon-2, samba-coe-v0.2, starcoder3, smaug-120b, capybara-34b, dolphin-3.0, dolphin-4.0, zephyr-7b-gamma, zephyr-7b-delta, miqu-2, miqu-3, goliath-240b, noromaid-v0.2, kunoichi-dpo-v3, fimbulvetr-v2, bagel-70b, openchat-4.0, starling-lm-70b, neural-chat-7b-v4, toppy-m-13b, mythomax-l2-70b, tiefighter-70b, chronos-hermes-70b, euryale-2.0, alpaca-13b, vicuna-70b, guanaco-13b, rwkv-7, rwkv-8, mamba-3, mamba-4, jina-embeddings-v3, bge-m4, nomic-embed-text-v2, all-minilm-l12-v2, e5-mistral-70b, mxbai-embed-large-v2, cohere-embed-english-v4.0, text-embedding-4, claude-3-haiku-20241022, gemini-1.5-pro-exp-0901, gemini-1.5-flash-exp-0901, mixtral-8x22b-instruct-v0.2, llama-3-120b-instruct, qwen-2.5-110b, meta-llama-4-120b, xwin-math-70b, plamo-70b, swallow-70b, elyza-japanese-llama-3-8b, blue-orchid-4x7b, llemma-70b, minotaur-70b, speecht5-large, bark-large-v2, seamless-m4t-v3, distil-whisper-large-v4, openvoice-v2, metavoice-2b, text-bison-003, chat-bison-003, code-bison-003, codechat-bison-003, magika-v2, alphafold-4, alphacode-3, alphageometry-2, alphastar-2, muzero-2, leela-chess-zero-v2, stockfish-17, llama-3-typhoon-v1.5-70b, qwen-2-math-110b, qwen-2-audio-72b, openai-o4, openai-o4-mini, openai-o4-preview, openai-o5, chatgpt-o2, chatgpt-o3, gpt-4o-realtime-preview-2, gpt-4o-audio-preview-2, claude-3-5-haiku-v2, qwen-3-max, deepseek-v4-coder-chat, deepseek-r2-zero, moonshot-v2-8k, moonshot-v2-32k, moonshot-v2-200k, kimi-chat-v2, abab7-chat, minimax-text-03, sensevoice-large, omchat-v3, bria-v3, hunyuan-dit-v2, lumina-next-t2i-v2, pixart-sigma-v2, sd4-large, sd4-large-turbo, sd4-medium, deepfloyd-if-v2, kandinsky-4.0, wuerstchen-v2, amused-1024, aura-flow-v2, mamba-codestral-72b, jamba-2-large, jamba-2-mini, granite-3.1-8b, ibm-granite-34b-code, reka-core-20241001, xai-grok-3-0202, xai-grok-3-vision-0202, whisper-v6, yolov13, yolov14, segment-anything-4, detic-v2, groundingdino-v2, blip-4, mplug-owl4, qwen-vl-max-0909, qwen-audio-turbo-v2, qwen-math-turbo-v2, open-sora-v2, latte-v2, mora-v2, h2o-danube4-4b, numina-math-72b, map-neo-72b, ziya-llama-70b, sus-chat-70b, eagle-70b, minicpm-3b, xverse-130b, codefuse-codellama-70b, wizardcoder-python-70b, phi-3-electra, btlm-7b, databricks-dolly-v2-12b, h2oai-predict-v2, llm360-emerald, nvidia-megatron-turing-nlg-1t, google-switch-transformer-v2, google-glam-v2, google-gopher-v2, google-chinchilla-v2, google-lamda-v2, google-sparrow-v2, google-meena-v2, facebook-blenderbot-4, facebook-galactica-v2, facebook-opt-iml-max-v2, anthropic-hh-rlhf-v2, aleph-alpha-luminous-supreme-v2, lighton-alfred-70b, nlpcloud-finetuned-llama-3, bria-rmbg-2.0, isnet-general-use-v2, u2net-v2, rembg-v2, dexined-v2, pidinet-v2, controlnet-canny-v2, controlnet-depth-v2, controlnet-hed-v2, controlnet-mlsd-v2, controlnet-normal-v2, controlnet-openpose-v2, controlnet-scribble-v2, controlnet-seg-v2, ip-adapter-v2, ip-adapter-faceid-v2, ip-adapter-plus-v2, lora-xl-v2, lycoris-v2, lokr-v2, loha-v2, svd-xt-1.2, animatediff-v4, text2video-zero-v2, zeroscope-v3, modelscope-damo-text-to-video-v2, potat-2, vqgan-clip-v2, clip-vit-huge, siglip-so400m-patch14-384-v2, eva-clip-v2, openclip-vit-G-14, dino-v3, deit-v2, swin-transformer-v2, vit-mae-v2, convnext-v3, resnet-152, efficientnet-v2, mobilenet-v4, rtdetr-v2, mask-rcnn-v2, faster-rcnn-v2, cascade-rcnn-v2, pointrend-v2, mask2former-v2, oneformer-v2, detr-v2, deformable-detr-v2, glip-v2, blip-v2, x-clip-v2, video-llava-v2, macaw-llm-v2, otter-video-v2, panda-70m-v2, internvideo3, vlogger-v2, emotech-v2, hume-ai-evi-v2, replica-ai-v2, character-ai-c2.0, pi-inflection-3, coral-cohere-v2, jasper-chat-v2, copy-ai-chat-v2, writesonic-chatsonic-v2, rytr-chat-v2, anyword-chat-v2, quillbot-flow-v2, grammarly-go-v2, notion-ai-v2, github-copilot-v2, gitlab-duo-v2, cursor-fast-v2, cursor-small-v2, cursor-large-v2, warp-ai-v2, mutable-ai-v2, sweep-ai-v2, bloop-ai-v2, cosine-genie-v2, devin-ai-v2, devika-v2, swe-agent-v2, opendevin-v2, auto-gpt-v2, baby-agi-v2, meta-gpt-v2, chatdev-v2, camel-ai-v2, gpt-engineer-v2, gpt-researcher-v2, private-gpt-v2, local-gpt-v2, h2o-gpt-v2, oobabooga-text-generation-webui-v2, koboldcpp-v2, llama-cpp-python-v2, vllm-v2, text-generation-inference-v2, ollama-v2, lm-studio-v2, gpt4all-v2, jan-ai-v2, faraday-dev-v2, backyard-ai-v2, anything-llm-v2, open-webui-v2, chatbox-v2, msty-v2, pinokio-v2, comfyui-v2, automatic1111-v2, invokeai-v2, fooocus-v2, sd-next-v2, stable-swarm-v2, diffusion-bee-v2, draw-things-v2, guernika-v2, diffusers-v2, transformers-v2, peft-v2, trl-v2, accelerate-v2, deepspeed-v2, megatron-lm-v2, fairseq-v2, fairseq2-v2, espnet-v2, speechbrain-v2, kornia-v2, timm-v2, torchvision-v2, torchaudio-v2, torchtext-v2, tensorrt-llm-v2, onnxruntime-v2, openvino-v2, neural-magic-deepsparse-v2, exllamav2-v2, autoawq-v2, autogptq-v2, bitsandbytes-v2, ggml-v2, gguf-v2, safetensors-v2, pt-v2, bin-v2, ckpt-v3, pth-v2, h5-v2, pb-v2, tflite-v2, coreml-v2, mlpackage-v2, mlmodel-v2, ncnn-v2, mnn-v2, mindspore-v2, paddlepaddle-v2, jax-v2, flax-v2, optax-v2, haiku-v2, trax-v2, equinox-v2, flax-linformer-v2, performer-v2, reformer-v2, routing-transformer-v2, sinkhorn-transformer-v2, sparse-transformer-v2, longformer-v2, linformer-v2, x-transformers-v2, lucidrains-implementations-v2";

        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < 15; i++)
        {
            builder.Append(baseModels).Append("  ");
        }

        var textBlock = new TextBlock
        {
            Text = builder.ToString(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (SolidColorBrush)FindResource("TextMutedColor"),
            Opacity = 0.85,
            LineHeight = 16,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(15),
            Content = textBlock,
            IsHitTestVisible = false
        };

        _fullScreenOverlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Topmost = true,
            WindowState = WindowState.Maximized,
            ShowInTaskbar = false
        };

        _fullScreenOverlay.PreviewMouseDown += (s, ev) =>
        {
            _fullScreenOverlay.Close();
            _fullScreenOverlay = null;
        };

        _fullScreenOverlay.Content = scrollViewer;
        _fullScreenOverlay.Show();
    }
}
    