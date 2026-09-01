using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class ShellAddProtocolTests
{
    private static ShellAddRequest CreateRequest(params string[] paths) =>
        new(Guid.NewGuid(), ShellAddOrigin.ExplorerDropTarget, paths, ActivateWindow: true, UnavailableItemCount: 1);

    [Fact]
    public void B1_AddPathsRoundTripPreservesTrickyPaths()
    {
        var request = CreateRequest(
            @"C:\Users\张三\图片\照片 (1) & 副本.jpg",
            @"C:\temp\" + new string('a', 200) + ".png",
            @"\\server\share\dir with space\x.webp");

        var frame = ShellAddProtocol.Encode(new ShellAddMessage(ShellAddMessageKind.AddPaths, request, null, 0, 0));
        Assert.True(ShellAddProtocol.TryDecode(frame, out var message, out _));

        Assert.Equal(ShellAddMessageKind.AddPaths, message.Kind);
        Assert.NotNull(message.Request);
        Assert.Equal(request.RequestId, message.Request!.RequestId);
        Assert.Equal(request.Paths, message.Request.Paths);
        Assert.True(message.Request.ActivateWindow);
        Assert.Equal(1, message.Request.UnavailableItemCount);
        Assert.Equal(ShellAddOrigin.ExplorerDropTarget, message.Request.Origin);
    }

    [Fact]
    public void B2_RegistrationAndActivationRoundTrip()
    {
        var registration = new ShellAddInstanceRegistration(4242, "NanoPic.ShellAdd.Instance.test", 0x1234, 987654321L);
        var frame = ShellAddProtocol.Encode(new ShellAddMessage(
            ShellAddMessageKind.RegisterInstance, null, registration, registration.ProcessId, registration.ActivationTicks));
        Assert.True(ShellAddProtocol.TryDecode(frame, out var message, out _));
        Assert.Equal(registration, message.Registration);

        var activation = ShellAddProtocol.Encode(new ShellAddMessage(ShellAddMessageKind.InstanceActivated, null, null, 7, 42));
        Assert.True(ShellAddProtocol.TryDecode(activation, out var activated, out _));
        Assert.Equal(ShellAddMessageKind.InstanceActivated, activated.Kind);
        Assert.Equal(7, activated.ProcessId);
        Assert.Equal(42, activated.ActivationTicks);
    }

    [Fact]
    public void B3_CorruptFramesAreRejectedWithoutThrowing()
    {
        var frame = ShellAddProtocol.Encode(new ShellAddMessage(ShellAddMessageKind.AddPaths, CreateRequest(@"C:\a.png"), null, 0, 0));

        var badMagic = frame.ToArray();
        badMagic[0] = (byte)'X';
        Assert.False(ShellAddProtocol.TryDecode(badMagic, out _, out _));

        var badVersion = frame.ToArray();
        badVersion[4] = 99;
        Assert.False(ShellAddProtocol.TryDecode(badVersion, out _, out var failure));
        Assert.Equal(ShellAddStatus.ProtocolMismatch, failure);

        var truncated = frame.Take(frame.Length - 4).ToArray();
        Assert.False(ShellAddProtocol.TryDecode(truncated, out _, out _));
    }

    [Fact]
    public void B4_ResponseRoundTrip()
    {
        var frame = ShellAddProtocol.EncodeResponse(ShellAddStatus.NoTarget, "没有可接收的窗口");
        Assert.True(ShellAddProtocol.TryDecodeResponse(frame, out var status, out var diagnostic));
        Assert.Equal(ShellAddStatus.NoTarget, status);
        Assert.Equal("没有可接收的窗口", diagnostic);
    }

    [Fact]
    public void B5_LedgerDeduplicatesRequestIds()
    {
        var ledger = new ShellAddRequestLedger(capacity: 4);
        var id = Guid.NewGuid();

        Assert.True(ledger.TryBegin(id));
        Assert.False(ledger.TryBegin(id));
        Assert.True(ledger.TryBegin(Guid.NewGuid()));

        ledger.Forget(id);
        Assert.True(ledger.TryBegin(id));
        Assert.False(ledger.TryBegin(id));
    }

    [Fact]
    public void B6_RegistryRanksMostRecentlyActivatedFirstAndDropsDeadProcesses()
    {
        var alive = new HashSet<int> { 10, 20 };
        var registry = new ShellAddInstanceRegistry(pid => alive.Contains(pid));
        registry.Register(new ShellAddInstanceRegistration(10, "pipe-10", 0, 100));
        registry.Register(new ShellAddInstanceRegistration(20, "pipe-20", 0, 200));
        registry.Register(new ShellAddInstanceRegistration(30, "pipe-30", 0, 300));

        var ranked = registry.RankedTargets();
        Assert.Equal(new[] { 20, 10 }, ranked.Select(target => target.ProcessId).ToArray());

        registry.Touch(10, 500);
        Assert.Equal(10, registry.RankedTargets()[0].ProcessId);

        registry.MarkDead(10);
        Assert.Equal(new[] { 20 }, registry.RankedTargets().Select(target => target.ProcessId).ToArray());
        Assert.True(registry.HasEverRegistered);
    }

    [Fact]
    public void B7_PipeServerRoundTripUsesCurrentUserOnlyAcl()
    {
        var identity = ShellAddIdentity.CreateIsolated("pipe-" + Guid.NewGuid().ToString("N"));
        var received = new List<ShellAddMessage>();
        using var server = new ShellAddPipeServer(identity.BrokerPipeName, message =>
        {
            received.Add(message);
            return ShellAddStatus.Accepted;
        });
        server.Start();

        var request = CreateRequest(@"C:\images\a.png", @"C:\images\b.png");
        var status = ShellAddPipeClient.Send(
            identity.BrokerPipeName,
            new ShellAddMessage(ShellAddMessageKind.AddPaths, request, null, 0, 0),
            connectTimeoutMilliseconds: 2000,
            responseTimeoutMilliseconds: 4000,
            out _);

        Assert.Equal(ShellAddStatus.Accepted, status);
        Assert.Equal(request.Paths, Assert.Single(received).Request!.Paths);
    }

    [Fact]
    public void B8_SendToMissingEndpointReportsNoTarget()
    {
        var identity = ShellAddIdentity.CreateIsolated("absent-" + Guid.NewGuid().ToString("N"));
        var status = ShellAddPipeClient.Send(
            identity.BrokerPipeName,
            new ShellAddMessage(ShellAddMessageKind.Ping, null, null, 0, 0),
            connectTimeoutMilliseconds: 300,
            responseTimeoutMilliseconds: 500,
            out _);

        Assert.Equal(ShellAddStatus.NoTarget, status);
    }

    [Fact]
    public void B9_BrokerRoutesToLocalWindowAndDeduplicatesRetries()
    {
        var identity = ShellAddIdentity.CreateIsolated("broker-" + Guid.NewGuid().ToString("N"));
        var delivered = new List<ShellAddRequest>();
        using var broker = new ShellAddService(identity, options: FastOptions());
        broker.LocalImportHandler = request =>
        {
            delivered.Add(request);
            return ShellAddStatus.Accepted;
        };
        broker.StartInstance();
        WaitUntil(() => broker.IsBrokerOwner, TimeSpan.FromSeconds(5));

        var request = CreateRequest(@"C:\images\a.png");
        Assert.Equal(ShellAddStatus.Accepted, broker.Deliver(request));
        Assert.Equal(ShellAddStatus.Duplicate, broker.Deliver(request));
        Assert.Single(delivered);
    }

    [Fact]
    public void B10_SecondInstanceReceivesRequestWhenItWasActivatedMoreRecently()
    {
        var identity = ShellAddIdentity.CreateIsolated("route-" + Guid.NewGuid().ToString("N"));
        var brokerDeliveries = 0;
        var instanceDeliveries = new List<ShellAddRequest>();
        var otherProcessId = Process.GetCurrentProcess().Id + 1000;

        using var brokerService = new ShellAddService(identity, options: FastOptions(), isProcessAlive: _ => true);
        brokerService.LocalImportHandler = _ =>
        {
            brokerDeliveries++;
            return ShellAddStatus.Accepted;
        };
        brokerService.StartInstance();
        WaitUntil(() => brokerService.IsBrokerOwner, TimeSpan.FromSeconds(5));

        // 同进程内模拟“另一个更晚激活的窗口”：独立 PID、独立端点 + 更大的激活序号。
        var otherEndpoint = identity.CreateInstancePipeName(otherProcessId);
        using var otherWindow = new ShellAddPipeServer(otherEndpoint, message =>
        {
            if (message.Request is not null)
            {
                instanceDeliveries.Add(message.Request);
            }

            return ShellAddStatus.Accepted;
        });
        otherWindow.Start();

        var registerStatus = ShellAddPipeClient.Send(
            identity.BrokerPipeName,
            new ShellAddMessage(
                ShellAddMessageKind.RegisterInstance,
                null,
                new ShellAddInstanceRegistration(otherProcessId, otherEndpoint, 0, long.MaxValue),
                otherProcessId,
                long.MaxValue),
            2000,
            4000,
            out _);
        Assert.Equal(ShellAddStatus.Accepted, registerStatus);

        Assert.Equal(ShellAddStatus.Accepted, brokerService.Deliver(CreateRequest(@"C:\images\b.png")));
        Assert.Single(instanceDeliveries);
        Assert.Equal(0, brokerDeliveries);
    }

    [Fact]
    public void B11_ColdStartWithoutAnyWindowReportsNoTargetImmediately()
    {
        var identity = ShellAddIdentity.CreateIsolated("cold-" + Guid.NewGuid().ToString("N"));
        using var embedding = new ShellAddService(identity, options: FastOptions());
        embedding.StartEmbedding();
        WaitUntil(() => embedding.IsBrokerOwner, TimeSpan.FromSeconds(5));

        var started = Stopwatch.StartNew();
        var status = embedding.Deliver(CreateRequest(@"C:\images\c.png"));
        started.Stop();

        Assert.Equal(ShellAddStatus.NoTarget, status);
        Assert.True(started.ElapsedMilliseconds < 1000, $"冷启动不应等待重新登记，实际耗时 {started.ElapsedMilliseconds} ms。");
    }

    [Fact]
    public void B12_EmbeddingTakesOverLocallyAfterPromotion()
    {
        var identity = ShellAddIdentity.CreateIsolated("takeover-" + Guid.NewGuid().ToString("N"));
        var delivered = new List<ShellAddRequest>();
        using var embedding = new ShellAddService(identity, options: FastOptions());
        embedding.StartEmbedding();
        WaitUntil(() => embedding.IsBrokerOwner, TimeSpan.FromSeconds(5));
        var request = CreateRequest(@"C:\images\d.png");
        Assert.Equal(ShellAddStatus.NoTarget, embedding.Deliver(request));

        embedding.LocalImportHandler = request =>
        {
            delivered.Add(request);
            return ShellAddStatus.Accepted;
        };
        embedding.PromoteToInstance();

        // NoTarget 没有进入任何队列，不得把同一请求 ID 永久误记成 Duplicate。
        Assert.Equal(ShellAddStatus.Accepted, embedding.Deliver(request));
        Assert.Single(delivered);
    }

    [Fact]
    public void B13_OnlyOneServiceOwnsTheBrokerMutexPerSession()
    {
        var identity = ShellAddIdentity.CreateIsolated("election-" + Guid.NewGuid().ToString("N"));
        using var first = new ShellAddService(identity, options: FastOptions());
        using var second = new ShellAddService(identity, options: FastOptions());
        first.StartEmbedding();
        second.StartEmbedding();

        WaitUntil(() => first.IsBrokerOwner || second.IsBrokerOwner, TimeSpan.FromSeconds(5));
        Thread.Sleep(600);

        Assert.True(first.IsBrokerOwner ^ second.IsBrokerOwner, "同一用户会话内只能有一个 broker owner。");
    }

    [Fact]
    public void B14_TargetThatDisappearsBeforeAckIsSkippedForTheNextLiveWindow()
    {
        var identity = ShellAddIdentity.CreateIsolated("reselect-" + Guid.NewGuid().ToString("N"));
        var localDeliveries = new List<ShellAddRequest>();
        var goneProcessId = Process.GetCurrentProcess().Id + 2000;

        using var brokerService = new ShellAddService(identity, options: FastOptions(), isProcessAlive: _ => true);
        brokerService.LocalImportHandler = request =>
        {
            localDeliveries.Add(request);
            return ShellAddStatus.Accepted;
        };
        brokerService.StartInstance();
        WaitUntil(() => brokerService.IsBrokerOwner, TimeSpan.FromSeconds(5));

        // 最近激活的窗口已经消失：端点从未监听，broker 必须顺延到下一个存活窗口而不是丢弃请求。
        var status = ShellAddPipeClient.Send(
            identity.BrokerPipeName,
            new ShellAddMessage(
                ShellAddMessageKind.RegisterInstance,
                null,
                new ShellAddInstanceRegistration(goneProcessId, identity.CreateInstancePipeName(goneProcessId), 0, long.MaxValue),
                goneProcessId,
                long.MaxValue),
            2000,
            4000,
            out _);
        Assert.Equal(ShellAddStatus.Accepted, status);

        Assert.Equal(ShellAddStatus.Accepted, brokerService.Deliver(CreateRequest(@"C:\images\f.png")));
        Assert.Single(localDeliveries);
    }

    [Fact]
    public void B15_PayloadCapRejectsAbsurdRequestsWithoutAllocatingUnbounded()
    {
        var hugePath = new string('p', 1024 * 1024);
        var paths = Enumerable.Range(0, 80).Select(_ => hugePath).ToArray();
        var request = new ShellAddRequest(Guid.NewGuid(), ShellAddOrigin.ExplorerDropTarget, paths, true, 0);

        Assert.Throws<InvalidOperationException>(() =>
            ShellAddProtocol.Encode(new ShellAddMessage(ShellAddMessageKind.AddPaths, request, null, 0, 0)));
        Assert.Equal(64 * 1024 * 1024, ShellAddProtocol.MaxPayloadBytes);
    }

    [Fact]
    public void B16_EndpointNamesAreScopedToTheCurrentUserAndSession()
    {
        var identity = ShellAddIdentity.Current();
        var sessionId = Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Local\ 前缀而不是 Global\：请求不跨 RDP、快速用户切换或其他登录会话。
        Assert.StartsWith(@"Local\", identity.BrokerMutexName, StringComparison.Ordinal);
        Assert.StartsWith(@"Local\", identity.RegistryMutexName, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Global\", identity.BrokerMutexName, StringComparison.Ordinal);

        foreach (var name in new[] { identity.BrokerMutexName, identity.BrokerPipeName, identity.CreateInstancePipeName(1234) })
        {
            Assert.Contains(identity.UserSid, name, StringComparison.Ordinal);
            Assert.Contains(sessionId, name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void B17_BrokerCallbackFailureReleasesMutexAndEndpointForTheNextInstance()
    {
        var identity = ShellAddIdentity.CreateIsolated("callback-failover-" + Guid.NewGuid().ToString("N"));
        using var callbackAttempted = new ManualResetEventSlim();
        using var failed = new ShellAddService(identity, options: FastOptions());
        failed.BrokerOwnershipAcquired = () =>
        {
            callbackAttempted.Set();
            throw new ApplicationException("Simulated COM registration failure.");
        };

        failed.StartEmbedding();
        Assert.True(callbackAttempted.Wait(TimeSpan.FromSeconds(5)), "第一个实例没有进入 broker 初始化回调。");
        WaitUntil(() => !failed.IsBrokerOwner, TimeSpan.FromSeconds(5));

        using var successor = new ShellAddService(identity, options: FastOptions());
        successor.StartEmbedding();
        WaitUntil(() => successor.IsBrokerOwner, TimeSpan.FromSeconds(5));

        Assert.False(failed.IsBrokerOwner);
        Assert.True(successor.IsBrokerOwner);
        AssertBrokerEndpointResponds(identity);
    }

    [Fact]
    public void B18_PartialPipeStartFailureRollsBackBeforeTheNextInstanceTakesOver()
    {
        var identity = ShellAddIdentity.CreateIsolated("pipe-failover-" + Guid.NewGuid().ToString("N"));
        using var startAttempted = new ManualResetEventSlim();
        using var failed = new ShellAddService(
            identity,
            log: null,
            options: FastOptions(),
            isProcessAlive: null,
            startBrokerServer: server =>
            {
                server.Start();
                startAttempted.Set();
                throw new IOException("Simulated failure after the broker pipe started.");
            });

        failed.StartEmbedding();
        Assert.True(startAttempted.Wait(TimeSpan.FromSeconds(5)), "第一个实例没有进入 broker pipe 启动阶段。");
        WaitUntil(() => !failed.IsBrokerOwner, TimeSpan.FromSeconds(5));

        using var successor = new ShellAddService(identity, options: FastOptions());
        successor.StartEmbedding();
        WaitUntil(() => successor.IsBrokerOwner, TimeSpan.FromSeconds(5));

        Assert.False(failed.IsBrokerOwner);
        Assert.True(successor.IsBrokerOwner);
        AssertBrokerEndpointResponds(identity);
    }

    [Fact]
    public void B19_TransientBrokerStartFailureRetriesWithinTheSameInstance()
    {
        var identity = ShellAddIdentity.CreateIsolated("self-retry-" + Guid.NewGuid().ToString("N"));
        var attempts = 0;
        using var service = new ShellAddService(
            identity,
            log: null,
            options: FastOptions(),
            isProcessAlive: null,
            startBrokerServer: server =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new IOException("Simulated transient broker startup failure.");
                }

                server.Start();
            });

        service.StartEmbedding();
        WaitUntil(() => service.IsBrokerOwner, TimeSpan.FromSeconds(5));

        Assert.True(attempts >= 2);
        AssertBrokerEndpointResponds(identity);
    }

    private static ShellAddServiceOptions FastOptions() => new()
    {
        ElectionPollMilliseconds = 100,
        HeartbeatMilliseconds = 200,
        ConnectTimeoutMilliseconds = 500,
        ResponseTimeoutMilliseconds = 2000,
        RecoveryGraceMilliseconds = 400,
        DeliveryAttempts = 2
    };

    private static void AssertBrokerEndpointResponds(ShellAddIdentity identity)
    {
        var status = ShellAddPipeClient.Send(
            identity.BrokerPipeName,
            new ShellAddMessage(ShellAddMessageKind.Ping, null, null, 0, 0),
            connectTimeoutMilliseconds: 2000,
            responseTimeoutMilliseconds: 4000,
            out var diagnostic);

        Assert.True(status == ShellAddStatus.Accepted, $"接任后的 broker endpoint 不可用：{status}。{diagnostic}");
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }

        Assert.True(condition(), "等待条件超时。");
    }
}
