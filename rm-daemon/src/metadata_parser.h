// metadata_parser.h - Virtual path reconstruction from reMarkable metadata
#ifndef METADATA_PARSER_H
#define METADATA_PARSER_H

#include <stdbool.h>
#include <stddef.h>

#define UUID_LEN 36
#define PATH_MAX 4096

/**
 * metadata_info_t - Information extracted from a .metadata file
 */
typedef struct {
    char doc_id[UUID_LEN + 1];     // Document UUID
    char visible_name[256];         // Display name
    char parent[UUID_LEN + 1];      // Parent folder UUID (empty if root)
    char type[32];                  // Document type (DocumentType, CollectionType)
} metadata_info_t;

/**
 * path_info_t - Complete path information for a document/page
 */
typedef struct {
    char full_path[PATH_MAX];       // Full virtual path (e.g., "Shared Vault/Math/Calculus")
    char document_name[256];        // Document name (e.g., "Calculus Notes")
    char page_name[64];            // Page name (e.g., "Page 3")
} path_info_t;

/**
 * reconstruct_virtual_path - Reconstruct the full virtual path for a document
 * 
 * @param doc_id: Document UUID
 * @param page_num: Page number within document (can be NULL)
 * @param info: Output path information
 * @return: 0 on success, -1 on error
 * 
 * Example:
 *   path_info_t info;
 *   if (reconstruct_virtual_path("ab46df20-ea64-4e05-b1e2-6c47fa2d73c3", "3", &info) == 0) {
 *       printf("Full path: %s\n", info.full_path);
 *       printf("Document: %s\n", info.document_name);
 *       printf("Page: %s\n", info.page_name);
 *   }
 */
int reconstruct_virtual_path(const char* doc_id, const char* page_num, 
                            path_info_t* info);

/**
 * parse_content_file - Parse .content file to get page numbers
 * 
 * @param doc_id: Document UUID
 * @param page_uuid: Page UUID to look for
 * @param page_num: Output buffer for page number
 * @param page_num_size: Size of output buffer
 * @return: true if found, false otherwise
 * 
 * Extracts the "idx" field from the content file which corresponds
 * to the page number shown in the reMarkable UI
 */
bool parse_content_file(const char* doc_id, const char* page_uuid,
                       char* page_num, size_t page_num_size);

#endif // METADATA_PARSER_H