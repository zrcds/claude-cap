using Avalonia.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace ClaudeCap;

/// <summary>
/// Hosts WebView2 Core inside an Avalonia Window (no WinForms dependency).
/// The Avalonia window provides the HWND parent for the CoreWebView2Controller.
/// HttpClient is NOT used — Cloudflare blocks it. All API calls run via
/// fetch() inside the real Chromium engine using ExecuteScriptAsync + postMessage.
/// </summary>
sealed class ClaudeWebScraper : IDisposable
{
    public record UsageResult(int Percent, int UsedCredits, int TotalCredits, string? ResetDate);

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tools", "claudecap", "webview2");

    private Window                   _host       = null!;
    private CoreWebView2Controller   _controller = null!;
    private CoreWebView2             _wv         = null!;
    private bool                     _ready;

    public static readonly ClaudeWebScraper Instance = new();
    private ClaudeWebScraper() { }

    // ── Init ─────────────────────────────────────────────────────────────────

    public async Task EnsureInitAsync()
    {
        if (_ready) return;
        Logger.Log("WebView2: initializing");

        // Create an Avalonia window to host WebView2 — no WinForms needed
        _host = new Window
        {
            Title                 = "Claude Cap",
            Width                 = 900,
            Height                = 680,
            ShowInTaskbar         = false,
            SystemDecorations     = SystemDecorations.Full,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        // Show briefly off-screen to obtain the underlying HWND
        _host.Show();
        await Task.Delay(100);

        var hwnd = _host.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not obtain HWND from Avalonia window");

        _host.Hide();

        // Create WebView2 controller attached to the Avalonia window's HWND
        Directory.CreateDirectory(DataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, DataFolder);
        _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
        _controller.IsVisible = false;
        _controller.Bounds    = new System.Drawing.Rectangle(0, 0, 900, 680);
        _wv = _controller.CoreWebView2;

        Logger.Log("WebView2: ready");
        _ready = true;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<UsageResult?> FetchAsync()
    {
        await EnsureInitAsync();
        Logger.Log("WebView2: FetchAsync");

        if (await GetSessionKeyAsync() != null)
        {
            var result = await GetUsageAsync();
            if (result != null) return result;
            Logger.Log("WebView2: session present but API failed — re-authenticating");
        }

        var key = await LoginAsync();
        if (key == null) return null;

        return await GetUsageAsync();
    }

    // ── Session check ─────────────────────────────────────────────────────────

    private async Task<string?> GetSessionKeyAsync()
    {
        try
        {
            var cookies = await _wv.CookieManager.GetCookiesAsync("https://claude.ai");
            var key     = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
            Logger.Log(key != null ? "WebView2: sessionKey found" : "WebView2: no sessionKey");
            return key;
        }
        catch (Exception ex)
        {
            Logger.Log($"WebView2: GetSessionKeyAsync error: {ex.Message}");
            return null;
        }
    }

    // ── Login flow ────────────────────────────────────────────────────────────

    private async Task<string?> LoginAsync()
    {
        Logger.Log("WebView2: starting login flow");
        var tcs = new TaskCompletionSource<bool>();

        EventHandler<CoreWebView2SourceChangedEventArgs>? sourceHandler = null;
        void Cleanup() => _wv.SourceChanged -= sourceHandler;

        sourceHandler = (_, _) =>
        {
            var url = _wv.Source ?? "";
            Logger.Log($"WebView2: source → {url}");
            bool isLoginPage = url.Contains("claude.ai/login")
                            || url.Contains("claude.ai/auth")
                            || url.Contains("claude.ai/sso-callback")
                            || url.Contains("okta.com");
            if (!isLoginPage && url.Contains("claude.ai"))
            {
                Cleanup();
                HideWindow();
                tcs.TrySetResult(true);
            }
        };
        _wv.SourceChanged += sourceHandler;

        ShowLoginWindow();
        _wv.Navigate("https://claude.ai/login");

        EventHandler<WindowClosingEventArgs>? onClose = null;
        onClose = (_, e) =>
        {
            e.Cancel = true;
            Logger.Log("WebView2: login window closed by user");
            Cleanup();
            HideWindow();
            _host.Closing -= onClose;
            tcs.TrySetResult(false);
        };
        _host.Closing += onClose;

        var ok = await tcs.Task;
        _host.Closing -= onClose;

        if (!ok) { Logger.Log("WebView2: login cancelled"); return null; }
        Logger.Log("WebView2: login completed");
        return await GetSessionKeyAsync();
    }

    // ── API calls via in-browser fetch ────────────────────────────────────────

    private async Task<UsageResult?> GetUsageAsync()
    {
        try
        {
            await EnsureOnClaudeAsync();

            var accountJson = await BrowserFetchAsync("https://claude.ai/api/account");
            if (accountJson == null) return null;

            string? accountUuid = null;
            string? orgUuid     = null;
            using (var accountDoc = JsonDocument.Parse(accountJson))
            {
                var acc = accountDoc.RootElement;
                if (acc.TryGetProperty("uuid", out var au)) accountUuid = au.GetString();
                if (acc.TryGetProperty("memberships", out var memberships) &&
                    memberships.GetArrayLength() > 0)
                {
                    var membership = memberships[0];
                    if (membership.TryGetProperty("organization", out var org) &&
                        org.TryGetProperty("uuid", out var ou))
                        orgUuid = ou.GetString();
                }
            }

            Logger.Log($"WebView2: orgUuid={orgUuid} accountUuid={accountUuid}");
            if (string.IsNullOrEmpty(orgUuid)) return null;

            var qs        = accountUuid != null ? $"?account_uuid={accountUuid}" : "";
            var usageUrl  = $"https://claude.ai/api/organizations/{orgUuid}/overage_spend_limit{qs}";
            var usageJson = await BrowserFetchAsync(usageUrl);
            if (usageJson == null) return null;
            Logger.Log($"WebView2: usage: {usageJson[..Math.Min(300, usageJson.Length)]}");

            using var usageDoc = JsonDocument.Parse(usageJson);
            var root  = usageDoc.RootElement;
            var used  = root.GetProperty("used_credits").GetInt32();
            var total = root.GetProperty("monthly_credit_limit").GetInt32();
            if (total <= 0) return null;

            string? resetDate = null;
            foreach (var field in new[] { "billing_period_ends_at", "period_ends_at", "billing_period_end", "next_reset_at", "reset_at" })
            {
                if (root.TryGetProperty(field, out var dt) &&
                    DateTime.TryParse(dt.GetString(), out var d))
                {
                    resetDate = d.ToString("MMM d");
                    break;
                }
            }
            resetDate ??= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(1).ToString("MMM d");

            Logger.Log($"WebView2: resetDate={resetDate}");
            return new UsageResult((int)Math.Round((double)used / total * 100), used, total, resetDate);
        }
        catch (Exception ex)
        {
            Logger.Log($"WebView2: GetUsageAsync error: {ex.Message}");
            return null;
        }
    }

    private async Task EnsureOnClaudeAsync()
    {
        if (_wv.Source?.StartsWith("https://claude.ai") == true) return;

        Logger.Log("WebView2: navigating to claude.ai for API context");
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? h = null;
        h = (_, _) => { _wv.NavigationCompleted -= h; tcs.TrySetResult(true); };
        _wv.NavigationCompleted += h;
        _wv.Navigate("https://claude.ai");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Logger.Log($"WebView2: now on {_wv.Source}");
    }

    private async Task<string?> BrowserFetchAsync(string url)
    {
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                _wv.WebMessageReceived -= handler;
                try
                {
                    var raw = JsonSerializer.Deserialize<string>(args.WebMessageAsJson);
                    tcs.TrySetResult(raw);
                }
                catch { tcs.TrySetResult(null); }
            };
            _wv.WebMessageReceived += handler;

            var safeUrl = url.Replace("'", "\\'");
            await _wv.ExecuteScriptAsync($$"""
                fetch('{{safeUrl}}', { credentials: 'include', headers: { Accept: 'application/json' } })
                    .then(r => r.ok
                        ? r.text()
                        : r.text().then(b => { throw new Error(r.status + ':' + b.substring(0, 200)); }))
                    .then(t => window.chrome.webview.postMessage(t))
                    .catch(e => window.chrome.webview.postMessage('__ERR:' + e.message));
                """);

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (result?.StartsWith("__ERR:") == true)
            {
                Logger.Log($"WebView2: BrowserFetch error for {url}: {result}");
                return null;
            }
            Logger.Log($"WebView2: BrowserFetch {url} → ok ({result?.Length ?? 0} chars)");
            return result;
        }
        catch (TimeoutException)
        {
            Logger.Log($"WebView2: BrowserFetch timed out for {url}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"WebView2: BrowserFetchAsync error: {ex.Message}");
            return null;
        }
    }

    // ── Window helpers ────────────────────────────────────────────────────────

    private void ShowLoginWindow()
    {
        Logger.Log("WebView2: showing login window");
        _host.Title         = "Claude Cap — Sign in to claude.ai";
        _host.ShowInTaskbar = true;
        _controller.Bounds    = new System.Drawing.Rectangle(0, 0, (int)_host.Width, (int)_host.Height);
        _controller.IsVisible = true;
        _host.Show();
        _host.Activate();
    }

    private void HideWindow()
    {
        _controller.IsVisible = false;
        _host.ShowInTaskbar   = false;
        _host.Hide();
    }

    // ── Session management ────────────────────────────────────────────────────

    public void ClearSession()
    {
        if (_ready)
        {
            _wv.CookieManager.DeleteAllCookies();
            Logger.Log("WebView2: cookies cleared");
        }
        else
        {
            try
            {
                if (Directory.Exists(DataFolder))
                    Directory.Delete(DataFolder, recursive: true);
                Logger.Log("WebView2: data folder deleted");
            }
            catch (Exception ex) { Logger.Log($"WebView2: clear error: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _controller?.Close();
        _host?.Close();
    }
}
