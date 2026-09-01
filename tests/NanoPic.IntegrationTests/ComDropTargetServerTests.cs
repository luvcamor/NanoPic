using System;
using System.Runtime.InteropServices;
using System.Threading;
using NanoPic.Infrastructure;
using Xunit;

namespace NanoPic.IntegrationTests;

public sealed class ComDropTargetServerTests
{
    private sealed class FakeServerReferences
    {
        private int _current;

        public int AddCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int ZeroNotifications { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int Current => Volatile.Read(ref _current);

        public uint Add()
        {
            AddCalls++;
            return (uint)Interlocked.Increment(ref _current);
        }

        public uint Release()
        {
            ReleaseCalls++;
            return (uint)Interlocked.Decrement(ref _current);
        }

        public void NotifyZero() => ZeroNotifications++;

        public int Disconnect(object target)
        {
            Assert.IsType<NanoPicDropTarget>(target);
            DisconnectCalls++;
            return ShellComNative.SOk;
        }

        public ServerProcessLifetime CreateLifetime() => new(Add, Release, NotifyZero);

        public ComDropTargetServer CreateServer(Func<ShellDropPayload, bool>? handler = null) => new(
            Guid.NewGuid(),
            handler ?? (_ => true),
            Add,
            Release,
            Disconnect);
    }

    [Fact]
    public void L1_DragLeaveKeepsServerReferenceUntilComConnectionCloses()
    {
        var references = new FakeServerReferences();
        var server = references.CreateServer();
        var releasedEvents = 0;
        server.ServerReferencesReleased += (_, _) => releasedEvents++;
        var target = Assert.IsType<NanoPicDropTarget>(server.CreateDropTarget());

        Assert.Equal(1u, target.AddConnection(ShellComNative.ExternalConnectionStrong, 0));
        Assert.Equal(ShellComNative.SOk, target.DragLeave());
        Assert.Equal(1, references.Current);
        Assert.Equal(0, references.ReleaseCalls);

        Assert.Equal(0u, target.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true));
        Assert.Equal(0, references.Current);
        Assert.Equal(1, references.ReleaseCalls);
        Assert.Equal(1, references.DisconnectCalls);
        Assert.Equal(1, releasedEvents);
    }

    [Fact]
    public void L2_HandledDropsDoNotReleaseTheObjectReference()
    {
        var references = new FakeServerReferences();
        var handled = 0;
        var server = references.CreateServer(_ =>
        {
            handled++;
            return true;
        });
        var target = Assert.IsType<NanoPicDropTarget>(server.CreateDropTarget());
        target.AddConnection(ShellComNative.ExternalConnectionStrong, 0);

        Assert.True(server.HandleDrop(new ShellDropPayload(new[] { @"C:\input-a.png" }, 0)));
        Assert.True(server.HandleDrop(new ShellDropPayload(new[] { @"C:\input-b.png" }, 0)));
        Assert.Equal(2, handled);
        Assert.Equal(1, references.Current);
        Assert.Equal(0, references.ReleaseCalls);

        target.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);
        Assert.Equal(1, references.ReleaseCalls);
    }

    [Fact]
    public void L3_MultipleTargetsNotifyOnlyWhenTheFinalLeaseIsReleased()
    {
        var references = new FakeServerReferences();
        var server = references.CreateServer();
        var releasedEvents = 0;
        server.ServerReferencesReleased += (_, _) => releasedEvents++;
        var first = Assert.IsType<NanoPicDropTarget>(server.CreateDropTarget());
        var second = Assert.IsType<NanoPicDropTarget>(server.CreateDropTarget());
        first.AddConnection(ShellComNative.ExternalConnectionStrong, 0);
        second.AddConnection(ShellComNative.ExternalConnectionStrong, 0);

        first.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);
        Assert.Equal(1, references.Current);
        Assert.Equal(0, releasedEvents);

        second.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);
        Assert.Equal(0, references.Current);
        Assert.Equal(1, releasedEvents);
    }

    [Fact]
    public void L4_RepeatedExternalReleaseCannotReleaseTheLeaseTwice()
    {
        var references = new FakeServerReferences();
        var server = references.CreateServer();
        var target = Assert.IsType<NanoPicDropTarget>(server.CreateDropTarget());
        target.AddConnection(ShellComNative.ExternalConnectionStrong, 0);

        target.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);
        target.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);

        Assert.Equal(1, references.AddCalls);
        Assert.Equal(1, references.ReleaseCalls);
        Assert.Equal(1, references.DisconnectCalls);
        Assert.Equal(0, references.Current);
    }

    [Fact]
    public void L5_ClassFactoryLocksUseTheSameBalancedLifetime()
    {
        var references = new FakeServerReferences();
        var lifetime = references.CreateLifetime();
        var factory = new NanoPicDropTargetFactory(
            () => throw new InvalidOperationException("The factory is not activated in this test."),
            lifetime,
            log: null);

        Assert.Equal(ShellComNative.SOk, factory.LockServer(lockServer: true));
        Assert.Equal(1, references.Current);
        Assert.Equal(ShellComNative.SOk, factory.LockServer(lockServer: false));

        Assert.Equal(0, references.Current);
        Assert.Equal(1, references.ZeroNotifications);
    }

    [Fact]
    public void L6_ComCallableWrapperExposesExternalConnection()
    {
        var references = new FakeServerReferences();
        var server = references.CreateServer();
        var target = Assert.IsType<NanoPicDropTarget>(server.CreateDropTarget());
        target.AddConnection(ShellComNative.ExternalConnectionStrong, 0);
        var unknown = Marshal.GetIUnknownForObject(target);
        var interfaceId = typeof(IExternalConnection).GUID;
        IntPtr externalConnection = IntPtr.Zero;

        try
        {
            Assert.Equal(ShellComNative.SOk, Marshal.QueryInterface(unknown, ref interfaceId, out externalConnection));
            Assert.NotEqual(IntPtr.Zero, externalConnection);
        }
        finally
        {
            if (externalConnection != IntPtr.Zero)
            {
                Marshal.Release(externalConnection);
            }

            Marshal.Release(unknown);
            target.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);
        }
    }
}
