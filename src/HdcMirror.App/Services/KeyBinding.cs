using System;
using System.Collections.Generic;

namespace HdcMirror.App.Services;

/// <summary>
/// 一条按键映射：电脑上按 <see cref="Gesture"/>（如 <c>Ctrl+H</c>），手机上执行 <see cref="Action"/>。
///
/// 全部由用户自己录入，程序不预设任何一条 —— 预设在用户看来就是「我改不了」。
/// </summary>
public sealed class KeyBinding
{
    /// <summary>按键组合，例如 <c>Ctrl+H</c>、<c>F5</c>、<c>Alt+Left</c>。</summary>
    public string Gesture { get; set; } = string.Empty;

    /// <summary>动作标识，取值见 <see cref="KeyActions"/>。</summary>
    public string Action { get; set; } = string.Empty;
}

/// <summary>可绑定到手机上的动作清单。</summary>
public static class KeyActions
{
    /// <summary>系统键：必须走 <c>uitest uiInput keyEvent</c>（<c>uinput -K</c> 对它们是假成功）。</summary>
    public const string Home = "Home";

    /// <summary>返回。</summary>
    public const string Back = "Back";

    /// <summary>最近任务。</summary>
    public const string Recent = "Recent";

    /// <summary>电源键（锁屏 / 亮屏）。</summary>
    public const string Power = "Power";

    /// <summary>音量加。</summary>
    public const string VolumeUp = "VolumeUp";

    /// <summary>音量减。</summary>
    public const string VolumeDown = "VolumeDown";

    /// <summary>回车。</summary>
    public const string Enter = "Enter";

    /// <summary>退格。</summary>
    public const string Backspace = "Backspace";

    /// <summary>制表。</summary>
    public const string Tab = "Tab";

    /// <summary>方向键上。</summary>
    public const string Up = "Up";

    /// <summary>方向键下。</summary>
    public const string Down = "Down";

    /// <summary>方向键左。</summary>
    public const string Left = "Left";

    /// <summary>方向键右。</summary>
    public const string Right = "Right";

    /// <summary>全部可选动作（顺序即下拉框顺序）。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Home, Back, Recent, Power, VolumeUp, VolumeDown,
        Enter, Backspace, Tab, Up, Down, Left, Right,
    };

    /// <summary>
    /// 把按键事件描述成稳定文本：修饰键按 Ctrl→Alt→Shift→Win 排序，主键取 <see cref="System.Windows.Input.Key"/> 名。
    /// </summary>
    public static string Describe(System.Windows.Input.KeyEventArgs e)
    {
        if (e is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        System.Windows.Input.ModifierKeys mods = System.Windows.Input.Keyboard.Modifiers;
        if ((mods & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((mods & System.Windows.Input.ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((mods & System.Windows.Input.ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.None)
        {
            return string.Empty;
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
