using System;

namespace HdcMirror.App.Video;

/// <summary>
/// 解码线程与 UI 线程之间的单帧交换槽。
///
/// 输入：解码线程产出的 BGRA32 帧（<c>stride × height</c> 字节，顶向下）。
/// 输出：UI 线程按自己的节奏取走的最新一帧。
/// 线程模型：只有「最新帧」有意义，UI 落后时旧帧直接覆盖丢弃，绝不排队——
/// 排队只会在窗口卡顿时堆积出越来越久的延迟。
/// </summary>
internal sealed class FrameExchange
{
    private readonly object _gate = new();
    private byte[] _pixels = Array.Empty<byte>();
    private int _width;
    private int _height;
    private int _stride;
    /// <summary>最近一次发布时刻（Stopwatch 时间戳），用于测量「解码完成 -> 界面渲染」延迟。</summary>
    public long LastPublishTicks { get; private set; }

    private long _published;
    private long _consumed;

    /// <summary>已发布的帧数。</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>已被 UI 取走的帧数。</summary>
    public long Consumed => Interlocked.Read(ref _consumed);

    /// <summary>解码线程调用：把一帧写进槽位，覆盖上一帧。</summary>
    public void Publish(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        LastPublishTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var size = stride * height;
        if (size <= 0 || pixels.Length < size)
        {
            return;
        }

        lock (_gate)
        {
            if (_pixels.Length != size)
            {
                _pixels = new byte[size];
            }

            pixels[..size].CopyTo(_pixels);
            _width = width;
            _height = height;
            _stride = stride;
            _published++;
        }
    }

    /// <summary>
    /// UI 线程调用：若有尚未取走的帧，则在锁内把它交给 <paramref name="consumer"/>。
    /// 锁内执行保证解码线程不会同时覆盖缓冲区（消费者应只做一次拷贝/绘制，不要做耗时工作）。
    /// </summary>
    /// <returns>是否交付了一帧。</returns>
    public bool Consume(Action<byte[], int, int, int> consumer)
    {
        if (consumer is null)
        {
            throw new ArgumentNullException(nameof(consumer));
        }

        lock (_gate)
        {
            if (_published == _consumed || _pixels.Length == 0)
            {
                return false;
            }

            _consumed = _published;
            consumer(_pixels, _width, _height, _stride);
            return true;
        }
    }
}
