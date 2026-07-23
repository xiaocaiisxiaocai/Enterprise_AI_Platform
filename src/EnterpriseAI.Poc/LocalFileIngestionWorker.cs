namespace EnterpriseAI.Poc;

public sealed class LocalFileIngestionWorker(
    LocalFileIngestionService ingestion,
    LocalFileIngestionWorkerOptions options,
    ILogger<LocalFileIngestionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogTimeout =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, "LocalIngestionTimeout"),
            "本地文件同步超过配置超时，当前批次未发布。");
    private static readonly Action<ILogger, Exception?> LogFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, "LocalIngestionFailure"),
            "本地文件同步失败，保留上一次完整投影。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Interval, stoppingToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(options.Timeout);
                ingestion.Synchronize(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                LogTimeout(logger, null);
            }
            catch (Exception exception)
            {
                LogFailure(logger, exception);
            }
        }
    }
}
