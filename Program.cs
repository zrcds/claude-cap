using Microsoft.Win32;
using System.Text.Json;

namespace ClaudeCap;

static class Program
{
    private enum TrayState { Loading, Connecting, Ok, Error }

    private static NotifyIcon?                   _trayIcon;
    private static System.Windows.Forms.Timer?   _timer;
    private static System.Windows.Forms.Timer?   _blinkTimer;
    private static bool                          _blinkOn = true;
    private static AppConfig                     _config  = new();
    private static int?                          _usagePercent;
    private static DateTime?                     _lastUpdated;
    private static ToolStripMenuItem?            _intervalMenu;
    private static int                           _lastNotifiedThreshold = 0;

    private static readonly string OutputFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "usage_data.json");

    private const string AppName      = "ClaudeCap";
    private const string StartupRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static Icon _capIcon        = LoadCapIcon();
    private static Icon _capIconError   = TintCapIcon(System.Drawing.Color.FromArgb(210, 45, 45));
    private static Icon _capIconDim     = TintCapIcon(System.Drawing.Color.FromArgb(90, 90, 90));
    private static Icon _capIconOrange  = TintCapIcon(System.Drawing.Color.FromArgb(220, 120, 20));
    private static Icon _capIconMaxed   = TintCapIcon(System.Drawing.Color.FromArgb(180, 0, 0));

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Logger.Clear();
        Logger.Log("=== ClaudeCap starting ===");

        var mutex = new System.Threading.Mutex(true, AppName, out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Claude Cap is already running.", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _config = AppConfig.Load();
        Logger.Log($"Config: RefreshInterval={_config.RefreshIntervalMinutes} min");
        SelfInstall();

        BuildTrayIcon();
        StartTimer();
        _ = RefreshAsync();

        Application.Run();
        mutex.ReleaseMutex();
    }

    // ── Tray icon setup ───────────────────────────────────────────────────────

    static void BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        _intervalMenu = BuildIntervalMenu();
        menu.Items.Add(_intervalMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open claude.ai/usage", null, (_, _) => OpenUsagePage());
        menu.Items.Add("View usage trend…", null, (_, _) => ShowUsageGraph());
        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Launch on Startup") { CheckOnClick = true };
        startupItem.Checked = IsStartupEnabled();
        startupItem.CheckedChanged += (_, _) => SetStartup(startupItem.Checked);
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sign out from claude.ai…", null, (_, _) =>
        {
            ClaudeWebScraper.Instance.ClearSession();
            Logger.Log("Session cleared by user");
            _ = RefreshAsync();
        });
        menu.Items.Add("View Logs", null, (_, _) => Logger.Open());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon             = _capIcon,
            Text             = "Claude Cap — loading…",
            ContextMenuStrip = menu,
            Visible          = true,
        };

        _trayIcon.DoubleClick += (_, _) => OpenUsagePage();
    }

    static ToolStripMenuItem BuildIntervalMenu()
    {
        var sub = new ToolStripMenuItem("Refresh every");
        foreach (var mins in new[] { 1, 2, 5, 10, 15, 30 })
        {
            var label = mins == 1 ? "1 minute" : $"{mins} minutes";
            var item  = new ToolStripMenuItem(label) { Tag = mins, Checked = mins == _config.RefreshIntervalMinutes };
            item.Click += OnIntervalClick;
            sub.DropDownItems.Add(item);
        }
        return sub;
    }

    static void OnIntervalClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not int mins) return;
        _config.RefreshIntervalMinutes = mins;
        _config.Save();
        _timer!.Interval = mins * 60 * 1000;
        Logger.Log($"Interval changed to {mins} min");
        RefreshIntervalCheckmarks();
    }

    static void RefreshIntervalCheckmarks()
    {
        if (_intervalMenu == null) return;
        foreach (ToolStripMenuItem item in _intervalMenu.DropDownItems)
            item.Checked = (int)item.Tag! == _config.RefreshIntervalMinutes;
    }

    // ── Refresh loop ──────────────────────────────────────────────────────────

    static void StartTimer()
    {
        _timer = new System.Windows.Forms.Timer
        {
            Interval = _config.RefreshIntervalMinutes * 60 * 1000
        };
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
            SetTray(_capIconError, "Claude Cap: could not retrieve usage data.");
            if (_trayIcon != null)
                _trayIcon.ShowBalloonTip(5000, "Claude Cap",
                    "Could not retrieve usage data.", ToolTipIcon.Error);
            return;
        }

        Logger.Log($"RefreshAsync: {result.Percent}% ({result.UsedCredits}/{result.TotalCredits})");

        if (_trayIcon?.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.Invoke(() => UpdateDisplay(result));
        else
            UpdateDisplay(result);
    }

    static void UpdateDisplay(ClaudeWebScraper.UsageResult result)
    {
        _usagePercent = result.Percent;
        _lastUpdated  = DateTime.Now;

        // Credits are in 1/100,000 of a dollar (25,000,000 credits = $250)
        double usedDollars  = result.UsedCredits  / 100.0;
        double totalDollars = result.TotalCredits / 100.0;

        File.WriteAllText(OutputFile, JsonSerializer.Serialize(new
        {
            percent     = result.Percent,
            used        = result.UsedCredits,
            total       = result.TotalCredits,
            used_dollars  = Math.Round(usedDollars,  2),
            total_dollars = Math.Round(totalDollars, 2),
            reset       = result.ResetDate,
        }));

        UsageHistory.Record(usedDollars, totalDollars, result.Percent);

        var resetLine = result.ResetDate != null ? $"\nResets: {result.ResetDate}" : "";
        var icon = result.Percent >= 100 ? _capIconMaxed
                 : result.Percent >= 90  ? _capIconOrange
                 : _capIcon;
        SetTray(icon,
            $"Claude Plan: {result.Percent}% used\n" +
            $"${usedDollars:F2} of ${totalDollars:F2} spent{resetLine}\n" +
            $"Updated: {_lastUpdated:HH:mm:ss}");

        int threshold = result.Percent >= 100 ? 100 : result.Percent >= 90 ? 90 : result.Percent >= 80 ? 80 : 0;
        if (threshold > _lastNotifiedThreshold)
        {
            _lastNotifiedThreshold = threshold;
            if (threshold >= 100)
                _trayIcon!.ShowBalloonTip(5000, "Claude Usage",
                    $"Plan limit reached! ({result.Percent}%)", ToolTipIcon.Error);
            else if (threshold >= 90)
                _trayIcon!.ShowBalloonTip(4000, "Claude Usage",
                    $"⚠️ {result.Percent}% of plan used!", ToolTipIcon.Warning);
            else
                _trayIcon!.ShowBalloonTip(3000, "Claude Usage",
                    $"{result.Percent}% of plan used.", ToolTipIcon.Info);
        }
    }

    // ── Blink (connecting state) ───────────────────────────────────────────────

    static void StartBlink()
    {
        void Apply()
        {
            if (_blinkTimer != null) return;
            _blinkOn    = true;
            _blinkTimer = new System.Windows.Forms.Timer { Interval = 550 };
            _blinkTimer.Tick += (_, _) =>
            {
                _blinkOn = !_blinkOn;
                if (_trayIcon != null)
                    _trayIcon.Icon = _blinkOn ? _capIcon : _capIconDim;
            };
            _blinkTimer.Start();
            if (_trayIcon != null)
            {
                _trayIcon.Icon = _capIcon;
                _trayIcon.Text = "Claude Cap — connecting…";
            }
        }

        if (_trayIcon?.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.Invoke(Apply);
        else
            Apply();
    }

    static void StopBlink()
    {
        void Apply()
        {
            _blinkTimer?.Stop();
            _blinkTimer?.Dispose();
            _blinkTimer = null;
        }

        if (_trayIcon?.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.Invoke(Apply);
        else
            Apply();
    }

    static void SetTray(Icon icon, string tooltip)
    {
        void Apply()
        {
            if (_trayIcon == null) return;
            _trayIcon.Icon = icon;
            _trayIcon.Text = tooltip[..Math.Min(127, tooltip.Length)];
        }

        if (_trayIcon?.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.Invoke(Apply);
        else
            Apply();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static void SelfInstall()
    {
        var claudeDir     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var scriptDest    = Path.Combine(claudeDir, "statusline-command.sh");
        var settingsPath  = Path.Combine(claudeDir, "settings.json");

        Directory.CreateDirectory(claudeDir);

        // Write statusline script if not already present
        if (!File.Exists(scriptDest))
        {
            try
            {
                using var stream = typeof(Program).Assembly
                    .GetManifestResourceStream("ClaudeCap.statusline-command.sh")!;
                using var reader = new System.IO.StreamReader(stream);
                var content = reader.ReadToEnd().Replace("\r\n", "\n"); // ensure LF line endings
                File.WriteAllText(scriptDest, content, new System.Text.UTF8Encoding(false));
                Logger.Log($"SelfInstall: wrote {scriptDest}");
            }
            catch (Exception ex) { Logger.Log($"SelfInstall: failed to write script: {ex.Message}"); }
        }

        // Patch settings.json to register the statusLine command if not already set
        try
        {
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(settingsPath))
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!
                           .AsObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            if (!root.ContainsKey("statusLine"))
            {
                root["statusLine"] = System.Text.Json.Nodes.JsonNode.Parse(
                    """{"type":"command","command":"bash ~/.claude/statusline-command.sh"}""");
                File.WriteAllText(settingsPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Logger.Log($"SelfInstall: registered statusLine in {settingsPath}");
            }
        }
        catch (Exception ex) { Logger.Log($"SelfInstall: failed to patch settings.json: {ex.Message}"); }

        // Auto-repair startup registry if enabled but pointing to wrong path
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, true);
            if (key != null)
            {
                var current = key.GetValue(AppName) as string;
                var expected = $"\"{Environment.ProcessPath}\"";
                if (current != null && current != expected)
                {
                    key.SetValue(AppName, expected);
                    Logger.Log($"SelfInstall: updated startup path from {current} to {expected}");
                }
            }
        }
        catch (Exception ex) { Logger.Log($"SelfInstall: failed to update startup registry: {ex.Message}"); }
    }

    static Icon LoadCapIcon()
    {
        var stream = typeof(Program).Assembly
            .GetManifestResourceStream("ClaudeCap.icon.ico")!;
        return new Icon(stream, new System.Drawing.Size(16, 16));
    }

    static Icon TintCapIcon(System.Drawing.Color tint)
    {
        using var src = _capIcon.ToBitmap();
        var dst = new System.Drawing.Bitmap(src.Width, src.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width;  x++)
        {
            var px = src.GetPixel(x, y);
            if (px.A == 0) { dst.SetPixel(x, y, System.Drawing.Color.Transparent); continue; }
            float lum = (0.299f * px.R + 0.587f * px.G + 0.114f * px.B) / 255f;
            dst.SetPixel(x, y, System.Drawing.Color.FromArgb(
                px.A,
                (int)(lum * tint.R),
                (int)(lum * tint.G),
                (int)(lum * tint.B)));
        }
        return Icon.FromHandle(dst.GetHicon());
    }

    static void OpenUsagePage() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "https://claude.ai/settings/usage",
            UseShellExecute = true,
        });

    static UsageGraphForm? _graphForm;
    static void ShowUsageGraph()
    {
        if (_graphForm != null && !_graphForm.IsDisposed) { _graphForm.Activate(); return; }
        var history      = UsageHistory.Load();
        var totalDollars = File.Exists(OutputFile)
            ? (JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(OutputFile))
                .TryGetProperty("total_dollars", out var v) ? v.GetDouble() : 250)
            : 250;
        _graphForm = new UsageGraphForm(history, totalDollars);
        _graphForm.Show();
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, false);
        return key?.GetValue(AppName) != null;
    }

    static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, true)!;
        if (enable)
            key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(AppName, false);
    }

    static void ExitApp()
    {
        StopBlink();
        _timer?.Stop();
        _trayIcon!.Visible = false;
        _trayIcon.Dispose();
        ClaudeWebScraper.Instance.Dispose();
        Application.Exit();
    }
}
