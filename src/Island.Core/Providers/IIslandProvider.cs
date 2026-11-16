using Island.Core.Events;

namespace Island.Core.Providers;

/// <summary>Provider 启用时运行，禁用或退出时取消并释放平台资源。</summary>
public interface IIslandProvider
{
    // 稳定标识，用于配置开关及 IslandEvent.Source。
    string Id { get; }

    // 停止时应注销系统回调、停止轮询并关闭已打开的资源。
    Task RunAsync(IIslandEventPublisher publisher, CancellationToken cancellationToken);
}
