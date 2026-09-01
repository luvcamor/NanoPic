using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace NanoPic.Infrastructure;

/// <summary>
/// 右键菜单集成的注册表状态机：检测、安装、修复、移除、中断恢复。
/// 所有写入都先取得当前用户范围的操作 Mutex；冲突状态下零覆盖、零误删。
/// 服务本身是同步的，UI 负责放到后台线程执行。
/// </summary>
public sealed class ShellContextMenuIntegrationService
{
    private const int MutexWaitMilliseconds = 5000;
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfFlush = 0x1000;

    private static readonly string MetadataKey = ShellIntegrationContract.PrivateMetadataKeyPath;

    private readonly IShellRegistryStore _store;
    private readonly string _currentExePath;
    private readonly string _productVersion;
    private readonly Func<string, bool> _fileExists;
    private readonly Action _notifyShell;
    private readonly string _mutexName;
    private readonly Action<string, Exception?>? _log;

    public ShellContextMenuIntegrationService(
        IShellRegistryStore store,
        string currentExePath,
        string productVersion,
        Func<string, bool>? fileExists = null,
        Action? notifyShell = null,
        string? mutexName = null,
        Action<string, Exception?>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currentExePath = NormalizePath(currentExePath ?? throw new ArgumentNullException(nameof(currentExePath)));
        _productVersion = productVersion ?? string.Empty;
        _fileExists = fileExists ?? (path => File.Exists(path));
        _notifyShell = notifyShell ?? DefaultNotifyShell;
        _mutexName = mutexName ?? ShellAddIdentity.Current().RegistryMutexName;
        _log = log;
    }

    public string CurrentExePath => _currentExePath;

    private static void DefaultNotifyShell() =>
        // 等待所有 Shell 组件收到通知，避免 Explorer 继续使用安装前缓存的 Verb 列表。
        ShellComNative.SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, IntPtr.Zero, IntPtr.Zero);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim('"'));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record Metadata(
        bool Exists,
        bool OwnedByNanoPic,
        int? SchemaVersion,
        ShellIntegrationOperationState OperationState,
        string? TransactionId,
        string? TargetExePath,
        string? ProductVersion);

    private Metadata ReadMetadata()
    {
        if (!_store.KeyExists(MetadataKey))
        {
            return new Metadata(false, false, null, ShellIntegrationOperationState.None, null, null, null);
        }

        var owner = _store.GetStringValue(MetadataKey, ShellIntegrationContract.OwnerValueName);
        var stateText = _store.GetStringValue(MetadataKey, "OperationState");
        var operationState = Enum.TryParse<ShellIntegrationOperationState>(stateText, ignoreCase: true, out var parsed)
            ? parsed
            : ShellIntegrationOperationState.None;

        return new Metadata(
            true,
            string.Equals(owner, ShellIntegrationContract.OwnerId, StringComparison.Ordinal),
            _store.GetInt32Value(MetadataKey, ShellIntegrationContract.SchemaValueName),
            operationState,
            _store.GetStringValue(MetadataKey, ShellIntegrationContract.TransactionValueName),
            _store.GetStringValue(MetadataKey, "TargetExePath"),
            _store.GetStringValue(MetadataKey, "ProductVersion"));
    }

    private bool IsForeignKey(string path)
    {
        if (!_store.KeyExists(path))
        {
            return false;
        }

        var owner = _store.GetStringValue(path, ShellIntegrationContract.OwnerValueName);
        return !string.Equals(owner, ShellIntegrationContract.OwnerId, StringComparison.Ordinal);
    }

    private bool IsOwnedKey(string path) =>
        _store.KeyExists(path) &&
        string.Equals(
            _store.GetStringValue(path, ShellIntegrationContract.OwnerValueName),
            ShellIntegrationContract.OwnerId,
            StringComparison.Ordinal);

    private bool IsOwnedKeyForTransaction(string path, string? transactionId) =>
        IsOwnedKey(path) &&
        _store.GetInt32Value(path, ShellIntegrationContract.SchemaValueName) == ShellIntegrationContract.SchemaVersion &&
        !string.IsNullOrWhiteSpace(transactionId) &&
        string.Equals(
            _store.GetStringValue(path, ShellIntegrationContract.TransactionValueName),
            transactionId,
            StringComparison.Ordinal);

    private static bool ContainsOnlyNames(IReadOnlyList<string> actual, params string[] allowed) =>
        actual.All(name => allowed.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));

    private bool IsSafeOwnedLeaf(string path, string transactionId, params string[] managedValueNames)
    {
        if (!_store.KeyExists(path))
        {
            return true;
        }

        var allowedValues = managedValueNames.Concat(new[]
        {
            ShellIntegrationContract.OwnerValueName,
            ShellIntegrationContract.SchemaValueName,
            ShellIntegrationContract.TransactionValueName
        }).ToArray();
        return IsOwnedKeyForTransaction(path, transactionId) &&
            _store.GetSubKeyNames(path).Count == 0 &&
            ContainsOnlyNames(_store.GetValueNames(path), allowedValues);
    }

    private bool IsSafeVerbTree(string extension)
    {
        var verbPath = ShellIntegrationContract.VerbKeyPath(extension);
        var transactionId = _store.GetStringValue(verbPath, ShellIntegrationContract.TransactionValueName);
        if (!IsOwnedKeyForTransaction(verbPath, transactionId) ||
            !ContainsOnlyNames(
                _store.GetSubKeyNames(verbPath),
                "DropTarget",
                "command") ||
            !ContainsOnlyNames(
                _store.GetValueNames(verbPath),
                string.Empty,
                "Icon",
                "MultiSelectModel",
                ShellIntegrationContract.OwnerValueName,
                ShellIntegrationContract.SchemaValueName,
                ShellIntegrationContract.TransactionValueName))
        {
            return false;
        }

        return IsSafeOwnedLeaf(ShellIntegrationContract.DropTargetKeyPath(extension), transactionId!, "Clsid") &&
            IsSafeOwnedLeaf(ShellIntegrationContract.CommandKeyPath(extension), transactionId!, string.Empty);
    }

    private bool IsSafeClsidTree()
    {
        var clsidPath = ShellIntegrationContract.ClsidKeyPath;
        var transactionId = _store.GetStringValue(clsidPath, ShellIntegrationContract.TransactionValueName);
        return IsOwnedKeyForTransaction(clsidPath, transactionId) &&
            ContainsOnlyNames(_store.GetSubKeyNames(clsidPath), "LocalServer32") &&
            ContainsOnlyNames(
                _store.GetValueNames(clsidPath),
                string.Empty,
                ShellIntegrationContract.OwnerValueName,
                ShellIntegrationContract.SchemaValueName,
                ShellIntegrationContract.TransactionValueName) &&
            IsSafeOwnedLeaf(ShellIntegrationContract.LocalServerKeyPath, transactionId!, string.Empty, "ServerExecutable");
    }

    private bool IsSafeMetadataTree()
    {
        var transactionId = _store.GetStringValue(MetadataKey, ShellIntegrationContract.TransactionValueName);
        return IsOwnedKeyForTransaction(MetadataKey, transactionId) &&
            _store.GetSubKeyNames(MetadataKey).Count == 0 &&
            ContainsOnlyNames(
                _store.GetValueNames(MetadataKey),
                ShellIntegrationContract.OwnerValueName,
                ShellIntegrationContract.SchemaValueName,
                ShellIntegrationContract.TransactionValueName,
                "OperationState",
                "TargetExePath",
                "ProductVersion",
                "Extensions",
                "Clsid");
    }

    private void WriteOwnedMarkers(string path, string transactionId)
    {
        _store.SetStringValue(path, ShellIntegrationContract.OwnerValueName, ShellIntegrationContract.OwnerId);
        _store.SetInt32Value(path, ShellIntegrationContract.SchemaValueName, ShellIntegrationContract.SchemaVersion);
        _store.SetStringValue(path, ShellIntegrationContract.TransactionValueName, transactionId);
    }

    /// <summary>检测当前注册状态；不做任何写入。</summary>
    public ShellIntegrationState Detect()
    {
        var metadata = ReadMetadata();
        var diagnostics = new List<ShellIntegrationDiagnostic>();
        var expectedExe = metadata.TargetExePath ?? ReadServerExecutable();

        var hasConflict = false;
        if (IsForeignKey(ShellIntegrationContract.ClsidKeyPath))
        {
            hasConflict = true;
            diagnostics.Add(new ShellIntegrationDiagnostic(
                ShellIntegrationContract.ClsidKeyPath,
                "CLSID 已存在但不带 NanoPic 所有权标记。",
                IsConflict: true));
        }

        var missing = new List<string>();
        var complete = 0;
        foreach (var extension in ShellIntegrationContract.SupportedExtensions)
        {
            var verbPath = ShellIntegrationContract.VerbKeyPath(extension);
            if (IsForeignKey(verbPath))
            {
                hasConflict = true;
                diagnostics.Add(new ShellIntegrationDiagnostic(
                    verbPath,
                    "同名 Verb 已存在但不带 NanoPic 所有权标记。",
                    IsConflict: true));
                continue;
            }

            if (!_store.KeyExists(verbPath))
            {
                missing.Add(extension);
                continue;
            }

            var problems = InspectVerb(extension, expectedExe, metadata.TransactionId);
            if (problems.Count == 0)
            {
                complete++;
            }
            else
            {
                missing.Add(extension);
                diagnostics.AddRange(problems);
            }
        }

        var clsidProblems = InspectClsid(expectedExe, metadata.TransactionId);
        diagnostics.AddRange(clsidProblems);

        var targetExists = expectedExe is not null && _fileExists(expectedExe);
        var status = ClassifyStatus(metadata, hasConflict, complete, clsidProblems.Count == 0, expectedExe);
        return new ShellIntegrationState(
            status,
            metadata.OperationState,
            expectedExe,
            targetExists,
            metadata.ProductVersion,
            metadata.TransactionId,
            complete,
            missing,
            diagnostics);
    }

    private ShellIntegrationStatus ClassifyStatus(
        Metadata metadata,
        bool hasConflict,
        int completeVerbs,
        bool clsidComplete,
        string? expectedExe)
    {
        if (hasConflict)
        {
            return ShellIntegrationStatus.Conflict;
        }

        if (metadata.Exists && !metadata.OwnedByNanoPic)
        {
            return ShellIntegrationStatus.Conflict;
        }

        if (metadata.OperationState is ShellIntegrationOperationState.Installing
            or ShellIntegrationOperationState.Repairing
            or ShellIntegrationOperationState.Removing)
        {
            return ShellIntegrationStatus.RecoveryPending;
        }

        var anyKey = _store.KeyExists(ShellIntegrationContract.ClsidKeyPath) ||
            ShellIntegrationContract.SupportedExtensions.Any(extension => _store.KeyExists(ShellIntegrationContract.VerbKeyPath(extension)));
        if (!metadata.Exists && !anyKey)
        {
            return ShellIntegrationStatus.NotInstalled;
        }

        var fullyRegistered =
            clsidComplete &&
            completeVerbs == ShellIntegrationContract.SupportedExtensions.Count &&
            metadata.Exists &&
            metadata.OwnedByNanoPic &&
            metadata.OperationState == ShellIntegrationOperationState.Installed &&
            metadata.SchemaVersion == ShellIntegrationContract.SchemaVersion &&
            !string.IsNullOrEmpty(metadata.TransactionId);
        if (!fullyRegistered)
        {
            return ShellIntegrationStatus.Partial;
        }

        return SamePath(expectedExe, _currentExePath)
            ? ShellIntegrationStatus.InstalledCurrent
            : ShellIntegrationStatus.InstalledStale;
    }

    private string? ReadServerExecutable()
    {
        var value = _store.GetStringValue(ShellIntegrationContract.LocalServerKeyPath, "ServerExecutable");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return NormalizePath(value!);
        }

        var command = _store.GetStringValue(ShellIntegrationContract.LocalServerKeyPath, null);
        return string.IsNullOrWhiteSpace(command) ? null : NormalizePath(command!.Trim('"'));
    }

    private List<ShellIntegrationDiagnostic> InspectVerb(string extension, string? expectedExe, string? transactionId)
    {
        var problems = new List<ShellIntegrationDiagnostic>();
        var verbPath = ShellIntegrationContract.VerbKeyPath(extension);
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                problems.Add(new ShellIntegrationDiagnostic(verbPath, message, IsConflict: false));
            }
        }

        Check(
            string.Equals(_store.GetStringValue(verbPath, null), ShellIntegrationContract.VerbDisplayName, StringComparison.Ordinal),
            "菜单显示名称与预期不一致。");
        Check(
            _store.GetInt32Value(verbPath, ShellIntegrationContract.SchemaValueName) == ShellIntegrationContract.SchemaVersion,
            "schema 版本与预期不一致。");
        Check(
            transactionId is not null &&
            string.Equals(_store.GetStringValue(verbPath, ShellIntegrationContract.TransactionValueName), transactionId, StringComparison.Ordinal),
            "事务 ID 与私有元数据不一致。");
        Check(
            string.Equals(_store.GetStringValue(verbPath, "MultiSelectModel"), ShellIntegrationContract.MultiSelectModel, StringComparison.Ordinal),
            "多选模型与预期不一致。");
        Check(
            expectedExe is not null &&
            string.Equals(_store.GetStringValue(verbPath, "Icon"), BuildIconValue(expectedExe), StringComparison.OrdinalIgnoreCase),
            "菜单图标路径与目标程序不一致。");

        var commandPath = ShellIntegrationContract.CommandKeyPath(extension);
        Check(_store.KeyExists(commandPath), "缺少 command 子键，Explorer 可能隐藏该菜单项。");
        Check(IsOwnedKeyForTransaction(commandPath, transactionId), "command 子键的所有权或事务标记不一致。");
        Check(
            expectedExe is not null &&
            string.Equals(_store.GetStringValue(commandPath, null), BuildCommandValue(expectedExe), StringComparison.OrdinalIgnoreCase),
            "command 命令行与目标程序不一致。");

        var dropTargetPath = ShellIntegrationContract.DropTargetKeyPath(extension);
        Check(_store.KeyExists(dropTargetPath), "缺少 DropTarget 子键。");
        Check(IsOwnedKeyForTransaction(dropTargetPath, transactionId), "DropTarget 子键的所有权或事务标记不一致。");
        Check(
            string.Equals(_store.GetStringValue(dropTargetPath, "Clsid"), ShellIntegrationContract.DropTargetClsidKey, StringComparison.OrdinalIgnoreCase),
            "DropTarget CLSID 与预期不一致。");
        return problems;
    }

    private List<ShellIntegrationDiagnostic> InspectClsid(string? expectedExe, string? transactionId)
    {
        var problems = new List<ShellIntegrationDiagnostic>();
        void Check(string path, bool condition, string message)
        {
            if (!condition)
            {
                problems.Add(new ShellIntegrationDiagnostic(path, message, IsConflict: false));
            }
        }

        var clsidPath = ShellIntegrationContract.ClsidKeyPath;
        Check(clsidPath, IsOwnedKey(clsidPath), "缺少 CLSID 键或所有权标记。");
        Check(
            clsidPath,
            string.Equals(_store.GetStringValue(clsidPath, null), ShellIntegrationContract.ClsidDisplayName, StringComparison.Ordinal),
            "CLSID 说明文本与预期不一致。");
        Check(
            clsidPath,
            _store.GetInt32Value(clsidPath, ShellIntegrationContract.SchemaValueName) == ShellIntegrationContract.SchemaVersion,
            "CLSID schema 版本与预期不一致。");
        Check(
            clsidPath,
            transactionId is not null &&
            string.Equals(_store.GetStringValue(clsidPath, ShellIntegrationContract.TransactionValueName), transactionId, StringComparison.Ordinal),
            "CLSID 事务 ID 与私有元数据不一致。");

        var serverPath = ShellIntegrationContract.LocalServerKeyPath;
        Check(serverPath, _store.KeyExists(serverPath), "缺少 LocalServer32 子键。");
        Check(serverPath, IsOwnedKeyForTransaction(serverPath, transactionId), "LocalServer32 子键的所有权或事务标记不一致。");
        Check(
            serverPath,
            expectedExe is not null && SamePath(_store.GetStringValue(serverPath, "ServerExecutable"), expectedExe),
            "ServerExecutable 与目标程序不一致。");
        Check(
            serverPath,
            expectedExe is not null &&
            string.Equals(_store.GetStringValue(serverPath, null), BuildCommandValue(expectedExe), StringComparison.OrdinalIgnoreCase),
            "LocalServer32 命令行与目标程序不一致。");
        return problems;
    }

    private static string BuildIconValue(string exePath) => string.Format(CultureInfo.InvariantCulture, "{0},0", exePath);

    private static string BuildCommandValue(string exePath) => string.Format(CultureInfo.InvariantCulture, "\"{0}\"", exePath);

    /// <summary>安装：预检发现冲突则零写入退出；写入后逐项读回验证，全部一致才提交。</summary>
    public ShellIntegrationOperationResult Install() => RunExclusive(() =>
    {
        var current = Detect();
        if (current.Status == ShellIntegrationStatus.Conflict)
        {
            return new ShellIntegrationOperationResult(false, current, "右键菜单配置存在冲突，未修改任何注册项。");
        }

        return WriteAndCommit(ShellIntegrationOperationState.Installing, current.TransactionId);
    });

    /// <summary>修复：只修改所有权可证明属于 NanoPic 的项，冲突项不接管、不覆盖。</summary>
    public ShellIntegrationOperationResult Repair() => RunExclusive(() =>
    {
        var current = Detect();
        if (current.Status == ShellIntegrationStatus.Conflict)
        {
            return new ShellIntegrationOperationResult(false, current, "右键菜单配置存在冲突，未修改任何注册项。");
        }

        return WriteAndCommit(ShellIntegrationOperationState.Repairing, current.TransactionId);
    });

    private ShellIntegrationOperationResult WriteAndCommit(ShellIntegrationOperationState phase, string? previousTransactionId)
    {
        // 每次写入都用新的事务 ID：中断恢复只认“元数据与目标键同属一个事务”的情况。
        var transactionId = Guid.NewGuid().ToString("B").ToUpperInvariant();
        _log?.Invoke($"开始 {phase} 右键菜单集成（上一事务 {previousTransactionId ?? "无"}）。", null);

        try
        {
            WriteMetadata(phase, transactionId);
            WriteClsid(transactionId);
            foreach (var extension in ShellIntegrationContract.SupportedExtensions)
            {
                WriteVerb(extension, transactionId);
            }

            var verification = InspectClsid(_currentExePath, transactionId);
            foreach (var extension in ShellIntegrationContract.SupportedExtensions)
            {
                verification.AddRange(InspectVerb(extension, _currentExePath, transactionId));
            }

            if (verification.Count > 0)
            {
                _log?.Invoke($"右键菜单注册验证失败：{verification.Count} 项不一致。", null);
                return new ShellIntegrationOperationResult(false, Detect(), "注册项写入后校验未通过，右键菜单可能不完整。");
            }

            WriteMetadata(ShellIntegrationOperationState.Installed, transactionId);
            _notifyShell();
            return new ShellIntegrationOperationResult(true, Detect());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            _log?.Invoke("写入右键菜单注册项失败。", exception);
            return new ShellIntegrationOperationResult(false, Detect(), "无法写入右键菜单注册项：" + exception.Message);
        }
    }

    private void WriteMetadata(ShellIntegrationOperationState state, string transactionId)
    {
        _store.CreateKey(MetadataKey);
        _store.SetStringValue(MetadataKey, ShellIntegrationContract.OwnerValueName, ShellIntegrationContract.OwnerId);
        _store.SetInt32Value(MetadataKey, ShellIntegrationContract.SchemaValueName, ShellIntegrationContract.SchemaVersion);
        _store.SetStringValue(MetadataKey, "OperationState", state.ToString());
        _store.SetStringValue(MetadataKey, ShellIntegrationContract.TransactionValueName, transactionId);
        _store.SetStringValue(MetadataKey, "TargetExePath", _currentExePath);
        _store.SetStringValue(MetadataKey, "ProductVersion", _productVersion);
        _store.SetStringValue(MetadataKey, "Extensions", string.Join(";", ShellIntegrationContract.SupportedExtensions));
        _store.SetStringValue(MetadataKey, "Clsid", ShellIntegrationContract.DropTargetClsidKey);
    }

    private void WriteClsid(string transactionId)
    {
        var clsidPath = ShellIntegrationContract.ClsidKeyPath;
        _store.CreateKey(clsidPath);
        // 先写所有权与事务，再写受管值：中断后仍能证明这一项由本次事务创建。
        _store.SetStringValue(clsidPath, ShellIntegrationContract.OwnerValueName, ShellIntegrationContract.OwnerId);
        _store.SetInt32Value(clsidPath, ShellIntegrationContract.SchemaValueName, ShellIntegrationContract.SchemaVersion);
        _store.SetStringValue(clsidPath, ShellIntegrationContract.TransactionValueName, transactionId);
        _store.SetStringValue(clsidPath, null, ShellIntegrationContract.ClsidDisplayName);

        var serverPath = ShellIntegrationContract.LocalServerKeyPath;
        _store.CreateKey(serverPath);
        WriteOwnedMarkers(serverPath, transactionId);
        _store.SetStringValue(serverPath, null, BuildCommandValue(_currentExePath));
        _store.SetStringValue(serverPath, "ServerExecutable", _currentExePath);
    }

    private void WriteVerb(string extension, string transactionId)
    {
        var verbPath = ShellIntegrationContract.VerbKeyPath(extension);
        _store.CreateKey(verbPath);
        _store.SetStringValue(verbPath, ShellIntegrationContract.OwnerValueName, ShellIntegrationContract.OwnerId);
        _store.SetInt32Value(verbPath, ShellIntegrationContract.SchemaValueName, ShellIntegrationContract.SchemaVersion);
        _store.SetStringValue(verbPath, ShellIntegrationContract.TransactionValueName, transactionId);
        _store.SetStringValue(verbPath, null, ShellIntegrationContract.VerbDisplayName);
        _store.SetStringValue(verbPath, "Icon", BuildIconValue(_currentExePath));
        _store.SetStringValue(verbPath, "MultiSelectModel", ShellIntegrationContract.MultiSelectModel);

        var dropTargetPath = ShellIntegrationContract.DropTargetKeyPath(extension);
        _store.CreateKey(dropTargetPath);
        WriteOwnedMarkers(dropTargetPath, transactionId);
        _store.SetStringValue(dropTargetPath, "Clsid", ShellIntegrationContract.DropTargetClsidKey);

        // Explorer 24H2 的经典菜单会过滤没有 command 子键的静态 Verb；实际激活仍优先走 DropTarget。
        var commandPath = ShellIntegrationContract.CommandKeyPath(extension);
        _store.CreateKey(commandPath);
        WriteOwnedMarkers(commandPath, transactionId);
        _store.SetStringValue(commandPath, null, BuildCommandValue(_currentExePath));
    }

    /// <summary>移除：只删除所有权、CLSID 与受管值均匹配的 NanoPic 子树，不动父容器。</summary>
    public ShellIntegrationOperationResult Remove() => RunExclusive(() =>
    {
        var before = Detect();
        var conflicts = new List<ShellIntegrationDiagnostic>();
        var changed = false;

        try
        {
            // 先完整预检，任何未知值/子键或不匹配的子键所有权都会使本次卸载零删除退出。
            foreach (var extension in ShellIntegrationContract.SupportedExtensions)
            {
                var verbPath = ShellIntegrationContract.VerbKeyPath(extension);
                if (!_store.KeyExists(verbPath))
                {
                    continue;
                }

                if (!IsSafeVerbTree(extension))
                {
                    conflicts.Add(new ShellIntegrationDiagnostic(verbPath, "该 Verb 含未知内容或所有权标记不完整，保留未删除。", IsConflict: true));
                }
            }

            var clsidPath = ShellIntegrationContract.ClsidKeyPath;
            if (_store.KeyExists(clsidPath) && !IsSafeClsidTree())
            {
                conflicts.Add(new ShellIntegrationDiagnostic(clsidPath, "CLSID 含未知内容或所有权标记不完整，保留未删除。", IsConflict: true));
            }

            if (_store.KeyExists(MetadataKey) && !IsSafeMetadataTree())
            {
                conflicts.Add(new ShellIntegrationDiagnostic(MetadataKey, "私有元数据含未知内容或所有权标记不完整，保留未删除。", IsConflict: true));
            }

            if (conflicts.Count > 0)
            {
                var preserved = Detect();
                _log?.Invoke($"移除右键菜单集成已取消：检测到 {conflicts.Count} 个所有权冲突，未删除任何注册项。", null);
                return new ShellIntegrationOperationResult(false, preserved, "注册项包含无法证明属于 NanoPic 的内容，已全部保留未删除。");
            }

            if (before.TransactionId is not null || before.Status != ShellIntegrationStatus.NotInstalled)
            {
                WriteRemovingMarker(before.TransactionId);
            }

            foreach (var extension in ShellIntegrationContract.SupportedExtensions)
            {
                var verbPath = ShellIntegrationContract.VerbKeyPath(extension);
                if (_store.KeyExists(verbPath))
                {
                    _store.DeleteKeyTree(verbPath);
                    changed = true;
                }
            }

            if (_store.KeyExists(clsidPath))
            {
                // 只有所有 NanoPic Verb 都通过预检并已移除后才删除 CLSID。
                _store.DeleteKeyTree(clsidPath);
                changed = true;
            }

            if (_store.KeyExists(MetadataKey))
            {
                _store.DeleteKeyTree(MetadataKey);
                changed = true;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            _log?.Invoke("移除右键菜单注册项失败。", exception);
            return new ShellIntegrationOperationResult(false, Detect(), "无法移除右键菜单注册项：" + exception.Message);
        }

        if (changed)
        {
            _notifyShell();
        }

        var after = Detect();
        _log?.Invoke(
            $"移除右键菜单集成完成：状态 {after.Status}，冲突保留 {conflicts.Count} 项，发生变更 {changed}。",
            null);
        return new ShellIntegrationOperationResult(true, after);
    });

    private void WriteRemovingMarker(string? transactionId)
    {
        if (!_store.KeyExists(MetadataKey))
        {
            return;
        }

        _store.SetStringValue(MetadataKey, "OperationState", ShellIntegrationOperationState.Removing.ToString());
        if (transactionId is not null)
        {
            _store.SetStringValue(MetadataKey, ShellIntegrationContract.TransactionValueName, transactionId);
        }
    }

    /// <summary>
    /// 普通交互启动时的对账：完成被中断的操作，并在安全条件下自动更新便携路径。
    /// 首次安装永远需要用户主动勾选，这里绝不会从 NotInstalled 变成已安装。
    /// </summary>
    public ShellIntegrationStartupReconcileResult ReconcileOnStartup()
    {
        var state = Detect();
        _log?.Invoke($"启动对账：检测到状态 {state.Status}（操作状态 {state.OperationState}，扩展 {state.RegisteredExtensionCount}/{state.ExpectedExtensionCount}）。", null);
        switch (state.Status)
        {
            case ShellIntegrationStatus.NotInstalled:
            case ShellIntegrationStatus.InstalledCurrent when SamePath(state.TargetExePath, _currentExePath) &&
                string.Equals(state.ProductVersion, _productVersion, StringComparison.Ordinal):
                return new ShellIntegrationStartupReconcileResult(state, ShellIntegrationReconcileAction.None);

            case ShellIntegrationStatus.InstalledCurrent:
            {
                // 仅版本变化且路径相同：静默更新产品版本元数据。
                var updated = RunExclusive(() =>
                {
                    if (state.TransactionId is not null)
                    {
                        _store.SetStringValue(MetadataKey, "ProductVersion", _productVersion);
                    }

                    return new ShellIntegrationOperationResult(true, Detect());
                });
                return new ShellIntegrationStartupReconcileResult(updated.State, ShellIntegrationReconcileAction.ProductVersionUpdated);
            }

            case ShellIntegrationStatus.RecoveryPending:
            {
                var resumed = state.OperationState == ShellIntegrationOperationState.Removing ? Remove() : Repair();
                return new ShellIntegrationStartupReconcileResult(
                    resumed.State,
                    resumed.Succeeded
                        ? ShellIntegrationReconcileAction.InterruptedOperationCompleted
                        : ShellIntegrationReconcileAction.NeedsUserDecision);
            }

            case ShellIntegrationStatus.InstalledStale when !state.TargetExeExists:
            {
                // 旧目标已经不存在：本副本可以安全接管，自动把路径更新为当前 exe。
                var repaired = Repair();
                return new ShellIntegrationStartupReconcileResult(
                    repaired.State,
                    repaired.Succeeded
                        ? ShellIntegrationReconcileAction.PathAutoUpdated
                        : ShellIntegrationReconcileAction.NeedsUserDecision);
            }

            default:
                return new ShellIntegrationStartupReconcileResult(state, ShellIntegrationReconcileAction.NeedsUserDecision);
        }
    }

    private ShellIntegrationOperationResult RunExclusive(Func<ShellIntegrationOperationResult> operation)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(MutexWaitMilliseconds);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                return new ShellIntegrationOperationResult(false, Detect(), "另一处正在修改右键菜单配置，请稍后重试。");
            }

            return operation();
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
        }
    }
}

public enum ShellIntegrationReconcileAction
{
    None = 0,
    ProductVersionUpdated,
    PathAutoUpdated,
    InterruptedOperationCompleted,
    NeedsUserDecision
}

public sealed record ShellIntegrationStartupReconcileResult(
    ShellIntegrationState State,
    ShellIntegrationReconcileAction Action);
