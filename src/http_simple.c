// http_simple_fixed.c - Fixed HTTP client with proper file upload
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netdb.h>
#include <fcntl.h>
#include <errno.h>
#include <time.h>
#include "http_simple.h"

#define BUFFER_SIZE 4096
#define MAX_HEADERS 32

/**
 * parse_url - Extract host, port, and path from URL
 */
static int parse_url(const char* url, char* host, int* port, char* path) {
    const char* p = url;
    if (strncmp(p, "http://", 7) == 0) {
        p += 7;
    } else {
        return -1;
    }
    
    const char* slash = strchr(p, '/');
    const char* colon = strchr(p, ':');
    
    if (colon && (!slash || colon < slash)) {
        size_t host_len = colon - p;
        if (host_len >= 256) return -1;
        strncpy(host, p, host_len);
        host[host_len] = '\0';
        
        *port = atoi(colon + 1);
        if (*port <= 0 || *port > 65535) return -1;
        
        p = slash ? slash : colon + strlen(colon + 1);
    } else {
        *port = 80;
        
        if (slash) {
            size_t host_len = slash - p;
            if (host_len >= 256) return -1;
            strncpy(host, p, host_len);
            host[host_len] = '\0';
            p = slash;
        } else {
            strcpy(host, p);
            p = p + strlen(p);
        }
    }
    
    strcpy(path, *p ? p : "/");
    return 0;
}

/**
 * connect_to_server - Create socket and connect to HTTP server
 */
static int connect_to_server(const char* host, int port, int timeout_sec) {
    struct hostent* server;
    struct sockaddr_in serv_addr;
    int sockfd;
    
    sockfd = socket(AF_INET, SOCK_STREAM, 0);
    if (sockfd < 0) {
        return -1;
    }
    
    struct timeval tv;
    tv.tv_sec = timeout_sec;
    tv.tv_usec = 0;
    setsockopt(sockfd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(sockfd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
    
    server = gethostbyname(host);
    if (server == NULL) {
        close(sockfd);
        return -1;
    }
    
    memset(&serv_addr, 0, sizeof(serv_addr));
    serv_addr.sin_family = AF_INET;
    memcpy(&serv_addr.sin_addr.s_addr, server->h_addr, server->h_length);
    serv_addr.sin_port = htons(port);
    
    if (connect(sockfd, (struct sockaddr*)&serv_addr, sizeof(serv_addr)) < 0) {
        close(sockfd);
        return -1;
    }
    
    return sockfd;
}

/**
 * read_http_response - Read HTTP response from socket
 */
static int read_http_response(int sockfd, http_response_t* response) {
    char buffer[BUFFER_SIZE];
    size_t total_read = 0;
    size_t capacity = BUFFER_SIZE;
    char* data = malloc(capacity);
    
    if (!data) return -1;
    
    ssize_t n;
    while ((n = read(sockfd, buffer, BUFFER_SIZE)) > 0) {
        if (total_read + n > capacity) {
            capacity *= 2;
            char* new_data = realloc(data, capacity);
            if (!new_data) {
                free(data);
                return -1;
            }
            data = new_data;
        }
        
        memcpy(data + total_read, buffer, n);
        total_read += n;
    }
    
    if (total_read < 12) {
        free(data);
        return -1;
    }
    
    char* status_start = strchr(data, ' ');
    if (!status_start) {
        free(data);
        return -1;
    }
    response->status_code = atoi(status_start + 1);
    
    char* body_start = strstr(data, "\r\n\r\n");
    if (body_start) {
        body_start += 4;
        response->body_size = total_read - (body_start - data);
        response->body = malloc(response->body_size + 1);
        if (response->body) {
            memcpy(response->body, body_start, response->body_size);
            response->body[response->body_size] = '\0';
        }
    } else {
        response->body = NULL;
        response->body_size = 0;
    }
    
    free(data);
    return 0;
}

/**
 * http_get - Perform HTTP GET request
 */
int http_get(const char* url, http_response_t* response) {
    char host[256];
    char path[1024];
    int port;
    
    if (parse_url(url, host, &port, path) < 0) {
        return -1;
    }
    
    int sockfd = connect_to_server(host, port, 10);
    if (sockfd < 0) {
        return -1;
    }
    
    char request[2048];
    snprintf(request, sizeof(request),
        "GET %s HTTP/1.1\r\n"
        "Host: %s\r\n"
        "User-Agent: RemarkableSyncClient/1.1\r\n"
        "Connection: close\r\n"
        "\r\n",
        path, host);
    
    if (write(sockfd, request, strlen(request)) < 0) {
        close(sockfd);
        return -1;
    }
    
    int result = read_http_response(sockfd, response);
    close(sockfd);
    
    return result;
}

/**
 * http_post_file - Upload file via HTTP POST with custom headers
 * FIXED VERSION: Properly sends file content
 */
int http_post_file(const char* url, const char* api_key,
                   const char* file_path, const char* virtual_path,
                   const char* doc_id, http_response_t* response) {
    char host[256];
    char path[1024];
    int port;
    
    // Parse URL
    if (parse_url(url, host, &port, path) < 0) {
        fprintf(stderr, "Failed to parse URL: %s\n", url);
        return -1;
    }
    
    // Open and read file
    FILE* f = fopen(file_path, "rb");
    if (!f) {
        fprintf(stderr, "Cannot open file: %s\n", file_path);
        return -1;
    }
    
    // Get file size
    fseek(f, 0, SEEK_END);
    long file_size = ftell(f);
    fseek(f, 0, SEEK_SET);
    
    if (file_size <= 0 || file_size > 10*1024*1024) {
        fprintf(stderr, "Invalid file size: %ld\n", file_size);
        fclose(f);
        return -1;
    }
    
    // Read file content
    unsigned char* file_data = malloc(file_size);
    if (!file_data) {
        fprintf(stderr, "Cannot allocate memory for file\n");
        fclose(f);
        return -1;
    }
    
    size_t bytes_read = fread(file_data, 1, file_size, f);
    fclose(f);
    
    if (bytes_read != (size_t)file_size) {
        fprintf(stderr, "Failed to read complete file: %zu/%ld\n", bytes_read, file_size);
        free(file_data);
        return -1;
    }
    
    // Connect to server
    int sockfd = connect_to_server(host, port, 10);
    if (sockfd < 0) {
        fprintf(stderr, "Cannot connect to server %s:%d\n", host, port);
        free(file_data);
        return -1;
    }
    
    // Extract filename
    const char* filename = strrchr(file_path, '/');
    filename = filename ? filename + 1 : file_path;
    
    // Build headers
    char headers[2048];
    int header_len = snprintf(headers, sizeof(headers),
        "POST %s HTTP/1.1\r\n"
        "Host: %s\r\n"
        "User-Agent: RemarkableSyncClient/1.1\r\n"
        "X-API-Key: %s\r\n"
        "X-Document-Path: %s\r\n"
        "X-Document-Id: %s\r\n"
        "X-Filename: %s\r\n"
        "Content-Type: application/octet-stream\r\n"
        "Content-Length: %ld\r\n"
        "Connection: close\r\n"
        "\r\n",
        path, host, api_key, virtual_path, doc_id, filename, file_size);
    
    // Send headers
    ssize_t sent = write(sockfd, headers, header_len);
    if (sent != header_len) {
        fprintf(stderr, "Failed to send headers: %zd/%d\n", sent, header_len);
        close(sockfd);
        free(file_data);
        return -1;
    }
    
    // Send file data
    size_t total_sent = 0;
    while (total_sent < (size_t)file_size) {
        size_t chunk = file_size - total_sent;
        if (chunk > BUFFER_SIZE) chunk = BUFFER_SIZE;
        
        ssize_t n = write(sockfd, file_data + total_sent, chunk);
        if (n <= 0) {
            if (n < 0) {
                fprintf(stderr, "Write error: %s\n", strerror(errno));
            } else {
                fprintf(stderr, "Connection closed by server\n");
            }
            close(sockfd);
            free(file_data);
            return -1;
        }
        total_sent += n;
    }
    
    free(file_data);
    
    // Verify all data was sent
    if (total_sent != (size_t)file_size) {
        fprintf(stderr, "Incomplete send: %zu/%ld bytes\n", total_sent, file_size);
        close(sockfd);
        return -1;
    }
    
    // Read response
    int result = read_http_response(sockfd, response);
    close(sockfd);
    
    return result;
}

/**
 * http_response_free - Free response structure
 */
void http_response_free(http_response_t* response) {
    if (response && response->body) {
        free(response->body);
        response->body = NULL;
        response->body_size = 0;
    }
}