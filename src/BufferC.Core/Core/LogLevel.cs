using System.Text.Json.Serialization;

namespace BufferC.Core.Core;

/// <summary>日志分级（数值升序=越靠后越详细；过滤比较 level &gt; _minLevel / &lt;= maxLevel 依赖此序）：
/// Error/Warn 故障与异常路径（可 grep 定位）；Info 默认运行态；Debug/Trace 联调期帧级明细。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4,
}

