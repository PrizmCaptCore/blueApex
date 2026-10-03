using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlueApex.Drawer;

/// <summary>Shell icons for desktop items (shortcuts, folders, Recycle Bin...), cached per id.</summary>
internal sealed class IconLoader
{
    private readonly Dictionary<string, ImageSource?> _cache = new();
    private readonly object _gate = new();
    // Thumbnails are decoded on worker threads, a few at a time, so a folder full of
    // photos neither freezes the drawer nor saturates the CPU.
    private readonly SemaphoreSlim _thumbnailSlots = new(2);

    /// <param name="thumbnail">Prefer a thumbnail (for pictures, videos) over the file-type icon.</param>
    public ImageSource? Get(string id, int sizePx, bool thumbnail = false)
    {
        var key = Key(id, thumbnail);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }
        var image = Load(id, sizePx, thumbnail);
        lock (_gate) _cache[key] = image;
        return image;
    }

    /// <summary>
    /// Like <see cref="Get"/> but off the UI thread: returns the cached image at once if
    /// there is one, otherwise null and later calls <paramref name="ready"/> on the
    /// dispatcher with the decoded image (never for a failed decode).
    /// </summary>
    public ImageSource? GetAsync(string id, int sizePx, bool thumbnail, System.Windows.Threading.Dispatcher dispatcher, Action<ImageSource> ready)
    {
        var key = Key(id, thumbnail);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }
        _ = Task.Run(async () =>
        {
            await _thumbnailSlots.WaitAsync();
            try
            {
                var image = Load(id, sizePx, thumbnail); // frozen, so it may cross threads
                lock (_gate) _cache[key] = image;
                if (image != null) await dispatcher.BeginInvoke(() => ready(image));
            }
            finally
            {
                _thumbnailSlots.Release();
            }
        });
        return null;
    }

    private static string Key(string id, bool thumbnail) => thumbnail ? id + "\n#thumb" : id;

    private static ImageSource? Load(string id, int sizePx, bool thumbnail)
    {
        if (BlueApex.Games.GameCatalog.IsGameId(id)) return LoadGame(id, sizePx);
        var hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var parsingName = BlueApex.Desktop.AppCatalog.IsAppId(id) ? BlueApex.Desktop.AppCatalog.ParsingName(id) : id;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) < 0)
                return null;
            var size = new SIZE { cx = sizePx, cy = sizePx };
            // Without ICONONLY the shell returns a thumbnail when it has one and the icon otherwise.
            var flags = thumbnail ? SIIGBF_BIGGERSIZEOK : SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK;
            if (factory.GetImage(size, flags, out hbitmap) < 0)
                return null;
            return FromHBitmap(hbitmap);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
        }
    }

    // A game's tile: the launcher's cached picture, else one downloaded from its CDN,
    // else the executable's icon. Pictures are centre-cropped to a square so Steam
    // headers and Epic box art sit in the tile like icons do.
    private static ImageSource? LoadGame(string id, int sizePx)
    {
        var game = BlueApex.Games.GameCatalog.Find(id);
        if (game == null) return null;
        var file = game.ImageFile ?? (game.ImageUrl != null ? BlueApex.Games.ImageCache.Fetch(game.ImageUrl) : null);
        if (file != null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(file);
                image.DecodePixelHeight = sizePx * 2; // enough for a 2x screen, small in memory
                image.EndInit();
                var side = Math.Min(image.PixelWidth, image.PixelHeight);
                ImageSource result = image.PixelWidth == image.PixelHeight
                    ? image
                    : new CroppedBitmap(image, new System.Windows.Int32Rect((image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
                result.Freeze();
                return result;
            }
            catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or ArgumentException)
            {
                // a broken cache file; fall through to the exe icon
            }
        }
        return game.Exe != null && System.IO.File.Exists(game.Exe) ? Load(game.Exe, sizePx, false) : null;
    }

    // The shell hands back a 32-bit DIB with premultiplied alpha. WPF's
    // CreateBitmapSourceFromHBitmap drops the alpha, so the pixels are copied directly.
    private static BitmapSource FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32)
        {
            var plain = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero,
                System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            plain.Freeze();
            return plain;
        }

        // DIB sections are stored bottom-up, so the rows are copied in reverse.
        var stride = bm.bmWidthBytes;
        var pixels = new byte[stride * bm.bmHeight];
        for (var row = 0; row < bm.bmHeight; row++)
            Marshal.Copy(bm.bmBits + (bm.bmHeight - 1 - row) * stride, pixels, row * stride, stride);
        var source = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
