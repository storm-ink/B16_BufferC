using System.Text.Json.Serialization;

namespace BufferC.Core.Core;

/// <summary>日志分级：Info 默认（现场运行期）；Debug/Trace 联调期（帧级明细）</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogLevel
{
    Info = 0,
    Debug = 1,
    Trace = 2,
}
