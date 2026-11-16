using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Island.App.Providers;
using Island.Core.Events;

namespace Island.App.UI;

using Island.App.Persistence;

internal sealed class QuotaSettingsPanel : ScrollViewer
{
    private sealed record RingChoice(string Key, string Label);

    public QuotaSettingsPanel(QuotaAccountStore store, Action refresh, Func<Guid, IReadOnlyList<QuotaMetric>> metricsFor,
        FrameworkElement appearance)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = new StackPanel();
        panel.Children.Add(appearance);
        SettingsRows.Section(panel, "应用与账号");
        var accountList = new ComboBox();
        var accountRows = new StackPanel();
        var editor = new StackPanel { Visibility = Visibility.Collapsed };
        var add = new Button { Content = "＋ 添加账号", Padding = new Thickness(10, 4, 10, 4) };
        var services = new ComboBox { ItemsSource = Enum.GetValues<QuotaService>() };
        var name = new TextBox { MaxLength = 36 };
        var colors = new[] { "蓝色", "橙色", "绿色", "紫色" };
        var colorIds = new[] { "Blue", "Orange", "Green", "Purple" };
        var swatches = new[] { "#97B9F7", "#EBA37D", "#91D8AC", "#B6A1EA" };
        var palette = new ComboBox { ItemsSource = colors };
        var pinned = new CheckBox { Content = "固定到收起的灵动岛（最多 3 个）", Margin = new Thickness(0, 10, 0, 2) };
        var localPath = new TextBox();
        var localLabel = Label("");
        var profilePath = new TextBox();
        var profileLabel = Label("Codex 配置目录（可选，多个登录配置用不同目录）");
        var password = new PasswordBox();
        var passwordLabel = Label("DeepSeek API Key（编辑时留空保留）");
        var credits = new TextBox();
        var creditsLabel = Label("WorkBuddy 剩余积分（手动录入，可留空）");
        var rings = new StackPanel();
        var ringBoxes = Enumerable.Range(0, 3).Select(index =>
        {
            var box = new ComboBox { DisplayMemberPath = nameof(RingChoice.Label) };
            rings.Children.Add(SettingsRows.Row($"第 {index + 1} 环额度", box));
            return box;
        }).ToArray();
        var note = Label("");
        var status = Label("");
        status.Margin = new Thickness(0, 10, 0, 0);
        var delete = new Button { Content = "删除此账号", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        var save = new Button { Content = "保存账号", Padding = new Thickness(10, 4, 10, 4) };
        var update = new Button { Content = "立即刷新额度", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        var discover = new Button { Content = "自动识别本机工具", Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

        List<QuotaAccount> accounts;
        try
        {
            accounts = store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            accounts = [];
            status.Text = "无法读取账号配置。为保留原文件，本次打开仅供查看。";
            save.IsEnabled = false;
        }

        void RefreshList(int selected)
        {
            accountList.ItemsSource = new[] { "新建账号" }.Concat(
                accounts.Select(account => $"{account.Service} · {account.Name}")).ToArray();
            accountList.SelectedIndex = selected;
            accountRows.Children.Clear();
            accountRows.Children.Add(new Border
            {
                BorderBrush = SettingsRows.Rule, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 10, 0, 8),
                Child = Columns(Cell("额度来源"), Cell("显示名称"), Cell("主题"), Cell("固定"))
            });
            if (accounts.Count == 0)
                accountRows.Children.Add(Label("暂无账号。可自动识别本机工具，或手动添加。"));
            for (int i = 0; i < accounts.Count; i++)
            {
                int index = i;
                var account = accounts[i];
                var swatch = new Ellipse { Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center,
                    Fill = SettingsRows.Brush(swatches[Math.Max(0, Array.IndexOf(colorIds, account.Color))]),
                    ToolTip = colors[Math.Max(0, Array.IndexOf(colorIds, account.Color))] };
                var pin = new CheckBox { IsChecked = account.Pinned, IsEnabled = save.IsEnabled };
                SettingsRows.StyleToggle(pin, $"固定 {account.Name} 到收起的小岛");
                pin.Click += (_, _) =>
                {
                    bool enabled = pin.IsChecked == true;
                    if (enabled && accounts.Count(item => item.Pinned) >= 3)
                    {
                        pin.IsChecked = false;
                        status.Text = "最多固定 3 个账号。";
                        return;
                    }
                    var updated = new List<QuotaAccount>(accounts);
                    updated[index] = account with { Pinned = enabled };
                    if (!Persist(updated, accountList.SelectedIndex, "已更新固定状态。"))
                        pin.IsChecked = account.Pinned;
                };
                var details = new Button { Content = "›", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Width = 27, Height = 28, ToolTip = $"编辑 {account.Name}" };
                System.Windows.Automation.AutomationProperties.SetName(details, $"编辑 {account.Name}");
                details.Click += (_, _) =>
                {
                    accountList.SelectedIndex = index + 1;
                    editor.Visibility = Visibility.Visible;
                    editor.BringIntoView();
                };
                accountRows.Children.Add(new Border
                {
                    BorderBrush = SettingsRows.Rule, BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 10, 0, 10),
                    Child = Columns(Cell(account.Service.ToString()), Cell(account.Name), swatch, pin, details)
                });
            }
        }

        void SetFields()
        {
            var service = services.SelectedItem is QuotaService choice ? choice : QuotaService.Codex;
            localLabel.Text = service == QuotaService.Claude ? "Claude Code 状态文件（留空使用默认位置）" : "Codex CLI 原生 exe 路径（可留空自动识别）";
            localLabel.Visibility = localPath.Visibility = Show(service is QuotaService.Codex or QuotaService.Claude);
            profileLabel.Visibility = profilePath.Visibility = Show(service == QuotaService.Codex);
            passwordLabel.Visibility = password.Visibility = Show(service == QuotaService.DeepSeek);
            creditsLabel.Visibility = credits.Visibility = Show(service == QuotaService.WorkBuddy);
            rings.Visibility = Show(service is QuotaService.Claude or QuotaService.Codex);
            var index = accountList.SelectedIndex - 1;
            var account = index >= 0 && index < accounts.Count ? accounts[index] : null;
            var choices = new List<RingChoice> { new("", "自动选择"), new("-", "隐藏本环") };
            var defaults = service == QuotaService.Claude
                ? new[] { new RingChoice("five_hour", "5 小时额度"), new RingChoice("seven_day", "7 天额度"), new RingChoice("spend_limit", "支出上限") }
                : new[] { new RingChoice("codex/primary", "主要额度"), new RingChoice("codex/secondary", "次要额度") };
            var current = account is null ? [] : metricsFor(account.Id);
            choices.AddRange(current.Select(metric => new RingChoice(metric.Key, metric.Label)));
            choices.AddRange(defaults.Where(choice => choices.All(item => item.Key != choice.Key)));
            for (int ring = 0; ring < ringBoxes.Length; ring++)
            {
                var key = account?.RingKeys?.ElementAtOrDefault(ring) ?? "";
                if (choices.All(choice => choice.Key != key)) choices.Add(new RingChoice(key, key));
                ringBoxes[ring].ItemsSource = choices.ToArray();
                ringBoxes[ring].SelectedIndex = choices.FindIndex(choice => choice.Key == key);
            }
            note.Text = service switch
            {
                QuotaService.Claude => "只读取官方状态栏导出的额度字段。配置方法见 README。",
                QuotaService.DeepSeek => "直连 DeepSeek 官方余额接口，密钥用 Windows DPAPI 保存。",
                QuotaService.WorkBuddy => "当前支持手动录入，官方开放接口尚未提供个人剩余积分查询。",
                QuotaService.Cursor => "使用官方 CLI 检查登录状态，个人额度在 Cursor 官网可查看。",
                _ => "通过本机的 Codex App Server 查询额度，使用客户端现有登录。"
            };
        }

        accountList.SelectionChanged += (_, _) =>
        {
            int index = accountList.SelectedIndex - 1;
            var existing = index >= 0 && index < accounts.Count ? accounts[index] : null;
            services.SelectedItem = existing?.Service ?? QuotaService.Codex;
            services.IsEnabled = existing is null;
            name.Text = existing?.Name ?? "";
            palette.SelectedIndex = Math.Max(0, Array.IndexOf(colorIds, existing?.Color));
            pinned.IsChecked = existing?.Pinned ?? false;
            localPath.Text = existing?.LocalPath ?? "";
            profilePath.Text = existing?.ProfilePath ?? "";
            password.Clear();
            credits.Text = existing?.ManualCredits?.ToString("G", CultureInfo.CurrentCulture) ?? "";
            delete.IsEnabled = existing is not null && save.IsEnabled;
            SetFields();
        };
        services.SelectionChanged += (_, _) => SetFields();
        save.Click += (_, _) =>
        {
            if (services.SelectedItem is not QuotaService service || string.IsNullOrWhiteSpace(name.Text))
            {
                status.Text = "请填写账号名称。";
                return;
            }
            int index = accountList.SelectedIndex - 1;
            var existing = index >= 0 ? accounts[index] : null;
            if (pinned.IsChecked == true && accounts.Count(account => account.Pinned && account.Id != existing?.Id) >= 3)
            {
                status.Text = "最多固定 3 个账号，请先取消固定一个账号。";
                return;
            }
            decimal? manual = null;
            if (service == QuotaService.WorkBuddy && !string.IsNullOrWhiteSpace(credits.Text))
            {
                if (!decimal.TryParse(credits.Text, out var amount) || amount < 0)
                {
                    status.Text = "请输入非负积分。";
                    return;
                }
                manual = amount;
            }
            var location = localPath.Text.Trim().Trim('"');
            var profile = profilePath.Text.Trim().Trim('"');
            if (service == QuotaService.Claude && location.Length > 0 && !Path.IsPathFullyQualified(location))
            {
                status.Text = "请填写状态文件的完整路径，或使用默认位置。";
                return;
            }
            if (service == QuotaService.Codex
                && ((location.Length > 0 && (!Path.IsPathFullyQualified(location) || !location.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(location)))
                    || (profile.Length > 0 && (!Path.IsPathFullyQualified(profile) || !Directory.Exists(profile)))))
            {
                status.Text = "请选择存在的 Codex 原生 exe，配置目录可留空。";
                return;
            }
            string key = service == QuotaService.DeepSeek
                ? (string.IsNullOrWhiteSpace(password.Password) ? existing?.ApiKey ?? "" : password.Password.Trim()) : "";
            var next = new QuotaAccount(existing?.Id ?? Guid.NewGuid(), service, name.Text.Trim(),
                colorIds[Math.Max(0, palette.SelectedIndex)], pinned.IsChecked == true,
                service is QuotaService.Codex or QuotaService.Claude ? location : "",
                service == QuotaService.Codex ? profile : "", key, manual,
                service is QuotaService.Claude or QuotaService.Codex
                    ? ringBoxes.Select(box => (box.SelectedItem as RingChoice)?.Key ?? "").ToArray() : null);
            var updated = new List<QuotaAccount>(accounts);
            if (index < 0)
            {
                index = updated.Count;
                updated.Add(next);
            }
            else
            {
                updated[index] = next;
            }
            Persist(updated, index + 1, "已保存账号。");
        };
        delete.Click += (_, _) =>
        {
            int index = accountList.SelectedIndex - 1;
            if (index < 0)
            {
                return;
            }
            var updated = new List<QuotaAccount>(accounts);
            updated.RemoveAt(index);
            Persist(updated, 0, "已删除账号。");
        };
        update.Click += (_, _) =>
        {
            refresh();
            status.Text = "已请求刷新，结果会显示在 AI 额度页。";
        };
        discover.IsEnabled = save.IsEnabled;
        add.IsEnabled = save.IsEnabled;
        add.Click += (_, _) =>
        {
            accountList.SelectedIndex = 0;
            editor.Visibility = Visibility.Visible;
            name.Focus();
        };
        discover.Click += (_, _) =>
        {
            var found = LocalAiDiscovery.Detect().Where(item => accounts.All(account => account.Service != item.Service)).ToArray();
            if (found.Length == 0)
            {
                status.Text = "没有发现尚未添加的本机工具。";
                return;
            }
            int openSlots = Math.Max(0, 3 - accounts.Count(account => account.Pinned));
            var next = new List<QuotaAccount>(accounts);
            foreach (var account in found)
            {
                bool pin = openSlots > 0 && account.Service is QuotaService.Codex or QuotaService.Claude;
                next.Add(account with { Pinned = pin });
                if (pin) openSlots--;
            }
            Persist(next, accounts.Count + 1, $"已添加 {string.Join("、", found.Select(item => item.Name))}。");
        };

        bool Persist(List<QuotaAccount> updated, int selection, string success)
        {
            try
            {
                store.Save(updated);
                accounts = updated;
                RefreshList(selection);
                refresh();
                status.Text = success;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                status.Text = "无法写入本机账号配置。";
                return false;
            }
        }

        SettingsRows.Section(editor, "账号详情");
        UIElement[] controls =
        {
            Label("服务"), services, Label("显示名称"), name,
            Label("配色"), palette, pinned, localLabel, localPath, profileLabel, profilePath,
            passwordLabel, password, creditsLabel, credits, rings, note,
            new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0),
                Children = { save, delete }
            }
        };
        for (int index = 0; index < controls.Length; index++)
        {
            var item = controls[index];
            if (item is TextBlock label && index + 1 < controls.Length
                && controls[index + 1] is FrameworkElement input && input is ComboBox or TextBox or PasswordBox)
            {
                editor.Children.Add(SettingsRows.Row(label, input));
                index++;
            }
            else if (item is CheckBox { Content: string caption } toggle)
                editor.Children.Add(SettingsRows.Row(caption, toggle));
            else
                editor.Children.Add(item);
        }
        panel.Children.Add(accountRows);
        panel.Children.Add(new WrapPanel { Margin = new Thickness(0, 12, 0, 6), Children = { add, discover, update } });
        panel.Children.Add(status);
        panel.Children.Add(editor);
        Content = panel;
        RefreshList(0);
    }

    private static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private static TextBlock Cell(string text) => new()
    {
        Text = text, Foreground = SettingsRows.Ink, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center, ToolTip = text
    };

    private static Grid Columns(params FrameworkElement[] cells)
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(2, GridUnitType.Star),
                     new GridLength(48), new GridLength(55), new GridLength(30) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        for (int index = 0; index < cells.Length; index++)
        {
            Grid.SetColumn(cells[index], index);
            grid.Children.Add(cells[index]);
        }
        return grid;
    }

    private static TextBlock Label(string value) => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4)
    };
}
