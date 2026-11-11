namespace HdcMirror.Decode.Video;

/// <summary>
/// 解码后的一帧画面，像素固定为 <b>BGRA32</b>（内存顺序 B、G、R、A，行序自顶向下）。
/// <para>
/// 选择这个格式而不是 P/Invoke 的 RGB32：WPF 的 <c>PixelFormats.Bgra32</c> 与它逐字节一致，
/// 表现层可以直接 <c>WriteableBitmap.WritePixels</c>，不需要再走一次 <c>Bgra32</c> ↔ <c>Bgr32</c> 的重排。
/// </para>
/// <para>
/// <see cref="Pixels"/> 是解码器内部复用的缓冲区（避免每帧 13 MB 的分配），只在
/// 「同一个解码器下一次 <see cref="IH264Decoder.TryDecode"/> 返回之前」有效。
/// 需要跨帧持有请自行拷走。
/// </para>
/// </summary>
public sealed class DecodedVideoFrame
{
    internal DecodedVideoFrame(byte[] pixels, int width, int height, int stride, long ptsNanoseconds, long sequence)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        PtsNanoseconds = ptsNanoseconds;
        Sequence = sequence;
    }

    /// <summary>BGRA32 像素，长度 = <see cref="Stride"/> × <see cref="Height"/>。</summary>
    public byte[] Pixels { get; }

    /// <summary>画面宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>画面高度（像素）。</summary>
    public int Height { get; }

    /// <summary>一行字节数，等于 <see cref="Width"/> × 4。</summary>
    public int Stride { get; }

    /// <summary>该帧在设备侧的呈现时间戳（纳秒，单调递增，仅用于测延迟）。</summary>
    public long PtsNanoseconds { get; }

    /// <summary>解码器内部的帧序号，从 1 开始，用来判断「有没有新帧」。</summary>
    public long Sequence { get; }
}

/// <summary>解码失败（MF 初始化失败、码流被判非法、输出格式不认识等）。</summary>
public sealed class H264DecodeException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="message">面向日志的中文说明。</param>
    public H264DecodeException(string message)
        : base(message)
    {
    }

    /// <summary>创建异常（带内层原因）。</summary>
    /// <param name="message">面向日志的中文说明。</param>
    /// <param name="inner">内层异常。</param>
    public H264DecodeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
