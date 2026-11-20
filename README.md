# NeonV — VPN & Proxy Client for Windows

NeonV is a minimalist, elegant, and user-friendly graphical client for the **Sing-box** core on Windows. It supports both **TUN Mode** (full VPN) and **System Proxy** mode.

<img width="920" height="680" alt="noname" src="https://github.com/user-attachments/assets/cb213b80-333e-4b89-95f4-a0eeef198854" />

## Why NeonV?

Most advanced proxy clients (such as v2rayN) are built with power users in mind. This often results in a cluttered, confusing user interface with hundreds of scrambled settings, endless menus, and no straightforward "Connect" button. 

**NeonV was built to solve this.** 

Our philosophy is simple: **VPN tools should just work.** 
* **No settings maze:** Only the most essential options are visible and structured logically.
* **One-click connection:** A prominent, unmistakable button to instantly enable or disable your connection.
* **Subscriptions that just work:** Manage remote subscriptions with auto-updates, traffic quotas, and expiry tracking without configuration headaches.
* **Beautiful UI:** A clean, modern Material 3 interface with dark, light, and system themes, instead of outdated and confusing Windows Forms.

## Key Features

* **Dual Operation Modes:** TUN (routes all system traffic through a virtual adapter) and Mixed/Proxy (local SOCKS5/HTTP port).
* **Full Subscription Support:** Manage provider subscriptions with startup auto-updates, traffic & expiry tracking, and batch ping checks. Supports Base64/URI lists, Sing-box JSON, and Xray JSON formats.
* **Protocols Supported:** VLESS, VMess, Trojan, Shadowsocks, WireGuard, Hysteria/Hysteria2, TUIC.
* **Latency & Connection Testing:** Test real-time ICMP/TCP ping across all nodes, plus real HTTP connectivity testing.
* **Country Flags & Color Emojis:** Automatic node country detection with a custom Direct2D/DirectWrite color emoji rendering engine.
* **Custom Routing & Rules:** Bypass or force specific domains and applications (e.g., routing only chosen programs through the proxy, or one-click bypass for Russian domains).
* **Live Reconnect:** Seamless automatic reconnection when switching profiles or modifying core network settings on the fly.
* **System Tray Support:** Run in the background with a convenient quick-control tray menu.
* **Multilingual:** Built-in localization support for English, Russian, Ukrainian, German, Chinese, Japanese, and Persian.

## Requirements

* **`sing-box.exe`** and **`wintun.dll`** are required for core functionality, but **they are already included in the release package**, so you do not need to download anything. The client is fully self-contained and does not require any pre-installed .NET runtimes.

## Installation & Usage

1. Go to the **Releases** tab on the right side of this repository and download the latest zip archive.
2. Extract the archive to a folder of your choice.
3. Run `NeonV.exe` (requires Administrator privileges to start).

## Building from Source

To build the client manually:
1. Clone the repository:
   ```bash
   git clone https://github.com/Hayashi-xz/NeonV.git
   
2. Open the solution file NeonV.sln in Visual Studio.
3. Restore the NuGet packages.
4. Select the Release build configuration and compile the project.
