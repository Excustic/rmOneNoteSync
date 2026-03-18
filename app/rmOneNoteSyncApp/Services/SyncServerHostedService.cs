using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SyncServerHostedService(
    ISyncServerService syncServer,
    ILogger<SyncServerHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Starting sync server hosted service");
            await syncServer.StartAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start sync server");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Stopping sync server hosted service");
            await syncServer.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error stopping sync server");
        }
    }
}