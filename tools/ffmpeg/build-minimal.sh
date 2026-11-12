#!/usr/bin/env bash
# 极简 FFmpeg（仅 H.264 解码）Windows x64 共享库构建脚本。
#
# 用法（在 MSYS2 MINGW64 环境里执行）：
#   MSYSTEM=MINGW64 C:/msys64/usr/bin/bash.exe -lc "bash tools/ffmpeg/build-minimal.sh"
#
# 可覆盖的环境变量：
#   FFMPEG_SRC   源码目录（默认 /d/ffmpeg-build/ffmpeg-9.0.1）
#   FFMPEG_PREFIX 安装前缀（默认 /d/ffmpeg-build/prefix）
#   WITH_SWRESAMPLE 1=同时构建极简 swresample（默认 1，见下方说明）
#   EXTRA_LDFLAGS 额外链接参数（默认 "-static -static-libgcc"，见下方说明）
#
# 为什么必须 -static -static-libgcc：
#   不加它时 avutil 会导入 libwinpthread-1.dll、swscale 会导入 libgcc_s_seh-1.dll。
#   这两个 MinGW 运行库不在应用输出目录，LoadLibrary 直接失败。加上后静态链进 DLL，
#   产物只剩 KERNEL32 / msvcrt / bcrypt 三个系统依赖，形态与旧 BtbN 构建一致。
#
# 为什么默认带 swresample：
#   完整构建的 avcodec-63.dll 导入表里有 swresample-7.dll；缺它时 LoadLibrary 失败，
#   FFmpeg.AutoGen 的 DynamicallyLoadedBindings 初始化会把所有函数指针留成抛异常的
#   空壳（实证：NotSupportedException）。极简构建下 avcodec 是否仍导入 swresample
#   取决于 configure 的依赖判定，留一个几十 KB 的 swresample 是最省事的保险。
set -euo pipefail

SRC="${FFMPEG_SRC:-/d/ffmpeg-build/ffmpeg-9.0.1}"
PREFIX="${FFMPEG_PREFIX:-/d/ffmpeg-build/prefix}"
WITH_SWRESAMPLE="${WITH_SWRESAMPLE:-1}"
EXTRA_LDFLAGS="${EXTRA_LDFLAGS:--static -static-libgcc}"

echo "== 源码：$SRC"
echo "== 前缀：$PREFIX"
echo "== swresample：$WITH_SWRESAMPLE"
echo "== 额外链接参数：$EXTRA_LDFLAGS"

cd "$SRC"
make distclean >/dev/null 2>&1 || true

ARGS=(
  --prefix="$PREFIX"
  --arch=x86_64
  --target-os=mingw32
  --enable-shared
  --disable-static
  --disable-programs
  --disable-doc
  --disable-network
  --disable-autodetect
  --disable-everything
  --disable-avdevice
  --disable-avfilter
  --disable-avformat
  --enable-decoder=h264
  --enable-parser=h264
  --enable-swscale
  --enable-w32threads
  --extra-ldflags="$EXTRA_LDFLAGS"
)

if [ "$WITH_SWRESAMPLE" = "1" ]; then
  echo "-- 保留极简 swresample（avcodec 可能硬依赖它）"
else
  ARGS+=(--disable-swresample)
fi

echo "== configure 参数："
printf '   %s\n' "${ARGS[@]}"

./configure "${ARGS[@]}"

echo "== make -j$(nproc)"
make -j"$(nproc)"
make install

echo "== 产物："
ls -l "$PREFIX/bin" || true
