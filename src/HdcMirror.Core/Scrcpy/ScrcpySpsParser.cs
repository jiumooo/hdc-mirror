using System.Globalization;

namespace HdcMirror.Core.Scrcpy;

/// <summary>SPS 解析结果（H.264 sequence parameter set 里与投屏相关的字段）。</summary>
public sealed record ScrcpySps
{
    /// <summary>显示宽度（像素，已扣除裁剪）。</summary>
    public int Width { get; init; }

    /// <summary>显示高度（像素，已扣除裁剪）。</summary>
    public int Height { get; init; }

    /// <summary>profile_idc。</summary>
    public int ProfileIdc { get; init; }

    /// <summary>profile 的可读名，例如 <c>High</c>。</summary>
    public string ProfileName { get; init; } = "Unknown";

    /// <summary>level_idc。</summary>
    public int LevelIdc { get; init; }

    /// <summary>level 的可读形式，例如 <c>5.2</c>。</summary>
    public string LevelText { get; init; } = "?";

    /// <summary>色度采样格式：0=单色、1=4:2:0、2=4:2:2、3=4:4:4。</summary>
    public int ChromaFormatIdc { get; init; }

    /// <summary>亮度位深。</summary>
    public int BitDepthLuma { get; init; }

    /// <summary>色度位深。</summary>
    public int BitDepthChroma { get; init; }

    /// <summary>是否只有帧编码（无场编码）。</summary>
    public bool FrameMbsOnly { get; init; }

    /// <summary><c>High L5.2 4:2:0 8bit 1260x2720</c> 这样的一行摘要，供日志与界面直接使用。</summary>
    public string Describe()
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0} L{1} {2} {3}bit {4}x{5}",
            ProfileName,
            LevelText,
            ChromaText(ChromaFormatIdc),
            BitDepthLuma,
            Width,
            Height);

    private static string ChromaText(int chromaFormatIdc) => chromaFormatIdc switch
    {
        0 => "mono",
        1 => "4:2:0",
        2 => "4:2:2",
        3 => "4:4:4",
        _ => "chroma?" + chromaFormatIdc.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// SPS（NAL type 7）解析器。实现沿用 POC 里已验证的那份：
/// 去 emulation prevention → 按位读 → 用裁剪量算出真实宽高。
/// <para>
/// 只解析到 <c>frame_cropping_flag</c> 为止，VUI 之后的内容与投屏无关。
/// </para>
/// </summary>
public static class ScrcpySpsParser
{
    private const int NalTypeSps = 7;
    private const int NalTypeIdr = 5;

    /// <summary>解析一个裸 SPS NAL（允许带 Annex B 起始码）。</summary>
    /// <param name="nal">SPS NAL 字节。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="FormatException">不是 SPS，或比特流不完整。</exception>
    public static ScrcpySps Parse(ReadOnlySpan<byte> nal)
    {
        var payload = StripStartCode(nal);
        if (payload.Length < 4)
        {
            throw new FormatException("SPS 数据过短，无法解析。");
        }

        var header = payload[0];
        var nalType = header & 0x1F;
        if (nalType != NalTypeSps)
        {
            throw new FormatException($"NAL 类型是 {nalType}，不是 SPS(7)。");
        }

        var rbsp = RemoveEmulationPrevention(payload[1..]);
        var reader = new BitReader(rbsp);

        var profileIdc = (int)reader.ReadBits(8);
        reader.ReadBits(8); // constraint_setN_flags + reserved_zero_2bits
        var levelIdc = (int)reader.ReadBits(8);
        reader.ReadUe(); // seq_parameter_set_id

        var chromaFormatIdc = 1;
        var bitDepthLuma = 8;
        var bitDepthChroma = 8;

        if (IsHighProfile(profileIdc))
        {
            chromaFormatIdc = (int)reader.ReadUe();
            if (chromaFormatIdc == 3)
            {
                reader.ReadBit(); // separate_colour_plane_flag
            }

            bitDepthLuma = (int)reader.ReadUe() + 8;
            bitDepthChroma = (int)reader.ReadUe() + 8;
            reader.ReadBit(); // qpprime_y_zero_transform_bypass_flag

            if (reader.ReadBit() == 1) // seq_scaling_matrix_present_flag
            {
                var listCount = chromaFormatIdc != 3 ? 8 : 12;
                for (var i = 0; i < listCount; i++)
                {
                    if (reader.ReadBit() == 1)
                    {
                        SkipScalingList(reader, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        reader.ReadUe(); // log2_max_frame_num_minus4
        var picOrderCntType = reader.ReadUe();
        if (picOrderCntType == 0)
        {
            reader.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (picOrderCntType == 1)
        {
            reader.ReadBit();  // delta_pic_order_always_zero_flag
            reader.ReadSe();   // offset_for_non_ref_pic
            reader.ReadSe();   // offset_for_top_to_bottom_field
            var cycleLength = reader.ReadUe();
            if (cycleLength > 255)
            {
                throw new FormatException($"pic_order_cnt_type=1 的循环长度 {cycleLength} 不合理。");
            }

            for (var i = 0; i < cycleLength; i++)
            {
                reader.ReadSe();
            }
        }

        reader.ReadUe(); // max_num_ref_frames
        reader.ReadBit(); // gaps_in_frame_num_value_allowed_flag

        var widthInMbs = reader.ReadUe() + 1;
        var heightInMapUnits = reader.ReadUe() + 1;
        if (widthInMbs > 4096 || heightInMapUnits > 4096)
        {
            throw new FormatException($"宏块尺寸 {widthInMbs}x{heightInMapUnits} 不合理，SPS 可能被截断。");
        }

        var frameMbsOnly = reader.ReadBit() == 1;
        if (!frameMbsOnly)
        {
            reader.ReadBit(); // mb_adaptive_frame_field_flag
        }

        reader.ReadBit(); // direct_8x8_inference_flag

        var cropLeft = 0;
        var cropRight = 0;
        var cropTop = 0;
        var cropBottom = 0;
        if (reader.ReadBit() == 1) // frame_cropping_flag
        {
            cropLeft = (int)reader.ReadUe();
            cropRight = (int)reader.ReadUe();
            cropTop = (int)reader.ReadUe();
            cropBottom = (int)reader.ReadUe();
        }

        // 裁剪单位：色度 4:2:0 时 X 除以 2、Y 除以 2；4:2:2 时 X 除以 2、Y 不除；
        // 单色时都不除；场编码时高度单位再乘 2。
        var subWidthC = chromaFormatIdc == 3 ? 1 : 2;
        var subHeightC = chromaFormatIdc == 1 ? 2 : 1;
        var cropUnitX = chromaFormatIdc == 0 ? 1 : subWidthC;
        var frameHeightFactor = 2 - (frameMbsOnly ? 1 : 0);
        var cropUnitY = (chromaFormatIdc == 0 ? 1 : subHeightC) * frameHeightFactor;

        var width = (int)(widthInMbs * 16) - (cropUnitX * (cropLeft + cropRight));
        var height = (int)(heightInMapUnits * 16 * frameHeightFactor) - (cropUnitY * (cropTop + cropBottom));
        if (width <= 0 || height <= 0)
        {
            throw new FormatException($"由 SPS 算出的尺寸非法：{width}x{height}。");
        }

        return new ScrcpySps
        {
            Width = width,
            Height = height,
            ProfileIdc = profileIdc,
            ProfileName = ProfileName(profileIdc),
            LevelIdc = levelIdc,
            LevelText = LevelText(levelIdc),
            ChromaFormatIdc = chromaFormatIdc,
            BitDepthLuma = bitDepthLuma,
            BitDepthChroma = bitDepthChroma,
            FrameMbsOnly = frameMbsOnly,
        };
    }

    /// <summary>在 annexB 码流里找第一个 SPS 并解析。</summary>
    /// <param name="annexB">Annex B 字节（可含多个 NAL）。</param>
    /// <returns>找不到或不合法时返回 <see langword="null"/>。</returns>
    public static ScrcpySps? ParseAnnexB(ReadOnlySpan<byte> annexB)
    {
        if (!TryFindNal(annexB, NalTypeSps, out var spsNal))
        {
            return null;
        }

        try
        {
            return Parse(spsNal);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>判断 Annex B 字节里是否含 IDR 切片（NAL type 5）。</summary>
    /// <param name="annexB">Annex B 字节。</param>
    public static bool ContainsIdrSlice(ReadOnlySpan<byte> annexB) => TryFindNal(annexB, NalTypeIdr, out _);

    /// <summary>取 profile_idc 的可读名。</summary>
    /// <param name="profileIdc">profile_idc。</param>
    public static string ProfileName(int profileIdc) => profileIdc switch
    {
        66 => "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        110 => "High 10",
        122 => "High 4:2:2",
        244 => "High 4:4:4",
        44 => "CAVLC 4:4:4",
        118 => "High 10 Intra",
        128 => "Stereo High",
        138 => "Multiview High",
        139 => "Multiview Depth",
        134 => "MFC High",
        135 => "MFC Depth",
        _ => "profile?" + profileIdc.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>取 level_idc 的可读形式（52 → 5.2，9 → 1b）。</summary>
    /// <param name="levelIdc">level_idc。</param>
    public static string LevelText(int levelIdc)
    {
        if (levelIdc == 9)
        {
            return "1b";
        }

        var major = levelIdc / 10;
        var minor = levelIdc % 10;
        return string.Format(CultureInfo.InvariantCulture, "{0}.{1}", major, minor);
    }

    private static bool IsHighProfile(int profileIdc)
        => profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

    private static ReadOnlySpan<byte> StripStartCode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
        {
            return data[4..];
        }

        if (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1)
        {
            return data[3..];
        }

        return data;
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var written = 0;
        var zeros = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            if (zeros >= 2 && value == 0x03)
            {
                // 00 00 03 → 丢弃这个 03，后面的字节按原样接上。
                zeros = 0;
                continue;
            }

            output[written++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return written == output.Length ? output : output[..written];
    }

    private static bool TryFindNal(ReadOnlySpan<byte> data, int nalType, out ReadOnlySpan<byte> nal)
    {
        nal = default;
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
            {
                continue;
            }

            var start = i + 3;
            if (start >= data.Length || (data[start] & 0x1F) != nalType)
            {
                continue;
            }

            var end = FindNextStartCode(data, start + 1);
            while (end > start + 1 && data[end - 1] == 0)
            {
                end--; // 去掉尾部的 trailing_zero_8bits
            }

            nal = data[start..end];
            return nal.Length > 1;
        }

        return false;
    }

    private static int FindNextStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                return i;
            }
        }

        return data.Length;
    }

    private static void SkipScalingList(BitReader reader, int size)
    {
        var lastScale = 8;
        var nextScale = 8;
        for (var i = 0; i < size; i++)
        {
            if (nextScale != 0)
            {
                var delta = reader.ReadSe();
                nextScale = (lastScale + delta + 256) % 256;
            }

            if (nextScale != 0)
            {
                lastScale = nextScale;
            }
        }
    }

    /// <summary>RBSP 位读取器（不含 emulation prevention 字节）。</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bitPosition;

        internal BitReader(byte[] data) => _data = data;

        private int RemainingBits => (_data.Length * 8) - _bitPosition;

        internal int ReadBit()
        {
            if (RemainingBits <= 0)
            {
                throw new FormatException("SPS 比特流提前结束。");
            }

            var value = (_data[_bitPosition >> 3] >> (7 - (_bitPosition & 7))) & 1;
            _bitPosition++;
            return value;
        }

        internal uint ReadBits(int count)
        {
            if (count < 0 || count > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "位数必须为 0~32。");
            }

            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (uint)ReadBit();
            }

            return value;
        }

        internal uint ReadUe()
        {
            var leadingZeros = 0;
            while (ReadBit() == 0)
            {
                leadingZeros++;
                if (leadingZeros > 31)
                {
                    throw new FormatException("exp-golomb 前导零超过 31 位。");
                }
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            return (uint)(((1UL << leadingZeros) - 1) + ReadBits(leadingZeros));
        }

        internal int ReadSe()
        {
            var value = ReadUe();
            return (value & 1) == 1 ? (int)((value + 1) / 2) : -(int)(value / 2);
        }
    }
}
