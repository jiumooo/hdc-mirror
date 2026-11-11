using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Decode.Video;

namespace HdcMirror.Decode.Ffmpeg;

/// <summary>
/// 基于 libavcodec（FFmpeg.AutoGen）的 H.264 解码器。
/// <para>
/// 输入：Annex B 字节流的单个 access unit（自带起始码 <c>00 00 [00] 01</c>），外加该帧的纳秒时间戳；
/// 以及运行期可变动的 SPS+PPS 配置帧。
/// 输出：BGRA32、自上而下、行优先的像素缓冲，供 WPF <c>WriteableBitmap</c> 直接 WritePixels。
/// </para>
/// <para>
/// 线程模型：<b>非线程安全</b>，必须始终在同一个线程上调用（推荐专职解码线程）。
/// 内部不做任何线程切换；单帧解码耗时在真机 1260×2720 上约 4 ms。
/// </para>
/// <para>
/// 配置帧语义（协议硬约束：每次 IDR 都会重发 SPS/PPS）：
/// 第一次 <see cref="Configure"/> 之后的第一帧会把配置帧拼在 access unit 前面一起送入，
/// 确保解码器在真正的第一个 IDR 之前拿到参数集；之后配置帧变化（例如旋转、码率切换）
/// <b>不重建解码器</b>，只是重新置位，随下一帧一并送入，并累加 <see cref="ReconfigurationCount"/>。
/// libavcodec 本身支持流内参数集变更，因此无需像 Media Foundation 那样销毁重建。
/// </para>
/// </summary>
public sealed unsafe class FfmpegH264Decoder : IH264Decoder
{
    private const string Category = FfmpegRuntime.Category;

    /// <summary>像素缓冲尾部的安全余量（字节），见 <see cref="EnsureScaler"/> 的说明。</summary>
    private const int PixelGuardBytes = 4096;

    private readonly ILogSink _log;
    private readonly Queue<PendingFrame> _pending = new();

    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _receive;
    private SwsContext* _scaler;

    private AVPixelFormat _sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int _sourceWidth;
    private int _sourceHeight;
    private byte[] _pixels = Array.Empty<byte>();

    private byte[]? _configuration;
    private bool _configurationPending;
    private int _reconfigurations;

    private int _width;
    private int _height;
    private long _sequence;
    private long _pendingPtsNanoseconds;
    private bool _disposed;

    /// <summary>创建一个尚未打开的解码器；第一次 <see cref="TryDecode"/> 时才真正打开 libavcodec 上下文。</summary>
    /// <param name="log">可选的日志接收器。</param>
    public FfmpegH264Decoder(ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
    }

    /// <inheritdoc />
    public string Name => "FFmpeg.AutoGen/libavcodec";

    /// <inheritdoc />
    public int FrameWidth => _width;

    /// <inheritdoc />
    public int FrameHeight => _height;

    /// <inheritdoc />
    public int ReconfigurationCount => _reconfigurations;

    /// <summary>当前生效的 SPS/PPS 长度（字节）；未收到配置帧时为 0。</summary>
    public int ConfigurationLength => _configuration?.Length ?? 0;

    /// <inheritdoc />
    public void Configure(ReadOnlySpan<byte> annexBConfiguration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (annexBConfiguration.IsEmpty)
        {
            return;
        }

        if (_configuration is not null && _configuration.AsSpan().SequenceEqual(annexBConfiguration))
        {
            return;
        }

        var updated = _configuration is not null;
        _configuration = annexBConfiguration.ToArray();
        _configurationPending = true;

        if (updated)
        {
            _reconfigurations++;
            _log.Info(Category, string.Create(
                CultureInfo.InvariantCulture,
                $"配置帧变更：{_configuration.Length} 字节（第 {_reconfigurations} 次），不重置解码器，随下一帧送入"));
        }
        else
        {
            _log.Debug(Category, string.Create(
                CultureInfo.InvariantCulture,
                $"配置帧已记录：{_configuration.Length} 字节"));
        }
    }

    /// <inheritdoc />
    public bool TryDecode(ReadOnlySpan<byte> annexBAccessUnit, long ptsNanoseconds, out DecodedVideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOpen();

        if (!annexBAccessUnit.IsEmpty)
        {
            Feed(annexBAccessUnit, ptsNanoseconds);
        }

        if (_pending.Count == 0)
        {
            ReceiveFrames();
        }

        if (_pending.Count == 0)
        {
            frame = null;
            return false;
        }

        var pending = _pending.Dequeue();
        frame = ConvertFrame(pending.Frame, pending.PtsNanoseconds);
        return true;
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        ClearPending();
        if (_context is not null)
        {
            ffmpeg.avcodec_flush_buffers(_context);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPending();
        ReleaseScaler();

        if (_receive is not null)
        {
            var receive = _receive;
            ffmpeg.av_frame_free(&receive);
            _receive = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_context is not null)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }
    }

    private void EnsureOpen()
    {
        if (_context is not null)
        {
            return;
        }

        FfmpegRuntime.EnsureInitialized(_log);

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null)
        {
            throw new H264DecodeException("libavcodec 未提供 H.264 解码器");
        }

        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context is null)
        {
            throw new H264DecodeException("avcodec_alloc_context3 失败");
        }

        // 线程模型与本层一致：单线程解码，避免 libavcodec 帧级多线程在反复重建时堆积原生工作线程。
        context->thread_count = 1;
        context->thread_type = ffmpeg.FF_THREAD_SLICE;

        var opened = ffmpeg.avcodec_open2(context, codec, null);
        if (opened < 0)
        {
            var failed = context;
            ffmpeg.avcodec_free_context(&failed);
            throw new H264DecodeException(string.Create(
                CultureInfo.InvariantCulture,
                $"avcodec_open2 失败：{opened}"));
        }

        _context = context;
        _packet = ffmpeg.av_packet_alloc();
        _receive = ffmpeg.av_frame_alloc();
        if (_packet is null || _receive is null)
        {
            var packet = _packet;
            var receive = _receive;
            var contextToFree = _context;
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (receive is not null)
            {
                ffmpeg.av_frame_free(&receive);
            }

            ffmpeg.avcodec_free_context(&contextToFree);
            _packet = null;
            _receive = null;
            _context = null;
            throw new H264DecodeException("分配 AVPacket/AVFrame 失败");
        }

        _log.Info(Category, string.Create(
            CultureInfo.InvariantCulture,
            $"H.264 解码器已打开：{Marshal.PtrToStringAnsi((IntPtr)codec->long_name)}，{FfmpegRuntime.VersionSummary}"));
    }

    private void Feed(ReadOnlySpan<byte> accessUnit, long ptsNanoseconds)
    {
        byte[]? combined = null;
        var payload = accessUnit;

        if (_configurationPending && _configuration is not null && !StartsWithParameterSet(accessUnit))
        {
            combined = new byte[_configuration.Length + accessUnit.Length];
            _configuration.CopyTo(combined, 0);
            accessUnit.CopyTo(combined.AsSpan(_configuration.Length));
            payload = combined;
        }

        _configurationPending = false;

        if (ffmpeg.av_new_packet(_packet, payload.Length) < 0)
        {
            throw new H264DecodeException(string.Create(
                CultureInfo.InvariantCulture,
                $"av_new_packet 分配 {payload.Length} 字节失败"));
        }

        fixed (byte* source = payload)
        {
            Buffer.MemoryCopy(source, _packet->data, payload.Length, payload.Length);
        }

        _packet->pts = ptsNanoseconds / 1000;
        _pendingPtsNanoseconds = ptsNanoseconds;
        var result = ffmpeg.avcodec_send_packet(_context, _packet);
        ffmpeg.av_packet_unref(_packet);

        if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            // 解码器要求先取帧：先抽干再重投一次。
            ReceiveFrames();
            if (ffmpeg.av_new_packet(_packet, payload.Length) < 0)
            {
                throw new H264DecodeException("av_new_packet 重试失败");
            }

            fixed (byte* source = payload)
            {
                Buffer.MemoryCopy(source, _packet->data, payload.Length, payload.Length);
            }

            _packet->pts = ptsNanoseconds / 1000;
            result = ffmpeg.avcodec_send_packet(_context, _packet);
            ffmpeg.av_packet_unref(_packet);
        }

        if (result < 0)
        {
            throw new H264DecodeException(string.Create(
                CultureInfo.InvariantCulture,
                $"avcodec_send_packet 失败：{result}（{payload.Length} 字节）"));
        }
    }

    private void ReceiveFrames()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_frame(_context, _receive);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            if (result < 0)
            {
                throw new H264DecodeException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"avcodec_receive_frame 失败：{result}"));
            }
        var clone = ffmpeg.av_frame_clone(_receive);
            if (clone is null)
            {
                ffmpeg.av_frame_unref(_receive);
                throw new H264DecodeException("av_frame_clone 失败");
            }

            _pending.Enqueue(new PendingFrame((IntPtr)clone, _pendingPtsNanoseconds));
            ffmpeg.av_frame_unref(_receive);
        }
    }

    private DecodedVideoFrame ConvertFrame(IntPtr framePointer, long ptsNanoseconds)
    {
        var frame = (AVFrame*)framePointer;
        var width = frame->width;
        var height = frame->height;
        var format = (AVPixelFormat)frame->format;

        if (width <= 0 || height <= 0)
        {
            var invalid = frame;
            ffmpeg.av_frame_free(&invalid);
            throw new H264DecodeException(string.Create(
                CultureInfo.InvariantCulture,
                $"解码帧尺寸非法：{width}×{height}"));
        }

        EnsureScaler(width, height, format);

        var stride = width * 4;
        var destination = new byte_ptrArray4();
        var destinationStride = new int_array4();
        fixed (byte* target = _pixels)
        {
            destination[0] = target;
            destinationStride[0] = stride;
            ffmpeg.sws_scale(_scaler, frame->data, frame->linesize, 0, height, destination, destinationStride);
        }
        var released = frame;
        ffmpeg.av_frame_free(&released);

        _width = width;
        _height = height;
        return new DecodedVideoFrame(_pixels, width, height, stride, ptsNanoseconds, ++_sequence);
    }

    private void EnsureScaler(int width, int height, AVPixelFormat format)
    {
        if (_scaler is not null && _sourceWidth == width && _sourceHeight == height && _sourceFormat == format)
        {
            return;
        }

        ReleaseScaler();
        _scaler = ffmpeg.sws_getContext(
            width,
            height,
            format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);

        if (_scaler is null)
        {
            throw new H264DecodeException(string.Create(
                CultureInfo.InvariantCulture,
                $"sws_getContext 失败：{format} {width}×{height} → BGRA"));
        }

        _sourceWidth = width;
        _sourceHeight = height;
        _sourceFormat = format;
        // 尾部留一页余量：swscale 的 SIMD 末行写可能越过 width*height*4，
        // 越过托管数组边界会破坏 GC 堆（表现为偶发 0x80131506），故显式留白。
        _pixels = new byte[(width * height * 4) + PixelGuardBytes];
        _log.Info(Category, string.Create(
            CultureInfo.InvariantCulture,
            $"颜色转换上下文建立：{format} {width}×{height} → BGRA32，缓冲 {_pixels.Length} 字节"));
    }

    private void ReleaseScaler()
    {
        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _sourceWidth = 0;
        _sourceHeight = 0;
        _sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private void ClearPending()
    {
        while (_pending.Count > 0)
        {
            var pending = _pending.Dequeue();
            var frame = (AVFrame*)pending.Frame;
            ffmpeg.av_frame_free(&frame);
        }
    }

    private static bool StartsWithParameterSet(ReadOnlySpan<byte> accessUnit)
    {
        var index = 0;
        if (accessUnit.Length >= 4 && accessUnit[0] == 0 && accessUnit[1] == 0 && accessUnit[2] == 0 && accessUnit[3] == 1)
        {
            index = 4;
        }
        else if (accessUnit.Length >= 3 && accessUnit[0] == 0 && accessUnit[1] == 0 && accessUnit[2] == 1)
        {
            index = 3;
        }
        else
        {
            return false;
        }

        return index < accessUnit.Length && (accessUnit[index] & 0x1F) == 7;
    }

    private readonly record struct PendingFrame(IntPtr Frame, long PtsNanoseconds);
}
