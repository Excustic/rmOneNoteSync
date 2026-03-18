// httpclient.c - Production HTTP client for reMarkable sync
#include "cache_io.h"
#include "http_simple.h"
#include "metadata_parser.h"
#include "version.h"
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

// Configuration defaults
#define DEFAULT_SERVER_URL "http://192.168.1.100:8080/upload"
#define DEFAULT_API_KEY "test-api-key"

#define DEFAULT_CACHE_PATH "/home/root/onenote-sync/cache/.sync_cache"
#define DEFAULT_XOCHITL_PATH "/home/root/.local/share/remarkable/xochitl"
#define DEFAULT_LOG_PATH "/home/root/onenote-sync/logs/httpclient.log"
#define DEFAULT_CONFIG_PATH "/home/root/onenote-sync/httpclient.conf"
#define DEFAULT_INTERVAL 30
#define DEFAULT_MAX_RETRIES 5
#define DEFAULT_RETRY_DELAY 20
#define DEFAULT_TIMEOUT 10
#define MAX_BATCH_SIZE 10 // Process up to 10 files per cycle
#define MAX_WHITELIST_DOCS 512

// Configuration structure
typedef struct {
  char server_url[256];
  char server_url_fallback[256];
  char api_key[128];
  int upload_interval_seconds;
  int max_retries;
  int retry_delay_seconds;
  int timeout_seconds;
  int whitelist_count;
  char whitelist[MAX_WHITELIST_DOCS][UUID_LEN + 1];
} config_t;

// Global variables
static volatile int keep_running = 1;
static config_t config;
static CacheHandle *cache = NULL;

/**
 * signal_handler - Handle SIGINT/SIGTERM for clean shutdown
 */
void signal_handler(int sig) { keep_running = 0; }

/**
 * log_msg - Write timestamped log message
 */
void log_msg(const char *fmt, ...) {
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

/**
 * load_config_from_file - Load configuration from local file
 */
void load_config_from_file() {
  // Set defaults
  strcpy(config.server_url, DEFAULT_SERVER_URL);
  config.server_url_fallback[0] = '\0';
  strcpy(config.api_key, DEFAULT_API_KEY);
  config.upload_interval_seconds = DEFAULT_INTERVAL;
  config.max_retries = DEFAULT_MAX_RETRIES;
  config.retry_delay_seconds = DEFAULT_RETRY_DELAY;
  config.timeout_seconds = DEFAULT_TIMEOUT;
  config.whitelist_count = 0;

  FILE *f = fopen(DEFAULT_CONFIG_PATH, "r");
  if (!f) {
    log_msg("No config file found, using defaults");
    return;
  }

  char line[512];
  while (fgets(line, sizeof(line), f)) {
    if (line[0] == '#' || line[0] == '\n' || line[0] == '\r')
      continue;

    char *eq = strchr(line, '=');
    if (!eq)
      continue;

    *eq = '\0';
    char *key = line;
    char *val = eq + 1;

    // Trim whitespace
    while (*val == ' ' || *val == '\t')
      val++;
    val[strcspn(val, "\n\r")] = '\0';

    if (strcmp(key, "SERVER_URL") == 0) {
      strncpy(config.server_url, val, sizeof(config.server_url) - 1);
    } else if (strcmp(key, "SERVER_URL_FALLBACK") == 0) {
      strncpy(config.server_url_fallback, val,
              sizeof(config.server_url_fallback) - 1);
    } else if (strcmp(key, "API_KEY") == 0) {
      strncpy(config.api_key, val, sizeof(config.api_key) - 1);
    } else if (strcmp(key, "UPLOAD_INTERVAL") == 0) {
      config.upload_interval_seconds = atoi(val);
    } else if (strcmp(key, "MAX_RETRIES") == 0) {
      config.max_retries = atoi(val);
    } else if (strcmp(key, "RETRY_DELAY") == 0) {
      config.retry_delay_seconds = atoi(val);
    } else if (strcmp(key, "TIMEOUT") == 0) {
      config.timeout_seconds = atoi(val);
    } else if (strcmp(key, "WHITELIST_COUNT") == 0) {
      config.whitelist_count = atoi(val);
      if (config.whitelist_count > MAX_WHITELIST_DOCS) {
        config.whitelist_count = MAX_WHITELIST_DOCS;
      }
    } else if (strncmp(key, "WHITELIST_", 10) == 0) {
      int idx = atoi(key + 10);
      if (idx >= 0 && idx < MAX_WHITELIST_DOCS) {
        strncpy(config.whitelist[idx], val, UUID_LEN);
        config.whitelist[idx][UUID_LEN] = '\0';
      }
    }
  }

  fclose(f);
  log_msg("Config loaded from file");
}

/**
 * upload_file - Upload a single .rm file
 *
 * @param doc_id: Document UUID
 * @param page_uuid: Page UUID
 * @param page_num: Page number
 * @param virtual_path: Virtual path for metadata
 * @return: 0 on success, -1 on error
 */
int upload_file(const char *doc_id, const char *page_uuid, const char *page_num,
                const char *virtual_path) {
  // Build file path
  char file_path[PATH_MAX];
  snprintf(file_path, sizeof(file_path), "%s/%s/%s.rm", DEFAULT_XOCHITL_PATH,
           doc_id, page_uuid);

  // Check if file exists
  struct stat st;
  if (stat(file_path, &st) != 0) {
    log_msg("File not found: %s", file_path);
    return -1;
  }

  // Build complete virtual path with page
  char full_virtual_path[PATH_MAX];
  if (page_num && *page_num) {
    snprintf(full_virtual_path, sizeof(full_virtual_path), "%s/Page %s",
             virtual_path, page_num);
  } else {
    strcpy(full_virtual_path, virtual_path);
  }

  log_msg("Uploading %s -> %s", file_path, full_virtual_path);

  // Perform upload
  http_response_t response;
  int result = http_post_file(config.server_url, config.api_key, file_path,
                              full_virtual_path, doc_id, &response);

  if (result != 0) {
    log_msg("Failed to connect to primary server %s", config.server_url);

    // Try fallback URL if configured
    if (config.server_url_fallback[0] != '\0') {
      log_msg("Trying fallback server %s...", config.server_url_fallback);
      result = http_post_file(config.server_url_fallback, config.api_key,
                              file_path, full_virtual_path, doc_id, &response);
    }
  }

  if (result == 0) {
    log_msg("Upload response: status=%d, size=%zu", response.status_code,
            response.body_size);

    if (response.status_code == 200 || response.status_code == 201) {
      log_msg("Upload successful");
      http_response_free(&response);
      return 0;
    } else {
      log_msg("Upload failed with status %d", response.status_code);
      if (response.body) {
        log_msg("Server error: %s", response.body);
      }
    }
    http_response_free(&response);
  } else {
    log_msg("Failed to connect to server");
  }

  return -1;
}

/**
 * process_pending_pages - Process pages pending upload
 *
 * @return: Number of pages processed
 */
int process_pending_pages() {
  // Reload cache to get latest changes from watcher
  cache_reload(cache);

  // Get all pages (cache is now a simple FIFO queue)
  PageEntry **pages = cache_get_all_pages(cache, MAX_BATCH_SIZE);
  if (!pages) {
    return 0;
  }

  int processed = 0;
  int failures = 0;

  for (int i = 0; pages[i] != NULL; i++) {
    PageEntry *page = pages[i];

    // Find which document this page belongs to
    const char *doc_id = cache_get_document_for_page(cache, page->uuid);
    if (!doc_id) {
      log_msg("Cannot find document for page %s", page->uuid);
      continue;
    }

    // Check whitelist
    if (config.whitelist_count > 0) {
      int allowed = 0;
      for (int w = 0; w < config.whitelist_count; w++) {
        if (strcmp(config.whitelist[w], doc_id) == 0) {
          allowed = 1;
          break;
        }
      }
      if (!allowed) {
        log_msg("Document %s not in whitelist, removing from queue", doc_id);
        cache_remove_page(cache, doc_id, page->uuid);
        continue;
      }
    }

    // Reconstruct virtual path
    path_info_t path_info;
    if (reconstruct_virtual_path(doc_id, page->page_num, &path_info) != 0) {
      log_msg("Cannot reconstruct path for document %s, removing from queue",
              doc_id);
      cache_remove_page(cache, doc_id, page->uuid);
      continue;
    }

    // Attempt upload
    int upload_result =
        upload_file(doc_id, page->uuid, page->page_num, path_info.full_path);

    if (upload_result == 0) {
      // Success - remove from queue
      cache_remove_page(cache, doc_id, page->uuid);
      processed++;
    } else {
      failures++;
      log_msg("Page %s upload failed (failure #%d this cycle)", page->uuid,
              failures);

      // If too many consecutive failures, stop trying this cycle
      if (failures >= config.max_retries) {
        log_msg("Too many failures (%d), stopping this cycle", failures);
        break;
      }

      // Wait before trying next page
      if (pages[i + 1] != NULL) {
        sleep(config.retry_delay_seconds);
      }
    }
  }

  free(pages);

  // Save cache after processing
  if (processed > 0 || cache->dirty) {
    cache_save(cache);
  }

  return processed;
}

/**
 * main - Main entry point
 */
int main(int argc, char **argv) {
  // Setup signal handlers
  signal(SIGINT, signal_handler);
  signal(SIGTERM, signal_handler);

  log_msg("=== HTTP Client started (v%s) ===", APP_VERSION);

  // Load configuration
  load_config_from_file();

  log_msg("Configuration:");
  log_msg("  Server URL: %s", config.server_url);
  if (config.server_url_fallback[0] != '\0') {
    log_msg("  Fallback URL: %s", config.server_url_fallback);
  }
  log_msg("  Api Key: %s", config.api_key);
  log_msg("  Upload interval: %d seconds", config.upload_interval_seconds);
  log_msg("  Max retries: %d", config.max_retries);

  // Open cache
  cache = cache_open(DEFAULT_CACHE_PATH);
  if (!cache) {
    log_msg("ERROR: Failed to open cache");
    return 1;
  }

  // Report cache status
  int queued = cache_count_pages(cache);
  log_msg("Cache status: %d pages queued", queued);

  // Main loop
  while (keep_running) {
    log_msg("--- Sync cycle starting ---");

    // Process pages in queue
    int processed = process_pending_pages();

    queued = cache_count_pages(cache);
    if (processed > 0) {
      log_msg("Processed %d pages this cycle, %d remaining", processed, queued);
    } else if (queued > 0) {
      log_msg("No pages processed, but %d still queued", queued);
    } else {
      log_msg("Queue empty, nothing to process");
    }

    // Wait for next cycle
    if (keep_running) {
      log_msg("Sleeping for %d seconds...", config.upload_interval_seconds);

      // Use interruptible sleep
      for (int i = 0; i < config.upload_interval_seconds && keep_running; i++) {
        sleep(1);
      }
    }
  }

  // Cleanup
  log_msg("Shutdown signal received, cleaning up...");
  cache_close(cache, true);
  log_msg("=== HTTP Client stopped ===");

  return 0;
}