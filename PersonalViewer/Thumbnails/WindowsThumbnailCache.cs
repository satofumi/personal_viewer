using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PersonalViewer.Projects;

namespace PersonalViewer.Thumbnails;

public sealed class WindowsThumbnailCache
{
    private const int ThumbnailSize = 96;
    private static readonly Guid ShellItemImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalViewer",
        "Cache",
        "Thumbnails");
    private readonly SemaphoreSlim _generationSemaphore = new(2, 2);

    public async Task<byte[]?> GetThumbnailAsync(IndexedFile file, CancellationToken cancellationToken)
    {
        await _generationSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => GetThumbnail(file, cancellationToken), cancellationToken);
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    private byte[]? GetThumbnail(IndexedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cachePath = GetCachePath(file);
        if (File.Exists(cachePath))
        {
            try
            {
                return LoadCachedThumbnail(cachePath);
            }
            catch (Exception)
            {
                TryDelete(cachePath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var thumbnail = GetWindowsThumbnail(file.Path);
        if (thumbnail is null)
        {
            return null;
        }

        var encodedThumbnail = EncodeThumbnail(thumbnail);
        cancellationToken.ThrowIfCancellationRequested();
        TrySaveThumbnail(cachePath, encodedThumbnail, cancellationToken);
        return encodedThumbnail;
    }

    private string GetCachePath(IndexedFile file)
    {
        var identity = string.Join(
            "\n",
            Path.GetFullPath(file.Path).ToUpperInvariant(),
            ThumbnailSize,
            file.SizeBytes,
            file.LastModifiedUtc.Ticks);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_cacheDirectory, $"{hash}.png");
    }

    private static byte[] LoadCachedThumbnail(string cachePath)
    {
        var data = File.ReadAllBytes(cachePath);
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The thumbnail cache contains no image frames.");
        }

        return data;
    }

    private static byte[] EncodeThumbnail(BitmapSource thumbnail)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(thumbnail));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource? GetWindowsThumbnail(string filePath)
    {
        var interfaceId = ShellItemImageFactoryId;
        var result = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref interfaceId, out var imageFactory);
        if (result < 0)
        {
            return null;
        }

        try
        {
            var size = new NativeSize(ThumbnailSize, ThumbnailSize);
            var flags = ShellItemImageFlags.ThumbnailOnly | ShellItemImageFlags.ScaleUp;
            var imageResult = imageFactory.GetImage(size, flags, out var bitmapHandle);
            if (imageResult < 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmapHandle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                DeleteObject(bitmapHandle);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(imageFactory);
        }
    }

    private void TrySaveThumbnail(string cachePath, byte[] thumbnailData, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllBytes(temporaryPath, thumbnailData);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;

        public readonly int Height = height;
    }

    [Flags]
    private enum ShellItemImageFlags : uint
    {
        ThumbnailOnly = 0x08,
        ScaleUp = 0x100
    }
}
