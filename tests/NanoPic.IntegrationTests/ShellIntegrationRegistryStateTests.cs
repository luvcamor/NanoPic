using System;
using System.Collections.Generic;
using System.Linq;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class ShellIntegrationRegistryStateTests
{
    private const string CurrentExe = @"C:\Portable\NanoPic 便携版 & 测试\NanoPic.exe";
    private const string OldExe = @"D:\旧目录\NanoPic.exe";

    private sealed class Harness
    {
        public InMemoryShellRegistryStore Store { get; } = new();
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase) { CurrentExe };
        public int ShellNotifications { get; private set; }

        public ShellContextMenuIntegrationService CreateService(string exePath = CurrentExe, string version = "3.2.5") =>
            new(
                Store,
                exePath,
                version,
                fileExists: path => ExistingFiles.Contains(path),
                notifyShell: () => ShellNotifications++,
                mutexName: @"Local\NanoPic.Tests." + Guid.NewGuid().ToString("N"));
    }

    private static void AssertFullyRegistered(InMemoryShellRegistryStore store, string exePath)
    {
        foreach (var extension in ShellIntegrationContract.SupportedExtensions)
        {
            var verb = ShellIntegrationContract.VerbKeyPath(extension);
            Assert.Equal(ShellIntegrationContract.VerbDisplayName, store.GetStringValue(verb, null));
            Assert.Equal(ShellIntegrationContract.OwnerId, store.GetStringValue(verb, ShellIntegrationContract.OwnerValueName));
            Assert.Equal(ShellIntegrationContract.MultiSelectModel, store.GetStringValue(verb, "MultiSelectModel"));
            Assert.Equal(exePath + ",0", store.GetStringValue(verb, "Icon"));
            Assert.Equal(
                ShellIntegrationContract.DropTargetClsidKey,
                store.GetStringValue(ShellIntegrationContract.DropTargetKeyPath(extension), "Clsid"));
            Assert.Equal(
                $"\"{exePath}\"",
                store.GetStringValue(ShellIntegrationContract.CommandKeyPath(extension), null));
        }

        Assert.Equal(exePath, store.GetStringValue(ShellIntegrationContract.LocalServerKeyPath, "ServerExecutable"));
        Assert.Equal($"\"{exePath}\"", store.GetStringValue(ShellIntegrationContract.LocalServerKeyPath, null));
    }

    [Fact]
    public void D1_InstallRegistersElevenVerbsAndNotifiesShellOnce()
    {
        var harness = new Harness();
        var service = harness.CreateService();

        Assert.Equal(ShellIntegrationStatus.NotInstalled, service.Detect().Status);

        var result = service.Install();

        Assert.True(result.Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, result.State.Status);
        Assert.Equal(11, result.State.RegisteredExtensionCount);
        Assert.Empty(result.State.MissingExtensions);
        Assert.Equal(1, harness.ShellNotifications);
        AssertFullyRegistered(harness.Store, CurrentExe);
    }

    [Fact]
    public void D2_InstallRepairAndRemoveAreIdempotent()
    {
        var harness = new Harness();
        var service = harness.CreateService();

        Assert.True(service.Install().Succeeded);
        Assert.True(service.Install().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, service.Detect().Status);

        Assert.True(service.Repair().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, service.Detect().Status);

        Assert.True(service.Remove().Succeeded);
        Assert.Equal(ShellIntegrationStatus.NotInstalled, service.Detect().Status);
        Assert.True(service.Remove().Succeeded);
        Assert.Equal(ShellIntegrationStatus.NotInstalled, service.Detect().Status);
    }

    [Fact]
    public void D3_MissingVerbOrClsidBecomesPartialAndRepairRestoresIt()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        service.Install();

        harness.Store.DeleteKeyTree(ShellIntegrationContract.VerbKeyPath(".webp"));
        var afterVerbLoss = service.Detect();
        Assert.Equal(ShellIntegrationStatus.Partial, afterVerbLoss.Status);
        Assert.Equal(10, afterVerbLoss.RegisteredExtensionCount);
        Assert.Contains(".webp", afterVerbLoss.MissingExtensions);

        Assert.True(service.Repair().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, service.Detect().Status);

        harness.Store.DeleteKeyTree(ShellIntegrationContract.ClsidKeyPath);
        Assert.Equal(ShellIntegrationStatus.Partial, service.Detect().Status);
        Assert.True(service.Repair().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, service.Detect().Status);

        harness.Store.DeleteKeyTree(ShellIntegrationContract.CommandKeyPath(".png"));
        Assert.Equal(ShellIntegrationStatus.Partial, service.Detect().Status);
        Assert.True(service.Repair().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, service.Detect().Status);
    }

    [Fact]
    public void D4_WrongIconOrSchemaIsPartialAndRepairable()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        service.Install();

        harness.Store.SetStringValue(ShellIntegrationContract.VerbKeyPath(".png"), "Icon", @"C:\other\app.exe,0");
        Assert.Equal(ShellIntegrationStatus.Partial, service.Detect().Status);
        Assert.True(service.Repair().Succeeded);

        harness.Store.SetInt32Value(ShellIntegrationContract.VerbKeyPath(".ico"), ShellIntegrationContract.SchemaValueName, 99);
        Assert.Equal(ShellIntegrationStatus.Partial, service.Detect().Status);
        Assert.True(service.Repair().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, service.Detect().Status);
    }

    [Fact]
    public void D5_MovedExecutableWithMissingOldCopyIsRepairedAutomatically()
    {
        var harness = new Harness();
        harness.ExistingFiles.Add(OldExe);
        harness.CreateService(OldExe).Install();
        harness.ExistingFiles.Remove(OldExe);

        var current = harness.CreateService();
        var state = current.Detect();
        Assert.Equal(ShellIntegrationStatus.InstalledStale, state.Status);
        Assert.False(state.TargetExeExists);
        Assert.False(state.HasOtherLivingCopy);

        var reconcile = current.ReconcileOnStartup();
        Assert.Equal(ShellIntegrationReconcileAction.PathAutoUpdated, reconcile.Action);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, reconcile.State.Status);
        AssertFullyRegistered(harness.Store, CurrentExe);
    }

    [Fact]
    public void D6_MovedExecutableWithLivingOldCopyIsNotTakenOverAutomatically()
    {
        var harness = new Harness();
        harness.ExistingFiles.Add(OldExe);
        harness.CreateService(OldExe).Install();

        var current = harness.CreateService();
        var state = current.Detect();
        Assert.Equal(ShellIntegrationStatus.InstalledStale, state.Status);
        Assert.True(state.HasOtherLivingCopy);

        var before = harness.Store.Snapshot();
        var reconcile = current.ReconcileOnStartup();

        Assert.Equal(ShellIntegrationReconcileAction.NeedsUserDecision, reconcile.Action);
        Assert.Equal(before, harness.Store.Snapshot());

        // 用户明确选择“设为当前版本”后才切换。
        Assert.True(current.Repair().Succeeded);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, current.Detect().Status);
        AssertFullyRegistered(harness.Store, CurrentExe);
    }

    [Fact]
    public void D7_ForeignVerbOwnershipIsConflictWithZeroWritesAndZeroDeletes()
    {
        var harness = new Harness();
        var verb = ShellIntegrationContract.VerbKeyPath(".jpg");
        harness.Store.SetStringValue(verb, null, "其他工具的菜单");
        harness.Store.SetStringValue(verb, ShellIntegrationContract.OwnerValueName, "SomeOtherVendor");
        var before = harness.Store.Snapshot();

        var service = harness.CreateService();
        var state = service.Detect();
        Assert.Equal(ShellIntegrationStatus.Conflict, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.IsConflict);

        var install = service.Install();
        Assert.False(install.Succeeded);
        Assert.Equal(before, harness.Store.Snapshot());
        Assert.Equal(0, harness.ShellNotifications);

        var remove = service.Remove();
        Assert.False(remove.Succeeded);
        Assert.Equal("其他工具的菜单", harness.Store.GetStringValue(verb, null));
    }

    [Fact]
    public void D8_InterruptedInstallIsCompletedOnNextInteractiveStart()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        service.Install();
        harness.Store.SetStringValue(
            ShellIntegrationContract.PrivateMetadataKeyPath,
            "OperationState",
            ShellIntegrationOperationState.Installing.ToString());
        harness.Store.DeleteKeyTree(ShellIntegrationContract.VerbKeyPath(".tiff"));

        Assert.Equal(ShellIntegrationStatus.RecoveryPending, service.Detect().Status);

        var reconcile = service.ReconcileOnStartup();
        Assert.Equal(ShellIntegrationReconcileAction.InterruptedOperationCompleted, reconcile.Action);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, reconcile.State.Status);
        Assert.Equal(11, reconcile.State.RegisteredExtensionCount);
    }

    [Fact]
    public void D9_InterruptedRemoveIsFinishedOnNextInteractiveStart()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        service.Install();
        harness.Store.DeleteKeyTree(ShellIntegrationContract.VerbKeyPath(".gif"));
        harness.Store.SetStringValue(
            ShellIntegrationContract.PrivateMetadataKeyPath,
            "OperationState",
            ShellIntegrationOperationState.Removing.ToString());

        Assert.Equal(ShellIntegrationStatus.RecoveryPending, service.Detect().Status);

        var reconcile = service.ReconcileOnStartup();
        Assert.Equal(ShellIntegrationReconcileAction.InterruptedOperationCompleted, reconcile.Action);
        Assert.Equal(ShellIntegrationStatus.NotInstalled, reconcile.State.Status);
    }

    [Fact]
    public void D10_RemoveKeepsParentContainersAndUnrelatedVerbs()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        service.Install();

        var siblingVerb = @"Software\Classes\SystemFileAssociations\.jpg\shell\OtherTool.Verb";
        harness.Store.SetStringValue(siblingVerb, null, "其他工具");

        Assert.True(service.Remove().Succeeded);

        Assert.True(harness.Store.KeyExists(@"Software\Classes\SystemFileAssociations\.jpg\shell"));
        Assert.Equal("其他工具", harness.Store.GetStringValue(siblingVerb, null));
        Assert.False(harness.Store.KeyExists(ShellIntegrationContract.VerbKeyPath(".jpg")));
        Assert.False(harness.Store.KeyExists(ShellIntegrationContract.ClsidKeyPath));
        Assert.False(harness.Store.KeyExists(ShellIntegrationContract.PrivateMetadataKeyPath));
    }

    [Fact]
    public void D11_StartupNeverInstallsByItself()
    {
        var harness = new Harness();
        var service = harness.CreateService();

        var reconcile = service.ReconcileOnStartup();

        Assert.Equal(ShellIntegrationReconcileAction.None, reconcile.Action);
        Assert.Equal(ShellIntegrationStatus.NotInstalled, reconcile.State.Status);
        Assert.Equal(0, harness.Store.WriteCount);
    }

    [Fact]
    public void D12_ProductVersionOnlyChangeIsUpdatedSilently()
    {
        var harness = new Harness();
        harness.CreateService(version: "3.2.5").Install();

        var upgraded = harness.CreateService(version: "3.3.0");
        var reconcile = upgraded.ReconcileOnStartup();

        Assert.Equal(ShellIntegrationReconcileAction.ProductVersionUpdated, reconcile.Action);
        Assert.Equal(ShellIntegrationStatus.InstalledCurrent, reconcile.State.Status);
        Assert.Equal("3.3.0", reconcile.State.ProductVersion);
    }

    [Fact]
    public void D13_DiagnosticReportContainsLocationsAndCounts()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        service.Install();
        harness.Store.DeleteKeyTree(ShellIntegrationContract.VerbKeyPath(".bmp"));

        var report = service.Detect().BuildDiagnosticReport();

        Assert.Contains("已注册扩展：10/11", report, StringComparison.Ordinal);
        Assert.Contains(ShellIntegrationContract.DropTargetClsidKey, report, StringComparison.Ordinal);
        Assert.Contains("缺失扩展：.bmp", report, StringComparison.Ordinal);
    }

    [Fact]
    public void D14_HintTellsWindows11UsersWhereTheClassicMenuLives()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        var notInstalled = service.Detect();
        service.Install();
        var installed = service.Detect();

        var win11NotInstalled = ShellIntegrationPresentation.BuildHint(notInstalled, isWindows11OrLater: true);
        var win11Installed = ShellIntegrationPresentation.BuildHint(installed, isWindows11OrLater: true);
        var win10Installed = ShellIntegrationPresentation.BuildHint(installed, isWindows11OrLater: false);

        Assert.Contains(ShellIntegrationPresentation.Windows11EntryNote, win11NotInstalled, StringComparison.Ordinal);
        Assert.Contains("添加到 NanoPic", win11NotInstalled, StringComparison.Ordinal);
        Assert.Contains(ShellIntegrationPresentation.Windows11EntryNote, win11Installed, StringComparison.Ordinal);
        Assert.DoesNotContain(ShellIntegrationPresentation.Windows11EntryNote, win10Installed, StringComparison.Ordinal);
        Assert.Contains("已启用", win10Installed, StringComparison.Ordinal);
    }

    [Fact]
    public void D15_AbnormalStateHintsStayActionable()
    {
        var harness = new Harness();
        harness.ExistingFiles.Add(OldExe);
        harness.CreateService(OldExe).Install();
        var stale = harness.CreateService().Detect();

        Assert.Equal(ShellIntegrationStatus.InstalledStale, stale.Status);
        Assert.Contains("另一份 NanoPic", ShellIntegrationPresentation.BuildHint(stale, true), StringComparison.Ordinal);

        harness.Store.DeleteKeyTree(ShellIntegrationContract.VerbKeyPath(".png"));
        var partial = harness.CreateService(OldExe).Detect();
        Assert.Equal(ShellIntegrationStatus.Partial, partial.Status);
        Assert.Contains("需要修复", ShellIntegrationPresentation.BuildHint(partial, false), StringComparison.Ordinal);
    }

    [Fact]
    public void D16_RemovePreservesEverythingWhenAnOwnedTreeContainsUnknownThirdPartyContent()
    {
        var harness = new Harness();
        var service = harness.CreateService();
        Assert.True(service.Install().Succeeded);
        var transactionId = service.Detect().TransactionId;
        var foreignChild = ShellIntegrationContract.CommandKeyPath(".png") + @"\OtherTool";
        harness.Store.SetStringValue(foreignChild, null, "第三方内容");
        var before = harness.Store.Snapshot();

        var removed = service.Remove();

        Assert.False(removed.Succeeded);
        Assert.Equal(before, harness.Store.Snapshot());
        Assert.True(harness.Store.KeyExists(foreignChild));
        Assert.Equal(transactionId, service.Detect().TransactionId);
    }
}
