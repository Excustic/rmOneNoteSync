// watcher.c - File system watcher for reMarkable document changes
#define _GNU_SOURCE
#include "cache_io.h"
#include "metadata_parser.h"
#include "version.h"
#include <dirent.h>
#include <errno.h>
#include <limits.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/inotify.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

// Configuration defaults
#define DEFAULT_WATCH_PATH "/home/root/.local/share/remarkable/xochitl"
#define DEFAULT_LOG_PATH "/home/root/onenote-sync/logs/watcher.log"
#define DEFAULT_CACHE_PATH "/home/root/onenote-sync/cache/.sync_cache"
#define DEFAULT_CONFIG_PATH "/home/root/onenote-sync/watcher.conf"

#define BUF_LEN (10 * (sizeof(struct inotify_event) + NAME_MAX + 1))

// Global configuration
static char watch_path[PATH_MAX] = DEFAULT_WATCH_PATH;
static char log_path[PATH_MAX] = DEFAULT_LOG_PATH;
static char cache_path[PATH_MAX] = DEFAULT_CACHE_PATH;
static CacheHandle *cache = NULL;

#define MAX_WHITELIST_DOCS 512
static char whitelist[MAX_WHITELIST_DOCS][UUID_LEN + 1];
static int whitelist_count = 0;

static int is_whitelisted(const char *doc_id) {
  if (whitelist_count <= 0)
    return 1;
  for (int i = 0; i < whitelist_count; i++) {
    if (strcmp(whitelist[i], doc_id) == 0)
      return 1;
  }
  return 0;
}

/**
 * ends_with - Check if a string ends with a suffix
 *
 * @param str: String to check
 * @param suffix: Suffix to look for
 * @return: 1 if it ends with suffix, 0 otherwise
 */
static int ends_with(const char *str, const char *suffix) {
  if (!str || !suffix)
    return 0;
  size_t len_str = strlen(str);
  size_t len_suffix = strlen(suffix);
  if (len_suffix > len_str)
    return 0;
  return strcmp(str + len_str - len_suffix, suffix) == 0;
}

/**
 * log_msg - Write timestamped log message
 */
void log_msg(const char *fmt, ...) {
  FILE *f = fopen(log_path, "a");
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
 * load_config - Load configuration from file
 */
void load_config() {
  FILE *f = fopen(DEFAULT_CONFIG_PATH, "r");
  if (!f)
    return;

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

    if (strcmp(key, "WATCH_PATH") == 0) {
      strncpy(watch_path, val, PATH_MAX - 1);
    } else if (strcmp(key, "LOG_PATH") == 0) {
      strncpy(log_path, val, PATH_MAX - 1);
    } else if (strcmp(key, "CACHE_PATH") == 0) {
      strncpy(cache_path, val, PATH_MAX - 1);
    } else if (strcmp(key, "WHITELIST_COUNT") == 0) {
      whitelist_count = atoi(val);
      if (whitelist_count > MAX_WHITELIST_DOCS) {
        whitelist_count = MAX_WHITELIST_DOCS;
      }
    } else if (strncmp(key, "WHITELIST_", 10) == 0) {
      int idx = atoi(key + 10);
      if (idx >= 0 && idx < MAX_WHITELIST_DOCS) {
        strncpy(whitelist[idx], val, UUID_LEN);
        whitelist[idx][UUID_LEN] = '\0';
      }
    }
  }
  fclose(f);
}

/**
 * extract_document_id - Extract document UUID from a path
 *
 * @param path: File path (e.g.,
 * "036f73e1-32ad-44a4-8909-182a7381b5a6.metadata")
 * @return: Pointer to start of UUID in path, or NULL
 */
const char *extract_document_id(const char *path) {
  if (!path)
    return NULL;

  // Find the last '/' to get filename
  const char *filename = strrchr(path, '/');
  if (!filename) {
    filename = path;
  } else {
    filename++; // Skip the '/'
  }

  // Check if it looks like a UUID (36 chars)
  if (strlen(filename) >= UUID_LEN) {
    // Verify it has the UUID format (8-4-4-4-12 with hyphens)
    if (filename[8] == '-' && filename[13] == '-' && filename[18] == '-' &&
        filename[23] == '-') {
      return filename;
    }
  }

  return NULL;
}

/**
 * scan_document_pages - Scan all .rm files in a document directory
 *
 * @param doc_id: Document UUID
 * @return: Number of pages updated
 */
int scan_document_pages(const char *doc_id,
                        unsigned long long last_opened_sec) {
  char dir_path[PATH_MAX];
  snprintf(dir_path, sizeof(dir_path), "%s/%s", watch_path, doc_id);

  DIR *dir = opendir(dir_path);
  if (!dir) {
    log_msg("Cannot open directory %s: %s", dir_path, strerror(errno));
    return 0;
  }

  int pages_updated = 0;
  struct dirent *entry;

  while ((entry = readdir(dir)) != NULL) {
    // Look for .rm files
    char *ext = strrchr(entry->d_name, '.');
    if (!ext || strcmp(ext, ".rm") != 0)
      continue;

    // Extract page UUID (filename without .rm extension)
    char page_uuid[UUID_LEN + 1];
    size_t name_len = ext - entry->d_name;
    if (name_len != UUID_LEN)
      continue;

    strncpy(page_uuid, entry->d_name, UUID_LEN);
    page_uuid[UUID_LEN] = '\0';

    // Get file modification time
    char file_path[PATH_MAX];
    snprintf(file_path, sizeof(file_path), "%s/%s", dir_path, entry->d_name);

    struct stat st;
    if (stat(file_path, &st) != 0) {
      log_msg("  [Page %s] Failed to stat file", page_uuid);
      continue;
    }

    // If a valid lastOpened is present, include only pages modified after it
    if (last_opened_sec > 0) {
      if ((unsigned long long)st.st_mtime <= last_opened_sec) {
        log_msg("  [Page %s] Skipped: mtime (%llu) <= last_opened_sec (%llu)",
                page_uuid, (unsigned long long)st.st_mtime, last_opened_sec);
        continue;
      } else {
        log_msg("  [Page %s] Included: mtime (%llu) > last_opened_sec (%llu)",
                page_uuid, (unsigned long long)st.st_mtime, last_opened_sec);
      }
    } else {
      log_msg("  [Page %s] Included: last_opened_sec is 0", page_uuid);
    }

    // Try to get page number from content file
    char page_num[8] = "";
    parse_content_file(doc_id, page_uuid, page_num, sizeof(page_num));

    // Check if this page needs updating
    DocumentEntry *doc = cache_find_document(cache, doc_id);
    PageEntry *page = doc ? cache_find_page(doc, page_uuid) : NULL;

    if (!page) {
      // New page - add to queue
      cache_add_or_update_page(cache, doc_id, page_uuid, page_num, st.st_mtime);
      pages_updated++;
      log_msg("Page %s/%s queued for sync (mtime=%ld)", doc_id, page_uuid,
              st.st_mtime);
    } else if (page->mtime < st.st_mtime) {
      // Already in cache but modified again - update timestamp
      cache_add_or_update_page(cache, doc_id, page_uuid, page_num, st.st_mtime);
      pages_updated++;
      log_msg("Queued page %s/%s modified, updating timestamp", doc_id,
              page_uuid);
    }
  }

  closedir(dir);
  return pages_updated;
}

static void get_json_value(const char *json, const char *key, char *out,
                           size_t out_size) {
  if (!out || out_size == 0)
    return;
  out[0] = '\0';
  char search_key[128];
  snprintf(search_key, sizeof(search_key), "\"%s\"", key);
  const char *pos = strstr(json, search_key);
  if (pos) {
    const char *colon = strchr(pos, ':');
    if (colon) {
      const char *p = colon + 1;
      while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')
        p++;
      if (*p == '"') {
        const char *quote2 = strchr(p + 1, '"');
        if (quote2) {
          size_t len = quote2 - (p + 1);
          if (len >= out_size)
            len = out_size - 1;
          strncpy(out, p + 1, len);
          out[len] = '\0';
        }
      } else {
        size_t i = 0;
        while (p[i] && p[i] != ',' && p[i] != '}' && p[i] != ' ' &&
               p[i] != '\n' && p[i] != '\r' && i < out_size - 1) {
          out[i] = p[i];
          i++;
        }
        out[i] = '\0';
      }
    }
  }
}

static unsigned long long get_document_open_time_sec(const char *doc_id) {
  char filepath[PATH_MAX];
  snprintf(filepath, sizeof(filepath), "%s/%s.metadata", watch_path, doc_id);

  FILE *f = fopen(filepath, "r");
  if (!f)
    return (unsigned long long)-1;

  char buffer[4096];
  size_t len = fread(buffer, 1, sizeof(buffer) - 1, f);
  buffer[len] = '\0';
  fclose(f);

  int is_complete = 0;
  for (long i = (long)len - 1; i >= 0; i--) {
    if (buffer[i] == ' ' || buffer[i] == '\n' || buffer[i] == '\r')
      continue;
    if (buffer[i] == '}')
      is_complete = 1;
    break;
  }
  if (!is_complete)
    return (unsigned long long)-1;

  char last_opened[64] = "";
  get_json_value(buffer, "lastOpened", last_opened, sizeof(last_opened));

  unsigned long long open_time = 0;
  if (strlen(last_opened) > 0 && strcmp(last_opened, "null") != 0)
    open_time = strtoull(last_opened, NULL, 10);

  return open_time / 1000;
}

/**
 * process_metadata_change - Process a change to a .metadata file
 *
 * @param filename: Name of the metadata file
 */
void process_metadata_change(const char *filename) {
  // Extract document ID from filename
  char doc_id[UUID_LEN + 1];
  if (strlen(filename) < UUID_LEN + 9)
    return; // UUID + ".metadata"

  strncpy(doc_id, filename, UUID_LEN);
  doc_id[UUID_LEN] = '\0';

  if (!is_whitelisted(doc_id)) {
    log_msg("Document %s not in whitelist, skipping metadata change", doc_id);
    return;
  }

  log_msg("Processing metadata change for document %s", doc_id);

  char filepath[PATH_MAX];
  snprintf(filepath, sizeof(filepath), "%s/%s", watch_path, filename);

  FILE *f = fopen(filepath, "r");
  if (!f) {
    log_msg("Failed to open metadata file %s for reading, skipping", filepath);
    return;
  }

  char buffer[4096];
  size_t len = fread(buffer, 1, sizeof(buffer) - 1, f);
  buffer[len] = '\0';
  fclose(f);

  // Check for partial write: valid JSON should end with '}' (ignoring
  // whitespace)
  int is_complete = 0;
  for (long i = (long)len - 1; i >= 0; i--) {
    if (buffer[i] == ' ' || buffer[i] == '\n' || buffer[i] == '\r')
      continue;
    if (buffer[i] == '}')
      is_complete = 1;
    break;
  }
  if (!is_complete) {
    log_msg("Skipping incomplete metadata read for %s", doc_id);
    return;
  }

  char last_mod[64] = "";
  char last_opened[64] = "";

  get_json_value(buffer, "lastModified", last_mod, sizeof(last_mod));
  get_json_value(buffer, "lastOpened", last_opened, sizeof(last_opened));

  unsigned long long mod_time = 0;
  unsigned long long open_time = 0;
  if (strlen(last_mod) > 0 && strcmp(last_mod, "null") != 0)
    mod_time = strtoull(last_mod, NULL, 10);
  if (strlen(last_opened) > 0 && strcmp(last_opened, "null") != 0)
    open_time = strtoull(last_opened, NULL, 10);

  unsigned long long open_time_sec = open_time / 1000;
  log_msg("Parsed metadata for %s: mod_time=%llu, open_time=%llu -> sec=%llu",
          doc_id, mod_time, open_time, open_time_sec);

  if (mod_time > 0 && open_time > 0 && mod_time <= open_time) {
    log_msg("Skipping document %s (lastModified %llu <= lastOpened %llu)",
            doc_id, mod_time, open_time);
    return;
  }

  // Synchronize memory with disk before updating - avoid old in-memory cache
  cache_reload(cache);

  // Scan all pages in this document
  int pages_updated = scan_document_pages(doc_id, open_time_sec);

  if (pages_updated > 0) {
    log_msg("Updated %d pages for document %s", pages_updated, doc_id);
    cache_save(cache);
  }
}

/**
 * main - Main entry point
 */
int main(int argc, char **argv) {
  // Load configuration
  load_config();

  // Override watch path if provided as argument
  if (argc > 1) {
    strncpy(watch_path, argv[1], PATH_MAX - 1);
    watch_path[PATH_MAX - 1] = '\0';
  }

  log_msg("=== Watcher started (v%s) ===", APP_VERSION);
  log_msg("Watch path: %s", watch_path);
  log_msg("Cache path: %s", cache_path);
  log_msg("Log path: %s", log_path);

  // Open cache
  cache = cache_open(cache_path);
  if (!cache) {
    log_msg("ERROR: Failed to open cache");
    return 1;
  }

  // Report cache status
  int queued = cache_count_pages(cache);
  log_msg("Cache loaded: %d pages queued", queued);

  // Initialize inotify
  int fd = inotify_init();
  if (fd < 0) {
    log_msg("ERROR: Failed to initialize inotify: %s", strerror(errno));
    cache_close(cache, true);
    return 1;
  }

  // Add watch
  int wd = inotify_add_watch(fd, watch_path,
                             IN_CREATE | IN_MODIFY | IN_DELETE | IN_MOVED_TO);
  if (wd < 0) {
    log_msg("ERROR: Failed to add watch on %s: %s", watch_path,
            strerror(errno));
    close(fd);
    cache_close(cache, true);
    return 1;
  }

  log_msg("Watching for changes...");

  // Event loop
  char buf[BUF_LEN];
  while (1) {
    int len = read(fd, buf, BUF_LEN);
    if (len < 0) {
      if (errno == EINTR)
        continue;
      log_msg("ERROR: Read failed: %s", strerror(errno));
      break;
    }

    // Process events
    int i = 0;
    while (i < len) {
      struct inotify_event *event = (struct inotify_event *)&buf[i];

      if (event->len > 0) {
        // Skip temporary files
        if (ends_with(event->name, ".tmp")) {
          i += sizeof(struct inotify_event) + event->len;
          continue;
        }

        // Check if it's a metadata file
        if (strstr(event->name, ".metadata")) {
          if (event->mask & (IN_CREATE | IN_MODIFY | IN_MOVED_TO)) {
            process_metadata_change(event->name);
          }
        }

        // Also check for direct .rm file changes in subdirectories
        if (strstr(event->name, ".rm")) {
          // Extract document ID from the path
          const char *doc_id_start = extract_document_id(event->name);
          if (doc_id_start) {
            char doc_id[UUID_LEN + 1];
            strncpy(doc_id, doc_id_start, UUID_LEN);
            doc_id[UUID_LEN] = '\0';

            if (is_whitelisted(doc_id)) {
              log_msg("Direct .rm change detected in %s", doc_id);
              unsigned long long open_sec = get_document_open_time_sec(doc_id);
              if (open_sec != (unsigned long long)-1) {
                // Reload cache to avoid old in-memory cache
                cache_reload(cache);
                scan_document_pages(doc_id, open_sec);
                cache_save(cache);
              } else {
                log_msg("Failed to read last_opened_sec for %s, skipping .rm "
                        "scan to avoid sync storm",
                        doc_id);
              }
            } else {
              log_msg("Direct .rm change detected but %s not in whitelist",
                      doc_id);
            }
          }
        }
      }

      i += sizeof(struct inotify_event) + event->len;
    }
  }

  // Cleanup
  inotify_rm_watch(fd, wd);
  close(fd);
  cache_close(cache, true);
  log_msg("=== Watcher stopped ===");

  return 0;
}