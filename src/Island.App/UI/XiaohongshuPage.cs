using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Island.App.Persistence;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Island.App.UI;

internal sealed class XiaohongshuPage : DockPanel, IDisposable
{
    private const string Home = "https://www.xiaohongshu.com/explore";
    private readonly Grid _content = new() { ClipToBounds = true };
    private readonly TextBlock _status = ExpandedTabs.Label("正在打开小红书…", 12, IslandTheme.Muted);
    private readonly Button _back = ExpandedTabs.MakeFlatButton("‹");
    private readonly Button _reload = ExpandedTabs.MakeFlatButton("刷新");
    private WebView2CompositionControl? _view;
    private bool _disposed;
    private bool _panelReady;
    public event Action? KeyboardRequested;
    public event Action? Hidden;

    public XiaohongshuPage()
    {
        _back.ToolTip = "返回上一页";
        _back.IsEnabled = false;
        _back.Click += (_, _) =>
        {
            if (_view?.CoreWebView2 is { CanGoBack: true } browser) browser.GoBack();
        };
        _reload.Click += async (_, _) =>
        {
            if (_view?.CoreWebView2 is { } browser) browser.Reload();
            else await OpenAsync();
        };
        var open = ExpandedTabs.MakeFlatButton("在浏览器打开");
        open.Click += (_, _) =>
        {
            try
            {
                string target = _view?.CoreWebView2?.Source is { } current && IsSite(current) ? current : Home;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Win32Exception) { ShowStatus("无法打开默认浏览器。"); }
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _back, _reload, open } };
        SetDock(actions, Dock.Top);
        Children.Add(actions);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _content.Children.Add(_status);
        Children.Add(_content);
        Loaded += async (_, _) => await OpenAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (_view?.CoreWebView2 is { } browser) browser.IsMuted = !IsVisible;
            if (!IsVisible) Hidden?.Invoke();
        };
    }

    public void SetPanelReady(bool ready)
    {
        if (_panelReady == ready) return;
        _panelReady = ready;
        if (!ready)
        {
            if (_view is { } view)
            {
                if (view.CoreWebView2 is { } browser) browser.IsMuted = true;
                _content.Children.Remove(view);
            }
            Hidden?.Invoke();
        }
        else if (_view is { } view)
        {
            _content.Children.Insert(0, view);
            if (view.CoreWebView2 is { } browser) browser.IsMuted = !IsVisible;
        }
        else
        {
            _ = OpenAsync();
        }
    }

    private async Task OpenAsync()
    {
        if (_disposed || !_panelReady || !IsLoaded || _view is not null) return;
        var profile = Path.Combine(LocalDataStore.DirectoryPath, "WebView2", "Xiaohongshu");
        var view = new WebView2CompositionControl
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = profile },
            ZoomFactor = .8,
            MinWidth = 32,
            MinHeight = 32
        };
        _view = view;
        _content.Children.Insert(0, view);
        view.PreviewMouseDown += (_, _) => KeyboardRequested?.Invoke();
        try
        {
            await view.EnsureCoreWebView2Async();
            if (_disposed || !ReferenceEquals(_view, view)) return;
            var browser = view.CoreWebView2;
            browser.Settings.AreHostObjectsAllowed = false;
            browser.Settings.IsWebMessageEnabled = false;
            browser.Settings.AreDevToolsEnabled = false;
            browser.IsMuted = !_panelReady || !IsVisible;
            browser.NavigationStarting += (_, e) =>
            {
                e.Cancel = !IsSite(e.Uri);
                if (e.Cancel) ShowStatus("岛内仅展示小红书网页。");
            };
            browser.NavigationCompleted += (_, e) =>
            {
                _back.IsEnabled = browser.CanGoBack;
                if (e.IsSuccess) _status.Visibility = Visibility.Collapsed;
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowStatus("暂时无法在岛内加载小红书。");
            };
            browser.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsSite(e.Uri)) browser.Navigate(e.Uri);
            };
            browser.Navigate(Home);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (_disposed) return;
            _content.Children.Remove(view);
            view.Dispose();
            _view = null;
            ShowStatus("无法启动内置浏览器，可以在默认浏览器打开。");
        }
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
    }

    internal static bool IsSite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        var host = uri.IdnHost;
        return host is "xiaohongshu.com" or "rednote.com" ||
            host.EndsWith(".xiaohongshu.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".rednote.com", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _disposed = true;
        _view?.Dispose();
        _view = null;
    }
}
