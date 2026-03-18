using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface ILogService
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
    void LogDebug(string message);
    
    Task<List<LogEntry>> GetRecentLogsAsync(int count = 100);
    Task ClearLogsAsync();
    string GetLogFilePath();
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
}