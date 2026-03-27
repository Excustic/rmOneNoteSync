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
#include <unistd.h>
#include <dirent.h>

#ifndef PATH_MAX
#define PATH_MAX 4096
#endif

static CacheHandle *g_cache = NULL;
static int *g_w_count = NULL;
static char (*g_w_list)[UUID_STR_LEN + 1] = NULL;
static volatile bool g_running = false;
static pthread_t g_thread;
static int g_server_fd = -1;

void httpserver_init(CacheHandle *active_cache, int *w_count,
                     char (*w_list)[UUID_STR_LEN + 1]) {
  g_cache = active_cache;
  g_w_count = w_count;
  g_w_list = w_list;
}

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

static void handle_whitelist(int client_fd) {
  char *json = malloc(8192);
  if (!json) {
    send_http_response(client_fd, 500, "application/json",
                       "{\"error\":\"memory error\"}");
    return;
  }

  strcpy(json, "{\n  \"whitelist\": [\n");
  int count = g_w_count ? *g_w_count : 0;
  for (int i = 0; i < count; i++) {
    char line[128];
    snprintf(line, sizeof(line), "    \"%s\"%s\n", g_w_list[i],
             (i < count - 1) ? "," : "");
    strcat(json, line);
  }
  strcat(json, "  ]\n}\n");

  send_http_response(client_fd, 200, "application/json", json);
  free(json);
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
    char* body = strstr(req_buffer, "\r\n\r\n");
    if (!body) {
        send_http_response(client_fd, 400, "text/plain", "Bad Request");
        return;
    }
    body += 4;

    int content_length = 0;
    char* cl_ptr = strstr(req_buffer, "Content-Length:");
    if (!cl_ptr) cl_ptr = strstr(req_buffer, "content-length:");
    if (cl_ptr) {
        content_length = atoi(cl_ptr + 15);
    }

    if (content_length <= 0 || content_length > 1024 * 1024) {
        send_http_response(client_fd, 400, "text/plain", "Invalid Content-Length");
        return;
    }

    int header_length = body - req_buffer;
    int body_received = received_so_far - header_length;

    char* full_body = malloc(content_length + 1);
    if (!full_body) {
        send_http_response(client_fd, 500, "text/plain", "Out of memory");
        return;
    }

    int to_copy = body_received > content_length ? content_length : body_received;
    memcpy(full_body, body, to_copy);
    int total_body_recv = to_copy;

    while (total_body_recv < content_length) {
        int r = recv(client_fd, full_body + total_body_recv, content_length - total_body_recv, 0);
        if (r <= 0) break;
        total_body_recv += r;
    }
    full_body[total_body_recv] = '\0';

    FILE* f = fopen("/home/root/onenote-sync/httpclient.conf", "w");
    if (f) {
        fwrite(full_body, 1, total_body_recv, f);
        fclose(f);
        send_http_response(client_fd, 200, "application/json", "{\"status\":\"success\"}");
    } else {
        send_http_response(client_fd, 500, "application/json", "{\"error\":\"failed to write config\"}");
    }
    free(full_body);
}

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

      if (strncmp(req, "GET /whitelist", 14) == 0) {
        handle_whitelist(client);
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
  if (pthread_create(&g_thread, NULL, httpserver_loop, p) != 0) {
    return -1;
  }
  return 0;
}

void httpserver_stop(void) {
  g_running = false;
  pthread_join(g_thread, NULL);
}
