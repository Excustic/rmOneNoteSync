// metadata_parser.c - Reconstruct virtual paths from reMarkable metadata
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dirent.h>
#include <sys/stat.h>
#include "metadata_parser.h"
#include "cache_io.h"

#define XOCHITL_PATH "/home/root/.local/share/remarkable/xochitl"
#define MAX_PATH_DEPTH 32

/**
 * read_json_value - Simple JSON parser to extract a value for a key
 * 
 * @param json: JSON string
 * @param key: Key to search for (e.g., "visibleName", "parent")
 * @param value: Output buffer for value
 * @param value_size: Size of output buffer
 * @return: true if found, false otherwise
 * 
 * Note: This is a simple parser that assumes well-formed JSON
 * and doesn't handle escaped characters in strings
 */
static bool read_json_value(const char* json, const char* key, 
                           char* value, size_t value_size) {
    char search_key[256];
    snprintf(search_key, sizeof(search_key), "\"%s\"", key);
    
    const char* key_pos = strstr(json, search_key);
    if (!key_pos) return false;
    
    // Find the colon after the key
    const char* colon = strchr(key_pos + strlen(search_key), ':');
    if (!colon) return false;
    
    // Skip whitespace after colon
    const char* p = colon + 1;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    
    if (*p == '"') {
        // String value
        p++; // Skip opening quote
        const char* end = strchr(p, '"');
        if (!end) return false;
        
        size_t len = end - p;
        if (len >= value_size) len = value_size - 1;
        strncpy(value, p, len);
        value[len] = '\0';
        return true;
    } else if (*p == 'n' && strncmp(p, "null", 4) == 0) {
        // Null value
        value[0] = '\0';
        return true;
    } else {
        // Number or boolean - copy until comma, }, or whitespace
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

/**
 * read_metadata_file - Read and parse a .metadata file
 * 
 * @param doc_id: Document UUID
 * @param info: Output metadata info
 * @return: true on success, false on error
 */
static bool read_metadata_file(const char* doc_id, metadata_info_t* info) {
    char path[PATH_MAX];
    snprintf(path, sizeof(path), "%s/%s.metadata", XOCHITL_PATH, doc_id);
    
    FILE* f = fopen(path, "r");
    if (!f) return false;
    
    // Read entire file (metadata files are small)
    char buffer[4096];
    size_t len = fread(buffer, 1, sizeof(buffer) - 1, f);
    fclose(f);
    
    if (len == 0) return false;
    buffer[len] = '\0';
    
    // Extract fields
    strncpy(info->doc_id, doc_id, UUID_LEN);
    info->doc_id[UUID_LEN] = '\0';
    
    if (!read_json_value(buffer, "visibleName", info->visible_name, 
                        sizeof(info->visible_name))) {
        strcpy(info->visible_name, "Untitled");
    }
    
    if (!read_json_value(buffer, "parent", info->parent, sizeof(info->parent))) {
        info->parent[0] = '\0';
    }
    
    // Handle "trash" as empty parent (document is in root)
    if (strcmp(info->parent, "trash") == 0) {
        info->parent[0] = '\0';
    }
    
    read_json_value(buffer, "type", info->type, sizeof(info->type));
    
    return true;
}

/**
 * build_path_recursive - Recursively build path from child to root
 * 
 * @param doc_id: Current document/folder UUID
 * @param path_parts: Array to store path components
 * @param depth: Current recursion depth
 * @param max_depth: Maximum allowed depth
 * @return: Number of path components found
 */
static int build_path_recursive(const char* doc_id, char path_parts[][256], 
                               int depth, int max_depth) {
    if (!doc_id || !*doc_id || depth >= max_depth) {
        return depth;
    }
    
    metadata_info_t info;
    if (!read_metadata_file(doc_id, &info)) {
        return depth;
    }
    
    // Store this component's name
    strcpy(path_parts[depth], info.visible_name);
    
    // If there's a parent, recurse
    if (info.parent[0] != '\0') {
        return build_path_recursive(info.parent, path_parts, depth + 1, max_depth);
    }
    
    return depth + 1;
}

/**
 * reconstruct_virtual_path - Reconstruct the full virtual path for a document
 * 
 * @param doc_id: Document UUID
 * @param page_num: Page number within document (can be NULL)
 * @param info: Output path information
 * @return: 0 on success, -1 on error
 * 
 * This function traverses the parent chain from the document up to the root,
 * building the complete virtual path as seen in the reMarkable UI
 */
int reconstruct_virtual_path(const char* doc_id, const char* page_num, 
                            path_info_t* info) {
    if (!doc_id || !info) return -1;
    
    memset(info, 0, sizeof(path_info_t));
    
    // Read the document's metadata
    metadata_info_t doc_meta;
    if (!read_metadata_file(doc_id, &doc_meta)) {
        return -1;
    }
    
    // Store document name
    strncpy(info->document_name, doc_meta.visible_name, 
            sizeof(info->document_name) - 1);
    
    // Build path from document to root
    char path_parts[MAX_PATH_DEPTH][256];
    int num_parts = 0;
    
    if (doc_meta.parent[0] != '\0') {
        num_parts = build_path_recursive(doc_meta.parent, path_parts, 0, MAX_PATH_DEPTH);
    }
    
    // Construct full path (parts are in reverse order)
    info->full_path[0] = '\0';
    for (int i = num_parts - 1; i >= 0; i--) {
        if (info->full_path[0] != '\0') {
            strcat(info->full_path, "/");
        }
        strcat(info->full_path, path_parts[i]);
    }
    
    // Add document name to path
    if (info->full_path[0] != '\0') {
        strcat(info->full_path, "/");
    }
    strcat(info->full_path, doc_meta.visible_name);
    
    // Add page name if provided
    if (page_num && *page_num) {
        snprintf(info->page_name, sizeof(info->page_name), "Page %s", page_num);
    } else {
        info->page_name[0] = '\0';
    }
    
    return 0;
}

/**
 * parse_content_file_fixed - Parse .content file to get page numbers
 *
 * @param doc_id: Document UUID
 * @param page_uuid: Page UUID to look for
 * @param page_num: Output buffer for page number
 * @param page_num_size: Size of output buffer
 * @return: true if found, false otherwise
 *
 * The .content file contains a JSON array of pages. Each page has an ID.
 * The page number is determined by the position in the array (0-based index).
 *
 * Example content structure:
 * {
 *   "pages": [
 *     {"id": "uuid1", ...},  // This is page 0
 *     {"id": "uuid2", ...},  // This is page 1
 *     {"id": "uuid3", ...}   // This is page 2
 *   ]
 * }
 */
bool parse_content_file(const char* doc_id, const char* page_uuid,
    char* page_num, size_t page_num_size) {
    char path[PATH_MAX];
    snprintf(path, sizeof(path), "%s/%s.content", XOCHITL_PATH, doc_id);

    FILE* f = fopen(path, "r");
    if (!f) {
        // No content file - this might be a single-page document
        // Default to page 1
        snprintf(page_num, page_num_size, "1");
        return true;
    }

    // Read entire file
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);

    if (size <= 0 || size > 1024 * 1024) { // Sanity check: max 1MB
        fclose(f);
        snprintf(page_num, page_num_size, "1");
        return true;
    }

    char* buffer = malloc(size + 1);
    if (!buffer) {
        fclose(f);
        return false;
    }

    if (fread(buffer, 1, size, f) != (size_t)size) {
        free(buffer);
        fclose(f);
        return false;
    }
    buffer[size] = '\0';
    fclose(f);

    // Find the "pages" array
    const char* pages_start = strstr(buffer, "\"pages\"");
    if (!pages_start) {
        // No pages array - might be an old format or single page
        free(buffer);
        snprintf(page_num, page_num_size, "1");
        return true;
    }

    // Find the opening bracket of the array
    const char* array_start = strchr(pages_start, '[');
    if (!array_start) {
        free(buffer);
        snprintf(page_num, page_num_size, "1");
        return true;
    }

    // Count pages and find our UUID
    int page_index = 0;
    bool found = false;
    const char* p = array_start + 1;

    while (*p && *p != ']') {
        // Skip whitespace
        while (*p && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;

        if (*p == '{') {
            // Found a page object
            // Look for the id field within this object
            const char* obj_start = p;
            int brace_count = 1;
            const char* obj_end = p + 1;

            // Find the end of this object
            while (*obj_end && brace_count > 0) {
                if (*obj_end == '{') brace_count++;
                else if (*obj_end == '}') brace_count--;
                obj_end++;
            }

            // Now search for our UUID within this object
            if (obj_end > obj_start) {
                // Create a temporary string for this object
                size_t obj_len = obj_end - obj_start;
                char* obj_str = malloc(obj_len + 1);
                if (obj_str) {
                    memcpy(obj_str, obj_start, obj_len);
                    obj_str[obj_len] = '\0';

                    // Check if this object contains our UUID
                    if (strstr(obj_str, page_uuid)) {
                        // Found it! The page number is the current index + 1
                        snprintf(page_num, page_num_size, "%d", page_index + 1);
                        found = true;
                        free(obj_str);
                        break;
                    }
                    free(obj_str);
                }
            }

            // Move to the end of this object
            p = obj_end;
            page_index++;

            // Skip comma if present
            while (*p && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
            if (*p == ',') p++;
        }
        else {
            // Unexpected character, move forward
            p++;
        }
    }

    free(buffer);

    if (!found) {
        // UUID not found in pages array - default to page 1
        snprintf(page_num, page_num_size, "1");
    }

    return true;
}

/**
 * scan_all_document_pages - Helper to get all pages with proper numbering
 *
 * This function scans a document directory and assigns page numbers
 * based on the order in the .content file
 */
int scan_all_document_pages(const char* doc_id, CacheHandle* cache) {
    char content_path[PATH_MAX];
    snprintf(content_path, sizeof(content_path), "%s/%s.content",
        XOCHITL_PATH, doc_id);

    FILE* f = fopen(content_path, "r");
    if (!f) {
        // No content file - treat as single page document
        return 0;
    }

    // Read entire file
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);

    if (size <= 0 || size > 1024 * 1024) {
        fclose(f);
        return 0;
    }

    char* buffer = malloc(size + 1);
    if (!buffer) {
        fclose(f);
        return 0;
    }

    if (fread(buffer, 1, size, f) != (size_t)size) {
        free(buffer);
        fclose(f);
        return 0;
    }
    buffer[size] = '\0';
    fclose(f);

    // Parse the pages array and extract UUIDs in order
    const char* pages_start = strstr(buffer, "\"pages\"");
    if (!pages_start) {
        free(buffer);
        return 0;
    }

    const char* array_start = strchr(pages_start, '[');
    if (!array_start) {
        free(buffer);
        return 0;
    }

    int page_number = 1;
    const char* p = array_start + 1;

    while (*p && *p != ']') {
        // Look for "id" field
        const char* id_pos = strstr(p, "\"id\"");
        if (!id_pos) break;

        // Find the colon
        const char* colon = strchr(id_pos, ':');
        if (!colon) break;

        // Find the opening quote of the UUID
        const char* quote1 = strchr(colon, '"');
        if (!quote1) break;
        quote1++;

        // Find the closing quote
        const char* quote2 = strchr(quote1, '"');
        if (!quote2) break;

        // Extract the UUID
        size_t uuid_len = quote2 - quote1;
        if (uuid_len == UUID_LEN) {
            char page_uuid[UUID_LEN + 1];
            memcpy(page_uuid, quote1, UUID_LEN);
            page_uuid[UUID_LEN] = '\0';

            // Update the cache with the page number
            DocumentEntry* doc = cache_find_document(cache, doc_id);
            if (doc) {
                PageEntry* page = cache_find_page(doc, page_uuid);
                if (page) {
                    // Update page number
                    snprintf(page->page_num, sizeof(page->page_num),
                        "%d", page_number);
                    cache->dirty = true;
                }
            }

            page_number++;
        }

        // Move to next object
        p = quote2 + 1;

        // Find the next opening brace or end of array
        while (*p && *p != '{' && *p != ']') p++;
    }

    free(buffer);
    return page_number - 1;
}