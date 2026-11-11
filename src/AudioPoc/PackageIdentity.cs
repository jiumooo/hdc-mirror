using System.Runtime.InteropServices;
using System.Text;

namespace HdcMirror.AudioPoc;

/// <summary>
/// 查询当前进程是否具备 MSIX 包身份。
/// AudioPlaybackConnection 是否需要包身份，是本次 POC 必须回答的问题之一，
/// 因此把「有没有包身份」作为一条独立证据打印出来。
/// </summary>
internal static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    public static (bool HasIdentity, string Detail) Query()
    {
        var length = 0;
        var probe = GetCurrentPackageFullName(ref length, null);

        if (probe == AppModelErrorNoPackage)
        {
            return (false, "无包身份（APPMODEL_ERROR_NO_PACKAGE = 15700），进程以普通 Win32 方式运行");
        }

        if (probe == ErrorInsufficientBuffer && length > 0)
        {
            var buffer = new StringBuilder(length);
            var rc = GetCurrentPackageFullName(ref length, buffer);
            if (rc == 0)
            {
                return (true, "有包身份（PackageFullName = " + buffer + "）");
            }

            return (false, "GetCurrentPackageFullName 二次调用返回 " + rc);
        }

        return (false, "GetCurrentPackageFullName 返回未知码 " + probe);
    }
}
