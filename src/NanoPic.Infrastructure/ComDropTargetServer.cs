using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NanoPic.Infrastructure;

/// <summary>从 Explorer 数据对象同步提取出来的不可变结果。</summary>
public sealed record ShellDropPayload(IReadOnlyList<string> Paths, int UnavailableItemCount)
{
    public static ShellDropPayload Empty { get; } = new(Array.Empty<string>(), 0);

    public bool HasPaths => Paths.Count > 0;
}

[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig]
    int DragEnter(ComTypes.IDataObject dataObject, int keyState, ShellPoint point, ref int effect);

    [PreserveSig]
    int DragOver(int keyState, ShellPoint point, ref int effect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop(ComTypes.IDataObject dataObject, int keyState, ShellPoint point, ref int effect);
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr instance);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
}

[ComImport]
[Guid("00000019-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExternalConnection
{
    [PreserveSig]
    uint AddConnection(uint extconn, uint reserved);

    [PreserveSig]
    uint ReleaseConnection(uint extconn, uint reserved, [MarshalAs(UnmanagedType.Bool)] bool lastReleaseCloses);
}

[StructLayout(LayoutKind.Sequential)]
public struct ShellPoint
{
    public int X;
    public int Y;
}

internal static class ShellComNative
{
    internal const int SOk = 0;
    internal const int EFail = unchecked((int)0x80004005);
    internal const int ENoInterface = unchecked((int)0x80004002);
    internal const int ClassENoAggregation = unchecked((int)0x80040110);

    internal const int DropEffectNone = 0;
    internal const int DropEffectCopy = 1;
    internal const uint ExternalConnectionStrong = 1;

    internal const short ClipboardFormatHDrop = 15;
    internal const int DvAspectContent = 1;

    internal const uint ClsCtxLocalServer = 4;
    internal const uint RegClsMultipleUse = 1;
    internal const uint RegClsSuspended = 4;
    private const uint CoinitMultiThreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

    /// <summary>
    /// 在非 UI 线程上注册 class object 前确保该线程已初始化 COM（MTA）。
    /// 托管 CCW 是 apartment-agile 的，因此把 class object 放在 MTA 上，
    /// Explorer 的激活就不再依赖 UI 线程是否空闲。
    /// </summary>
    internal static void EnsureComInitializedMultiThreaded() => CoInitializeEx(IntPtr.Zero, CoinitMultiThreaded);

    [DllImport("ole32.dll")]
    internal static extern int CoRegisterClassObject(
        ref Guid classId,
        IntPtr unknown,
        uint classContext,
        uint flags,
        out uint register);

    [DllImport("ole32.dll")]
    internal static extern int CoRevokeClassObject(uint register);

    [DllImport("ole32.dll")]
    internal static extern int CoResumeClassObjects();

    [DllImport("ole32.dll")]
    internal static extern uint CoAddRefServerProcess();

    [DllImport("ole32.dll")]
    internal static extern uint CoReleaseServerProcess();

    [DllImport("ole32.dll")]
    internal static extern int CoDisconnectObject([MarshalAs(UnmanagedType.IUnknown)] object unknown, uint reserved);

    [DllImport("ole32.dll")]
    internal static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint DragQueryFile(IntPtr drop, uint fileIndex, StringBuilder? buffer, uint bufferLength);

    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

/// <summary>
/// 把 COM 的进程级引用计数封装为一次性租约。对象引用与 class-factory lock
/// 共用同一释放路径，确保任何一次归零都能通知宿主进入安全退出流程。
/// </summary>
internal sealed class ServerProcessLifetime
{
    private readonly Func<uint> _addReference;
    private readonly Func<uint> _releaseReference;
    private readonly Action _referencesReleased;

    internal ServerProcessLifetime(
        Func<uint> addReference,
        Func<uint> releaseReference,
        Action referencesReleased)
    {
        _addReference = addReference ?? throw new ArgumentNullException(nameof(addReference));
        _releaseReference = releaseReference ?? throw new ArgumentNullException(nameof(releaseReference));
        _referencesReleased = referencesReleased ?? throw new ArgumentNullException(nameof(referencesReleased));
    }

    internal IDisposable Acquire()
    {
        _addReference();
        return new Lease(this);
    }

    internal void LockServer(bool lockServer)
    {
        if (lockServer)
        {
            _addReference();
            return;
        }

        ReleaseReference();
    }

    private void ReleaseReference()
    {
        if (_releaseReference() == 0)
        {
            _referencesReleased();
        }
    }

    private sealed class Lease : IDisposable
    {
        private ServerProcessLifetime? _owner;

        internal Lease(ServerProcessLifetime owner) => _owner = owner;

        ~Lease() => Release();

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release() => Interlocked.Exchange(ref _owner, null)?.ReleaseReference();
    }
}

/// <summary>
/// CF_HDROP 提取：必须在 <c>IDropTarget.Drop</c> 返回之前完成，绝不保存 COM 接口指针异步使用。
/// </summary>
public static class ShellDropDataExtractor
{
    /// <summary>路径缓冲上限，覆盖 Windows 可表示的长路径（含 <c>\\?\</c> 场景）。</summary>
    private const int MaxPathCharacters = 32_768;

    public static ShellDropPayload Extract(ComTypes.IDataObject? dataObject) => Extract(dataObject, null);

    public static ShellDropPayload Extract(ComTypes.IDataObject? dataObject, Action<string, Exception?>? log)
    {
        if (dataObject is null)
        {
            return ShellDropPayload.Empty;
        }

        var format = new ComTypes.FORMATETC
        {
            cfFormat = ShellComNative.ClipboardFormatHDrop,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL
        };

        var queryResult = dataObject.QueryGetData(ref format);
        if (queryResult != ShellComNative.SOk)
        {
            log?.Invoke($"数据对象不提供 CF_HDROP（QueryGetData 0x{queryResult:X8}）。", null);
            return ShellDropPayload.Empty;
        }

        ComTypes.STGMEDIUM medium = default;
        try
        {
            dataObject.GetData(ref format, out medium);
            if (medium.tymed != ComTypes.TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
            {
                log?.Invoke($"CF_HDROP 介质不可用（tymed={medium.tymed}）。", null);
                return ShellDropPayload.Empty;
            }

            var payload = ReadDropHandle(medium.unionmember);
            log?.Invoke($"已从 CF_HDROP 提取 {payload.Paths.Count} 个路径，跳过 {payload.UnavailableItemCount} 项。", null);
            return payload;
        }
        catch (Exception exception) when (exception is COMException or ExternalException or ArgumentException)
        {
            log?.Invoke("读取 CF_HDROP 失败。", exception);
            return ShellDropPayload.Empty;
        }
        finally
        {
            if (medium.tymed != ComTypes.TYMED.TYMED_NULL)
            {
                ShellComNative.ReleaseStgMedium(ref medium);
            }
        }
    }

    private static ShellDropPayload ReadDropHandle(IntPtr dropHandle)
    {
        var count = ShellComNative.DragQueryFile(dropHandle, 0xFFFFFFFF, null, 0);
        if (count == 0)
        {
            return ShellDropPayload.Empty;
        }

        var paths = new List<string>((int)Math.Min(count, 100_000));
        var unavailable = 0;
        for (uint index = 0; index < count; index++)
        {
            var length = ShellComNative.DragQueryFile(dropHandle, index, null, 0);
            if (length == 0 || length > MaxPathCharacters)
            {
                unavailable++;
                continue;
            }

            var buffer = new StringBuilder((int)length + 1);
            if (ShellComNative.DragQueryFile(dropHandle, index, buffer, (uint)buffer.Capacity) == 0)
            {
                unavailable++;
                continue;
            }

            var path = buffer.ToString();
            if (string.IsNullOrWhiteSpace(path))
            {
                unavailable++;
                continue;
            }

            // 首版只接收真实文件；目录仍然只能从 NanoPic 内部的“添加文件夹”入口导入。
            if (Directory.Exists(PortableDirectoryProbe(path)))
            {
                unavailable++;
                continue;
            }

            paths.Add(path);
        }

        return new ShellDropPayload(paths, unavailable);
    }

    private static string PortableDirectoryProbe(string path) => NanoPic.Core.PortablePath.ForFileSystem(path);
}

/// <summary>
/// 进程外 COM DropTarget 对象。所有调用都由 COM 编组到注册 class object 的 STA 线程上，
/// 因此 <see cref="Drop"/> 内可以安全地同步提取数据并交给宿主。
/// </summary>
internal sealed class NanoPicDropTarget : IDropTarget, IExternalConnection
{
    private readonly Func<ShellDropPayload, bool> _handler;
    private readonly Action<string, Exception?>? _log;
    private readonly Action<object> _disconnect;
    private IDisposable? _serverReference;
    private int _strongConnectionCount;

    internal NanoPicDropTarget(
        Func<ShellDropPayload, bool> handler,
        Action<string, Exception?>? log,
        IDisposable serverReference,
        Action<object> disconnect)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log;
        _serverReference = serverReference ?? throw new ArgumentNullException(nameof(serverReference));
        _disconnect = disconnect ?? throw new ArgumentNullException(nameof(disconnect));
    }

    public uint AddConnection(uint extconn, uint reserved)
    {
        if ((extconn & ShellComNative.ExternalConnectionStrong) == 0)
        {
            return (uint)Math.Max(Volatile.Read(ref _strongConnectionCount), 0);
        }

        return (uint)Interlocked.Increment(ref _strongConnectionCount);
    }

    public uint ReleaseConnection(uint extconn, uint reserved, bool lastReleaseCloses)
    {
        if ((extconn & ShellComNative.ExternalConnectionStrong) == 0)
        {
            return (uint)Math.Max(Volatile.Read(ref _strongConnectionCount), 0);
        }

        var remaining = Interlocked.Decrement(ref _strongConnectionCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _strongConnectionCount, 0);
            _log?.Invoke("COM 外部连接释放次数超过获取次数，已忽略重复释放。", null);
            return 0;
        }

        if (remaining == 0)
        {
            DisconnectAndReleaseServerReference();
        }

        return (uint)remaining;
    }

    private void DisconnectAndReleaseServerReference()
    {
        var serverReference = Interlocked.Exchange(ref _serverReference, null);
        if (serverReference is null)
        {
            return;
        }

        try
        {
            _disconnect(this);
        }
        catch (Exception exception)
        {
            _log?.Invoke("断开 Explorer 的 COM DropTarget 连接失败。", exception);
        }
        finally
        {
            serverReference.Dispose();
        }
    }

    public int DragEnter(ComTypes.IDataObject dataObject, int keyState, ShellPoint point, ref int effect)
    {
        effect = ShellComNative.DropEffectCopy;
        return ShellComNative.SOk;
    }

    public int DragOver(int keyState, ShellPoint point, ref int effect)
    {
        effect = ShellComNative.DropEffectCopy;
        return ShellComNative.SOk;
    }

    public int DragLeave()
    {
        return ShellComNative.SOk;
    }

    public int Drop(ComTypes.IDataObject dataObject, int keyState, ShellPoint point, ref int effect)
    {
        try
        {
            var payload = ShellDropDataExtractor.Extract(dataObject, _log);
            if (!payload.HasPaths)
            {
                _log?.Invoke("Explorer 数据对象没有提供任何可用的文件路径。", null);
                effect = ShellComNative.DropEffectNone;
                return ShellComNative.EFail;
            }

            var accepted = _handler(payload);
            _log?.Invoke($"Shell 请求处理结果：{(accepted ? "已接收" : "未接收")}（{payload.Paths.Count} 个路径）。", null);
            effect = accepted ? ShellComNative.DropEffectCopy : ShellComNative.DropEffectNone;
            return accepted ? ShellComNative.SOk : ShellComNative.EFail;
        }
        catch (Exception exception)
        {
            _log?.Invoke("处理 Explorer 拖放数据时发生异常。", exception);
            effect = ShellComNative.DropEffectNone;
            return ShellComNative.EFail;
        }
    }
}

internal sealed class NanoPicDropTargetFactory : IClassFactory
{
    private readonly Func<IDropTarget> _create;
    private readonly ServerProcessLifetime _serverLifetime;
    private readonly Action<string, Exception?>? _log;

    internal NanoPicDropTargetFactory(
        Func<IDropTarget> create,
        ServerProcessLifetime serverLifetime,
        Action<string, Exception?>? log)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _serverLifetime = serverLifetime ?? throw new ArgumentNullException(nameof(serverLifetime));
        _log = log;
    }

    public int CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        if (outerUnknown != IntPtr.Zero)
        {
            return ShellComNative.ClassENoAggregation;
        }

        IntPtr unknown = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(_create());
            return Marshal.QueryInterface(unknown, ref interfaceId, out instance);
        }
        catch (Exception exception) when (exception is COMException or InvalidComObjectException or OutOfMemoryException)
        {
            _log?.Invoke("创建 DropTarget COM 对象失败。", exception);
            return ShellComNative.ENoInterface;
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    public int LockServer(bool lockServer)
    {
        _serverLifetime.LockServer(lockServer);
        return ShellComNative.SOk;
    }
}

/// <summary>
/// LocalServer32 宿主：注册 class factory（MULTIPLEUSE + SUSPENDED），初始化完成后 resume，
/// 并用 <c>CoAddRefServerProcess</c>/<c>CoReleaseServerProcess</c> 管理服务器生命周期，
/// 避免“检查对象数后退出”的激活竞态。
/// </summary>
public sealed class ComDropTargetServer : IDisposable
{
    private readonly Guid _classId;
    private readonly Func<ShellDropPayload, bool> _dropHandler;
    private readonly Func<object, int> _disconnectObject;
    private readonly Action<string, Exception?>? _log;
    private readonly ServerProcessLifetime _serverLifetime;
    private readonly object _sync = new();
    private NanoPicDropTargetFactory? _factory;
    private IntPtr _factoryUnknown = IntPtr.Zero;
    private uint _registrationCookie;
    private bool _resumed;

    public ComDropTargetServer(
        Guid classId,
        Func<ShellDropPayload, bool> dropHandler,
        Action<string, Exception?>? log = null)
        : this(
            classId,
            dropHandler,
            ShellComNative.CoAddRefServerProcess,
            ShellComNative.CoReleaseServerProcess,
            target => ShellComNative.CoDisconnectObject(target, 0),
            log)
    {
    }

    internal ComDropTargetServer(
        Guid classId,
        Func<ShellDropPayload, bool> dropHandler,
        Func<uint> addServerReference,
        Func<uint> releaseServerReference,
        Func<object, int> disconnectObject,
        Action<string, Exception?>? log = null)
    {
        _classId = classId;
        _dropHandler = dropHandler ?? throw new ArgumentNullException(nameof(dropHandler));
        _disconnectObject = disconnectObject ?? throw new ArgumentNullException(nameof(disconnectObject));
        _log = log;
        _serverLifetime = new ServerProcessLifetime(
            addServerReference,
            releaseServerReference,
            NotifyServerReferencesReleased);
    }

    public bool IsRegistered => _registrationCookie != 0;

    /// <summary>服务器引用计数归零：宿主据此决定隐藏进程是否可以安全退出。</summary>
    public event EventHandler? ServerReferencesReleased;

    /// <summary>必须在 STA 线程（WPF Dispatcher 线程）上调用。</summary>
    public bool Register()
    {
        lock (_sync)
        {
            if (_registrationCookie != 0)
            {
                return true;
            }

            _factory = new NanoPicDropTargetFactory(CreateDropTarget, _serverLifetime, _log);
            _factoryUnknown = Marshal.GetIUnknownForObject(_factory);
            // 注册线程可能是后台线程（选举线程）：先确保该线程已初始化 COM。
            // 已经是 STA 的 UI 线程会返回 RPC_E_CHANGED_MODE，忽略即可。
            ShellComNative.EnsureComInitializedMultiThreaded();
            var classId = _classId;
            var hresult = ShellComNative.CoRegisterClassObject(
                ref classId,
                _factoryUnknown,
                ShellComNative.ClsCtxLocalServer,
                ShellComNative.RegClsMultipleUse | ShellComNative.RegClsSuspended,
                out _registrationCookie);
            if (hresult != ShellComNative.SOk)
            {
                _log?.Invoke($"注册 COM class object 失败（HRESULT 0x{hresult:X8}）。", null);
                Marshal.Release(_factoryUnknown);
                _factoryUnknown = IntPtr.Zero;
                _factory = null;
                _registrationCookie = 0;
                return false;
            }

            return true;
        }
    }

    public bool Resume()
    {
        lock (_sync)
        {
            if (_registrationCookie == 0 || _resumed)
            {
                return _resumed;
            }

            var hresult = ShellComNative.CoResumeClassObjects();
            if (hresult != ShellComNative.SOk)
            {
                _log?.Invoke($"CoResumeClassObjects 失败（HRESULT 0x{hresult:X8}）。", null);
                return false;
            }

            _resumed = true;
            return true;
        }
    }

    public void Revoke()
    {
        lock (_sync)
        {
            if (_registrationCookie != 0)
            {
                ShellComNative.CoRevokeClassObject(_registrationCookie);
                _registrationCookie = 0;
            }

            if (_factoryUnknown != IntPtr.Zero)
            {
                Marshal.Release(_factoryUnknown);
                _factoryUnknown = IntPtr.Zero;
            }

            _factory = null;
            _resumed = false;
        }
    }

    internal IDropTarget CreateDropTarget()
    {
        // 每个 DropTarget 对象持有一次性 server lease；COM 标准封送器通过
        // IExternalConnection 通知最后一个外部连接释放，绝不把引用寿命绑定到 Drop 调用次数。
        var serverReference = _serverLifetime.Acquire();
        try
        {
            return new NanoPicDropTarget(HandleDrop, _log, serverReference, DisconnectDropTarget);
        }
        catch
        {
            serverReference.Dispose();
            throw;
        }
    }

    internal bool HandleDrop(ShellDropPayload payload) => _dropHandler(payload);

    private void DisconnectDropTarget(object target)
    {
        try
        {
            var hresult = _disconnectObject(target);
            if (hresult < 0)
            {
                _log?.Invoke($"CoDisconnectObject 失败（HRESULT 0x{hresult:X8}）。", null);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidComObjectException)
        {
            _log?.Invoke("CoDisconnectObject 调用失败。", exception);
        }
    }

    private void NotifyServerReferencesReleased()
    {
        try
        {
            ServerReferencesReleased?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            // 该路径也可能来自 lease finalizer；事件订阅者异常绝不能终止进程。
            _log?.Invoke("通知 COM server 引用归零时发生异常。", exception);
        }
    }

    public void Dispose() => Revoke();
}
