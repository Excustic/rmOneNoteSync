# reMarkable OneNote Sync 📝🔄

[![GitHub Release](https://img.shields.io/github/v/release/Excustic/rmOneNoteSync?style=flat-square)](https://github.com/Excustic/rmOneNoteSync/releases/latest)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg?style=flat-square)](https://www.gnu.org/licenses/gpl-3.0)

A lightweight, cross-platform desktop application and on-device daemon that seamlessly synchronizes your reMarkable notebooks directly to Microsoft OneNote. 

![App Screenshot](link-to-your-screenshot-here.png)

## ✨ Features
* **Two-Part Architecture:** A lightweight C daemon running quietly on your tablet, and a modern Avalonia C# dashboard for your PC.
* **Universal Compatibility:** Supports reMarkable 1, reMarkable 2, and the reMarkable Paper Pro.
* **Cross-Platform:** Native desktop clients for Windows, macOS, and Linux.
* **Background Sync:** Set it and forget it. Syncs automatically over Wi-Fi or USB.
* **Auto-Updating:** Built-in update checker so you never miss a feature.

## 🚀 Installation

Head over to the [Releases page](https://github.com/Excustic/rmOneNoteSync/releases/latest) to grab the latest version for your operating system.

### Windows
Download and run `rmOneNoteSyncApp-Setup.exe`. 

### Linux
We provide native packages for major distributions, as well as a portable tarball for Arch/custom setups.
* **Debian/Ubuntu:** `sudo apt install ./rmOneNoteSync.deb`
* **Fedora/RedHat:** `sudo dnf install ./rmOneNoteSync.rpm`
* **Arch/Other:** Download the `.tar.gz`, extract it, and run `./install.sh`.

### macOS
Download the macOS portable release, extract the folder, and run the executable.

## 📖 Usage
1. Connect your reMarkable tablet to your computer via USB or ensure it is on the same Wi-Fi network.
2. Open the desktop app and navigate to **Settings**.
3. Follow the prompts to deploy the sync daemon to your tablet.
4. Authenticate with your Microsoft Account to link your OneNote notebooks.

## 🧪 Compatibility Matrix

We strive to support a wide range of hardware and operating systems. Below is the current testing status of various combinations:

| Desktop OS | App Version | reMarkable Device | rM OS Version | Status |
| :--- | :--- | :--- | :--- | :--- |
| Windows 11 (x64) | v0.6.0 | reMarkable 2 | v3.15.x | ✅ Fully Tested |
| Arch Linux | v0.6.0 | rM Paper Pro | v3.15.x | ✅ Fully Tested |
| Ubuntu 24.04 | v0.6.0 | reMarkable 2 | v3.15.x | ⚠️ Beta |
| macOS 14 (Apple Silicon) | v0.6.0 | reMarkable 1 | v3.2.x | ❌ Unverified |

*(If you successfully run this app on an "Unverified" or unlisted combination, please let us know in the Issues tab so we can update this chart!)*

---

## 🛠️ For Developers
Want to compile the app from source or contribute to the project? Please read our [Contributing Guidelines](CONTRIBUTING.md) for instructions on setting up your local environment and configuring the Azure Microsoft Graph API credentials.