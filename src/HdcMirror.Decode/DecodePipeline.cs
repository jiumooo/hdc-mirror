using HdcMirror.Core.Diagnostics;
using HdcMirror.Decode.Ffmpeg;
using HdcMirror.Decode.Video;

namespace HdcMirror.Decode;

/// <summary>
/// 解码层入口：把设备推来的 Annex B 码流变成 BGRA32 画面。
/// <para>
/// M1b 选型结论：走 <b>FFmpeg.AutoGen + libavcodec</b>，输出统一为 BGRA32（转换由 swscale 完成）。
/// 首选方案是 Windows 自带 Media Foundation 的 H.264 解码 MFT，实测在该设备码流上
/// 流式阶段恒返回 <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>、一帧不出，逐项排除封装/序列头/同步标志/对齐几何后
/// 判定不可用，改用 FFmpeg；完整取证过程见 docs\11-M1b交付说明.md 的选型章节。
/// </para>
/// <para>本程序集不得引用 WPF / Windows 桌面 SDK：表现层只接触 <see cref="DecodedVideoFrame"/> 里的
/// <c>byte[]</c>，自己决定怎么上屏。</para>
/// </summary>
public static class DecodePipeline
{
    /// <summary>当前实现状态标识，写入诊断信息，避免"看起来已经具备解码能力"的误判。</summary>
    public const string ImplementationState = "M1b-ffmpeg-autogen";

    /// <summary>该层是否已具备真实解码能力。</summary>
    public static bool IsImplemented => true;

    /// <summary>
    /// 创建 H.264 解码器。返回的实例非线程安全，必须在同一个线程上使用（推荐专职解码线程）。
    /// </summary>
    /// <param name="log">日志出口，可为空。</param>
    /// <remarks>
    /// 原生库由 <see cref="FfmpegRuntime"/> 在首次解码时定位并加载，缺失时 <see cref="IH264Decoder.TryDecode"/>
    /// 会抛 <see cref="H264DecodeException"/>，调用方不必预先探测。
    /// </remarks>
    public static IH264Decoder CreateH264Decoder(ILogSink? log = null) => new FfmpegH264Decoder(log);
}
