using System;

namespace HdcMirror.App.Audio;

/// <summary>
/// 全进程唯一的音频接收器。
///
/// 为什么要单例：<c>AudioPlaybackConnection</c> 有**进程级锁** —— 同一个进程里 Open 失败一次之后，
/// 后续重试只会立刻拿到同一个错误，必须换进程才能重新等那 30 秒。反过来说，同一时刻只能有一个
/// 接收端在跑，镜像窗口的控制条按钮与「音画与音频」设置页必须是同一个实例，否则两个子进程互相顶掉。
/// </summary>
public static class AudioSinkHub
{
    private static AudioSinkHost? _instance;

    /// <summary>取得共享实例（首次访问时创建）。</summary>
    public static AudioSinkHost Instance => _instance ??= new AudioSinkHost();

    /// <summary>当前状态。</summary>
    public static AudioSinkState State => _instance?.State ?? AudioSinkState.Idle;

    /// <summary>最近一次失败原因。</summary>
    public static string? LastError => _instance?.LastError;

    /// <summary>程序退出时调用。</summary>
    public static void Shutdown()
    {
        _instance?.Dispose();
        _instance = null;
    }
}
