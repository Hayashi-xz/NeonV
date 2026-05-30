# NeonV — VPN & Proxy Client for Windows

NeonV is a minimalist, elegant, and user-friendly graphical client for the **Sing-box** core on Windows. It supports both **TUN Mode** (full VPN) and **System Proxy** mode.

## Why NeonV?

Most advanced proxy clients (such as v2rayN) are built with power users in mind. This often results in a cluttered, confusing user interface with hundreds of scrambled settings, endless menus, and no straightforward "Connect" button. 

**NeonV was built to solve this.** 

Our philosophy is simple: **VPN tools should just work.** 
* **No settings maze:** Only the most essential options are visible and structured logically.
* **One-click connection:** A prominent, unmistakable button to instantly enable or disable your connection.
* **Beautiful UI:** A clean, modern Material 3 interface with dark/light themes, instead of outdated and confusing Windows Forms.

## Key Features

* **Dual Operation Modes:** TUN (routes all system traffic through a virtual adapter) and Mixed/Proxy (local SOCKS5/HTTP port).
* **Protocols Supported:** VLESS, VMess, Trojan, ShadowSocks, Wireguard, Hysteria/Hysteria2, TUIC.
* **One-Click Latency Check:** Easily test real-time ping to all your profiles.
* **Custom Routing & Rules:** Bypass or force specific domains and applications (e.g., routing only chosen programs through the proxy).
* **System Tray Support:** Run in the background with a convenient quick-control tray menu.
* **Multilingual:** Built-in localization support for English, Russian, Ukrainian, German, Chinese, Japanese, and Persian.

## Requirements

* **.NET Desktop Runtime** installed on your system.
* **`sing-box.exe`** and **`wintun.dll`** are required for core functionality, but **they are already included in the release package**, so you do not need to download them separately.

## Installation & Usage

1. Go to the **Releases** tab on the right side of this repository and download the latest zip archive.
2. Extract the archive to a folder of your choice.
3. Run `NeonV.exe` (Administrator rights may be required to initialize the virtual TUN adapter).

## Building from Source

To build the client manually:
1. Clone the repository:
   ```bash
   git clone https://github.com/Hayashi-xz/NeonV.git

2.  Open the solution file NeonV.sln in Visual Studio.
3.  Restore the NuGet packages.
4.  Select the Release build configuration and compile the project.
