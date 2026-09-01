using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NanoPic.Infrastructure;
using Xunit;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NanoPic.IntegrationTests;

public sealed class ShellDropDataExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nanopic-hdrop-" + Guid.NewGuid().ToString("N"));

    public ShellDropDataExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public int FileListOffset;
        public int X;
        public int Y;
        public int IsNonClientArea;
        public int IsWideCharacter;
    }

    /// <summary>构造真实的 CF_HDROP 内存块，让测试走与 Explorer 相同的提取路径。</summary>
    private static IntPtr CreateHDrop(IEnumerable<string> paths)
    {
        var list = new StringBuilder();
        foreach (var path in paths)
        {
            list.Append(path).Append('\0');
        }

        list.Append('\0');

        var headerSize = Marshal.SizeOf<DropFiles>();
        var listBytes = Encoding.Unicode.GetBytes(list.ToString());
        var handle = Marshal.AllocHGlobal(headerSize + listBytes.Length);
        var header = new DropFiles
        {
            FileListOffset = headerSize,
            IsWideCharacter = 1
        };
        Marshal.StructureToPtr(header, handle, fDeleteOld: false);
        Marshal.Copy(listBytes, 0, handle + headerSize, listBytes.Length);
        return handle;
    }

    private sealed class FakeShellDataObject : ComTypes.IDataObject
    {
        private readonly Func<IntPtr>? _createHDrop;

        public FakeShellDataObject(Func<IntPtr>? createHDrop) => _createHDrop = createHDrop;

        public int GetDataCalls { get; private set; }

        public int QueryGetData(ref ComTypes.FORMATETC format) =>
            _createHDrop is not null && format.cfFormat == 15 && format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL)
                ? 0
                : 1;

        public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
        {
            GetDataCalls++;
            if (_createHDrop is null)
            {
                medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_NULL, unionmember = IntPtr.Zero };
                return;
            }

            medium = new ComTypes.STGMEDIUM
            {
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
                unionmember = _createHDrop(),
                pUnkForRelease = null
            };
        }

        public int DAdvise(ref ComTypes.FORMATETC format, ComTypes.ADVF advf, ComTypes.IAdviseSink sink, out int connection) =>
            throw new NotSupportedException();

        public void DUnadvise(int connection) => throw new NotSupportedException();

        public int EnumDAdvise(out ComTypes.IEnumSTATDATA enumAdvise) => throw new NotSupportedException();

        public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction) => throw new NotSupportedException();

        public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut) =>
            throw new NotSupportedException();

        public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) =>
            throw new NotSupportedException();

        public void SetData(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium, bool release) =>
            throw new NotSupportedException();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    [Fact]
    public void C1_SingleAndMultiSelectionPathsAreExtractedInOrder()
    {
        var files = new[]
        {
            CreateFile("a.png"),
            CreateFile("图片 与 符号 & (1).jpg"),
            CreateFile("brackets [x].webp")
        };
        var dataObject = new FakeShellDataObject(() => CreateHDrop(files));

        var payload = ShellDropDataExtractor.Extract(dataObject);

        Assert.Equal(files, payload.Paths);
        Assert.Equal(0, payload.UnavailableItemCount);
        Assert.Equal(1, dataObject.GetDataCalls);
    }

    [Fact]
    public void C2_LongPathsSurviveExtraction()
    {
        var deep = Path.Combine(_root, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(deep);
        var longFile = Path.Combine(deep, "long-name-" + new string('f', 40) + ".png");
        File.WriteAllBytes(longFile, new byte[] { 9 });
        Assert.True(longFile.Length > 260);

        var payload = ShellDropDataExtractor.Extract(new FakeShellDataObject(() => CreateHDrop(new[] { longFile })));

        Assert.Equal(longFile, Assert.Single(payload.Paths));
    }

    [Fact]
    public void C3_DirectoriesAreCountedAsUnavailableInsteadOfImported()
    {
        var directory = Path.Combine(_root, "folder");
        Directory.CreateDirectory(directory);
        var file = CreateFile("kept.png");

        var payload = ShellDropDataExtractor.Extract(new FakeShellDataObject(() => CreateHDrop(new[] { directory, file })));

        Assert.Equal(new[] { file }, payload.Paths);
        Assert.Equal(1, payload.UnavailableItemCount);
    }

    [Fact]
    public void C4_UnsupportedDataFormatReturnsEmptyWithoutTouchingTheMedium()
    {
        var dataObject = new FakeShellDataObject(null);

        var payload = ShellDropDataExtractor.Extract(dataObject);

        Assert.False(payload.HasPaths);
        Assert.Equal(0, payload.UnavailableItemCount);
        Assert.Equal(0, dataObject.GetDataCalls);
    }

    [Fact]
    public void C5_NullDataObjectIsRejectedSafely()
    {
        var payload = ShellDropDataExtractor.Extract(null);
        Assert.Same(ShellDropPayload.Empty, payload);
    }

    [Fact]
    public void C6_EmptyDropListYieldsNoPaths()
    {
        var payload = ShellDropDataExtractor.Extract(new FakeShellDataObject(() => CreateHDrop(Enumerable.Empty<string>())));
        Assert.False(payload.HasPaths);
    }

    [Fact]
    public void C7_DropWithOnlyUnavailableItemsIsRejectedWithoutCallingTheHandler()
    {
        var directory = Path.Combine(_root, "folder-only");
        Directory.CreateDirectory(directory);
        var handled = 0;
        var target = new NanoPicDropTarget(
            _ =>
            {
                handled++;
                return true;
            },
            log: null,
            new NoopDisposable(),
            _ => { });
        target.AddConnection(ShellComNative.ExternalConnectionStrong, 0);
        var effect = ShellComNative.DropEffectCopy;

        try
        {
            var result = target.Drop(
                new FakeShellDataObject(() => CreateHDrop(new[] { directory })),
                0,
                default,
                ref effect);

            Assert.Equal(ShellComNative.EFail, result);
            Assert.Equal(ShellComNative.DropEffectNone, effect);
            Assert.Equal(0, handled);
        }
        finally
        {
            target.ReleaseConnection(ShellComNative.ExternalConnectionStrong, 0, lastReleaseCloses: true);
        }
    }
}
