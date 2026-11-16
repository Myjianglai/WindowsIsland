using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Island.Core.Events;
using Island.Core.Providers;

namespace Island.App.Providers;

internal sealed class ProviderHost(
    IIslandProvider provider, IIslandEventPublisher publisher,
    Func<IslandEvent> onDisabled, Func<IslandEvent> onFaulted, CancellationToken shutdown)
{
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource? _stop;
    private Task? _run;

    public async Task SetEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync(shutdown);
        try
        {
            if (enabled && _run is { IsCompleted: false })
            {
                return;
            }
            if (_stop is not null)
            {
                _stop.Cancel();
                await _run!;
                _stop.Dispose();
                _stop = null;
            }
            if (!enabled)
            {
                await publisher.PublishAsync(onDisabled(), shutdown);
                return;
            }
            _stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            var token = _stop.Token;
            _run = Task.Run(() => RunAsync(token));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await provider.RunAsync(publisher, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 数据源已关闭，或应用正在退出。
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Provider {0} failed ({1}).", provider.Id, ex.GetType().Name);
            // 队列背压不影响关闭或重新开启 Provider。
            publisher.TryPublish(onFaulted());
        }
    }
}
