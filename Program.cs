using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SkiaSharp;
using System.Text.Json;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace ClaudeCap;

// ── Entry point ───────────────────────────────────────────────────────────────

static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
}

// ── Avalonia Application ──────────────────────────────────────────────────────

class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            TrayApp.Start(desktop);
        base.OnFrameworkInitializationCompleted();
    }
}

// ── Tray application logic ────────────────────────────────────────────────────

static class TrayApp
{
    private static TrayIcon?         _tray;
    private static NativeMenuItem?   _statusItem;
    private static NativeMenuItem?   _intervalMenu;
    private static DispatcherTimer?  _timer;
    private static DispatcherTimer?  _blinkTimer;
    private static bool              _blinkOn  = true;
    private static AppConfig         _config   = new();
    private static int?              _usagePercent;
    private static DateTime?         _lastUpdated;
    private static int               _lastNotifiedThreshold = 0;
    private static UsageGraphWindow? _graphWindow;

    private static readonly string OutputFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "usage_data.json");

    private const string AppName = "ClaudeCap";
#if WINDOWS
    private const string StartupRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
#endif

    private static WindowIcon _iconNormal = null!;
    private static WindowIcon _iconError  = null!;
    private static WindowIcon _iconDim    = null!;
    private static WindowIcon _iconOrange = null!;
    private static WindowIcon _iconMaxed  = null!;

    // ── Start ─────────────────────────────────────────────────────────────────

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Logger.Clear();
        Logger.Log("=== ClaudeCap starting (Avalonia) ===");

        var mutex = new System.Threading.Mutex(true, AppName, out bool isNew);
        if (!isNew)
        {
            Logger.Log("Already running — exiting");
            desktop.Shutdown();
            return;
        }

        _config = AppConfig.Load();
        Logger.Log($"Config: RefreshInterval={_config.RefreshIntervalMinutes} min");
        SelfInstall();
        LoadIcons();
        BuildTray();
        StartTimer();
        _ = RefreshAsync();
    }

    // ── Icons (SkiaSharp — cross-platform) ───────────────────────────────────

    static void LoadIcons()
    {
        using var stream = typeof(TrayApp).Assembly
            .GetManifestResourceStream("ClaudeCap.icon.ico")!;
        using var baseBmp = SKBitmap.Decode(stream)
            ?? throw new Exception("Failed to decode icon.ico");

        _iconNormal = SkiaBitmapToWindowIcon(baseBmp);
        _iconError  = TintWithSkia(baseBmp, new SKColor(210,  45,  45));
        _iconDim    = TintWithSkia(baseBmp, new SKColor( 90,  90,  90));
        _iconOrange = TintWithSkia(baseBmp, new SKColor(220, 120,  20));
        _iconMaxed  = TintWithSkia(baseBmp, new SKColor(180,   0,   0));
    }

    static WindowIcon SkiaBitmapToWindowIcon(SKBitmap bmp)
    {
        using var img  = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms   = new MemoryStream(data.ToArray());
        return new WindowIcon(ms);
    }

    static WindowIcon TintWithSkia(SKBitmap src, SKColor tint)
    {
        var dst = new SKBitmap(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width;  x++)
        {
            var px = src.GetPixel(x, y);
            if (px.Alpha == 0) { dst.SetPixel(x, y, SKColors.Transparent); continue; }
            float lum = (0.299f * px.Red + 0.587f * px.Green + 0.114f * px.Blue) / 255f;
            dst.SetPixel(x, y, new SKColor(
                (byte)(lum * tint.Red),
                (byte)(lum * tint.Green),
                (byte)(lum * tint.Blue),
                px.Alpha));
        }
        var result = SkiaBitmapToWindowIcon(dst);
        dst.Dispose();
        return result;
    }

    // ── Tray icon ─────────────────────────────────────────────────────────────

    static void BuildTray()
    {
        var menu = new NativeMenu();

        _statusItem = new NativeMenuItem("Claude Cap — loading…") { IsEnabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var refreshItem = new NativeMenuItem("Refresh now");
        refreshItem.Click += (_, _) => _ = RefreshAsync();
        menu.Items.Add(refreshItem);

        _intervalMenu = new NativeMenuItem("Refresh every") { Menu = BuildIntervalMenu() };
        menu.Items.Add(_intervalMenu);

        menu.Items.Add(new NativeMenuItemSeparator());

        var usageItem = new NativeMenuItem("Open claude.ai/usage");
        usageItem.Click += (_, _) => OpenUsagePage();
        menu.Items.Add(usageItem);

        var graphItem = new NativeMenuItem("View usage trend…");
        graphItem.Click += (_, _) => ShowUsageGraph();
        menu.Items.Add(graphItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        static string StartupLabel(bool on) => on ? "Launch on Startup  ✓" : "Launch on Startup";
        var startupItem = new NativeMenuItem(StartupLabel(IsStartupEnabled()));
        startupItem.Click += (_, _) =>
        {
            var enable = startupItem.Header == StartupLabel(false);
            SetStartup(enable);
            startupItem.Header = StartupLabel(enable);
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var signOutItem = new NativeMenuItem("Sign out from claude.ai…");
        signOutItem.Click += (_, _) =>
        {
            ClaudeWebScraper.Instance.ClearSession();
            Logger.Log("Session cleared");
            _ = RefreshAsync();
        };
        menu.Items.Add(signOutItem);

        var logsItem = new NativeMenuItem("View Logs");
        logsItem.Click += (_, _) => Logger.Open();
        menu.Items.Add(logsItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        _tray = new TrayIcon
        {
            Icon        = _iconNormal,
            ToolTipText = "Claude Cap — loading…",
            Menu        = menu,
            IsVisible   = true,
        };
    }

    static NativeMenu BuildIntervalMenu()
    {
        var sub = new NativeMenu();
        foreach (var mins in new[] { 1, 2, 5, 10, 15, 30 })
        {
            var label = mins == 1 ? "1 minute" : $"{mins} minutes";
            var item  = new NativeMenuItem(label)
            {
                IsChecked = mins == _config.RefreshIntervalMinutes,
            };
            item.Click += (_, _) => OnIntervalClick(mins);
            sub.Items.Add(item);
        }
        return sub;
    }

    static void OnIntervalClick(int mins)
    {
        _config.RefreshIntervalMinutes = mins;
        _config.Save();
        if (_timer != null) _timer.Interval = TimeSpan.FromMinutes(mins);
        Logger.Log($"Interval changed to {mins} min");
        RefreshIntervalCheckmarks();
    }

    static void RefreshIntervalCheckmarks()
    {
        if (_intervalMenu?.Menu == null) return;
        foreach (var it in _intervalMenu.Menu.Items.OfType<NativeMenuItem>())
        {
            var parts = it.Header?.Split(' ');
            if (parts?.Length > 0 && int.TryParse(parts[0], out int n))
                it.IsChecked = n == _config.RefreshIntervalMinutes;
        }
    }

    // ── Refresh loop ──────────────────────────────────────────────────────────

    static void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_config.RefreshIntervalMinutes) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Logger.Log($"Timer: refresh every {_config.RefreshIntervalMinutes} min");
    }

    static async Task RefreshAsync()
    {
        Logger.Log("--- RefreshAsync ---");
        StartBlink();
        var result = await ClaudeWebScraper.Instance.FetchAsync();
        StopBlink();

        if (result == null)
        {
            Logger.Log("RefreshAsync: fetch returned null");
            SetTray(_iconError, "Claude Cap: could not retrieve usage data.");
            return;
        }

        Logger.Log($"RefreshAsync: {result.Percent}% ({result.UsedCredits}/{result.TotalCredits})");
        UpdateDisplay(result);
    }

    static void UpdateDisplay(ClaudeWebScraper.UsageResult result)
    {
        _usagePercent = result.Percent;
        _lastUpdated  = DateTime.Now;

        double usedDollars  = result.UsedCredits  / 100.0;
        double totalDollars = result.TotalCredits / 100.0;

        File.WriteAllText(OutputFile, JsonSerializer.Serialize(new
        {
            percent       = result.Percent,
            used          = result.UsedCredits,
            total         = result.TotalCredits,
            used_dollars  = Math.Round(usedDollars,  2),
            total_dollars = Math.Round(totalDollars, 2),
            reset         = result.ResetDate,
        }));

        UsageHistory.Record(usedDollars, totalDollars, result.Percent);

        if (_statusItem != null)
            _statusItem.Header = $"${usedDollars:F2} / ${totalDollars:F2} · {result.Percent}%";

        var resetLine = result.ResetDate != null ? $"\nResets: {result.ResetDate}" : "";
        var icon = result.Percent >= 100 ? _iconMaxed
                 : result.Percent >= 90  ? _iconOrange
                 : _iconNormal;

        SetTray(icon,
            $"Claude Plan: {result.Percent}% used\n" +
            $"${usedDollars:F2} of ${totalDollars:F2} spent{resetLine}\n" +
            $"Updated: {_lastUpdated:HH:mm:ss}");

        int threshold = result.Percent >= 100 ? 100 : result.Percent >= 90 ? 90 : result.Percent >= 80 ? 80 : 0;
        if (threshold > _lastNotifiedThreshold)
        {
            _lastNotifiedThreshold = threshold;
            Logger.Log($"Threshold reached: {threshold}%");
            // TODO: add system notifications in a future PR
        }
    }

    // ── Blink ─────────────────────────────────────────────────────────────────

    static void StartBlink()
    {
        if (_blinkTimer != null) return;
        _blinkOn    = true;
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            if (_tray != null) _tray.Icon = _blinkOn ? _iconNormal : _iconDim;
        };
        _blinkTimer.Start();
        if (_tray != null)
        {
            _tray.Icon        = _iconNormal;
            _tray.ToolTipText = "Claude Cap — connecting…";
        }
    }

    static void StopBlink()
    {
        _blinkTimer?.Stop();
        _blinkTimer = null;
    }

    static void SetTray(WindowIcon icon, string tooltip)
    {
        if (_tray == null) return;
        _tray.Icon        = icon;
        _tray.ToolTipText = tooltip[..Math.Min(127, tooltip.Length)];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static void SelfInstall()
    {
        var claudeDir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var scriptDest   = Path.Combine(claudeDir, "statusline-command.sh");
        var settingsPath = Path.Combine(claudeDir, "settings.json");

        Directory.CreateDirectory(claudeDir);

        if (!File.Exists(scriptDest))
        {
            try
            {
                using var stream = typeof(TrayApp).Assembly
                    .GetManifestResourceStream("ClaudeCap.statusline-command.sh")!;
                using var reader = new System.IO.StreamReader(stream);
                var content = reader.ReadToEnd().Replace("\r\n", "\n");
                File.WriteAllText(scriptDest, content, new System.Text.UTF8Encoding(false));
                Logger.Log($"SelfInstall: wrote {scriptDest}");
            }
            catch (Exception ex) { Logger.Log($"SelfInstall: script error: {ex.Message}"); }
        }

        try
        {
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(settingsPath))
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            else
                root = new System.Text.Json.Nodes.JsonObject();

            if (!root.ContainsKey("statusLine"))
            {
                root["statusLine"] = System.Text.Json.Nodes.JsonNode.Parse(
                    """{"type":"command","command":"bash ~/.claude/statusline-command.sh"}""");
                File.WriteAllText(settingsPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Logger.Log("SelfInstall: registered statusLine");
            }
        }
        catch (Exception ex) { Logger.Log($"SelfInstall: settings error: {ex.Message}"); }

#if WINDOWS
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, true);
            if (key != null)
            {
                var current  = key.GetValue(AppName) as string;
                var expected = $"\"{Environment.ProcessPath}\"";
                if (current != null && current != expected)
                {
                    key.SetValue(AppName, expected);
                    Logger.Log("SelfInstall: updated startup path");
                }
            }
        }
        catch (Exception ex) { Logger.Log($"SelfInstall: registry error: {ex.Message}"); }
#endif
    }

    static void OpenUsagePage() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "https://claude.ai/settings/usage",
            UseShellExecute = true,
        });

    static void ShowUsageGraph()
    {
        if (_graphWindow != null && _graphWindow.IsVisible) { _graphWindow.Activate(); return; }
        var history      = UsageHistory.Load();
        var totalDollars = File.Exists(OutputFile)
            ? (JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(OutputFile))
                .TryGetProperty("total_dollars", out var v) ? v.GetDouble() : 250)
            : 250;
        _graphWindow = new UsageGraphWindow(history, totalDollars);
        _graphWindow.Show();
    }

    static bool IsStartupEnabled()
    {
#if WINDOWS
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, false);
        return key?.GetValue(AppName) != null;
#elif MACOS
        return File.Exists(MacLaunchAgentPlist);
#else
        return false;
#endif
    }

    static void SetStartup(bool enable)
    {
#if WINDOWS
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, true)!;
        if (enable) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else        key.DeleteValue(AppName, false);
#elif MACOS
        if (enable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacLaunchAgentPlist)!);
            File.WriteAllText(MacLaunchAgentPlist, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.zrcds.claudecap</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{Environment.ProcessPath}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>KeepAlive</key>
                    <false/>
                </dict>
                </plist>
                """);
        }
        else
        {
            if (File.Exists(MacLaunchAgentPlist)) File.Delete(MacLaunchAgentPlist);
        }
#endif
    }

#if MACOS
    static readonly string MacLaunchAgentPlist = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.zrcds.claudecap.plist");
#endif

    static void ExitApp()
    {
        StopBlink();
        _timer?.Stop();
        if (_tray != null) _tray.IsVisible = false;
        ClaudeWebScraper.Instance.Dispose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt)
            lt.Shutdown();
    }
}
