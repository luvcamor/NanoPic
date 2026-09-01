using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.App.Tests;

public sealed class ShellIntegrationSettingsUiTests
{
    private static ShellIntegrationState CreateState(
        ShellIntegrationStatus status,
        bool targetExists = true,
        int registered = 11,
        ShellIntegrationOperationState operationState = ShellIntegrationOperationState.Installed) =>
        new(
            status,
            operationState,
            @"C:\Portable\NanoPic.exe",
            targetExists,
            "3.2.5",
            "{11111111-2222-3333-4444-555555555555}",
            registered,
            Array.Empty<string>(),
            status == ShellIntegrationStatus.Conflict
                ? new[] { new ShellIntegrationDiagnostic("Software\\Classes\\CLSID\\x", "被第三方改写", true) }
                : Array.Empty<ShellIntegrationDiagnostic>());

    private static void Inspect(ShellIntegrationState state, string? transientHint, Action<CheckBox, TextBlock, Panel, IReadOnlyDictionary<string, Button>> assertions)
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var method = typeof(MainWindow).GetMethod("RefreshShellIntegrationUi", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(window, new object?[] { state, transientHint });

            var check = Assert.IsType<CheckBox>(window.FindName("ShellIntegrationCheck"));
            var hint = Assert.IsType<TextBlock>(window.FindName("ShellIntegrationHint"));
            var actions = Assert.IsAssignableFrom<Panel>(window.FindName("ShellIntegrationActions"));
            var buttons = new Dictionary<string, Button>(StringComparer.Ordinal)
            {
                ["adopt"] = Assert.IsType<Button>(window.FindName("ShellIntegrationAdoptButton")),
                ["repair"] = Assert.IsType<Button>(window.FindName("ShellIntegrationRepairButton")),
                ["remove"] = Assert.IsType<Button>(window.FindName("ShellIntegrationRemoveButton")),
                ["diagnostics"] = Assert.IsType<Button>(window.FindName("ShellIntegrationDiagnosticsButton"))
            };

            assertions(check, hint, actions, buttons);
        });
    }

    [Fact]
    public void E1_NotInstalledShowsCompactUncheckedSwitch()
    {
        Inspect(CreateState(ShellIntegrationStatus.NotInstalled, registered: 0, operationState: ShellIntegrationOperationState.None), null, (check, hint, actions, _) =>
        {
            Assert.False(check.IsChecked);
            Assert.True(check.IsEnabled);
            Assert.Equal(Visibility.Collapsed, actions.Visibility);
            Assert.Contains("添加到 NanoPic", hint.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void E2_InstalledCurrentShowsCheckedSwitchWithoutActionRow()
    {
        Inspect(CreateState(ShellIntegrationStatus.InstalledCurrent), null, (check, _, actions, _) =>
        {
            Assert.True(check.IsChecked);
            Assert.True(check.IsEnabled);
            Assert.Equal(Visibility.Collapsed, actions.Visibility);
        });
    }

    [Fact]
    public void E3_OtherLivingCopyOffersAdoptAndRemove()
    {
        Inspect(CreateState(ShellIntegrationStatus.InstalledStale), null, (check, hint, actions, buttons) =>
        {
            Assert.Null(check.IsChecked);
            Assert.True(check.IsEnabled);
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.Equal(Visibility.Visible, buttons["adopt"].Visibility);
            Assert.Equal(Visibility.Visible, buttons["remove"].Visibility);
            Assert.Equal(Visibility.Collapsed, buttons["repair"].Visibility);
            Assert.Contains("另一份 NanoPic", hint.Text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(ShellIntegrationStatus.Partial)]
    [InlineData(ShellIntegrationStatus.RecoveryPending)]
    public void E4_PartialAndRecoveryPendingOfferRepairAndRemove(ShellIntegrationStatus status)
    {
        Inspect(CreateState(status, registered: 9, operationState: ShellIntegrationOperationState.Installing), null, (check, hint, actions, buttons) =>
        {
            Assert.Null(check.IsChecked);
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.Equal(Visibility.Visible, buttons["repair"].Visibility);
            Assert.Equal(Visibility.Visible, buttons["remove"].Visibility);
            Assert.Equal(Visibility.Collapsed, buttons["adopt"].Visibility);
            Assert.Contains("需要修复", hint.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void E5_ConflictDisablesToggleAndOnlyOffersDiagnostics()
    {
        Inspect(CreateState(ShellIntegrationStatus.Conflict, registered: 3), null, (check, hint, actions, buttons) =>
        {
            Assert.Null(check.IsChecked);
            Assert.False(check.IsEnabled);
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.Equal(Visibility.Visible, buttons["diagnostics"].Visibility);
            Assert.Equal(Visibility.Collapsed, buttons["repair"].Visibility);
            Assert.Equal(Visibility.Collapsed, buttons["remove"].Visibility);
            Assert.Contains("冲突", hint.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void E6_AutomaticPathRepairShowsTransientHintOnly()
    {
        Inspect(CreateState(ShellIntegrationStatus.InstalledCurrent), "已自动更新右键菜单路径。", (check, hint, actions, _) =>
        {
            Assert.True(check.IsChecked);
            Assert.Equal("已自动更新右键菜单路径。", hint.Text);
            Assert.Equal(Visibility.Collapsed, actions.Visibility);
        });
    }

    [Fact]
    public void E7_OnlyStableStatesAllowDirectToggle()
    {
        Assert.True(CreateState(ShellIntegrationStatus.NotInstalled).AllowsDirectToggle);
        Assert.True(CreateState(ShellIntegrationStatus.InstalledCurrent).AllowsDirectToggle);
        Assert.False(CreateState(ShellIntegrationStatus.InstalledStale).AllowsDirectToggle);
        Assert.False(CreateState(ShellIntegrationStatus.Partial).AllowsDirectToggle);
        Assert.False(CreateState(ShellIntegrationStatus.Conflict).AllowsDirectToggle);
        Assert.False(CreateState(ShellIntegrationStatus.RecoveryPending).AllowsDirectToggle);
    }

    [Fact]
    public void E8_ActionRowStaysInsideTheNarrowSettingsColumn()
    {
        // 设置栏固定 264 px：异常状态下的操作按钮必须换行而不是被裁掉。
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var method = typeof(MainWindow).GetMethod("RefreshShellIntegrationUi", BindingFlags.Instance | BindingFlags.NonPublic);
            method!.Invoke(window, new object?[] { CreateState(ShellIntegrationStatus.InstalledStale), null });

            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            root.Measure(new Size(820, 540));
            root.Arrange(new Rect(0, 0, 820, 540));
            root.UpdateLayout();

            var actions = Assert.IsAssignableFrom<Panel>(window.FindName("ShellIntegrationActions"));
            var available = actions.ActualWidth;
            Assert.True(available > 0, "操作行没有获得布局宽度。");
            foreach (var button in actions.Children.OfType<Button>().Where(child => child.Visibility == Visibility.Visible))
            {
                var bounds = button.TransformToAncestor(actions).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Right <= available + 0.5, $"按钮“{button.Content}”超出设置栏宽度：{bounds} / {available}。");
            }
        });
    }

    [Fact]
    public void E9_SwitchIsTwoStateSoAClickCannotCycleIntoAnAccidentalUninstall()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var check = Assert.IsType<CheckBox>(window.FindName("ShellIntegrationCheck"));

            // 三态开关点击会依次进入"不确定"，而"不确定→未勾选"这一步会被当成卸载，
            // 用户看起来只是又点了一下开关，实际却把右键菜单删掉了。
            Assert.False(check.IsThreeState);

            var method = typeof(MainWindow).GetMethod("RefreshShellIntegrationUi", BindingFlags.Instance | BindingFlags.NonPublic);
            method!.Invoke(window, new object?[] { CreateState(ShellIntegrationStatus.Partial, registered: 5), null });
            Assert.Null(check.IsChecked);
        });
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
