# reMarkable OneNote Sync - Deployment Guide

## Overview
This guide will walk you through building and deploying the sync service on your reMarkable Paper Pro.

## Prerequisites
- Ubuntu VM with cross-compilation toolchain installed
- SSH access to your reMarkable device (IP: 10.11.99.1)
- Python 3 on your PC for running the test server

## File Structure
You should have these files in your project directory:
```
RemarkableSyncClient/
├── cache_io.c           # Cache I/O implementation
├── cache_io.h           # Cache I/O header
├── metadata_parser.c    # Path reconstruction
├── metadata_parser.h    # Metadata parser header
├── http_simple.c        # HTTP client implementation
├── http_simple.h        # HTTP client header
├── watcher_updated.c    # Updated watcher with SYNC_PENDING
├── httpclient.c         # Production HTTP client
├── cache_debug.c        # Cache debug tool
├── Makefile            # Build system
├── watcher.conf        # Watcher configuration
├── httpclient.conf     # HTTP client configuration
└── test_server.py      # Test server for development
```

## Step 1: Prepare Build Environment

### 1.1 Copy all files to your Ubuntu VM
Transfer all the source files to a directory on your Ubuntu VM, for example:
```bash
mkdir -p ~/RemarkableSyncClient
cd ~/RemarkableSyncClient
# Copy all files here
```

### 1.2 Source the cross-compilation environment
```bash
source /opt/codex/ferrari/4.3.98/environment-setup-cortexa53-crypto-remarkable-linux
```

### 1.3 Verify environment
```bash
echo $CC
# Should output: aarch64-remarkable-linux-gcc or similar
```

## Step 2: Build the Binaries

### 2.1 Build all components
```bash
cd ~/RemarkableSyncClient
make clean
make all
```

You should see:
```
Built: watcher
Built: httpclient
Built: cache_debug
====================================
Build complete!
Binaries created:
  - watcher
  - httpclient
  - cache_debug
====================================
```

### 2.2 Verify the binaries
```bash
file watcher httpclient cache_debug
# Should show: ELF 64-bit LSB executable, ARM aarch64
```

## Step 3: Prepare reMarkable Device

### 3.1 Create directory structure on reMarkable
SSH into your reMarkable:
```bash
ssh root@10.11.99.1
```

Create the necessary directories:
```bash
mkdir -p /home/root/onenote-sync/cache
mkdir -p /home/root/onenote-sync/logs
mkdir -p /home/root/onenote-sync/bin
```

Exit SSH:
```bash
exit
```

## Step 4: Deploy Files to reMarkable

### 4.1 Copy binaries (from your PC/VM)
```bash
scp watcher httpclient cache_debug root@10.11.99.1:/home/root/onenote-sync/bin/
```

### 4.2 Copy configuration files
First, edit the configuration files if needed, then:
```bash
scp watcher.conf httpclient.conf root@10.11.99.1:/home/root/onenote-sync/
```

### 4.3 Set permissions
SSH into reMarkable and set executable permissions:
```bash
ssh root@10.11.99.1
chmod +x /home/root/onenote-sync/bin/*
exit
```

## Step 5: Test the Installation

### 5.1 Start the test server on your PC
On your development PC (not the reMarkable):
```bash
python3 test_server.py 8080
```

### 5.2 Update configuration on reMarkable
SSH into reMarkable and edit the config:
```bash
ssh root@10.11.99.1
vi /home/root/onenote-sync/httpclient.conf
```

Update `SERVER_URL` to point to your PC's IP address:
```ini
SERVER_URL=http://YOUR_PC_IP:8080/upload
```

### 5.3 Test the cache debug tool
```bash
/home/root/onenote-sync/bin/cache_debug /home/root/onenote-sync/cache/.sync_cache
```

### 5.4 Test the watcher manually
```bash
/home/root/onenote-sync/bin/watcher
# Check the log
tail -f /home/root/onenote-sync/logs/watcher.log
```

Press Ctrl+C to stop.

### 5.5 Test the HTTP client manually
```bash
/home/root/onenote-sync/bin/httpclient
# Check the log
tail -f /home/root/onenote-sync/logs/httpclient.log
```

## Step 6: Set Up Automatic Startup

### 6.1 Create systemd service for watcher
Create `/etc/systemd/system/remarkable-sync-watcher.service`:
```ini
[Unit]
Description=reMarkable Sync Watcher
After=home.mount

[Service]
Type=simple
ExecStart=/home/root/onenote-sync/bin/watcher
Restart=on-failure
RestartSec=10
User=root

[Install]
WantedBy=multi-user.target
```

### 6.2 Create systemd service for HTTP client
Create `/etc/systemd/system/remarkable-sync-httpclient.service`:
```ini
[Unit]
Description=reMarkable Sync HTTP Client
After=home.mount network.target
Wants=remarkable-sync-watcher.service

[Service]
Type=simple
ExecStart=/home/root/onenote-sync/bin/httpclient
Restart=on-failure
RestartSec=30
User=root

[Install]
WantedBy=multi-user.target
```

### 6.3 Enable and start services
```bash
systemctl daemon-reload
systemctl enable remarkable-sync-watcher.service
systemctl enable remarkable-sync-httpclient.service
systemctl start remarkable-sync-watcher.service
systemctl start remarkable-sync-httpclient.service
```

### 6.4 Check service status
```bash
systemctl status remarkable-sync-watcher.service
systemctl status remarkable-sync-httpclient.service
```

## Step 7: Monitor and Troubleshoot

### View logs
```bash
# Watch watcher logs
tail -f /home/root/onenote-sync/logs/watcher.log

# Watch HTTP client logs
tail -f /home/root/onenote-sync/logs/httpclient.log

# View systemd logs
journalctl -u remarkable-sync-watcher.service -f
journalctl -u remarkable-sync-httpclient.service -f
```

### Debug cache contents
```bash
/home/root/onenote-sync/bin/cache_debug -v /home/root/onenote-sync/cache/.sync_cache
```

### Stop services
```bash
systemctl stop remarkable-sync-watcher.service
systemctl stop remarkable-sync-httpclient.service
```

## Configuration Reference

### watcher.conf
- `WATCH_PATH`: Directory to monitor (default: xochitl directory)
- `LOG_PATH`: Log file location
- `CACHE_PATH`: Shared cache file

### httpclient.conf
- `SERVER_URL`: Upload server endpoint
- `API_KEY`: Authentication key
- `SHARED_PATH`: Filter for which paths to sync ("*" for all)
- `UPLOAD_INTERVAL`: Seconds between upload attempts
- `MAX_RETRIES`: Maximum retry attempts per file
- `RETRY_DELAY`: Seconds between retries
- `TIMEOUT`: HTTP timeout in seconds

## Expected Behavior

1. **Watcher** monitors the xochitl directory for changes
2. When a document is modified, it scans all pages and marks new/changed ones as SYNC_PENDING
3. **HTTP Client** periodically checks for SYNC_PENDING pages
4. For each pending page, it:
   - Reconstructs the virtual path from metadata
   - Checks if the path matches the SHARED_PATH filter
   - Uploads the .rm file with path metadata
   - Updates status to SYNC_UPLOADED or SYNC_FAILED

## Troubleshooting

### Issue: Services won't start
- Check logs: `journalctl -xe`
- Verify binaries are executable: `ls -la /home/root/onenote-sync/bin/`
- Check paths in config files exist

### Issue: Files not uploading
- Check `SHARED_PATH` filter in httpclient.conf
- Verify server is reachable: `curl http://YOUR_SERVER/`
- Check cache status: `cache_debug` tool
- Review httpclient.log for errors

### Issue: Cache corruption
- Stop both services
- Delete cache: `rm /home/root/onenote-sync/cache/.sync_cache`
- Restart services (will rebuild cache)

## Next Steps

1. Replace test server with production OneNote sync server
2. Update `SERVER_URL` and `API_KEY` in httpclient.conf
3. Configure `SHARED_PATH` to limit which documents sync
4. Monitor logs to ensure stable operation

## Important Notes

- The cache is binary format for efficiency
- Both services share the same cache file
- Logs are not rotated automatically (consider adding logrotate)
- Services will restart automatically on failure
- The system is designed to be resilient to network interruptions