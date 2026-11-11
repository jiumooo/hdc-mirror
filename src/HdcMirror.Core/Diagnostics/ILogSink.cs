namespace HdcMirror.Core.Diagnostics;

/// <summary>日志级别。数值越大越严重，用于过滤。</summary>
public enum LogLevel
{
    /// <summary>仅在排障时打开的细节。</summary>
    Debug = 0,

    /// <summary>正常流程中的关键节点。</summary>
    Info = 1,

    /// <summary>可恢复的异常情况，功能继续。</summary>
    Warn = 2,

    /// <summary>功能失败，需要上层处理。</summary>
    Error = 3,
}

/// <summary>
/// 诊断日志出口。协议层与 hdc 层只依赖本接口，写到哪里由宿主决定。
/// <para>
/// 硬性约束：所有参数都必须是字符串。**禁止**把 protobuf 生成的消息对象传进来 ——
/// 设备侧契约的生成类 <c>toString()</c> 会抛 <c>IllegalStateException: missing method setResult</c>，
/// 在日志里拼接它等于在异常路径上再抛一次异常。记录 protobuf 内容时只记长度、标志位、时间戳这类标量。
/// </para>
/// </summary>
public interface ILogSink
{
    /// <summary>写入一条日志。</summary>
    /// <param name="level">级别。</param>
    /// <param name="category">来源分类，例如 <c>hdc</c> / <c>scrcpy</c> / <c>daemon</c>。</param>
    /// <param name="message">已格式化好的消息文本，不得是 protobuf 消息对象。</param>
    /// <param name="exception">可选异常，实现方按自身格式追加。</param>
    void Write(LogLevel level, string category, string message, Exception? exception = null);

    /// <summary>写入 <see cref="LogLevel.Debug"/> 日志。</summary>
    void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    /// <summary>写入 <see cref="LogLevel.Info"/> 日志。</summary>
    void Info(string category, string message) => Write(LogLevel.Info, category, message);

    /// <summary>写入 <see cref="LogLevel.Warn"/> 日志。</summary>
    void Warn(string category, string message, Exception? exception = null)
        => Write(LogLevel.Warn, category, message, exception);

    /// <summary>写入 <see cref="LogLevel.Error"/> 日志。</summary>
    void Error(string category, string message, Exception? exception = null)
        => Write(LogLevel.Error, category, message, exception);
}

/// <summary>不做任何事的日志出口，用于测试或明确关闭诊断的场景。</summary>
public sealed class NullLogSink : ILogSink
{
    /// <summary>唯一实例。</summary>
    public static readonly NullLogSink Instance = new();

    private NullLogSink()
    {
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}
