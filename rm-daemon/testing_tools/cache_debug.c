// cache_debug.c - Cache debug tool with virtual path display
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <stdbool.h>
#include <dirent.h>
#include <sys/stat.h>

#define UUID_LEN 36
#define MAX_PAGE_NUM_LEN 8
#define PATH_MAX 4096
#define CACHE_MAGIC 0x524D4348  // "RMCH" in hex
#define MAX_PATH_DEPTH 32

// Default xochitl path (can be overridden with -x)
static char xochitl_path[PATH_MAX] = "/home/root/.local/share/remarkable/xochitl";

/**
 * print_usage - Display usage information
 */
void print_usage(const char* prog_name) {
    printf("Usage: %s [OPTIONS] <cache_file>\n", prog_name);
    printf("\nOptions:\n");
    printf("  -h, --help     Show this help message\n");
    printf("  -v, --verbose  Show detailed output\n");
    printf("  -s, --summary  Show summary only\n");
    printf("  -d DOC_ID      Show only specific document\n");
    printf("  -x PATH        Override xochitl path (default: %s)\n", xochitl_path);
    printf("\nExamples:\n");
    printf("  %s /home/root/onenote-sync/cache/.sync_cache\n", prog_name);
    printf("  %s -v /home/root/onenote-sync/cache/.sync_cache\n", prog_name);
    printf("\n");
}

/**
 * format_timestamp - Convert timestamp to readable format
 */
void format_timestamp(time_t timestamp, char* buffer, size_t size) {
    if (timestamp == 0) {
        strncpy(buffer, "Never", size - 1);
        buffer[size - 1] = '\0';
        return;
    }

    struct tm* tm_info = localtime(&timestamp);
    strftime(buffer, size, "%Y-%m-%d %H:%M:%S", tm_info);
}

// ---- Inline metadata parser (to keep cache_debug self-contained) ----

typedef struct {
    char doc_id[UUID_LEN + 1];
    char visible_name[256];
    char parent[UUID_LEN + 1];
    char type[32];
} debug_metadata_t;

static bool read_json_val(const char* json, const char* key,
                          char* value, size_t value_size) {
    char search_key[256];
    snprintf(search_key, sizeof(search_key), "\"%s\"", key);

    const char* key_pos = strstr(json, search_key);
    if (!key_pos) return false;

    const char* colon = strchr(key_pos + strlen(search_key), ':');
    if (!colon) return false;

    const char* p = colon + 1;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;

    if (*p == '"') {
        p++;
        const char* end = strchr(p, '"');
        if (!end) return false;
        size_t len = end - p;
        if (len >= value_size) len = value_size - 1;
        strncpy(value, p, len);
        value[len] = '\0';
        return true;
    } else if (*p == 'n' && strncmp(p, "null", 4) == 0) {
        value[0] = '\0';
        return true;
    } else {
        size_t i = 0;
        while (p[i] && p[i] != ',' && p[i] != '}' &&
               p[i] != ' ' && p[i] != '\n' && p[i] != '\r' &&
               i < value_size - 1) {
            value[i] = p[i];
            i++;
        }
        value[i] = '\0';
        return i > 0;
    }
}

static bool read_metadata(const char* doc_id, debug_metadata_t* info) {
    char path[PATH_MAX];
    snprintf(path, sizeof(path), "%s/%s.metadata", xochitl_path, doc_id);

    FILE* f = fopen(path, "r");
    if (!f) return false;

    char buffer[4096];
    size_t len = fread(buffer, 1, sizeof(buffer) - 1, f);
    fclose(f);
    if (len == 0) return false;
    buffer[len] = '\0';

    strncpy(info->doc_id, doc_id, UUID_LEN);
    info->doc_id[UUID_LEN] = '\0';

    if (!read_json_val(buffer, "visibleName", info->visible_name,
                       sizeof(info->visible_name))) {
        strcpy(info->visible_name, "Untitled");
    }

    if (!read_json_val(buffer, "parent", info->parent, sizeof(info->parent))) {
        info->parent[0] = '\0';
    }

    if (strcmp(info->parent, "trash") == 0) {
        info->parent[0] = '\0';
    }

    read_json_val(buffer, "type", info->type, sizeof(info->type));
    return true;
}

static int build_path_parts(const char* doc_id, char parts[][256],
                            int depth, int max_depth) {
    if (!doc_id || !*doc_id || depth >= max_depth) return depth;

    debug_metadata_t info;
    if (!read_metadata(doc_id, &info)) return depth;

    strcpy(parts[depth], info.visible_name);

    if (info.parent[0] != '\0') {
        return build_path_parts(info.parent, parts, depth + 1, max_depth);
    }

    return depth + 1;
}

/**
 * get_virtual_path - Reconstruct the virtual path for a document
 */
void get_virtual_path(const char* doc_id, const char* page_num,
                      char* out_path, size_t out_size) {
    debug_metadata_t doc_meta;
    if (!read_metadata(doc_id, &doc_meta)) {
        snprintf(out_path, out_size, "(unknown)");
        return;
    }

    char path_parts[MAX_PATH_DEPTH][256];
    int num_parts = 0;

    if (doc_meta.parent[0] != '\0') {
        num_parts = build_path_parts(doc_meta.parent, path_parts, 0, MAX_PATH_DEPTH);
    }

    // Build full path (parts are in reverse order)
    out_path[0] = '\0';
    for (int i = num_parts - 1; i >= 0; i--) {
        if (out_path[0] != '\0') strcat(out_path, "/");
        strcat(out_path, path_parts[i]);
    }

    if (out_path[0] != '\0') strcat(out_path, "/");
    strcat(out_path, doc_meta.visible_name);

    // Add page
    if (page_num && *page_num) {
        char page_suffix[64];
        snprintf(page_suffix, sizeof(page_suffix), "/Page %s", page_num);
        strcat(out_path, page_suffix);
    }
}

/**
 * parse_cache_file - Main parsing function
 */
int parse_cache_file(const char* filename, int verbose, int summary_only,
                    const char* filter_doc) {
    FILE* f = fopen(filename, "rb");
    if (!f) {
        fprintf(stderr, "Error: Cannot open cache file '%s'\n", filename);
        return 1;
    }

    // Read and verify header
    uint32_t magic, num_docs;
    uint8_t version;

    if (fread(&magic, sizeof(magic), 1, f) != 1) {
        fprintf(stderr, "Error: Cannot read magic number\n");
        fclose(f);
        return 1;
    }

    if (magic != CACHE_MAGIC) {
        fprintf(stderr, "Error: Invalid magic number (0x%08X, expected 0x%08X)\n",
            magic, CACHE_MAGIC);
        fclose(f);
        return 1;
    }

    if (fread(&version, sizeof(version), 1, f) != 1) {
        fprintf(stderr, "Error: Cannot read version\n");
        fclose(f);
        return 1;
    }

    if (version != 4) {
        fprintf(stderr, "Error: Unsupported version (%d), expected 4\n", version);
        fclose(f);
        return 1;
    }

    if (fread(&num_docs, sizeof(num_docs), 1, f) != 1) {
        fprintf(stderr, "Error: Cannot read document count\n");
        fclose(f);
        return 1;
    }

    // Print header info
    printf("=== Cache File Debug Info ===\n");
    printf("File: %s\n", filename);
    printf("Magic: 0x%08X (RMCH)\n", magic);
    printf("Version: %d\n", version);
    printf("Documents: %d\n", num_docs);
    printf("\n");

    if (num_docs == 0) {
        printf("Cache is empty.\n");
        fclose(f);
        return 0;
    }

    // Counters
    uint32_t total_pages = 0;

    // Process each document
    for (uint32_t i = 0; i < num_docs; i++) {
        uint8_t doc_id_len;
        if (fread(&doc_id_len, sizeof(doc_id_len), 1, f) != 1) break;

        if (doc_id_len != UUID_LEN) break;

        char doc_id[UUID_LEN + 1];
        if (fread(doc_id, doc_id_len, 1, f) != 1) break;
        doc_id[doc_id_len] = '\0';

        uint16_t num_pages;
        if (fread(&num_pages, sizeof(num_pages), 1, f) != 1) break;

        // Check if we should show this document
        int show_document = (!filter_doc || strcmp(doc_id, filter_doc) == 0);

        if (show_document && !summary_only) {
            printf("=== Document: %s ===\n", doc_id);
            printf("Total Pages: %d\n\n", num_pages);

            if (!verbose) {
                printf("  %-4s  %-19s  %-36s  %s\n",
                       "Page", "Modified", "UUID", "Virtual Path");
                printf("  %-4s  %-19s  %-36s  %s\n",
                       "----", "-------------------",
                       "------------------------------------",
                       "--------------------------------------------");
            }
        }

        // Read pages
        for (uint16_t j = 0; j < num_pages; j++) {
            char page_uuid[UUID_LEN + 1];
            if (fread(page_uuid, UUID_LEN, 1, f) != 1) goto cleanup;
            page_uuid[UUID_LEN] = '\0';

            uint8_t page_num_len;
            if (fread(&page_num_len, sizeof(page_num_len), 1, f) != 1) goto cleanup;

            char page_num[MAX_PAGE_NUM_LEN] = "";
            if (page_num_len > 0 && page_num_len < MAX_PAGE_NUM_LEN) {
                if (fread(page_num, page_num_len, 1, f) != 1) goto cleanup;
                page_num[page_num_len] = '\0';
            }

            time_t mtime;
            if (fread(&mtime, sizeof(mtime), 1, f) != 1) goto cleanup;



            int show_page = show_document && !summary_only;

            if (show_page) {
                char time_str[32];
                format_timestamp(mtime, time_str, sizeof(time_str));

                // Reconstruct virtual path
                char virtual_path[PATH_MAX];
                get_virtual_path(doc_id, page_num, virtual_path, sizeof(virtual_path));

                if (verbose) {
                    printf("  Page UUID: %s\n", page_uuid);
                    printf("  Page Number: %s\n",
                           strlen(page_num) > 0 ? page_num : "(unknown)");
                    printf("  Modified: %s (%ld)\n", time_str, mtime);
                    printf("  Virtual Path: %s\n", virtual_path);
                    printf("  ---\n");
                } else {
                    printf("  %-4s  %s  %s  %s\n",
                        strlen(page_num) > 0 ? page_num : "?",
                        time_str,
                        page_uuid,
                        virtual_path);
                }
            }

            total_pages++;
        }

        if (show_document && !summary_only) {
            printf("\n");
        }
    }

cleanup:
    fclose(f);

    // Print summary
    if (!filter_doc || summary_only) {
        printf("=== Summary ===\n");
        printf("Total Pages: %d\n", total_pages);
    }

    return 0;
}

int main(int argc, char** argv) {
    if (argc < 2) {
        print_usage(argv[0]);
        return 1;
    }

    int verbose = 0;
    int summary_only = 0;
    char* filter_doc = NULL;
    char* cache_file = NULL;

    // Parse arguments
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "-h") == 0 || strcmp(argv[i], "--help") == 0) {
            print_usage(argv[0]);
            return 0;
        } else if (strcmp(argv[i], "-v") == 0 || strcmp(argv[i], "--verbose") == 0) {
            verbose = 1;
        } else if (strcmp(argv[i], "-s") == 0 || strcmp(argv[i], "--summary") == 0) {
            summary_only = 1;
        } else if (strcmp(argv[i], "-d") == 0 && i + 1 < argc) {
            filter_doc = argv[++i];
        } else if (strcmp(argv[i], "-x") == 0 && i + 1 < argc) {
            strncpy(xochitl_path, argv[++i], PATH_MAX - 1);
            xochitl_path[PATH_MAX - 1] = '\0';
        } else if (argv[i][0] != '-') {
            cache_file = argv[i];
        }
    }

    if (!cache_file) {
        fprintf(stderr, "Error: No cache file specified\n");
        print_usage(argv[0]);
        return 1;
    }

    return parse_cache_file(cache_file, verbose, summary_only, filter_doc);
}