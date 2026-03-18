#!/usr/bin/env python3
"""
test_server_fixed.py - Fixed HTTP server for testing reMarkable sync client

This version properly handles:
- Binary file uploads
- UTF-8 encoded paths (including Hebrew)
- Windows-safe filenames
"""

import http.server
import socketserver
import json
import os
import sys
import re
from datetime import datetime
from urllib.parse import urlparse, parse_qs

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
UPLOAD_DIR = "uploads"

# Ensure upload directory exists
os.makedirs(UPLOAD_DIR, exist_ok=True)

def sanitize_filename(filename):
    """
    Sanitize filename for Windows/Linux compatibility
    """
    # Replace invalid characters with underscore
    # Windows invalid chars: < > : " | ? * \ /
    invalid_chars = '<>:"|?*\\/\r\n'
    for char in invalid_chars:
        filename = filename.replace(char, '_')
    
    # Remove trailing dots and spaces (Windows issue)
    filename = filename.rstrip('. ')
    
    # Limit length
    if len(filename) > 200:
        filename = filename[:200]
    
    return filename

class SyncServerHandler(http.server.BaseHTTPRequestHandler):
    """Handler for sync requests"""
    
    def do_GET(self):
        """Handle GET requests"""
        parsed = urlparse(self.path)
        
        if parsed.path == "/config":
            # Return configuration
            query = parse_qs(parsed.query)
            device_id = query.get('device_id', ['unknown'])[0]
            
            config = {
                "server_url": f"http://{self.server.server_address[0]}:{PORT}/upload",
                "api_key": "test-api-key",
                "upload_interval_seconds": 30,
                "max_retries": 5,
                "retry_delay_seconds": 20,
                "timeout_seconds": 10,
                "device_id": device_id,
                "timestamp": datetime.now().isoformat()
            }
            
            self.send_response(200)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.end_headers()
            self.wfile.write(json.dumps(config, indent=2, ensure_ascii=False).encode('utf-8'))
            
            print(f"[CONFIG] Sent config to device {device_id}")
            
        else:
            self.send_error(404, "Not Found")
    
    def do_POST(self):
        """Handle POST requests"""
        if self.path == "/upload":
            try:
                # Extract headers (handle UTF-8)
                api_key = self.headers.get('X-API-Key', '')
                doc_path = self.headers.get('X-Document-Path', 'Unknown')
                filename = self.headers.get('X-Filename', 'unknown.rm')
                content_length = int(self.headers.get('Content-Length', 0))
                
                # Validate API key
                if api_key != "test-api-key":
                    self.send_error(401, "Invalid API Key")
                    print(f"[UPLOAD] Rejected - invalid API key: {api_key}")
                    return
                
                # Validate content length
                if content_length <= 0:
                    self.send_error(400, "No content")
                    print(f"[UPLOAD] Rejected - no content")
                    return
                
                # Read file data
                file_data = self.rfile.read(content_length)
                
                # Verify we got all the data
                if len(file_data) != content_length:
                    self.send_error(400, f"Incomplete upload: got {len(file_data)} bytes, expected {content_length}")
                    print(f"[UPLOAD] Rejected - incomplete: {len(file_data)}/{content_length} bytes")
                    return
                
                # Create safe filename
                timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
                
                # Sanitize path for filename
                safe_path = sanitize_filename(doc_path.replace('/', '_'))
                safe_filename = sanitize_filename(filename)
                
                # Build output filename
                output_filename = f"{timestamp}_{safe_path}_{safe_filename}"
                output_path = os.path.join(UPLOAD_DIR, output_filename)
                
                # Save file
                with open(output_path, 'wb') as f:
                    f.write(file_data)
                
                # Verify file was written correctly
                actual_size = os.path.getsize(output_path)
                
                # Log the upload
                print(f"[UPLOAD] Success:")
                print(f"  Path: {doc_path}")
                print(f"  Filename: {filename}")
                print(f"  Expected size: {content_length} bytes")
                print(f"  Received size: {len(file_data)} bytes")
                print(f"  Saved size: {actual_size} bytes")
                print(f"  Saved as: {output_path}")
                
                if actual_size != len(file_data):
                    print(f"  WARNING: File size mismatch!")
                
                # Send response
                response = {
                    "status": "success",
                    "message": "File uploaded successfully",
                    "path": doc_path,
                    "filename": filename,
                    "size": len(file_data),
                    "saved_as": output_filename,
                    "timestamp": datetime.now().isoformat()
                }
                
                response_json = json.dumps(response, ensure_ascii=False)
                response_bytes = response_json.encode('utf-8')
                
                self.send_response(200)
                self.send_header('Content-Type', 'application/json; charset=utf-8')
                self.send_header('Content-Length', str(len(response_bytes)))
                self.end_headers()
                self.wfile.write(response_bytes)
                
            except Exception as e:
                print(f"[UPLOAD] Error: {e}")
                import traceback
                traceback.print_exc()
                self.send_error(500, f"Server error: {str(e)}")
            
        else:
            self.send_error(404, "Not Found")
    
    def log_message(self, format, *args):
        """Override to reduce verbosity"""
        # Only log errors
        if args[1][0] != '2':  # Not a 2xx status code
            super().log_message(format, *args)

class ReuseAddrTCPServer(socketserver.TCPServer):
    """TCP server that allows address reuse"""
    allow_reuse_address = True

if __name__ == "__main__":
    print(f"=== reMarkable Sync Test Server (Fixed) ===")
    print(f"Listening on port {PORT}")
    print(f"Upload directory: {UPLOAD_DIR}")
    print(f"")
    print(f"Endpoints:")
    print(f"  GET  http://localhost:{PORT}/config?device_id=XXX")
    print(f"  POST http://localhost:{PORT}/upload")
    print(f"")
    print(f"Features:")
    print(f"  - Handles binary uploads correctly")
    print(f"  - Supports UTF-8 paths (Hebrew, etc.)")
    print(f"  - Windows-safe filenames")
    print(f"  - Detailed logging")
    print(f"")
    print(f"Press Ctrl+C to stop")
    print(f"=" * 40)
    
    with ReuseAddrTCPServer(("", PORT), SyncServerHandler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nShutting down...")
            sys.exit(0)