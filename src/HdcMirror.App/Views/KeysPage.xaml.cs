using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;
using KeyBinding = HdcMirror.App.Services.KeyBinding;

namespace HdcMirror.App.Views;

/// <summary>
/// 按键映射页：**没有任何预设**，全部由用户自己录。
/// 一行 = 一个组合键 + 一个手机动作；「点输入框再按组合键」即可捕获按键。
/// 保存后写进 <c>settings.json</c>，镜像窗口按这张表把按键翻译成手机侧操作。
/// </summary>
public partial class KeysPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly List<RowWidgets> _rows = new();

    /// <summary>初始化按键映射页。</summary>
    public KeysPage()
    {
        InitializeComponent();
        Load();
    }

    /// <summary>一行里的三个控件。</summary>
    private sealed class RowWidgets
    {
        public required TextBox Gesture { get; init; }

        public required ComboBox Action { get; init; }
    }

    private void Load()
    {
        RowsHost.Children.Clear();
        _rows.Clear();

        foreach (KeyBinding binding in _settings.KeyBindings)
        {
            AddRow(binding.Gesture, binding.Action);
        }

        if (_rows.Count == 0)
        {
            AddRow(string.Empty, KeyActions.Back);
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => AddRow(string.Empty, KeyActions.Back);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var list = new List<KeyBinding>(_rows.Count);
        foreach (RowWidgets row in _rows)
        {
            string gesture = row.Gesture.Text.Trim();
            // 下拉框里放的是本地化显示名，设置里必须存英文 id —— 执行侧是按 id 比较的。
            string action = row.Action.SelectedIndex >= 0 && row.Action.SelectedIndex < KeyActions.All.Count
                ? KeyActions.All[row.Action.SelectedIndex]
                : KeyActions.Normalize(row.Action.SelectedItem as string ?? KeyActions.Back);
            if (gesture.Length == 0)
            {
                continue;
            }

            list.Add(new KeyBinding { Gesture = gesture, Action = action });
        }

        _settings.KeyBindings = list;
        _settings.Save();
        SavedText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Keys_SavedFormat,
            list.Count);
    }

    private void AddRow(string gesture, string action)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var gestureBox = new TextBox
        {
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Text = gesture,
            ToolTip = Strings.Keys_PressHint,
        };
        gestureBox.PreviewKeyDown += OnGesturePreviewKeyDown;
        Grid.SetColumn(gestureBox, 0);
        grid.Children.Add(gestureBox);

        var actionBox = new ComboBox
        {
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            ItemsSource = ActionLabels(),
            SelectedIndex = IndexOfAction(action),
        };
        Grid.SetColumn(actionBox, 1);
        grid.Children.Add(actionBox);

        var remove = new Button
        {
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 13,
            Content = Strings.Keys_Delete,
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        var row = new RowWidgets { Gesture = gestureBox, Action = actionBox };
        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            RowsHost.Children.Remove(grid);
        };

        _rows.Add(row);
        RowsHost.Children.Add(grid);
    }

    /// <summary>在输入框里按下组合键即可捕获；只按修饰键不写值。</summary>
    private void OnGesturePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key is Key.Tab)
        {
            return;
        }

        string gesture = KeyActions.Describe(e);
        if (gesture.Length == 0)
        {
            return;
        }

        box.Text = gesture;
        e.Handled = true;
    }

    private static List<string> ActionLabels()
    {
        var labels = new List<string>(KeyActions.All.Count);
        foreach (string id in KeyActions.All)
        {
            labels.Add(KeyActions.Label(id));
        }

        return labels;
    }

    private static int IndexOfAction(string action)
    {
        // 老版本存的是「主屏幕」这类显示名，先翻译回 id 再定位，否则下拉框会空着。
        string normalized = KeyActions.Normalize(action);
        for (int i = 0; i < KeyActions.All.Count; i++)
        {
            if (string.Equals(KeyActions.All[i], normalized, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }
}
