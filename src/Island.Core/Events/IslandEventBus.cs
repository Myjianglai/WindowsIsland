using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Island.Core.Events;

public interface IIslandEventPublisher
{
    ValueTask PublishAsync(IslandEvent message, CancellationToken cancellationToken = default);

    // 系统回调可使用此方法，避免队列满时阻塞回调线程。
    bool TryPublish(IslandEvent message);
}

/// <summary>多个 Provider 写入，一个界面调度器按入队顺序读取。</summary>
public sealed class IslandEventBus : IIslandEventPublisher
{
    private readonly Channel<IslandEvent> _channel;
    private int _activeReader;

    public IslandEventBus(int capacity = 64)
    {
        _channel = Channel.CreateBounded<IslandEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ValueTask PublishAsync(IslandEvent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public bool TryPublish(IslandEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _channel.Writer.TryWrite(message);
    }

    public async IAsyncEnumerable<IslandEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _activeReader, 1, 0) != 0)
        {
            throw new InvalidOperationException("事件总线只允许一个活动消费者。");
        }

        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
            }
        }
        finally
        {
            Volatile.Write(ref _activeReader, 0);
        }
    }

    /// <summary>由应用退出流程调用，停止接收新事件并允许读完已入队的事件。</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
