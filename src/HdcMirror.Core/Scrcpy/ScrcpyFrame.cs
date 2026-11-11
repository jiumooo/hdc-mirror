namespace HdcMirror.Core.Scrcpy;

/// <summary>
/// 一帧视频数据（Annex B H.264 访问单元，一条 gRPC 消息 = 一帧）。
/// <para>
/// 配置帧（SPS+PPS 合包）不会出现在帧序列里 —— 它由 <see cref="ScrcpyClient"/> 截留为 extradata。
/// </para>
/// </summary>
public sealed class ScrcpyFrame
{
    /// <summary>构造一帧。</summary>
    /// <param name="annexB">Annex B 字节，首个 NAL 前带 4 字节起始码 <c>00 00 00 01</c>。</param>
    /// <param name="isKeyFrame">是否关键帧（IDR）。</param>
    /// <param name="pts">设备侧纳秒时间戳。</param>
    /// <param name="flags">原始帧标志（8=配置帧、2=IDR、0=普通帧），仅用于诊断。</param>
    public ScrcpyFrame(byte[] annexB, bool isKeyFrame, long pts, int flags)
    {
        AnnexB = annexB ?? Array.Empty<byte>();
        IsKeyFrame = isKeyFrame;
        Pts = pts;
        Flags = flags;
    }

    /// <summary>Annex B 字节。</summary>
    public byte[] AnnexB { get; }

    /// <summary>是否关键帧（IDR）。</summary>
    public bool IsKeyFrame { get; }

    /// <summary>设备侧纳秒时间戳。</summary>
    public long Pts { get; }

    /// <summary>原始帧标志。</summary>
    public int Flags { get; }

    /// <summary>字节长度。</summary>
    public int Length => AnnexB.Length;
}
