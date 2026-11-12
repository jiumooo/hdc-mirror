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
            string action = row.Action.SelectedItem as string ?? KeyActions.Back;
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
            Height = 30,
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
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            ItemsSource = ActionLabels(),
            SelectedIndex = IndexOfAction(action),
        };
        Grid.SetColumn(actionBox, 1);
        grid.Children.Add(actionBox);

        var remove = new Button
        {
            Height = 30,
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
            labels.Add(Label(id));
        }

        return labels;
    }

    private static int IndexOfAction(string action)
    {
        for (int i = 0; i < KeyActions.All.Count; i++)
        {
            if (string.Equals(KeyActions.All[i], action, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    private static string Label(string id) => id switch
    {
        KeyActions.Home => Strings.KeyAct_Home,
        KeyActions.Back => Strings.KeyAct_Back,
        KeyActions.Recent => Strings.KeyAct_Recent,
        KeyActions.Power => Strings.KeyAct_Power,
        KeyActions.VolumeUp => Strings.KeyAct_VolumeUp,
        KeyActions.VolumeDown => Strings.KeyAct_VolumeDown,
        KeyActions.Enter => Strings.KeyAct_Enter,
        KeyActions.Backspace => Strings.KeyAct_Backspace,
        KeyActions.Tab => Strings.KeyAct_Tab,
        KeyActions.Up => Strings.KeyAct_Up,
        KeyActions.Down => Strings.KeyAct_Down,
        KeyActions.Left => Strings.KeyAct_Left,
        KeyActions.Right => Strings.KeyAct_Right,
        _ => id,
    };
}
