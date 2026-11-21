namespace Tbox.Cloud;

internal sealed class CaptureRetention(Captures captures, ILogger<CaptureRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { await captures.PurgeExpiredAsync(stoppingToken); }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Expired cloud captures could not be removed. The service will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
