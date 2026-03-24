# Claude Cap

**Claude Cap** is a Windows system tray app that shows your Claude.ai plan usage directly in your **Claude Code status line** — so you never lose track of your monthly budget while you work.

---

## Claude Code Status Line

The main reason to install Claude Cap is this: a live plan budget bar that appears at the bottom of every Claude Code session.

<p align="center">
  <img src="docs/screenshot_statusline.png" alt="Claude Cap status line in Claude Code" />
</p>

On first run, Claude Cap writes a script and registers it in `~/.claude/settings.json` — no manual setup. From that point on, every Claude Code session shows your current spend, updated in the background every few minutes.

---

## Usage

Claude Cap lives in your **system tray**.

Icon turns **orange** at 90% usage, **dark red** at 100%. Icon turns **red** on any data fetch error.

### Usage Trend Graph

See where your budget is headed — spend history and projected month-end estimate.

![Usage Trend Graph](docs/screenshot_graph.png)

---

## Features

- **Claude Code status line** — live plan budget bar, auto-installed on first run
- **Tray icon** — icon color changes at 90% and 100% usage thresholds
- **Smart notifications** — one-time balloon alerts at 80%, 90%, and 100%
- **Usage trend graph** — 62 days of history with month-end projection
- **Authentication** — a browser window appears on first launch to log in to claude.ai; after that, runs silently in the background
- **Configurable refresh** — right-click to set interval (default: every 5 minutes)
- **Zero setup** — self-installs on first run; ships as a single `.exe`

---

## Requirements

| Variant | Size | Requirement |
|---------|------|-------------|
| **Standalone** | ~109 MB | Windows 10/11 + Edge/WebView2 (ships with Windows 11) |
| **Slim** | ~1.1 MB | Windows 10/11 + [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) |

> **WebView2** is included with Windows 11 and Microsoft Edge. On Windows 10 without Edge, use the standalone build or install the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

---

## How to Install

### Option A — Standalone (recommended, no dependencies)

1. Download `ClaudeCap-standalone.exe` from the [Releases](../../releases) page.
2. Place it anywhere — e.g. `C:\Tools\ClaudeCap\ClaudeCap.exe`.
3. Double-click to run.

On first launch, Claude Cap:
- Opens a browser window to log in to claude.ai (one time only)
- Writes `~/.claude/statusline-command.sh`
- Patches `~/.claude/settings.json` to register the status line

### Option B — Slim (if you already have .NET 9 Desktop Runtime)

1. Download `ClaudeCap-slim.exe` from the [Releases](../../releases) page.
2. Ensure [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) is installed.
3. Run `ClaudeCap-slim.exe`.

### Add to Windows startup (optional)

1. Press `Win + R`, type `shell:startup`, press Enter.
2. Create a shortcut to `ClaudeCap.exe` in that folder.

---

## Files Written

| Path | Contents |
|------|----------|
| `~/.claude/statusline-command.sh` | Claude Code status line script (auto-installed) |
| `~/.claude/usage_data.json` | Latest fetch: `percent`, `used_dollars`, `total_dollars`, `reset` |
| `~/.claude/usage_history.json` | Up to 250 timestamped readings (~62 days × 4/day) |
| `~/.claude/tools/claudecap/config.json` | App config (`RefreshIntervalMinutes`) |

---

## Building from Source

**Prerequisites:** .NET 9 SDK

```bash
git clone https://github.com/zrcds/claude-cap
cd claude-cap

# Standalone (~109 MB, no runtime needed)
dotnet publish ClaudeCap.csproj -c Release -r win-x64 -p:SelfContained=true --output bin/publish-standalone

# Slim (~1.1 MB, requires .NET 9 Desktop Runtime)
dotnet publish ClaudeCap.csproj -c Release -r win-x64 -p:SelfContained=false --output bin/publish-slim
```

---

## License

MIT
