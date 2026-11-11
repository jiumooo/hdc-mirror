namespace HdcMirror.Decode.Video;

/// <summary>
/// H.264 解码器的抽象。实现只接受 <b>Annex B</b> 字节流（以 <c>00 00 00 01</c> 起始码分隔），
/// 一次 <see cref="TryDecode"/> 对应设备推来的一帧（一个 access unit）。
/// <para>线程模型：<b>非线程安全</b>。同一个实例的 <see cref="Configure"/> /
/// <see cref="TryDecode"/> / <see cref="Flush"/> / <see cref="Dispose"/> 必须在同一个线程上调用，
/// 并且该线程需要是 MTA（实现会在首次使用时自己 <c>CoInitializeEx(MULTITHREADED)</c>）。</para>
/// </summary>
public interface IH264Decoder : IDisposable
{
    /// <summary>实现名称，写进日志便于区分走了哪条解码路径。</summary>
    string Name { get; }

    /// <summary>当前输出画面宽度，尚未解出帧时为 0。</summary>
    int FrameWidth { get; }

    /// <summary>当前输出画面高度，尚未解出帧时为 0。</summary>
    int FrameHeight { get; }

    /// <summary>运行期因配置帧（SPS/PPS）变化而重建解码器的次数。</summary>
    int ReconfigurationCount { get; }

    /// <summary>
    /// 设置/更新解码所需的 SPS+PPS（Annex B，通常是从 gRPC 流里截留的 32 字节配置帧）。
    /// <para>
    /// 幂等：内容与上次相同则直接返回；不同则拆掉旧解码器重建 —— 协议规定每次 IDR 都会重发配置帧，
    /// 运行期换分辨率的场景必须能跟上，而 MF 的 <c>SetInputType</c> 只在非流状态可用，所以走重建。
    /// </para>
    /// </summary>
    /// <param name="annexBConfiguration">SPS+PPS（Annex B，可含多个 NAL）。</param>
    void Configure(ReadOnlySpan<byte> annexBConfiguration);

    /// <summary>
    /// 解一帧。
    /// </summary>
    /// <param name="annexBAccessUnit">一帧的 Annex B 码流（可能含配置帧前缀）。</param>
    /// <param name="ptsNanoseconds">设备侧呈现时间戳（纳秒）。</param>
    /// <param name="frame">解出新画面时返回帧对象；需要更多输入时返回 <see langword="null"/>。</param>
    /// <returns>解码器是否正常工作（<see langword="false"/> 表示本帧没产出，不是错误）。</returns>
    /// <exception cref="H264DecodeException">解码器已损坏或码流被 MF 判为非法。</exception>
    bool TryDecode(ReadOnlySpan<byte> annexBAccessUnit, long ptsNanoseconds, out DecodedVideoFrame? frame);

    /// <summary>丢弃解码器里的参考帧与待输出数据（重连或长时间静默后调用，避免参考帧错位导致花屏）。</summary>
    void Flush();
}
