using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace NanoPic.Infrastructure;

/// <summary>
/// Shell 集成的作用域标识：当前用户 SID + 当前 Windows 会话。所有 Mutex 与管道名都带上该作用域，
/// 因此请求既不跨用户也不跨登录会话（RDP、快速用户切换各自独立）。
/// </summary>
public sealed class ShellAddIdentity
{
    private readonly string _scope;

    private ShellAddIdentity(string userSid, int sessionId, string scope)
    {
        UserSid = userSid;
        SessionId = sessionId;
        _scope = scope;
    }

    public string UserSid { get; }
    public int SessionId { get; }

    /// <summary>broker 选举用的会话内 Mutex；使用 Local\ 前缀，不跨会话。</summary>
    public string BrokerMutexName => @"Local\NanoPic.ShellAdd.Broker." + _scope;

    /// <summary>注册表操作串行化 Mutex。</summary>
    public string RegistryMutexName => @"Local\NanoPic.ShellIntegration.Registry." + _scope;

    public string BrokerPipeName => "NanoPic.ShellAdd.Broker." + _scope;

    public string CreateInstancePipeName(int processId) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "NanoPic.ShellAdd.Instance.{0}.{1}.{2}",
            _scope,
            processId,
            Guid.NewGuid().ToString("N"));

    public static ShellAddIdentity Current()
    {
        string sid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value ?? "unknown-user";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            sid = "unknown-user";
        }

        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return new ShellAddIdentity(sid, sessionId, string.Format(CultureInfo.InvariantCulture, "{0}.{1}", sid, sessionId));
    }

    /// <summary>为自动化测试创建互相隔离的命名作用域，避免与真实运行实例互相干扰。</summary>
    public static ShellAddIdentity CreateIsolated(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("测试作用域标签不能为空。", nameof(tag));
        var current = Current();
        return new ShellAddIdentity(
            current.UserSid,
            current.SessionId,
            string.Format(CultureInfo.InvariantCulture, "test.{0}.{1}", current.SessionId, tag));
    }
}

/// <summary>按请求 ID 去重，避免 broker 切换或客户端重试导致同一选择集被导入两次。</summary>
public sealed class ShellAddRequestLedger
{
    private sealed class LedgerEntry
    {
        public LedgerEntry(Guid requestId, DateTimeOffset seenAt)
        {
            RequestId = requestId;
            SeenAt = seenAt;
        }

        public Guid RequestId { get; }
        public DateTimeOffset SeenAt { get; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = new();
    private readonly Queue<LedgerEntry> _order = new();
    private readonly int _capacity;
    private readonly TimeSpan _retention;

    public ShellAddRequestLedger(int capacity = 512, TimeSpan? retention = null)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _retention = retention ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>首次见到该请求返回 true；重复请求返回 false。</summary>
    public bool TryBegin(Guid requestId)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_seen.TryGetValue(requestId, out var seenAt) && now - seenAt <= _retention)
            {
                return false;
            }

            _seen[requestId] = now;
            _order.Enqueue(new LedgerEntry(requestId, now));
            while (_seen.Count > _capacity && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                if (_seen.TryGetValue(oldest.RequestId, out var currentSeenAt) && currentSeenAt == oldest.SeenAt)
                {
                    _seen.Remove(oldest.RequestId);
                }
            }

            return true;
        }
    }

    /// <summary>请求没有进入任何目标队列时撤销占位，使相同请求 ID 可以安全重试。</summary>
    public void Forget(Guid requestId)
    {
        lock (_sync)
        {
            _seen.Remove(requestId);
        }
    }
}

/// <summary>
/// 单线程接受连接的命名管道服务端。ACL 显式限制为当前用户 SID，不依赖默认 DACL。
/// 处理回调运行在管道线程上，调用方负责切换到自己的 UI 线程。
/// </summary>
public sealed class ShellAddPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<ShellAddMessage, ShellAddStatus> _handler;
    private readonly Action<string, Exception?>? _log;
    private readonly object _sync = new();
    private Thread? _thread;
    private NamedPipeServerStream? _current;
    private volatile bool _stopping;

    public ShellAddPipeServer(string pipeName, Func<ShellAddMessage, ShellAddStatus> handler, Action<string, Exception?>? log = null)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        lock (_sync)
        {
            if (_thread is not null)
            {
                return;
            }

            // 先创建首个实例，确保 Start 返回后端点已可连接：新 broker 必须在对外接受请求前就绪。
            _current = CreateServerStream();
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "NanoPic.ShellAdd.PipeServer"
            };
            _thread.Start();
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确定当前用户 SID。");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            inBufferSize: 64 * 1024,
            outBufferSize: 4 * 1024,
            pipeSecurity: security);
    }

    private void RunLoop()
    {
        while (!_stopping)
        {
            NamedPipeServerStream? server;
            lock (_sync)
            {
                server = _current;
                if (server is null && !_stopping)
                {
                    try
                    {
                        server = _current = CreateServerStream();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _log?.Invoke("无法创建 Shell 请求管道实例。", exception);
                        return;
                    }
                }
            }

            if (server is null)
            {
                return;
            }

            try
            {
                server.WaitForConnection();
                HandleConnection(server);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_stopping)
                {
                    _log?.Invoke("Shell 请求管道连接中断。", exception);
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_current, server))
                    {
                        _current = null;
                    }
                }

                try
                {
                    server.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream server)
    {
        if (!ShellAddProtocol.TryReadFrame(server, out var frame))
        {
            return;
        }

        ShellAddStatus status;
        var diagnostic = string.Empty;
        if (!ShellAddProtocol.TryDecode(frame, out var message, out var failure))
        {
            status = failure;
            diagnostic = "无法解析请求帧。";
        }
        else
        {
            try
            {
                status = _handler(message);
            }
            catch (Exception exception)
            {
                _log?.Invoke("处理 Shell 请求时发生异常。", exception);
                status = ShellAddStatus.Error;
                diagnostic = exception.GetType().Name;
            }
        }

        var response = ShellAddProtocol.EncodeResponse(status, diagnostic);
        server.Write(response, 0, response.Length);
        server.Flush();
        try
        {
            server.WaitForPipeDrain();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _stopping = true;
        NamedPipeServerStream? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }

        // 唤醒仍阻塞在 WaitForConnection 的循环：先建立一次本地连接，再释放实例。
        try
        {
            using var waker = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
            waker.Connect(100);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }

        try
        {
            current?.Dispose();
        }
        catch (IOException)
        {
        }

        var thread = _thread;
        _thread = null;
        thread?.Join(TimeSpan.FromSeconds(2));
    }
}

public static class ShellAddPipeClient
{
    public static ShellAddStatus Send(
        string pipeName,
        ShellAddMessage message,
        int connectTimeoutMilliseconds,
        int responseTimeoutMilliseconds,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(connectTimeoutMilliseconds);
            var frame = ShellAddProtocol.Encode(message);
            client.Write(frame, 0, frame.Length);
            client.Flush();

            // 命名管道同步读取不支持超时，用后台读取 + 有界等待，避免调用线程（可能是 STA）无限阻塞。
            var stream = client;
            byte[]? response = null;
            var read = Task.Run(() => ShellAddProtocol.TryReadFrame(stream, out response));
            if (!read.Wait(responseTimeoutMilliseconds))
            {
                diagnostic = "等待目标确认超时。";
                return ShellAddStatus.Error;
            }

            if (!read.Result || response is null)
            {
                diagnostic = "目标未返回确认。";
                return ShellAddStatus.Error;
            }

            if (!ShellAddProtocol.TryDecodeResponse(response, out var status, out var responseDiagnostic))
            {
                diagnostic = "确认帧不合法。";
                return status == ShellAddStatus.ProtocolMismatch ? ShellAddStatus.ProtocolMismatch : ShellAddStatus.Error;
            }

            diagnostic = responseDiagnostic;
            return status;
        }
        catch (TimeoutException)
        {
            diagnostic = "无法连接到目标端点。";
            return ShellAddStatus.NoTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
            diagnostic = exception.Message;
            return ShellAddStatus.Error;
        }
        finally
        {
            client?.Dispose();
        }
    }
}
