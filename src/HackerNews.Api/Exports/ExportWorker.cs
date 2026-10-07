using Microsoft.Extensions.Options;
using Npgsql;

namespace HackerNews.Api.Exports;

public sealed class ExportWorker(IExportStore store, TimeProvider timeProvider,
    IOptions<ExportOptions> options, ILogger<ExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await store.ProcessNextAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (NpgsqlException exception)
            {
                logger.LogError(exception, "Export database operation failed; durable work will be retried");
            }
            try
            {
                await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
