// http_simple.h - Lightweight HTTP client header
#ifndef HTTP_SIMPLE_H
#define HTTP_SIMPLE_H

#include <stddef.h>

/**
 * http_response_t - HTTP response structure
 */
typedef struct http_response {
    int status_code;        // HTTP status code (200, 404, etc.)
    char* body;            // Response body (null-terminated)
    size_t body_size;      // Size of body in bytes
} http_response_t;

/**
 * http_get - Perform HTTP GET request
 * 
 * @param url: Full URL to fetch (http://host:port/path)
 * @param response: Output response structure
 * @return: 0 on success, -1 on error
 * 
 * Example:
 *   http_response_t resp;
 *   if (http_get("http://192.168.1.100:8080/config", &resp) == 0) {
 *       printf("Status: %d\n", resp.status_code);
 *       printf("Body: %s\n", resp.body);
 *       http_response_free(&resp);
 *   }
 */
int http_get(const char* url, http_response_t* response);

/**
 * http_post_file - Upload file via HTTP POST with custom headers
 * 
 * @param url: Upload URL
 * @param api_key: API key for X-API-Key header
 * @param file_path: Path to file to upload
 * @param virtual_path: Virtual path for X-Document-Path header
 * @param response: Output response structure
 * @return: 0 on success, -1 on error
 * 
 * Sends file as raw binary with custom headers:
 *   X-API-Key: <api_key>
 *   X-Document-Path: <virtual_path>
 *   X-Document-Id: <doc_id>
 *   X-Filename: <basename of file_path>
 *   Content-Type: application/octet-stream
 * Example:
 *   http_response_t resp;
 *   if (http_post_file("http://server/upload", "secret-key",
 *                      "/path/to/file.rm", 
 *                      "Shared/Math/Page1",
 *                      "uuid-document-1234",
 *                      &resp) == 0) {
 *       if (resp.status_code == 200) {
 *           printf("Upload successful\n");
 *       }
 *       http_response_free(&resp);
 *   }
 */
int http_post_file(const char* url, const char* api_key,
                   const char* file_path, const char* virtual_path,
                   const char* doc_id, http_response_t* response);

/**
 * http_response_free - Free response structure
 * 
 * @param response: Response to free
 * 
 * Call this after processing response to free allocated memory
 */
void http_response_free(http_response_t* response);

#endif // HTTP_SIMPLE_H