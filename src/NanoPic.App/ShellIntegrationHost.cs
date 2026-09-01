using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using NanoPic.Infrastructure;

namespace NanoPic.App;

/// <summary>Windows 拒绝前台切换时的退化提示：闪烁任务栏，不循环抢焦点。</summary>
internal static class NativeWindowAttention
{
    private const uint FlashWindowTray = 0x00000002;
    private const uint FlashWindowTimerNoForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint StructureSize;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    internal static void FlashTaskbar(IntPtr windowHandle)
    {
        var info = new FlashWindowInfo
        {
            StructureSize = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            WindowHandle = windowHandle,
            Flags = FlashWindowTray | FlashWindowTimerNoForeground,
            Count = 3,
            Timeout = 0
        };
        FlashWindowEx(ref info);
    }
}

/// <summary>
/// 把 COM DropTarget、会话 broker、注册表状态机粘合到 WPF 宿主上的唯一位置。
/// COM 与 IPC 细节不进入 <see cref="MainWindow"/>；窗口只提供“接收请求”和“激活自己”两个能力。
/// </summary>
internal sealed class ShellIntegrationHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string, Exception?> _log;
    private readonly ShellAddService _addService;
    private readonly ComDropTargetServer _comServer;
    private readonly object _sync = new();
    private Func<ShellAddRequest, bool>? _windowReceiver;
    private bool _integrationAvailable;
    private bool _isEmbedding;
    private bool _disposed;

    public ShellIntegrationHost(
        Dispatcher dispatcher,
        string executablePath,
        string productVersion,
        Action<string, Exception?> log)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var identity = ShellAddIdentity.Current();
        Registry = new ShellContextMenuIntegrationService(
            new WindowsShellRegistryStore(),
            executablePath,
            productVersion,
            log: log);
        _addService = new ShellAddService(identity, log);
        _addService.LocalImportHandler = DeliverToWindow;
        // 直接在选举线程上注册 class object：Explorer 的激活不应等待 UI 线程空闲。
        _addService.BrokerOwnershipAcquired = TryRegisterComServer;
        _comServer = new ComDropTargetServer(ShellIntegrationContract.DropTargetClsid, HandleDrop, log);
        _comServer.ServerReferencesReleased += (_, _) => _dispatcher.BeginInvoke(new Action(RaiseEmbeddingIdle));
    }

    public ShellContextMenuIntegrationService Registry { get; }

    /// <summary>由主窗口设置：把请求复制进导入队列并返回是否接收成功。</summary>
    public Func<ShellAddRequest, bool>? WindowReceiver
    {
        get => Volatile.Read(ref _windowReceiver);
        set => Volatile.Write(ref _windowReceiver, value);
    }

    /// <summary>由 App 设置：在没有任何窗口可接收时创建一个正常窗口，返回是否创建成功。</summary>
    public Func<bool>? EnsureWindow { get; set; }

    /// <summary>隐藏的 embedding 进程已无请求可处理，宿主据此决定退出。</summary>
    public event EventHandler? EmbeddingIdle;

    public bool IsBrokerOwner => _addService.IsBrokerOwner;

    /// <summary>只有普通交互启动才做注册表恢复；<c>-Embedding</c> 进程不主动改写注册表。</summary>
    public bool AllowStartupReconcile => !_isEmbedding;

    /// <summary>普通窗口实例：登记端点、参加选举，并在具备条件时提供 COM class object。</summary>
    public void StartNormalInstance()
    {
        _addService.StartInstance();
        SyncComRegistration();
    }

    /// <summary>COM embedding 进程：立即提供 class object（Explorer 正在等待），同时参加选举。</summary>
    public void StartEmbedding()
    {
        _isEmbedding = true;
        _integrationAvailable = true;
        _addService.StartEmbedding();
        TryRegisterComServer();
    }

    public void ReportActivated() => _addService.ReportActivated();

    /// <summary>安装或卸载完成后重新对齐 COM class object 的注册状态。</summary>
    public void SyncComRegistration()
    {
        if (_isEmbedding)
        {
            return;
        }

        try
        {
            _integrationAvailable = Registry.Detect().Status == ShellIntegrationStatus.InstalledCurrent;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log("读取右键菜单注册状态失败。", exception);
            _integrationAvailable = false;
        }

        if (_integrationAvailable)
        {
            TryRegisterComServer();
        }
        else
        {
            _comServer.Revoke();
        }
    }

    private void TryRegisterComServer()
    {
        lock (_sync)
        {
            if (_disposed || !_integrationAvailable)
            {
                return;
            }

            // 只有 broker owner 提供 class object，避免多实例同时注册导致 Explorer 目标不确定。
            if (!_isEmbedding && !_addService.IsBrokerOwner)
            {
                return;
            }

            if (!_comServer.Register())
            {
                return;
            }

            _comServer.Resume();
            _log("已注册 Explorer DropTarget class object。", null);
        }
    }

    private void RaiseEmbeddingIdle() => EmbeddingIdle?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 在 <c>IDropTarget.Drop</c> 内同步执行：此时路径已复制完毕，与 Shell 数据对象彻底脱钩。
    /// 托管 CCW 是 apartment-agile 的，Drop 可能落在任意 RPC 线程上，因此所有涉及 WPF 的操作
    /// 都必须显式回到 Dispatcher 线程。
    /// </summary>
    private bool HandleDrop(ShellDropPayload payload)
    {
        var request = new ShellAddRequest(
            Guid.NewGuid(),
            ShellAddOrigin.ExplorerDropTarget,
            payload.Paths,
            ActivateWindow: true,
            payload.UnavailableItemCount);

        var status = _addService.Deliver(request);
        if (status is ShellAddStatus.Accepted or ShellAddStatus.Duplicate)
        {
            return true;
        }

        _log($"Shell 请求未能投递到已有窗口（{status}），尝试本地接管。", null);
        var ensureWindow = EnsureWindow;
        if (ensureWindow is null)
        {
            return false;
        }

        var created = _dispatcher.CheckAccess()
            ? ensureWindow()
            : _dispatcher.Invoke(ensureWindow);
        if (!created || WindowReceiver is null)
        {
            return false;
        }

        // 本地接管：先登记自身端点，后续请求可以直接落到这个窗口。
        _addService.PromoteToInstance();
        return DeliverToWindow(request) is ShellAddStatus.Accepted;
    }

    private ShellAddStatus DeliverToWindow(ShellAddRequest request)
    {
        var receiver = WindowReceiver;
        if (receiver is null)
        {
            return ShellAddStatus.NoTarget;
        }

        return receiver(request) ? ShellAddStatus.Accepted : ShellAddStatus.NoTarget;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _comServer.Dispose();
        _addService.Dispose();
    }
}
