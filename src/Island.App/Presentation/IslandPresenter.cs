using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Island.App.UI;
using Island.Core.Events;

namespace Island.App.Presentation;

internal enum IslandState
{
    Compact,
    Expanded,
    Banner
}

internal sealed class IslandPresenter
{
    private readonly IslandWindow _view;
    private readonly DispatcherTimer _pointer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer _todoReminder = new() { Interval = TimeSpan.FromSeconds(3) };
    private IslandState _state;
    private long? _outsideSince;

    public IslandPresenter(IslandWindow view)
    {
        _view = view;
        _view.IslandClicked += () =>
        {
            if (_state == IslandState.Compact && !_view.IsAvoided)
            {
                ChangeState(IslandState.Expanded);
            }
        };
        _pointer.Tick += (_, _) => CheckPointer();
    }

    public async Task RunAsync(IslandEventBus events, CancellationToken cancellationToken)
    {
        EventHandler remind = (_, _) => _view.CheckTodoReminders(events.TryPublish);
        try
        {
            _pointer.Start();
            _todoReminder.Tick += remind;
            _todoReminder.Start();
            await foreach (var message in events.ReadAllAsync(cancellationToken))
            {
                if (message.Type == IslandEventType.Media)
                {
                    _view.SetMedia(message);
                    continue;
                }
                if (message.Type == IslandEventType.Clipboard)
                {
                    _view.SetClipboard(message);
                    continue;
                }
                if (message.Type == IslandEventType.Quota)
                {
                    if (_view.SetQuota(message.Quota ?? new QuotaSnapshot([])) is { } warning)
                        events.TryPublish(warning);
                    continue;
                }
                if (message.Type == IslandEventType.SystemMetrics)
                {
                    _view.SetSystemMetrics(message.Metrics);
                    continue;
                }
                if (message.Type == IslandEventType.DeviceBattery)
                {
                    _view.SetDevices(message);
                    continue;
                }
                while (_view.IsEditingTodo)
                    await Task.Delay(120, cancellationToken);
                ChangeState(IslandState.Banner, message);
                await Task.Delay(message.Timeout, cancellationToken);
                // 等待收起动画走完，再消费下一条横幅。
                var collapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _view.Display(IslandState.Compact, null, () => collapsed.TrySetResult());
                await collapsed.Task.WaitAsync(cancellationToken);
                _state = IslandState.Compact;
            }
        }
        finally
        {
            _pointer.Stop();
            _todoReminder.Stop();
            _todoReminder.Tick -= remind;
        }
    }

    private void CheckPointer()
    {
        _view.TickDisplay(_state == IslandState.Compact && !_view.IsDragging && !_view.IsPointerNear(16));
        if (_view.IsAvoided && _state == IslandState.Expanded)
        {
            ChangeState(IslandState.Compact);
        }
        if (_state == IslandState.Banner || _view.IsDragging || _view.IsAvoided)
        {
            _outsideSince = null;
            return;
        }

        if (_view.IsPointerNear(_state == IslandState.Compact ? 12 : 16))
        {
            _outsideSince = null;
            if (_state == IslandState.Compact)
            {
                ChangeState(IslandState.Expanded);
            }
        }
        else if (_state == IslandState.Expanded)
        {
            _outsideSince ??= Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_outsideSince.Value) >= TimeSpan.FromMilliseconds(135))
            {
                ChangeState(IslandState.Compact);
            }
        }
    }

    private void ChangeState(IslandState state, IslandEvent? message = null)
    {
        _state = state;
        _outsideSince = null;
        _view.Display(state, message);
    }
}
