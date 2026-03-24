using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;

namespace ClaudeCap;

/// <summary>
/// Uses WebView2 for both authentication and all API calls.
/// HttpClient is NOT used — Cloudflare blocks it regardless of cookies.
/// Instead, fetch() is executed inside the real Chromium engine via
/// ExecuteScriptAsync + postMessage, which has the correct browser
/// fingerprint and cf_clearance cookie already set.
/// </summary>
sealed class ClaudeWebScraper : IDisposable
{
    public record UsageResult(int Percent, int UsedCredits, int TotalCredits, string? ResetDate);

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tools", "claudecap", "webview2");

    private Form    _host  = null!;
    private WebView2 _wv   = null!;
    private bool    _ready;

    public static readonly ClaudeWebScraper Instance = new();
    private ClaudeWebScraper() { }


    // ── Init ─────────────────────────────────────────────────────────────────

    public async Task EnsureInitAsync()
    {
        if (_ready) return;
        Logger.Log("WebView2: initializing");

        _host = new Form
        {
            Text            = "Claude Cap",
            Size            = new System.Drawing.Size(900, 680),
            StartPosition   = FormStartPosition.Manual,
            Location        = new System.Drawing.Point(-32000, -32000),
            ShowInTaskbar   = false,
            FormBorderStyle = FormBorderStyle.Sizable,
        };
        _wv = new WebView2 { Dock = DockStyle.Fill };
        _host.Controls.Add(_wv);

        Directory.CreateDirectory(DataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, DataFolder);
        _host.Show();
        await _wv.EnsureCoreWebView2Async(env);
        _host.Hide();

        Logger.Log("WebView2: ready");
        _ready = true;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<UsageResult?> FetchAsync()
    {
        await EnsureInitAsync();
        Logger.Log("WebView2: FetchAsync");

        // 1. Try with existing session
        if (await GetSessionKeyAsync() != null)
        {
            var result = await GetUsageAsync();
            if (result != null) return result;
            Logger.Log("WebView2: session present but API failed — re-authenticating");
        }

        // 2. Need login
        var key = await LoginAsync();
        if (key == null) return null;

        return await GetUsageAsync();
    }

    // ── Session check ─────────────────────────────────────────────────────────

    private async Task<string?> GetSessionKeyAsync()
    {
        try
        {
            var cookies = await _wv.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
            var key = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
            Logger.Log(key != null
                ? "WebView2: sessionKey found in cookie store"
                : "WebView2: no sessionKey in cookie store");
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

        void Cleanup()
        {
            _wv.CoreWebView2.SourceChanged -= sourceHandler;
        }

        sourceHandler = (_, _) =>
        {
            var url = _wv.CoreWebView2.Source ?? "";
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

        _wv.CoreWebView2.SourceChanged += sourceHandler;
        ShowLoginWindow();
        _wv.CoreWebView2.Navigate("https://claude.ai/login");

        _host.FormClosing += OnClose;
        void OnClose(object? s, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Logger.Log("WebView2: login window closed by user");
            Cleanup();
            HideWindow();
            _host.FormClosing -= OnClose;
            tcs.TrySetResult(false);
        }

        var ok = await tcs.Task;
        _host.FormClosing -= OnClose;

        if (!ok) { Logger.Log("WebView2: login cancelled"); return null; }

        Logger.Log("WebView2: login completed");
        return await GetSessionKeyAsync();
    }

    // ── API calls via in-browser fetch ────────────────────────────────────────

    private async Task<UsageResult?> GetUsageAsync()
    {
        try
        {
            // Must be on a claude.ai page so fetch() runs in the correct origin
            await EnsureOnClaudeAsync();

            // Step 1: get account + org UUIDs from /api/account
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

            // Step 2: get usage
            var qs       = accountUuid != null ? $"?account_uuid={accountUuid}" : "";
            var usageUrl = $"https://claude.ai/api/organizations/{orgUuid}/overage_spend_limit{qs}";
            var usageJson = await BrowserFetchAsync(usageUrl);
            if (usageJson == null) return null;
            Logger.Log($"WebView2: usage: {usageJson[..Math.Min(300, usageJson.Length)]}");

            using var usageDoc = JsonDocument.Parse(usageJson);
            var root  = usageDoc.RootElement;
            var used  = root.GetProperty("used_credits").GetInt32();
            var total = root.GetProperty("monthly_credit_limit").GetInt32();
            if (total <= 0) return null;

            // Try API fields first; fall back to first day of next calendar month
            // (Anthropic bills on the 1st, so the page computes it the same way)
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
            if (resetDate == null)
            {
                var now   = DateTime.Now;
                var first = new DateTime(now.Year, now.Month, 1).AddMonths(1);
                resetDate = first.ToString("MMM d");
            }
            Logger.Log($"WebView2: resetDate={resetDate}");

            return new UsageResult((int)Math.Round((double)used / total * 100), used, total, resetDate);
        }
        catch (Exception ex)
        {
            Logger.Log($"WebView2: GetUsageAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Navigates silently to claude.ai if the current page is not already there.
    /// Required so that fetch() runs in the correct origin context (bypasses Cloudflare).
    /// </summary>
    private async Task EnsureOnClaudeAsync()
    {
        if (_wv.CoreWebView2.Source?.StartsWith("https://claude.ai") == true)
            return;

        Logger.Log("WebView2: navigating to claude.ai for API context");
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? h = null;
        h = (_, _) => { _wv.CoreWebView2.NavigationCompleted -= h; tcs.TrySetResult(true); };
        _wv.CoreWebView2.NavigationCompleted += h;
        _wv.CoreWebView2.Navigate("https://claude.ai");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Logger.Log($"WebView2: now on {_wv.CoreWebView2.Source}");
    }

    /// <summary>
    /// Runs a GET fetch() inside the WebView2 Chromium engine and returns the response body.
    /// Uses postMessage to bridge the async JS result back to C#.
    /// This bypasses Cloudflare because the request originates from a real browser engine.
    /// </summary>
    private async Task<string?> BrowserFetchAsync(string url)
    {
        try
        {
            var tcs = new TaskCompletionSource<string?>();

            EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                _wv.CoreWebView2.WebMessageReceived -= handler;
                try
                {
                    // ExecuteScriptAsync / postMessage wraps strings as JSON
                    var raw = JsonSerializer.Deserialize<string>(args.WebMessageAsJson);
                    tcs.TrySetResult(raw);
                }
                catch { tcs.TrySetResult(null); }
            };
            _wv.CoreWebView2.WebMessageReceived += handler;

            // $$""" — JS braces are literal; only {{safeUrl}} is C# interpolation
            var safeUrl = url.Replace("'", "\\'");
            await _wv.CoreWebView2.ExecuteScriptAsync($$"""
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
        _host.Text          = "Claude Cap — Sign in to claude.ai";
        _host.ShowInTaskbar = true;
        var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        _host.Location = new System.Drawing.Point(
            wa.X + (wa.Width  - _host.Width)  / 2,
            wa.Y + (wa.Height - _host.Height) / 2);
        _host.Show();
        _host.BringToFront();
        _host.Activate();
    }

    private void HideWindow()
    {
        _host.ShowInTaskbar = false;
        _host.Hide();
    }

    // ── Session management ────────────────────────────────────────────────────

    public void ClearSession()
    {
        if (_ready)
        {
            _wv.CoreWebView2.CookieManager.DeleteAllCookies();
            Logger.Log("WebView2: session cookies cleared");
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
        _wv?.Dispose();
        _host?.Dispose();
    }
}
