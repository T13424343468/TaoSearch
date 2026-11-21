using System.Text.Json;
using System.Text.Json.Serialization;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Service;

/// <summary>
/// 服务端载荷序列化（AOT 源生成，零第三方依赖）：覆盖 8 方法全部请求/响应载荷与事件推送。
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QuerySpec))]
[JsonSerializable(typeof(ResultBatch))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(ContentPreview))]
[JsonSerializable(typeof(IndexStatus))]
[JsonSerializable(typeof(RebuildRequest))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(SubscribeAck))]
[JsonSerializable(typeof(UnsubscribeRequest))]
[JsonSerializable(typeof(RegisterParserRequest))]
[JsonSerializable(typeof(EventNotification))]
[JsonSerializable(typeof(FileEvent))]
[JsonSerializable(typeof(AckResponse))]
[JsonSerializable(typeof(MatchRangeTuple))]
internal sealed partial class ServicePayloadContext : JsonSerializerContext;

/// <summary>MatchRanges 元组序列化载体（值元组不直接进源生成契约）。</summary>
public sealed record MatchRangeTuple(int Start, int Length);

/// <summary>序列化薄壳：统一入口，后续换帧内压缩等只动这里。</summary>
public static class JsonSerializerShim
{
    public static byte[] Serialize<T>(T value) where T : class =>
        JsonSerializer.SerializeToUtf8Bytes(value, ServicePayloadContext.Default.Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) where T : class
    {
        var reader = new Utf8JsonReader(utf8);
        return JsonSerializer.Deserialize<T>(ref reader, ServicePayloadContext.Default.Options);
    }
}
