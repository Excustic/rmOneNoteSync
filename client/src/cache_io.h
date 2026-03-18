// cache_io.h - Shared cache structures and I/O functions
#ifndef CACHE_IO_H
#define CACHE_IO_H

#include <stdbool.h>
#include <stdint.h>
#include <time.h>

#define CACHE_MAGIC 0x524D4348 // "RMCH" in hex
#define CACHE_VERSION 4        // Version 4: simple FIFO queue (no sync status)
#define UUID_LEN 36
#define MAX_PAGE_NUM_LEN 8
#define PATH_MAX 4096

/**
 * PageEntry - Represents a single page within a document
 */
typedef struct PageEntry {
  char uuid[UUID_LEN + 1];         // Page UUID
  char page_num[MAX_PAGE_NUM_LEN]; // Page number (idx from content file)
  time_t mtime;                    // Last modification time
  struct PageEntry *next;          // Next page in linked list
} PageEntry;

/**
 * DocumentEntry - Represents a document with multiple pages
 */
typedef struct DocumentEntry {
  char doc_id[UUID_LEN + 1];  // Document UUID
  PageEntry *pages;           // Linked list of pages
  struct DocumentEntry *next; // Next document in hash table bucket
} DocumentEntry;

/**
 * CacheHandle - Opaque handle for cache operations
 */
typedef struct CacheHandle {
  DocumentEntry **table; // Hash table of documents
  size_t table_size;     // Size of hash table
  bool dirty;            // Whether cache needs saving
  char path[PATH_MAX];   // Path to cache file
} CacheHandle;

/**
 * cache_open - Open or create a cache file
 *
 * @param path: Path to cache file
 * @return: Cache handle or NULL on error
 */
CacheHandle *cache_open(const char *path);

/**
 * cache_close - Close cache and free resources
 *
 * @param cache: Cache handle
 * @param save: Whether to save changes before closing
 */
void cache_close(CacheHandle *cache, bool save);

/**
 * cache_save - Save cache to disk
 *
 * @param cache: Cache handle
 * @return: 0 on success, -1 on error
 */
int cache_save(CacheHandle *cache);

/**
 * cache_find_document - Find a document by ID
 *
 * @param cache: Cache handle
 * @param doc_id: Document UUID
 * @return: Document entry or NULL if not found
 */
DocumentEntry *cache_find_document(CacheHandle *cache, const char *doc_id);

/**
 * cache_find_page - Find a page within a document
 *
 * @param doc: Document entry
 * @param page_uuid: Page UUID
 * @return: Page entry or NULL if not found
 */
PageEntry *cache_find_page(DocumentEntry *doc, const char *page_uuid);

/**
 * cache_add_or_update_page - Add or update a page entry
 *
 * @param cache: Cache handle
 * @param doc_id: Document UUID
 * @param page_uuid: Page UUID
 * @param page_num: Page number (can be empty string)
 * @param mtime: Modification time
 * @return: 0 on success, -1 on error
 */
int cache_add_or_update_page(CacheHandle *cache, const char *doc_id,
                             const char *page_uuid, const char *page_num,
                             time_t mtime);

/**
 * cache_remove_page - Remove a page from the cache
 *
 * @param cache: Cache handle
 * @param doc_id: Document UUID
 * @param page_uuid: Page UUID
 * @return: 0 on success, -1 on error
 */
int cache_remove_page(CacheHandle *cache, const char *doc_id,
                      const char *page_uuid);

/**
 * cache_get_all_pages - Get list of all pages in the cache
 *
 * @param cache: Cache handle
 * @param max_pages: Maximum number of pages to return
 * @return: Array of page entries terminated by NULL (caller must free array)
 */
PageEntry **cache_get_all_pages(CacheHandle *cache, int max_pages);

/**
 * cache_count_pages - Count total pages in the cache
 *
 * @param cache: Cache handle
 * @return: Number of pages
 */
int cache_count_pages(CacheHandle *cache);

/**
 * cache_get_document_for_page - Find which document contains a page
 *
 * @param cache: Cache handle
 * @param page_uuid: Page UUID to search for
 * @return: Document ID or NULL if not found
 */
const char *cache_get_document_for_page(CacheHandle *cache,
                                        const char *page_uuid);

/**
 * cache_reload - Reload cache from disk to get latest changes
 *
 * @param cache: Cache handle
 * @return: 0 on success, -1 on error
 */
int cache_reload(CacheHandle *cache);

#endif // CACHE_IO_H