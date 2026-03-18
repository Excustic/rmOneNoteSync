#!/bin/bash

# watcher_profiler.sh - Lightweight profiler for reMarkable watcher service
# Usage: ./watcher_profiler.sh [OPTIONS] [PID]

WATCHER_CMD="watcher"
WATCHER_PATH="/home/root/onenote-sync/watcher"
DEFAULT_INTERVAL=5
INTERVAL=$DEFAULT_INTERVAL
CONTINUOUS=0
QUIET=0
OUTPUT_FILE=""
MAX_SAMPLES=0
SAMPLE_COUNT=0

# Colors for output (if terminal supports it)
if [ -t 1 ]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    BLUE='\033[0;34m'
    CYAN='\033[0;36m'
    NC='\033[0m' # No Color
else
    RED=''
    GREEN=''
    YELLOW=''
    BLUE=''
    CYAN=''
    NC=''
fi

print_usage() {
    echo "Usage: $0 [OPTIONS] [PID]"
    echo ""
    echo "Monitor CPU and memory usage of the watcher service"
    echo ""
    echo "OPTIONS:"
    echo "  -h, --help          Show this help"
    echo "  -i, --interval SEC  Sampling interval in seconds (default: $DEFAULT_INTERVAL)"
    echo "  -c, --continuous    Run continuously until Ctrl+C"
    echo "  -n, --samples NUM   Take NUM samples then exit"
    echo "  -q, --quiet         Minimal output"
    echo "  -o, --output FILE   Save output to file"
    echo "  -p, --pid PID       Monitor specific PID"
    echo ""
    echo "EXAMPLES:"
    echo "  $0                          # Find and monitor watcher process"
    echo "  $0 -c -i 2                  # Continuous monitoring every 2 seconds"
    echo "  $0 -n 10 -o profile.log     # Take 10 samples, save to file"
    echo "  $0 -p 1234                  # Monitor specific PID"
    echo ""
}

log() {
    local msg="$1"
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S')
    
    if [ "$OUTPUT_FILE" != "" ]; then
        echo "[$timestamp] $msg" >> "$OUTPUT_FILE"
    fi
    
    if [ "$QUIET" -eq 0 ]; then
        echo -e "$msg"
    fi
}

find_watcher_pid() {
    # Try multiple methods to find the watcher process
    local pid=""
    
    # Method 1: Look for the exact command
    pid=$(ps aux | grep "$WATCHER_PATH" | grep -v grep | awk '{print $2}' | head -n1)
    
    if [ "$pid" = "" ]; then
        # Method 2: Look for any process with "watcher" in the name
        pid=$(ps aux | grep "$WATCHER_CMD" | grep -v grep | grep -v profiler | awk '{print $2}' | head -n1)
    fi
    
    if [ "$pid" = "" ]; then
        # Method 3: Look in /proc for processes with matching command
        for proc_dir in /proc/[0-9]*; do
            if [ -r "$proc_dir/comm" ]; then
                if grep -q "watcher" "$proc_dir/comm" 2>/dev/null; then
                    pid=$(basename "$proc_dir")
                    break
                fi
            fi
        done
    fi
    
    echo "$pid"
}

get_process_info() {
    local pid=$1
    local proc_stat="/proc/$pid/stat"
    local proc_status="/proc/$pid/status"
    
    if [ ! -r "$proc_stat" ] || [ ! -r "$proc_status" ]; then
        echo "ERROR: Cannot read process info for PID $pid"
        return 1
    fi
    
    # Read process stat file
    local stat_line=$(cat "$proc_stat")
    local stat_fields=($stat_line)
    
    # Extract relevant fields (0-indexed)
    local utime=${stat_fields[13]}    # User time
    local stime=${stat_fields[14]}    # System time
    local cutime=${stat_fields[15]}   # Children user time
    local cstime=${stat_fields[16]}   # Children system time
    local starttime=${stat_fields[21]} # Process start time
    
    # Total process time (including children)
    local total_time=$((utime + stime + cutime + cstime))
    
    # Get memory info from status file
    local vsize_kb=$(grep "^VmSize:" "$proc_status" 2>/dev/null | awk '{print $2}')
    local rss_kb=$(grep "^VmRSS:" "$proc_status" 2>/dev/null | awk '{print $2}')
    
    # If VmSize/VmRSS not available, try stat file
    if [ "$vsize_kb" = "" ]; then
        vsize_kb=$((${stat_fields[22]} / 1024))  # Convert bytes to KB
    fi
    if [ "$rss_kb" = "" ]; then
        rss_kb=$((${stat_fields[23]} * 4))  # Pages to KB (assuming 4KB pages)
    fi
    
    echo "$total_time $vsize_kb $rss_kb $starttime"
}

get_system_cpu_time() {
    # Read first line of /proc/stat to get total CPU time
    local cpu_line=$(head -n1 /proc/stat)
    local cpu_fields=($cpu_line)
    
    # Sum all CPU time fields (user, nice, system, idle, iowait, irq, softirq)
    local total=0
    for i in {1..7}; do
        if [ "${cpu_fields[$i]}" != "" ]; then
            total=$((total + ${cpu_fields[$i]}))
        fi
    done
    
    echo "$total"
}

format_memory() {
    local kb=$1
    
    if [ "$kb" = "" ] || [ "$kb" -eq 0 ]; then
        echo "0 KB"
        return
    fi
    
    if [ "$kb" -gt 1048576 ]; then
        # GB
        local gb=$((kb / 1048576))
        local remainder=$((kb % 1048576))
        local decimal=$((remainder * 10 / 1048576))
        echo "${gb}.${decimal} GB"
    elif [ "$kb" -gt 1024 ]; then
        # MB
        local mb=$((kb / 1024))
        local remainder=$((kb % 1024))
        local decimal=$((remainder * 10 / 1024))
        echo "${mb}.${decimal} MB"
    else
        echo "$kb KB"
    fi
}

monitor_process() {
    local pid=$1
    local prev_proc_time=0
    local prev_sys_time=0
    local first_run=1
    
    log "${CYAN}=== Watcher Process Monitor ===${NC}"
    log "${BLUE}PID: $pid${NC}"
    log "${BLUE}Command: $(cat /proc/$pid/comm 2>/dev/null || echo 'unknown')${NC}"
    log "${BLUE}Sampling interval: ${INTERVAL}s${NC}"
    log ""
    
    if [ "$QUIET" -eq 0 ]; then
        printf "%-8s %-8s %-10s %-10s %-12s %-8s\n" "Time" "CPU%" "Memory" "Virtual" "Uptime" "Status"
        printf "%-8s %-8s %-10s %-10s %-12s %-8s\n" "--------" "------" "----------" "----------" "------------" "--------"
    fi
    
    while true; do
        # Check if process still exists
        if [ ! -r "/proc/$pid/stat" ]; then
            log "${RED}ERROR: Process $pid no longer exists${NC}"
            return 1
        fi
        
        # Get current measurements
        local proc_info=$(get_process_info "$pid")
        if [ $? -ne 0 ]; then
            log "$proc_info"
            return 1
        fi
        
        local proc_fields=($proc_info)
        local curr_proc_time=${proc_fields[0]}
        local vsize_kb=${proc_fields[1]}
        local rss_kb=${proc_fields[2]}
        local start_time=${proc_fields[3]}
        
        local curr_sys_time=$(get_system_cpu_time)
        
        # Calculate CPU percentage
        local cpu_percent="0.0"
        if [ "$first_run" -eq 0 ]; then
            local proc_delta=$((curr_proc_time - prev_proc_time))
            local sys_delta=$((curr_sys_time - prev_sys_time))
            
            if [ "$sys_delta" -gt 0 ]; then
                # Convert to percentage with 1 decimal place
                cpu_percent=$(echo "scale=1; $proc_delta * 100.0 / $sys_delta" | bc 2>/dev/null || echo "0.0")
            fi
        fi
        
        # Calculate uptime
        local uptime_seconds=$(cat /proc/uptime | cut -d' ' -f1 | cut -d'.' -f1)
        local clock_ticks=$(getconf CLK_TCK 2>/dev/null || echo 100)
        local process_uptime=$(((uptime_seconds * clock_ticks - start_time) / clock_ticks))
        
        local uptime_str=""
        if [ "$process_uptime" -gt 3600 ]; then
            local hours=$((process_uptime / 3600))
            local minutes=$(((process_uptime % 3600) / 60))
            uptime_str="${hours}h${minutes}m"
        elif [ "$process_uptime" -gt 60 ]; then
            local minutes=$((process_uptime / 60))
            local seconds=$((process_uptime % 60))
            uptime_str="${minutes}m${seconds}s"
        else
            uptime_str="${process_uptime}s"
        fi
        
        # Format memory
        local memory_str=$(format_memory "$rss_kb")
        local vsize_str=$(format_memory "$vsize_kb")
        
        # Determine status color
        local status_color="$GREEN"
        local status="OK"
        
        if [ "${cpu_percent%.*}" -gt 50 ]; then
            status_color="$RED"
            status="HIGH_CPU"
        elif [ "$rss_kb" -gt 102400 ]; then  # > 100MB
            status_color="$YELLOW"
            status="HIGH_MEM"
        fi
        
        # Output current stats
        local timestamp=$(date '+%H:%M:%S')
        
        if [ "$QUIET" -eq 0 ]; then
            printf "%-8s ${YELLOW}%-7s${NC} %-10s %-10s %-12s ${status_color}%-8s${NC}\n" \
                   "$timestamp" "${cpu_percent}%" "$memory_str" "$vsize_str" "$uptime_str" "$status"
        fi
        
        # Log to file if specified
        if [ "$OUTPUT_FILE" != "" ]; then
            echo "$(date '+%Y-%m-%d %H:%M:%S') CPU:${cpu_percent}% MEM:$memory_str VIRT:$vsize_str UPTIME:$uptime_str STATUS:$status" >> "$OUTPUT_FILE"
        fi
        
        # Update for next iteration
        prev_proc_time="$curr_proc_time"
        prev_sys_time="$curr_sys_time"
        first_run=0
        
        # Check if we should continue
        SAMPLE_COUNT=$((SAMPLE_COUNT + 1))
        
        if [ "$MAX_SAMPLES" -gt 0 ] && [ "$SAMPLE_COUNT" -ge "$MAX_SAMPLES" ]; then
            log ""
            log "${GREEN}Completed $SAMPLE_COUNT samples${NC}"
            break
        fi
        
        if [ "$CONTINUOUS" -eq 0 ] && [ "$MAX_SAMPLES" -eq 0 ]; then
            break
        fi
        
        sleep "$INTERVAL"
    done
}

# Parse command line arguments
TARGET_PID=""

while [ $# -gt 0 ]; do
    case $1 in
        -h|--help)
            print_usage
            exit 0
            ;;
        -i|--interval)
            INTERVAL="$2"
            if ! echo "$INTERVAL" | grep -qE '^[0-9]+$'; then
                echo "Error: Interval must be a positive integer"
                exit 1
            fi
            shift 2
            ;;
        -c|--continuous)
            CONTINUOUS=1
            shift
            ;;
        -n|--samples)
            MAX_SAMPLES="$2"
            if ! echo "$MAX_SAMPLES" | grep -qE '^[0-9]+$'; then
                echo "Error: Sample count must be a positive integer"
                exit 1
            fi
            shift 2
            ;;
        -q|--quiet)
            QUIET=1
            shift
            ;;
        -o|--output)
            OUTPUT_FILE="$2"
            shift 2
            ;;
        -p|--pid)
            TARGET_PID="$2"
            if ! echo "$TARGET_PID" | grep -qE '^[0-9]+$'; then
                echo "Error: PID must be a positive integer"
                exit 1
            fi
            shift 2
            ;;
        -*)
            echo "Error: Unknown option $1"
            print_usage
            exit 1
            ;;
        *)
            if echo "$1" | grep -qE '^[0-9]+$'; then
                TARGET_PID="$1"
            else
                echo "Error: Invalid argument $1"
                exit 1
            fi
            shift
            ;;
    esac
done

# Check if bc is available for floating point calculations
if ! command -v bc >/dev/null 2>&1; then
    echo "Warning: 'bc' not found, CPU percentages will be less accurate"
fi

# Find the process to monitor
if [ "$TARGET_PID" = "" ]; then
    log "${YELLOW}Searching for watcher process...${NC}"
    TARGET_PID=$(find_watcher_pid)
    
    if [ "$TARGET_PID" = "" ]; then
        log "${RED}ERROR: Watcher process not found${NC}"
        log "Make sure the watcher service is running:"
        log "  $WATCHER_PATH"
        exit 1
    fi
    
    log "${GREEN}Found watcher process: PID $TARGET_PID${NC}"
else
    log "${BLUE}Monitoring specified PID: $TARGET_PID${NC}"
fi

# Set up signal handling for clean exit
trap 'echo ""; log "${YELLOW}Monitoring stopped by user${NC}"; exit 0' INT TERM

# Start monitoring
monitor_process "$TARGET_PID"