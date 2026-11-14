using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HdcMirror.App.Services;
using HdcMirror.App.Resources;

namespace HdcMirror.App.Views;

/// <summary>
/// 设备管理页：显示已记住的设备、支持删除，也支持手动填 IP + 端口发起连接。
/// 本页只做界面，真正连设备由 <see cref="MainWindow"/> 处理（订阅 <see cref="ConnectRequested"/>）。
/// </summary>
public partial class DeviceManagePage
{
    private readonly DeviceStore _store;

    /// <summary>构造设备管理页。</summary>
    /// <param name="store">本地设备清单。</param>
    public DeviceManagePage(DeviceStore store)
    {
        _store = store;
        InitializeComponent();
        Refresh();
    }

    /// <summary>请求连接某台设备（端点形如 <c>192.0.2.10:5555</c>）。</summary>
    public event EventHandler<string>? ConnectRequested;

    /// <summary>本页删掉一条本机记录后通知外部（参数为被删端点）。</summary>
    public event EventHandler<string>? Deleted;

    /// <summary>重新从磁盘读取清单并刷新列表。</summary>
    public void Refresh()
    {
        IReadOnlyList<SavedDevice> items = _store.Load();
        SavedList.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSavedConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedDevice device })
        {
            ConnectRequested?.Invoke(this, device.Endpoint);
        }
    }

    private void OnSavedDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedDevice device })
        {
            // 与设备栏的删除按钮保持同一套确认流程，避免一边有确认框一边没有。
            bool confirmed = ConfirmWindow.Ask(
                Window.GetWindow(this),
                Strings.Confirm_DeleteTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.Confirm_DeleteBodyFormat, device.Title),
                Strings.Confirm_DeleteOk);
            if (!confirmed)
            {
                return;
            }

            _store.Remove(device.Endpoint);
            Refresh();
            Deleted?.Invoke(this, device.Endpoint);
        }
    }

    private void OnManualConnectClick(object sender, RoutedEventArgs e)
    {
        string host = IpBox.Text.Trim();
        string port = PortBox.Text.Trim();

        if (host.Length == 0)
        {
            IpBox.Focus();
            return;
        }

        // 允许两种写法：只填 IP（默认 5555）或直接粘 IP:端口。
        if (host.Contains(':', StringComparison.Ordinal) && port.Length == 0)
        {
            ConnectRequested?.Invoke(this, host);
            return;
        }

        if (port.Length == 0)
        {
            port = "5555";
        }

        if (!ushort.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort portNumber)
            || portNumber == 0)
        {
            PortBox.Focus();
            return;
        }

        string endpoint = string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1}",
            host,
            portNumber);

        ConnectRequested?.Invoke(this, endpoint);
    }
}
