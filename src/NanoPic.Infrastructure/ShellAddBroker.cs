using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace NanoPic.Infrastructure;

public sealed record ShellAddInstanceSnapshot(int ProcessId, string EndpointName, long ActivationTicks, bool IsSelf);

public sealed class ShellAddServiceOptions
{
    public int ElectionPollMilliseconds { get; init; } = 500;
    public int HeartbeatMilliseconds { get; init; } = 2000;
    public int ConnectTimeoutMilliseconds { get; init; } = 600;
    public int ResponseTimeoutMilliseconds { get; init; } = 3000;

    /// <summary>broker 刚接任、实例尚未重新登记时的等待窗口，避免误判“无窗口可接收”。</summary>
    public int RecoveryGraceMilliseconds { get; init; } = 1500;

    public int DeliveryAttempts { get; init; } = 3;
}

/// <summary>
/// 当前用户会话内的 NanoPic 窗口实例登记表。目标选择只依据“最近一次激活”的单调时间戳，
/// 不使用 PID 大小或窗口枚举顺序。
/// </summary>
public sealed class ShellAddInstanceRegistry
{
    private sealed class Entry
    {
        public int ProcessId;
        public string EndpointName = string.Empty;
        public long WindowHandle;
        public long ActivationTicks;
        public bool IsSelf;
        public bool IsDead;
    }

    private readonly object _sync = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly Func<int, bool> _isProcessAlive;

    public ShellAddInstanceRegistry(Func<int, bool>? isProcessAlive = null) =>
        _isProcessAlive = isProcessAlive ?? DefaultIsProcessAlive;

    public bool HasEverRegistered { get; private set; }

    public void Register(ShellAddInstanceRegistration registration, bool isSelf = false)
    {
        if (registration is null) throw new ArgumentNullException(nameof(registration));

        lock (_sync)
        {
            HasEverRegistered = true;
            if (!_entries.TryGetValue(registration.ProcessId, out var entry))
            {
                entry = new Entry { ProcessId = registration.ProcessId };
                _entries[registration.ProcessId] = entry;
            }

            entry.EndpointName = registration.EndpointName;
            entry.WindowHandle = registration.WindowHandle;
            entry.IsSelf = isSelf || entry.IsSelf;
            entry.IsDead = false;
            if (registration.ActivationTicks > entry.ActivationTicks)
            {
                entry.ActivationTicks = registration.ActivationTicks;
            }
        }
    }

    public void Touch(int processId, long activationTicks)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(processId, out var entry) && activationTicks > entry.ActivationTicks)
            {
                entry.ActivationTicks = activationTicks;
                entry.IsDead = false;
            }
        }
    }

    public void Remove(int processId)
    {
        lock (_sync)
        {
            _entries.Remove(processId);
        }
    }

    public void MarkDead(int processId)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(processId, out var entry))
            {
                entry.IsDead = true;
            }
        }
    }

    /// <summary>按最近激活顺序返回仍存活的候选窗口。</summary>
    public IReadOnlyList<ShellAddInstanceSnapshot> RankedTargets()
    {
        lock (_sync)
        {
            var dead = new List<int>();
            var alive = new List<ShellAddInstanceSnapshot>();
            foreach (var entry in _entries.Values)
            {
                if (entry.IsDead || !_isProcessAlive(entry.ProcessId))
                {
                    dead.Add(entry.ProcessId);
                    continue;
                }

                alive.Add(new ShellAddInstanceSnapshot(entry.ProcessId, entry.EndpointName, entry.ActivationTicks, entry.IsSelf));
            }

            foreach (var processId in dead)
            {
                _entries.Remove(processId);
            }

            return alive
                .OrderByDescending(snapshot => snapshot.ActivationTicks)
                .ThenByDescending(snapshot => snapshot.IsSelf)
                .ToArray();
        }
    }

    private static bool DefaultIsProcessAlive(int processId)
    {
        if (processId == Process.GetCurrentProcess().Id)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// 每用户、每会话一个 broker：持有 <c>Local\</c> Mutex 的进程负责注册 COM class object 并把
/// Explorer 送来的请求路由到最近激活的 NanoPic 窗口；其余实例只登记自己并等待接任。
/// </summary>
public sealed class ShellAddService : IDisposable
{
    private readonly ShellAddIdentity _identity;
    private readonly ShellAddServiceOptions _options;
    private readonly Action<string, Exception?>? _log;
    private readonly ShellAddInstanceRegistry _registry;
    private readonly Action<ShellAddPipeServer> _startBrokerServer;
    private readonly ShellAddRequestLedger _brokerLedger = new();
    private readonly ShellAddRequestLedger _localLedger = new();
    private readonly object _sync = new();
    private readonly int _processId = Process.GetCurrentProcess().Id;

    private Mutex? _brokerMutex;
    private Thread? _electionThread;
    private Thread? _heartbeatThread;
    private ShellAddPipeServer? _brokerServer;
    private ShellAddPipeServer? _instanceServer;
    private ShellAddInstanceRegistration? _selfRegistration;
    private volatile bool _stopping;
    private volatile bool _isBrokerOwner;

    public ShellAddService(
        ShellAddIdentity identity,
        Action<string, Exception?>? log = null,
        ShellAddServiceOptions? options = null,
        Func<int, bool>? isProcessAlive = null)
        : this(identity, log, options, isProcessAlive, server => server.Start())
    {
    }

    internal ShellAddService(
        ShellAddIdentity identity,
        Action<string, Exception?>? log,
        ShellAddServiceOptions? options,
        Func<int, bool>? isProcessAlive,
        Action<ShellAddPipeServer> startBrokerServer)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _log = log;
        _options = options ?? new ShellAddServiceOptions();
        _registry = new ShellAddInstanceRegistry(isProcessAlive);
        _startBrokerServer = startBrokerServer ?? throw new ArgumentNullException(nameof(startBrokerServer));
    }

    /// <summary>把请求交给本进程窗口的导入队列；返回 Accepted 表示已进入队列（不等待扫描完成）。</summary>
    public Func<ShellAddRequest, ShellAddStatus>? LocalImportHandler { get; set; }

    /// <summary>本进程成为 broker owner 时回调：宿主在此注册并恢复 COM class object。</summary>
    public Action? BrokerOwnershipAcquired { get; set; }

    public bool IsBrokerOwner => _isBrokerOwner;

    public string? InstanceEndpointName => _selfRegistration?.EndpointName;

    public ShellAddIdentity Identity => _identity;

    /// <summary>普通窗口实例：开放自身端点、向 broker 登记，并参加选举。</summary>
    public void StartInstance(long windowHandle = 0)
    {
        lock (_sync)
        {
            if (_instanceServer is not null)
            {
                return;
            }

            var endpoint = _identity.CreateInstancePipeName(_processId);
            _instanceServer = new ShellAddPipeServer(endpoint, HandleInstanceMessage, _log);
            _instanceServer.Start();
            _selfRegistration = new ShellAddInstanceRegistration(_processId, endpoint, windowHandle, Stopwatch.GetTimestamp());
            _registry.Register(_selfRegistration, isSelf: true);
        }

        StartElection();
        StartHeartbeat();
    }

    /// <summary>COM embedding 进程：只参加选举，不创建窗口端点。</summary>
    public void StartEmbedding()
    {
        StartElection();
    }

    private void StartElection()
    {
        lock (_sync)
        {
            if (_electionThread is not null)
            {
                return;
            }

            _electionThread = new Thread(RunElection)
            {
                IsBackground = true,
                Name = "NanoPic.ShellAdd.Election"
            };
            _electionThread.Start();
        }
    }

    private void StartHeartbeat()
    {
        lock (_sync)
        {
            if (_heartbeatThread is not null)
            {
                return;
            }

            _heartbeatThread = new Thread(RunHeartbeat)
            {
                IsBackground = true,
                Name = "NanoPic.ShellAdd.Heartbeat"
            };
            _heartbeatThread.Start();
        }
    }

    private void RunElection()
    {
        try
        {
            _brokerMutex = new Mutex(initiallyOwned: false, _identity.BrokerMutexName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.IO.IOException or NotSupportedException)
        {
            _log?.Invoke("无法参加 Shell 集成 broker 选举。", exception);
            return;
        }

        while (!_stopping)
        {
            var owner = false;
            while (!_stopping && !owner)
            {
                try
                {
                    owner = _brokerMutex.WaitOne(_options.ElectionPollMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    // 上一任 broker 进程异常退出：Mutex 归本进程所有，直接接任。
                    owner = true;
                }
                catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }

            if (!owner)
            {
                return;
            }

            try
            {
                if (BecomeBroker())
                {
                    // Mutex 的所有权是线程绑定的：选举线程必须持续存活并在此处释放，
                    // 否则线程结束会让 Mutex 变成 abandoned，其他实例会在本进程仍是 broker 时抢到所有权。
                    while (!_stopping)
                    {
                        Thread.Sleep(100);
                    }

                    return;
                }
            }
            finally
            {
                try
                {
                    _brokerMutex.ReleaseMutex();
                }
                catch (Exception exception) when (exception is ApplicationException or ObjectDisposedException)
                {
                }
            }

            if (!_stopping)
            {
                // 临时初始化失败时保留本进程的接任能力，同时给其他实例一个公平接任窗口。
                Thread.Sleep(Math.Max(_options.HeartbeatMilliseconds, 1000));
            }
        }
    }

    private bool BecomeBroker()
    {
        ShellAddPipeServer? server = null;
        try
        {
            // 顺序很关键：端点先就绪，再宣布成为 owner（宿主随后 resume COM class object）。
            server = new ShellAddPipeServer(_identity.BrokerPipeName, HandleBrokerMessage, _log);
            _startBrokerServer(server);
            lock (_sync)
            {
                if (_stopping)
                {
                    server.Dispose();
                    return false;
                }

                _brokerServer = server;
            }

            _isBrokerOwner = true;
            _log?.Invoke("已成为当前会话的 Shell 集成 broker。", null);
            BrokerOwnershipAcquired?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            _isBrokerOwner = false;
            lock (_sync)
            {
                if (ReferenceEquals(_brokerServer, server))
                {
                    _brokerServer = null;
                }
            }

            try
            {
                server?.Dispose();
            }
            catch (Exception cleanupException)
            {
                _log?.Invoke("清理接任失败的 Shell broker 端点时发生异常。", cleanupException);
            }

            _log?.Invoke("接任 Shell 集成 broker 失败。", exception);
            return false;
        }
    }

    private void RunHeartbeat()
    {
        while (!_stopping)
        {
            if (!_isBrokerOwner)
            {
                var registration = _selfRegistration;
                if (registration is not null)
                {
                    var status = ShellAddPipeClient.Send(
                        _identity.BrokerPipeName,
                        new ShellAddMessage(ShellAddMessageKind.RegisterInstance, null, registration, registration.ProcessId, registration.ActivationTicks),
                        _options.ConnectTimeoutMilliseconds,
                        _options.ResponseTimeoutMilliseconds,
                        out _);
                    if (status != ShellAddStatus.Accepted)
                    {
                        // broker 尚未就绪或正在切换：下一次心跳继续尝试，请求不会因此丢失。
                        Thread.Sleep(Math.Min(_options.HeartbeatMilliseconds, 500));
                        continue;
                    }
                }
            }

            Thread.Sleep(_options.HeartbeatMilliseconds);
        }
    }

    /// <summary>窗口被激活时调用：更新本实例的单调激活序号。</summary>
    public void ReportActivated()
    {
        var registration = _selfRegistration;
        if (registration is null)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp();
        var updated = registration with { ActivationTicks = ticks };
        _selfRegistration = updated;
        _registry.Touch(_processId, ticks);
        if (_isBrokerOwner)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(state =>
        {
            ShellAddPipeClient.Send(
                _identity.BrokerPipeName,
                new ShellAddMessage(ShellAddMessageKind.InstanceActivated, null, null, _processId, ticks),
                _options.ConnectTimeoutMilliseconds,
                _options.ResponseTimeoutMilliseconds,
                out _);
        });
    }

    /// <summary>本进程（COM DropTarget 所在进程）收到 Shell 请求后的投递入口。</summary>
    public ShellAddStatus Deliver(ShellAddRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        if (_isBrokerOwner)
        {
            return Route(request);
        }

        for (var attempt = 0; attempt < _options.DeliveryAttempts; attempt++)
        {
            var status = ShellAddPipeClient.Send(
                _identity.BrokerPipeName,
                new ShellAddMessage(ShellAddMessageKind.AddPaths, request, null, _processId, 0),
                _options.ConnectTimeoutMilliseconds,
                _options.ResponseTimeoutMilliseconds,
                out var diagnostic);
            if (status is ShellAddStatus.Accepted or ShellAddStatus.Duplicate)
            {
                return status;
            }

            if (status == ShellAddStatus.NoTarget && attempt + 1 >= _options.DeliveryAttempts)
            {
                return ShellAddStatus.NoTarget;
            }

            _log?.Invoke($"投递到 broker 失败（第 {attempt + 1} 次）：{status}。{diagnostic}", null);
            Thread.Sleep(150);
        }

        return DeliverLocally(request);
    }

    private ShellAddStatus Route(ShellAddRequest request)
    {
        if (!_brokerLedger.TryBegin(request.RequestId))
        {
            return ShellAddStatus.Duplicate;
        }

        var completed = false;
        try
        {
            var deadline = Environment.TickCount + _options.RecoveryGraceMilliseconds;
            while (true)
            {
                var targets = _registry.RankedTargets();
                foreach (var target in targets)
                {
                    ShellAddStatus status;
                    if (target.IsSelf)
                    {
                        status = DeliverLocally(request);
                    }
                    else
                    {
                        status = ShellAddPipeClient.Send(
                            target.EndpointName,
                            new ShellAddMessage(ShellAddMessageKind.AddPaths, request, null, _processId, 0),
                            _options.ConnectTimeoutMilliseconds,
                            _options.ResponseTimeoutMilliseconds,
                            out _);
                    }

                    if (status is ShellAddStatus.Accepted or ShellAddStatus.Duplicate)
                    {
                        completed = true;
                        return status;
                    }

                    // 目标在确认之前退出或不再响应：标记失效并顺延到下一个存活窗口。
                    _registry.MarkDead(target.ProcessId);
                }

                // 只有在“曾经有实例登记过”时才等待重新登记；冷启动时立即返回，由调用方创建窗口。
                if (!_registry.HasEverRegistered || Environment.TickCount - deadline >= 0)
                {
                    return ShellAddStatus.NoTarget;
                }

                Thread.Sleep(100);
            }
        }
        finally
        {
            if (!completed)
            {
                _brokerLedger.Forget(request.RequestId);
            }
        }
    }

    private ShellAddStatus DeliverLocally(ShellAddRequest request)
    {
        var handler = LocalImportHandler;
        if (handler is null)
        {
            return ShellAddStatus.NoTarget;
        }

        if (!_localLedger.TryBegin(request.RequestId))
        {
            return ShellAddStatus.Duplicate;
        }

        try
        {
            var status = handler(request);
            if (status is not (ShellAddStatus.Accepted or ShellAddStatus.Duplicate))
            {
                _localLedger.Forget(request.RequestId);
            }

            return status;
        }
        catch (Exception exception)
        {
            _localLedger.Forget(request.RequestId);
            _log?.Invoke("本地接收 Shell 请求失败。", exception);
            return ShellAddStatus.Error;
        }
    }

    /// <summary>embedding 进程在创建本地窗口后重新登记自己，使后续请求可以本地接管。</summary>
    public void PromoteToInstance(long windowHandle = 0)
    {
        StartInstance(windowHandle);
    }

    private ShellAddStatus HandleBrokerMessage(ShellAddMessage message) => message.Kind switch
    {
        ShellAddMessageKind.RegisterInstance when message.Registration is not null => RegisterRemote(message.Registration),
        ShellAddMessageKind.InstanceActivated => TouchRemote(message.ProcessId, message.ActivationTicks),
        ShellAddMessageKind.UnregisterInstance => RemoveRemote(message.ProcessId),
        ShellAddMessageKind.AddPaths when message.Request is not null => Route(message.Request),
        ShellAddMessageKind.Ping => ShellAddStatus.Accepted,
        _ => ShellAddStatus.Rejected
    };

    private ShellAddStatus RegisterRemote(ShellAddInstanceRegistration registration)
    {
        _registry.Register(registration, isSelf: registration.ProcessId == _processId);
        return ShellAddStatus.Accepted;
    }

    private ShellAddStatus TouchRemote(int processId, long ticks)
    {
        _registry.Touch(processId, ticks);
        return ShellAddStatus.Accepted;
    }

    private ShellAddStatus RemoveRemote(int processId)
    {
        _registry.Remove(processId);
        return ShellAddStatus.Accepted;
    }

    private ShellAddStatus HandleInstanceMessage(ShellAddMessage message) => message.Kind switch
    {
        ShellAddMessageKind.AddPaths when message.Request is not null => DeliverLocally(message.Request),
        ShellAddMessageKind.Ping => ShellAddStatus.Accepted,
        _ => ShellAddStatus.Rejected
    };

    public void Dispose()
    {
        _stopping = true;
        var registration = _selfRegistration;
        if (registration is not null && !_isBrokerOwner)
        {
            ShellAddPipeClient.Send(
                _identity.BrokerPipeName,
                new ShellAddMessage(ShellAddMessageKind.UnregisterInstance, null, null, registration.ProcessId, 0),
                200,
                400,
                out _);
        }

        ShellAddPipeServer? instanceServer;
        ShellAddPipeServer? brokerServer;
        lock (_sync)
        {
            instanceServer = _instanceServer;
            brokerServer = _brokerServer;
            _instanceServer = null;
            _brokerServer = null;
        }

        instanceServer?.Dispose();
        brokerServer?.Dispose();

        // 选举线程负责释放 Mutex（所有权线程绑定），这里只等它退出。
        _electionThread?.Join(TimeSpan.FromSeconds(2));
        _heartbeatThread?.Join(TimeSpan.FromSeconds(1));
        _electionThread = null;
        _heartbeatThread = null;

        if (_brokerMutex is not null)
        {
            _brokerMutex.Dispose();
            _brokerMutex = null;
        }

        _isBrokerOwner = false;
    }
}
