#ifndef HTTPSERVER_H
#define HTTPSERVER_H

#include "cache_io.h"

#define UUID_STR_LEN 36

// Initialize the HTTP server with references to the cache and config whitelist
void httpserver_init(CacheHandle* active_cache, int* w_count, char (*w_list)[UUID_STR_LEN + 1]);

// Start the HTTP server on a specified port (e.g., 8000)
// Returns 0 on success, -1 on failure.
int httpserver_start(int port);

// Stop the HTTP server
void httpserver_stop(void);

#endif // HTTPSERVER_H
