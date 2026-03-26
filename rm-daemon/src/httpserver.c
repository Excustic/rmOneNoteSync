#include "httpserver.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <sys/select.h>
#include <stdbool.h>

static CacheHandle* g_cache = NULL;
static int* g_w_count = NULL;
static char (*g_w_list)[UUID_STR_LEN + 1] = NULL;
static volatile bool g_running = false;
static pthread_t g_thread;
static int g_server_fd = -1;

void httpserver_init(CacheHandle* active_cache, int* w_count, char (*w_list)[UUID_STR_LEN + 1]) {
    g_cache = active_cache;
    g_w_count = w_count;
    g_w_list = w_list;
}

static void send_http_response(int client_fd, int status, const char* content_type, const char* body) {
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

static void handle_whitelist(int client_fd) {
    char* json = malloc(8192);
    if (!json) {
        send_http_response(client_fd, 500, "application/json", "{\"error\":\"memory error\"}");
        return;
    }
    
    strcpy(json, "{\n  \"whitelist\": [\n");
    int count = g_w_count ? *g_w_count : 0;
    for (int i = 0; i < count; i++) {
        char line[128];
        snprintf(line, sizeof(line), "    \"%s\"%s\n", g_w_list[i], (i < count - 1) ? "," : "");
        strcat(json, line);
    }
    strcat(json, "  ]\n}\n");
    
    send_http_response(client_fd, 200, "application/json", json);
    free(json);
}

static void handle_metadata(int client_fd, const char* doc_id) {
    char path[4096];
    snprintf(path, sizeof(path), "/home/root/.local/share/remarkable/xochitl/%s.metadata", doc_id);
    FILE* f = fopen(path, "r");
    char title[256] = "Untitled";
    if (f) {
        char buffer[4096];
        size_t len = fread(buffer, 1, sizeof(buffer)-1, f);
        buffer[len] = '\0';
        fclose(f);
        
        char* name_pos = strstr(buffer, "\"visibleName\"");
        if (name_pos) {
            char* colon = strchr(name_pos, ':');
            if (colon) {
                char* quote1 = strchr(colon, '"');
                if (quote1) {
                    quote1++;
                    char* quote2 = strchr(quote1, '"');
                    if (quote2) {
                        int tlen = quote2 - quote1;
                        if (tlen > 255) tlen = 255;
                        strncpy(title, quote1, tlen);
                        title[tlen] = '\0';
                    }
                }
            }
        }
    } else {
        send_http_response(client_fd, 404, "application/json", "{\"error\":\"metadata not found\"}");
        return;
    }

    snprintf(path, sizeof(path), "/home/root/.local/share/remarkable/xochitl/%s.content", doc_id);
    f = fopen(path, "r");
    char* pages_json = malloc(65536);
    strcpy(pages_json, "[\n");
    bool has_pages = false;

    if (f) {
        fseek(f, 0, SEEK_END);
        long size = ftell(f);
        fseek(f, 0, SEEK_SET);
        if (size > 0 && size < 1024*1024) {
            char* cbuf = malloc(size + 1);
            if (cbuf && fread(cbuf, 1, size, f) == (size_t)size) {
                cbuf[size] = '\0';
                char* pages_start = strstr(cbuf, "\"pages\"");
                if (pages_start) {
                    char* array_start = strchr(pages_start, '[');
                    if (array_start) {
                        char* p = array_start + 1;
                        while (*p && *p != ']') {
                            char* id_pos = strstr(p, "\"id\"");
                            if (!id_pos) break;
                            char* colon = strchr(id_pos, ':');
                            if (!colon) break;
                            char* q1 = strchr(colon, '"');
                            if (!q1) break;
                            q1++;
                            char* q2 = strchr(q1, '"');
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
    
    char* json = malloc(65536 + 1024);
    snprintf(json, 65536 + 1024, 
             "{\n"
             "  \"id\": \"%s\",\n"
             "  \"name\": \"%s\",\n"
             "  \"pages\": %s\n"
             "}\n", doc_id, title, pages_json);
             
    send_http_response(client_fd, 200, "application/json", json);
    free(pages_json);
    free(json);
}

static void handle_sync(int client_fd, const char* body) {
    if (!g_cache) {
        send_http_response(client_fd, 500, "application/json", "{\"error\":\"cache not initialized\"}");
        return;
    }
    
    char* doc_id_ptr = strstr((char*)body, "\"document_id\"");
    if (!doc_id_ptr) {
        send_http_response(client_fd, 400, "application/json", "{\"error\":\"missing document_id\"}");
        return;
    }
    
    char* doc_colon = strchr(doc_id_ptr, ':');
    if (!doc_colon) {
        send_http_response(client_fd, 400, "application/json", "{\"error\":\"invalid document_id\"}");
        return;
    }
    char* doc_q1 = strchr(doc_colon, '"');
    if (!doc_q1) {
        send_http_response(client_fd, 400, "application/json", "{\"error\":\"invalid document_id string\"}");
        return;
    }
    doc_q1++;
    char* doc_q2 = strchr(doc_q1, '"');
    if (!doc_q2 || (doc_q2 - doc_q1) != 36) {
        send_http_response(client_fd, 400, "application/json", "{\"error\":\"invalid document_id length\"}");
        return;
    }
    
    char doc_id[37];
    strncpy(doc_id, doc_q1, 36);
    doc_id[36] = '\0';
    
    char* pages_arr = strstr((char*)body, "\"pages\"");
    if (!pages_arr) {
        send_http_response(client_fd, 400, "application/json", "{\"error\":\"missing pages\"}");
        return;
    }
    
    char* arr_start = strchr(pages_arr, '[');
    if(!arr_start) {
        send_http_response(client_fd, 400, "application/json", "{\"error\":\"invalid pages array\"}");
        return;
    }
    
    char* p = arr_start + 1;
    int queued = 0;
    while (*p && *p != ']') {
        char* q1 = strchr(p, '"');
        if (!q1) break;
        q1++;
        char* q2 = strchr(q1, '"');
        if (!q2) break;
        
        if (q2 - q1 == 36) {
            char page_uuid[37];
            strncpy(page_uuid, q1, 36);
            page_uuid[36] = '\0';
            
            cache_add_or_update_page(g_cache, doc_id, page_uuid, "", time(NULL)); 
            queued++;
        }
        p = q2 + 1;
    }
    
    if (queued > 0) {
        cache_save(g_cache);
    }
    
    char resp[128];
    snprintf(resp, sizeof(resp), "{\"status\":\"success\", \"queued\": %d}", queued);
    send_http_response(client_fd, 200, "application/json", resp);
}

static void* httpserver_loop(void* arg) {
    int port = *(int*)arg;
    free(arg);
    
    g_server_fd = socket(AF_INET, SOCK_STREAM, 0);
    int opt = 1;
    setsockopt(g_server_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));
    
    struct sockaddr_in addr;
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(port);
    
    if (bind(g_server_fd, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        perror("bind failed");
        return NULL;
    }
    
    listen(g_server_fd, 5);
    
    while(g_running) {
        struct timeval tv;
        tv.tv_sec = 1;
        tv.tv_usec = 0;
        
        fd_set readfds;
        FD_ZERO(&readfds);
        FD_SET(g_server_fd, &readfds);
        
        int n = select(g_server_fd + 1, &readfds, NULL, NULL, &tv);
        if (n <= 0) continue;
        
        int client = accept(g_server_fd, NULL, NULL);
        if (client < 0) continue;
        
        char req[16384];
        int received = recv(client, req, sizeof(req)-1, 0);
        if (received > 0) {
            req[received] = '\0';
            
            if (strncmp(req, "GET /whitelist", 14) == 0) {
                handle_whitelist(client);
            } else if (strncmp(req, "GET /metadata?id=", 17) == 0) {
                char doc_id[37];
                char* id_start = req + 17;
                int i = 0;
                while(i < 36 && id_start[i] != ' ' && id_start[i] != '\0') {
                    doc_id[i] = id_start[i];
                    i++;
                }
                doc_id[i] = '\0';
                if (i == 36) {
                    handle_metadata(client, doc_id);
                } else {
                    send_http_response(client, 400, "application/json", "{\"error\":\"invalid id\"}");
                }
            } else if (strncmp(req, "POST /sync", 10) == 0) {
                char* separator = strstr(req, "\r\n\r\n");
                if (separator) {
                    handle_sync(client, separator + 4);
                } else {
                    send_http_response(client, 400, "application/json", "{\"error\":\"invalid body\"}");
                }
            } else {
                send_http_response(client, 404, "application/json", "{\"error\":\"not found\"}");
            }
        }
        close(client);
    }
    
    close(g_server_fd);
    return NULL;
}

int httpserver_start(int port) {
    g_running = true;
    int* p = malloc(sizeof(int));
    *p = port;
    if (pthread_create(&g_thread, NULL, httpserver_loop, p) != 0) {
        return -1;
    }
    return 0;
}

void httpserver_stop(void) {
    g_running = false;
    pthread_join(g_thread, NULL);
}
