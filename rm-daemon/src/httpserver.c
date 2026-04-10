#include "httpserver.h"
#include "metadata_parser.h"
#include <arpa/inet.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/file.h>
#include <unistd.h>
#include <dirent.h>
#include <stdarg.h>
#include <time.h>
#include "version.h"

#ifndef PATH_MAX
#define PATH_MAX 4096
#endif

#define CONFIG_PATH "/home/root/onenote-sync/daemon.conf"
#define ENDPOINTS_PATH "/home/root/onenote-sync/endpoints.conf"
#define WHITELIST_PATH "/home/root/onenote-sync/whitelist.dat"
#define MAX_WHITELIST_DOCS 512
#define MAX_ENDPOINTS 10
#define DEFAULT_LOG_PATH "/home/root/onenote-sync/logs/httpserver.log"

static CacheHandle *g_cache = NULL;
static int *g_w_count = NULL;
static char (*g_w_list)[UUID_STR_LEN + 1] = NULL;
static volatile bool g_running = false;
static pthread_t g_thread;
static int g_server_fd = -1;

/**
 * log_msg - Write timestamped log message
 */
static void log_msg(const char *fmt, ...) {
  FILE *f = fopen(DEFAULT_LOG_PATH, "a");
  if (!f)
    return;

  time_t now = time(NULL);
  struct tm tm;
  localtime_r(&now, &tm);
  char timestr[32];
  strftime(timestr, sizeof(timestr), "[%Y-%m-%d %H:%M:%S]", &tm);

  fprintf(f, "%s ", timestr);

  va_list ap;
  va_start(ap, fmt);
  vfprintf(f, fmt, ap);
  va_end(ap);

  fprintf(f, "\n");
  fclose(f);
}

void httpserver_init(CacheHandle *active_cache, int *w_count,
                     char (*w_list)[UUID_STR_LEN + 1]) {
  g_cache = active_cache;
  g_w_count = w_count;
  g_w_list = w_list;
}

// ---------- Utility helpers ----------

static void send_http_response(int client_fd, int status,
                               const char *content_type, const char *body) {
  char header[512];
  snprintf(header, sizeof(header),
           "HTTP/1.1 %d OK\r\n"
           "Content-Type: %s\r\n"
           "Content-Length: %zu\r\n"
           "Connection: close\r\n"
           "Access-Control-Allow-Origin: *\r\n\r\n",
           status, content_type, body ? strlen(body) : 0);
  send(client_fd, header, strlen(header), 0);
  if (body) {
    send(client_fd, body, strlen(body), 0);
  }
}

static void extract_json_str(const char* json, const char* key, char* out, size_t out_size) {
    if (!out || out_size == 0) return;
    out[0] = '\0';
    char search_key[128];
    snprintf(search_key, sizeof(search_key), "\"%s\"", key);
    const char* pos = strstr(json, search_key);
    if (!pos) return;
    const char* colon = strchr(pos, ':');
    if (!colon) return;
    const char* q1 = strchr(colon, '"');
    if (!q1) return;
    const char* q2 = strchr(q1 + 1, '"');
    if (!q2) return;
    size_t len = q2 - (q1 + 1);
    if (len >= out_size) len = out_size - 1;
    strncpy(out, q1 + 1, len);
    out[len] = '\0';
}

/**
 * read_file_locked - Read entire file contents with shared lock
 * Caller must free() the returned buffer.
 */
static char* read_file_locked(const char *path, size_t *out_len) {
    FILE *f = fopen(path, "r");
    if (!f) return NULL;

    int fd = fileno(f);
    flock(fd, LOCK_SH);

    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);

    if (size <= 0 || size > 1024 * 1024) {
        flock(fd, LOCK_UN);
        fclose(f);
        return NULL;
    }

    char *buf = malloc(size + 1);
    if (!buf) {
        flock(fd, LOCK_UN);
        fclose(f);
        return NULL;
    }

    size_t rlen = fread(buf, 1, size, f);
    buf[rlen] = '\0';
    if (out_len) *out_len = rlen;

    flock(fd, LOCK_UN);
    fclose(f);
    return buf;
}

/**
 * write_file_locked - Write content to file with exclusive lock
 * Uses write-to-tmp + rename for atomicity.
 */
static int write_file_locked(const char *path, const char *content) {
    char tmp_path[PATH_MAX];
    snprintf(tmp_path, sizeof(tmp_path), "%s.tmp", path);

    FILE *f = fopen(tmp_path, "w");
    if (!f) return -1;

    int fd = fileno(f);
    flock(fd, LOCK_EX);

    size_t len = strlen(content);
    size_t written = fwrite(content, 1, len, f);
    fflush(f);

    flock(fd, LOCK_UN);
    fclose(f);

    if (written != len) {
        unlink(tmp_path);
        return -1;
    }

    return rename(tmp_path, path);
}

/**
 * extract_body - Extract HTTP body from a request buffer, reading more from
 * socket if needed. Caller must free() the returned buffer.
 */
static char* extract_body(int client_fd, char *req_buffer, int received,
                          int *body_len_out) {
    char *body_start = strstr(req_buffer, "\r\n\r\n");
    if (!body_start) return NULL;
    body_start += 4;

    int content_length = 0;
    char *cl_ptr = strstr(req_buffer, "Content-Length:");
    if (!cl_ptr) cl_ptr = strstr(req_buffer, "content-length:");
    if (cl_ptr) content_length = atoi(cl_ptr + 15);

    if (content_length <= 0 || content_length > 1024 * 1024) return NULL;

    int header_length = body_start - req_buffer;
    int body_received = received - header_length;

    char *full_body = malloc(content_length + 1);
    if (!full_body) return NULL;

    int to_copy = body_received > content_length ? content_length : body_received;
    memcpy(full_body, body_start, to_copy);
    int total = to_copy;

    while (total < content_length) {
        int r = recv(client_fd, full_body + total, content_length - total, 0);
        if (r <= 0) break;
        total += r;
    }
    full_body[total] = '\0';
    if (body_len_out) *body_len_out = total;
    return full_body;
}

// ---------- Whitelist handlers ----------

static void handle_whitelist_get(int client_fd) {
    // Read whitelist.dat from disk for freshness
    size_t file_len = 0;
    char *content = read_file_locked(WHITELIST_PATH, &file_len);

    // Parse whitelist entries and sync folders
    char wl_ids[MAX_WHITELIST_DOCS][37];
    int wl_count = 0;
    char sf_ids[MAX_WHITELIST_DOCS][37];
    int sf_count = 0;

    if (content) {
        char *line_buf = strdup(content);
        char *saveptr = NULL;
        char *line = strtok_r(line_buf, "\n", &saveptr);
        while (line) {
            if (line[0] != '#' && line[0] != '\0' && line[0] != '\r') {
                char *eq = strchr(line, '=');
                if (eq) {
                    *eq = '\0';
                    char *key = line;
                    char *val = eq + 1;
                    while (*val == ' ' || *val == '\t') val++;
                    // Trim CR
                    char *cr = strchr(val, '\r');
                    if (cr) *cr = '\0';

                    if (strncmp(key, "WHITELIST_", 10) == 0 &&
                        strcmp(key, "WHITELIST_COUNT") != 0) {
                        if (wl_count < MAX_WHITELIST_DOCS && strlen(val) == 36) {
                            strncpy(wl_ids[wl_count], val, 36);
                            wl_ids[wl_count][36] = '\0';
                            wl_count++;
                        }
                    } else if (strncmp(key, "SYNC_FOLDER_", 12) == 0 &&
                               strcmp(key, "SYNC_FOLDER_COUNT") != 0) {
                        if (sf_count < MAX_WHITELIST_DOCS && strlen(val) == 36) {
                            strncpy(sf_ids[sf_count], val, 36);
                            sf_ids[sf_count][36] = '\0';
                            sf_count++;
                        }
                    }
                }
            }
            line = strtok_r(NULL, "\n", &saveptr);
        }
        free(line_buf);
        free(content);
    } else {
        // Fallback: read from memory (backwards compat if file doesn't exist yet)
        int count = g_w_count ? *g_w_count : 0;
        for (int i = 0; i < count && i < MAX_WHITELIST_DOCS; i++) {
            strncpy(wl_ids[i], g_w_list[i], 36);
            wl_ids[i][36] = '\0';
        }
        wl_count = count;
    }

    // Build JSON response
    size_t json_alloc = 8192 + (wl_count + sf_count) * 64;
    char *json = malloc(json_alloc);
    if (!json) {
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"memory error\"}");
        return;
    }

    strcpy(json, "{\n  \"whitelist\": [\n");
    for (int i = 0; i < wl_count; i++) {
        char line[128];
        snprintf(line, sizeof(line), "    \"%s\"%s\n", wl_ids[i],
                 (i < wl_count - 1) ? "," : "");
        strcat(json, line);
    }
    strcat(json, "  ],\n  \"sync_folders\": [\n");
    for (int i = 0; i < sf_count; i++) {
        char line[128];
        snprintf(line, sizeof(line), "    \"%s\"%s\n", sf_ids[i],
                 (i < sf_count - 1) ? "," : "");
        strcat(json, line);
    }
    strcat(json, "  ]\n}\n");

    send_http_response(client_fd, 200, "application/json", json);
    free(json);
}

/**
 * handle_whitelist_put - Replace entire whitelist atomically
 * Body: { "whitelist": ["uuid",...], "sync_folders": ["uuid",...] }
 */
static void handle_whitelist_put(int client_fd, char *req_buffer, int received) {
    int body_len = 0;
    char *body = extract_body(client_fd, req_buffer, received, &body_len);
    if (!body) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"missing or invalid body\"}");
        return;
    }

    // Parse whitelist UUIDs from JSON array
    char wl_ids[MAX_WHITELIST_DOCS][37];
    int wl_count = 0;
    char sf_ids[MAX_WHITELIST_DOCS][37];
    int sf_count = 0;

    // Parse "whitelist" array
    char *wl_arr = strstr(body, "\"whitelist\"");
    if (wl_arr) {
        char *arr_start = strchr(wl_arr, '[');
        if (arr_start) {
            char *p = arr_start + 1;
            while (*p && *p != ']') {
                char *q1 = strchr(p, '"');
                if (!q1) break;
                q1++;
                char *q2 = strchr(q1, '"');
                if (!q2) break;
                if (q2 - q1 == 36 && wl_count < MAX_WHITELIST_DOCS) {
                    strncpy(wl_ids[wl_count], q1, 36);
                    wl_ids[wl_count][36] = '\0';
                    wl_count++;
                }
                p = q2 + 1;
            }
        }
    }

    // Parse "sync_folders" array
    char *sf_arr = strstr(body, "\"sync_folders\"");
    if (sf_arr) {
        char *arr_start = strchr(sf_arr, '[');
        if (arr_start) {
            char *p = arr_start + 1;
            while (*p && *p != ']') {
                char *q1 = strchr(p, '"');
                if (!q1) break;
                q1++;
                char *q2 = strchr(q1, '"');
                if (!q2) break;
                if (q2 - q1 == 36 && sf_count < MAX_WHITELIST_DOCS) {
                    strncpy(sf_ids[sf_count], q1, 36);
                    sf_ids[sf_count][36] = '\0';
                    sf_count++;
                }
                p = q2 + 1;
            }
        }
    }

    // Build whitelist.dat content
    size_t buf_size = 256 + (wl_count + sf_count) * 64;
    char *file_content = malloc(buf_size);
    if (!file_content) {
        free(body);
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"out of memory\"}");
        return;
    }

    char *p = file_content;
    p += sprintf(p, "# Sync whitelist — managed by rmOneNoteSyncApp\n\n");
    p += sprintf(p, "# Document Whitelist\n");
    p += sprintf(p, "WHITELIST_COUNT=%d\n", wl_count);
    for (int i = 0; i < wl_count; i++)
        p += sprintf(p, "WHITELIST_%d=%s\n", i, wl_ids[i]);

    p += sprintf(p, "\n# Sync Folders\n");
    p += sprintf(p, "SYNC_FOLDER_COUNT=%d\n", sf_count);
    for (int i = 0; i < sf_count; i++)
        p += sprintf(p, "SYNC_FOLDER_%d=%s\n", i, sf_ids[i]);

    int result = write_file_locked(WHITELIST_PATH, file_content);
    free(file_content);
    free(body);

    if (result == 0) {
        log_msg("PUT /whitelist: Updated whitelist (%d docs, %d folders)", wl_count, sf_count);
        char resp[128];
        snprintf(resp, sizeof(resp),
                 "{\"status\":\"success\",\"whitelist_count\":%d,\"sync_folder_count\":%d}",
                 wl_count, sf_count);
        send_http_response(client_fd, 200, "application/json", resp);
    } else {
        log_msg("PUT /whitelist: Failed to write whitelist.dat");
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"failed to write whitelist\"}");
    }
}

/**
 * handle_whitelist_add - Append a single doc or folder to the whitelist
 * Body: { "id": "uuid", "type": "document"|"folder" }
 */
static void handle_whitelist_add(int client_fd, char *req_buffer, int received) {
    int body_len = 0;
    char *body = extract_body(client_fd, req_buffer, received, &body_len);
    if (!body) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"missing body\"}");
        return;
    }

    char id[64] = "";
    char type[32] = "document";
    extract_json_str(body, "id", id, sizeof(id));
    extract_json_str(body, "type", type, sizeof(type));
    free(body);

    if (strlen(id) != 36) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"invalid id\"}");
        return;
    }

    int is_folder = (strcmp(type, "folder") == 0);

    // Read existing file
    size_t file_len = 0;
    char *content = read_file_locked(WHITELIST_PATH, &file_len);

    // Check if already present
    if (content && strstr(content, id)) {
        free(content);
        send_http_response(client_fd, 200, "application/json",
                           "{\"status\":\"already_exists\"}");
        return;
    }

    // Parse existing counts and entries
    int wl_count = 0, sf_count = 0;
    char wl_ids[MAX_WHITELIST_DOCS][37];
    char sf_ids[MAX_WHITELIST_DOCS][37];

    if (content) {
        char *line_buf = strdup(content);
        char *saveptr = NULL;
        char *line = strtok_r(line_buf, "\n", &saveptr);
        while (line) {
            char *eq = strchr(line, '=');
            if (eq && line[0] != '#') {
                *eq = '\0';
                char *key = line;
                char *val = eq + 1;
                char *cr = strchr(val, '\r');
                if (cr) *cr = '\0';

                if (strncmp(key, "WHITELIST_", 10) == 0 &&
                    strcmp(key, "WHITELIST_COUNT") != 0 &&
                    wl_count < MAX_WHITELIST_DOCS && strlen(val) == 36) {
                    strncpy(wl_ids[wl_count], val, 36);
                    wl_ids[wl_count][36] = '\0';
                    wl_count++;
                } else if (strncmp(key, "SYNC_FOLDER_", 12) == 0 &&
                           strcmp(key, "SYNC_FOLDER_COUNT") != 0 &&
                           sf_count < MAX_WHITELIST_DOCS && strlen(val) == 36) {
                    strncpy(sf_ids[sf_count], val, 36);
                    sf_ids[sf_count][36] = '\0';
                    sf_count++;
                }
            }
            line = strtok_r(NULL, "\n", &saveptr);
        }
        free(line_buf);
        free(content);
    }

    // Add the new entry
    if (is_folder && sf_count < MAX_WHITELIST_DOCS) {
        strncpy(sf_ids[sf_count], id, 36);
        sf_ids[sf_count][36] = '\0';
        sf_count++;
    } else if (!is_folder && wl_count < MAX_WHITELIST_DOCS) {
        strncpy(wl_ids[wl_count], id, 36);
        wl_ids[wl_count][36] = '\0';
        wl_count++;
    }

    // Rebuild file
    size_t buf_size = 256 + (wl_count + sf_count) * 64;
    char *file_content = malloc(buf_size);
    char *wp = file_content;
    wp += sprintf(wp, "# Sync whitelist — managed by rmOneNoteSyncApp\n\n");
    wp += sprintf(wp, "# Document Whitelist\n");
    wp += sprintf(wp, "WHITELIST_COUNT=%d\n", wl_count);
    for (int i = 0; i < wl_count; i++)
        wp += sprintf(wp, "WHITELIST_%d=%s\n", i, wl_ids[i]);
    wp += sprintf(wp, "\n# Sync Folders\n");
    wp += sprintf(wp, "SYNC_FOLDER_COUNT=%d\n", sf_count);
    for (int i = 0; i < sf_count; i++)
        wp += sprintf(wp, "SYNC_FOLDER_%d=%s\n", i, sf_ids[i]);

    int result = write_file_locked(WHITELIST_PATH, file_content);
    free(file_content);

    if (result == 0) {
        send_http_response(client_fd, 200, "application/json",
                           "{\"status\":\"added\"}");
    } else {
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"write failed\"}");
    }
}

/**
 * handle_whitelist_delete - Remove a single doc or folder from the whitelist
 * Body: { "id": "uuid" }
 */
static void handle_whitelist_delete(int client_fd, char *req_buffer, int received) {
    int body_len = 0;
    char *body = extract_body(client_fd, req_buffer, received, &body_len);
    if (!body) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"missing body\"}");
        return;
    }

    char id[64] = "";
    extract_json_str(body, "id", id, sizeof(id));
    free(body);

    if (strlen(id) != 36) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"invalid id\"}");
        return;
    }

    // Read existing
    size_t file_len = 0;
    char *content = read_file_locked(WHITELIST_PATH, &file_len);
    if (!content) {
        send_http_response(client_fd, 404, "application/json",
                           "{\"error\":\"whitelist not found\"}");
        return;
    }

    // Parse, skip matching ID
    int wl_count = 0, sf_count = 0;
    char wl_ids[MAX_WHITELIST_DOCS][37];
    char sf_ids[MAX_WHITELIST_DOCS][37];

    char *line_buf = strdup(content);
    char *saveptr = NULL;
    char *line = strtok_r(line_buf, "\n", &saveptr);
    while (line) {
        char *eq = strchr(line, '=');
        if (eq && line[0] != '#') {
            *eq = '\0';
            char *key = line;
            char *val = eq + 1;
            char *cr = strchr(val, '\r');
            if (cr) *cr = '\0';

            if (strncmp(key, "WHITELIST_", 10) == 0 &&
                strcmp(key, "WHITELIST_COUNT") != 0 &&
                strlen(val) == 36 && strcmp(val, id) != 0) {
                strncpy(wl_ids[wl_count], val, 36);
                wl_ids[wl_count][36] = '\0';
                wl_count++;
            } else if (strncmp(key, "SYNC_FOLDER_", 12) == 0 &&
                       strcmp(key, "SYNC_FOLDER_COUNT") != 0 &&
                       strlen(val) == 36 && strcmp(val, id) != 0) {
                strncpy(sf_ids[sf_count], val, 36);
                sf_ids[sf_count][36] = '\0';
                sf_count++;
            }
        }
        line = strtok_r(NULL, "\n", &saveptr);
    }
    free(line_buf);
    free(content);

    // Rebuild
    size_t buf_size = 256 + (wl_count + sf_count) * 64;
    char *file_content = malloc(buf_size);
    char *wp = file_content;
    wp += sprintf(wp, "# Sync whitelist — managed by rmOneNoteSyncApp\n\n");
    wp += sprintf(wp, "# Document Whitelist\n");
    wp += sprintf(wp, "WHITELIST_COUNT=%d\n", wl_count);
    for (int i = 0; i < wl_count; i++)
        wp += sprintf(wp, "WHITELIST_%d=%s\n", i, wl_ids[i]);
    wp += sprintf(wp, "\n# Sync Folders\n");
    wp += sprintf(wp, "SYNC_FOLDER_COUNT=%d\n", sf_count);
    for (int i = 0; i < sf_count; i++)
        wp += sprintf(wp, "SYNC_FOLDER_%d=%s\n", i, sf_ids[i]);

    int result = write_file_locked(WHITELIST_PATH, file_content);
    free(file_content);

    if (result == 0) {
        send_http_response(client_fd, 200, "application/json",
                           "{\"status\":\"removed\"}");
    } else {
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"write failed\"}");
    }
}

// ---------- Endpoints handlers ----------

static void handle_endpoints_get(int client_fd) {
    size_t file_len = 0;
    char *content = read_file_locked(ENDPOINTS_PATH, &file_len);

    char *json = malloc(8192);
    if (!json) {
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"memory error\"}");
        if (content) free(content);
        return;
    }

    strcpy(json, "{\n  \"endpoints\": [\n");
    int count = 0;

    if (content) {
        // Count lines first
        int total_lines = 0;
        char *tmp = strdup(content);
        char *sv = NULL;
        char *ln = strtok_r(tmp, "\n", &sv);
        while (ln) {
            while (*ln == ' ' || *ln == '\t') ln++;
            if (*ln != '#' && *ln != '\0' && *ln != '\r') total_lines++;
            ln = strtok_r(NULL, "\n", &sv);
        }
        free(tmp);

        // Build JSON
        char *saveptr = NULL;
        char *line = strtok_r(content, "\n", &saveptr);
        while (line) {
            while (*line == ' ' || *line == '\t') line++;
            char *cr = strchr(line, '\r');
            if (cr) *cr = '\0';
            if (*line != '#' && *line != '\0') {
                char entry[512];
                count++;
                snprintf(entry, sizeof(entry), "    \"%s\"%s\n", line,
                         count < total_lines ? "," : "");
                strcat(json, entry);
            }
            line = strtok_r(NULL, "\n", &saveptr);
        }
        free(content);
    }

    strcat(json, "  ]\n}\n");
    send_http_response(client_fd, 200, "application/json", json);
    free(json);
}

/**
 * handle_endpoints_add - Add a server URL if not already present
 * Body: { "url": "http://..." }
 */
static void handle_endpoints_add(int client_fd, char *req_buffer, int received) {
    int body_len = 0;
    char *body = extract_body(client_fd, req_buffer, received, &body_len);
    if (!body) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"missing body\"}");
        return;
    }

    char url[256] = "";
    extract_json_str(body, "url", url, sizeof(url));
    free(body);

    if (strlen(url) < 7) { // minimum "http://x"
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"invalid url\"}");
        return;
    }

    // Normalize: strip trailing slash and /upload addendum
    size_t ulen = strlen(url);
    if (ulen > 0 && url[ulen - 1] == '/') {
        url[--ulen] = '\0';
    }
    if (ulen > 7 && strcmp(url + ulen - 7, "/upload") == 0) {
        url[ulen - 7] = '\0';
    }

    // Read existing
    size_t file_len = 0;
    char *content = read_file_locked(ENDPOINTS_PATH, &file_len);

    // Check for duplicate
    if (content) {
        char *dup_check = strdup(content);
        char *saveptr = NULL;
        char *line = strtok_r(dup_check, "\n", &saveptr);
        while (line) {
            while (*line == ' ' || *line == '\t') line++;
            char *cr = strchr(line, '\r');
            if (cr) *cr = '\0';
            // Normalize existing entry for comparison
            size_t llen = strlen(line);
            if (llen > 0 && line[llen - 1] == '/') line[llen - 1] = '\0';
            if (strcmp(line, url) == 0) {
                free(dup_check);
                free(content);
                send_http_response(client_fd, 200, "application/json",
                                   "{\"status\":\"already_exists\"}");
                return;
            }
            line = strtok_r(NULL, "\n", &saveptr);
        }
        free(dup_check);
    }

    // Append
    size_t new_size = (content ? strlen(content) : 0) + strlen(url) + 16;
    char *new_content = malloc(new_size);
    if (content) {
        // Put new URL at the top (most recently added = highest priority)
        snprintf(new_content, new_size, "%s\n%s", url, content);
        free(content);
    } else {
        snprintf(new_content, new_size, "# Server endpoints\n%s\n", url);
    }

    int result = write_file_locked(ENDPOINTS_PATH, new_content);
    free(new_content);

    if (result == 0) {
        log_msg("POST /endpoints/add: Added endpoint %s", url);
        send_http_response(client_fd, 200, "application/json",
                           "{\"status\":\"added\"}");
    } else {
        log_msg("POST /endpoints/add: Failed to write write endpoints.conf");
        send_http_response(client_fd, 500, "application/json",
                           "{\"error\":\"write failed\"}");
    }
}

/**
 * handle_endpoints_delete - Remove a server URL
 * Body: { "url": "http://..." }
 */
static void handle_endpoints_delete(int client_fd, char *req_buffer, int received) {
    int body_len = 0;
    char *body = extract_body(client_fd, req_buffer, received, &body_len);
    if (!body) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"missing body\"}");
        return;
    }

    char url[256] = "";
    extract_json_str(body, "url", url, sizeof(url));
    free(body);

    if (strlen(url) < 7) {
        send_http_response(client_fd, 400, "application/json",
                           "{\"error\":\"invalid url\"}");
        return;
    }

    // Normalize
    size_t ulen = strlen(url);
    if (ulen > 0 && url[ulen - 1] == '/') url[ulen - 1] = '\0';

    size_t file_len = 0;
    char *content = read_file_locked(ENDPOINTS_PATH, &file_len);
    if (!content) {
        send_http_response(client_fd, 404, "application/json",
                           "{\"error\":\"endpoints file not found\"}");
        return;
    }

    // Rebuild without the matching URL
    char *result_buf = malloc(file_len + 1);
    result_buf[0] = '\0';
    int removed = 0;

    char *saveptr = NULL;
    char *line = strtok_r(content, "\n", &saveptr);
    while (line) {
        char *trimmed = line;
        while (*trimmed == ' ' || *trimmed == '\t') trimmed++;
        char *cr = strchr(trimmed, '\r');
        if (cr) *cr = '\0';

        // Normalize for comparison
        char normalized[256];
        strncpy(normalized, trimmed, sizeof(normalized) - 1);
        normalized[sizeof(normalized) - 1] = '\0';
        size_t nlen = strlen(normalized);
        if (nlen > 0 && normalized[nlen - 1] == '/') normalized[nlen - 1] = '\0';

        if (strcmp(normalized, url) == 0) {
            removed = 1;
        } else {
            strcat(result_buf, trimmed);
            strcat(result_buf, "\n");
        }
        line = strtok_r(NULL, "\n", &saveptr);
    }
    free(content);

    if (removed) {
        int result = write_file_locked(ENDPOINTS_PATH, result_buf);
        free(result_buf);
        if (result == 0) {
            log_msg("DELETE /endpoints: Removed endpoint %s", url);
            send_http_response(client_fd, 200, "application/json",
                               "{\"status\":\"removed\"}");
        } else {
            log_msg("DELETE /endpoints: Failed to write write endpoints.conf");
            send_http_response(client_fd, 500, "application/json",
                               "{\"error\":\"write failed\"}");
        }
    } else {
        log_msg("DELETE /endpoints: Endpoint %s not found", url);
        free(result_buf);
        send_http_response(client_fd, 404, "application/json",
                           "{\"error\":\"url not found\"}");
    }
}

// ---------- Existing handlers (metadata, filetree, sync, config, version) ----------

static void append_document_json(const char *doc_id, const char *title, char **json_out, size_t *json_alloc, size_t *json_len, bool *first_doc) {
    char path[PATH_MAX];
    snprintf(path, sizeof(path), "/home/root/.local/share/remarkable/xochitl/%s.content", doc_id);
    FILE *f = fopen(path, "r");
    char *pages_json = malloc(65536);
    strcpy(pages_json, "[\n");
    bool has_pages = false;

    if (f) {
        fseek(f, 0, SEEK_END);
        long size = ftell(f);
        fseek(f, 0, SEEK_SET);
        if (size > 0 && size < 1024 * 1024) {
            char *cbuf = malloc(size + 1);
            if (cbuf && fread(cbuf, 1, size, f) == (size_t)size) {
                cbuf[size] = '\0';
                char *pages_start = strstr(cbuf, "\"pages\"");
                if (pages_start) {
                    char *array_start = strchr(pages_start, '[');
                    if (array_start) {
                        char *p = array_start + 1;
                        while (*p && *p != ']') {
                            char *id_pos = strstr(p, "\"id\"");
                            if (!id_pos) break;
                            char *colon = strchr(id_pos, ':');
                            if (!colon) break;
                            char *q1 = strchr(colon, '"');
                            if (!q1) break;
                            q1++;
                            char *q2 = strchr(q1, '"');
                            if (!q2) break;
                            
                            int ulen = q2 - q1;
                            if (ulen == 36) {
                                char page_uuid[37];
                                strncpy(page_uuid, q1, 36);
                                page_uuid[36] = '\0';

                                char pline[128];
                                snprintf(pline, sizeof(pline), "    %s\"%s\"", has_pages ? ",\n" : "", page_uuid);
                                strcat(pages_json, pline);
                                has_pages = true;
                            }
                            p = q2 + 1;
                        }
                    }
                }
            }
            if (cbuf) free(cbuf);
        }
        fclose(f);
    }
    strcat(pages_json, "\n  ]");

    char doc_block[65536 + 1024];
    snprintf(doc_block, sizeof(doc_block),
             "%s    {\n"
             "      \"id\": \"%s\",\n"
             "      \"name\": \"%s\",\n"
             "      \"pages\": %s\n"
             "    }", (*first_doc) ? "" : ",\n", doc_id, title, pages_json);

    size_t blen = strlen(doc_block);
    if (*json_len + blen + 1024 > *json_alloc) {
        *json_alloc *= 2;
        *json_out = realloc(*json_out, *json_alloc);
    }
    strcat(*json_out, doc_block);
    *json_len += blen;
    *first_doc = false;

    free(pages_json);
}

static void handle_metadata(int client_fd, const char *req_id) {
    char path[PATH_MAX];
    snprintf(path, sizeof(path), "/home/root/.local/share/remarkable/xochitl/%s.metadata", req_id);
    FILE *f = fopen(path, "r");
    char title[256] = "Untitled";
    char type[64] = "";
    if (f) {
        char buffer[4096];
        size_t len = fread(buffer, 1, sizeof(buffer) - 1, f);
        buffer[len] = '\0';
        fclose(f);
        extract_json_str(buffer, "visibleName", title, sizeof(title));
        extract_json_str(buffer, "type", type, sizeof(type));
    } else {
        send_http_response(client_fd, 404, "application/json", "{\"error\":\"metadata not found\"}");
        return;
    }

    size_t json_alloc = 256 * 1024;
    char *json = malloc(json_alloc);
    strcpy(json, "{\n  \"documents\": [\n");
    size_t json_len = strlen(json);
    bool first_doc = true;

    if (strcmp(type, "CollectionType") == 0) {
        DIR *dir = opendir("/home/root/.local/share/remarkable/xochitl");
        if (dir) {
            struct dirent *ent;
            while ((ent = readdir(dir)) != NULL) {
                char *ext = strstr(ent->d_name, ".metadata");
                if (ext && strlen(ent->d_name) == 36 + 9) {
                    char child_id[37];
                    strncpy(child_id, ent->d_name, 36);
                    child_id[36] = '\0';

                    char cpath[PATH_MAX];
                    snprintf(cpath, sizeof(cpath), "/home/root/.local/share/remarkable/xochitl/%s", ent->d_name);
                    FILE *cf = fopen(cpath, "r");
                    if (cf) {
                        char cbuf[4096];
                        size_t clen = fread(cbuf, 1, sizeof(cbuf) - 1, cf);
                        cbuf[clen] = '\0';
                        fclose(cf);

                        char cparent[64] = "";
                        char ctype[64] = "";
                        char ctitle[256] = "Untitled";
                        extract_json_str(cbuf, "parent", cparent, sizeof(cparent));
                        extract_json_str(cbuf, "type", ctype, sizeof(ctype));
                        extract_json_str(cbuf, "visibleName", ctitle, sizeof(ctitle));

                        if (strcmp(cparent, req_id) == 0 && strcmp(ctype, "DocumentType") == 0) {
                            append_document_json(child_id, ctitle, &json, &json_alloc, &json_len, &first_doc);
                        }
                    }
                }
            }
            closedir(dir);
        }
    } else {
        append_document_json(req_id, title, &json, &json_alloc, &json_len, &first_doc);
    }

    strcat(json, "\n  ]\n}");
    send_http_response(client_fd, 200, "application/json", json);
    free(json);
}

static void handle_sync(int client_fd, const char *body) {
  if (!g_cache) {
    send_http_response(client_fd, 500, "application/json",
                       "{\"error\":\"cache not initialized\"}");
    return;
  }

  char *doc_id_ptr = strstr((char *)body, "\"document_id\"");
  if (!doc_id_ptr) {
    send_http_response(client_fd, 400, "application/json",
                       "{\"error\":\"missing document_id\"}");
    return;
  }

  char *doc_colon = strchr(doc_id_ptr, ':');
  if (!doc_colon) {
    send_http_response(client_fd, 400, "application/json",
                       "{\"error\":\"invalid document_id\"}");
    return;
  }
  char *doc_q1 = strchr(doc_colon, '"');
  if (!doc_q1) {
    send_http_response(client_fd, 400, "application/json",
                       "{\"error\":\"invalid document_id string\"}");
    return;
  }
  doc_q1++;
  char *doc_q2 = strchr(doc_q1, '"');
  if (!doc_q2 || (doc_q2 - doc_q1) != 36) {
    send_http_response(client_fd, 400, "application/json",
                       "{\"error\":\"invalid document_id length\"}");
    return;
  }

  char doc_id[37];
  strncpy(doc_id, doc_q1, 36);
  doc_id[36] = '\0';

  char *pages_arr = strstr((char *)body, "\"pages\"");
  if (!pages_arr) {
    send_http_response(client_fd, 400, "application/json",
                       "{\"error\":\"missing pages\"}");
    return;
  }

  char *arr_start = strchr(pages_arr, '[');
  if (!arr_start) {
    send_http_response(client_fd, 400, "application/json",
                       "{\"error\":\"invalid pages array\"}");
    return;
  }

  char *p = arr_start + 1;
  int queued = 0;
  while (*p && *p != ']') {
    char *q1 = strchr(p, '"');
    if (!q1)
      break;
    q1++;
    char *q2 = strchr(q1, '"');
    if (!q2)
      break;

    if (q2 - q1 == 36) {
      char page_uuid[37];
      strncpy(page_uuid, q1, 36);
      page_uuid[36] = '\0';
      char page_num[MAX_PAGE_NUM_LEN];
      parse_content_file(doc_id, page_uuid, page_num, sizeof(page_num));
      cache_add_or_update_page(g_cache, doc_id, page_uuid, page_num,
                               time(NULL));
      queued++;
    }
    p = q2 + 1;
  }

  if (queued > 0) {
    cache_save(g_cache);
  }

  char resp[128];
  snprintf(resp, sizeof(resp), "{\"status\":\"success\", \"queued\": %d}",
           queued);
  send_http_response(client_fd, 200, "application/json", resp);
}

static void handle_filetree(int client_fd) {
    size_t alloc_size = 1024 * 1024; // 1MB should be enough for most filetrees
    char *json = malloc(alloc_size);
    if (!json) {
        send_http_response(client_fd, 500, "application/json", "{\"error\":\"out of memory\"}");
        return;
    }

    strcpy(json, "[\n");
    size_t json_len = 2;
    bool first_file = true;

    DIR *dir = opendir("/home/root/.local/share/remarkable/xochitl");
    if (!dir) {
        free(json);
        send_http_response(client_fd, 500, "application/json", "{\"error\":\"cannot open directory\"}");
        return;
    }

    struct dirent *ent;
    while ((ent = readdir(dir)) != NULL) {
        if (ent->d_type == DT_REG) {
            const char *ext = strstr(ent->d_name, ".metadata");
            if (ext && strlen(ent->d_name) == 36 + 9) {
                char id_buf[37];
                strncpy(id_buf, ent->d_name, 36);
                id_buf[36] = '\0';

                char filepath[PATH_MAX];
                snprintf(filepath, sizeof(filepath), "/home/root/.local/share/remarkable/xochitl/%s", ent->d_name);
                
                FILE *f = fopen(filepath, "r");
                if (f) {
                    char buffer[4096];
                    size_t read_len = fread(buffer, 1, sizeof(buffer) - 1, f);
                    buffer[read_len] = '\0';
                    fclose(f);

                    char visibleName[256] = "Untitled";
                    char docType[64] = "DocumentType";
                    char parent[64] = "";
                    char lastModified[64] = "0";

                    extract_json_str(buffer, "visibleName", visibleName, sizeof(visibleName));
                    extract_json_str(buffer, "type", docType, sizeof(docType));
                    extract_json_str(buffer, "parent", parent, sizeof(parent));
                    extract_json_str(buffer, "lastModified", lastModified, sizeof(lastModified));

                    if (strcmp(parent, "trash") == 0) {
                        continue;
                    }

                    char block[1024];
                    snprintf(block, sizeof(block), "%s  {\"id\": \"%s\", \"visibleName\": \"%s\", \"type\": \"%s\", \"parent\": \"%s\", \"lastModified\": \"%s\"}",
                             first_file ? "" : ",\n", id_buf, visibleName, docType, parent, lastModified);
                    
                    size_t blen = strlen(block);
                    if (json_len + blen + 10 > alloc_size) {
                        alloc_size *= 2;
                        char *new_json = realloc(json, alloc_size);
                        if (!new_json) break;
                        json = new_json;
                    }
                    strcat(json, block);
                    json_len += blen;
                    first_file = false;
                }
            }
        }
    }
    closedir(dir);

    strcat(json, "\n]");
    send_http_response(client_fd, 200, "application/json", json);
    free(json);
}

static void handle_config_post(int client_fd, char* req_buffer, int received_so_far) {
    int body_len = 0;
    char *full_body = extract_body(client_fd, req_buffer, received_so_far, &body_len);
    if (!full_body || body_len <= 0) {
        send_http_response(client_fd, 400, "text/plain", "Bad Request");
        if (full_body) free(full_body);
        return;
    }

    int result = write_file_locked(CONFIG_PATH, full_body);
    free(full_body);

    if (result == 0) {
        log_msg("POST /config: Updated daemon settings");
        send_http_response(client_fd, 200, "application/json", "{\"status\":\"success\"}");
    } else {
        log_msg("POST /config: Failed to write to file");
        send_http_response(client_fd, 500, "application/json", "{\"error\":\"failed to write config\"}");
    }
}

static void handle_version_get(int client_fd) {
    char response[256];
    snprintf(response, sizeof(response), 
        "{\"version\":\"%s\",\"cache_format\":\"%d\"}", 
        APP_VERSION, CACHE_VERSION);
    send_http_response(client_fd, 200, "application/json", response);
}

static void handle_config_get(int client_fd) {
    size_t file_len = 0;
    char *buf = read_file_locked(CONFIG_PATH, &file_len);
    if (!buf) {
        send_http_response(client_fd, 404, "text/plain", "Config not found");
        return;
    }

    send_http_response(client_fd, 200, "text/plain", buf);
    free(buf);
}

// ---------- Server loop ----------

static void *httpserver_loop(void *arg) {
  int port = *(int *)arg;
  free(arg);

  g_server_fd = socket(AF_INET, SOCK_STREAM, 0);
  int opt = 1;
  setsockopt(g_server_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

  struct sockaddr_in addr;
  addr.sin_family = AF_INET;
  addr.sin_addr.s_addr = INADDR_ANY;
  addr.sin_port = htons(port);

  if (bind(g_server_fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
    perror("bind failed");
    return NULL;
  }

  listen(g_server_fd, 5);

  log_msg("=== HTTP Server started (v%s) ===", APP_VERSION);

  while (g_running) {
    struct timeval tv;
    tv.tv_sec = 1;
    tv.tv_usec = 0;

    fd_set readfds;
    FD_ZERO(&readfds);
    FD_SET(g_server_fd, &readfds);

    int n = select(g_server_fd + 1, &readfds, NULL, NULL, &tv);
    if (n <= 0)
      continue;

    int client = accept(g_server_fd, NULL, NULL);
    if (client < 0)
      continue;

    char req[16384];
    int received = recv(client, req, sizeof(req) - 1, 0);
    if (received > 0) {
      req[received] = '\0';

      // Log the request
      char method[16], path[256];
      if (sscanf(req, "%15s %255s", method, path) == 2) {
        log_msg("Request: %s %s", method, path);
      }

      // --- Whitelist routes ---
      if (strncmp(req, "GET /whitelist", 14) == 0) {
        handle_whitelist_get(client);
      } else if (strncmp(req, "PUT /whitelist", 14) == 0) {
        handle_whitelist_put(client, req, received);
      } else if (strncmp(req, "POST /whitelist/add", 19) == 0) {
        handle_whitelist_add(client, req, received);
      } else if (strncmp(req, "DELETE /whitelist", 17) == 0) {
        handle_whitelist_delete(client, req, received);

      // --- Endpoint routes ---
      } else if (strncmp(req, "GET /endpoints", 14) == 0) {
        handle_endpoints_get(client);
      } else if (strncmp(req, "POST /endpoints/add", 19) == 0) {
        handle_endpoints_add(client, req, received);
      } else if (strncmp(req, "DELETE /endpoints", 17) == 0) {
        handle_endpoints_delete(client, req, received);

      // --- Existing routes ---
      } else if (strncmp(req, "GET /filetree", 13) == 0) {
        handle_filetree(client);
      } else if (strncmp(req, "GET /metadata?id=", 17) == 0) {
        char doc_id[37];
        char *id_start = req + 17;
        int i = 0;
        while (i < 36 && id_start[i] != ' ' && id_start[i] != '\0') {
          doc_id[i] = id_start[i];
          i++;
        }
        doc_id[i] = '\0';
        if (i == 36) {
          handle_metadata(client, doc_id);
        } else {
          send_http_response(client, 400, "application/json",
                             "{\"error\":\"invalid id\"}");
        }
      } else if (strncmp(req, "GET /version", 12) == 0) {
        handle_version_get(client);
      } else if (strncmp(req, "GET /config", 11) == 0) {
        handle_config_get(client);
      } else if (strncmp(req, "POST /config", 12) == 0) {
        handle_config_post(client, req, received);
      } else if (strncmp(req, "POST /sync", 10) == 0) {
        char *separator = strstr(req, "\r\n\r\n");
        if (separator) {
          handle_sync(client, separator + 4);
        } else {
          send_http_response(client, 400, "application/json",
                             "{\"error\":\"invalid body\"}");
        }
      } else {
        send_http_response(client, 404, "application/json",
                           "{\"error\":\"not found\"}");
      }
    }
    close(client);
  }

  close(g_server_fd);
  return NULL;
}

int httpserver_start(int port) {
  g_running = true;
  int *p = malloc(sizeof(int));
  *p = port;
  log_msg("=== HTTP Server started (v%s) on port %d ===", APP_VERSION, port);
  if (pthread_create(&g_thread, NULL, httpserver_loop, p) != 0) {
    return -1;
  }
  return 0;
}

void httpserver_stop(void) {
  g_running = false;
  pthread_join(g_thread, NULL);
  log_msg("=== HTTP Server stopped ===");
}
