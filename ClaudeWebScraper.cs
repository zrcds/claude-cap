using System.Text.Json;
#if WINDOWS
using Avalonia.Controls;
using Microsoft.Web.WebView2.Core;
#elif MACOS
using AppKit;
using CoreGraphics;
using Foundation;
using WebKit;
#endif

namespace ClaudeCap;

/// <summary>
/// Fetches Claude plan usage from claude.ai.
/// Windows: CoreWebView2 (WebView2/Chromium) hosted in an Avalonia Window HWND.
/// macOS:   WKWebView hosted in a native NSWindow.
/// HttpClient is NOT used — Cloudflare blocks it regardless of cookies.
/// All API calls run via fetch() inside the real browser engine.
/// </summary>
sealed class ClaudeWebScraper : IDisposable
{
    public record UsageResult(int Percent, int UsedCredits, int TotalCredits, string? ResetDate);

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tools", "claudecap", "webview2");

    private bool _ready;

#if WINDOWS
    private Window                   _host       = null!;
    private CoreWebView2Controller   _controller = null!;
    private CoreWebView2             _wv         = null!;
#elif MACOS
    private NSWindow                 _macWindow  = null!;
    private WKWebView                _wv         = null!;
    private ScriptMessageHandler     _msgHandler = null!;
    private NavigationDelegate       _navDelegate = null!;
    private CloseBlocker             _closeBlocker = null!;
#endif

    public static readonly ClaudeWebScraper Instance = new();
    private ClaudeWebScraper() { }

    // ── Init ─────────────────────────────────────────────────────────────────

    public async Task EnsureInitAsync()
    {
        if (_ready) return;

#if WINDOWS
        Logger.Log("WebView2: initializing");

        _host = new Window
        {
            Title                 = "Claude Cap",
            Width                 = 900,
            Height                = 680,
            ShowInTaskbar         = false,
            SystemDecorations     = SystemDecorations.Full,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        _host.Show();
        await Task.Delay(100);

        var hwnd = _host.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not obtain HWND from Avalonia window");

        _host.Hide();

        Directory.CreateDirectory(DataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, DataFolder);
        _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
        _controller.IsVisible = false;
        _controller.Bounds    = new System.Drawing.Rectangle(0, 0, 900, 680);
        _wv = _controller.CoreWebView2;

        Logger.Log("WebView2: ready");

#elif MACOS
        Logger.Log("WKWebView: initializing");

        _msgHandler  = new ScriptMessageHandler();
        _navDelegate = new NavigationDelegate();
        _closeBlocker = new CloseBlocker();

        var config = new WKWebViewConfiguration();
        config.UserContentController.AddScriptMessageHandler(_msgHandler, "claudecap");

        _wv = new WKWebView(new CGRect(0, 0, 900, 680), config);
        _wv.NavigationDelegate = _navDelegate;

        _macWindow = new NSWindow(
            new CGRect(0, 0, 900, 680),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered,
            false);
        _macWindow.ContentView   = _wv;
        _macWindow.IsReleasedWhenClosed = false;
        _macWindow.Delegate      = _closeBlocker;
        _macWindow.OrderOut(null); // hidden until login needed

        Logger.Log("WKWebView: ready");
        await Task.CompletedTask;
#endif

        _ready = true;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<UsageResult?> FetchAsync()
    {
        await EnsureInitAsync();
        Logger.Log("Scraper: FetchAsync");

        // Try existing session first
        if (await GetSessionKeyAsync() != null)
        {
            var result = await GetUsageAsync();
            if (result != null) return result;
            Logger.Log("Scraper: session present but API failed — re-authenticating");
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
#if WINDOWS
            var cookies = await _wv.CookieManager.GetCookiesAsync("https://claude.ai");
            var key     = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
#elif MACOS
            var tcs = new TaskCompletionSource<string?>();
            _wv.Configuration.WebsiteDataStore.HttpCookieStore.GetAllCookies(cookies =>
            {
                var key = cookies?.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
                tcs.TrySetResult(key);
            });
            var key = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
#else
            string? key = null;
            await Task.CompletedTask;
#endif
            Logger.Log(key != null ? "Scraper: sessionKey found" : "Scraper: no sessionKey");
            return key;
        }
        catch (Exception ex)
        {
            Logger.Log($"Scraper: GetSessionKeyAsync error: {ex.Message}");
            return null;
        }
    }

    // ── Login flow ────────────────────────────────────────────────────────────

    private async Task<string?> LoginAsync()
    {
        Logger.Log("Scraper: starting login flow");
        var tcs = new TaskCompletionSource<bool>();

#if WINDOWS
        EventHandler<CoreWebView2SourceChangedEventArgs>? sourceHandler = null;
        void Cleanup() => _wv.SourceChanged -= sourceHandler;

        sourceHandler = (_, _) =>
        {
            var url = _wv.Source ?? "";
            Logger.Log($"Scraper: source → {url}");
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
            Logger.Log("Scraper: login window closed");
            Cleanup();
            HideWindow();
            _host.Closing -= onClose;
            tcs.TrySetResult(false);
        };
        _host.Closing += onClose;

        var ok = await tcs.Task;
        _host.Closing -= onClose;

#elif MACOS
        Action<string?>? navHandler = null;
        void Cleanup() => _navDelegate.NavigationFinished -= navHandler;

        navHandler = (url) =>
        {
            Logger.Log($"Scraper: navigation → {url}");
            if (url == null) return;
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
        _navDelegate.NavigationFinished += navHandler;

        _closeBlocker.UserTriedToClose += OnClose;
        void OnClose()
        {
            Logger.Log("Scraper: login window closed");
            Cleanup();
            HideWindow();
            _closeBlocker.UserTriedToClose -= OnClose;
            tcs.TrySetResult(false);
        }

        ShowLoginWindow();
        _wv.LoadRequest(new NSUrlRequest(new NSUrl("https://claude.ai/login")));

        var ok = await tcs.Task;
        _closeBlocker.UserTriedToClose -= OnClose;
#else
        var ok = false;
        await Task.CompletedTask;
#endif

        if (!ok) { Logger.Log("Scraper: login cancelled"); return null; }
        Logger.Log("Scraper: login completed");
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

            Logger.Log($"Scraper: orgUuid={orgUuid} accountUuid={accountUuid}");
            if (string.IsNullOrEmpty(orgUuid)) return null;

            var qs        = accountUuid != null ? $"?account_uuid={accountUuid}" : "";
            var usageUrl  = $"https://claude.ai/api/organizations/{orgUuid}/overage_spend_limit{qs}";
            var usageJson = await BrowserFetchAsync(usageUrl);
            if (usageJson == null) return null;
            Logger.Log($"Scraper: usage: {usageJson[..Math.Min(300, usageJson.Length)]}");

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
            Logger.Log($"Scraper: resetDate={resetDate}");
            return new UsageResult((int)Math.Round((double)used / total * 100), used, total, resetDate);
        }
        catch (Exception ex)
        {
            Logger.Log($"Scraper: GetUsageAsync error: {ex.Message}");
            return null;
        }
    }

    private async Task EnsureOnClaudeAsync()
    {
#if WINDOWS
        if (_wv.Source?.StartsWith("https://claude.ai") == true) return;
        Logger.Log("Scraper: navigating to claude.ai for API context");
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? h = null;
        h = (_, _) => { _wv.NavigationCompleted -= h; tcs.TrySetResult(true); };
        _wv.NavigationCompleted += h;
        _wv.Navigate("https://claude.ai");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Logger.Log($"Scraper: now on {_wv.Source}");
#elif MACOS
        var currentUrl = _wv.Url?.AbsoluteString ?? "";
        if (currentUrl.StartsWith("https://claude.ai")) return;
        Logger.Log("Scraper: navigating to claude.ai for API context");
        var tcs = new TaskCompletionSource<bool>();
        Action<string?>? h = null;
        h = (_) => { _navDelegate.NavigationFinished -= h; tcs.TrySetResult(true); };
        _navDelegate.NavigationFinished += h;
        _wv.LoadRequest(new NSUrlRequest(new NSUrl("https://claude.ai")));
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Logger.Log($"Scraper: now on {_wv.Url?.AbsoluteString}");
#endif
    }

    private async Task<string?> BrowserFetchAsync(string url)
    {
        try
        {
            var tcs = new TaskCompletionSource<string?>();

#if WINDOWS
            EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                _wv.WebMessageReceived -= handler;
                try { tcs.TrySetResult(JsonSerializer.Deserialize<string>(args.WebMessageAsJson)); }
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

#elif MACOS
            Action<string>? handler = null;
            handler = (body) =>
            {
                _msgHandler.MessageReceived -= handler;
                tcs.TrySetResult(body);
            };
            _msgHandler.MessageReceived += handler;

            var safeUrl = url.Replace("'", "\\'");
            var script = $$"""
                fetch('{{safeUrl}}', { credentials: 'include', headers: { Accept: 'application/json' } })
                    .then(r => r.ok
                        ? r.text()
                        : r.text().then(b => { throw new Error(r.status + ':' + b.substring(0, 200)); }))
                    .then(t => window.webkit.messageHandlers.claudecap.postMessage(t))
                    .catch(e => window.webkit.messageHandlers.claudecap.postMessage('__ERR:' + e.message));
                """;
            _wv.EvaluateJavaScript(new NSString(script), (_, _) => { });
#else
            tcs.TrySetResult(null);
#endif

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (result?.StartsWith("__ERR:") == true)
            {
                Logger.Log($"Scraper: BrowserFetch error for {url}: {result}");
                return null;
            }
            Logger.Log($"Scraper: BrowserFetch {url} → ok ({result?.Length ?? 0} chars)");
            return result;
        }
        catch (TimeoutException)
        {
            Logger.Log($"Scraper: BrowserFetch timed out for {url}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"Scraper: BrowserFetchAsync error: {ex.Message}");
            return null;
        }
    }

    // ── Window helpers ────────────────────────────────────────────────────────

    private void ShowLoginWindow()
    {
        Logger.Log("Scraper: showing login window");
#if WINDOWS
        _host.Title         = "Claude Cap — Sign in to claude.ai";
        _host.ShowInTaskbar = true;
        _controller.Bounds    = new System.Drawing.Rectangle(0, 0, (int)_host.Width, (int)_host.Height);
        _controller.IsVisible = true;
        _host.Show();
        _host.Activate();
#elif MACOS
        _macWindow.Title = "Claude Cap — Sign in to claude.ai";
        _macWindow.Center();
        _macWindow.MakeKeyAndOrderFront(null);
#endif
    }

    private void HideWindow()
    {
#if WINDOWS
        _controller.IsVisible = false;
        _host.ShowInTaskbar   = false;
        _host.Hide();
#elif MACOS
        _macWindow.OrderOut(null);
#endif
    }

    // ── Session management ────────────────────────────────────────────────────

    public void ClearSession()
    {
#if WINDOWS
        if (_ready)
        {
            _wv.CookieManager.DeleteAllCookies();
            Logger.Log("Scraper: cookies cleared");
        }
        else
        {
            try
            {
                if (Directory.Exists(DataFolder))
                    Directory.Delete(DataFolder, recursive: true);
                Logger.Log("Scraper: data folder deleted");
            }
            catch (Exception ex) { Logger.Log($"Scraper: clear error: {ex.Message}"); }
        }
#elif MACOS
        if (_ready)
        {
            _wv.Configuration.WebsiteDataStore.HttpCookieStore.DeleteAllCookies(() =>
                Logger.Log("Scraper: cookies cleared"));
        }
        else
        {
            try
            {
                if (Directory.Exists(DataFolder))
                    Directory.Delete(DataFolder, recursive: true);
            }
            catch (Exception ex) { Logger.Log($"Scraper: clear error: {ex.Message}"); }
        }
#endif
    }

    public void Dispose()
    {
#if WINDOWS
        _controller?.Close();
        _host?.Close();
#elif MACOS
        _macWindow?.Close();
#endif
    }
}

// ── macOS helper classes ──────────────────────────────────────────────────────

#if MACOS
sealed class ScriptMessageHandler : NSObject, IWKScriptMessageHandler
{
    public event Action<string>? MessageReceived;

    public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
    {
        var body = message.Body?.ToString();
        if (body != null) MessageReceived?.Invoke(body);
    }
}

sealed class NavigationDelegate : NSObject, IWKNavigationDelegate
{
    public event Action<string?>? NavigationFinished;

    public void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        => NavigationFinished?.Invoke(webView.Url?.AbsoluteString);

    public void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
        => NavigationFinished?.Invoke(webView.Url?.AbsoluteString);

    public void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
        => NavigationFinished?.Invoke(null);
}

sealed class CloseBlocker : NSWindowDelegate
{
    public event Action? UserTriedToClose;

    public override bool WindowShouldClose(NSObject sender)
    {
        UserTriedToClose?.Invoke();
        return false;
    }
}
#endif
