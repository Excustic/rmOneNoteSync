# Makefile for reMarkable Sync Client
# 
# Usage:
#   source /opt/codex/ferrari/4.3.98/environment-setup-cortexa53-crypto-remarkable-linux
#   make all

# Source directories
SRC_DIR = src
TOOLS_DIR = testing_tools
BUILD_DIR = build

# The cross-compiler will be set by the environment script
CC ?= $(CC)

# Source files (ONLY define these once!)
WATCHER_SRCS    = $(SRC_DIR)/watcher.c $(SRC_DIR)/cache_io.c $(SRC_DIR)/metadata_parser.c
HTTPCLIENT_SRCS = $(SRC_DIR)/httpclient.c $(SRC_DIR)/cache_io.c $(SRC_DIR)/metadata_parser.c $(SRC_DIR)/http_simple.c $(SRC_DIR)/httpserver.c
DEBUG_SRCS      = $(TOOLS_DIR)/cache_debug.c

# Output binaries
WATCHER_BIN    = $(BUILD_DIR)/watcher
HTTPCLIENT_BIN = $(BUILD_DIR)/httpclient
DEBUG_BIN      = $(BUILD_DIR)/cache_debug

# Build flags
CFLAGS = -Wall -O2 -g -pthread
LDFLAGS = -pthread

# Ensure the build directory exists
$(BUILD_DIR):
	mkdir -p $(BUILD_DIR)

# Default target
all: $(BUILD_DIR) $(WATCHER_BIN) $(HTTPCLIENT_BIN) $(DEBUG_BIN)
	@echo "===================================="
	@echo "Build complete!"
	@echo "===================================="

# Build rules
$(WATCHER_BIN): $(WATCHER_SRCS)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(WATCHER_SRCS)
	@echo "Built: $@"

$(HTTPCLIENT_BIN): $(HTTPCLIENT_SRCS)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(HTTPCLIENT_SRCS)
	@echo "Built: $@"

$(DEBUG_BIN): $(DEBUG_SRCS)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(DEBUG_SRCS)
	@echo "Built: $@"

clean:
	rm -rf $(BUILD_DIR)
	@echo "Cleaned build artifacts"

.PHONY: all clean