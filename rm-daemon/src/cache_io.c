// cache_io.c - Binary cache I/O implementation
#include "cache_io.h"
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <time.h>
#include <unistd.h>

#define HASH_TABLE_SIZE 256

/**
 * hash_string - Simple hash function for document IDs
 *
 * @param str: String to hash
 * @return: Hash value for table indexing
 */
static unsigned int hash_string(const char *str) {
  unsigned int hash = 5381;
  int c;
  while ((c = *str++)) {
    hash = ((hash << 5) + hash) + c;
  }
  return hash % HASH_TABLE_SIZE;
}

/**
 * read_pages_from_file - Read page entries from a cache file
 */
static PageEntry *read_pages_from_file(FILE *f, uint16_t num_pages) {
  PageEntry *first_page = NULL;
  PageEntry *last_page = NULL;

  for (uint16_t j = 0; j < num_pages; j++) {
    PageEntry *page = calloc(1, sizeof(PageEntry));
    if (!page)
      break;

    if (fread(page->uuid, UUID_LEN, 1, f) != 1) {
      free(page);
      break;
    }
    page->uuid[UUID_LEN] = '\0';

    uint8_t page_num_len;
    if (fread(&page_num_len, sizeof(page_num_len), 1, f) != 1) {
      free(page);
      break;
    }

    if (page_num_len >= MAX_PAGE_NUM_LEN) {
      free(page);
      break;
    }

    if (page_num_len > 0) {
      if (fread(page->page_num, page_num_len, 1, f) != 1) {
        free(page);
        break;
      }
      page->page_num[page_num_len] = '\0';
    }

    if (fread(&page->mtime, sizeof(page->mtime), 1, f) != 1) {
      free(page);
      break;
    }

    // Add to linked list
    if (last_page) {
      last_page->next = page;
    } else {
      first_page = page;
    }
    last_page = page;
  }

  return first_page;
}

/**
 * cache_open - Open or create a cache file
 *
 * @param path: Path to cache file
 * @return: Cache handle or NULL on error
 */
CacheHandle *cache_open(const char *path) {
  CacheHandle *cache = calloc(1, sizeof(CacheHandle));
  if (!cache)
    return NULL;

  // Initialize hash table
  cache->table = calloc(HASH_TABLE_SIZE, sizeof(DocumentEntry *));
  if (!cache->table) {
    free(cache);
    return NULL;
  }

  cache->table_size = HASH_TABLE_SIZE;
  strncpy(cache->path, path, PATH_MAX - 1);
  cache->path[PATH_MAX - 1] = '\0';
  cache->dirty = false;

  // Try to load existing cache
  FILE *f = fopen(path, "rb");
  if (!f) {
    // No existing cache, that's OK
    return cache;
  }

  // Read and verify header
  uint32_t magic, num_docs;
  uint8_t version;

  if (fread(&magic, sizeof(magic), 1, f) != 1 || magic != CACHE_MAGIC) {
    fclose(f);
    return cache; // Invalid cache, start fresh
  }

  if (fread(&version, sizeof(version), 1, f) != 1) {
    fclose(f);
    return cache;
  }

  if (version != CACHE_VERSION) {
    fclose(f);
    return cache; // Incompatible version, start fresh
  }

  if (fread(&num_docs, sizeof(num_docs), 1, f) != 1) {
    fclose(f);
    return cache;
  }

  // Read documents
  for (uint32_t i = 0; i < num_docs; i++) {
    uint8_t doc_id_len;
    if (fread(&doc_id_len, sizeof(doc_id_len), 1, f) != 1)
      break;

    if (doc_id_len != UUID_LEN)
      break;

    DocumentEntry *doc = calloc(1, sizeof(DocumentEntry));
    if (!doc)
      break;

    if (fread(doc->doc_id, doc_id_len, 1, f) != 1) {
      free(doc);
      break;
    }
    doc->doc_id[doc_id_len] = '\0';

    uint16_t num_pages;
    if (fread(&num_pages, sizeof(num_pages), 1, f) != 1) {
      free(doc);
      break;
    }

    // Read pages
    doc->pages = read_pages_from_file(f, num_pages);

    // Only add document if it has pages
    if (doc->pages) {
      unsigned int hash = hash_string(doc->doc_id);
      doc->next = cache->table[hash];
      cache->table[hash] = doc;
    } else {
      free(doc);
    }
  }

  fclose(f);

  return cache;
}

/**
 * cache_close - Close cache and free resources
 *
 * @param cache: Cache handle
 * @param save: Whether to save changes before closing
 */
void cache_close(CacheHandle *cache, bool save) {
  if (!cache)
    return;

  if (save && cache->dirty) {
    cache_save(cache);
  }

  // Free all documents and pages
  for (size_t i = 0; i < cache->table_size; i++) {
    DocumentEntry *doc = cache->table[i];
    while (doc) {
      DocumentEntry *next_doc = doc->next;

      // Free pages
      PageEntry *page = doc->pages;
      while (page) {
        PageEntry *next_page = page->next;
        free(page);
        page = next_page;
      }

      free(doc);
      doc = next_doc;
    }
  }

  free(cache->table);
  free(cache);
}

/**
 * cache_save - Save cache with file locking
 */
int cache_save(CacheHandle *cache) {
  if (!cache || !cache->dirty)
    return 0;

  // Write to temporary file first
  char temp_path[PATH_MAX];
  snprintf(temp_path, sizeof(temp_path), "%s.tmp", cache->path);

  FILE *f = fopen(temp_path, "wb");
  if (!f)
    return -1;

  // Use exclusive lock for writing
  int fd = fileno(f);
  flock(fd, LOCK_EX);

  // Count documents
  uint32_t num_docs = 0;
  for (size_t i = 0; i < cache->table_size; i++) {
    for (DocumentEntry *doc = cache->table[i]; doc; doc = doc->next) {
      num_docs++;
    }
  }

  // Write header
  uint32_t magic = CACHE_MAGIC;
  uint8_t version = CACHE_VERSION;
  fwrite(&magic, sizeof(magic), 1, f);
  fwrite(&version, sizeof(version), 1, f);
  fwrite(&num_docs, sizeof(num_docs), 1, f);

  // Write documents
  for (size_t i = 0; i < cache->table_size; i++) {
    for (DocumentEntry *doc = cache->table[i]; doc; doc = doc->next) {
      uint8_t doc_id_len = UUID_LEN;
      fwrite(&doc_id_len, sizeof(doc_id_len), 1, f);
      fwrite(doc->doc_id, doc_id_len, 1, f);

      // Count pages
      uint16_t num_pages = 0;
      for (PageEntry *page = doc->pages; page; page = page->next) {
        num_pages++;
      }
      fwrite(&num_pages, sizeof(num_pages), 1, f);

      // Write pages
      for (PageEntry *page = doc->pages; page; page = page->next) {
        fwrite(page->uuid, UUID_LEN, 1, f);

        uint8_t page_num_len = strlen(page->page_num);
        fwrite(&page_num_len, sizeof(page_num_len), 1, f);
        if (page_num_len > 0) {
          fwrite(page->page_num, page_num_len, 1, f);
        }

        fwrite(&page->mtime, sizeof(page->mtime), 1, f);
      }
    }
  }

  flock(fd, LOCK_UN); // Release lock
  fclose(f);

  // Atomic rename
  if (rename(temp_path, cache->path) != 0) {
    unlink(temp_path);
    return -1;
  }

  cache->dirty = false;
  return 0;
}

/**
 * cache_find_document - Find a document by ID
 */
DocumentEntry *cache_find_document(CacheHandle *cache, const char *doc_id) {
  if (!cache || !doc_id)
    return NULL;

  unsigned int hash = hash_string(doc_id);
  DocumentEntry *doc = cache->table[hash];

  while (doc) {
    if (strcmp(doc->doc_id, doc_id) == 0) {
      return doc;
    }
    doc = doc->next;
  }

  return NULL;
}

/**
 * cache_find_page - Find a page within a document
 */
PageEntry *cache_find_page(DocumentEntry *doc, const char *page_uuid) {
  if (!doc || !page_uuid)
    return NULL;

  PageEntry *page = doc->pages;
  while (page) {
    if (strcmp(page->uuid, page_uuid) == 0) {
      return page;
    }
    page = page->next;
  }

  return NULL;
}

/**
 * cache_add_or_update_page - Add or update a page entry
 */
int cache_add_or_update_page(CacheHandle *cache, const char *doc_id,
                             const char *page_uuid, const char *page_num,
                             time_t mtime) {
  if (!cache || !doc_id || !page_uuid)
    return -1;

  // Find or create document
  DocumentEntry *doc = cache_find_document(cache, doc_id);
  if (!doc) {
    doc = calloc(1, sizeof(DocumentEntry));
    if (!doc)
      return -1;

    strncpy(doc->doc_id, doc_id, UUID_LEN);
    doc->doc_id[UUID_LEN] = '\0';

    // Add to hash table
    unsigned int hash = hash_string(doc_id);
    doc->next = cache->table[hash];
    cache->table[hash] = doc;
  }

  // Find or create page
  PageEntry *page = cache_find_page(doc, page_uuid);
  if (!page) {
    page = calloc(1, sizeof(PageEntry));
    if (!page)
      return -1;

    strncpy(page->uuid, page_uuid, UUID_LEN);
    page->uuid[UUID_LEN] = '\0';

    // Add to linked list
    page->next = doc->pages;
    doc->pages = page;
  }

  // Update page data
  if (page_num) {
    strncpy(page->page_num, page_num, MAX_PAGE_NUM_LEN - 1);
    page->page_num[MAX_PAGE_NUM_LEN - 1] = '\0';
  }
  page->mtime = mtime;

  cache->dirty = true;
  return 0;
}

/**
 * cache_remove_page - Remove a page from the cache
 */
int cache_remove_page(CacheHandle *cache, const char *doc_id,
                      const char *page_uuid) {
  if (!cache || !doc_id || !page_uuid)
    return -1;

  DocumentEntry *doc = cache_find_document(cache, doc_id);
  if (!doc)
    return -1;

  PageEntry *prev = NULL;
  PageEntry *page = doc->pages;

  while (page) {
    if (strcmp(page->uuid, page_uuid) == 0) {
      if (prev) {
        prev->next = page->next;
      } else {
        doc->pages = page->next;
      }
      free(page);
      cache->dirty = true;

      // Clean up empty documents from the hash table
      if (!doc->pages) {
        unsigned int hash = hash_string(doc_id);
        DocumentEntry *prev_doc = NULL;
        DocumentEntry *curr_doc = cache->table[hash];
        while (curr_doc) {
          if (curr_doc == doc) {
            if (prev_doc) {
              prev_doc->next = curr_doc->next;
            } else {
              cache->table[hash] = curr_doc->next;
            }
            free(doc);
            break;
          }
          prev_doc = curr_doc;
          curr_doc = curr_doc->next;
        }
      }
      return 0;
    }
    prev = page;
    page = page->next;
  }

  return -1;
}

/**
 * cache_get_all_pages - Get list of all pages in the cache
 */
PageEntry **cache_get_all_pages(CacheHandle *cache, int max_pages) {
  if (!cache || max_pages <= 0)
    return NULL;

  PageEntry **results = calloc(max_pages + 1, sizeof(PageEntry *));
  if (!results)
    return NULL;

  int count = 0;

  for (size_t i = 0; i < cache->table_size && count < max_pages; i++) {
    for (DocumentEntry *doc = cache->table[i]; doc && count < max_pages;
         doc = doc->next) {
      for (PageEntry *page = doc->pages; page && count < max_pages;
           page = page->next) {
        results[count++] = page;
      }
    }
  }

  return results;
}

/**
 * cache_count_pages - Count total pages in the cache
 */
int cache_count_pages(CacheHandle *cache) {
  if (!cache)
    return 0;

  int count = 0;

  for (size_t i = 0; i < cache->table_size; i++) {
    for (DocumentEntry *doc = cache->table[i]; doc; doc = doc->next) {
      for (PageEntry *page = doc->pages; page; page = page->next) {
        count++;
      }
    }
  }

  return count;
}

/**
 * cache_get_document_for_page - Find which document contains a page
 */
const char *cache_get_document_for_page(CacheHandle *cache,
                                        const char *page_uuid) {
  if (!cache || !page_uuid)
    return NULL;

  for (size_t i = 0; i < cache->table_size; i++) {
    for (DocumentEntry *doc = cache->table[i]; doc; doc = doc->next) {
      for (PageEntry *page = doc->pages; page; page = page->next) {
        if (strcmp(page->uuid, page_uuid) == 0) {
          return doc->doc_id;
        }
      }
    }
  }

  return NULL;
}

/**
 * cache_reload - Reload cache from disk to get latest changes
 */
int cache_reload(CacheHandle *cache) {
  if (!cache)
    return -1;

  // Clear existing cache entries
  for (size_t i = 0; i < cache->table_size; i++) {
    DocumentEntry *doc = cache->table[i];
    while (doc) {
      DocumentEntry *next_doc = doc->next;

      PageEntry *page = doc->pages;
      while (page) {
        PageEntry *next_page = page->next;
        free(page);
        page = next_page;
      }

      free(doc);
      doc = next_doc;
    }
    cache->table[i] = NULL;
  }

  // Reload from file
  FILE *f = fopen(cache->path, "rb");
  if (!f) {
    cache->dirty = false;
    return 0;
  }

  // Use file locking to ensure we don't read while another process is writing
  int fd = fileno(f);
  flock(fd, LOCK_SH);

  // Read and verify header
  uint32_t magic, num_docs;
  uint8_t version;

  if (fread(&magic, sizeof(magic), 1, f) != 1 || magic != CACHE_MAGIC) {
    flock(fd, LOCK_UN);
    fclose(f);
    return -1;
  }

  if (fread(&version, sizeof(version), 1, f) != 1) {
    flock(fd, LOCK_UN);
    fclose(f);
    return -1;
  }

  if (version != CACHE_VERSION) {
    flock(fd, LOCK_UN);
    fclose(f);
    return -1;
  }

  if (fread(&num_docs, sizeof(num_docs), 1, f) != 1) {
    flock(fd, LOCK_UN);
    fclose(f);
    return -1;
  }

  // Read documents
  for (uint32_t i = 0; i < num_docs; i++) {
    uint8_t doc_id_len;
    if (fread(&doc_id_len, sizeof(doc_id_len), 1, f) != 1)
      break;

    if (doc_id_len != UUID_LEN)
      break;

    DocumentEntry *doc = calloc(1, sizeof(DocumentEntry));
    if (!doc)
      break;

    if (fread(doc->doc_id, doc_id_len, 1, f) != 1) {
      free(doc);
      break;
    }
    doc->doc_id[doc_id_len] = '\0';

    uint16_t num_pages;
    if (fread(&num_pages, sizeof(num_pages), 1, f) != 1) {
      free(doc);
      break;
    }

    // Read pages
    doc->pages = read_pages_from_file(f, num_pages);

    // Only add document if it has pages
    if (doc->pages) {
      unsigned int hash = hash_string(doc->doc_id);
      doc->next = cache->table[hash];
      cache->table[hash] = doc;
    } else {
      free(doc);
    }
  }

  flock(fd, LOCK_UN);
  fclose(f);

  cache->dirty = false;
  return 0;
}