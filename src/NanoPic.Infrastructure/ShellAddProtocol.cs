using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NanoPic.Infrastructure;

/// <summary>Shell 请求的来源，仅用于日志与诊断。</summary>
public enum ShellAddOrigin
{
    Unknown = 0,
    ExplorerDropTarget = 1
}

public enum ShellAddMessageKind
{
    None = 0,
    AddPaths = 1,
    RegisterInstance = 2,
    InstanceActivated = 3,
    UnregisterInstance = 4,
    Ping = 5
}

public enum ShellAddStatus
{
    /// <summary>请求已复制并进入目标窗口的导入队列。</summary>
    Accepted = 0,
    /// <summary>相同请求 ID 已处理过，本次忽略。</summary>
    Duplicate = 1,
    /// <summary>当前用户会话内没有可接收的窗口。</summary>
    NoTarget = 2,
    /// <summary>协议版本或消息格式不被接受。</summary>
    ProtocolMismatch = 3,
    /// <summary>请求被目标拒绝（例如载荷超限）。</summary>
    Rejected = 4,
    /// <summary>传输或处理过程中发生错误。</summary>
    Error = 5
}

/// <summary>一次“添加到 NanoPic”请求：由 COM DropTarget 同步提取后即与 Shell 完全脱钩。</summary>
public sealed record ShellAddRequest(
    Guid RequestId,
    ShellAddOrigin Origin,
    IReadOnlyList<string> Paths,
    bool ActivateWindow,
    int UnavailableItemCount);

public sealed record ShellAddInstanceRegistration(
    int ProcessId,
    string EndpointName,
    long WindowHandle,
    long ActivationTicks);

public sealed record ShellAddMessage(
    ShellAddMessageKind Kind,
    ShellAddRequest? Request,
    ShellAddInstanceRegistration? Registration,
    int ProcessId,
    long ActivationTicks);

/// <summary>
/// 请求帧格式：魔数 + 协议版本 + 消息类型 + 载荷长度 + 载荷。所有字符串按“长度前缀 + UTF-8”写入，
/// 不做任何命令行拼接，也不依赖平台默认编码。
/// </summary>
public static class ShellAddProtocol
{
    public const int ProtocolVersion = 1;

    /// <summary>单个请求载荷上限，仅用于防止异常客户端耗尽内存，不是文件数量上限。</summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    private static readonly byte[] Magic = { (byte)'N', (byte)'P', (byte)'S', (byte)'A' };

    public static byte[] Encode(ShellAddMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        var payload = EncodePayload(message);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException("Shell 请求载荷超过协议上限。");
        }

        var frame = new byte[Magic.Length + 12 + payload.Length];
        Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
        WriteInt32(frame, Magic.Length, ProtocolVersion);
        WriteInt32(frame, Magic.Length + 4, (int)message.Kind);
        WriteInt32(frame, Magic.Length + 8, payload.Length);
        Buffer.BlockCopy(payload, 0, frame, Magic.Length + 12, payload.Length);
        return frame;
    }

    public static byte[] EncodeResponse(ShellAddStatus status, string? diagnostic)
    {
        var text = Encoding.UTF8.GetBytes(diagnostic ?? string.Empty);
        var frame = new byte[Magic.Length + 12 + text.Length];
        Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
        WriteInt32(frame, Magic.Length, ProtocolVersion);
        WriteInt32(frame, Magic.Length + 4, (int)status);
        WriteInt32(frame, Magic.Length + 8, text.Length);
        Buffer.BlockCopy(text, 0, frame, Magic.Length + 12, text.Length);
        return frame;
    }

    public static bool TryDecodeResponse(byte[] frame, out ShellAddStatus status, out string diagnostic)
    {
        status = ShellAddStatus.Error;
        diagnostic = string.Empty;
        if (frame is null || frame.Length < Magic.Length + 12 || !HasMagic(frame))
        {
            return false;
        }

        if (ReadInt32(frame, Magic.Length) != ProtocolVersion)
        {
            status = ShellAddStatus.ProtocolMismatch;
            return false;
        }

        var raw = ReadInt32(frame, Magic.Length + 4);
        if (raw < 0 || raw > (int)ShellAddStatus.Error)
        {
            return false;
        }

        var length = ReadInt32(frame, Magic.Length + 8);
        if (length < 0 || Magic.Length + 12 + length > frame.Length)
        {
            return false;
        }

        status = (ShellAddStatus)raw;
        diagnostic = Encoding.UTF8.GetString(frame, Magic.Length + 12, length);
        return true;
    }

    /// <summary>从流中读取一个完整帧；返回 false 表示连接关闭或帧不合法。</summary>
    public static bool TryReadFrame(Stream stream, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        var header = new byte[Magic.Length + 12];
        if (!TryReadExactly(stream, header, header.Length))
        {
            return false;
        }

        if (!HasMagic(header))
        {
            return false;
        }

        var length = ReadInt32(header, Magic.Length + 8);
        if (length < 0 || length > MaxPayloadBytes)
        {
            return false;
        }

        frame = new byte[header.Length + length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        return length == 0 || TryReadExactlyInto(stream, frame, header.Length, length);
    }

    public static bool TryDecode(byte[] frame, out ShellAddMessage message, out ShellAddStatus failure)
    {
        message = new ShellAddMessage(ShellAddMessageKind.None, null, null, 0, 0);
        failure = ShellAddStatus.Rejected;
        if (frame is null || frame.Length < Magic.Length + 12 || !HasMagic(frame))
        {
            return false;
        }

        if (ReadInt32(frame, Magic.Length) != ProtocolVersion)
        {
            failure = ShellAddStatus.ProtocolMismatch;
            return false;
        }

        var kindValue = ReadInt32(frame, Magic.Length + 4);
        var length = ReadInt32(frame, Magic.Length + 8);
        if (length < 0 || Magic.Length + 12 + length > frame.Length)
        {
            return false;
        }

        if (kindValue is < (int)ShellAddMessageKind.AddPaths or > (int)ShellAddMessageKind.Ping)
        {
            return false;
        }

        try
        {
            var kind = (ShellAddMessageKind)kindValue;
            using var stream = new MemoryStream(frame, Magic.Length + 12, length, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            switch (kind)
            {
                case ShellAddMessageKind.AddPaths:
                {
                    var requestId = new Guid(reader.ReadBytes(16));
                    var origin = reader.ReadInt32();
                    var activate = reader.ReadBoolean();
                    var unavailable = reader.ReadInt32();
                    var count = reader.ReadInt32();
                    if (count < 0 || unavailable < 0)
                    {
                        return false;
                    }

                    var paths = new List<string>(Math.Min(count, 4096));
                    for (var i = 0; i < count; i++)
                    {
                        paths.Add(ReadString(reader));
                    }

                    message = new ShellAddMessage(
                        kind,
                        new ShellAddRequest(
                            requestId,
                            origin == (int)ShellAddOrigin.ExplorerDropTarget ? ShellAddOrigin.ExplorerDropTarget : ShellAddOrigin.Unknown,
                            paths,
                            activate,
                            unavailable),
                        null,
                        0,
                        0);
                    return true;
                }

                case ShellAddMessageKind.RegisterInstance:
                {
                    var pid = reader.ReadInt32();
                    var endpoint = ReadString(reader);
                    var handle = reader.ReadInt64();
                    var ticks = reader.ReadInt64();
                    message = new ShellAddMessage(
                        kind,
                        null,
                        new ShellAddInstanceRegistration(pid, endpoint, handle, ticks),
                        pid,
                        ticks);
                    return true;
                }

                case ShellAddMessageKind.InstanceActivated:
                case ShellAddMessageKind.UnregisterInstance:
                {
                    var pid = reader.ReadInt32();
                    var ticks = reader.ReadInt64();
                    message = new ShellAddMessage(kind, null, null, pid, ticks);
                    return true;
                }

                default:
                    message = new ShellAddMessage(ShellAddMessageKind.Ping, null, null, 0, 0);
                    return true;
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
        {
            return false;
        }
    }

    private static byte[] EncodePayload(ShellAddMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            switch (message.Kind)
            {
                case ShellAddMessageKind.AddPaths:
                {
                    var request = message.Request ?? throw new InvalidOperationException("AddPaths 消息缺少请求内容。");
                    writer.Write(request.RequestId.ToByteArray());
                    writer.Write((int)request.Origin);
                    writer.Write(request.ActivateWindow);
                    writer.Write(request.UnavailableItemCount);
                    writer.Write(request.Paths.Count);
                    foreach (var path in request.Paths)
                    {
                        WriteString(writer, path);
                    }

                    break;
                }

                case ShellAddMessageKind.RegisterInstance:
                {
                    var registration = message.Registration ?? throw new InvalidOperationException("RegisterInstance 消息缺少登记内容。");
                    writer.Write(registration.ProcessId);
                    WriteString(writer, registration.EndpointName);
                    writer.Write(registration.WindowHandle);
                    writer.Write(registration.ActivationTicks);
                    break;
                }

                case ShellAddMessageKind.InstanceActivated:
                case ShellAddMessageKind.UnregisterInstance:
                    writer.Write(message.ProcessId);
                    writer.Write(message.ActivationTicks);
                    break;

                default:
                    break;
            }
        }

        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaxPayloadBytes)
        {
            throw new EndOfStreamException("字符串长度前缀不合法。");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("字符串内容不完整。");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool HasMagic(byte[] buffer)
    {
        for (var i = 0; i < Magic.Length; i++)
        {
            if (buffer[i] != Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadExactly(Stream stream, byte[] buffer, int count) =>
        TryReadExactlyInto(stream, buffer, 0, count);

    private static bool TryReadExactlyInto(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            int chunk;
            try
            {
                chunk = stream.Read(buffer, offset + read, count - read);
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (chunk <= 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    internal static string FormatInvariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
