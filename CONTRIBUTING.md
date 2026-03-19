# Contributing to reMarkable OneNote Sync

Thank you for your interest in contributing! This project consists of two separate codebases:
1. `rm-daemon`: The C-based background service for the tablet.
2. `app/rmOneNoteSyncApp`: The Avalonia C# desktop dashboard.

## Local Development Setup

### 1. Building the Desktop App (C#)
Ensure you have the [.NET 9 SDK](https://dotnet.microsoft.com/download) installed.
```bash
cd app/rmOneNoteSyncApp
dotnet build