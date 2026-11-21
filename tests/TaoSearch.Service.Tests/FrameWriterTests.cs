using TaoSearch.Kernel.Contracts;
using TaoSearch.Service;
using Xunit;

namespace TaoSearch.Service.Tests;

/// <summary>帧编码协议测试（协议 §3.4）：[4B 载荷长度 LE][2B 版本][2B 方法号][4B 序号][载荷]。</summary>
public class FrameWriterTests
{
    [Fact]
    public void Encode_HeaderFieldsAndPayloadLayout()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        byte[] frame = FrameWriter.Encode(method: 7, seq: 0x11223344, payload);

        Assert.Equal(12 + payload.Length, frame.Length);
        Assert.Equal(payload.Length, BitConverter.ToInt32(frame, 0));           // 4B 载荷长度
        Assert.Equal(IpcProtocol.Version, BitConverter.ToUInt16(frame, 4));     // 2B 版本
        Assert.Equal(7, BitConverter.ToUInt16(frame, 6));                       // 2B 方法号
        Assert.Equal(0x11223344u, BitConverter.ToUInt32(frame, 8));             // 4B 序号
        Assert.Equal(payload, frame[12..]);                                     // 载荷原样
    }

    [Fact]
    public void Encode_EmptyPayload_LengthIsHeaderOnly()
    {
        byte[] frame = FrameWriter.Encode(method: (ushort)IpcMethod.GetIndexStatus, seq: 0, []);

        Assert.Equal(12, frame.Length);
        Assert.Equal(0, BitConverter.ToInt32(frame, 0));
        Assert.Equal((ushort)IpcMethod.GetIndexStatus, BitConverter.ToUInt16(frame, 6));
    }

    [Fact]
    public void Encode_LargePayload_LengthCountsPayloadBytesOnly()
    {
        byte[] payload = new byte[64 * 1024];
        payload[0] = 0xAB;
        payload[^1] = 0xCD;

        byte[] frame = FrameWriter.Encode(method: 1, seq: 42, payload);

        Assert.Equal(12 + payload.Length, frame.Length);
        Assert.Equal(payload.Length, BitConverter.ToInt32(frame, 0));
        Assert.Equal(0xAB, frame[12]);
        Assert.Equal(0xCD, frame[^1]);
    }
}
