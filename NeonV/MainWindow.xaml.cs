using Microsoft.Win32;
using QRCoder;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;

namespace NeonV;

public static class Direct2DEmojiRenderer
{
    private const int CanvasSize = 64;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint WICBitmapCacheOnLoad = 2;
    private const uint D2D1_FACTORY_TYPE_MULTI_THREADED = 1;
    private const uint D2D1_RENDER_TARGET_TYPE_DEFAULT = 0;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    private const uint D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 4;
    private const uint DWRITE_FACTORY_TYPE_SHARED = 0;
    private const uint DWRITE_FONT_WEIGHT_NORMAL = 400;
    private const uint DWRITE_FONT_STYLE_NORMAL = 0;
    private const uint DWRITE_FONT_STRETCH_NORMAL = 5;
    private const uint DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    private const uint DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;
    private const uint DWRITE_MEASURING_MODE_NATURAL = 0;

    private static Guid CLSID_WICImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static Guid IID_IWICImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static Guid GUID_WICPixelFormat32bppPBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    private static Guid IID_ID2D1Factory = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_PIXEL_FORMAT
    {
        public uint format;
        public uint alphaMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public uint type;
        public D2D1_PIXEL_FORMAT pixelFormat;
        public float dpiX;
        public float dpiY;
        public uint usage;
        public uint minLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_COLOR_F
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_RECT_F
    {
        public float left;
        public float top;
        public float right;
        public float bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WicCreateBitmapDelegate(IntPtr self, uint width, uint height, ref Guid pixelFormat, uint option, out IntPtr bitmap);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WicCopyPixelsDelegate(IntPtr self, IntPtr prc, uint cbStride, uint cbBufferSize, [Out] byte[] pbBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D2DCreateWicBitmapRenderTargetDelegate(IntPtr self, IntPtr target, ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties, out IntPtr renderTarget);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D2DCreateSolidColorBrushDelegate(IntPtr self, ref D2D1_COLOR_F color, IntPtr brushProperties, out IntPtr solidColorBrush);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D2DBeginDrawDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D2DClearDelegate(IntPtr self, ref D2D1_COLOR_F clearColor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D2DDrawTextDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string str, uint stringLength, IntPtr textFormat, ref D2D1_RECT_F layoutRect, IntPtr defaultForegroundBrush, uint options, uint measuringMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D2DEndDrawDelegate(IntPtr self, IntPtr tag1, IntPtr tag2);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DWriteCreateTextFormatDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string fontFamilyName, IntPtr fontCollection, uint fontWeight, uint fontStyle, uint fontStretch, float fontSize, [MarshalAs(UnmanagedType.LPWStr)] string localeName, out IntPtr textFormat);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DWriteSetAlignmentDelegate(IntPtr self, uint alignment);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(uint factoryType, ref Guid riid, IntPtr pFactoryOptions, out IntPtr ppIFactory);

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(uint factoryType, ref Guid iid, out IntPtr factory);

    private static readonly object _renderLock = new();
    private static readonly ConcurrentDictionary<string, BitmapSource?> _cache = new();

    private static IntPtr _wicFactory;
    private static IntPtr _wicBitmap;
    private static IntPtr _d2dFactory;
    private static IntPtr _renderTarget;
    private static IntPtr _sentinelBrush;
    private static IntPtr _dwriteFactory;
    private static IntPtr _textFormat;

    private static WicCopyPixelsDelegate? _wicCopyPixels;
    private static D2DBeginDrawDelegate? _beginDraw;
    private static D2DClearDelegate? _clear;
    private static D2DDrawTextDelegate? _drawText;
    private static D2DEndDrawDelegate? _endDraw;

    private static T GetVtableMethod<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr methodPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    private static bool EnsureInitialized()
    {
        if (_renderTarget != IntPtr.Zero && _textFormat != IntPtr.Zero && _sentinelBrush != IntPtr.Zero)
            return true;

        try
        {
            if (_wicFactory == IntPtr.Zero)
            {
                if (CoCreateInstance(ref CLSID_WICImagingFactory, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref IID_IWICImagingFactory, out _wicFactory) < 0)
                    return false;
            }

            if (_wicBitmap == IntPtr.Zero)
            {
                var createBitmap = GetVtableMethod<WicCreateBitmapDelegate>(_wicFactory, 17);
                if (createBitmap(_wicFactory, CanvasSize, CanvasSize, ref GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, out _wicBitmap) < 0)
                    return false;
                _wicCopyPixels = GetVtableMethod<WicCopyPixelsDelegate>(_wicBitmap, 7);
            }

            if (_d2dFactory == IntPtr.Zero)
            {
                if (D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, ref IID_ID2D1Factory, IntPtr.Zero, out _d2dFactory) < 0)
                    return false;
            }

            if (_renderTarget == IntPtr.Zero)
            {
                var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
                {
                    type = D2D1_RENDER_TARGET_TYPE_DEFAULT,
                    pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED },
                    dpiX = 96.0f,
                    dpiY = 96.0f,
                    usage = 0,
                    minLevel = 0
                };
                var createRt = GetVtableMethod<D2DCreateWicBitmapRenderTargetDelegate>(_d2dFactory, 13);
                if (createRt(_d2dFactory, _wicBitmap, ref rtProps, out _renderTarget) < 0)
                    return false;

                _beginDraw = GetVtableMethod<D2DBeginDrawDelegate>(_renderTarget, 48);
                _clear = GetVtableMethod<D2DClearDelegate>(_renderTarget, 47);
                _drawText = GetVtableMethod<D2DDrawTextDelegate>(_renderTarget, 27);
                _endDraw = GetVtableMethod<D2DEndDrawDelegate>(_renderTarget, 49);
            }

            if (_sentinelBrush == IntPtr.Zero)
            {
                var sentinelColor = new D2D1_COLOR_F { r = 254f / 255f, g = 1f / 255f, b = 254f / 255f, a = 1.0f };
                var createBrush = GetVtableMethod<D2DCreateSolidColorBrushDelegate>(_renderTarget, 8);
                if (createBrush(_renderTarget, ref sentinelColor, IntPtr.Zero, out _sentinelBrush) < 0)
                    return false;
            }

            if (_dwriteFactory == IntPtr.Zero)
            {
                if (DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, ref IID_IDWriteFactory, out _dwriteFactory) < 0)
                    return false;
            }

            if (_textFormat == IntPtr.Zero)
            {
                var createTextFormat = GetVtableMethod<DWriteCreateTextFormatDelegate>(_dwriteFactory, 15);
                if (createTextFormat(_dwriteFactory, "Segoe UI Emoji", IntPtr.Zero, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 42.0f, "", out _textFormat) < 0)
                    return false;

                GetVtableMethod<DWriteSetAlignmentDelegate>(_textFormat, 3)(_textFormat, DWRITE_TEXT_ALIGNMENT_CENTER);
                GetVtableMethod<DWriteSetAlignmentDelegate>(_textFormat, 4)(_textFormat, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ResetRenderTarget()
    {
        if (_sentinelBrush != IntPtr.Zero) { Marshal.Release(_sentinelBrush); _sentinelBrush = IntPtr.Zero; }
        if (_renderTarget != IntPtr.Zero) { Marshal.Release(_renderTarget); _renderTarget = IntPtr.Zero; }
        _beginDraw = null;
        _clear = null;
        _drawText = null;
        _endDraw = null;
    }

    public static BitmapSource? GetEmojiImage(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return null;
        return _cache.GetOrAdd(emoji, RenderEmojiInternal);
    }

    private static BitmapSource? RenderEmojiInternal(string emoji)
    {
        lock (_renderLock)
        {
            try
            {
                if (!EnsureInitialized()) return null;

                var clearColor = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 0 };
                var rect = new D2D1_RECT_F { left = 0, top = 0, right = CanvasSize, bottom = CanvasSize };

                _beginDraw!(_renderTarget);
                _clear!(_renderTarget, ref clearColor);
                _drawText!(_renderTarget, emoji, (uint)emoji.Length, _textFormat, ref rect, _sentinelBrush, D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT, DWRITE_MEASURING_MODE_NATURAL);
                int hr = _endDraw!(_renderTarget, IntPtr.Zero, IntPtr.Zero);
                if (hr < 0)
                {
                    ResetRenderTarget();
                    return null;
                }

                byte[] pixels = new byte[CanvasSize * CanvasSize * 4];
                if (_wicCopyPixels!(_wicBitmap, IntPtr.Zero, CanvasSize * 4, (uint)pixels.Length, pixels) < 0)
                    return null;

                int minX = CanvasSize, minY = CanvasSize, maxX = -1, maxY = -1;
                bool hasColorData = false;

                for (int y = 0; y < CanvasSize; y++)
                {
                    int rowOffset = y * CanvasSize * 4;
                    for (int x = 0; x < CanvasSize; x++)
                    {
                        int idx = rowOffset + x * 4;
                        byte b = pixels[idx];
                        byte g = pixels[idx + 1];
                        byte r = pixels[idx + 2];
                        byte a = pixels[idx + 3];

                        if (a > 4)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;

                            if (!hasColorData && a > 30)
                            {
                                if (g > 4 || Math.Abs(r - b) > 6 || r < a * 0.5)
                                    hasColorData = true;
                            }
                        }
                    }
                }

                if (minX > maxX || minY > maxY || !hasColorData)
                    return null;

                int w = maxX - minX + 1;
                int h = maxY - minY + 1;
                int size = Math.Min(CanvasSize, Math.Max(w, h) + 4);
                int startX = Math.Clamp(minX - (size - w) / 2, 0, CanvasSize - size);
                int startY = Math.Clamp(minY - (size - h) / 2, 0, CanvasSize - size);

                byte[] cropped = new byte[size * size * 4];
                int stride = size * 4;
                for (int y = 0; y < size; y++)
                {
                    Buffer.BlockCopy(pixels, ((startY + y) * CanvasSize + startX) * 4, cropped, y * stride, stride);
                }

                var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, cropped, stride);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void Dispose()
    {
        lock (_renderLock)
        {
            ResetRenderTarget();
            if (_textFormat != IntPtr.Zero) { Marshal.Release(_textFormat); _textFormat = IntPtr.Zero; }
            if (_dwriteFactory != IntPtr.Zero) { Marshal.Release(_dwriteFactory); _dwriteFactory = IntPtr.Zero; }
            if (_d2dFactory != IntPtr.Zero) { Marshal.Release(_d2dFactory); _d2dFactory = IntPtr.Zero; }
            if (_wicBitmap != IntPtr.Zero) { Marshal.Release(_wicBitmap); _wicBitmap = IntPtr.Zero; }
            if (_wicFactory != IntPtr.Zero) { Marshal.Release(_wicFactory); _wicFactory = IntPtr.Zero; }
            _wicCopyPixels = null;
        }
    }
}

public static class EmojiText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(EmojiText),
        new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    private static bool IsEmojiCodePoint(int cp) =>
        cp is (>= 0x1F000 and <= 0x1FAFF)
            or (>= 0x2600 and <= 0x27BF)
            or (>= 0x2300 and <= 0x23FF)
            or 0x2B50 or 0x2B55 or 0x2934 or 0x2935 or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x203C or 0x2049 or 0x2122 or 0x2139 or (>= 0x2194 and <= 0x21AA) or 0x24C2 or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or (>= 0x25FB and <= 0x25FE);

    private static int SafeGetCodePoint(string s, int index)
    {
        if (index < 0 || index >= s.Length) return 0;
        if (char.IsHighSurrogate(s[index]))
        {
            if (index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]))
                return char.ConvertToUtf32(s, index);
            return s[index];
        }
        return s[index];
    }

    private static bool IsCandidateCluster(string cluster)
    {
        if (string.IsNullOrEmpty(cluster)) return false;
        if (cluster.Contains('\uFE0F') || cluster.Contains('\u20E3') || cluster.Contains('\u200D'))
            return true;
        return IsEmojiCodePoint(SafeGetCodePoint(cluster, 0));
    }

    private static bool HasAnyCandidate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (IsEmojiCodePoint(char.ConvertToUtf32(text, i))) return true;
                    i++;
                }
                continue;
            }
            if (c is '\uFE0F' or '\u20E3' or '\u200D' || IsEmojiCodePoint(c))
                return true;
        }
        return false;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        string text = e.NewValue as string ?? "";
        tb.Inlines.Clear();
        tb.Text = string.Empty;

        if (string.IsNullOrEmpty(text))
            return;

        if (!HasAnyCandidate(text))
        {
            tb.Text = text;
            return;
        }

        double emojiSize = Math.Ceiling((double.IsNaN(tb.FontSize) || tb.FontSize <= 0 ? 14 : tb.FontSize) * 1.15);
        var plainSb = new System.Text.StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            string cluster = enumerator.GetTextElement();
            if (IsCandidateCluster(cluster))
            {
                var bmp = Direct2DEmojiRenderer.GetEmojiImage(cluster);
                if (bmp != null)
                {
                    if (plainSb.Length > 0)
                    {
                        tb.Inlines.Add(new Run(plainSb.ToString()));
                        plainSb.Clear();
                    }

                    var img = new System.Windows.Controls.Image
                    {
                        Source = bmp,
                        Width = emojiSize,
                        Height = emojiSize,
                        Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 1, 0)
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    tb.Inlines.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
                    continue;
                }
            }
            plainSb.Append(cluster);
        }

        if (plainSb.Length > 0)
            tb.Inlines.Add(new Run(plainSb.ToString()));
    }
}

public class RoundedRectClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is double w && values[1] is double h && w > 0 && h > 0)
        {
            double r = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var pr) ? pr : 13;
            var geo = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            geo.Freeze();
            return geo;
        }
        return Geometry.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class JobObjectManager
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private static IntPtr _jobHandle;

    public static void AddProcess(IntPtr handle)
    {
        if (_jobHandle == IntPtr.Zero)
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 }
            };
            Marshal.StructureToPtr(info, extendedInfoPtr, false);
            SetInformationJobObject(_jobHandle, 9, extendedInfoPtr, (uint)length);
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
        AssignProcessToJobObject(_jobHandle, handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsage;
        public UIntPtr PeakJobMemoryUsage;
    }
}

public static class SystemProxyManager
{
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    public static void EnableProxy(string proxyAddress)
    {
        try
        {
            using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            registry?.SetValue("ProxyEnable", 1);
            registry?.SetValue("ProxyServer", proxyAddress);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { }
    }

    public static void DisableProxy()
    {
        try
        {
            using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            registry?.SetValue("ProxyEnable", 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { }
    }
}

public static class CountryFlagHelper
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, BitmapImage> _flagCache = new();
    private static readonly ConcurrentDictionary<string, Task> _downloadTasks = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<VpnProfile, byte>> _waitingProfiles = new();

    private static readonly ConcurrentDictionary<string, string> _geoIpCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<string>> _geoIpTasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _geoIpSemaphore = new(3, 3);

    private static readonly HashSet<string> _isoCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad","ae","af","ag","ai","al","am","ao","aq","ar","as","at","au","aw","ax","az",
        "ba","bb","bd","be","bf","bg","bh","bi","bj","bl","bm","bn","bo","bq","br","bs","bt","bv","bw","by","bz",
        "ca","cc","cd","cf","cg","ch","ci","ck","cl","cm","cn","co","cr","cu","cv","cw","cx","cy","cz",
        "de","dj","dk","dm","do","dz",
        "ec","ee","eg","eh","er","es","et",
        "fi","fj","fk","fm","fo","fr",
        "ga","gb","gd","ge","gf","gg","gh","gi","gl","gm","gn","gp","gq","gr","gs","gt","gu","gw","gy",
        "hk","hm","hn","hr","ht","hu",
        "id","ie","il","im","in","io","iq","ir","is","it",
        "je","jm","jo","jp",
        "ke","kg","kh","ki","km","kn","kp","kr","kw","ky","kz",
        "la","lb","lc","li","lk","lr","ls","lt","lu","lv","ly",
        "ma","mc","md","me","mf","mg","mh","mk","ml","mm","mn","mo","mp","mq","mr","ms","mt","mu","mv","mw","mx","my","mz",
        "na","nc","ne","nf","ng","ni","nl","no","np","nr","nu","nz",
        "om",
        "pa","pe","pf","pg","ph","pk","pl","pm","pn","pr","ps","pt","pw","py",
        "qa",
        "re","ro","rs","ru","rw",
        "sa","sb","sc","sd","se","sg","sh","si","sj","sk","sl","sm","sn","so","sr","ss","st","sv","sx","sy","sz",
        "tc","td","tf","tg","th","tj","tk","tl","tm","tn","to","tr","tt","tv","tw","tz",
        "ua","ug","um","us","uy","uz",
        "va","vc","ve","vg","vi","vn","vu",
        "wf","ws",
        "ye","yt",
        "za","zm","zw"
    };

    private static readonly HashSet<string> _ambiguousTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co","io","me","tv","cc","ai","gg","ly","to","sh","fm","ws","am","is","it","in","so","by","do","go","id","my","no","as","at","re","st","la","vc","ag","ms","tk","ml","ga","cf","gq","im","nu","bz"
    };

    private static readonly Dictionary<string, string> _nameToCountryCode = new(StringComparer.OrdinalIgnoreCase)
    {
        { "germany", "de" }, { "германия", "de" }, { "deutschland", "de" }, { "frankfurt", "de" }, { "франкфурт", "de" },
        { "estonia", "ee" }, { "эстония", "ee" }, { "tallinn", "ee" }, { "таллин", "ee" },
        { "finland", "fi" }, { "финляндия", "fi" }, { "helsinki", "fi" }, { "хельсинки", "fi" },
        { "netherlands", "nl" }, { "нидерланды", "nl" }, { "holland", "nl" }, { "голландия", "nl" }, { "amsterdam", "nl" }, { "амстердам", "nl" },
        { "russia", "ru" }, { "россия", "ru" }, { "moscow", "ru" }, { "москва", "ru" },
        { "united states", "us" }, { "america", "us" }, { "сша", "us" }, { "америка", "us" }, { "usa", "us" },
        { "poland", "pl" }, { "польша", "pl" }, { "warsaw", "pl" }, { "варшава", "pl" },
        { "turkey", "tr" }, { "турция", "tr" }, { "istanbul", "tr" }, { "стамбул", "tr" },
        { "sweden", "se" }, { "швеция", "se" }, { "stockholm", "se" }, { "стокгольм", "se" },
        { "france", "fr" }, { "франция", "fr" }, { "paris", "fr" }, { "париж", "fr" },
        { "japan", "jp" }, { "япония", "jp" }, { "tokyo", "jp" }, { "токио", "jp" },
        { "singapore", "sg" }, { "сингапур", "sg" },
        { "ukraine", "ua" }, { "украина", "ua" }, { "kyiv", "ua" }, { "киев", "ua" },
        { "great britain", "gb" }, { "united kingdom", "gb" }, { "великобритания", "gb" }, { "england", "gb" }, { "англия", "gb" }, { "london", "gb" }, { "лондон", "gb" }, { "uk", "gb" },
        { "canada", "ca" }, { "канада", "ca" },
        { "kazakhstan", "kz" }, { "казахстан", "kz" }, { "almaty", "kz" }, { "алматы", "kz" },
        { "iceland", "is" }, { "исландия", "is" },
        { "latvia", "lv" }, { "латвия", "lv" }, { "riga", "lv" }, { "рига", "lv" },
        { "lithuania", "lt" }, { "литва", "lt" }, { "vilnius", "lt" }, { "вильнюс", "lt" },
        { "austria", "at" }, { "австрия", "at" }, { "vienna", "at" }, { "вена", "at" },
        { "switzerland", "ch" }, { "швейцария", "ch" }, { "zurich", "ch" }, { "цюрих", "ch" },
        { "hong kong", "hk" }, { "гонконг", "hk" },
        { "norway", "no" }, { "норвегия", "no" },
        { "denmark", "dk" }, { "дания", "dk" },
        { "italy", "it" }, { "италия", "it" },
        { "spain", "es" }, { "испания", "es" },
        { "czech", "cz" }, { "чехия", "cz" },
        { "romania", "ro" }, { "румыния", "ro" },
        { "bulgaria", "bg" }, { "болгария", "bg" },
        { "moldova", "md" }, { "молдова", "md" },
        { "belarus", "by" }, { "беларусь", "by" },
        { "georgia", "ge" }, { "грузия", "ge" },
        { "armenia", "am" }, { "армения", "am" },
        { "azerbaijan", "az" }, { "азербайджан", "az" },
        { "uzbekistan", "uz" }, { "узбекистан", "uz" },
        { "israel", "il" }, { "израиль", "il" },
        { "india", "in" }, { "индия", "in" },
        { "brazil", "br" }, { "бразилия", "br" },
        { "korea", "kr" }, { "корея", "kr" },
        { "taiwan", "tw" }, { "тайвань", "tw" },
        { "ireland", "ie" }, { "ирландия", "ie" },
        { "portugal", "pt" }, { "португалия", "pt" },
        { "hungary", "hu" }, { "венгрия", "hu" },
        { "serbia", "rs" }, { "сербия", "rs" },
        { "slovakia", "sk" }, { "словакия", "sk" },
        { "slovenia", "si" }, { "словения", "si" },
        { "greece", "gr" }, { "греция", "gr" },
        { "cyprus", "cy" }, { "кипр", "cy" },
        { "dubai", "ae" }, { "дубай", "ae" }, { "оаэ", "ae" }, { "uae", "ae" },
        { "australia", "au" }, { "австралия", "au" },
        { "china", "cn" }, { "китай", "cn" }
    };

    public static string NormalizeCountryCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2) return "";
        code = code.ToLowerInvariant();
        if (code == "uk") code = "gb";
        return _isoCountryCodes.Contains(code) ? code : "";
    }

    public static string StripFlagEmojisAndDuplicateCode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsSurrogatePair(name, i))
            {
                int cp = char.ConvertToUtf32(name, i);
                if (cp is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    i++;
                    continue;
                }
                sb.Append(name[i]);
                sb.Append(name[i + 1]);
                i++;
                continue;
            }

            if (name[i] is '\uFE0F' or '\uFE0E')
            {
                int lastIdx = sb.Length - 1;
                if (lastIdx >= 0 && (char.IsLowSurrogate(sb[lastIdx]) || IsPotentialEmojiChar(sb[lastIdx])))
                {
                    sb.Append(name[i]);
                }
                continue;
            }

            sb.Append(name[i]);
        }

        string cleaned = Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"^([A-Za-z]{2})\s+(?=\[\1\]|\(\1\))", "", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(cleaned) ? name.Trim() : cleaned;
    }

    private static bool IsPotentialEmojiChar(char c) => c >= 0x2000;

    public static string DetectCountryCode(string name, string address)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "";

        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsSurrogatePair(name, i))
            {
                int cp1 = char.ConvertToUtf32(name, i);
                if (cp1 is >= 0x1F1E6 and <= 0x1F1FF && i + 2 < name.Length && char.IsSurrogatePair(name, i + 2))
                {
                    int cp2 = char.ConvertToUtf32(name, i + 2);
                    if (cp2 is >= 0x1F1E6 and <= 0x1F1FF)
                    {
                        string emojiCode = $"{(char)('a' + (cp1 - 0x1F1E6))}{(char)('a' + (cp2 - 0x1F1E6))}";
                        string normalized = NormalizeCountryCode(emojiCode);
                        if (!string.IsNullOrEmpty(normalized)) return normalized;
                    }
                }
                i++;
            }
        }

        var bracketMatch = Regex.Match(name, @"\[([A-Za-z]{2})\d*\]|\(([A-Za-z]{2})\d*\)");
        if (bracketMatch.Success)
        {
            string normalized = NormalizeCountryCode(bracketMatch.Groups[1].Value + bracketMatch.Groups[2].Value);
            if (!string.IsNullOrEmpty(normalized)) return normalized;
        }

        foreach (var (key, code) in _nameToCountryCode)
        {
            if (key.Length <= 3)
            {
                if (Regex.IsMatch(name, $@"(?:^|[^a-zA-Zа-яА-ЯёЁ]){Regex.Escape(key)}(?:[^a-zA-Zа-яА-ЯёЁ]|$)", RegexOptions.IgnoreCase))
                    return code;
            }
            else if (name.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        var upperCodeMatch = Regex.Match(name, @"(?:^|[\s|_\-•·/])([A-Z]{2})(?=[\s|_\-•·/\d]|$)");
        if (upperCodeMatch.Success)
        {
            string normalized = NormalizeCountryCode(upperCodeMatch.Groups[1].Value);
            if (!string.IsNullOrEmpty(normalized)) return normalized;
        }

        if (!string.IsNullOrEmpty(address) && !IPAddress.TryParse(address, out _))
        {
            var parts = address.Split('.');
            if (parts.Length > 1)
            {
                string tld = parts[^1].ToLowerInvariant();
                if (tld.Length == 2 && !_ambiguousTlds.Contains(tld))
                {
                    string normalized = NormalizeCountryCode(tld);
                    if (!string.IsNullOrEmpty(normalized)) return normalized;
                }
            }
        }

        return "";
    }

    public static string CountryCodeToEmoji(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 2) return "?";
        code = code.ToUpperInvariant();
        int firstChar = 0x1F1E6 + (code[0] - 'A');
        int secondChar = 0x1F1E6 + (code[1] - 'A');
        return char.ConvertFromUtf32(firstChar) + char.ConvertFromUtf32(secondChar);
    }

    public static BitmapImage? GetFlagImage(string countryCode, VpnProfile? subscriber = null)
    {
        countryCode = NormalizeCountryCode(countryCode);
        if (string.IsNullOrEmpty(countryCode)) return null;

        if (_flagCache.TryGetValue(countryCode, out var cached)) return cached;

        if (subscriber != null)
        {
            var set = _waitingProfiles.GetOrAdd(countryCode, _ => new ConcurrentDictionary<VpnProfile, byte>());
            set.TryAdd(subscriber, 0);
        }

        _downloadTasks.GetOrAdd(countryCode, code => Task.Run(async () =>
        {
            try
            {
                byte[]? bytes = null;
                try
                {
                    bytes = await _httpClient.GetByteArrayAsync($"https://flagcdn.com/w80/{code}.png");
                }
                catch
                {
                    try
                    {
                        bytes = await _httpClient.GetByteArrayAsync($"https://flagcdn.com/w40/{code}.png");
                    }
                    catch { }
                }

                if (bytes != null && bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();

                    _flagCache[code] = img;

                    if (_waitingProfiles.TryRemove(code, out var profiles))
                    {
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            foreach (var p in profiles.Keys) p.NotifyVisuals();
                        });
                    }
                }
            }
            catch { }
            finally
            {
                _downloadTasks.TryRemove(code, out _);
            }
        }));

        return null;
    }

    private static bool IsPrivateOrInvalidIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
        }
        return false;
    }

    public static async Task<string> ResolveCountryCodeByHostAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";

        try
        {
            IPAddress? targetIp;
            if (!IPAddress.TryParse(address, out targetIp))
            {
                var ips = await Dns.GetHostAddressesAsync(address).WaitAsync(TimeSpan.FromSeconds(3));
                targetIp = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? ips.FirstOrDefault();
            }

            if (targetIp == null || IsPrivateOrInvalidIp(targetIp)) return "";

            string ipKey = targetIp.ToString();
            if (_geoIpCache.TryGetValue(ipKey, out var cachedCode)) return cachedCode;

            string resolved = await _geoIpTasks.GetOrAdd(ipKey, async key =>
            {
                await _geoIpSemaphore.WaitAsync();
                try
                {
                    if (_geoIpCache.TryGetValue(key, out var existing)) return existing;

                    string code = "";

                    try
                    {
                        string json = await _httpClient.GetStringAsync($"https://ipinfo.io/{key}/json");
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("country", out var cc))
                            code = NormalizeCountryCode(cc.GetString() ?? "");
                    }
                    catch { }

                    if (string.IsNullOrEmpty(code))
                    {
                        try
                        {
                            string json = await _httpClient.GetStringAsync($"https://ipwho.is/{key}?fields=success,country_code");
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                                doc.RootElement.TryGetProperty("country_code", out var cc))
                                code = NormalizeCountryCode(cc.GetString() ?? "");
                        }
                        catch { }
                    }

                    if (string.IsNullOrEmpty(code))
                    {
                        try
                        {
                            string json = await _httpClient.GetStringAsync($"http://ip-api.com/json/{key}?fields=status,countryCode");
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("status", out var status) &&
                                status.GetString() == "success" &&
                                doc.RootElement.TryGetProperty("countryCode", out var cc))
                                code = NormalizeCountryCode(cc.GetString() ?? "");
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(code))
                    {
                        _geoIpCache[key] = code;
                        return code;
                    }
                    return "";
                }
                catch
                {
                    return "";
                }
                finally
                {
                    _geoIpSemaphore.Release();
                    _geoIpTasks.TryRemove(key, out _);
                }
            });

            return resolved;
        }
        catch
        {
            return "";
        }
    }

    public static void ClearDownloadState()
    {
        _downloadTasks.Clear();
    }
}

public static class PingManager
{
    private static readonly ConcurrentDictionary<string, Task<long>> _tasks = new();

    public static Task<long> GetPingTask(string key, Func<Task<long>> pingAction) =>
        _tasks.GetOrAdd(key, _ => pingAction());

    public static void Clear() => _tasks.Clear();
}

public class Subscription : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? LastUpdated { get; set; }
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public long TotalBytes { get; set; }
    public DateTime? ExpireDate { get; set; }
    public bool IsExpanded { get; set; } = true;

    [JsonIgnore]
    public ObservableCollection<VpnProfile> Profiles { get; } = new();

    [JsonIgnore]
    public string ExpandChevronIcon => IsExpanded ? "\xE70E" : "\xE76C";

    [JsonIgnore]
    public double ChevronAngle => IsExpanded ? 90.0 : 0.0;

    [JsonIgnore]
    public double ExpandedOpacity => IsExpanded ? 1.0 : 0.0;

    [JsonIgnore]
    public double ExpandedScaleY => IsExpanded ? 1.0 : 0.0;

    [JsonIgnore]
    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string TrafficText
    {
        get
        {
            if (TotalBytes > 0)
                return $"{FormatBytes(UploadBytes + DownloadBytes)} / {FormatBytes(TotalBytes)}";
            if (UploadBytes + DownloadBytes > 0)
                return $"{FormatBytes(UploadBytes + DownloadBytes)} / ∞";
            return MainWindow.L("TrafficInfinite");
        }
    }

    [JsonIgnore]
    public string ExpireText => ExpireDate.HasValue
        ? string.Format(MainWindow.L("ExpiresFmt"), ExpireDate.Value.ToString("dd.MM.yyyy"))
        : "";

    [JsonIgnore]
    public string LastUpdatedText => LastUpdated.HasValue
        ? string.Format(MainWindow.L("UpdatedFmt"), LastUpdated.Value.ToString("dd.MM.yyyy HH:mm"))
        : MainWindow.L("NeverUpdated");

    [JsonIgnore] public string TooltipPing => MainWindow.L("TooltipPing");
    [JsonIgnore] public string TooltipUpdate => MainWindow.L("TooltipUpdate");
    [JsonIgnore] public string TooltipDelete => MainWindow.L("TooltipDelete");

    private static string FormatBytes(long bytes) => bytes switch
    {
        > 1073741824 => $"{bytes / 1073741824.0:F1} GB",
        > 1048576 => $"{bytes / 1048576.0:F1} MB",
        > 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void NotifyTexts()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(TrafficText));
        OnPropertyChanged(nameof(ExpireText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(TooltipPing));
        OnPropertyChanged(nameof(TooltipUpdate));
        OnPropertyChanged(nameof(TooltipDelete));
    }

    public void NotifyAll()
    {
        NotifyTexts();
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ExpandChevronIcon));
        OnPropertyChanged(nameof(ChevronAngle));
        OnPropertyChanged(nameof(ExpandedOpacity));
        OnPropertyChanged(nameof(ExpandedScaleY));
        OnPropertyChanged(nameof(ExpandedVisibility));
    }
}

public class VpnProfile : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "vless";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Uuid { get; set; } = "";
    public string Password { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Fp { get; set; } = "";
    public string Alpn { get; set; } = "";
    public string Pbk { get; set; } = "";
    public string Sid { get; set; } = "";
    public string Flow { get; set; } = "";
    public int AlterId { get; set; } = 0;
    public string Security { get; set; } = "auto";
    public string Transport { get; set; } = "tcp";
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";
    public string Method { get; set; } = "chacha20-ietf-poly1305";
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string LocalAddress { get; set; } = "10.0.0.2/32";
    public int Mtu { get; set; } = 1420;
    public string Up { get; set; } = "";
    public string Down { get; set; } = "";
    public string Obfs { get; set; } = "";
    public string ObfsPassword { get; set; } = "";
    public string Link { get; set; } = "";
    public bool Insecure { get; set; }
    public string? SubscriptionId { get; set; }
    public string? SubscriptionName { get; set; }

    private long _lastPingMs = -1;
    private bool _isPingError;
    private bool _isUrlTest;
    private string _pingText = "...";
    private SolidColorBrush _pingColor = Brushes.Gray;
    private string? _resolvedCountryCode;
    private bool _geoIpAttempted;

    private static readonly SemaphoreSlim _pingSemaphore = new(10, 10);

    [JsonIgnore] public string DisplayName => CountryFlagHelper.StripFlagEmojisAndDuplicateCode(Name);

    [JsonIgnore] public string DisplayAddress => $"{Address}:{Port}";

    [JsonIgnore]
    public string DisplayProtocolDetails
    {
        get
        {
            var parts = new List<string> { Protocol.ToUpperInvariant() };
            if (!string.IsNullOrEmpty(Transport) && Transport != "tcp") parts.Add(Transport.ToUpperInvariant());
            else if (Protocol is "vless" or "vmess") parts.Add("TCP");
            if (!string.IsNullOrEmpty(Pbk)) parts.Add("REALITY");
            else if (Security is "tls" || Port == 443) parts.Add("TLS");
            return string.Join(" / ", parts);
        }
    }

    [JsonIgnore]
    public string CountryCode
    {
        get
        {
            string detected = CountryFlagHelper.DetectCountryCode(Name, Address);
            if (!string.IsNullOrEmpty(detected)) return detected;
            return _resolvedCountryCode ?? "";
        }
    }

    [JsonIgnore]
    public string FlagEmoji => CountryFlagHelper.CountryCodeToEmoji(CountryCode);

    [JsonIgnore]
    public BitmapImage? FlagImage
    {
        get
        {
            string code = CountryCode;
            if (string.IsNullOrEmpty(code))
            {
                TriggerGeoIpResolve();
                return null;
            }
            return CountryFlagHelper.GetFlagImage(code, this);
        }
    }

    [JsonIgnore]
    public Visibility FlagEmojiVisibility => FlagImage == null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility FlagFallbackVisibility => FlagImage == null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public bool IsActiveProfile => MainWindow.ActiveProfile == this;

    [JsonIgnore]
    public Visibility ActiveIndicatorVisibility => MainWindow.ActiveProfile == this ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Brush SubItemBackground => MainWindow.ActiveProfile == this
        ? (Application.Current?.MainWindow?.TryFindResource("HoverColor") as Brush ?? new SolidColorBrush(Color.FromRgb(74, 68, 88)))
        : System.Windows.Media.Brushes.Transparent;

    [JsonIgnore]
    public string PingText
    {
        get => _pingText;
        set { _pingText = value; OnPropertyChanged(nameof(PingText)); }
    }

    [JsonIgnore]
    public SolidColorBrush PingColor
    {
        get => _pingColor;
        set { _pingColor = value; OnPropertyChanged(nameof(PingColor)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void TriggerGeoIpResolve()
    {
        if (_geoIpAttempted || string.IsNullOrWhiteSpace(Address)) return;
        _geoIpAttempted = true;

        _ = Task.Run(async () =>
        {
            string code = await CountryFlagHelper.ResolveCountryCodeByHostAsync(Address);
            if (!string.IsNullOrEmpty(code))
            {
                _resolvedCountryCode = code;
                CountryFlagHelper.GetFlagImage(code, this);
                Application.Current?.Dispatcher?.InvokeAsync(NotifyVisuals);
            }
        });
    }

    public void ResetGeoIpRetry()
    {
        if (string.IsNullOrEmpty(CountryCode))
        {
            _geoIpAttempted = false;
        }
        NotifyVisuals();
    }

    public void NotifyVisuals()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsActiveProfile));
        OnPropertyChanged(nameof(ActiveIndicatorVisibility));
        OnPropertyChanged(nameof(SubItemBackground));
        OnPropertyChanged(nameof(FlagImage));
        OnPropertyChanged(nameof(FlagEmoji));
        OnPropertyChanged(nameof(FlagEmojiVisibility));
        OnPropertyChanged(nameof(FlagFallbackVisibility));
        OnPropertyChanged(nameof(DisplayProtocolDetails));
    }

    public void ForceUpdatePing()
    {
        PingColor = GetPingColor(_lastPingMs);
        if (_lastPingMs > 0)
            PingText = _isUrlTest ? $"{_lastPingMs} {MainWindow.L("PingMs")} (HTTP)" : $"{_lastPingMs} {MainWindow.L("PingMs")}";
        else if (PingText != "...")
            PingText = _isPingError ? MainWindow.L("PingError") : MainWindow.L("PingTimeout");
    }

    public void SetUrlTestResult(long ms)
    {
        _lastPingMs = ms;
        _isPingError = ms <= 0;
        _isUrlTest = true;
        PingColor = GetPingColor(ms);
        PingText = ms > 0 ? $"{ms} {MainWindow.L("PingMs")} (HTTP)" : MainWindow.L("PingError");
    }

    private static SolidColorBrush GetPingColor(long ms)
    {
        if (ms is < 0 or > 9999)
        {
            var errBrush = new SolidColorBrush(MainWindow.IsDarkTheme ? Color.FromRgb(248, 113, 113) : Color.FromRgb(220, 38, 38));
            errBrush.Freeze();
            return errBrush;
        }

        ms = Math.Clamp(ms, 0, 600);
        double t; byte r, g, b;

        if (MainWindow.IsDarkTheme)
        {
            if (ms <= 200) { t = ms / 200.0; r = (byte)(74 + t * 176); g = (byte)(222 - t * 18); b = (byte)(128 - t * 107); }
            else { t = (ms - 200) / 400.0; r = (byte)(250 - t * 2); g = (byte)(204 - t * 91); b = (byte)(21 + t * 92); }
        }
        else
        {
            if (ms <= 200) { t = ms / 200.0; r = (byte)(22 + t * 180); g = (byte)(163 - t * 25); b = (byte)(4 + t * 0); }
            else { t = (ms - 200) / 400.0; r = (byte)(202 + t * 18); g = (byte)(138 - t * 100); b = (byte)(4 + t * 34); }
        }
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void UpdatePing(long ms, string text, bool isError = false)
    {
        _lastPingMs = ms;
        _isPingError = isError;
        _isUrlTest = false;
        PingText = text;
        PingColor = GetPingColor(ms);
    }

    private async Task<long> MeasureLatencyAsync(IPAddress targetIp, int port)
    {
        long bestMs = long.MaxValue;

        try
        {
            using var ping = new Ping();
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(targetIp, 2000);
                    if (reply.Status == IPStatus.Success) bestMs = Math.Min(bestMs, reply.RoundtripTime);
                }
                catch { }
            }
        }
        catch { }

        if (bestMs != long.MaxValue) return Math.Max(1, bestMs);

        if (MainWindow.IsVpnRunning && MainWindow.ActiveProfile != this) return -1;

        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var tcp = new TcpClient(targetIp.AddressFamily) { NoDelay = true };
                var sw = Stopwatch.StartNew();
                await tcp.ConnectAsync(targetIp, port).WaitAsync(TimeSpan.FromSeconds(2));
                sw.Stop();
                bestMs = Math.Min(bestMs, sw.ElapsedMilliseconds);
            }
            catch { }
        }

        return bestMs != long.MaxValue ? Math.Max(1, bestMs) : -1;
    }

    public async Task CheckPingAsync(bool bypassCache = false)
    {
        await _pingSemaphore.WaitAsync();
        try
        {
            var host = Address;
            var port = Port > 0 ? Port : 443;

            if (string.IsNullOrEmpty(host) && Uri.TryCreate(Link, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
                port = uri.Port > 0 ? uri.Port : 443;
            }

            if (string.IsNullOrEmpty(host)) { UpdatePing(-1, MainWindow.L("PingError"), true); return; }

            if (!IPAddress.TryParse(host, out var targetIp))
            {
                var ips = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(3));
                targetIp = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? ips.FirstOrDefault();
            }

            if (targetIp == null || targetIp.Equals(IPAddress.Any) || targetIp.Equals(IPAddress.IPv6Any))
            {
                UpdatePing(-1, MainWindow.L("PingError"), true);
                return;
            }

            string protocolFamily = Protocol is "hysteria" or "hysteria2" or "tuic" or "wireguard" ? "udp" : "tcp";
            string cacheKey = $"{targetIp}:{port}:{protocolFamily}_{(MainWindow.IsVpnRunning ? "active" : "inactive")}";

            long ms = bypassCache
                ? await MeasureLatencyAsync(targetIp, port)
                : await PingManager.GetPingTask(cacheKey, () => MeasureLatencyAsync(targetIp, port));

            if (ms > 0)
            {
                ms = Math.Max(1, ms + Random.Shared.Next(-1, 2));
                UpdatePing(ms, $"{ms} {MainWindow.L("PingMs")}");
            }
            else UpdatePing(-1, MainWindow.L("PingTimeout"));
        }
        catch
        {
            UpdatePing(-1, MainWindow.L("PingError"), true);
        }
        finally
        {
            _pingSemaphore.Release();
        }
    }
}

public class AppSettings
{
    public List<VpnProfile> Profiles { get; set; } = new List<VpnProfile>();
    public List<Subscription> Subscriptions { get; set; } = new List<Subscription>();
    public string Hwid { get; set; } = "";
    public string Language { get; set; } = "";
    public int SelectedProfileIndex { get; set; } = -1;
    public string Mode { get; set; } = "TUN";
    public string Theme { get; set; } = "System";
    public string RemoteDns { get; set; } = "8.8.8.8";
    public string DirectDns { get; set; } = "77.88.8.8";
    public bool AutoStart { get; set; }
    public bool AutoConnect { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoUpdateSubscriptions { get; set; } = true;
    public bool HasShownTrayNotification { get; set; }
    public bool EnableVisualEffects { get; set; }
    public bool DisableAnimations { get; set; }
    public bool UseCustomConfig { get; set; }
    public bool BypassRuDomains { get; set; }
    public string RouteMode { get; set; } = "Bypass";
    public List<string> RoutingDomains { get; set; } = new List<string>();
    public List<string> RoutingApps { get; set; } = new List<string>();
    public int ProxyPort { get; set; } = 2080;
    public bool AllowLan { get; set; }
    public bool StrictRoute { get; set; } = true;
    public bool EnableIpv6 { get; set; }
    public string LogLevel { get; set; } = "info";
    public string TunStack { get; set; } = "system";
}

public partial class MainWindow : Window
{
    public static bool IsVpnRunning;
    public static VpnProfile? ActiveProfile;
    public static bool IsDarkTheme = true;
    public static string CurrentLanguage = "ru";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private Process? _singboxProcess;
    private bool _isRunning;
    private readonly string _exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private AppSettings _currentSettings = new();
    private bool _isUpdatingSettings;
    private bool _isUpdatingRouting;
    private bool _lastAppliedAutoStart;
    private string _currentTunInterfaceName = "NeonV";
    private readonly HashSet<string> _blockedTunNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<VpnProfile> _profiles = new();
    private readonly ObservableCollection<VpnProfile> _standaloneProfiles = new();
    private readonly ObservableCollection<Subscription> _subscriptions = new();
    public ObservableCollection<string> RoutingDomainsList { get; set; } = new();
    public ObservableCollection<string> RoutingAppsList { get; set; } = new();

    private DispatcherTimer? _pingTimer;
    private DispatcherTimer? _uptimeTimer;
    private DispatcherTimer? _logTimer;
    private DateTime _connectionStartTime;

    private readonly Dictionary<string, string> _manualInputValues = new(StringComparer.OrdinalIgnoreCase);
    private VpnProfile? _editingProfile;
    private string _originalIp = "";
    private string _currentQrLink = "";
    private string _lastLocationCountryCode = "";
    private string? _lastLocationApiName;

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _trayNavMainItem;
    private Forms.ToolStripMenuItem? _trayNavSubscriptionsItem;
    private Forms.ToolStripMenuItem? _trayNavSettingsItem;
    private Forms.ToolStripMenuItem? _trayNavLogsItem;
    private Forms.ToolStripMenuItem? _trayModeItem;
    private Forms.ToolStripMenuItem? _trayToggleItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private bool _isRealClose;

    private readonly Queue<string> _logBuffer = new();
    private readonly object _logLock = new();
    private string _lastCoreError = "";

    private long _lastBytesReceived;
    private long _lastBytesSent;
    private CancellationTokenSource? _saveSettingsCts;
    private CancellationTokenSource? _notificationCts;
    private CancellationTokenSource? _locationCts;
    private readonly SemaphoreSlim _vpnStateSemaphore = new(1, 1);
    private Window? _fullScreenOverlay;

    private static readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["ru"] = new()
        {
            ["NavMain"] = "Главная",
            ["NavSubs"] = "Подписки",
            ["NavSettings"] = "Настройки",
            ["NavLogs"] = "Логи",
            ["ModeLabel"] = "Режим работы:",
            ["BtnAddProfile"] = "Добавить профиль",
            ["BtnRefreshPing"] = "Обновить пинг",
            ["SearchPlaceholder"] = "Поиск профилей...",
            ["EmptyProfilesTitle"] = "Нет добавленных профилей",
            ["EmptyProfilesDesc"] = "Нажмите «Добавить профиль» для начала работы",
            ["BtnAddSub"] = "Добавить подписку",
            ["BtnUpdateAllSubs"] = "Обновить все",
            ["EmptySubsTitle"] = "Нет добавленных подписок",
            ["EmptySubsDesc"] = "Нажмите «Добавить подписку» чтобы загрузить список серверов",
            ["SettingsTitle"] = "Настройки",
            ["GroupLaunch"] = "Запуск и работа",
            ["AutoStart"] = "Запускать вместе с Windows",
            ["StartMinimized"] = "Запускать свернутым в трей",
            ["MinimizeToTray"] = "Сворачивать в трей при закрытии",
            ["AutoUpdateSubs"] = "Автообновление подписок при запуске",
            ["CustomConfig"] = "Использовать собственный config.json (без перезаписи)",
            ["GroupAppearance"] = "Внешний вид",
            ["LangLabel"] = "Язык интерфейса (Language)",
            ["ThemeLabel"] = "Тема оформления",
            ["ThemeSystem"] = "Системная",
            ["ThemeDark"] = "Темная",
            ["ThemeLight"] = "Светлая",
            ["VisualEffects"] = "Включить визуальные эффекты",
            ["DisableAnimations"] = "Отключить анимации переходов",
            ["GroupNetwork"] = "Сеть и Маршрутизация",
            ["DnsLabel"] = "Удаленный DNS (Remote)",
            ["DirectDnsLabel"] = "Прямой DNS (Direct)",
            ["BtnRouting"] = "Настроить исключения (Маршруты)",
            ["GroupAdvanced"] = "Продвинутые настройки",
            ["ProxyPortLabel"] = "Порт локального прокси",
            ["TunStackLabel"] = "Сетевой стек TUN",
            ["StrictRoute"] = "Строгая маршрутизация TUN (Strict Route)",
            ["EnableIpv6"] = "Поддержка IPv6 (Dual-Stack)",
            ["AllowLan"] = "Разрешить подключения по локальной сети (LAN)",
            ["AllowLanDesc"] = "Позволяет другим устройствам в вашей Wi-Fi сети подключаться к этому прокси.",
            ["LogsTitle"] = "Логи ядра",
            ["LogLevelLabel"] = "Уровень: ",
            ["AutoScroll"] = "Автопрокрутка",
            ["BtnCopyLogs"] = "Скопировать",
            ["BtnClearLogs"] = "Очистить",
            ["ImportTitle"] = "Добавление профиля",
            ["EditTitle"] = "Редактировать профиль",
            ["PasteImport"] = "Вставить из буфера обмена",
            ["OrManual"] = "Или добавить вручную:",
            ["ProtocolLabel"] = "Протокол",
            ["Cancel"] = "Отмена",
            ["Save"] = "Сохранить",
            ["AddSubTitle"] = "Добавить подписку",
            ["SubNameLabel"] = "Название (необязательно)",
            ["SubUrlLabel"] = "Ссылка на подписку (URL)",
            ["Add"] = "Добавить",
            ["RoutingTitle"] = "Исключения и правила",
            ["BypassRu"] = "Не проксировать российские сайты (.ru, .рф)",
            ["RouteModeLabel"] = "Режим исключений:",
            ["RouteModeBypass"] = "Включить для всех, КРОМЕ...",
            ["RouteModeOnly"] = "Включить ТОЛЬКО ДЛЯ...",
            ["DomainsLabel"] = "Сайты и домены (например: google.com)",
            ["AppsLabel"] = "Приложения (например: telegram.exe)",
            ["Done"] = "Готово",
            ["QRTitle"] = "QR-Код профиля",
            ["CopyLink"] = "Скопировать ссылку",
            ["CtxConnect"] = "Подключиться",
            ["CtxUrlTest"] = "Тест соединения (HTTP)",
            ["CtxEdit"] = "Редактировать профиль",
            ["CtxCopy"] = "Копировать ссылку",
            ["CtxUp"] = "Переместить вверх",
            ["CtxDown"] = "Переместить вниз",
            ["CtxDelete"] = "Удалить",
            ["TrafficInfinite"] = "Трафик: ∞",
            ["ExpiresFmt"] = "Истекает: {0}",
            ["UpdatedFmt"] = "Обновлено: {0}",
            ["NeverUpdated"] = "Еще не обновлялось",
            ["TooltipPing"] = "Измерить пинг",
            ["TooltipUpdate"] = "Обновить",
            ["TooltipDelete"] = "Удалить",
            ["PingMs"] = "мс",
            ["PingTimeout"] = "Таймаут",
            ["PingError"] = "Ошибка",
            ["UnknownLocation"] = "Неизвестно",
            ["TrayModeFmt"] = "Режим: {0}",
            ["TrayExit"] = "Выход",
            ["TrayConnectVpn"] = "Включить VPN",
            ["TrayDisconnectVpn"] = "Выключить VPN",
            ["TrayConnectProxy"] = "Включить Proxy",
            ["TrayDisconnectProxy"] = "Выключить Proxy",
            ["TrayBgTip"] = "Приложение работает в фоновом режиме.",
            ["BtnConnectVpn"] = "ВКЛЮЧИТЬ VPN",
            ["BtnDisconnectVpn"] = "ВЫКЛЮЧИТЬ VPN",
            ["BtnConnectProxy"] = "ВКЛЮЧИТЬ PROXY",
            ["BtnDisconnectProxy"] = "ВЫКЛЮЧИТЬ PROXY",
            ["AutoConnectVpn"] = "Автоматически подключать VPN при запуске",
            ["AutoConnectProxy"] = "Автоматически подключать Proxy при запуске",
            ["FieldName"] = "Название профиля",
            ["FieldAddress"] = "Адрес (IP или домен)",
            ["FieldPort"] = "Порт",
            ["FieldPassword"] = "Пароль (Password)",
            ["FieldUuid"] = "UUID / Имя пользователя",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint (Отпечаток)",
            ["FieldInsecure"] = "Игнорировать ошибки TLS",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "Transport (tcp, ws...)",
            ["FieldPath"] = "Path (Путь)",
            ["FieldHost"] = "Host",
            ["FieldSecurity"] = "Security",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "Метод шифрования",
            ["FieldPublicKey"] = "Public Key (Публичный)",
            ["FieldPrivateKey"] = "Private Key (Приватный)",
            ["FieldLocalAddress"] = "Local Address (Локальный)",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "Up Mbps (Отдача)",
            ["FieldDown"] = "Down Mbps (Загрузка)",
            ["FieldObfs"] = "Тип Obfs",
            ["FieldObfsPassword"] = "Пароль Obfs",
            ["NotifySelectProfile"] = "Сначала выберите профиль из списка!",
            ["NotifyAdminRequired"] = "Требуются права Администратора!",
            ["NotifyWintunMissing"] = "Ошибка: Файл wintun.dll не найден!",
            ["NotifyCoreExited"] = "Ядро Sing-box завершило работу с ошибкой. Проверьте логи.",
            ["NotifyCopied"] = "Скопировано в буфер обмена",
            ["NotifyImported"] = "Профиль успешно импортирован",
            ["NotifyImportError"] = "Ошибка: в буфере нет профилей или они неверны",
            ["NotifyInvalidAddress"] = "Укажите корректный адрес сервера",
            ["NotifyInvalidPort"] = "Укажите корректный порт (1-65535)",
            ["NotifyQrTooLong"] = "Генерация QR-кода невозможна: превышена длина ссылки",
            ["NotifyQrError"] = "Произошла ошибка при генерации QR-кода",
            ["NotifySubInvalidUrl"] = "Укажите корректную ссылку на подписку",
            ["NotifySubLoading"] = "Загрузка профилей подписки...",
            ["NotifySubAllUpdating"] = "Обновление всех подписок...",
            ["NotifySubAllUpdated"] = "Все подписки обновлены",
            ["NotifySubDeleted"] = "Подписка удалена",
            ["NotifySubDeviceLimit"] = "Достигнут лимит устройств для этой подписки",
            ["NotifySubLoadErrorFmt"] = "Ошибка загрузки подписки ({0})",
            ["NotifySubAppUnsupported"] = "Сервер отклонил запрос (App not supported). Проверьте лимит устройств.",
            ["NotifySubNoProfiles"] = "Подписка не содержит поддерживаемых профилей",
            ["NotifySubUpdatedCountFmt"] = "Подписка обновлена (загружено серверов: {0})",
            ["NotifySubErrorFmt"] = "Ошибка обновления подписки: {0}",
            ["NotifyRoutesReconnecting"] = "Маршруты изменены, переподключение...",
            ["NotifyLogLevelReconnecting"] = "Уровень логов изменен, переподключение...",
            ["NotifyReconnectingServer"] = "Смена сервера, переподключение...",
            ["NotifyModeReconnecting"] = "Режим работы изменён, переподключение...",
            ["NotifySettingsReconnecting"] = "Настройки изменены, переподключение...",
            ["NotifyProfileUpdatedReconnecting"] = "Профиль изменён, переподключение...",
            ["NotifyPortBusyFallbackFmt"] = "Порт {0} занят, используется порт {1}"
        },
        ["en"] = new()
        {
            ["NavMain"] = "Home",
            ["NavSubs"] = "Subscriptions",
            ["NavSettings"] = "Settings",
            ["NavLogs"] = "Logs",
            ["ModeLabel"] = "Work mode:",
            ["BtnAddProfile"] = "Add profile",
            ["BtnRefreshPing"] = "Refresh ping",
            ["SearchPlaceholder"] = "Search profiles...",
            ["EmptyProfilesTitle"] = "No added profiles",
            ["EmptyProfilesDesc"] = "Click \"Add profile\" to get started",
            ["BtnAddSub"] = "Add subscription",
            ["BtnUpdateAllSubs"] = "Update all",
            ["EmptySubsTitle"] = "No added subscriptions",
            ["EmptySubsDesc"] = "Click \"Add subscription\" to load server list",
            ["SettingsTitle"] = "Settings",
            ["GroupLaunch"] = "Launch & Startup",
            ["AutoStart"] = "Launch with Windows",
            ["StartMinimized"] = "Start minimized to tray",
            ["MinimizeToTray"] = "Minimize to tray on close",
            ["AutoUpdateSubs"] = "Auto-update subscriptions on launch",
            ["CustomConfig"] = "Use custom config.json (without overwrite)",
            ["GroupAppearance"] = "Appearance",
            ["LangLabel"] = "Language",
            ["ThemeLabel"] = "Interface theme",
            ["ThemeSystem"] = "System",
            ["ThemeDark"] = "Dark",
            ["ThemeLight"] = "Light",
            ["VisualEffects"] = "Enable visual effects",
            ["DisableAnimations"] = "Disable transition animations",
            ["GroupNetwork"] = "Network & Routing",
            ["DnsLabel"] = "Remote DNS",
            ["DirectDnsLabel"] = "Direct DNS",
            ["BtnRouting"] = "Configure routing rules",
            ["GroupAdvanced"] = "Advanced settings",
            ["ProxyPortLabel"] = "Local proxy port",
            ["TunStackLabel"] = "TUN network stack",
            ["StrictRoute"] = "TUN Strict Route",
            ["EnableIpv6"] = "Enable IPv6 (Dual-Stack)",
            ["AllowLan"] = "Allow LAN connections",
            ["AllowLanDesc"] = "Allows other devices on your Wi-Fi network to connect to this proxy.",
            ["LogsTitle"] = "Core logs",
            ["LogLevelLabel"] = "Level: ",
            ["AutoScroll"] = "Auto-scroll",
            ["BtnCopyLogs"] = "Copy",
            ["BtnClearLogs"] = "Clear",
            ["ImportTitle"] = "Add profile",
            ["EditTitle"] = "Edit profile",
            ["PasteImport"] = "Paste from clipboard",
            ["OrManual"] = "Or add manually:",
            ["ProtocolLabel"] = "Protocol",
            ["Cancel"] = "Cancel",
            ["Save"] = "Save",
            ["AddSubTitle"] = "Add subscription",
            ["SubNameLabel"] = "Name (optional)",
            ["SubUrlLabel"] = "Subscription URL",
            ["Add"] = "Add",
            ["RoutingTitle"] = "Routing & Rules",
            ["BypassRu"] = "Bypass Russian websites (.ru, .рф)",
            ["RouteModeLabel"] = "Routing mode:",
            ["RouteModeBypass"] = "Enable for all EXCEPT...",
            ["RouteModeOnly"] = "Enable ONLY FOR...",
            ["DomainsLabel"] = "Sites and domains (e.g. google.com)",
            ["AppsLabel"] = "Applications (e.g. telegram.exe)",
            ["Done"] = "Done",
            ["QRTitle"] = "Profile QR Code",
            ["CopyLink"] = "Copy link",
            ["CtxConnect"] = "Connect",
            ["CtxUrlTest"] = "Connection test (HTTP)",
            ["CtxEdit"] = "Edit profile",
            ["CtxCopy"] = "Copy link",
            ["CtxUp"] = "Move up",
            ["CtxDown"] = "Move down",
            ["CtxDelete"] = "Delete",
            ["TrafficInfinite"] = "Traffic: ∞",
            ["ExpiresFmt"] = "Expires: {0}",
            ["UpdatedFmt"] = "Updated: {0}",
            ["NeverUpdated"] = "Not updated yet",
            ["TooltipPing"] = "Measure ping",
            ["TooltipUpdate"] = "Update",
            ["TooltipDelete"] = "Delete",
            ["PingMs"] = "ms",
            ["PingTimeout"] = "Timeout",
            ["PingError"] = "Error",
            ["UnknownLocation"] = "Unknown",
            ["TrayModeFmt"] = "Mode: {0}",
            ["TrayExit"] = "Exit",
            ["TrayConnectVpn"] = "Connect VPN",
            ["TrayDisconnectVpn"] = "Disconnect VPN",
            ["TrayConnectProxy"] = "Connect Proxy",
            ["TrayDisconnectProxy"] = "Disconnect Proxy",
            ["TrayBgTip"] = "Application is running in the background.",
            ["BtnConnectVpn"] = "CONNECT VPN",
            ["BtnDisconnectVpn"] = "DISCONNECT VPN",
            ["BtnConnectProxy"] = "CONNECT PROXY",
            ["BtnDisconnectProxy"] = "DISCONNECT PROXY",
            ["AutoConnectVpn"] = "Automatically connect VPN on launch",
            ["AutoConnectProxy"] = "Automatically connect Proxy on launch",
            ["FieldName"] = "Profile name",
            ["FieldAddress"] = "Address (IP or domain)",
            ["FieldPort"] = "Port",
            ["FieldPassword"] = "Password",
            ["FieldUuid"] = "UUID / Username",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint",
            ["FieldInsecure"] = "Ignore TLS errors",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "Transport (tcp, ws...)",
            ["FieldPath"] = "Path",
            ["FieldHost"] = "Host",
            ["FieldSecurity"] = "Security",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "Encryption method",
            ["FieldPublicKey"] = "Public Key",
            ["FieldPrivateKey"] = "Private Key",
            ["FieldLocalAddress"] = "Local Address",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "Up Mbps",
            ["FieldDown"] = "Down Mbps",
            ["FieldObfs"] = "Obfs type",
            ["FieldObfsPassword"] = "Obfs password",
            ["NotifySelectProfile"] = "Please select a profile first!",
            ["NotifyAdminRequired"] = "Administrator privileges required!",
            ["NotifyWintunMissing"] = "Error: wintun.dll not found!",
            ["NotifyCoreExited"] = "Sing-box core exited with an error. Check logs.",
            ["NotifyCopied"] = "Copied to clipboard",
            ["NotifyImported"] = "Profile imported successfully",
            ["NotifyImportError"] = "Error: no valid profiles in clipboard",
            ["NotifyInvalidAddress"] = "Enter a valid server address",
            ["NotifyInvalidPort"] = "Enter a valid port (1-65535)",
            ["NotifyQrTooLong"] = "Cannot generate QR code: link is too long",
            ["NotifyQrError"] = "Error generating QR code",
            ["NotifySubInvalidUrl"] = "Enter a valid subscription URL",
            ["NotifySubLoading"] = "Loading subscription profiles...",
            ["NotifySubAllUpdating"] = "Updating all subscriptions...",
            ["NotifySubAllUpdated"] = "All subscriptions updated",
            ["NotifySubDeleted"] = "Subscription deleted",
            ["NotifySubDeviceLimit"] = "Device limit reached for this subscription",
            ["NotifySubLoadErrorFmt"] = "Failed to load subscription ({0})",
            ["NotifySubAppUnsupported"] = "Server rejected request (App not supported). Check device limit.",
            ["NotifySubNoProfiles"] = "Subscription contains no supported profiles",
            ["NotifySubUpdatedCountFmt"] = "Subscription updated (servers loaded: {0})",
            ["NotifySubErrorFmt"] = "Subscription update error: {0}",
            ["NotifyRoutesReconnecting"] = "Routes changed, reconnecting...",
            ["NotifyLogLevelReconnecting"] = "Log level changed, reconnecting...",
            ["NotifyReconnectingServer"] = "Server changed, reconnecting...",
            ["NotifyModeReconnecting"] = "Work mode changed, reconnecting...",
            ["NotifySettingsReconnecting"] = "Settings changed, reconnecting...",
            ["NotifyProfileUpdatedReconnecting"] = "Profile updated, reconnecting...",
            ["NotifyPortBusyFallbackFmt"] = "Port {0} is busy, using port {1}"
        },
        ["uk"] = new()
        {
            ["NavMain"] = "Головна",
            ["NavSubs"] = "Підписки",
            ["NavSettings"] = "Налаштування",
            ["NavLogs"] = "Логи",
            ["ModeLabel"] = "Режим роботи:",
            ["BtnAddProfile"] = "Додати профіль",
            ["BtnRefreshPing"] = "Оновити пінг",
            ["SearchPlaceholder"] = "Пошук профілів...",
            ["EmptyProfilesTitle"] = "Немає доданих профілів",
            ["EmptyProfilesDesc"] = "Натисніть «Додати профіль» для початку",
            ["BtnAddSub"] = "Додати підписку",
            ["BtnUpdateAllSubs"] = "Оновити всі",
            ["EmptySubsTitle"] = "Немає доданих підписок",
            ["EmptySubsDesc"] = "Натисніть «Додати підписку» для завантаження серверів",
            ["SettingsTitle"] = "Налаштування",
            ["GroupLaunch"] = "Запуск і робота",
            ["AutoStart"] = "Запускати разом з Windows",
            ["StartMinimized"] = "Запускати згорнутим у трей",
            ["MinimizeToTray"] = "Згортати в трей при закритті",
            ["AutoUpdateSubs"] = "Автооновлення підписок при запуску",
            ["CustomConfig"] = "Використовувати власний config.json (без перезапису)",
            ["GroupAppearance"] = "Зовнішній вигляд",
            ["LangLabel"] = "Мова інтерфейсу (Language)",
            ["ThemeLabel"] = "Тема оформлення",
            ["ThemeSystem"] = "Системна",
            ["ThemeDark"] = "Темна",
            ["ThemeLight"] = "Світла",
            ["VisualEffects"] = "Увімкнути візуальні ефекти",
            ["DisableAnimations"] = "Вимкнути анімації переходів",
            ["GroupNetwork"] = "Мережа та Маршрутизація",
            ["DnsLabel"] = "Віддалений DNS (Remote)",
            ["DirectDnsLabel"] = "Прямий DNS (Direct)",
            ["BtnRouting"] = "Налаштувати винятки (Маршрути)",
            ["GroupAdvanced"] = "Розширені налаштування",
            ["ProxyPortLabel"] = "Порт локального проксі",
            ["TunStackLabel"] = "Мережевий стек TUN",
            ["StrictRoute"] = "Сувора маршрутизація TUN (Strict Route)",
            ["EnableIpv6"] = "Підтримка IPv6 (Dual-Stack)",
            ["AllowLan"] = "Дозволити підключення локальної мережі (LAN)",
            ["AllowLanDesc"] = "Дозволяє іншим пристроям у вашій мережі підключатися до проксі.",
            ["LogsTitle"] = "Логи ядра",
            ["LogLevelLabel"] = "Рівень: ",
            ["AutoScroll"] = "Автопрокрутка",
            ["BtnCopyLogs"] = "Скопіювати",
            ["BtnClearLogs"] = "Очистити",
            ["ImportTitle"] = "Додавання профілю",
            ["EditTitle"] = "Редагувати профіль",
            ["PasteImport"] = "Вставити з буфера обміну",
            ["OrManual"] = "Або додати вручну:",
            ["ProtocolLabel"] = "Протокол",
            ["Cancel"] = "Скасувати",
            ["Save"] = "Зберегти",
            ["AddSubTitle"] = "Додати підписку",
            ["SubNameLabel"] = "Назва (необов'язково)",
            ["SubUrlLabel"] = "Посилання на підписку (URL)",
            ["Add"] = "Додати",
            ["RoutingTitle"] = "Винятки та правила",
            ["BypassRu"] = "Не проксіювати російські сайти (.ru, .рф)",
            ["RouteModeLabel"] = "Режим винятків:",
            ["RouteModeBypass"] = "Увімкнути для всіх, КРІМ...",
            ["RouteModeOnly"] = "Увімкнути ТІЛЬКИ ДЛЯ...",
            ["DomainsLabel"] = "Сайти та домени (наприклад: google.com)",
            ["AppsLabel"] = "Програми (наприклад: telegram.exe)",
            ["Done"] = "Готово",
            ["QRTitle"] = "QR-код профілю",
            ["CopyLink"] = "Скопіювати посилання",
            ["CtxConnect"] = "Підключитися",
            ["CtxUrlTest"] = "Тест з'єднання (HTTP)",
            ["CtxEdit"] = "Редагувати профіль",
            ["CtxCopy"] = "Копіювати посилання",
            ["CtxUp"] = "Перемістити вгору",
            ["CtxDown"] = "Перемістити вниз",
            ["CtxDelete"] = "Видалити",
            ["TrafficInfinite"] = "Трафік: ∞",
            ["ExpiresFmt"] = "Закінчується: {0}",
            ["UpdatedFmt"] = "Оновлено: {0}",
            ["NeverUpdated"] = "Ще не оновлювалося",
            ["TooltipPing"] = "Виміряти пінг",
            ["TooltipUpdate"] = "Оновити",
            ["TooltipDelete"] = "Видалити",
            ["PingMs"] = "мс",
            ["PingTimeout"] = "Тайм-аут",
            ["PingError"] = "Помилка",
            ["UnknownLocation"] = "Невідомо",
            ["TrayModeFmt"] = "Режим: {0}",
            ["TrayExit"] = "Вихід",
            ["TrayConnectVpn"] = "Увімкнути VPN",
            ["TrayDisconnectVpn"] = "Вимкнути VPN",
            ["TrayConnectProxy"] = "Увімкнути Proxy",
            ["TrayDisconnectProxy"] = "Вимкнути Proxy",
            ["TrayBgTip"] = "Програма працює у фоновому режимі.",
            ["BtnConnectVpn"] = "УВІМКНУТИ VPN",
            ["BtnDisconnectVpn"] = "ВИМКНУТИ VPN",
            ["BtnConnectProxy"] = "УВІМКНУТИ PROXY",
            ["BtnDisconnectProxy"] = "ВИМКНУТИ PROXY",
            ["AutoConnectVpn"] = "Автоматично підключати VPN при запуску",
            ["AutoConnectProxy"] = "Автоматично підключати Proxy при запуску",
            ["FieldName"] = "Назва профілю",
            ["FieldAddress"] = "Адреса (IP або домен)",
            ["FieldPort"] = "Порт",
            ["FieldPassword"] = "Пароль (Password)",
            ["FieldUuid"] = "UUID / Ім'я користувача",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint (Відбиток)",
            ["FieldInsecure"] = "Ігнорувати помилки TLS",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "Transport (tcp, ws...)",
            ["FieldPath"] = "Path (Шлях)",
            ["FieldHost"] = "Host",
            ["FieldSecurity"] = "Security",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "Метод шифрування",
            ["FieldPublicKey"] = "Public Key (Публічний)",
            ["FieldPrivateKey"] = "Private Key (Приватний)",
            ["FieldLocalAddress"] = "Local Address (Локальний)",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "Up Mbps (Віддача)",
            ["FieldDown"] = "Down Mbps (Завантаження)",
            ["FieldObfs"] = "Тип Obfs",
            ["FieldObfsPassword"] = "Пароль Obfs",
            ["NotifySelectProfile"] = "Спочатку виберіть профіль зі списку!",
            ["NotifyAdminRequired"] = "Потрібні права Адміністратора!",
            ["NotifyWintunMissing"] = "Помилка: файл wintun.dll не знайдено!",
            ["NotifyCoreExited"] = "Ядро Sing-box завершило роботу з помилкою. Перевірте логи.",
            ["NotifyCopied"] = "Скопійовано до буфера обміну",
            ["NotifyImported"] = "Профіль успішно імпортовано",
            ["NotifyImportError"] = "Помилка: у буфері немає профілів або вони невірні",
            ["NotifyInvalidAddress"] = "Вкажіть коректну адресу сервера",
            ["NotifyInvalidPort"] = "Вкажіть коректний порт (1-65535)",
            ["NotifyQrTooLong"] = "Генерація QR-коду неможлива: перевищено довжину посилання",
            ["NotifyQrError"] = "Виникла помилка при генерації QR-коду",
            ["NotifySubInvalidUrl"] = "Вкажіть коректне посилання на підписку",
            ["NotifySubLoading"] = "Завантаження профілів підписки...",
            ["NotifySubAllUpdating"] = "Оновлення всіх підписок...",
            ["NotifySubAllUpdated"] = "Усі підписки оновлено",
            ["NotifySubDeleted"] = "Підписку видалено",
            ["NotifySubDeviceLimit"] = "Досягнуто ліміт пристроїв для цієї підписки",
            ["NotifySubLoadErrorFmt"] = "Помилка завантаження підписки ({0})",
            ["NotifySubAppUnsupported"] = "Сервер відхилив запит (App not supported). Перевірте ліміт пристроїв.",
            ["NotifySubNoProfiles"] = "Підписка не містить підтримуваних профілів",
            ["NotifySubUpdatedCountFmt"] = "Підписку оновлено (завантажено серверів: {0})",
            ["NotifySubErrorFmt"] = "Помилка оновлення підписки: {0}",
            ["NotifyRoutesReconnecting"] = "Маршрути змінено, перепідключення...",
            ["NotifyLogLevelReconnecting"] = "Рівень логів змінено, перепідключення...",
            ["NotifyReconnectingServer"] = "Зміна сервера, перепідключення...",
            ["NotifyModeReconnecting"] = "Режим роботи змінено, перепідключення...",
            ["NotifySettingsReconnecting"] = "Налаштування змінено, перепідключення...",
            ["NotifyProfileUpdatedReconnecting"] = "Профіль змінено, перепідключення...",
            ["NotifyPortBusyFallbackFmt"] = "Порт {0} зайнятий, використовується порт {1}"
        },
        ["de"] = new()
        {
            ["NavMain"] = "Übersicht",
            ["NavSubs"] = "Abonnements",
            ["NavSettings"] = "Einstellungen",
            ["NavLogs"] = "Protokolle",
            ["ModeLabel"] = "Betriebsmodus:",
            ["BtnAddProfile"] = "Profil hinzufügen",
            ["BtnRefreshPing"] = "Ping aktualisieren",
            ["SearchPlaceholder"] = "Profile suchen...",
            ["EmptyProfilesTitle"] = "Keine Profile vorhanden",
            ["EmptyProfilesDesc"] = "Klicken Sie auf «Profil hinzufügen» zum Starten",
            ["BtnAddSub"] = "Abo hinzufügen",
            ["BtnUpdateAllSubs"] = "Alle aktualisieren",
            ["EmptySubsTitle"] = "Keine Abonnements vorhanden",
            ["EmptySubsDesc"] = "Klicken Sie auf «Abo hinzufügen», um Server zu laden",
            ["SettingsTitle"] = "Einstellungen",
            ["GroupLaunch"] = "Start & Betrieb",
            ["AutoStart"] = "Mit Windows starten",
            ["StartMinimized"] = "Minimiert in den Tray starten",
            ["MinimizeToTray"] = "Beim Schließen in den Tray minimieren",
            ["AutoUpdateSubs"] = "Abonnements beim Start aktualisieren",
            ["CustomConfig"] = "Eigene config.json nutzen",
            ["GroupAppearance"] = "Erscheinungsbild",
            ["LangLabel"] = "Sprache (Language)",
            ["ThemeLabel"] = "Design",
            ["ThemeSystem"] = "System",
            ["ThemeDark"] = "Dunkel",
            ["ThemeLight"] = "Hell",
            ["VisualEffects"] = "Visuelle Effekte aktivieren",
            ["DisableAnimations"] = "Übergangsanimationen deaktivieren",
            ["GroupNetwork"] = "Netzwerk & Routing",
            ["DnsLabel"] = "Remote DNS",
            ["DirectDnsLabel"] = "Direkter DNS",
            ["BtnRouting"] = "Routing-Regeln konfigurieren",
            ["GroupAdvanced"] = "Erweiterte Einstellungen",
            ["ProxyPortLabel"] = "Lokaler Proxy-Port",
            ["TunStackLabel"] = "TUN-Netzwerkstack",
            ["StrictRoute"] = "TUN Strict Route",
            ["EnableIpv6"] = "IPv6 aktivieren (Dual-Stack)",
            ["AllowLan"] = "LAN-Verbindungen zulassen",
            ["AllowLanDesc"] = "Erlaubt anderen Geräten im WLAN-Netzwerk die Verbindung.",
            ["LogsTitle"] = "Core-Protokolle",
            ["LogLevelLabel"] = "Level: ",
            ["AutoScroll"] = "Auto-Scroll",
            ["BtnCopyLogs"] = "Kopieren",
            ["BtnClearLogs"] = "Löschen",
            ["ImportTitle"] = "Profil hinzufügen",
            ["EditTitle"] = "Profil bearbeiten",
            ["PasteImport"] = "Aus Zwischenablage einfügen",
            ["OrManual"] = "Oder manuell hinzufügen:",
            ["ProtocolLabel"] = "Protokoll",
            ["Cancel"] = "Abbrechen",
            ["Save"] = "Speichern",
            ["AddSubTitle"] = "Abo hinzufügen",
            ["SubNameLabel"] = "Name (optional)",
            ["SubUrlLabel"] = "Abo-URL",
            ["Add"] = "Hinzufügen",
            ["RoutingTitle"] = "Routing-Regeln",
            ["BypassRu"] = "Russische Websites umgehen (.ru, .рф)",
            ["RouteModeLabel"] = "Ausnahmemodus:",
            ["RouteModeBypass"] = "Aktivieren für alle AUSSER...",
            ["RouteModeOnly"] = "NUR aktivieren FÜR...",
            ["DomainsLabel"] = "Domains (z.B. google.com)",
            ["AppsLabel"] = "Anwendungen (z.B. telegram.exe)",
            ["Done"] = "Fertig",
            ["QRTitle"] = "Profil QR-Code",
            ["CopyLink"] = "Link kopieren",
            ["CtxConnect"] = "Verbinden",
            ["CtxUrlTest"] = "Verbindungstest (HTTP)",
            ["CtxEdit"] = "Profil bearbeiten",
            ["CtxCopy"] = "Link kopieren",
            ["CtxUp"] = "Nach oben",
            ["CtxDown"] = "Nach unten",
            ["CtxDelete"] = "Löschen",
            ["TrafficInfinite"] = "Datenverkehr: ∞",
            ["ExpiresFmt"] = "Läuft ab: {0}",
            ["UpdatedFmt"] = "Aktualisiert: {0}",
            ["NeverUpdated"] = "Noch nicht aktualisiert",
            ["TooltipPing"] = "Ping messen",
            ["TooltipUpdate"] = "Aktualisieren",
            ["TooltipDelete"] = "Löschen",
            ["PingMs"] = "ms",
            ["PingTimeout"] = "Zeitlimit",
            ["PingError"] = "Fehler",
            ["UnknownLocation"] = "Unbekannt",
            ["TrayModeFmt"] = "Modus: {0}",
            ["TrayExit"] = "Beenden",
            ["TrayConnectVpn"] = "VPN verbinden",
            ["TrayDisconnectVpn"] = "VPN trennen",
            ["TrayConnectProxy"] = "Proxy verbinden",
            ["TrayDisconnectProxy"] = "Proxy trennen",
            ["TrayBgTip"] = "Die Anwendung läuft im Hintergrund.",
            ["BtnConnectVpn"] = "VPN VERBINDEN",
            ["BtnDisconnectVpn"] = "VPN TRENNEN",
            ["BtnConnectProxy"] = "PROXY VERBINDEN",
            ["BtnDisconnectProxy"] = "PROXY TRENNEN",
            ["AutoConnectVpn"] = "VPN beim Start automatisch verbinden",
            ["AutoConnectProxy"] = "Proxy beim Start automatisch verbinden",
            ["FieldName"] = "Profilname",
            ["FieldAddress"] = "Adresse (IP oder Domain)",
            ["FieldPort"] = "Port",
            ["FieldPassword"] = "Passwort",
            ["FieldUuid"] = "UUID / Benutzername",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint",
            ["FieldInsecure"] = "TLS-Fehler ignorieren",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "Transport (tcp, ws...)",
            ["FieldPath"] = "Pfad",
            ["FieldHost"] = "Host",
            ["FieldSecurity"] = "Security",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "Verschlüsselungsmethode",
            ["FieldPublicKey"] = "Public Key",
            ["FieldPrivateKey"] = "Private Key",
            ["FieldLocalAddress"] = "Lokale Adresse",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "Upload Mbps",
            ["FieldDown"] = "Download Mbps",
            ["FieldObfs"] = "Obfs-Typ",
            ["FieldObfsPassword"] = "Obfs-Passwort",
            ["NotifySelectProfile"] = "Bitte zuerst ein Profil auswählen!",
            ["NotifyAdminRequired"] = "Administratorrechte erforderlich!",
            ["NotifyWintunMissing"] = "Fehler: wintun.dll nicht gefunden!",
            ["NotifyCoreExited"] = "Sing-box wurde mit einem Fehler beendet. Protokolle prüfen.",
            ["NotifyCopied"] = "In Zwischenablage kopiert",
            ["NotifyImported"] = "Profil erfolgreich importiert",
            ["NotifyImportError"] = "Fehler: Keine gültigen Profile in Zwischenablage",
            ["NotifyInvalidAddress"] = "Gültige Serveradresse eingeben",
            ["NotifyInvalidPort"] = "Gültigen Port eingeben (1-65535)",
            ["NotifyQrTooLong"] = "QR-Code nicht möglich: Link zu lang",
            ["NotifyQrError"] = "Fehler beim Erstellen des QR-Codes",
            ["NotifySubInvalidUrl"] = "Gültige Abo-URL eingeben",
            ["NotifySubLoading"] = "Abonnement wird geladen...",
            ["NotifySubAllUpdating"] = "Alle Abonnements werden aktualisiert...",
            ["NotifySubAllUpdated"] = "Alle Abonnements aktualisiert",
            ["NotifySubDeleted"] = "Abonnement gelöscht",
            ["NotifySubDeviceLimit"] = "Gerätelimit für dieses Abo erreicht",
            ["NotifySubLoadErrorFmt"] = "Fehler beim Laden des Abos ({0})",
            ["NotifySubAppUnsupported"] = "Anfrage abgelehnt (App not supported). Gerätelimit prüfen.",
            ["NotifySubNoProfiles"] = "Abonnement enthält keine unterstützten Profile",
            ["NotifySubUpdatedCountFmt"] = "Abo aktualisiert (Server geladen: {0})",
            ["NotifySubErrorFmt"] = "Fehler bei Abo-Aktualisierung: {0}",
            ["NotifyRoutesReconnecting"] = "Routen geändert, verbinde neu...",
            ["NotifyLogLevelReconnecting"] = "Protokolllevel geändert, verbinde neu...",
            ["NotifyReconnectingServer"] = "Server gewechselt, verbinde neu...",
            ["NotifyModeReconnecting"] = "Modus geändert, verbinde neu...",
            ["NotifySettingsReconnecting"] = "Einstellungen geändert, verbinde neu...",
            ["NotifyProfileUpdatedReconnecting"] = "Profil aktualisiert, verbinde neu...",
            ["NotifyPortBusyFallbackFmt"] = "Port {0} ist belegt, verwende Port {1}"
        },
        ["zh-CN"] = new()
        {
            ["NavMain"] = "首页",
            ["NavSubs"] = "订阅",
            ["NavSettings"] = "设置",
            ["NavLogs"] = "日志",
            ["ModeLabel"] = "工作模式:",
            ["BtnAddProfile"] = "添加配置",
            ["BtnRefreshPing"] = "刷新延迟",
            ["SearchPlaceholder"] = "搜索节点...",
            ["EmptyProfilesTitle"] = "暂无配置",
            ["EmptyProfilesDesc"] = "点击“添加配置”开始使用",
            ["BtnAddSub"] = "添加订阅",
            ["BtnUpdateAllSubs"] = "更新全部",
            ["EmptySubsTitle"] = "暂无订阅",
            ["EmptySubsDesc"] = "点击“添加订阅”获取节点列表",
            ["SettingsTitle"] = "设置",
            ["GroupLaunch"] = "常规启动",
            ["AutoStart"] = "开机自启",
            ["StartMinimized"] = "启动后最小化到托盘",
            ["MinimizeToTray"] = "关闭时最小化到托盘",
            ["AutoUpdateSubs"] = "启动时自动更新订阅",
            ["CustomConfig"] = "使用自定义 config.json",
            ["GroupAppearance"] = "外观",
            ["LangLabel"] = "界面语言 (Language)",
            ["ThemeLabel"] = "主题",
            ["ThemeSystem"] = "跟随系统",
            ["ThemeDark"] = "暗色",
            ["ThemeLight"] = "亮色",
            ["VisualEffects"] = "开启视觉特效",
            ["DisableAnimations"] = "禁用过渡动画",
            ["GroupNetwork"] = "网络与分流",
            ["DnsLabel"] = "远程 DNS",
            ["DirectDnsLabel"] = "直连 DNS (Direct)",
            ["BtnRouting"] = "配置分流规则",
            ["GroupAdvanced"] = "高级选项",
            ["ProxyPortLabel"] = "本地代理端口",
            ["TunStackLabel"] = "TUN 协议栈",
            ["StrictRoute"] = "TUN 严格路由 (Strict Route)",
            ["EnableIpv6"] = "启用 IPv6 (双栈)",
            ["AllowLan"] = "允许局域网连接",
            ["AllowLanDesc"] = "允许同一 Wi-Fi 网络下的其他设备连接代理。",
            ["LogsTitle"] = "核心日志",
            ["LogLevelLabel"] = "级别: ",
            ["AutoScroll"] = "自动滚动",
            ["BtnCopyLogs"] = "复制",
            ["BtnClearLogs"] = "清空",
            ["ImportTitle"] = "添加配置",
            ["EditTitle"] = "编辑配置",
            ["PasteImport"] = "从剪贴板导入",
            ["OrManual"] = "或手动添加:",
            ["ProtocolLabel"] = "协议",
            ["Cancel"] = "取消",
            ["Save"] = "保存",
            ["AddSubTitle"] = "添加订阅",
            ["SubNameLabel"] = "名称 (可选)",
            ["SubUrlLabel"] = "订阅链接 (URL)",
            ["Add"] = "添加",
            ["RoutingTitle"] = "分流规则",
            ["BypassRu"] = "不代理俄罗斯网站 (.ru, .рф)",
            ["RouteModeLabel"] = "分流模式:",
            ["RouteModeBypass"] = "绕过列表中的项目...",
            ["RouteModeOnly"] = "仅代理列表中的项目...",
            ["DomainsLabel"] = "网站与域名 (如 google.com)",
            ["AppsLabel"] = "应用程序 (如 telegram.exe)",
            ["Done"] = "完成",
            ["QRTitle"] = "配置二维码",
            ["CopyLink"] = "复制链接",
            ["CtxConnect"] = "连接",
            ["CtxUrlTest"] = "HTTP 延迟测试",
            ["CtxEdit"] = "编辑配置",
            ["CtxCopy"] = "复制链接",
            ["CtxUp"] = "上移",
            ["CtxDown"] = "下移",
            ["CtxDelete"] = "删除",
            ["TrafficInfinite"] = "流量: ∞",
            ["ExpiresFmt"] = "到期: {0}",
            ["UpdatedFmt"] = "已更新: {0}",
            ["NeverUpdated"] = "尚未更新",
            ["TooltipPing"] = "测试延迟",
            ["TooltipUpdate"] = "更新",
            ["TooltipDelete"] = "删除",
            ["PingMs"] = "ms",
            ["PingTimeout"] = "超时",
            ["PingError"] = "错误",
            ["UnknownLocation"] = "未知",
            ["TrayModeFmt"] = "模式: {0}",
            ["TrayExit"] = "退出",
            ["TrayConnectVpn"] = "连接 VPN",
            ["TrayDisconnectVpn"] = "断开 VPN",
            ["TrayConnectProxy"] = "连接 Proxy",
            ["TrayDisconnectProxy"] = "断开 Proxy",
            ["TrayBgTip"] = "应用程序正在后台运行。",
            ["BtnConnectVpn"] = "连接 VPN",
            ["BtnDisconnectVpn"] = "断开 VPN",
            ["BtnConnectProxy"] = "连接 PROXY",
            ["BtnDisconnectProxy"] = "断开 PROXY",
            ["AutoConnectVpn"] = "启动时自动连接 VPN",
            ["AutoConnectProxy"] = "启动时自动连接 Proxy",
            ["FieldName"] = "配置名称",
            ["FieldAddress"] = "服务器地址 (IP 或域名)",
            ["FieldPort"] = "端口",
            ["FieldPassword"] = "密码 (Password)",
            ["FieldUuid"] = "UUID / 用户名",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint (指纹)",
            ["FieldInsecure"] = "跳过 TLS 证书验证",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "传输协议 (tcp, ws...)",
            ["FieldPath"] = "路径 (Path)",
            ["FieldHost"] = "伪装域名 (Host)",
            ["FieldSecurity"] = "加密方式 (Security)",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "加密方法",
            ["FieldPublicKey"] = "公钥 (Public Key)",
            ["FieldPrivateKey"] = "私钥 (Private Key)",
            ["FieldLocalAddress"] = "本地地址 (Local Address)",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "上行 Mbps",
            ["FieldDown"] = "下行 Mbps",
            ["FieldObfs"] = "混淆类型 (Obfs)",
            ["FieldObfsPassword"] = "混淆密码",
            ["NotifySelectProfile"] = "请先选择一个配置！",
            ["NotifyAdminRequired"] = "需要管理员权限！",
            ["NotifyWintunMissing"] = "错误：未找到 wintun.dll！",
            ["NotifyCoreExited"] = "Sing-box 核心异常退出，请查看日志。",
            ["NotifyCopied"] = "已复制到剪贴板",
            ["NotifyImported"] = "配置导入成功",
            ["NotifyImportError"] = "错误：剪贴板中没有有效配置",
            ["NotifyInvalidAddress"] = "请输入有效的服务器地址",
            ["NotifyInvalidPort"] = "请输入有效的端口 (1-65535)",
            ["NotifyQrTooLong"] = "无法生成二维码：链接过长",
            ["NotifyQrError"] = "生成二维码时出错",
            ["NotifySubInvalidUrl"] = "请输入有效的订阅链接",
            ["NotifySubLoading"] = "正在加载订阅节点...",
            ["NotifySubAllUpdating"] = "正在更新全部订阅...",
            ["NotifySubAllUpdated"] = "全部订阅已更新",
            ["NotifySubDeleted"] = "订阅已删除",
            ["NotifySubDeviceLimit"] = "已达到该订阅的设备数量上限",
            ["NotifySubLoadErrorFmt"] = "加载订阅失败 ({0})",
            ["NotifySubAppUnsupported"] = "服务器拒绝请求 (App not supported)，请检查设备上限。",
            ["NotifySubNoProfiles"] = "订阅中不包含受支持的节点",
            ["NotifySubUpdatedCountFmt"] = "订阅已更新 (已加载节点: {0})",
            ["NotifySubErrorFmt"] = "更新订阅失败: {0}",
            ["NotifyRoutesReconnecting"] = "分流规则已修改，正在重连...",
            ["NotifyLogLevelReconnecting"] = "日志级别已修改，正在重连...",
            ["NotifyReconnectingServer"] = "切换节点，正在重连...",
            ["NotifyModeReconnecting"] = "工作模式已修改，正在重连...",
            ["NotifySettingsReconnecting"] = "设置已修改，正在重连...",
            ["NotifyProfileUpdatedReconnecting"] = "配置已更新，正在重连...",
            ["NotifyPortBusyFallbackFmt"] = "端口 {0} 已被占用，改用端口 {1}"
        },
        ["zh-TW"] = new()
        {
            ["NavMain"] = "首頁",
            ["NavSubs"] = "訂閱",
            ["NavSettings"] = "設定",
            ["NavLogs"] = "日誌",
            ["ModeLabel"] = "運作模式:",
            ["BtnAddProfile"] = "新增設定檔",
            ["BtnRefreshPing"] = "重新整理延遲",
            ["SearchPlaceholder"] = "搜尋節點...",
            ["EmptyProfilesTitle"] = "尚無設定檔",
            ["EmptyProfilesDesc"] = "點擊「新增設定檔」開始使用",
            ["BtnAddSub"] = "新增訂閱",
            ["BtnUpdateAllSubs"] = "更新全部",
            ["EmptySubsTitle"] = "尚無訂閱",
            ["EmptySubsDesc"] = "點擊「新增訂閱」獲取節點清單",
            ["SettingsTitle"] = "設定",
            ["GroupLaunch"] = "啟動設定",
            ["AutoStart"] = "開機自動啟動",
            ["StartMinimized"] = "啟動時最小化至系統匣",
            ["MinimizeToTray"] = "關閉時最小化至系統匣",
            ["AutoUpdateSubs"] = "啟動時自動更新訂閱",
            ["CustomConfig"] = "使用自訂 config.json",
            ["GroupAppearance"] = "外觀",
            ["LangLabel"] = "介面語言 (Language)",
            ["ThemeLabel"] = "主題",
            ["ThemeSystem"] = "跟隨系統",
            ["ThemeDark"] = "深色",
            ["ThemeLight"] = "淺色",
            ["VisualEffects"] = "啟用視覺效果",
            ["DisableAnimations"] = "停用轉場動畫",
            ["GroupNetwork"] = "網路與分流",
            ["DnsLabel"] = "遠端 DNS",
            ["DirectDnsLabel"] = "直連 DNS (Direct)",
            ["BtnRouting"] = "設定分流規則",
            ["GroupAdvanced"] = "進階選項",
            ["ProxyPortLabel"] = "本機代理連接埠",
            ["TunStackLabel"] = "TUN 網路堆疊",
            ["StrictRoute"] = "TUN 嚴格路由 (Strict Route)",
            ["EnableIpv6"] = "啟用 IPv6 (雙協定)",
            ["AllowLan"] = "允許區域網路連線",
            ["AllowLanDesc"] = "允許同 Wi-Fi 網路內的其他裝置連線代理。",
            ["LogsTitle"] = "核心日誌",
            ["LogLevelLabel"] = "層級: ",
            ["AutoScroll"] = "自動捲動",
            ["BtnCopyLogs"] = "複製",
            ["BtnClearLogs"] = "清除",
            ["ImportTitle"] = "新增設定檔",
            ["EditTitle"] = "編輯設定檔",
            ["PasteImport"] = "自剪貼簿貼上",
            ["OrManual"] = "或手動新增:",
            ["ProtocolLabel"] = "通訊協定",
            ["Cancel"] = "取消",
            ["Save"] = "儲存",
            ["AddSubTitle"] = "新增訂閱",
            ["SubNameLabel"] = "名稱 (選填)",
            ["SubUrlLabel"] = "訂閱網址 (URL)",
            ["Add"] = "新增",
            ["RoutingTitle"] = "分流規則",
            ["BypassRu"] = "不代理俄羅斯網站 (.ru, .рф)",
            ["RouteModeLabel"] = "分流模式:",
            ["RouteModeBypass"] = "略過清單中的項目...",
            ["RouteModeOnly"] = "僅代理清單中的項目...",
            ["DomainsLabel"] = "網站與網域 (例如 google.com)",
            ["AppsLabel"] = "應用程式 (例如 telegram.exe)",
            ["Done"] = "完成",
            ["QRTitle"] = "設定檔 QR Code",
            ["CopyLink"] = "複製連結",
            ["CtxConnect"] = "連線",
            ["CtxUrlTest"] = "HTTP 延遲測試",
            ["CtxEdit"] = "編輯設定檔",
            ["CtxCopy"] = "複製連結",
            ["CtxUp"] = "上移",
            ["CtxDown"] = "下移",
            ["CtxDelete"] = "刪除",
            ["TrafficInfinite"] = "流量: ∞",
            ["ExpiresFmt"] = "到期: {0}",
            ["UpdatedFmt"] = "已更新: {0}",
            ["NeverUpdated"] = "尚未更新",
            ["TooltipPing"] = "測試延遲",
            ["TooltipUpdate"] = "更新",
            ["TooltipDelete"] = "刪除",
            ["PingMs"] = "ms",
            ["PingTimeout"] = "逾時",
            ["PingError"] = "錯誤",
            ["UnknownLocation"] = "未知",
            ["TrayModeFmt"] = "模式: {0}",
            ["TrayExit"] = "結束",
            ["TrayConnectVpn"] = "連線 VPN",
            ["TrayDisconnectVpn"] = "中斷 VPN",
            ["TrayConnectProxy"] = "連線 Proxy",
            ["TrayDisconnectProxy"] = "中斷 Proxy",
            ["TrayBgTip"] = "應用程式正在背景執行。",
            ["BtnConnectVpn"] = "連線 VPN",
            ["BtnDisconnectVpn"] = "中斷 VPN",
            ["BtnConnectProxy"] = "連線 PROXY",
            ["BtnDisconnectProxy"] = "中斷 PROXY",
            ["AutoConnectVpn"] = "啟動時自動連線 VPN",
            ["AutoConnectProxy"] = "啟動時自動連線 Proxy",
            ["FieldName"] = "設定檔名稱",
            ["FieldAddress"] = "伺服器位址 (IP 或網域)",
            ["FieldPort"] = "連接埠",
            ["FieldPassword"] = "密碼 (Password)",
            ["FieldUuid"] = "UUID / 使用者名稱",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint (指紋)",
            ["FieldInsecure"] = "忽略 TLS 憑證錯誤",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "傳輸協定 (tcp, ws...)",
            ["FieldPath"] = "路徑 (Path)",
            ["FieldHost"] = "偽裝網域 (Host)",
            ["FieldSecurity"] = "加密方式 (Security)",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "加密方法",
            ["FieldPublicKey"] = "公鑰 (Public Key)",
            ["FieldPrivateKey"] = "私鑰 (Private Key)",
            ["FieldLocalAddress"] = "本機位址 (Local Address)",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "上傳 Mbps",
            ["FieldDown"] = "下載 Mbps",
            ["FieldObfs"] = "混淆類型 (Obfs)",
            ["FieldObfsPassword"] = "混淆密碼",
            ["NotifySelectProfile"] = "請先選擇一個設定檔！",
            ["NotifyAdminRequired"] = "需要系統管理員權限！",
            ["NotifyWintunMissing"] = "錯誤：找不到 wintun.dll！",
            ["NotifyCoreExited"] = "Sing-box 核心異常結束，請檢查日誌。",
            ["NotifyCopied"] = "已複製到剪貼簿",
            ["NotifyImported"] = "設定檔匯入成功",
            ["NotifyImportError"] = "錯誤：剪貼簿中沒有有效的設定檔",
            ["NotifyInvalidAddress"] = "請輸入有效的伺服器位址",
            ["NotifyInvalidPort"] = "請輸入有效的連接埠 (1-65535)",
            ["NotifyQrTooLong"] = "無法產生 QR Code：連結過長",
            ["NotifyQrError"] = "產生 QR Code 時發生錯誤",
            ["NotifySubInvalidUrl"] = "請輸入有效的訂閱網址",
            ["NotifySubLoading"] = "正在載入訂閱節點...",
            ["NotifySubAllUpdating"] = "正在更新全部訂閱...",
            ["NotifySubAllUpdated"] = "已更新全部訂閱",
            ["NotifySubDeleted"] = "訂閱已刪除",
            ["NotifySubDeviceLimit"] = "已達到此訂閱的裝置數量上限",
            ["NotifySubLoadErrorFmt"] = "載入訂閱失敗 ({0})",
            ["NotifySubAppUnsupported"] = "伺服器拒絕請求 (App not supported)，請檢查裝置上限。",
            ["NotifySubNoProfiles"] = "訂閱中不包含支援的節點",
            ["NotifySubUpdatedCountFmt"] = "訂閱已更新 (已載入節點: {0})",
            ["NotifySubErrorFmt"] = "更新訂閱失敗: {0}",
            ["NotifyRoutesReconnecting"] = "分流規則已變更，正在重新連線...",
            ["NotifyLogLevelReconnecting"] = "日誌層級已變更，正在重新連線...",
            ["NotifyReconnectingServer"] = "切換節點，正在重新連線...",
            ["NotifyModeReconnecting"] = "運作模式已變更，正在重新連線...",
            ["NotifySettingsReconnecting"] = "設定已變更，正在重新連線...",
            ["NotifyProfileUpdatedReconnecting"] = "設定檔已更新，正在重新連線...",
            ["NotifyPortBusyFallbackFmt"] = "連接埠 {0} 已被佔用，改用連接埠 {1}"
        },
        ["ja"] = new()
        {
            ["NavMain"] = "ホーム",
            ["NavSubs"] = "サブスク",
            ["NavSettings"] = "設定",
            ["NavLogs"] = "ログ",
            ["ModeLabel"] = "動作モード:",
            ["BtnAddProfile"] = "プロファイル追加",
            ["BtnRefreshPing"] = "Ping更新",
            ["SearchPlaceholder"] = "サーバー検索...",
            ["EmptyProfilesTitle"] = "プロファイルがありません",
            ["EmptyProfilesDesc"] = "「プロファイル追加」をクリックして開始",
            ["BtnAddSub"] = "サブスク追加",
            ["BtnUpdateAllSubs"] = "すべて更新",
            ["EmptySubsTitle"] = "サブスクリプションがありません",
            ["EmptySubsDesc"] = "「サブスク追加」をクリックしてサーバーを取得",
            ["SettingsTitle"] = "設定",
            ["GroupLaunch"] = "起動と動作",
            ["AutoStart"] = "Windows起動時に実行",
            ["StartMinimized"] = "トレイに最小化して起動",
            ["MinimizeToTray"] = "閉じる時にトレイへ最小化",
            ["AutoUpdateSubs"] = "起動時にサブスクを自動更新",
            ["CustomConfig"] = "独自の config.json を使用",
            ["GroupAppearance"] = "外観",
            ["LangLabel"] = "言語 (Language)",
            ["ThemeLabel"] = "テーマ",
            ["ThemeSystem"] = "システム",
            ["ThemeDark"] = "ダーク",
            ["ThemeLight"] = "ライト",
            ["VisualEffects"] = "視覚効果を有効にする",
            ["DisableAnimations"] = "アニメーションを無効にする",
            ["GroupNetwork"] = "ネットワークとルーティング",
            ["DnsLabel"] = "リモートDNS",
            ["DirectDnsLabel"] = "ダイレクトDNS",
            ["BtnRouting"] = "ルーティングルールの設定",
            ["GroupAdvanced"] = "詳細設定",
            ["ProxyPortLabel"] = "ローカルプロキシポート",
            ["TunStackLabel"] = "TUNスタック",
            ["StrictRoute"] = "TUN Strict Route (厳格ルーティング)",
            ["EnableIpv6"] = "IPv6 を有効にする (Dual-Stack)",
            ["AllowLan"] = "LAN接続を許可",
            ["AllowLanDesc"] = "同じWi-Fi内の他のデバイスからの接続を許可します。",
            ["LogsTitle"] = "コアログ",
            ["LogLevelLabel"] = "レベル: ",
            ["AutoScroll"] = "自動スクロール",
            ["BtnCopyLogs"] = "コピー",
            ["BtnClearLogs"] = "消去",
            ["ImportTitle"] = "プロファイルの追加",
            ["EditTitle"] = "プロファイルを編集",
            ["PasteImport"] = "クリップボードから貼り付け",
            ["OrManual"] = "または手動で追加:",
            ["ProtocolLabel"] = "プロトコル",
            ["Cancel"] = "キャンセル",
            ["Save"] = "保存",
            ["AddSubTitle"] = "サブスク追加",
            ["SubNameLabel"] = "名前 (省略可)",
            ["SubUrlLabel"] = "サブスクリプションURL",
            ["Add"] = "追加",
            ["RoutingTitle"] = "ルーティング規則",
            ["BypassRu"] = "ロシアのサイトをプロキシしない (.ru, .рф)",
            ["RouteModeLabel"] = "ルールモード:",
            ["RouteModeBypass"] = "リスト以外をすべてプロキシ...",
            ["RouteModeOnly"] = "リストのみプロキシ...",
            ["DomainsLabel"] = "ドメイン (例: google.com)",
            ["AppsLabel"] = "アプリ (例: telegram.exe)",
            ["Done"] = "完了",
            ["QRTitle"] = "QRコード",
            ["CopyLink"] = "リンクをコピー",
            ["CtxConnect"] = "接続",
            ["CtxUrlTest"] = "接続テスト (HTTP)",
            ["CtxEdit"] = "プロファイルを編集",
            ["CtxCopy"] = "リンクをコピー",
            ["CtxUp"] = "上に移動",
            ["CtxDown"] = "下に移動",
            ["CtxDelete"] = "削除",
            ["TrafficInfinite"] = "通信量: ∞",
            ["ExpiresFmt"] = "有効期限: {0}",
            ["UpdatedFmt"] = "更新日時: {0}",
            ["NeverUpdated"] = "未更新",
            ["TooltipPing"] = "Ping測定",
            ["TooltipUpdate"] = "更新",
            ["TooltipDelete"] = "削除",
            ["PingMs"] = "ms",
            ["PingTimeout"] = "タイムアウト",
            ["PingError"] = "エラー",
            ["UnknownLocation"] = "不明",
            ["TrayModeFmt"] = "モード: {0}",
            ["TrayExit"] = "終了",
            ["TrayConnectVpn"] = "VPN接続",
            ["TrayDisconnectVpn"] = "VPN切断",
            ["TrayConnectProxy"] = "Proxy接続",
            ["TrayDisconnectProxy"] = "Proxy切断",
            ["TrayBgTip"] = "バックグラウンドで実行中です。",
            ["BtnConnectVpn"] = "VPNに接続",
            ["BtnDisconnectVpn"] = "VPNを切断",
            ["BtnConnectProxy"] = "PROXYに接続",
            ["BtnDisconnectProxy"] = "PROXYを切断",
            ["AutoConnectVpn"] = "起動時に自動でVPNに接続",
            ["AutoConnectProxy"] = "起動時に自動でProxyに接続",
            ["FieldName"] = "プロファイル名",
            ["FieldAddress"] = "アドレス (IPまたはドメイン)",
            ["FieldPort"] = "ポート",
            ["FieldPassword"] = "パスワード (Password)",
            ["FieldUuid"] = "UUID / ユーザー名",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint",
            ["FieldInsecure"] = "TLSエラーを無視",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "Transport (tcp, ws...)",
            ["FieldPath"] = "パス (Path)",
            ["FieldHost"] = "ホスト (Host)",
            ["FieldSecurity"] = "Security",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "暗号化方式",
            ["FieldPublicKey"] = "公開鍵 (Public Key)",
            ["FieldPrivateKey"] = "秘密鍵 (Private Key)",
            ["FieldLocalAddress"] = "ローカルアドレス",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "上り Mbps",
            ["FieldDown"] = "下り Mbps",
            ["FieldObfs"] = "Obfsタイプ",
            ["FieldObfsPassword"] = "Obfsパスワード",
            ["NotifySelectProfile"] = "先にプロファイルを選択してください！",
            ["NotifyAdminRequired"] = "管理者権限が必要です！",
            ["NotifyWintunMissing"] = "エラー: wintun.dll が見つかりません！",
            ["NotifyCoreExited"] = "Sing-boxコアがエラー終了しました。ログを確認してください。",
            ["NotifyCopied"] = "クリップボードにコピーしました",
            ["NotifyImported"] = "プロファイルをインポートしました",
            ["NotifyImportError"] = "エラー: 有効なプロファイルがありません",
            ["NotifyInvalidAddress"] = "有効なサーバーアドレスを入力してください",
            ["NotifyInvalidPort"] = "有効なポートを入力してください (1-65535)",
            ["NotifyQrTooLong"] = "QRコードを生成できません: リンクが長すぎます",
            ["NotifyQrError"] = "QRコードの生成中にエラーが発生しました",
            ["NotifySubInvalidUrl"] = "有効なサブスクリプションURLを入力してください",
            ["NotifySubLoading"] = "サブスクリプションを読み込み中...",
            ["NotifySubAllUpdating"] = "すべてのサブスクを更新中...",
            ["NotifySubAllUpdated"] = "すべてのサブスクを更新しました",
            ["NotifySubDeleted"] = "サブスクを削除しました",
            ["NotifySubDeviceLimit"] = "このサブスクのデバイス上限に達しました",
            ["NotifySubLoadErrorFmt"] = "サブスクの読み込みエラー ({0})",
            ["NotifySubAppUnsupported"] = "リクエストが拒否されました (App not supported)。デバイス数を確認してください。",
            ["NotifySubNoProfiles"] = "対応するプロファイルが含まれていません",
            ["NotifySubUpdatedCountFmt"] = "サブスクを更新しました (サーバー数: {0})",
            ["NotifySubErrorFmt"] = "サブスク更新エラー: {0}",
            ["NotifyRoutesReconnecting"] = "ルートを変更しました、再接続中...",
            ["NotifyLogLevelReconnecting"] = "ログレベルを変更しました、再接続中...",
            ["NotifyReconnectingServer"] = "サーバーを切り替え中、再接続中...",
            ["NotifyModeReconnecting"] = "モードを変更しました、再接続中...",
            ["NotifySettingsReconnecting"] = "設定を変更しました、再接続中...",
            ["NotifyProfileUpdatedReconnecting"] = "プロファイルを更新しました、再接続中...",
            ["NotifyPortBusyFallbackFmt"] = "ポート {0} は使用中のため、ポート {1} を使用します"
        },
        ["fa"] = new()
        {
            ["NavMain"] = "اصلی",
            ["NavSubs"] = "اشتراک‌ها",
            ["NavSettings"] = "تنظیمات",
            ["NavLogs"] = "گزارش‌ها",
            ["ModeLabel"] = "حالت کارکرد:",
            ["BtnAddProfile"] = "افزودن پروفایل",
            ["BtnRefreshPing"] = "بروزرسانی پینگ",
            ["SearchPlaceholder"] = "جستجوی سرورها...",
            ["EmptyProfilesTitle"] = "پروفایلی وجود ندارد",
            ["EmptyProfilesDesc"] = "روی «افزودن پروفایل» کلیک کنید",
            ["BtnAddSub"] = "افزودن اشتراک",
            ["BtnUpdateAllSubs"] = "بروزرسانی همه",
            ["EmptySubsTitle"] = "اشتراکی وجود ندارد",
            ["EmptySubsDesc"] = "روی «افزودن اشتراک» کلیک کنید",
            ["SettingsTitle"] = "تنظیمات",
            ["GroupLaunch"] = "راه‌اندازی و اجرا",
            ["AutoStart"] = "اجرا با ویندوز",
            ["StartMinimized"] = "اجرا به صورت کوچک در پس‌زمینه",
            ["MinimizeToTray"] = "کوچک کردن در پس‌زمینه هنگام بستن",
            ["AutoUpdateSubs"] = "بروزرسانی خودکار اشتراک‌ها هنگام اجرا",
            ["CustomConfig"] = "استفاده از config.json شخصی",
            ["GroupAppearance"] = "ظاهر",
            ["LangLabel"] = "زبان رابط کاربری (Language)",
            ["ThemeLabel"] = "پوسته",
            ["ThemeSystem"] = "سیستم",
            ["ThemeDark"] = "تیره",
            ["ThemeLight"] = "روشن",
            ["VisualEffects"] = "فعال‌سازی جلوه‌های بصری",
            ["DisableAnimations"] = "غیرفعال‌سازی انیمیشن‌ها",
            ["GroupNetwork"] = "شبکه و مسیریابی",
            ["DnsLabel"] = "دی‌ان‌اس راه دور (Remote)",
            ["DirectDnsLabel"] = "دی‌ان‌اس مستقیم (Direct)",
            ["BtnRouting"] = "تنظیم قوانین مسیریابی",
            ["GroupAdvanced"] = "تنظیمات پیشرفته",
            ["ProxyPortLabel"] = "پورت پروکسی محلی",
            ["TunStackLabel"] = "استک شبکه TUN",
            ["StrictRoute"] = "مسیریابی دقیق TUN (Strict Route)",
            ["EnableIpv6"] = "فعال‌سازی IPv6 (Dual-Stack)",
            ["AllowLan"] = "اجازه به شبکه محلی (LAN)",
            ["AllowLanDesc"] = "امکان اتصال سایر دستگاه‌های شبکه محلی به پروکسی.",
            ["LogsTitle"] = "گزارش‌های هسته",
            ["LogLevelLabel"] = "سطح: ",
            ["AutoScroll"] = "اسکرول خودکار",
            ["BtnCopyLogs"] = "کپی",
            ["BtnClearLogs"] = "پاک‌سازی",
            ["ImportTitle"] = "افزودن سرور",
            ["EditTitle"] = "ویرایش سرور",
            ["PasteImport"] = "جای‌گذاری از کلیپ‌بورد",
            ["OrManual"] = "یا افزودن دستی:",
            ["ProtocolLabel"] = "پروتکل",
            ["Cancel"] = "لغو",
            ["Save"] = "ذخیره",
            ["AddSubTitle"] = "افزودن اشتراک",
            ["SubNameLabel"] = "نام (اختیاری)",
            ["SubUrlLabel"] = "لینک اشتراک (URL)",
            ["Add"] = "افزودن",
            ["RoutingTitle"] = "قوانین مسیریابی",
            ["BypassRu"] = "عبور مستقیم سایت‌های روسی (.ru, .рф)",
            ["RouteModeLabel"] = "حالت قوانین:",
            ["RouteModeBypass"] = "فعال برای همه به جز...",
            ["RouteModeOnly"] = "فعال فقط برای...",
            ["DomainsLabel"] = "دامنه‌ها (مانند google.com)",
            ["AppsLabel"] = "برنامه‌ها (مانند telegram.exe)",
            ["Done"] = "انجام شد",
            ["QRTitle"] = "کد QR سرور",
            ["CopyLink"] = "کپی لینک",
            ["CtxConnect"] = "اتصال",
            ["CtxUrlTest"] = "تست اتصال (HTTP)",
            ["CtxEdit"] = "ویرایش سرور",
            ["CtxCopy"] = "کپی لینک",
            ["CtxUp"] = "انتقال به بالا",
            ["CtxDown"] = "انتقال به پایین",
            ["CtxDelete"] = "حذف",
            ["TrafficInfinite"] = "ترافیک: ∞",
            ["ExpiresFmt"] = "انقضا: {0}",
            ["UpdatedFmt"] = "بروزرسانی: {0}",
            ["NeverUpdated"] = "هنوز بروزرسانی نشده",
            ["TooltipPing"] = "تست پینگ",
            ["TooltipUpdate"] = "بروزرسانی",
            ["TooltipDelete"] = "حذف",
            ["PingMs"] = "ms",
            ["PingTimeout"] = "اتمام زمان",
            ["PingError"] = "خطا",
            ["UnknownLocation"] = "نامشخص",
            ["TrayModeFmt"] = "حالت: {0}",
            ["TrayExit"] = "خروج",
            ["TrayConnectVpn"] = "اتصال VPN",
            ["TrayDisconnectVpn"] = "قطع VPN",
            ["TrayConnectProxy"] = "اتصال Proxy",
            ["TrayDisconnectProxy"] = "قطع Proxy",
            ["TrayBgTip"] = "برنامه در پس‌زمینه در حال اجرا است.",
            ["BtnConnectVpn"] = "اتصال VPN",
            ["BtnDisconnectVpn"] = "قطع VPN",
            ["BtnConnectProxy"] = "اتصال PROXY",
            ["BtnDisconnectProxy"] = "قطع PROXY",
            ["AutoConnectVpn"] = "اتصال خودکار VPN هنگام اجرا",
            ["AutoConnectProxy"] = "اتصال خودکار Proxy هنگام اجرا",
            ["FieldName"] = "نام پروفایل",
            ["FieldAddress"] = "آدرس (IP یا دامنه)",
            ["FieldPort"] = "پورت",
            ["FieldPassword"] = "رمز عبور (Password)",
            ["FieldUuid"] = "UUID / نام کاربری",
            ["FieldSni"] = "SNI",
            ["FieldFp"] = "Fingerprint",
            ["FieldInsecure"] = "نادیده گرفتن خطای TLS",
            ["FieldFlow"] = "Flow",
            ["FieldPbk"] = "Reality PBK",
            ["FieldSid"] = "Reality SID",
            ["FieldTransport"] = "Transport (tcp, ws...)",
            ["FieldPath"] = "مسیر (Path)",
            ["FieldHost"] = "Host",
            ["FieldSecurity"] = "Security",
            ["FieldAlterId"] = "AlterID",
            ["FieldMethod"] = "روش رمزنگاری",
            ["FieldPublicKey"] = "کلید عمومی (Public Key)",
            ["FieldPrivateKey"] = "کلید خصوصی (Private Key)",
            ["FieldLocalAddress"] = "آدرس محلی",
            ["FieldMtu"] = "MTU",
            ["FieldAlpn"] = "ALPN",
            ["FieldUp"] = "آپلود Mbps",
            ["FieldDown"] = "دانلود Mbps",
            ["FieldObfs"] = "نوع Obfs",
            ["FieldObfsPassword"] = "رمز Obfs",
            ["NotifySelectProfile"] = "لطفاً ابتدا یک سرور را انتخاب کنید!",
            ["NotifyAdminRequired"] = "دسترسی ادمین مورد نیاز است!",
            ["NotifyWintunMissing"] = "خطا: فایل wintun.dll یافت نشد!",
            ["NotifyCoreExited"] = "هسته Sing-box با خطا متوقف شد. گزارش‌ها را بررسی کنید.",
            ["NotifyCopied"] = "در کلیپ‌بورد کپی شد",
            ["NotifyImported"] = "سرور با موفقیت اضافه شد",
            ["NotifyImportError"] = "خطا: سروری در کلیپ‌بورد یافت نشد",
            ["NotifyInvalidAddress"] = "آدرس معتبر وارد کنید",
            ["NotifyInvalidPort"] = "پورت معتبر وارد کنید (1-65535)",
            ["NotifyQrTooLong"] = "امکان ساخت QR نیست: لینک بیش از حد طولانی است",
            ["NotifyQrError"] = "خطا در ساخت کد QR",
            ["NotifySubInvalidUrl"] = "لینک اشتراک معتبر وارد کنید",
            ["NotifySubLoading"] = "در حال دریافت سرورهای اشتراک...",
            ["NotifySubAllUpdating"] = "در حال بروزرسانی همه اشتراک‌ها...",
            ["NotifySubAllUpdated"] = "همه اشتراک‌ها بروزرسانی شدند",
            ["NotifySubDeleted"] = "اشتراک حذف شد",
            ["NotifySubDeviceLimit"] = "محدودیت دستگاه برای این اشتراک تکمیل شده است",
            ["NotifySubLoadErrorFmt"] = "خطا در دریافت اشتراک ({0})",
            ["NotifySubAppUnsupported"] = "درخواست رد شد (App not supported). محدودیت دستگاه را بررسی کنید.",
            ["NotifySubNoProfiles"] = "اشتراک هیچ سرور پشتیبانی‌شده‌ای ندارد",
            ["NotifySubUpdatedCountFmt"] = "اشتراک بروزرسانی شد (تعداد سرورها: {0})",
            ["NotifySubErrorFmt"] = "خطا در بروزرسانی اشتراک: {0}",
            ["NotifyRoutesReconnecting"] = "قوانین تغییر کرد، در حال اتصال مجدد...",
            ["NotifyLogLevelReconnecting"] = "سطح گزارش تغییر کرد، در حال اتصال مجدد...",
            ["NotifyReconnectingServer"] = "تغییر سرور، در حال اتصال مجدد...",
            ["NotifyModeReconnecting"] = "حالت کارکرد تغییر کرد، در حال اتصال مجدد...",
            ["NotifySettingsReconnecting"] = "تنظیمات تغییر کرد، در حال اتصال مجدد...",
            ["NotifyProfileUpdatedReconnecting"] = "پروفایل ویرایش شد، در حال اتصال مجدد...",
            ["NotifyPortBusyFallbackFmt"] = "پورت {0} اشغال است، از پورت {1} استفاده می‌شود"
        }
    };

    public static string L(string key)
    {
        if (_translations.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            return val;
        if (_translations["ru"].TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> _countryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = new() { ["ru"] = "США", ["en"] = "United States", ["uk"] = "США", ["de"] = "USA", ["zh-CN"] = "美国", ["zh-TW"] = "美國", ["ja"] = "アメリカ", ["fa"] = "ایالات متحده" },
        ["GB"] = new() { ["ru"] = "Великобритания", ["en"] = "United Kingdom", ["uk"] = "Велика Британія", ["de"] = "Vereinigtes Königreich", ["zh-CN"] = "英国", ["zh-TW"] = "英國", ["ja"] = "イギリス", ["fa"] = "بریتانیا" },
        ["DE"] = new() { ["ru"] = "Германия", ["en"] = "Germany", ["uk"] = "Німеччина", ["de"] = "Deutschland", ["zh-CN"] = "德国", ["zh-TW"] = "德國", ["ja"] = "ドイツ", ["fa"] = "آلمان" },
        ["NL"] = new() { ["ru"] = "Нидерланды", ["en"] = "Netherlands", ["uk"] = "Нідерланди", ["de"] = "Niederlande", ["zh-CN"] = "荷兰", ["zh-TW"] = "荷蘭", ["ja"] = "オランダ", ["fa"] = "هلند" },
        ["FI"] = new() { ["ru"] = "Финляндия", ["en"] = "Finland", ["uk"] = "Фінляндія", ["de"] = "Finnland", ["zh-CN"] = "芬兰", ["zh-TW"] = "芬蘭", ["ja"] = "フィンランド", ["fa"] = "فنلاند" },
        ["EE"] = new() { ["ru"] = "Эстония", ["en"] = "Estonia", ["uk"] = "Естонія", ["de"] = "Estland", ["zh-CN"] = "爱沙尼亚", ["zh-TW"] = "愛沙尼亞", ["ja"] = "エストニア", ["fa"] = "استونی" },
        ["PL"] = new() { ["ru"] = "Польша", ["en"] = "Poland", ["uk"] = "Польща", ["de"] = "Polen", ["zh-CN"] = "波兰", ["zh-TW"] = "波蘭", ["ja"] = "ポーランド", ["fa"] = "لهستان" },
        ["SE"] = new() { ["ru"] = "Швеция", ["en"] = "Sweden", ["uk"] = "Швеція", ["de"] = "Schweden", ["zh-CN"] = "瑞典", ["zh-TW"] = "瑞典", ["ja"] = "スウェーデン", ["fa"] = "سوئد" },
        ["FR"] = new() { ["ru"] = "Франция", ["en"] = "France", ["uk"] = "Франція", ["de"] = "Frankreich", ["zh-CN"] = "法国", ["zh-TW"] = "法國", ["ja"] = "フランス", ["fa"] = "فرانسه" },
        ["AT"] = new() { ["ru"] = "Австрия", ["en"] = "Austria", ["uk"] = "Австрія", ["de"] = "Österreich", ["zh-CN"] = "奥地利", ["zh-TW"] = "奧地利", ["ja"] = "オーストリア", ["fa"] = "اتریش" },
        ["CH"] = new() { ["ru"] = "Швейцария", ["en"] = "Switzerland", ["uk"] = "Швейцарія", ["de"] = "Schweiz", ["zh-CN"] = "瑞士", ["zh-TW"] = "瑞士", ["ja"] = "スイス", ["fa"] = "سوئیس" },
        ["IE"] = new() { ["ru"] = "Ирландия", ["en"] = "Ireland", ["uk"] = "Ірландія", ["de"] = "Irland", ["zh-CN"] = "爱尔兰", ["zh-TW"] = "愛爾蘭", ["ja"] = "アイルランド", ["fa"] = "ایرلند" },
        ["NO"] = new() { ["ru"] = "Норвегия", ["en"] = "Norway", ["uk"] = "Норвегія", ["de"] = "Norwegen", ["zh-CN"] = "挪威", ["zh-TW"] = "挪威", ["ja"] = "ノルウェー", ["fa"] = "نروژ" },
        ["DK"] = new() { ["ru"] = "Дания", ["en"] = "Denmark", ["uk"] = "Данія", ["de"] = "Dänemark", ["zh-CN"] = "丹麦", ["zh-TW"] = "丹麥", ["ja"] = "デンマーク", ["fa"] = "دانمارک" },
        ["IT"] = new() { ["ru"] = "Италия", ["en"] = "Italy", ["uk"] = "Італія", ["de"] = "Italien", ["zh-CN"] = "意大利", ["zh-TW"] = "義大利", ["ja"] = "イタリア", ["fa"] = "ایتالیا" },
        ["ES"] = new() { ["ru"] = "Испания", ["en"] = "Spain", ["uk"] = "Іспанія", ["de"] = "Spanien", ["zh-CN"] = "西班牙", ["zh-TW"] = "西班牙", ["ja"] = "スペイン", ["fa"] = "اسپانیا" },
        ["PT"] = new() { ["ru"] = "Португалия", ["en"] = "Portugal", ["uk"] = "Португалія", ["de"] = "Portugal", ["zh-CN"] = "葡萄牙", ["zh-TW"] = "葡萄牙", ["ja"] = "ポルトガル", ["fa"] = "پرتغال" },
        ["CZ"] = new() { ["ru"] = "Чехия", ["en"] = "Czechia", ["uk"] = "Чехія", ["de"] = "Tschechien", ["zh-CN"] = "捷克", ["zh-TW"] = "捷克", ["ja"] = "チェコ", ["fa"] = "جمهوری چک" },
        ["RO"] = new() { ["ru"] = "Румыния", ["en"] = "Romania", ["uk"] = "Румунія", ["de"] = "Rumänien", ["zh-CN"] = "罗马尼亚", ["zh-TW"] = "羅馬尼亞", ["ja"] = "ルーマニア", ["fa"] = "رومانی" },
        ["BG"] = new() { ["ru"] = "Болгария", ["en"] = "Bulgaria", ["uk"] = "Болгарія", ["de"] = "Bulgarien", ["zh-CN"] = "保加利亚", ["zh-TW"] = "保加利亞", ["ja"] = "ブルガリア", ["fa"] = "بلغارستان" },
        ["HU"] = new() { ["ru"] = "Венгрия", ["en"] = "Hungary", ["uk"] = "Угорщина", ["de"] = "Ungarn", ["zh-CN"] = "匈牙利", ["zh-TW"] = "匈牙利", ["ja"] = "ハンガリー", ["fa"] = "مجارستان" },
        ["SK"] = new() { ["ru"] = "Словакия", ["en"] = "Slovakia", ["uk"] = "Словаччина", ["de"] = "Slowakei", ["zh-CN"] = "斯洛伐克", ["zh-TW"] = "斯洛伐克", ["ja"] = "スロバキア", ["fa"] = "اسلواکی" },
        ["SI"] = new() { ["ru"] = "Словения", ["en"] = "Slovenia", ["uk"] = "Словенія", ["de"] = "Slowenien", ["zh-CN"] = "斯洛文尼亚", ["zh-TW"] = "斯洛維尼亞", ["ja"] = "スロベニア", ["fa"] = "اسلوونی" },
        ["GR"] = new() { ["ru"] = "Греция", ["en"] = "Greece", ["uk"] = "Греція", ["de"] = "Griechenland", ["zh-CN"] = "希腊", ["zh-TW"] = "希臘", ["ja"] = "ギリシャ", ["fa"] = "یونان" },
        ["CY"] = new() { ["ru"] = "Кипр", ["en"] = "Cyprus", ["uk"] = "Кіпр", ["de"] = "Zypern", ["zh-CN"] = "塞浦路斯", ["zh-TW"] = "賽普勒斯", ["ja"] = "キプロス", ["fa"] = "قبرس" },
        ["RS"] = new() { ["ru"] = "Сербия", ["en"] = "Serbia", ["uk"] = "Сербія", ["de"] = "Serbien", ["zh-CN"] = "塞尔维亚", ["zh-TW"] = "塞爾維亞", ["ja"] = "セルビア", ["fa"] = "صربستان" },
        ["LV"] = new() { ["ru"] = "Латвия", ["en"] = "Latvia", ["uk"] = "Латвія", ["de"] = "Lettland", ["zh-CN"] = "拉脱维亚", ["zh-TW"] = "拉脫維亞", ["ja"] = "ラトビア", ["fa"] = "لتونی" },
        ["LT"] = new() { ["ru"] = "Литва", ["en"] = "Lithuania", ["uk"] = "Литва", ["de"] = "Litauen", ["zh-CN"] = "立陶宛", ["zh-TW"] = "立陶宛", ["ja"] = "リトアニア", ["fa"] = "لیتوانی" },
        ["IS"] = new() { ["ru"] = "Исландия", ["en"] = "Iceland", ["uk"] = "Ісландія", ["de"] = "Island", ["zh-CN"] = "冰岛", ["zh-TW"] = "冰島", ["ja"] = "アイスランド", ["fa"] = "ایسلند" },
        ["UA"] = new() { ["ru"] = "Украина", ["en"] = "Ukraine", ["uk"] = "Україна", ["de"] = "Ukraine", ["zh-CN"] = "乌克兰", ["zh-TW"] = "烏克蘭", ["ja"] = "ウクライナ", ["fa"] = "اوکراین" },
        ["RU"] = new() { ["ru"] = "Россия", ["en"] = "Russia", ["uk"] = "Росія", ["de"] = "Russland", ["zh-CN"] = "俄罗斯", ["zh-TW"] = "俄羅斯", ["ja"] = "ロシア", ["fa"] = "روسیه" },
        ["BY"] = new() { ["ru"] = "Беларусь", ["en"] = "Belarus", ["uk"] = "Білорусь", ["de"] = "Belarus", ["zh-CN"] = "白俄罗斯", ["zh-TW"] = "白俄羅斯", ["ja"] = "ベラルーシ", ["fa"] = "بلاروس" },
        ["MD"] = new() { ["ru"] = "Молдова", ["en"] = "Moldova", ["uk"] = "Молдова", ["de"] = "Moldau", ["zh-CN"] = "摩尔多瓦", ["zh-TW"] = "摩爾多瓦", ["ja"] = "モルドバ", ["fa"] = "مولداوی" },
        ["KZ"] = new() { ["ru"] = "Казахстан", ["en"] = "Kazakhstan", ["uk"] = "Казахстан", ["de"] = "Kasachstan", ["zh-CN"] = "哈萨克斯坦", ["zh-TW"] = "哈薩克", ["ja"] = "カザフスタン", ["fa"] = "قزاقستان" },
        ["GE"] = new() { ["ru"] = "Грузия", ["en"] = "Georgia", ["uk"] = "Грузія", ["de"] = "Georgien", ["zh-CN"] = "格鲁吉亚", ["zh-TW"] = "喬治亞", ["ja"] = "ジョージア", ["fa"] = "گرجستان" },
        ["AM"] = new() { ["ru"] = "Армения", ["en"] = "Armenia", ["uk"] = "Вірменія", ["de"] = "Armenien", ["zh-CN"] = "亚美尼亚", ["zh-TW"] = "亞美尼亞", ["ja"] = "アルメニア", ["fa"] = "ارمنستان" },
        ["AZ"] = new() { ["ru"] = "Азербайджан", ["en"] = "Azerbaijan", ["uk"] = "Азербайджан", ["de"] = "Aserbaidschan", ["zh-CN"] = "阿塞拜疆", ["zh-TW"] = "亞塞拜然", ["ja"] = "アゼルバイジャン", ["fa"] = "آذربایجان" },
        ["UZ"] = new() { ["ru"] = "Узбекистан", ["en"] = "Uzbekistan", ["uk"] = "Узбекистан", ["de"] = "Usbekistan", ["zh-CN"] = "乌兹别克斯坦", ["zh-TW"] = "烏茲別克", ["ja"] = "ウズベキスタン", ["fa"] = "ازبکستان" },
        ["TR"] = new() { ["ru"] = "Турция", ["en"] = "Turkey", ["uk"] = "Туреччина", ["de"] = "Türkei", ["zh-CN"] = "土耳其", ["zh-TW"] = "土耳其", ["ja"] = "トルコ", ["fa"] = "ترکیه" },
        ["IL"] = new() { ["ru"] = "Израиль", ["en"] = "Israel", ["uk"] = "Ізраїль", ["de"] = "Israel", ["zh-CN"] = "以色列", ["zh-TW"] = "以色列", ["ja"] = "イスラエル", ["fa"] = "اسرائیل" },
        ["AE"] = new() { ["ru"] = "ОАЭ", ["en"] = "UAE", ["uk"] = "ОАЕ", ["de"] = "VAE", ["zh-CN"] = "阿联酋", ["zh-TW"] = "阿聯", ["ja"] = "アラブ首長国連邦", ["fa"] = "امارات" },
        ["CA"] = new() { ["ru"] = "Канада", ["en"] = "Canada", ["uk"] = "Канада", ["de"] = "Kanada", ["zh-CN"] = "加拿大", ["zh-TW"] = "加拿大", ["ja"] = "カナダ", ["fa"] = "کانادا" },
        ["BR"] = new() { ["ru"] = "Бразилия", ["en"] = "Brazil", ["uk"] = "Бразилія", ["de"] = "Brasilien", ["zh-CN"] = "巴西", ["zh-TW"] = "巴西", ["ja"] = "ブラジル", ["fa"] = "برزیل" },
        ["JP"] = new() { ["ru"] = "Япония", ["en"] = "Japan", ["uk"] = "Японія", ["de"] = "Japan", ["zh-CN"] = "日本", ["zh-TW"] = "日本", ["ja"] = "日本", ["fa"] = "ژاپن" },
        ["SG"] = new() { ["ru"] = "Сингапур", ["en"] = "Singapore", ["uk"] = "Сінгапур", ["de"] = "Singapur", ["zh-CN"] = "新加坡", ["zh-TW"] = "新加坡", ["ja"] = "シンガポール", ["fa"] = "سنگاپور" },
        ["HK"] = new() { ["ru"] = "Гонконг", ["en"] = "Hong Kong", ["uk"] = "Гонконг", ["de"] = "Hongkong", ["zh-CN"] = "香港", ["zh-TW"] = "香港", ["ja"] = "香港", ["fa"] = "هنگ‌کنگ" },
        ["TW"] = new() { ["ru"] = "Тайвань", ["en"] = "Taiwan", ["uk"] = "Тайвань", ["de"] = "Taiwan", ["zh-CN"] = "台湾", ["zh-TW"] = "台灣", ["ja"] = "台湾", ["fa"] = "تایوان" },
        ["KR"] = new() { ["ru"] = "Южная Корея", ["en"] = "South Korea", ["uk"] = "Південна Корея", ["de"] = "Südkorea", ["zh-CN"] = "韩国", ["zh-TW"] = "韓國", ["ja"] = "韓国", ["fa"] = "کره جنوبی" },
        ["IN"] = new() { ["ru"] = "Индия", ["en"] = "India", ["uk"] = "Індія", ["de"] = "Indien", ["zh-CN"] = "印度", ["zh-TW"] = "印度", ["ja"] = "インド", ["fa"] = "هند" },
        ["AU"] = new() { ["ru"] = "Австралия", ["en"] = "Australia", ["uk"] = "Австралія", ["de"] = "Australien", ["zh-CN"] = "澳大利亚", ["zh-TW"] = "澳洲", ["ja"] = "オーストラリア", ["fa"] = "استرالیا" },
        ["CN"] = new() { ["ru"] = "Китай", ["en"] = "China", ["uk"] = "Китай", ["de"] = "China", ["zh-CN"] = "中国", ["zh-TW"] = "中國", ["ja"] = "中国", ["fa"] = "چین" },
        ["BE"] = new() { ["ru"] = "Бельгия", ["en"] = "Belgium", ["uk"] = "Бельгія", ["de"] = "Belgien", ["zh-CN"] = "比利时", ["zh-TW"] = "比利時", ["ja"] = "ベルギー", ["fa"] = "بلژیک" },
        ["LU"] = new() { ["ru"] = "Люксембург", ["en"] = "Luxembourg", ["uk"] = "Люксембург", ["de"] = "Luxemburg", ["zh-CN"] = "卢森堡", ["zh-TW"] = "盧森堡", ["ja"] = "ルクセンブルク", ["fa"] = "لوکزامبورگ" },
        ["HR"] = new() { ["ru"] = "Хорватия", ["en"] = "Croatia", ["uk"] = "Хорватія", ["de"] = "Kroatien", ["zh-CN"] = "克罗地亚", ["zh-TW"] = "克羅埃西亞", ["ja"] = "クロアチア", ["fa"] = "کرواسی" },
        ["AL"] = new() { ["ru"] = "Албания", ["en"] = "Albania", ["uk"] = "Албанія", ["de"] = "Albanien", ["zh-CN"] = "阿尔巴尼亚", ["zh-TW"] = "阿爾巴尼亞", ["ja"] = "アルバニア", ["fa"] = "آلبانی" },
        ["TH"] = new() { ["ru"] = "Таиланд", ["en"] = "Thailand", ["uk"] = "Таїланд", ["de"] = "Thailand", ["zh-CN"] = "泰国", ["zh-TW"] = "泰國", ["ja"] = "タイ", ["fa"] = "تایلند" },
        ["VN"] = new() { ["ru"] = "Вьетнам", ["en"] = "Vietnam", ["uk"] = "В'єтнам", ["de"] = "Vietnam", ["zh-CN"] = "越南", ["zh-TW"] = "越南", ["ja"] = "ベトナム", ["fa"] = "ویتنام" },
        ["ID"] = new() { ["ru"] = "Индонезия", ["en"] = "Indonesia", ["uk"] = "Індонезія", ["de"] = "Indonesien", ["zh-CN"] = "印度尼西亚", ["zh-TW"] = "印尼", ["ja"] = "インドネシア", ["fa"] = "اندونزی" },
        ["MY"] = new() { ["ru"] = "Малайзия", ["en"] = "Malaysia", ["uk"] = "Малайзія", ["de"] = "Malaysia", ["zh-CN"] = "马来西亚", ["zh-TW"] = "馬來西亞", ["ja"] = "マレーシア", ["fa"] = "مالزی" },
        ["PH"] = new() { ["ru"] = "Филиппины", ["en"] = "Philippines", ["uk"] = "Філіппіни", ["de"] = "Philippinen", ["zh-CN"] = "菲律宾", ["zh-TW"] = "菲律賓", ["ja"] = "フィリピン", ["fa"] = "فیلیپین" },
        ["MX"] = new() { ["ru"] = "Мексика", ["en"] = "Mexico", ["uk"] = "Мексика", ["de"] = "Mexiko", ["zh-CN"] = "墨西哥", ["zh-TW"] = "墨西哥", ["ja"] = "メキシコ", ["fa"] = "مکزیک" },
        ["AR"] = new() { ["ru"] = "Аргентина", ["en"] = "Argentina", ["uk"] = "Аргентина", ["de"] = "Argentinien", ["zh-CN"] = "阿根廷", ["zh-TW"] = "阿根廷", ["ja"] = "アルゼンチン", ["fa"] = "آرژانتین" },
        ["CL"] = new() { ["ru"] = "Чили", ["en"] = "Chile", ["uk"] = "Чилі", ["de"] = "Chile", ["zh-CN"] = "智利", ["zh-TW"] = "智利", ["ja"] = "チリ", ["fa"] = "شیلی" },
        ["ZA"] = new() { ["ru"] = "ЮАР", ["en"] = "South Africa", ["uk"] = "ПАР", ["de"] = "Südafrika", ["zh-CN"] = "南非", ["zh-TW"] = "南非", ["ja"] = "南アフリカ", ["fa"] = "آفریقای جنوبی" }
    };

    private static string LocalizeCountryName(string countryCode, string? apiCountryName)
    {
        if (!string.IsNullOrEmpty(countryCode))
        {
            string upper = countryCode.ToUpperInvariant();
            if (_countryNames.TryGetValue(upper, out var map) && map.TryGetValue(CurrentLanguage, out var localized))
                return localized;

            try
            {
                var region = new RegionInfo(upper);
                if (CurrentLanguage == "en") return region.EnglishName;
                if (!string.IsNullOrWhiteSpace(apiCountryName)) return apiCountryName;
                return region.EnglishName;
            }
            catch { }
        }

        return !string.IsNullOrWhiteSpace(apiCountryName) ? apiCountryName : L("UnknownLocation");
    }

    public MainWindow()
    {
        SystemProxyManager.DisableProxy();
        KillOrphanedSingboxProcesses();
        Task.Run(() => { try { using var t = new TcpClient(); t.Connect("8.8.8.8", 53); } catch { } });

        InitializeTrayIcon();
        InitializeComponent();

        Loaded += MainWindow_Loaded;
    }

    private void KillOrphanedSingboxProcesses()
    {
        try
        {
            string expectedPath = Path.GetFullPath(Path.Combine(_exeDirectory, "sing-box.exe"));
            foreach (var proc in Process.GetProcessesByName("sing-box"))
            {
                try
                {
                    string? procPath = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(procPath) &&
                        string.Equals(Path.GetFullPath(procPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(true);
                        proc.WaitForExit(1500);
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
    }

    private static HashSet<string> GetExistingAdapterNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.IsNullOrWhiteSpace(ni.Name))
                    names.Add(ni.Name.Trim());
            }
        }
        catch { }

        try
        {
            using var netKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}");
            if (netKey != null)
            {
                foreach (string subName in netKey.GetSubKeyNames())
                {
                    try
                    {
                        using var connKey = netKey.OpenSubKey($@"{subName}\Connection");
                        if (connKey?.GetValue("Name") is string adapterName && !string.IsNullOrWhiteSpace(adapterName))
                            names.Add(adapterName.Trim());
                    }
                    catch { }
                }
            }
        }
        catch { }

        return names;
    }

    private string SelectAvailableTunInterfaceName()
    {
        var existing = GetExistingAdapterNames();
        if (!existing.Contains("NeonV") && !_blockedTunNames.Contains("NeonV"))
            return "NeonV";

        for (int i = 2; i <= 50; i++)
        {
            string candidate = $"NeonV-{i}";
            if (!existing.Contains(candidate) && !_blockedTunNames.Contains(candidate))
                return candidate;
        }

        return $"NeonV-{Guid.NewGuid().ToString("N")[..4]}";
    }

    private static bool IsTunConflictError(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText)) return false;
        return errorText.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("существует", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("існує", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("bereits vorhanden", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("configure tun interface", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("starting TUN interface", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("WintunCreateAdapter", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("create tun", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanBindTcpPort(IPAddress ip, int port)
    {
        try
        {
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(ip, port));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindFreeTcpPort(IPAddress ip)
    {
        try
        {
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(ip, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        catch
        {
            return 0;
        }
    }

    private static string DetectSystemLanguage()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            string name = culture.Name;
            string twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();

            if (twoLetter == "zh")
            {
                if (name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Hant", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-TW";
                }
                return "zh-CN";
            }

            if (_translations.ContainsKey(twoLetter))
                return twoLetter;
        }
        catch { }
        return "en";
    }

    private static string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i && i == 1) return "Light";
        }
        catch { }
        return "Dark";
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            if (_currentSettings.Theme == "System")
            {
                Dispatcher.InvokeAsync(() => ApplyTheme("System"));
            }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadSettings();
            _lastAppliedAutoStart = _currentSettings.AutoStart;
            ApplyTheme(_currentSettings.Theme);
            ApplyLanguage(_currentSettings.Language);

            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

            if (ListRoutingDomains != null) ListRoutingDomains.ItemsSource = RoutingDomainsList;
            if (ListRoutingApps != null) ListRoutingApps.ItemsSource = RoutingAppsList;
            if (LvProfiles != null) LvProfiles.ItemsSource = _standaloneProfiles;
            if (LvSubscriptions != null) LvSubscriptions.ItemsSource = _subscriptions;

            ApplySettingsToUI();
            UpdateButtonText();
            UpdateDynamicTexts();
            UpdateEmptyState();

            foreach (var p in _profiles) p.ForceUpdatePing();

            _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _pingTimer.Tick += (s, ev) =>
            {
                PingManager.Clear();
                if (ActiveProfile != null) _ = ActiveProfile.CheckPingAsync();
            };
            _pingTimer.Start();

            _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uptimeTimer.Tick += (s, ev) =>
            {
                if (!_isRunning) return;
                if (TxtConnectionTime != null) TxtConnectionTime.Text = FormatUptime(DateTime.Now - _connectionStartTime);
                UpdateTraffic();
            };

            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += FlushLogs;
            _logTimer.Start();

            ApplyVisualEffects();

            bool isAutoStart = Environment.GetCommandLineArgs().Contains("--autostart");
            if (_currentSettings.StartMinimized && isAutoStart)
            {
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
                Hide();
            }

            UpdateMenuIndicator(BtnNavMain, 0);

            if (_currentSettings.AutoUpdateSubscriptions && _subscriptions.Count > 0)
                _ = AutoUpdateSubscriptionsOnStartupAsync();

            if (_currentSettings.AutoConnect && isAutoStart && ActiveProfile != null && !_isRunning)
                await StartVPNAsync();
        }
        catch { }
    }

    private async Task AutoUpdateSubscriptionsOnStartupAsync()
    {
        try
        {
            await Task.Delay(1500);
            foreach (var sub in _subscriptions.ToList())
            {
                if (_isRealClose) break;
                await UpdateSubscriptionAsync(sub, showNotify: false);
            }
        }
        catch { }
    }

    private static string SanitizeHwid(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string cleaned = Regex.Replace(raw, @"[^a-zA-Z0-9=-]", "");
            if (cleaned.Length > 64) cleaned = cleaned[..64];
            if (cleaned.Length >= 10) return cleaned;
        }
        return Guid.NewGuid().ToString("N");
    }

    private static string GetOrCreateHwid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid))
            {
                return SanitizeHwid(guid);
            }
        }
        catch { }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: false } cb && VisualTreeHelper.GetParent(cb) is UIElement parent)
        {
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        }
    }

    private TimeSpan GetDuration(double seconds) =>
        _currentSettings.DisableAnimations ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon { Text = "NeonV", Visible = true };
        UpdateTrayIconImage(false);
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;

        var contextMenu = new Forms.ContextMenuStrip();
        var boldFont = new System.Drawing.Font(System.Drawing.SystemFonts.MenuFont.FontFamily, System.Drawing.SystemFonts.MenuFont.Size, System.Drawing.FontStyle.Bold);

        _trayToggleItem = new Forms.ToolStripMenuItem("...", null, (_, _) => Dispatcher.InvokeAsync(ToggleVpnAsync)) { Font = boldFont };
        contextMenu.Items.Add(_trayToggleItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayNavMainItem = new Forms.ToolStripMenuItem("Главная", null, (_, _) => ShowTab(0));
        _trayNavSubscriptionsItem = new Forms.ToolStripMenuItem("Подписки", null, (_, _) => ShowTab(1));
        _trayNavSettingsItem = new Forms.ToolStripMenuItem("Настройки", null, (_, _) => ShowTab(2));
        _trayNavLogsItem = new Forms.ToolStripMenuItem("Логи", null, (_, _) => ShowTab(3));

        contextMenu.Items.Add(_trayNavMainItem);
        contextMenu.Items.Add(_trayNavSubscriptionsItem);
        contextMenu.Items.Add(_trayNavSettingsItem);
        contextMenu.Items.Add(_trayNavLogsItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayModeItem = new Forms.ToolStripMenuItem("Mode", null, (_, _) => ToggleMode());
        contextMenu.Items.Add(_trayModeItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayExitItem = new Forms.ToolStripMenuItem("Выход", null, (_, _) => ExitApplication());
        contextMenu.Items.Add(_trayExitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void UpdateTrayIconImage(bool isActive)
    {
        if (_notifyIcon == null) return;
        try
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new System.Drawing.Rectangle(2, 2, 28, 28);
            var brushColor = isActive ? System.Drawing.Color.FromArgb(103, 80, 164) : System.Drawing.Color.Gray;

            using var brush = new System.Drawing.SolidBrush(brushColor);
            g.FillEllipse(brush, rect);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3);
            g.DrawEllipse(pen, rect);

            _notifyIcon.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch { }
    }

    private class DarkMenuColorTable : Forms.ProfessionalColorTable
    {
        public DarkMenuColorTable() { UseSystemColors = false; }
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(33, 31, 38);
        public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(43, 41, 48);
        public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(43, 41, 48);
        public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(43, 41, 48);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(73, 69, 79);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(73, 69, 79);
        public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(33, 31, 38);
    }

    private class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                var rect = new System.Drawing.Rectangle(1, 0, e.Item.Width - 2, e.Item.Height - 1);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(74, 68, 88));
                e.Graphics.FillRectangle(brush, rect);
                using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(147, 143, 153));
                e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            else base.OnRenderMenuItemBackground(e);
        }
    }

    private void UpdateTrayTheme(bool isDark)
    {
        var menu = _notifyIcon?.ContextMenuStrip;
        if (menu == null) return;

        if (isDark)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = System.Drawing.Color.FromArgb(33, 31, 38);
            foreach (Forms.ToolStripItem item in menu.Items)
                item.ForeColor = System.Drawing.Color.FromArgb(230, 224, 233);
        }
        else
        {
            menu.Renderer = new Forms.ToolStripProfessionalRenderer();
            menu.BackColor = System.Drawing.SystemColors.Control;
            foreach (Forms.ToolStripItem item in menu.Items)
                item.ForeColor = System.Drawing.SystemColors.ControlText;
        }
    }

    private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;

        Dispatcher.Invoke(() =>
        {
            if (IsVisible && GetForegroundWindow() == new WindowInteropHelper(this).Handle)
                Hide();
            else
            {
                Show();
                ShowInTaskbar = true;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Focus();
            }
        });
    }

    private void ShowTab(int index)
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            if (index == 0) BtnNavMain_Click(null!, null!);
            else if (index == 1) BtnNavSubscriptions_Click(null!, null!);
            else if (index == 2) BtnNavSettings_Click(null!, null!);
            else if (index == 3) BtnNavLogs_Click(null!, null!);
        });
    }

    private void ToggleMode()
    {
        Dispatcher.Invoke(() => { if (CmbMode != null) CmbMode.SelectedIndex = CmbMode.SelectedIndex == 0 ? 1 : 0; });
    }

    private async void ExitApplication()
    {
        _isRealClose = true;
        if (_isRunning) await StopVPNAsync();
        Dispatcher.Invoke(Close);
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            if (RootBorder != null) RootBorder.Margin = new Thickness(7);
            if (IconMaximize != null) IconMaximize.Text = "\xE923";
        }
        else
        {
            if (RootBorder != null) RootBorder.Margin = new Thickness(0);
            if (IconMaximize != null) IconMaximize.Text = "\xE922";
        }
    }

    private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Grid grid) { grid.Focus(); Keyboard.ClearFocus(); }
    }

    private static string PluralizeSlavic(int n, string unit, bool isUkrainian)
    {
        int m10 = n % 10, m100 = n % 100;
        int form = (m100 is >= 11 and <= 14) ? 2 : (m10 == 1) ? 0 : (m10 is >= 2 and <= 4) ? 1 : 2;

        if (isUkrainian)
        {
            return unit switch
            {
                "month" => new[] { "місяць", "місяці", "місяців" }[form],
                "day" => new[] { "день", "дні", "днів" }[form],
                "hour" => new[] { "година", "години", "годин" }[form],
                "minute" => new[] { "хвилина", "хвилини", "хвилин" }[form],
                _ => new[] { "секунда", "секунди", "секунд" }[form]
            };
        }

        return unit switch
        {
            "month" => new[] { "месяц", "месяца", "месяцев" }[form],
            "day" => new[] { "день", "дня", "дней" }[form],
            "hour" => new[] { "час", "часа", "часов" }[form],
            "minute" => new[] { "минута", "минуты", "минут" }[form],
            _ => new[] { "секунда", "секунды", "секунд" }[form]
        };
    }

    private static string FormatUptimeUnit(int n, string unit)
    {
        return CurrentLanguage switch
        {
            "ru" => $"{n} {PluralizeSlavic(n, unit, false)}",
            "uk" => $"{n} {PluralizeSlavic(n, unit, true)}",
            "de" => unit switch
            {
                "month" => $"{n} {(n == 1 ? "Monat" : "Monate")}",
                "day" => $"{n} {(n == 1 ? "Tag" : "Tage")}",
                "hour" => $"{n} {(n == 1 ? "Stunde" : "Stunden")}",
                "minute" => $"{n} {(n == 1 ? "Minute" : "Minuten")}",
                _ => $"{n} {(n == 1 ? "Sekunde" : "Sekunden")}"
            },
            "zh-CN" => unit switch
            {
                "month" => $"{n} 个月",
                "day" => $"{n} 天",
                "hour" => $"{n} 小时",
                "minute" => $"{n} 分钟",
                _ => $"{n} 秒"
            },
            "zh-TW" => unit switch
            {
                "month" => $"{n} 個月",
                "day" => $"{n} 天",
                "hour" => $"{n} 小時",
                "minute" => $"{n} 分鐘",
                _ => $"{n} 秒"
            },
            "ja" => unit switch
            {
                "month" => $"{n}ヶ月",
                "day" => $"{n}日",
                "hour" => $"{n}時間",
                "minute" => $"{n}分",
                _ => $"{n}秒"
            },
            "fa" => unit switch
            {
                "month" => $"{n} ماه",
                "day" => $"{n} روز",
                "hour" => $"{n} ساعت",
                "minute" => $"{n} دقیقه",
                _ => $"{n} ثانیه"
            },
            _ => unit switch
            {
                "month" => $"{n} {(n == 1 ? "month" : "months")}",
                "day" => $"{n} {(n == 1 ? "day" : "days")}",
                "hour" => $"{n} {(n == 1 ? "hour" : "hours")}",
                "minute" => $"{n} {(n == 1 ? "minute" : "minutes")}",
                _ => $"{n} {(n == 1 ? "second" : "seconds")}"
            }
        };
    }

    private static string FormatUptime(TimeSpan ts)
    {
        int totalMonths = (int)(ts.TotalDays / 30);
        if (totalMonths > 0) return FormatUptimeUnit(totalMonths, "month");
        if (ts.Days > 0) return FormatUptimeUnit(ts.Days, "day");
        if (ts.Hours > 0) return FormatUptimeUnit(ts.Hours, "hour");
        if (ts.Minutes > 0) return FormatUptimeUnit(ts.Minutes, "minute");
        return FormatUptimeUnit(ts.Seconds, "second");
    }

    private void ApplyTheme(string theme)
    {
        string resolvedTheme = theme == "System" ? GetSystemTheme() : theme;
        IsDarkTheme = resolvedTheme != "Light";
        Resources["ToggleBtnBgColor"] = new SolidColorBrush(Color.FromRgb(103, 80, 164));
        Resources["ErrorColor"] = new SolidColorBrush(IsDarkTheme ? Color.FromRgb(248, 113, 113) : Color.FromRgb(220, 38, 38));

        if (!IsDarkTheme)
        {
            Resources["BgColor"] = new SolidColorBrush(Color.FromRgb(254, 247, 255));
            Resources["PanelColor"] = new SolidColorBrush(Color.FromRgb(243, 237, 247));
            Resources["ConsoleBgColor"] = new SolidColorBrush(Color.FromRgb(240, 240, 240));
            Resources["TextColor"] = new SolidColorBrush(Color.FromRgb(29, 27, 32));
            Resources["TextMutedColor"] = new SolidColorBrush(Color.FromRgb(73, 69, 79));
            Resources["AccentColor"] = new SolidColorBrush(Color.FromRgb(103, 80, 164));
            Resources["OnAccentColor"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            Resources["HoverColor"] = new SolidColorBrush(Color.FromRgb(232, 222, 248));
            Resources["OutlineColor"] = new SolidColorBrush(Color.FromRgb(121, 116, 126));
        }
        else
        {
            Resources["BgColor"] = new SolidColorBrush(Color.FromRgb(20, 18, 24));
            Resources["PanelColor"] = new SolidColorBrush(Color.FromRgb(36, 34, 41));
            Resources["ConsoleBgColor"] = new SolidColorBrush(Color.FromRgb(10, 10, 12));
            Resources["TextColor"] = new SolidColorBrush(Color.FromRgb(230, 224, 233));
            Resources["TextMutedColor"] = new SolidColorBrush(Color.FromRgb(202, 196, 208));
            Resources["AccentColor"] = new SolidColorBrush(Color.FromRgb(208, 188, 255));
            Resources["OnAccentColor"] = new SolidColorBrush(Color.FromRgb(56, 30, 114));
            Resources["HoverColor"] = new SolidColorBrush(Color.FromRgb(74, 68, 88));
            Resources["OutlineColor"] = new SolidColorBrush(Color.FromRgb(147, 143, 153));
        }

        if (IsLoaded && FindResource("TextMutedColor") is Brush mutedBrush && FindResource("AccentColor") is Brush accentBrush)
        {
            if (BtnNavMain != null) BtnNavMain.Foreground = mutedBrush;
            if (BtnNavSubscriptions != null) BtnNavSubscriptions.Foreground = mutedBrush;
            if (BtnNavSettings != null) BtnNavSettings.Foreground = mutedBrush;
            if (BtnNavLogs != null) BtnNavLogs.Foreground = mutedBrush;

            if (ViewMain?.Visibility == Visibility.Visible && BtnNavMain != null) BtnNavMain.Foreground = accentBrush;
            else if (ViewSubscriptions?.Visibility == Visibility.Visible && BtnNavSubscriptions != null) BtnNavSubscriptions.Foreground = accentBrush;
            else if (ViewSettings?.Visibility == Visibility.Visible && BtnNavSettings != null) BtnNavSettings.Foreground = accentBrush;
            else if (BtnNavLogs != null) BtnNavLogs.Foreground = accentBrush;
        }

        foreach (var p in _profiles) p.ForceUpdatePing();

        UpdateButtonText();
        UpdateTrayTheme(IsDarkTheme);
        ApplyVisualEffects();
    }

    private void ApplyLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang) || !_translations.ContainsKey(lang))
            lang = "en";

        _currentSettings.Language = lang;
        CurrentLanguage = lang;

        if (TxtNavMain != null) TxtNavMain.Text = L("NavMain");
        if (TxtNavSubs != null) TxtNavSubs.Text = L("NavSubs");
        if (TxtNavSettings != null) TxtNavSettings.Text = L("NavSettings");
        if (TxtNavLogs != null) TxtNavLogs.Text = L("NavLogs");

        if (TxtModeLabel != null) TxtModeLabel.Text = L("ModeLabel");
        if (TxtBtnAddProfile != null) TxtBtnAddProfile.Text = L("BtnAddProfile");
        if (TxtBtnRefreshPing != null) TxtBtnRefreshPing.Text = L("BtnRefreshPing");
        if (TxtSearchPlaceholder != null) TxtSearchPlaceholder.Text = L("SearchPlaceholder");
        if (TxtEmptyProfilesTitle != null) TxtEmptyProfilesTitle.Text = L("EmptyProfilesTitle");
        if (TxtEmptyProfilesDesc != null) TxtEmptyProfilesDesc.Text = L("EmptyProfilesDesc");

        if (TxtBtnAddSub != null) TxtBtnAddSub.Text = L("BtnAddSub");
        if (TxtBtnUpdateAllSubs != null) TxtBtnUpdateAllSubs.Text = L("BtnUpdateAllSubs");
        if (TxtEmptySubsTitle != null) TxtEmptySubsTitle.Text = L("EmptySubsTitle");
        if (TxtEmptySubsDesc != null) TxtEmptySubsDesc.Text = L("EmptySubsDesc");

        if (TxtSettingsTitle != null) TxtSettingsTitle.Text = L("SettingsTitle");
        if (TxtGroupLaunch != null) TxtGroupLaunch.Text = L("GroupLaunch");
        if (ChkAutoStart != null) ChkAutoStart.Content = L("AutoStart");
        if (ChkStartMinimized != null) ChkStartMinimized.Content = L("StartMinimized");
        if (ChkMinimizeToTray != null) ChkMinimizeToTray.Content = L("MinimizeToTray");
        if (ChkAutoUpdateSubs != null) ChkAutoUpdateSubs.Content = L("AutoUpdateSubs");
        if (ChkUseCustomConfig != null) ChkUseCustomConfig.Content = L("CustomConfig");

        if (TxtGroupAppearance != null) TxtGroupAppearance.Text = L("GroupAppearance");
        if (TxtLangLabel != null) TxtLangLabel.Text = L("LangLabel");
        if (TxtThemeLabel != null) TxtThemeLabel.Text = L("ThemeLabel");
        if (CmbTheme != null && CmbTheme.Items.Count >= 3)
        {
            ((ComboBoxItem)CmbTheme.Items[0]).Content = L("ThemeSystem");
            ((ComboBoxItem)CmbTheme.Items[1]).Content = L("ThemeDark");
            ((ComboBoxItem)CmbTheme.Items[2]).Content = L("ThemeLight");
        }
        if (ChkVisualEffects != null) ChkVisualEffects.Content = L("VisualEffects");
        if (ChkDisableAnimations != null) ChkDisableAnimations.Content = L("DisableAnimations");

        if (TxtGroupNetwork != null) TxtGroupNetwork.Text = L("GroupNetwork");
        if (TxtDnsLabel != null) TxtDnsLabel.Text = L("DnsLabel");
        if (TxtDirectDnsLabel != null) TxtDirectDnsLabel.Text = L("DirectDnsLabel");
        if (TxtBtnRouting != null) TxtBtnRouting.Text = L("BtnRouting");

        if (TxtGroupAdvanced != null) TxtGroupAdvanced.Text = L("GroupAdvanced");
        if (TxtProxyPortLabel != null) TxtProxyPortLabel.Text = L("ProxyPortLabel");
        if (TxtTunStackLabel != null) TxtTunStackLabel.Text = L("TunStackLabel");
        if (ChkStrictRoute != null) ChkStrictRoute.Content = L("StrictRoute");
        if (ChkEnableIpv6 != null) ChkEnableIpv6.Content = L("EnableIpv6");
        if (ChkAllowLan != null) ChkAllowLan.Content = L("AllowLan");
        if (TxtAllowLanDesc != null) TxtAllowLanDesc.Text = L("AllowLanDesc");

        if (TxtLogsTitle != null) TxtLogsTitle.Text = L("LogsTitle");
        if (TxtLogLevelLabel != null) TxtLogLevelLabel.Text = L("LogLevelLabel");
        if (ChkAutoScroll != null) ChkAutoScroll.Content = L("AutoScroll");
        if (BtnCopyLogs != null) BtnCopyLogs.Content = L("BtnCopyLogs");
        if (BtnClearLogs != null) BtnClearLogs.Content = L("BtnClearLogs");

        if (TxtImportTitle != null) TxtImportTitle.Text = _editingProfile != null ? L("EditTitle") : L("ImportTitle");
        if (BtnPasteImport != null) BtnPasteImport.Content = L("PasteImport");
        if (TxtOrManual != null) TxtOrManual.Text = L("OrManual");
        if (TxtProtocolLabel != null) TxtProtocolLabel.Text = L("ProtocolLabel");
        if (BtnCancelImport != null) BtnCancelImport.Content = L("Cancel");
        if (BtnSaveImport != null) BtnSaveImport.Content = L("Save");

        if (TxtAddSubTitle != null) TxtAddSubTitle.Text = L("AddSubTitle");
        if (TxtSubNameLabel != null) TxtSubNameLabel.Text = L("SubNameLabel");
        if (TxtSubUrlLabel != null) TxtSubUrlLabel.Text = L("SubUrlLabel");
        if (BtnCancelSub != null) BtnCancelSub.Content = L("Cancel");
        if (BtnSaveSub != null) BtnSaveSub.Content = L("Add");

        if (TxtRoutingTitle != null) TxtRoutingTitle.Text = L("RoutingTitle");
        if (ChkBypassRu != null) ChkBypassRu.Content = L("BypassRu");
        if (TxtRouteModeLabel != null) TxtRouteModeLabel.Text = L("RouteModeLabel");
        if (CmbRouteMode != null && CmbRouteMode.Items.Count >= 2)
        {
            ((ComboBoxItem)CmbRouteMode.Items[0]).Content = L("RouteModeBypass");
            ((ComboBoxItem)CmbRouteMode.Items[1]).Content = L("RouteModeOnly");
        }
        if (TxtDomainsLabel != null) TxtDomainsLabel.Text = L("DomainsLabel");
        if (BtnAddDomain != null) BtnAddDomain.Content = L("Add");
        if (TxtAppsLabel != null) TxtAppsLabel.Text = L("AppsLabel");
        if (BtnAddApp != null) BtnAddApp.Content = L("Add");
        if (BtnCloseRouting != null) BtnCloseRouting.Content = L("Done");

        if (TxtQRTitle != null) TxtQRTitle.Text = L("QRTitle");
        if (BtnCopyLinkFromQR != null) BtnCopyLinkFromQR.Content = L("CopyLink");

        if (FindResource("RowContextMenu") is ContextMenu rowMenu)
        {
            foreach (var item in rowMenu.Items)
            {
                if (item is MenuItem mi && mi.Tag is string tag)
                {
                    mi.Header = L(tag);
                }
            }
        }

        if (_trayNavMainItem != null) _trayNavMainItem.Text = L("NavMain");
        if (_trayNavSubscriptionsItem != null) _trayNavSubscriptionsItem.Text = L("NavSubs");
        if (_trayNavSettingsItem != null) _trayNavSettingsItem.Text = L("NavSettings");
        if (_trayNavLogsItem != null) _trayNavLogsItem.Text = L("NavLogs");
        if (_trayExitItem != null) _trayExitItem.Text = L("TrayExit");

        foreach (var sub in _subscriptions) sub.NotifyTexts();
        foreach (var p in _profiles) p.ForceUpdatePing();

        if (_isRunning)
        {
            if (TxtLocation != null && TxtLocation.Text != "...")
                TxtLocation.Text = LocalizeCountryName(_lastLocationCountryCode, _lastLocationApiName);
            if (TxtConnectionTime != null && TxtConnectionTime.Text != "...")
                TxtConnectionTime.Text = FormatUptime(DateTime.Now - _connectionStartTime);
        }

        UpdateButtonText();
        UpdateDynamicTexts();
    }

    private void ApplyVisualEffects()
    {
        bool enabled = _currentSettings.EnableVisualEffects;
        Color accentColor = TryFindResource("AccentColor") is SolidColorBrush accent
            ? accent.Color
            : Color.FromRgb(208, 188, 255);

        Resources["VisualEffectGlowOpacity"] = enabled ? (IsDarkTheme ? 0.55 : 0.30) : 0.0;

        if (AmbientGlowOverlay != null)
        {
            if (enabled)
            {
                byte a1 = (byte)(IsDarkTheme ? 42 : 28);
                byte a2 = (byte)(IsDarkTheme ? 14 : 9);
                if (AmbientGlowTop != null)
                {
                    AmbientGlowTop.Fill = new RadialGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(a1, accentColor.R, accentColor.G, accentColor.B), 0.0),
                        new GradientStop(Color.FromArgb(a2, accentColor.R, accentColor.G, accentColor.B), 0.55),
                        new GradientStop(Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B), 1.0)
                    });
                }
                if (AmbientGlowBottom != null)
                {
                    AmbientGlowBottom.Fill = new RadialGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb((byte)(a1 * 0.7), accentColor.R, accentColor.G, accentColor.B), 0.0),
                        new GradientStop(Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B), 1.0)
                    });
                }
            }

            double targetOpacity = enabled ? 1.0 : 0.0;
            if (IsLoaded)
            {
                AmbientGlowOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    To = targetOpacity,
                    Duration = GetDuration(0.35),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                AmbientGlowOverlay.Opacity = targetOpacity;
            }
        }

        if (TxtAppTitle != null)
        {
            TxtAppTitle.Effect = enabled
                ? new DropShadowEffect
                {
                    Color = accentColor,
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = IsDarkTheme ? 0.55 : 0.25
                }
                : null;
        }

        if (SidebarShadowBorder != null)
        {
            SidebarShadowBorder.Effect = new DropShadowEffect
            {
                BlurRadius = enabled ? 28 : 20,
                Opacity = enabled ? (IsDarkTheme ? 0.45 : 0.22) : 0.15,
                ShadowDepth = 0,
                Direction = 0
            };
        }

        if (BtnToggle != null)
        {
            if (enabled)
            {
                Color toggleGlowColor = _isRunning ? Color.FromRgb(179, 38, 30) : accentColor;
                double baseOpacity = IsDarkTheme ? 0.6 : 0.3;
                var effect = new DropShadowEffect
                {
                    Color = toggleGlowColor,
                    BlurRadius = 25,
                    ShadowDepth = 0,
                    Opacity = baseOpacity
                };
                BtnToggle.Effect = effect;

                if (_isRunning && !_currentSettings.DisableAnimations)
                {
                    var pulseEase = new SineEase { EasingMode = EasingMode.EaseInOut };
                    effect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation
                    {
                        From = 20,
                        To = 34,
                        Duration = TimeSpan.FromSeconds(1.8),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = pulseEase
                    });
                    effect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation
                    {
                        From = IsDarkTheme ? 0.45 : 0.25,
                        To = IsDarkTheme ? 0.85 : 0.48,
                        Duration = TimeSpan.FromSeconds(1.8),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = pulseEase
                    });
                }
            }
            else
            {
                BtnToggle.Effect = null;
            }
        }

        if (MenuIndicator != null)
        {
            MenuIndicator.Effect = enabled
                ? new DropShadowEffect
                {
                    Color = accentColor,
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = IsDarkTheme ? 0.9 : 0.5
                }
                : null;
        }

        if (BtnAddProfile != null)
        {
            BtnAddProfile.Effect = enabled
                ? new DropShadowEffect
                {
                    Color = accentColor,
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = IsDarkTheme ? 0.45 : 0.25
                }
                : null;
        }

        if (BtnAddSub != null)
        {
            BtnAddSub.Effect = enabled
                ? new DropShadowEffect
                {
                    Color = accentColor,
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = IsDarkTheme ? 0.45 : 0.25
                }
                : null;
        }

        if (SettingsCardShadow != null)
        {
            SettingsCardShadow.Opacity = enabled ? 1.0 : 0.0;
            SettingsCardShadow.Effect = enabled
                ? new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Opacity = IsDarkTheme ? 0.35 : 0.14
                }
                : null;
        }

        if (LogsConsoleShadow != null)
        {
            LogsConsoleShadow.Opacity = enabled ? 1.0 : 0.0;
            LogsConsoleShadow.Effect = enabled
                ? new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Opacity = IsDarkTheme ? 0.4 : 0.16
                }
                : null;
        }

        if (LocationBadge != null)
        {
            if (enabled)
            {
                LocationBadge.Background = TryFindResource("PanelColor") as Brush ?? Brushes.Transparent;
                LocationBadge.BorderThickness = new Thickness(1);
                LocationBadge.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(IsDarkTheme ? 95 : 70), accentColor.R, accentColor.G, accentColor.B));
            }
            else
            {
                LocationBadge.Background = Brushes.Transparent;
                LocationBadge.BorderThickness = new Thickness(0);
                LocationBadge.BorderBrush = null;
            }
        }
    }

    private void LoadSettings()
    {
        string settingsPath = Path.Combine(_exeDirectory, "settings.json");

        bool isFirstLaunch = !File.Exists(settingsPath);

        try
        {
            if (!isFirstLaunch)
            {
                string json = File.ReadAllText(settingsPath);
                if (JsonSerializer.Deserialize<AppSettings>(json) is { } saved) _currentSettings = saved;
            }
        }
        catch { }

        _currentSettings.Hwid = string.IsNullOrWhiteSpace(_currentSettings.Hwid)
            ? GetOrCreateHwid()
            : SanitizeHwid(_currentSettings.Hwid);

        if (isFirstLaunch || string.IsNullOrWhiteSpace(_currentSettings.Language) || !_translations.ContainsKey(_currentSettings.Language))
        {
            _currentSettings.Language = DetectSystemLanguage();
        }

        if (isFirstLaunch || (_currentSettings.Theme is not ("System" or "Dark" or "Light")))
        {
            _currentSettings.Theme = "System";
        }

        if (string.IsNullOrWhiteSpace(_currentSettings.RemoteDns)) _currentSettings.RemoteDns = "8.8.8.8";
        if (string.IsNullOrWhiteSpace(_currentSettings.DirectDns)) _currentSettings.DirectDns = "77.88.8.8";
        if (_currentSettings.ProxyPort <= 0 || _currentSettings.ProxyPort > 65535) _currentSettings.ProxyPort = 2080;

        _profiles.Clear();
        _standaloneProfiles.Clear();
        foreach (var p in _currentSettings.Profiles)
        {
            if (string.IsNullOrEmpty(p.Address) && !string.IsNullOrEmpty(p.Link)) ParseLinkIntoProfile(p, p.Link);
            _profiles.Add(p);
            if (string.IsNullOrEmpty(p.SubscriptionId)) _standaloneProfiles.Add(p);
            _ = p.CheckPingAsync();
        }

        _subscriptions.Clear();
        foreach (var s in _currentSettings.Subscriptions)
        {
            s.Profiles.Clear();
            foreach (var sp in _profiles.Where(p => p.SubscriptionId == s.Id))
            {
                s.Profiles.Add(sp);
            }
            _subscriptions.Add(s);
        }

        if (_currentSettings.SelectedProfileIndex >= 0 && _currentSettings.SelectedProfileIndex < _profiles.Count)
        {
            ActiveProfile = _profiles[_currentSettings.SelectedProfileIndex];
        }

        RoutingDomainsList.Clear();
        foreach (var d in _currentSettings.RoutingDomains) RoutingDomainsList.Add(d);

        RoutingAppsList.Clear();
        foreach (var a in _currentSettings.RoutingApps) RoutingAppsList.Add(a);
    }

    private void ApplySettingsToUI()
    {
        _isUpdatingSettings = true;

        if (ActiveProfile != null && string.IsNullOrEmpty(ActiveProfile.SubscriptionId) && LvProfiles != null)
        {
            LvProfiles.SelectedItem = ActiveProfile;
        }

        if (CmbLanguage != null)
        {
            var targetTag = string.IsNullOrEmpty(_currentSettings.Language) ? "en" : _currentSettings.Language;
            CmbLanguage.SelectedItem = CmbLanguage.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == targetTag) ?? CmbLanguage.Items[0];
        }

        if (TxtDns != null) TxtDns.Text = _currentSettings.RemoteDns;
        if (TxtDirectDns != null) TxtDirectDns.Text = _currentSettings.DirectDns;
        if (ChkAutoStart != null) ChkAutoStart.IsChecked = _currentSettings.AutoStart;
        if (ChkAutoConnect != null) ChkAutoConnect.IsChecked = _currentSettings.AutoConnect;
        if (ChkStartMinimized != null) ChkStartMinimized.IsChecked = _currentSettings.StartMinimized;
        if (ChkMinimizeToTray != null) ChkMinimizeToTray.IsChecked = _currentSettings.MinimizeToTray;
        if (ChkAutoUpdateSubs != null) ChkAutoUpdateSubs.IsChecked = _currentSettings.AutoUpdateSubscriptions;
        if (ChkUseCustomConfig != null) ChkUseCustomConfig.IsChecked = _currentSettings.UseCustomConfig;
        if (ChkVisualEffects != null) ChkVisualEffects.IsChecked = _currentSettings.EnableVisualEffects;
        if (ChkDisableAnimations != null) ChkDisableAnimations.IsChecked = _currentSettings.DisableAnimations;
        if (CmbTheme != null)
        {
            var themeTag = string.IsNullOrEmpty(_currentSettings.Theme) ? "System" : _currentSettings.Theme;
            CmbTheme.SelectedItem = CmbTheme.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == themeTag) ?? CmbTheme.Items[0];
        }
        if (CmbMode != null) CmbMode.SelectedIndex = _currentSettings.Mode == "Proxy" ? 1 : 0;
        if (TxtProxyPort != null) TxtProxyPort.Text = _currentSettings.ProxyPort.ToString();
        if (ChkStrictRoute != null) ChkStrictRoute.IsChecked = _currentSettings.StrictRoute;
        if (ChkEnableIpv6 != null) ChkEnableIpv6.IsChecked = _currentSettings.EnableIpv6;
        if (ChkAllowLan != null) ChkAllowLan.IsChecked = _currentSettings.AllowLan;

        if (CmbTunStack != null)
            CmbTunStack.SelectedItem = CmbTunStack.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _currentSettings.TunStack) ?? CmbTunStack.Items[0];

        if (CmbLogLevel != null)
            CmbLogLevel.SelectedItem = CmbLogLevel.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _currentSettings.LogLevel) ?? CmbLogLevel.Items[0];

        _isUpdatingSettings = false;
        UpdateDynamicTexts();
    }

    private void UpdateEmptyState()
    {
        if (EmptyStatePanel != null)
            EmptyStatePanel.Visibility = _standaloneProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (EmptySubscriptionsPanel != null)
            EmptySubscriptionsPanel.Visibility = _subscriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string GetTrayModeText()
    {
        return string.Format(L("TrayModeFmt"), _currentSettings.Mode);
    }

    private void UpdateDynamicTexts()
    {
        if (ChkAutoConnect != null)
        {
            bool isProxy = _currentSettings.Mode == "Proxy";
            ChkAutoConnect.Content = isProxy ? L("AutoConnectProxy") : L("AutoConnectVpn");
        }

        if (_trayModeItem != null)
            _trayModeItem.Text = GetTrayModeText();
    }

    private void SyncSettingsFromUI()
    {
        _currentSettings.Profiles = _profiles.ToList();
        _currentSettings.Subscriptions = _subscriptions.ToList();
        _currentSettings.SelectedProfileIndex = ActiveProfile != null ? _profiles.IndexOf(ActiveProfile) : -1;

        if (CmbTheme?.SelectedItem is ComboBoxItem thItem && thItem.Tag != null) _currentSettings.Theme = thItem.Tag.ToString() ?? "System";
        if (CmbMode != null) _currentSettings.Mode = CmbMode.SelectedIndex == 1 ? "Proxy" : "TUN";
        if (TxtDns != null) _currentSettings.RemoteDns = string.IsNullOrWhiteSpace(TxtDns.Text) ? "8.8.8.8" : TxtDns.Text.Trim();
        if (TxtDirectDns != null) _currentSettings.DirectDns = string.IsNullOrWhiteSpace(TxtDirectDns.Text) ? "77.88.8.8" : TxtDirectDns.Text.Trim();

        if (ChkAutoStart != null) _currentSettings.AutoStart = ChkAutoStart.IsChecked ?? false;
        if (ChkAutoConnect != null) _currentSettings.AutoConnect = ChkAutoConnect.IsChecked ?? false;
        if (ChkStartMinimized != null) _currentSettings.StartMinimized = ChkStartMinimized.IsChecked ?? false;
        if (ChkMinimizeToTray != null) _currentSettings.MinimizeToTray = ChkMinimizeToTray.IsChecked ?? true;
        if (ChkAutoUpdateSubs != null) _currentSettings.AutoUpdateSubscriptions = ChkAutoUpdateSubs.IsChecked ?? true;
        if (ChkUseCustomConfig != null) _currentSettings.UseCustomConfig = ChkUseCustomConfig.IsChecked ?? false;

        if (ChkVisualEffects != null) _currentSettings.EnableVisualEffects = ChkVisualEffects.IsChecked ?? false;
        if (ChkDisableAnimations != null) _currentSettings.DisableAnimations = ChkDisableAnimations.IsChecked ?? false;

        if (TxtProxyPort != null && int.TryParse(TxtProxyPort.Text, out int port) && port is >= 1 and <= 65535) _currentSettings.ProxyPort = port;
        if (ChkStrictRoute != null) _currentSettings.StrictRoute = ChkStrictRoute.IsChecked ?? true;
        if (ChkEnableIpv6 != null) _currentSettings.EnableIpv6 = ChkEnableIpv6.IsChecked ?? false;
        if (ChkAllowLan != null) _currentSettings.AllowLan = ChkAllowLan.IsChecked ?? false;

        if (CmbTunStack?.SelectedItem is ComboBoxItem tsItem && tsItem.Tag != null) _currentSettings.TunStack = tsItem.Tag.ToString() ?? "system";
        if (CmbLogLevel?.SelectedItem is ComboBoxItem llItem && llItem.Tag != null) _currentSettings.LogLevel = llItem.Tag.ToString() ?? "info";
    }

    private void FlushSaveSettingsSync()
    {
        try
        {
            _saveSettingsCts?.Cancel();
            SyncSettingsFromUI();
            string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_exeDirectory, "settings.json"), json);
        }
        catch { }
    }

    private async void SaveSettings()
    {
        if (_isUpdatingSettings) return;

        try
        {
            SyncSettingsFromUI();

            _saveSettingsCts?.Cancel();
            _saveSettingsCts = new CancellationTokenSource();
            var token = _saveSettingsCts.Token;

            await Task.Delay(500, token);

            string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(_exeDirectory, "settings.json"), json, token);

            if (_lastAppliedAutoStart != _currentSettings.AutoStart)
            {
                bool targetAutoStart = _currentSettings.AutoStart;
                _lastAppliedAutoStart = targetAutoStart;
                await ManageAutoStartAsync(targetAutoStart);
            }
        }
        catch (TaskCanceledException) { }
        catch { }
    }

    private static async Task ManageAutoStartAsync(bool enable)
    {
        try
        {
            await Task.Run(() =>
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath)) return;

                string taskName = $"NeonV_Autostart_{Environment.UserName}";

                if (enable)
                {
                    string userId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    string safeUserId = SecurityElement.Escape(userId) ?? userId;
                    string safeExePath = SecurityElement.Escape(exePath) ?? exePath;
                    string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{safeUserId}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{safeUserId}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
  <Actions Context=""Author""><Exec><Command>{safeExePath}</Command><Arguments>--autostart</Arguments></Exec></Actions>
</Task>";
                    string tempFile = Path.Combine(Path.GetTempPath(), $"{taskName}.xml");
                    File.WriteAllText(tempFile, xml);

                    var psi = new ProcessStartInfo("schtasks.exe", $"/create /tn \"{taskName}\" /xml \"{tempFile}\" /f")
                    {
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit();
                    try { File.Delete(tempFile); } catch { }
                }
                else
                {
                    var psi = new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{taskName}\" /f")
                    {
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit();
                }
            });
        }
        catch { }
    }

    private void ShowNotification(string message, bool isError = false)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            _notificationCts?.Cancel();
            _notificationCts = new CancellationTokenSource();
            var token = _notificationCts.Token;

            if (TxtNotification != null) TxtNotification.Text = message;
            if (FindResource("TextColor") is Brush textColor && TxtNotification != null)
                TxtNotification.Foreground = isError ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : textColor;

            if (NotificationSnackbar != null)
            {
                NotificationSnackbar.IsHitTestVisible = true;
                NotificationSnackbar.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
                try
                {
                    await Task.Delay(3000, token);
                    NotificationSnackbar.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2) });
                    NotificationSnackbar.IsHitTestVisible = false;
                }
                catch (TaskCanceledException) { }
            }
        });
    }

    private void NumberValidation(object sender, TextCompositionEventArgs e) => e.Handled = !int.TryParse(e.Text, out _);

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        if (CmbLanguage?.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            ApplyLanguage(lang);
            SaveSettings();
        }
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        if (CmbTheme?.SelectedItem is ComboBoxItem item && item.Tag is string themeTag)
        {
            _currentSettings.Theme = themeTag;
            ApplyTheme(themeTag);
            SaveSettings();
        }
    }

    private void ChkVisualEffects_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        _currentSettings.EnableVisualEffects = ChkVisualEffects?.IsChecked ?? false;
        ApplyVisualEffects();
        SaveSettings();
    }

    private async void CmbSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        if (_isRunning && _currentSettings.Mode != "Proxy")
        {
            ShowNotification(L("NotifySettingsReconnecting"));
            await RestartVpnAsync();
        }
    }

    private async void CmbLogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        if (_isRunning)
        {
            ShowNotification(L("NotifyLogLevelReconnecting"));
            await RestartVpnAsync();
        }
    }

    private async void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        UpdateButtonText();
        UpdateDynamicTexts();

        if (_isRunning)
        {
            ShowNotification(L("NotifyModeReconnecting"));
            await RestartVpnAsync();
        }
    }

    private void TxtSettings_Changed(object sender, RoutedEventArgs e) => SaveSettings();

    private async void ChkReconnectSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        if (_isRunning)
        {
            ShowNotification(L("NotifySettingsReconnecting"));
            await RestartVpnAsync();
        }
    }

    private async void ChkTunReconnectSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSettings) return;
        SaveSettings();
        if (_isRunning && _currentSettings.Mode != "Proxy")
        {
            ShowNotification(L("NotifySettingsReconnecting"));
            await RestartVpnAsync();
        }
    }

    private string _runningDns = "8.8.8.8";
    private string _runningDirectDns = "77.88.8.8";
    private int _runningProxyPort = 2080;
    private int _runningConfiguredProxyPort = 2080;

    private async void TxtReconnectSetting_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSettings || !_isRunning) return;

        string newDns = string.IsNullOrWhiteSpace(TxtDns?.Text) ? "8.8.8.8" : TxtDns.Text.Trim();
        string newDirectDns = string.IsNullOrWhiteSpace(TxtDirectDns?.Text) ? "77.88.8.8" : TxtDirectDns.Text.Trim();
        int newPort = TxtProxyPort != null && int.TryParse(TxtProxyPort.Text, out int p) && p is >= 1 and <= 65535 ? p : 2080;

        bool dnsChanged = !string.Equals(newDns, _runningDns, StringComparison.OrdinalIgnoreCase);
        bool directDnsChanged = !string.Equals(newDirectDns, _runningDirectDns, StringComparison.OrdinalIgnoreCase);
        bool portChanged = _currentSettings.Mode == "Proxy" && newPort != _runningConfiguredProxyPort;

        if (dnsChanged || directDnsChanged || portChanged)
        {
            SaveSettings();
            ShowNotification(L("NotifySettingsReconnecting"));
            await RestartVpnAsync();
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CollectionViewSource.GetDefaultView(_standaloneProfiles) is { } view)
        {
            view.Filter = item =>
            {
                if (string.IsNullOrWhiteSpace(TxtSearch?.Text)) return true;
                var p = (VpnProfile)item;
                return p.Name.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) ||
                       p.DisplayName.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) ||
                       p.Address.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase);
            };
        }
    }

    private async void LvProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSettings) return;

        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            bool serverChanged = ActiveProfile != p;
            ActiveProfile = p;
            RefreshAllVisualIndicators();
            SaveSettings();
            _ = p.CheckPingAsync();

            if (_isRunning && serverChanged)
            {
                ShowNotification(L("NotifyReconnectingServer"));
                await RestartVpnAsync();
            }
        }
    }

    private async void LvProfiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (LvProfiles?.SelectedItem is VpnProfile p)
        {
            if (e.Key == Key.Delete)
            {
                bool wasActive = ActiveProfile == p;
                _profiles.Remove(p);
                _standaloneProfiles.Remove(p);
                if (wasActive)
                {
                    ActiveProfile = _standaloneProfiles.FirstOrDefault() ?? _profiles.FirstOrDefault();
                    RefreshAllVisualIndicators();
                    if (_isRunning) await StopVPNAsync();
                }
                SaveSettings();
                UpdateEmptyState();
            }
            else if (e.Key == Key.Enter)
            {
                if (!_isRunning) await ToggleVpnAsync();
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                MenuItem_CopyLink_Click(null!, null!);
            }
        }
    }

    private void UpdateButtonText()
    {
        if (BtnToggleText == null || BtnToggle == null) return;
        string mode = _currentSettings.Mode;

        if (_isRunning)
        {
            BtnToggleText.Text = mode == "Proxy" ? L("BtnDisconnectProxy") : L("BtnDisconnectVpn");
            BtnToggle.Background = new SolidColorBrush(Color.FromRgb(179, 38, 30));
            if (_trayToggleItem != null) _trayToggleItem.Text = mode == "Proxy" ? L("TrayDisconnectProxy") : L("TrayDisconnectVpn");
        }
        else
        {
            BtnToggleText.Text = mode == "Proxy" ? L("BtnConnectProxy") : L("BtnConnectVpn");
            BtnToggle.SetResourceReference(Control.BackgroundProperty, "ToggleBtnBgColor");
            if (_trayToggleItem != null) _trayToggleItem.Text = mode == "Proxy" ? L("TrayConnectProxy") : L("TrayConnectVpn");
        }
        ApplyVisualEffects();
    }

    private void SwitchTab(Grid? target)
    {
        if (target != null && target.Visibility == Visibility.Visible) return;

        if (ViewMain != null) ViewMain.Visibility = Visibility.Collapsed;
        if (ViewSubscriptions != null) ViewSubscriptions.Visibility = Visibility.Collapsed;
        if (ViewSettings != null) ViewSettings.Visibility = Visibility.Collapsed;
        if (ViewLogs != null) ViewLogs.Visibility = Visibility.Collapsed;

        if (SharedConnectBar != null)
            SharedConnectBar.Visibility = (target == ViewMain || target == ViewSubscriptions) ? Visibility.Visible : Visibility.Collapsed;

        if (target != null)
        {
            target.Visibility = Visibility.Visible;
            target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation { From = 0, To = 1, Duration = GetDuration(0.25) });
        }
    }

    private void UpdateMenuIndicator(Button? activeBtn, double targetY)
    {
        if (FindResource("TextMutedColor") is Brush muted && FindResource("AccentColor") is Brush accent)
        {
            if (BtnNavMain != null) BtnNavMain.Foreground = muted;
            if (BtnNavSubscriptions != null) BtnNavSubscriptions.Foreground = muted;
            if (BtnNavSettings != null) BtnNavSettings.Foreground = muted;
            if (BtnNavLogs != null) BtnNavLogs.Foreground = muted;
            if (activeBtn != null) activeBtn.Foreground = accent;
        }

        MenuIndicator?.RenderTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = targetY, Duration = GetDuration(0.25), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void BtnNavMain_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewMain); UpdateMenuIndicator(BtnNavMain, 0); }
    private void BtnNavSubscriptions_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewSubscriptions); UpdateMenuIndicator(BtnNavSubscriptions, 48); }
    private void BtnNavSettings_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewSettings); UpdateMenuIndicator(BtnNavSettings, 96); }
    private void BtnNavLogs_Click(object sender, RoutedEventArgs e) { SwitchTab(ViewLogs); UpdateMenuIndicator(BtnNavLogs, 144); }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_fullScreenOverlay != null) { _fullScreenOverlay.Close(); _fullScreenOverlay = null; e.Handled = true; return; }
            if (OverlayQR?.Visibility == Visibility.Visible) { CloseQROverlay(); e.Handled = true; return; }
            if (OverlayRouting?.Visibility == Visibility.Visible) { CloseRoutingOverlay(false); e.Handled = true; return; }
            if (OverlaySubscription?.Visibility == Visibility.Visible) { CloseSubscriptionOverlay(); e.Handled = true; return; }
            if (OverlayImport?.Visibility == Visibility.Visible) { CloseImportOverlay(); e.Handled = true; return; }
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && OverlayImport?.Visibility != Visibility.Visible && OverlayRouting?.Visibility != Visibility.Visible && OverlayQR?.Visibility != Visibility.Visible && OverlaySubscription?.Visibility != Visibility.Visible)
        {
            PerformClipboardImport();
            e.Handled = true;
        }
    }

    private async Task RestartVpnAsync()
    {
        await _vpnStateSemaphore.WaitAsync();
        try
        {
            if (!_isRunning) return;

            if (BtnToggle != null) BtnToggle.IsEnabled = false;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = false;

            await StopVpnCoreAsync();
            await Task.Delay(400);
            await StartVpnCoreAsync();
        }
        finally
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = true;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
            _vpnStateSemaphore.Release();
        }
    }

    private void BtnOpenImport_Click(object sender, RoutedEventArgs e)
    {
        _editingProfile = null;
        if (TxtImportTitle != null) TxtImportTitle.Text = L("ImportTitle");
        if (BtnPasteImport != null) BtnPasteImport.Visibility = Visibility.Visible;
        if (ImportSeparator != null) ImportSeparator.Visibility = Visibility.Visible;
        if (TxtOrManual != null) TxtOrManual.Visibility = Visibility.Visible;
        if (OverlayImport != null) OverlayImport.Visibility = Visibility.Visible;
        if (CmbProtocol != null) CmbProtocol.SelectedIndex = 0;

        UpdateManualFields("vless");

        OverlayImportBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayImportCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayImportTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseImportOverlay()
    {
        _editingProfile = null;
        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlayImport != null) OverlayImport.Visibility = Visibility.Collapsed; };

        OverlayImportBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayImportCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayImportTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlayImportBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseImportOverlay();
    private void BtnCancelImport_Click(object sender, RoutedEventArgs e) => CloseImportOverlay();
    private void BtnPasteFromClipboard_Click(object sender, RoutedEventArgs e) => PerformClipboardImport();

    private void BtnToggleExpandSubscription_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement header && header.Tag is Subscription sub)
        {
            sub.IsExpanded = !sub.IsExpanded;
            SaveSettings();

            if (LogicalTreeHelper.FindLogicalNode(header, "SubChevron") is UIElement chevron &&
                chevron.RenderTransform is RotateTransform rot)
            {
                rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
                {
                    To = sub.IsExpanded ? 90.0 : 0.0,
                    Duration = GetDuration(0.24),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            var container = header.Parent is DependencyObject parent
                ? LogicalTreeHelper.FindLogicalNode(parent, "SubProfilesContainer") as FrameworkElement
                : null;

            if (container == null)
            {
                sub.NotifyAll();
                return;
            }

            if (container.LayoutTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(1, sub.IsExpanded ? 0 : 1);
                container.LayoutTransform = scale;
            }

            if (sub.IsExpanded)
            {
                if (container.Visibility != Visibility.Visible)
                {
                    container.Opacity = 0;
                    scale.ScaleY = 0;
                    container.Visibility = Visibility.Visible;
                }
                container.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    To = 1,
                    Duration = GetDuration(0.22),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
                {
                    To = 1,
                    Duration = GetDuration(0.26),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                container.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    To = 0,
                    Duration = GetDuration(0.18),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                });
                var collapseAnim = new DoubleAnimation
                {
                    To = 0,
                    Duration = GetDuration(0.24),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                collapseAnim.Completed += (_, _) =>
                {
                    if (!sub.IsExpanded)
                        container.Visibility = Visibility.Collapsed;
                };
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, collapseAnim);
            }
        }
    }

    private async void SubProfile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VpnProfile profile })
        {
            bool serverChanged = ActiveProfile != profile;
            ActiveProfile = profile;
            _isUpdatingSettings = true;
            if (LvProfiles != null) LvProfiles.SelectedItem = null;
            _isUpdatingSettings = false;

            RefreshAllVisualIndicators();
            SaveSettings();
            _ = profile.CheckPingAsync();

            if (_isRunning && serverChanged)
            {
                ShowNotification(L("NotifyReconnectingServer"));
                await RestartVpnAsync();
            }
            else if (e.ClickCount == 2 && !_isRunning)
            {
                await StartVPNAsync();
            }
        }
    }

    private void RefreshAllVisualIndicators()
    {
        foreach (var p in _profiles) p.NotifyVisuals();
    }

    private void BtnOpenAddSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (TxtSubscriptionName != null) TxtSubscriptionName.Text = "";
        if (TxtSubscriptionUrl != null) TxtSubscriptionUrl.Text = "";

        if (OverlaySubscription != null) OverlaySubscription.Visibility = Visibility.Visible;
        OverlaySubscriptionBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlaySubscriptionCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlaySubscriptionTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseSubscriptionOverlay()
    {
        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlaySubscription != null) OverlaySubscription.Visibility = Visibility.Collapsed; };

        OverlaySubscriptionBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlaySubscriptionCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlaySubscriptionTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlaySubscriptionBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseSubscriptionOverlay();
    private void BtnCancelSubscription_Click(object sender, RoutedEventArgs e) => CloseSubscriptionOverlay();

    private async void BtnSaveSubscription_Click(object sender, RoutedEventArgs e)
    {
        string url = TxtSubscriptionUrl?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ShowNotification(L("NotifySubInvalidUrl"), true);
            return;
        }

        string name = TxtSubscriptionName?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            try { name = new Uri(url).Host; } catch { name = "Subscription"; }
        }

        var sub = new Subscription { Name = name, Url = url, IsExpanded = true };

        _subscriptions.Add(sub);
        SaveSettings();
        UpdateEmptyState();
        CloseSubscriptionOverlay();

        ShowNotification(L("NotifySubLoading"));
        await UpdateSubscriptionAsync(sub);
    }

    private async void BtnPingSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Subscription sub } btn) return;
        if (sub.Profiles.Count == 0) return;

        btn.IsEnabled = false;
        try
        {
            PingManager.Clear();
            var list = sub.Profiles.ToList();
            foreach (var p in list)
            {
                p.PingText = "...";
                p.ResetGeoIpRetry();
            }

            await Task.WhenAll(list.Select(p => p.CheckPingAsync(bypassCache: true)));
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void BtnUpdateSingleSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Subscription sub })
        {
            await UpdateSubscriptionAsync(sub);
        }
    }

    private async void BtnUpdateAllSubscriptions_Click(object sender, RoutedEventArgs e)
    {
        if (_subscriptions.Count == 0) return;
        ShowNotification(L("NotifySubAllUpdating"));
        foreach (var sub in _subscriptions.ToList())
        {
            await UpdateSubscriptionAsync(sub, false);
        }
        ShowNotification(L("NotifySubAllUpdated"));
    }

    private async void BtnDeleteSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Subscription sub })
        {
            bool wasActiveFromSub = ActiveProfile != null && ActiveProfile.SubscriptionId == sub.Id;

            _subscriptions.Remove(sub);
            var profilesToRemove = _profiles.Where(p => p.SubscriptionId == sub.Id).ToList();
            foreach (var p in profilesToRemove) _profiles.Remove(p);

            if (wasActiveFromSub)
            {
                ActiveProfile = _standaloneProfiles.FirstOrDefault() ?? _profiles.FirstOrDefault();
                RefreshAllVisualIndicators();
                if (_isRunning) await StopVPNAsync();
            }

            SaveSettings();
            UpdateEmptyState();
            ShowNotification(L("NotifySubDeleted"));
        }
    }

    private static string SafeBase64Decode(string input)
    {
        try
        {
            input = Regex.Replace(input.Trim(), @"\s+", "").Replace("-", "+").Replace("_", "/");
            int mod4 = input.Length % 4;
            if (mod4 > 0) input += new string('=', 4 - mod4);
            byte[] bytes = Convert.FromBase64String(input);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    private sealed record ClientFetchIdentity(string UserAgent, string Os, string VerOs, string Model, string? AppVersion);

    private sealed record SubscriptionFetchResult(
        HttpStatusCode StatusCode,
        string Content,
        string? UserInfo,
        string? ProfileTitle,
        bool MaxDevicesReached,
        bool HwidNotSupported);

    private static async Task<SubscriptionFetchResult> FetchWithHeadersAndRedirectsAsync(string url, ClientFetchIdentity id, string hwid)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        Uri currentUri = new(url);
        HttpResponseMessage? response = null;

        for (int redirect = 0; redirect < 6; redirect++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.TryAddWithoutValidation("User-Agent", id.UserAgent);
            req.Headers.TryAddWithoutValidation("x-hwid", hwid);
            req.Headers.TryAddWithoutValidation("x-device-os", id.Os);
            req.Headers.TryAddWithoutValidation("x-ver-os", id.VerOs);
            req.Headers.TryAddWithoutValidation("x-device-model", id.Model);
            req.Headers.TryAddWithoutValidation("x-device-locale", CurrentLanguage);
            if (!string.IsNullOrEmpty(id.AppVersion))
                req.Headers.TryAddWithoutValidation("x-app-version", id.AppVersion);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");

            response = await client.SendAsync(req);
            int code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location != null)
            {
                Uri nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                response.Dispose();
                currentUri = nextUri;
                continue;
            }
            break;
        }

        if (response == null)
            return new SubscriptionFetchResult(HttpStatusCode.ServiceUnavailable, "", null, null, false, false);

        using (response)
        {
            bool maxDevices =
                (response.Headers.TryGetValues("x-hwid-max-devices-reached", out var maxDev) && maxDev.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase))) ||
                (response.Headers.TryGetValues("x-hwid-limit", out var limDev) && limDev.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)));

            bool hwidUnsupported =
                response.Headers.TryGetValues("x-hwid-not-supported", out var notSup) && notSup.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase));

            string? userInfo = null;
            if (response.Headers.TryGetValues("subscription-userinfo", out var hVals))
                userInfo = string.Join(";", hVals);
            else if (response.Content.Headers.TryGetValues("subscription-userinfo", out var cVals))
                userInfo = string.Join(";", cVals);

            string? profileTitle = null;
            if (response.Headers.TryGetValues("profile-title", out var tVals))
                profileTitle = tVals.FirstOrDefault();

            string content = await response.Content.ReadAsStringAsync();
            return new SubscriptionFetchResult(response.StatusCode, content, userInfo, profileTitle, maxDevices, hwidUnsupported);
        }
    }

    private static void ApplySubscriptionUserInfo(Subscription sub, string info)
    {
        foreach (var part in info.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2)
            {
                string k = kv[0].Trim().ToLowerInvariant();
                string v = kv[1].Trim();
                if (k == "upload" && long.TryParse(v, out long up)) sub.UploadBytes = up;
                else if (k == "download" && long.TryParse(v, out long dn)) sub.DownloadBytes = dn;
                else if (k == "total" && long.TryParse(v, out long tot)) sub.TotalBytes = tot;
                else if (k == "expire" && long.TryParse(v, out long exp) && exp > 0)
                {
                    try { sub.ExpireDate = DateTimeOffset.FromUnixTimeSeconds(exp).LocalDateTime; } catch { }
                }
            }
        }
    }

    private async Task UpdateSubscriptionAsync(Subscription sub, bool showNotify = true)
    {
        try
        {
            string hwid = SanitizeHwid(_currentSettings.Hwid);
            _currentSettings.Hwid = hwid;

            var identities = new[]
            {
                new ClientFetchIdentity("Happ/4.6.0", "Windows", "11", "PC", "4.6.0"),
                new ClientFetchIdentity("v2rayN/7.16.0", "Windows", "11", "PC", "7.16.0"),
                new ClientFetchIdentity("v2raytun/2.0", "Windows", "11", "PC", "2.0"),
                new ClientFetchIdentity("Happ/3.13.0", "Android", "15", "Pixel 8", "3.13.0"),
                new ClientFetchIdentity("FlClashX/0.2.0", "Windows", "11", "PC", "0.2.0"),
                new ClientFetchIdentity("HiddifyNext/2.5.7", "Windows", "11", "PC", "2.5.7")
            };

            List<VpnProfile>? parsedProfiles = null;
            SubscriptionFetchResult? lastResult = null;
            bool rejectedByClient = false;

            foreach (var id in identities)
            {
                var res = await FetchWithHeadersAndRedirectsAsync(sub.Url, id, hwid);
                lastResult = res;

                if (res.MaxDevicesReached)
                {
                    if (showNotify) ShowNotification(L("NotifySubDeviceLimit"), true);
                    return;
                }

                if (!string.IsNullOrEmpty(res.UserInfo))
                    ApplySubscriptionUserInfo(sub, res.UserInfo);

                if ((int)res.StatusCode < 200 || (int)res.StatusCode >= 300)
                    continue;

                if (res.Content.TrimStart().StartsWith("happ://crypt", StringComparison.OrdinalIgnoreCase))
                {
                    rejectedByClient = true;
                    continue;
                }

                var profiles = ParseSubscriptionContent(res.Content, sub, out bool sawUnsupportedStub);
                if (profiles.Count > 0)
                {
                    parsedProfiles = profiles;
                    break;
                }

                if (res.HwidNotSupported || sawUnsupportedStub)
                    rejectedByClient = true;
            }

            if (parsedProfiles == null || parsedProfiles.Count == 0)
            {
                if (lastResult != null && ((int)lastResult.StatusCode < 200 || (int)lastResult.StatusCode >= 300))
                {
                    if (showNotify) ShowNotification(string.Format(L("NotifySubLoadErrorFmt"), lastResult.StatusCode), true);
                }
                else if (rejectedByClient || (lastResult?.Content.Contains("App not supported", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    if (showNotify) ShowNotification(L("NotifySubAppUnsupported"), true);
                }
                else
                {
                    if (showNotify) ShowNotification(L("NotifySubNoProfiles"), true);
                }
                return;
            }

            if (lastResult?.ProfileTitle is { } rawTitle && !string.IsNullOrWhiteSpace(rawTitle) && sub.Name == GetHostSafe(sub.Url))
            {
                string title = rawTitle.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)
                    ? SafeBase64Decode(rawTitle[7..])
                    : rawTitle;
                if (!string.IsNullOrWhiteSpace(title)) sub.Name = title.Trim();
            }

            string? prevActiveName = ActiveProfile?.SubscriptionId == sub.Id ? ActiveProfile.Name : null;
            string? prevActiveHost = ActiveProfile?.SubscriptionId == sub.Id ? ActiveProfile.DisplayAddress : null;

            var oldProfiles = _profiles.Where(p => p.SubscriptionId == sub.Id).ToList();
            foreach (var op in oldProfiles) _profiles.Remove(op);
            sub.Profiles.Clear();

            foreach (var np in parsedProfiles)
            {
                np.SubscriptionName = sub.Name;
                _profiles.Add(np);
                sub.Profiles.Add(np);
            }

            if (prevActiveName != null)
            {
                ActiveProfile = parsedProfiles.FirstOrDefault(p => p.Name == prevActiveName && p.DisplayAddress == prevActiveHost)
                             ?? parsedProfiles.FirstOrDefault(p => p.Name == prevActiveName)
                             ?? parsedProfiles.FirstOrDefault();
                RefreshAllVisualIndicators();
            }

            sub.LastUpdated = DateTime.Now;
            sub.NotifyAll();

            SaveSettings();
            UpdateEmptyState();

            PingManager.Clear();
            _ = Task.WhenAll(parsedProfiles.Select(p => p.CheckPingAsync()));

            if (showNotify) ShowNotification(string.Format(L("NotifySubUpdatedCountFmt"), parsedProfiles.Count));
        }
        catch (Exception ex)
        {
            if (showNotify) ShowNotification(string.Format(L("NotifySubErrorFmt"), ex.Message), true);
        }
    }

    private static string GetHostSafe(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    private static bool IsStubProfile(VpnProfile p)
    {
        if ((p.Address is "0.0.0.0" or "127.0.0.1" or "localhost" or "::1") && p.Port <= 1)
            return true;

        if (p.Name.Contains("App not supported", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Unsupported app", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static List<VpnProfile> ParseSubscriptionContent(string content, Subscription sub, out bool sawUnsupportedStub)
    {
        sawUnsupportedStub = false;
        var result = new List<VpnProfile>();
        string raw = content.Trim();

        if (!raw.Contains("://") && !raw.StartsWith("{") && !raw.StartsWith("["))
        {
            string decoded = SafeBase64Decode(raw);
            if (!string.IsNullOrEmpty(decoded)) raw = decoded;
        }

        if (raw.StartsWith("{") || raw.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        ParseJsonConfigElement(elem, sub, result, ref sawUnsupportedStub);
                    }
                    if (result.Count > 0 || sawUnsupportedStub) return result;
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    ParseJsonConfigElement(doc.RootElement, sub, result, ref sawUnsupportedStub);
                    if (result.Count > 0 || sawUnsupportedStub) return result;
                }
            }
            catch { }
        }

        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#subscription-userinfo:", StringComparison.OrdinalIgnoreCase))
            {
                ApplySubscriptionUserInfo(sub, line[23..].Trim());
                continue;
            }
            if (line.StartsWith("#")) continue;

            var p = new VpnProfile();
            if (ParseLinkIntoProfile(p, line) && !string.IsNullOrEmpty(p.Address))
            {
                if (IsStubProfile(p))
                {
                    if (p.Name.Contains("App not supported", StringComparison.OrdinalIgnoreCase)) sawUnsupportedStub = true;
                    continue;
                }

                p.SubscriptionId = sub.Id;
                p.SubscriptionName = sub.Name;
                result.Add(p);
            }
        }

        return result;
    }

    private static void ParseJsonConfigElement(JsonElement root, Subscription sub, List<VpnProfile> result, ref bool sawUnsupportedStub)
    {
        string? topRemarks = root.TryGetProperty("remarks", out var rem) ? rem.GetString() : null;

        if (root.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in endpoints.EnumerateArray())
            {
                var p = ParseSingBoxEndpointToProfile(ep);
                if (p != null)
                {
                    if (!string.IsNullOrWhiteSpace(topRemarks) && p.Name == p.Protocol) p.Name = topRemarks;
                    if (IsStubProfile(p))
                    {
                        if (p.Name.Contains("App not supported", StringComparison.OrdinalIgnoreCase)) sawUnsupportedStub = true;
                        continue;
                    }
                    p.SubscriptionId = sub.Id;
                    p.SubscriptionName = sub.Name;
                    result.Add(p);
                }
            }
        }

        if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
        {
            foreach (var ob in outbounds.EnumerateArray())
            {
                var p = ParseJsonOutboundToProfile(ob) ?? ParseXrayOutboundToProfile(ob, topRemarks);
                if (p != null)
                {
                    if (IsStubProfile(p))
                    {
                        if (p.Name.Contains("App not supported", StringComparison.OrdinalIgnoreCase)) sawUnsupportedStub = true;
                        continue;
                    }

                    p.SubscriptionId = sub.Id;
                    p.SubscriptionName = sub.Name;
                    result.Add(p);
                }
            }
        }
    }

    private static VpnProfile? ParseSingBoxEndpointToProfile(JsonElement ep)
    {
        try
        {
            string type = ep.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type != "wireguard") return null;

            var p = new VpnProfile
            {
                Protocol = "wireguard",
                Name = ep.TryGetProperty("tag", out var tg) ? tg.GetString() ?? "wireguard" : "wireguard",
                PrivateKey = ep.TryGetProperty("private_key", out var pk) ? pk.GetString() ?? "" : "",
                Mtu = ep.TryGetProperty("mtu", out var mtu) ? mtu.GetInt32() : 1420
            };

            if (ep.TryGetProperty("address", out var addrArr) && addrArr.ValueKind == JsonValueKind.Array)
                p.LocalAddress = string.Join(",", addrArr.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)));

            if (ep.TryGetProperty("peers", out var peers) && peers.ValueKind == JsonValueKind.Array)
            {
                var peer = peers.EnumerateArray().FirstOrDefault();
                if (peer.ValueKind == JsonValueKind.Object)
                {
                    p.Address = peer.TryGetProperty("address", out var pa) ? pa.GetString() ?? "" : "";
                    p.Port = peer.TryGetProperty("port", out var pp) ? pp.GetInt32() : 51820;
                    p.PublicKey = peer.TryGetProperty("public_key", out var pub) ? pub.GetString() ?? "" : "";
                }
            }

            return string.IsNullOrEmpty(p.Address) ? null : p;
        }
        catch { return null; }
    }

    private static VpnProfile? ParseJsonOutboundToProfile(JsonElement ob)
    {
        try
        {
            string type = ob.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(type) || type is "direct" or "block" or "dns" or "selector" or "urltest") return null;

            var p = new VpnProfile
            {
                Protocol = type,
                Name = ob.TryGetProperty("tag", out var tg) ? tg.GetString() ?? type : type,
                Address = ob.TryGetProperty("server", out var s) ? s.GetString() ?? "" : "",
                Port = ob.TryGetProperty("server_port", out var pt) ? pt.GetInt32() : 443
            };

            if (ob.TryGetProperty("uuid", out var id)) p.Uuid = id.GetString() ?? "";
            if (ob.TryGetProperty("password", out var pw)) p.Password = pw.GetString() ?? "";
            if (string.IsNullOrEmpty(p.Password) && ob.TryGetProperty("auth_str", out var authStr)) p.Password = authStr.GetString() ?? "";
            if (ob.TryGetProperty("method", out var m)) p.Method = m.GetString() ?? p.Method;
            if (ob.TryGetProperty("flow", out var fl)) p.Flow = fl.GetString() ?? "";
            if (ob.TryGetProperty("private_key", out var pk)) p.PrivateKey = pk.GetString() ?? "";
            if (ob.TryGetProperty("peer_public_key", out var pub)) p.PublicKey = pub.GetString() ?? "";

            if (ob.TryGetProperty("up_mbps", out var upM) && upM.ValueKind == JsonValueKind.Number) p.Up = upM.GetInt32().ToString();
            if (ob.TryGetProperty("down_mbps", out var dnM) && dnM.ValueKind == JsonValueKind.Number) p.Down = dnM.GetInt32().ToString();
            if (ob.TryGetProperty("obfs", out var obfs))
            {
                if (obfs.ValueKind == JsonValueKind.Object)
                {
                    p.Obfs = obfs.TryGetProperty("type", out var ot) ? ot.GetString() ?? "" : "";
                    p.ObfsPassword = obfs.TryGetProperty("password", out var op) ? op.GetString() ?? "" : "";
                }
                else if (obfs.ValueKind == JsonValueKind.String)
                {
                    p.Obfs = obfs.GetString() ?? "";
                }
            }

            if (ob.TryGetProperty("tls", out var tls))
            {
                if (tls.TryGetProperty("enabled", out var en) && en.GetBoolean()) p.Security = "tls";
                if (tls.TryGetProperty("server_name", out var sn)) p.Sni = sn.GetString() ?? "";
                if (tls.TryGetProperty("insecure", out var ins)) p.Insecure = ins.GetBoolean();
                if (tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
                    p.Alpn = string.Join(",", alpn.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)));
                if (tls.TryGetProperty("utls", out var utls) && utls.TryGetProperty("fingerprint", out var fp)) p.Fp = fp.GetString() ?? "";
                if (tls.TryGetProperty("reality", out var reality))
                {
                    if (reality.TryGetProperty("public_key", out var pbk)) p.Pbk = pbk.GetString() ?? "";
                    if (reality.TryGetProperty("short_id", out var sid)) p.Sid = sid.GetString() ?? "";
                }
            }

            if (ob.TryGetProperty("transport", out var tr))
            {
                p.Transport = tr.TryGetProperty("type", out var trt) ? trt.GetString() ?? "tcp" : "tcp";
                p.Path = tr.TryGetProperty("path", out var pth) ? pth.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(p.Path) && tr.TryGetProperty("service_name", out var snm)) p.Path = snm.GetString() ?? "";
                if (tr.TryGetProperty("headers", out var hd) && hd.TryGetProperty("Host", out var hst)) p.Host = hst.GetString() ?? "";
                else if (tr.TryGetProperty("host", out var hst2))
                {
                    p.Host = hst2.ValueKind == JsonValueKind.Array
                        ? hst2.EnumerateArray().FirstOrDefault().GetString() ?? ""
                        : hst2.GetString() ?? "";
                }
            }

            return string.IsNullOrEmpty(p.Address) ? null : p;
        }
        catch { return null; }
    }

    private static VpnProfile? ParseXrayOutboundToProfile(JsonElement ob, string? fallbackName)
    {
        try
        {
            string protocol = ob.TryGetProperty("protocol", out var pr) ? pr.GetString()?.ToLowerInvariant() ?? "" : "";
            if (string.IsNullOrEmpty(protocol) || protocol is "freedom" or "blackhole" or "dns") return null;

            var p = new VpnProfile
            {
                Protocol = protocol == "hy2" ? "hysteria2" : protocol,
                Name = !string.IsNullOrWhiteSpace(fallbackName) ? fallbackName : (ob.TryGetProperty("tag", out var tg) ? tg.GetString() ?? protocol : protocol)
            };

            if (ob.TryGetProperty("settings", out var settings))
            {
                if (settings.TryGetProperty("vnext", out var vnext) && vnext.ValueKind == JsonValueKind.Array)
                {
                    var node = vnext.EnumerateArray().FirstOrDefault();
                    if (node.ValueKind == JsonValueKind.Object)
                    {
                        p.Address = node.TryGetProperty("address", out var add) ? add.GetString() ?? "" : "";
                        p.Port = node.TryGetProperty("port", out var pt) ? pt.GetInt32() : 443;
                        if (node.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
                        {
                            var u = users.EnumerateArray().FirstOrDefault();
                            if (u.ValueKind == JsonValueKind.Object)
                            {
                                p.Uuid = u.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                                p.Flow = u.TryGetProperty("flow", out var fl) ? fl.GetString() ?? "" : "";
                                p.AlterId = u.TryGetProperty("alterId", out var aid) ? aid.GetInt32() : 0;
                                if (u.TryGetProperty("security", out var sec)) p.Security = sec.GetString() ?? "auto";
                            }
                        }
                    }
                }
                else if (settings.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                {
                    var srv = servers.EnumerateArray().FirstOrDefault();
                    if (srv.ValueKind == JsonValueKind.Object)
                    {
                        p.Address = srv.TryGetProperty("address", out var add) ? add.GetString() ?? "" : "";
                        p.Port = srv.TryGetProperty("port", out var pt) ? pt.GetInt32() : 443;
                        p.Password = srv.TryGetProperty("password", out var pw) ? pw.GetString() ?? "" : "";
                        if (srv.TryGetProperty("method", out var md)) p.Method = md.GetString() ?? p.Method;
                    }
                }
            }

            if (ob.TryGetProperty("streamSettings", out var stream))
            {
                if (stream.TryGetProperty("network", out var net))
                    p.Transport = net.GetString()?.ToLowerInvariant() ?? "tcp";

                string streamSec = stream.TryGetProperty("security", out var sc) ? sc.GetString()?.ToLowerInvariant() ?? "" : "";
                if (!string.IsNullOrEmpty(streamSec) && streamSec != "none")
                {
                    if (p.Protocol != "vmess") p.Security = streamSec;
                    else if (streamSec == "tls") p.Security = "tls";
                }

                if (stream.TryGetProperty("tlsSettings", out var tls))
                {
                    if (tls.TryGetProperty("serverName", out var sn)) p.Sni = sn.GetString() ?? "";
                    if (tls.TryGetProperty("fingerprint", out var fp)) p.Fp = fp.GetString() ?? p.Fp;
                    if (tls.TryGetProperty("allowInsecure", out var ins)) p.Insecure = ins.GetBoolean();
                    if (tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
                        p.Alpn = string.Join(",", alpn.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)));
                }

                if (stream.TryGetProperty("realitySettings", out var reality))
                {
                    if (reality.TryGetProperty("serverName", out var sn)) p.Sni = sn.GetString() ?? "";
                    if (reality.TryGetProperty("fingerprint", out var fp)) p.Fp = fp.GetString() ?? p.Fp;
                    if (reality.TryGetProperty("publicKey", out var pbk)) p.Pbk = pbk.GetString() ?? "";
                    if (reality.TryGetProperty("shortId", out var sid)) p.Sid = sid.GetString() ?? "";
                }

                if (stream.TryGetProperty("wsSettings", out var ws))
                {
                    p.Path = ws.TryGetProperty("path", out var wp) ? wp.GetString() ?? "" : "";
                    if (ws.TryGetProperty("headers", out var hd) && hd.TryGetProperty("Host", out var wh)) p.Host = wh.GetString() ?? "";
                    if (string.IsNullOrEmpty(p.Host) && ws.TryGetProperty("host", out var wh2)) p.Host = wh2.GetString() ?? "";
                }
                else if (stream.TryGetProperty("grpcSettings", out var grpc))
                {
                    p.Path = grpc.TryGetProperty("serviceName", out var snm) ? snm.GetString() ?? "" : "";
                }
                else if (stream.TryGetProperty("httpupgradeSettings", out var hu))
                {
                    p.Path = hu.TryGetProperty("path", out var hp) ? hp.GetString() ?? "" : "";
                    p.Host = hu.TryGetProperty("host", out var hh) ? hh.GetString() ?? "" : "";
                }
            }

            return string.IsNullOrEmpty(p.Address) ? null : p;
        }
        catch { return null; }
    }

    private void ShowQROverlay(string link)
    {
        _currentQrLink = link;

        if (ImgQR != null) ImgQR.Source = null;

        if (link.Length > 800)
        {
            ShowNotification(L("NotifyQrTooLong"), true);
        }
        else
        {
            try
            {
                var qrGenerator = new QRCodeGenerator();
                var qrCodeData = qrGenerator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);

                var qrCode = new QRCode(qrCodeData);
                using var bitmap = qrCode.GetGraphic(20);

                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                if (ImgQR != null) ImgQR.Source = bmp;
            }
            catch (Exception)
            {
                ShowNotification(L("NotifyQrError"), true);
            }
        }

        if (OverlayQR != null) OverlayQR.Visibility = Visibility.Visible;
        OverlayQRBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayQRCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayQRTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseQROverlay()
    {
        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlayQR != null) OverlayQR.Visibility = Visibility.Collapsed; };

        OverlayQRBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayQRCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayQRTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlayQRBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseQROverlay();

    private void BtnCopyLinkFromQR_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentQrLink))
        {
            Clipboard.SetText(_currentQrLink);
            ShowNotification(L("NotifyCopied"));
            CloseQROverlay();
        }
    }

    private void UpdateBypassRuVisibility(bool show, bool animate)
    {
        if (BypassRuContainer == null || BypassRuScale == null || BypassRuTransform == null) return;

        if (!animate)
        {
            BypassRuContainer.BeginAnimation(OpacityProperty, null);
            BypassRuScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BypassRuTransform.BeginAnimation(TranslateTransform.YProperty, null);

            BypassRuScale.ScaleY = show ? 1.0 : 0.0;
            BypassRuContainer.Opacity = show ? 1.0 : 0.0;
            BypassRuTransform.Y = show ? 0.0 : 14.0;
            BypassRuContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (show)
        {
            if (BypassRuContainer.Visibility != Visibility.Visible)
            {
                BypassRuScale.ScaleY = 0.0;
                BypassRuContainer.Opacity = 0.0;
                BypassRuTransform.Y = 14.0;
                BypassRuContainer.Visibility = Visibility.Visible;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            BypassRuScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
            {
                To = 1.0,
                Duration = GetDuration(0.25),
                EasingFunction = ease
            });
            BypassRuContainer.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1.0,
                Duration = GetDuration(0.22),
                EasingFunction = ease
            });
            BypassRuTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = 0.0,
                Duration = GetDuration(0.25),
                EasingFunction = ease
            });
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

            var scaleAnim = new DoubleAnimation
            {
                To = 0.0,
                Duration = GetDuration(0.22),
                EasingFunction = ease
            };
            scaleAnim.Completed += (_, _) =>
            {
                if (CmbRouteMode != null && CmbRouteMode.SelectedIndex != 0)
                    BypassRuContainer.Visibility = Visibility.Collapsed;
            };

            BypassRuScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            BypassRuContainer.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 0.0,
                Duration = GetDuration(0.18),
                EasingFunction = ease
            });
            BypassRuTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = 14.0,
                Duration = GetDuration(0.22),
                EasingFunction = ease
            });
        }
    }

    private void CmbRouteMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingRouting || CmbRouteMode == null) return;
        UpdateBypassRuVisibility(CmbRouteMode.SelectedIndex == 0, animate: true);
    }

    private void BtnOpenRouting_Click(object sender, RoutedEventArgs e)
    {
        RoutingDomainsList.Clear();
        foreach (var d in _currentSettings.RoutingDomains) RoutingDomainsList.Add(d);

        RoutingAppsList.Clear();
        foreach (var a in _currentSettings.RoutingApps) RoutingAppsList.Add(a);

        _isUpdatingRouting = true;
        if (OverlayRouting != null) OverlayRouting.Visibility = Visibility.Visible;
        if (ChkBypassRu != null) ChkBypassRu.IsChecked = _currentSettings.BypassRuDomains;
        bool isOnlyMode = _currentSettings.RouteMode == "Only";
        if (CmbRouteMode != null) CmbRouteMode.SelectedIndex = isOnlyMode ? 1 : 0;
        UpdateBypassRuVisibility(!isOnlyMode, animate: false);
        _isUpdatingRouting = false;

        OverlayRoutingBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayRoutingCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
        OverlayRoutingTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseRoutingOverlay(bool saveChanges)
    {
        if (saveChanges)
        {
            bool isOnlyMode = CmbRouteMode?.SelectedIndex == 1;
            bool newBypassRu = !isOnlyMode ? (ChkBypassRu?.IsChecked ?? false) : _currentSettings.BypassRuDomains;
            bool bypassRuChanged = !isOnlyMode && _currentSettings.BypassRuDomains != newBypassRu;
            bool modeChanged = (_currentSettings.RouteMode == "Only") != isOnlyMode;
            var domainsChanged = !new HashSet<string>(_currentSettings.RoutingDomains).SetEquals(RoutingDomainsList);
            var appsChanged = !new HashSet<string>(_currentSettings.RoutingApps).SetEquals(RoutingAppsList);

            if (bypassRuChanged || modeChanged || domainsChanged || appsChanged)
            {
                _currentSettings.BypassRuDomains = newBypassRu;
                _currentSettings.RouteMode = isOnlyMode ? "Only" : "Bypass";
                _currentSettings.RoutingDomains = RoutingDomainsList.ToList();
                _currentSettings.RoutingApps = RoutingAppsList.ToList();
                SaveSettings();

                if (_isRunning)
                {
                    ShowNotification(L("NotifyRoutesReconnecting"));
                    _ = RestartVpnAsync();
                }
            }
        }

        var fadeOut = new DoubleAnimation { To = 0, Duration = GetDuration(0.2) };
        fadeOut.Completed += (_, _) => { if (OverlayRouting != null) OverlayRouting.Visibility = Visibility.Collapsed; };

        OverlayRoutingBg?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayRoutingCard?.BeginAnimation(OpacityProperty, fadeOut);
        OverlayRoutingTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 30, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void OverlayRoutingBg_MouseDown(object sender, MouseButtonEventArgs e) => CloseRoutingOverlay(false);
    private void BtnCloseRouting_Click(object sender, RoutedEventArgs e) => CloseRoutingOverlay(true);

    private void TxtNewDomain_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnAddDomain_Click(null, null); e.Handled = true; }
    }

    private void TxtNewApp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnAddApp_Click(null, null); e.Handled = true; }
    }

    private void BtnAddDomain_Click(object? sender, RoutedEventArgs? e)
    {
        if (TxtNewDomain == null) return;
        string domain = TxtNewDomain.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain)) return;

        if (!IsIpOrCidr(domain))
        {
            if ((domain.StartsWith("http://") || domain.StartsWith("https://")) && Uri.TryCreate(domain, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                domain = uri.Host.ToLowerInvariant();
            else
            {
                int slash = domain.IndexOf('/');
                if (slash > 0) domain = domain[..slash];
            }
            if (domain.StartsWith("*.")) domain = domain[2..];
            domain = domain.Trim().Trim('.');
        }

        if (!string.IsNullOrEmpty(domain) && !RoutingDomainsList.Contains(domain))
        {
            RoutingDomainsList.Add(domain);
            TxtNewDomain.Text = "";
        }
    }

    private void BtnAddApp_Click(object? sender, RoutedEventArgs? e)
    {
        if (TxtNewApp == null) return;
        string app = TxtNewApp.Text.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(app) && !RoutingAppsList.Contains(app))
        {
            if (!app.EndsWith(".exe")) app += ".exe";
            RoutingAppsList.Add(app);
            TxtNewApp.Text = "";
        }
    }

    private void BtnRemoveDomain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string domain }) RoutingDomainsList.Remove(domain);
    }

    private void BtnRemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string app }) RoutingAppsList.Remove(app);
    }

    private void PerformClipboardImport()
    {
        if (!Clipboard.ContainsText()) return;

        string text = Clipboard.GetText().Trim();
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        VpnProfile? lastAdded = null;

        foreach (var line in lines)
        {
            var p = new VpnProfile();
            if (ParseLinkIntoProfile(p, line) && !string.IsNullOrEmpty(p.Address))
            {
                _profiles.Add(p);
                _standaloneProfiles.Add(p);
                _ = p.CheckPingAsync();
                lastAdded = p;
            }
        }

        if (lastAdded != null)
        {
            SaveSettings();
            UpdateEmptyState();
            if (LvProfiles != null) LvProfiles.SelectedItem = lastAdded;
            ShowNotification(L("NotifyImported"));
            CloseImportOverlay();
        }
        else
        {
            ShowNotification(L("NotifyImportError"), true);
        }
    }

    private void CmbProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProtocol?.SelectedItem is ComboBoxItem item && PanelManualFields != null && item.Content?.ToString() is { } contentStr)
            UpdateManualFields(contentStr, _editingProfile != null);
    }

    private void UpdateManualFields(string protocol, bool keepValues = false)
    {
        if (PanelManualFields == null) return;
        PanelManualFields.Children.Clear();

        if (!keepValues) _manualInputValues.Clear();

        AddManualField("Name", L("FieldName"));
        AddManualField("Address", L("FieldAddress"));
        AddManualField("Port", L("FieldPort"));

        if (protocol is "vless" or "trojan" or "vmess" or "tuic" or "hysteria" or "hysteria2")
        {
            AddManualField(protocol is "trojan" || protocol.StartsWith("hysteria") ? "Password" : "Uuid",
                           protocol is "trojan" || protocol.StartsWith("hysteria") ? L("FieldPassword") : L("FieldUuid"));

            if (protocol == "tuic") AddManualField("Password", L("FieldPassword"));

            AddManualField("Sni", L("FieldSni"));
            if (protocol is "vless" or "trojan" or "vmess") AddManualField("Fp", L("FieldFp"));
            AddManualField("Insecure", L("FieldInsecure"));
        }

        if (protocol == "vless") { AddManualField("Flow", L("FieldFlow")); AddManualField("Pbk", L("FieldPbk")); AddManualField("Sid", L("FieldSid")); AddManualField("Transport", L("FieldTransport")); AddManualField("Path", L("FieldPath")); AddManualField("Host", L("FieldHost")); }
        if (protocol == "trojan") { AddManualField("Transport", L("FieldTransport")); AddManualField("Path", L("FieldPath")); AddManualField("Host", L("FieldHost")); }
        if (protocol == "vmess") { AddManualField("Transport", L("FieldTransport")); AddManualField("Path", L("FieldPath")); AddManualField("Host", L("FieldHost")); AddManualField("Security", L("FieldSecurity")); AddManualField("AlterId", L("FieldAlterId")); }
        if (protocol == "shadowsocks") { AddManualField("Method", L("FieldMethod")); AddManualField("Password", L("FieldPassword")); }
        if (protocol == "wireguard") { AddManualField("PublicKey", L("FieldPublicKey")); AddManualField("PrivateKey", L("FieldPrivateKey")); AddManualField("LocalAddress", L("FieldLocalAddress")); AddManualField("Mtu", L("FieldMtu")); }
        if (protocol is "hysteria" or "hysteria2" or "tuic") { AddManualField("Alpn", L("FieldAlpn")); AddManualField("Up", L("FieldUp")); AddManualField("Down", L("FieldDown")); }
        if (protocol is "hysteria" or "hysteria2") { AddManualField("Obfs", L("FieldObfs")); AddManualField("ObfsPassword", L("FieldObfsPassword")); }
        if (protocol is "socks" or "http") { AddManualField("Uuid", L("FieldUuid")); AddManualField("Password", L("FieldPassword")); }
    }

    private void AddManualField(string key, string labelText)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");

        FrameworkElement inputElement;
        var dropdownOptions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Fp", new[] { "chrome", "firefox", "safari", "edge", "ios", "android", "360", "random", "randomized", "" } },
            { "Security", new[] { "auto", "none", "tls" } },
            { "Transport", new[] { "tcp", "ws", "grpc", "http", "httpupgrade" } },
            { "Flow", new[] { "", "xtls-rprx-vision" } }
        };

        if (key == "Insecure")
        {
            var checkBox = new CheckBox { Name = $"ChkField_{key}", Content = labelText };
            checkBox.SetResourceReference(Control.ForegroundProperty, "TextColor");

            if (_manualInputValues.TryGetValue(key, out var val)) checkBox.IsChecked = val == "true";
            else { checkBox.IsChecked = false; _manualInputValues[key] = "false"; }

            checkBox.Checked += (_, _) => _manualInputValues[key] = "true";
            checkBox.Unchecked += (_, _) => _manualInputValues[key] = "false";

            Grid.SetColumnSpan(checkBox, 2);
            grid.Children.Add(checkBox);
            PanelManualFields?.Children.Add(grid);
            return;
        }

        if (dropdownOptions.TryGetValue(key, out var options))
        {
            var comboBox = new ComboBox { Name = $"CmbField_{key}" };
            comboBox.SetResourceReference(Control.ForegroundProperty, "TextColor");
            comboBox.SetResourceReference(Control.BackgroundProperty, "BgColor");
            comboBox.SetResourceReference(Control.BorderBrushProperty, "OutlineColor");

            foreach (var opt in options) comboBox.Items.Add(opt);

            if (_manualInputValues.TryGetValue(key, out var existingValue))
            {
                if (options.Contains(existingValue, StringComparer.OrdinalIgnoreCase))
                    comboBox.SelectedItem = options.FirstOrDefault(o => o.Equals(existingValue, StringComparison.OrdinalIgnoreCase));
                else if (!string.IsNullOrWhiteSpace(existingValue))
                {
                    comboBox.Items.Add(existingValue);
                    comboBox.SelectedItem = existingValue;
                }
                else if (options.Length > 0)
                {
                    comboBox.SelectedIndex = 0;
                    _manualInputValues[key] = options[0];
                }
            }
            else if (options.Length > 0)
            {
                comboBox.SelectedIndex = 0;
                _manualInputValues[key] = options[0];
            }

            comboBox.SelectionChanged += (_, _) => { if (comboBox.SelectedItem is string strValue) _manualInputValues[key] = strValue; };
            inputElement = comboBox;
        }
        else
        {
            var textBox = new TextBox { Name = $"TxtField_{key}" };
            textBox.SetResourceReference(Control.ForegroundProperty, "TextColor");
            textBox.SetResourceReference(Control.BackgroundProperty, "BgColor");
            textBox.SetResourceReference(Control.BorderBrushProperty, "OutlineColor");

            if (_manualInputValues.TryGetValue(key, out var existingValue)) textBox.Text = existingValue;
            textBox.TextChanged += (_, _) => _manualInputValues[key] = textBox.Text;
            inputElement = textBox;
        }

        Grid.SetColumn(label, 0);
        Grid.SetColumn(inputElement, 1);
        grid.Children.Add(label);
        grid.Children.Add(inputElement);
        PanelManualFields?.Children.Add(grid);
    }

    private void BtnSaveImport_Click(object sender, RoutedEventArgs e)
    {
        var p = new VpnProfile
        {
            Protocol = (CmbProtocol?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "vless",
            Name = _manualInputValues.GetValueOrDefault("Name", "Manual Profile"),
            Address = _manualInputValues.GetValueOrDefault("Address", "").Trim()
        };

        string portStr = _manualInputValues.GetValueOrDefault("Port", "443").Trim();

        if (string.IsNullOrWhiteSpace(p.Address) || p.Address.Contains(' '))
        {
            ShowNotification(L("NotifyInvalidAddress"), true);
            return;
        }

        if (!int.TryParse(portStr, out int port) || port is < 1 or > 65535)
        {
            ShowNotification(L("NotifyInvalidPort"), true);
            return;
        }
        p.Port = port;

        p.Uuid = _manualInputValues.GetValueOrDefault("Uuid", "");
        p.Password = _manualInputValues.GetValueOrDefault("Password", "");
        p.Sni = _manualInputValues.GetValueOrDefault("Sni", "");
        p.Fp = p.Protocol is "hysteria" or "hysteria2" or "tuic" ? "" : _manualInputValues.GetValueOrDefault("Fp", "");
        p.Flow = _manualInputValues.GetValueOrDefault("Flow", "");
        p.Pbk = _manualInputValues.GetValueOrDefault("Pbk", "");
        p.Sid = _manualInputValues.GetValueOrDefault("Sid", "");
        p.Transport = _manualInputValues.GetValueOrDefault("Transport", "tcp");
        p.Path = _manualInputValues.GetValueOrDefault("Path", "");
        p.Host = _manualInputValues.GetValueOrDefault("Host", "");
        p.Security = _manualInputValues.GetValueOrDefault("Security", "");
        p.Insecure = _manualInputValues.GetValueOrDefault("Insecure", "false") == "true";

        if (int.TryParse(_manualInputValues.GetValueOrDefault("AlterId", "0"), out int aid)) p.AlterId = aid;

        p.Method = _manualInputValues.GetValueOrDefault("Method", "");
        p.PublicKey = _manualInputValues.GetValueOrDefault("PublicKey", "");
        p.PrivateKey = _manualInputValues.GetValueOrDefault("PrivateKey", "");
        p.LocalAddress = _manualInputValues.GetValueOrDefault("LocalAddress", "10.0.0.2/32");

        if (int.TryParse(_manualInputValues.GetValueOrDefault("Mtu", "1420"), out int mtu)) p.Mtu = mtu;

        p.Alpn = _manualInputValues.GetValueOrDefault("Alpn", "");
        p.Up = _manualInputValues.GetValueOrDefault("Up", "");
        p.Down = _manualInputValues.GetValueOrDefault("Down", "");
        p.Obfs = _manualInputValues.GetValueOrDefault("Obfs", "");
        p.ObfsPassword = _manualInputValues.GetValueOrDefault("ObfsPassword", "");
        p.Link = "";

        bool isNewProfile = _editingProfile == null;
        bool editedActive = _editingProfile != null && ActiveProfile == _editingProfile;

        if (isNewProfile)
        {
            _profiles.Add(p);
            _standaloneProfiles.Add(p);
            _isUpdatingSettings = true;
            if (LvProfiles != null) LvProfiles.SelectedItem = p;
            _isUpdatingSettings = false;
            ActiveProfile = p;
        }
        else if (_editingProfile != null)
        {
            int index = _profiles.IndexOf(_editingProfile);
            if (index >= 0)
            {
                p.SubscriptionId = _editingProfile.SubscriptionId;
                p.SubscriptionName = _editingProfile.SubscriptionName;
                _profiles[index] = p;

                int sIndex = _standaloneProfiles.IndexOf(_editingProfile);
                if (sIndex >= 0) _standaloneProfiles[sIndex] = p;

                if (editedActive || LvProfiles?.SelectedItem == _editingProfile)
                {
                    _isUpdatingSettings = true;
                    if (LvProfiles != null && sIndex >= 0) LvProfiles.SelectedItem = p;
                    _isUpdatingSettings = false;
                }
            }
            if (editedActive) ActiveProfile = p;
        }

        _editingProfile = null;
        RefreshAllVisualIndicators();
        UpdateEmptyState();
        PingManager.Clear();
        _ = p.CheckPingAsync();
        SaveSettings();
        CloseImportOverlay();

        if (editedActive && _isRunning)
        {
            ShowNotification(L("NotifyProfileUpdatedReconnecting"));
            _ = RestartVpnAsync();
        }
    }

    private static bool ParseLinkIntoProfile(VpnProfile p, string link)
    {
        link = link.Trim();
        p.Link = link;
        p.Fp = "";
        var allowed = new HashSet<string> { "vless", "vmess", "trojan", "shadowsocks", "ss", "wireguard", "wg", "hysteria", "hysteria2", "hy2", "tuic", "socks", "socks5", "http" };

        try
        {
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                p.Protocol = "vmess";
                string decodedJson = SafeBase64Decode(link[8..]);
                if (string.IsNullOrEmpty(decodedJson)) return false;

                using var doc = JsonDocument.Parse(decodedJson);
                var root = doc.RootElement;

                p.Name = root.TryGetProperty("ps", out var ps) ? ps.GetString() ?? "VMESS" : "VMESS";
                p.Address = root.TryGetProperty("add", out var add) ? add.GetString() ?? "" : "";
                p.Port = root.TryGetProperty("port", out var pt) ? (pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : int.Parse(pt.ToString()!)) : 443;
                p.Uuid = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                p.Transport = root.TryGetProperty("net", out var net) ? (net.GetString() ?? "tcp").ToLowerInvariant() : "tcp";
                if (p.Transport == "h2") p.Transport = "http";
                p.Sni = root.TryGetProperty("sni", out var sni) ? sni.GetString() ?? "" : "";
                p.Path = root.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "";
                p.Host = root.TryGetProperty("host", out var host) ? host.GetString() ?? "" : "";
                p.Fp = root.TryGetProperty("fp", out var fp) ? fp.GetString() ?? "" : "";
                p.Alpn = root.TryGetProperty("alpn", out var alpn) ? alpn.GetString() ?? "" : "";
                if (root.TryGetProperty("aid", out var aid))
                    p.AlterId = aid.ValueKind == JsonValueKind.Number ? aid.GetInt32() : (int.TryParse(aid.ToString(), out int aidVal) ? aidVal : 0);

                string vmessTls = root.TryGetProperty("tls", out var tls) ? tls.GetString() ?? "" : "";
                string vmessScy = root.TryGetProperty("scy", out var scy) ? scy.GetString() ?? "" : "";
                p.Security = vmessTls.Equals("tls", StringComparison.OrdinalIgnoreCase)
                    ? "tls"
                    : (string.IsNullOrEmpty(vmessScy) ? "auto" : vmessScy);
                if (p.Security == "tls" && string.IsNullOrEmpty(p.Sni) && !string.IsNullOrEmpty(p.Host))
                    p.Sni = p.Host;
                if (root.TryGetProperty("allowInsecure", out var vmessIns))
                {
                    string insStr = vmessIns.ToString();
                    p.Insecure = vmessIns.ValueKind == JsonValueKind.True || insStr == "1" || insStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                return true;
            }

            if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            {
                int hashIdx = link.IndexOf('#');
                string body = hashIdx >= 0 ? link[5..hashIdx] : link[5..];
                if (!body.Contains('@'))
                {
                    string frag = hashIdx >= 0 ? Uri.UnescapeDataString(link[(hashIdx + 1)..]) : "";
                    string decoded = SafeBase64Decode(body);
                    if (decoded.Contains('@'))
                    {
                        link = $"ss://{decoded}" + (string.IsNullOrEmpty(frag) ? "" : $"#{Uri.EscapeDataString(frag)}");
                    }
                }
            }

            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;

            p.Protocol = uri.Scheme.ToLowerInvariant();
            if (!allowed.Contains(p.Protocol)) return false;

            p.Protocol = p.Protocol switch
            {
                "hy2" => "hysteria2",
                "socks5" => "socks",
                "ss" => "shadowsocks",
                "wg" => "wireguard",
                _ => p.Protocol
            };

            p.Address = uri.IdnHost.Trim('[', ']');
            if (string.IsNullOrEmpty(p.Address)) p.Address = uri.Host.Trim('[', ']');
            p.Port = uri.Port > 0 ? uri.Port : (p.Protocol == "wireguard" ? 51820 : 443);
            p.Name = Uri.UnescapeDataString(uri.GetComponents(UriComponents.Fragment, UriFormat.Unescaped));
            if (string.IsNullOrEmpty(p.Name)) p.Name = p.Address;

            string userInfo = Uri.UnescapeDataString(uri.UserInfo);
            if (!string.IsNullOrEmpty(userInfo))
            {
                if (p.Protocol is "vless" or "tuic") p.Uuid = userInfo.Split(':')[0];
                if (p.Protocol is "trojan" or "hysteria2" or "hysteria") p.Password = userInfo;
                if (p.Protocol == "wireguard") p.PrivateKey = userInfo;
                if (p.Protocol == "shadowsocks")
                {
                    string decoded = userInfo;
                    if (!userInfo.Contains(':'))
                    {
                        try
                        {
                            string b64 = userInfo.Replace("-", "+").Replace("_", "/");
                            int mod4 = b64.Length % 4;
                            if (mod4 > 0) b64 += new string('=', 4 - mod4);
                            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        }
                        catch { }
                    }
                    var parts = decoded.Split(':', 2);
                    if (parts.Length == 2) { p.Method = parts[0]; p.Password = parts[1]; }
                }
                if (p.Protocol == "tuic" && userInfo.Contains(':')) p.Password = userInfo.Split(':', 2)[1];
                if (p.Protocol is "http" or "socks")
                {
                    var parts = userInfo.Split(':', 2);
                    p.Uuid = parts[0];
                    if (parts.Length > 1) p.Password = parts[1];
                }
            }

            var q = ParseQueryString(uri.Query);
            p.Sni = q.TryGetValue("sni", out var sniVal) ? sniVal : q.GetValueOrDefault("peer", "");
            p.Pbk = q.GetValueOrDefault("pbk", "");
            p.Sid = q.GetValueOrDefault("sid", "");
            p.Fp = p.Protocol is "hysteria" or "hysteria2" or "tuic" ? "" : q.GetValueOrDefault("fp", "");
            p.Flow = q.GetValueOrDefault("flow", "");
            if (p.Flow.EndsWith("-udp443", StringComparison.OrdinalIgnoreCase))
                p.Flow = p.Flow[..^7];

            p.Alpn = q.GetValueOrDefault("alpn", "");
            p.Method = q.GetValueOrDefault("method", p.Method);

            if (p.Protocol == "hysteria")
            {
                if (string.IsNullOrEmpty(p.Password) && q.TryGetValue("auth", out var authVal)) p.Password = authVal;
                if (q.TryGetValue("upmbps", out var upM)) p.Up = upM;
                else if (q.TryGetValue("up", out var up)) p.Up = up;
                if (q.TryGetValue("downmbps", out var dnM)) p.Down = dnM;
                else if (q.TryGetValue("down", out var dn)) p.Down = dn;
            }
            else if (p.Protocol == "hysteria2")
            {
                if (q.TryGetValue("up", out var up)) p.Up = up;
                else if (q.TryGetValue("upmbps", out var upM)) p.Up = upM;
                if (q.TryGetValue("down", out var dn)) p.Down = dn;
                else if (q.TryGetValue("downmbps", out var dnM)) p.Down = dnM;
                if (uri.Port <= 0 && q.TryGetValue("mport", out var mport))
                {
                    var mPortMatch = Regex.Match(mport, @"\d+");
                    if (mPortMatch.Success && int.TryParse(mPortMatch.Value, out int parsedPort) && parsedPort is >= 1 and <= 65535)
                        p.Port = parsedPort;
                }
            }
            else if (p.Protocol == "wireguard")
            {
                if (string.IsNullOrEmpty(p.PrivateKey) && q.TryGetValue("privatekey", out var privKey)) p.PrivateKey = privKey;
                if (q.TryGetValue("publickey", out var pubKey)) p.PublicKey = pubKey;
                else if (q.TryGetValue("public_key", out var pubKey2)) p.PublicKey = pubKey2;
                if (q.TryGetValue("address", out var localAddr)) p.LocalAddress = localAddr;
                else if (q.TryGetValue("local_address", out var localAddr2)) p.LocalAddress = localAddr2;
                if (q.TryGetValue("mtu", out var mtuStr) && int.TryParse(mtuStr, out int mtu)) p.Mtu = mtu;
            }

            if (q.TryGetValue("type", out var typeVal) || q.TryGetValue("net", out typeVal))
            {
                p.Transport = typeVal.ToLowerInvariant();
                if (p.Transport == "h2") p.Transport = "http";
                else if (p.Transport == "raw") p.Transport = "tcp";
            }

            if (p.Transport == "tcp" && q.TryGetValue("headerType", out var ht) && ht.Equals("http", StringComparison.OrdinalIgnoreCase))
                p.Transport = "http";

            if (q.TryGetValue("serviceName", out var snVal))
                p.Path = snVal;
            else if (q.TryGetValue("path", out var pathVal))
                p.Path = pathVal;

            if (q.TryGetValue("host", out var hostVal))
                p.Host = hostVal;

            if (q.TryGetValue("security", out var secVal))
                p.Security = secVal.ToLowerInvariant();

            if (q.TryGetValue("obfs", out var obfs)) p.Obfs = obfs;
            if (q.TryGetValue("obfs-password", out var opw)) p.ObfsPassword = opw;
            else if (q.TryGetValue("obfsParam", out var opar) && p.Protocol == "hysteria") p.Obfs = opar;

            if (q.TryGetValue("insecure", out var ins) || q.TryGetValue("allowInsecure", out ins) || q.TryGetValue("allow_insecure", out ins))
                p.Insecure = ins == "1" || ins.Equals("true", StringComparison.OrdinalIgnoreCase);

            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return dict;

        foreach (var part in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(new[] { '=' }, 2);
            if (kv.Length == 2) dict[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return dict;
    }

    private async void BtnRefreshPing_Click(object sender, RoutedEventArgs e)
    {
        if (BtnRefreshPing != null) BtnRefreshPing.IsEnabled = false;
        try
        {
            PingManager.Clear();
            CountryFlagHelper.ClearDownloadState();

            if (!_currentSettings.DisableAnimations && IconRefreshPing?.RenderTransform is RotateTransform transform)
                transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever });

            foreach (var p in _profiles) p.ResetGeoIpRetry();
            await Task.WhenAll(_profiles.Select(p => p.CheckPingAsync()));
        }
        catch { }
        finally
        {
            if (!_currentSettings.DisableAnimations && IconRefreshPing?.RenderTransform is RotateTransform resetTransform)
                resetTransform.BeginAnimation(RotateTransform.AngleProperty, null);

            if (BtnRefreshPing != null) BtnRefreshPing.IsEnabled = true;
        }
    }

    private VpnProfile? GetContextMenuProfile(object? sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: ListBoxItem { DataContext: VpnProfile p } } })
            return p;
        return LvProfiles?.SelectedItem as VpnProfile ?? ActiveProfile;
    }

    private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            if (!_isRunning) item.IsSelected = true;
            item.Focus();
        }
    }

    private async void ListBoxItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: true } && !_isRunning) await ToggleVpnAsync();
    }

    private async void MenuItem_Connect_Click(object sender, RoutedEventArgs e)
    {
        var p = GetContextMenuProfile(sender);
        if (p == null) return;

        if (LvProfiles != null && LvProfiles.SelectedItem != p)
        {
            LvProfiles.SelectedItem = p;
            return;
        }

        if (!_isRunning) await ToggleVpnAsync();
        else if (ActiveProfile != p)
        {
            ActiveProfile = p;
            RefreshAllVisualIndicators();
            SaveSettings();
            ShowNotification(L("NotifyReconnectingServer"));
            await RestartVpnAsync();
        }
    }

    private async void MenuItem_UrlTest_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuProfile(sender) is { } p)
        {
            p.PingText = "...";
            long time = await RunUrlTestAsync(p);
            p.SetUrlTestResult(time);
        }
    }

    private async Task<long> RunUrlTestAsync(VpnProfile p)
    {
        int port = 2081;
        bool useTempProxy = true;

        if (_isRunning && ActiveProfile == p && _currentSettings.Mode == "Proxy")
        {
            useTempProxy = false;
            port = _runningProxyPort > 0 ? _runningProxyPort : (_currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080);
        }

        Process? tempProcess = null;
        string tempConfig = "";
        long result = -1;

        try
        {
            if (useTempProxy)
            {
                port = FindFreeTcpPort(IPAddress.Loopback);
                if (port <= 0) port = Random.Shared.Next(10000, 60000);
                tempConfig = Path.Combine(_exeDirectory, $"temp_{port}.json");
                string configContent = GenerateJsonConfig(p, "8.8.8.8", "Proxy", port);
                await File.WriteAllTextAsync(tempConfig, configContent);

                tempProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(_exeDirectory, "sing-box.exe"),
                        Arguments = $"run -c \"{tempConfig}\"",
                        WorkingDirectory = _exeDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                tempProcess.Start();
                JobObjectManager.AddProcess(tempProcess.Handle);

                bool portReady = false;
                for (int i = 0; i < 20; i++)
                {
                    if (tempProcess.HasExited) return -1;
                    try
                    {
                        using var probe = new TcpClient();
                        await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(200));
                        portReady = true;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(150);
                    }
                }

                if (!portReady && tempProcess.HasExited) return -1;
            }

            using var handler = new HttpClientHandler { Proxy = new WebProxy("127.0.0.1", port), UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("http://cp.cloudflare.com/generate_204");
            sw.Stop();
            if (response.IsSuccessStatusCode) result = sw.ElapsedMilliseconds;
        }
        catch { }
        finally
        {
            if (useTempProxy && tempProcess != null)
            {
                try { tempProcess.Kill(true); tempProcess.WaitForExit(1000); } catch { }
                tempProcess.Dispose();
            }
            if (!string.IsNullOrEmpty(tempConfig))
            {
                try { File.Delete(tempConfig); } catch { }
            }
        }

        return result;
    }

    private static string BuildShareLink(VpnProfile p)
    {
        if (!string.IsNullOrEmpty(p.Link)) return p.Link;

        string frag = string.IsNullOrEmpty(p.Name) ? "" : $"#{Uri.EscapeDataString(p.Name)}";
        if (p.Protocol == "shadowsocks")
        {
            string creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{p.Method}:{p.Password}"));
            return $"ss://{creds}@{p.Address}:{p.Port}{frag}";
        }

        var qp = new List<string>();
        void AddQ(string k, string? v)
        {
            if (!string.IsNullOrEmpty(v)) qp.Add($"{k}={Uri.EscapeDataString(v)}");
        }

        if (p.Protocol == "vless")
        {
            AddQ("security", !string.IsNullOrEmpty(p.Pbk) ? "reality" : (string.IsNullOrEmpty(p.Security) ? "none" : p.Security));
            AddQ("type", string.IsNullOrEmpty(p.Transport) ? "tcp" : p.Transport);
            AddQ("sni", p.Sni);
            AddQ("fp", p.Fp);
            AddQ("pbk", p.Pbk);
            AddQ("sid", p.Sid);
            AddQ("flow", p.Flow);
            AddQ("alpn", p.Alpn);
            AddQ("path", p.Path);
            AddQ("host", p.Host);
            if (p.Insecure) AddQ("allowInsecure", "1");
            string qs = qp.Count > 0 ? "?" + string.Join("&", qp) : "";
            return $"vless://{Uri.EscapeDataString(p.Uuid)}@{p.Address}:{p.Port}{qs}{frag}";
        }

        if (p.Protocol == "trojan")
        {
            AddQ("security", string.IsNullOrEmpty(p.Security) ? "tls" : p.Security);
            AddQ("type", string.IsNullOrEmpty(p.Transport) ? "tcp" : p.Transport);
            AddQ("sni", p.Sni);
            AddQ("fp", p.Fp);
            AddQ("alpn", p.Alpn);
            AddQ("path", p.Path);
            AddQ("host", p.Host);
            if (p.Insecure) AddQ("allowInsecure", "1");
            string qs = qp.Count > 0 ? "?" + string.Join("&", qp) : "";
            return $"trojan://{Uri.EscapeDataString(p.Password)}@{p.Address}:{p.Port}{qs}{frag}";
        }

        if (p.Protocol is "hysteria2" or "hysteria")
        {
            AddQ("sni", p.Sni);
            AddQ("alpn", p.Alpn);
            AddQ("obfs", p.Obfs);
            AddQ("obfs-password", p.ObfsPassword);
            AddQ("up", p.Up);
            AddQ("down", p.Down);
            if (p.Insecure) AddQ("insecure", "1");
            string qs = qp.Count > 0 ? "?" + string.Join("&", qp) : "";
            string scheme = p.Protocol == "hysteria2" ? "hysteria2" : "hysteria";
            return $"{scheme}://{Uri.EscapeDataString(p.Password)}@{p.Address}:{p.Port}{qs}{frag}";
        }

        if (p.Protocol == "tuic")
        {
            AddQ("sni", p.Sni);
            AddQ("alpn", p.Alpn);
            AddQ("congestion_control", "bbr");
            if (p.Insecure) AddQ("allow_insecure", "1");
            string qs = qp.Count > 0 ? "?" + string.Join("&", qp) : "";
            string user = string.IsNullOrEmpty(p.Password) ? Uri.EscapeDataString(p.Uuid) : $"{Uri.EscapeDataString(p.Uuid)}:{Uri.EscapeDataString(p.Password)}";
            return $"tuic://{user}@{p.Address}:{p.Port}{qs}{frag}";
        }

        if (p.Protocol == "wireguard")
        {
            AddQ("publickey", p.PublicKey);
            AddQ("address", p.LocalAddress);
            if (p.Mtu > 0) AddQ("mtu", p.Mtu.ToString());
            string qs = qp.Count > 0 ? "?" + string.Join("&", qp) : "";
            return $"wireguard://{Uri.EscapeDataString(p.PrivateKey)}@{p.Address}:{p.Port}{qs}{frag}";
        }

        return $"{p.Protocol}://{(string.IsNullOrEmpty(p.Uuid) ? p.Password : p.Uuid)}@{p.Address}:{p.Port}{frag}";
    }

    private void MenuItem_CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuProfile(sender) is { } p)
        {
            ShowQROverlay(BuildShareLink(p));
        }
    }

    private void MenuItem_MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuProfile(sender) is { } p)
        {
            int idx = _standaloneProfiles.IndexOf(p);
            if (idx > 0)
            {
                _standaloneProfiles.Move(idx, idx - 1);
                int allIdx = _profiles.IndexOf(p);
                if (allIdx > 0) _profiles.Move(allIdx, allIdx - 1);
                SaveSettings();
            }
        }
    }

    private void MenuItem_MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuProfile(sender) is { } p)
        {
            int idx = _standaloneProfiles.IndexOf(p);
            if (idx >= 0 && idx < _standaloneProfiles.Count - 1)
            {
                _standaloneProfiles.Move(idx, idx + 1);
                int allIdx = _profiles.IndexOf(p);
                if (allIdx >= 0 && allIdx < _profiles.Count - 1) _profiles.Move(allIdx, allIdx + 1);
                SaveSettings();
            }
        }
    }

    private void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuProfile(sender) is { } p)
        {
            _editingProfile = p;
            _manualInputValues["Name"] = p.Name;
            _manualInputValues["Address"] = p.Address;
            _manualInputValues["Port"] = p.Port.ToString();
            _manualInputValues["Uuid"] = p.Uuid;
            _manualInputValues["Password"] = p.Password;
            _manualInputValues["Sni"] = p.Sni;
            _manualInputValues["Fp"] = p.Fp;
            _manualInputValues["Flow"] = p.Flow;
            _manualInputValues["Pbk"] = p.Pbk;
            _manualInputValues["Sid"] = p.Sid;
            _manualInputValues["Transport"] = p.Transport;
            _manualInputValues["Path"] = p.Path;
            _manualInputValues["Host"] = p.Host;
            _manualInputValues["Security"] = p.Security;
            _manualInputValues["AlterId"] = p.AlterId.ToString();
            _manualInputValues["Method"] = p.Method;
            _manualInputValues["PublicKey"] = p.PublicKey;
            _manualInputValues["PrivateKey"] = p.PrivateKey;
            _manualInputValues["LocalAddress"] = p.LocalAddress;
            _manualInputValues["Mtu"] = p.Mtu.ToString();
            _manualInputValues["Alpn"] = p.Alpn;
            _manualInputValues["Up"] = p.Up;
            _manualInputValues["Down"] = p.Down;
            _manualInputValues["Obfs"] = p.Obfs;
            _manualInputValues["ObfsPassword"] = p.ObfsPassword;
            _manualInputValues["Insecure"] = p.Insecure ? "true" : "false";

            if (TxtImportTitle != null) TxtImportTitle.Text = L("EditTitle");

            if (BtnPasteImport != null) BtnPasteImport.Visibility = Visibility.Collapsed;
            if (ImportSeparator != null) ImportSeparator.Visibility = Visibility.Collapsed;
            if (TxtOrManual != null) TxtOrManual.Visibility = Visibility.Collapsed;
            if (OverlayImport != null) OverlayImport.Visibility = Visibility.Visible;

            if (CmbProtocol != null)
            {
                var item = CmbProtocol.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == p.Protocol);
                CmbProtocol.SelectedItem = item ?? CmbProtocol.Items[0];
            }

            UpdateManualFields(p.Protocol, true);

            OverlayImportBg?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
            OverlayImportCard?.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = GetDuration(0.2) });
            OverlayImportTransform?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = GetDuration(0.2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private async void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuProfile(sender) is { } p)
        {
            bool wasActive = ActiveProfile == p;
            _profiles.Remove(p);
            _standaloneProfiles.Remove(p);
            foreach (var sub in _subscriptions) sub.Profiles.Remove(p);

            if (wasActive)
            {
                ActiveProfile = _standaloneProfiles.FirstOrDefault() ?? _profiles.FirstOrDefault();
                RefreshAllVisualIndicators();
                if (_isRunning) await StopVPNAsync();
            }
            SaveSettings();
            UpdateEmptyState();
        }
    }

    private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (TxtConsole == null) return;
        var text = new TextRange(TxtConsole.Document.ContentStart, TxtConsole.Document.ContentEnd).Text;

        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            ShowNotification(L("NotifyCopied"));
        }
    }

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
    {
        ConsoleParagraph?.Inlines.Clear();
        lock (_logLock)
        {
            _logBuffer.Clear();
        }
    }

    private void Log(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            _lastCoreError = Regex.Replace(message, @"\x1B\[[0-9;]*m", "").Trim();
        }
        lock (_logLock)
        {
            _logBuffer.Enqueue(message);
            if (_logBuffer.Count > 1000) _logBuffer.Dequeue();
        }
    }

    private void FlushLogs(object? sender, EventArgs e)
    {
        List<string> messagesToPrint;
        lock (_logLock)
        {
            if (_logBuffer.Count == 0) return;
            messagesToPrint = _logBuffer.ToList();
            _logBuffer.Clear();
        }

        if (ConsoleParagraph == null || TxtConsole == null) return;

        bool needsScroll = false;
        TxtConsole.BeginChange();
        try
        {
            foreach (var message in messagesToPrint)
            {
                var parts = Regex.Split(message, @"\x1B\[([0-9;]*)m");
                bool isDefaultColor = true;
                Brush? currentCustomBrush = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    if (i % 2 == 0)
                    {
                        if (!string.IsNullOrEmpty(parts[i]))
                        {
                            var run = new Run(parts[i]);
                            if (message.Contains("[ERRO]", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
                            {
                                run.SetResourceReference(TextElement.ForegroundProperty, "ErrorColor");
                            }
                            else if (isDefaultColor)
                            {
                                run.SetResourceReference(TextElement.ForegroundProperty, "TextColor");
                            }
                            else
                            {
                                run.Foreground = currentCustomBrush;
                            }
                            ConsoleParagraph.Inlines.Add(run);
                        }
                    }
                    else
                    {
                        string code = parts[i];
                        if (code is "0" or "") isDefaultColor = true;
                        else
                        {
                            isDefaultColor = false;
                            currentCustomBrush = code switch
                            {
                                _ when code.Contains("31") || code.Contains("91") => new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                                _ when code.Contains("32") || code.Contains("92") => new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                                _ when code.Contains("33") || code.Contains("93") => new SolidColorBrush(Color.FromRgb(250, 204, 21)),
                                _ when code.Contains("36") || code.Contains("96") => new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                                _ when code.Contains("34") || code.Contains("94") => new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                                _ when code.Contains("35") || code.Contains("95") => new SolidColorBrush(Color.FromRgb(232, 121, 249)),
                                _ when code.Contains("90") => new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                                _ => null
                            };
                            if (currentCustomBrush == null) isDefaultColor = true;
                        }
                    }
                }
                ConsoleParagraph.Inlines.Add(new Run("\n"));
                needsScroll = true;
            }

            if (ConsoleParagraph.Inlines.Count > 1500)
            {
                while (ConsoleParagraph.Inlines.Count > 500)
                    ConsoleParagraph.Inlines.Remove(ConsoleParagraph.Inlines.FirstInline);
            }
        }
        finally { TxtConsole.EndChange(); }

        if (needsScroll && ViewLogs?.Visibility == Visibility.Visible && ChkAutoScroll?.IsChecked == true)
            TxtConsole.ScrollToEnd();
    }

    private static async Task<string> GetCurrentIpAsync(HttpClientHandler? handler, CancellationToken ct = default)
    {
        try
        {
            using var client = handler != null
                ? new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(2000) }
                : new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(1500) };

            try
            {
                string ip = (await client.GetStringAsync("https://api.ipify.org", ct)).Trim();
                if (IPAddress.TryParse(ip, out _)) return ip;
            }
            catch { }

            string response = await client.GetStringAsync("https://ipinfo.io/json", ct);
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.TryGetProperty("ip", out var q) ? q.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private void UpdateTraffic()
    {
        if (!_isRunning) return;
        try
        {
            if (NetworkInterface.GetIsNetworkAvailable())
            {
                var upInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                NetworkInterface[] targetInterfaces;
                if (_currentSettings.Mode != "Proxy")
                {
                    var tunIfaces = upInterfaces
                        .Where(n => n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                                    n.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                                    n.Name.Contains("sing-box", StringComparison.OrdinalIgnoreCase) ||
                                    n.Name.Contains("NeonV", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    targetInterfaces = tunIfaces.Length > 0
                        ? tunIfaces
                        : upInterfaces.Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToArray();
                }
                else
                {
                    targetInterfaces = upInterfaces
                        .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                    !n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }

                long bytesR = 0, bytesS = 0;
                foreach (var ni in targetInterfaces)
                {
                    var stats = ni.GetIPv4Statistics();
                    bytesR += stats.BytesReceived;
                    bytesS += stats.BytesSent;
                }

                if (_lastBytesReceived > 0 && _lastBytesSent > 0)
                {
                    long dR = Math.Max(0, bytesR - _lastBytesReceived);
                    long dS = Math.Max(0, bytesS - _lastBytesSent);

                    if (TxtSpeedDown != null) TxtSpeedDown.Text = $"↓ {FormatSpeed(dR)}";
                    if (TxtSpeedUp != null) TxtSpeedUp.Text = $"↑ {FormatSpeed(dS)}";
                }
                _lastBytesReceived = bytesR;
                _lastBytesSent = bytesS;
            }
        }
        catch { }
    }

    private static string FormatSpeed(long bytes) => bytes switch
    {
        > 1048576 => $"{bytes / 1048576.0:F1} MB/s",
        > 1024 => $"{bytes / 1024.0:F1} KB/s",
        _ => $"{bytes} B/s"
    };

    private void AnimateToggleButtonPulse()
    {
        if (_currentSettings.DisableAnimations || BtnToggle?.RenderTransform is not ScaleTransform scale) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation { From = 0.97, To = 1.0, Duration = GetDuration(0.22), EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation { From = 0.97, To = 1.0, Duration = GetDuration(0.22), EasingFunction = ease });
    }

    private async void BtnToggle_Click(object sender, RoutedEventArgs e) => await ToggleVpnAsync();

    private async Task ToggleVpnAsync()
    {
        await _vpnStateSemaphore.WaitAsync();
        try
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = false;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = false;

            if (_isRunning) await StopVpnCoreAsync();
            else await StartVpnCoreAsync();
        }
        finally
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = true;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
            _vpnStateSemaphore.Release();
        }
    }

    private async Task StartVPNAsync()
    {
        await _vpnStateSemaphore.WaitAsync();
        try
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = false;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = false;
            await StartVpnCoreAsync();
        }
        finally
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = true;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
            _vpnStateSemaphore.Release();
        }
    }

    private async Task StopVPNAsync()
    {
        await _vpnStateSemaphore.WaitAsync();
        try
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = false;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = false;
            await StopVpnCoreAsync();
        }
        finally
        {
            if (BtnToggle != null) BtnToggle.IsEnabled = true;
            if (_trayToggleItem != null) _trayToggleItem.Enabled = true;
            _vpnStateSemaphore.Release();
        }
    }

    private async Task StartVpnCoreAsync()
    {
        var targetProfile = ActiveProfile ?? _standaloneProfiles.FirstOrDefault() ?? _profiles.FirstOrDefault();

        if (targetProfile == null)
        {
            ShowNotification(L("NotifySelectProfile"), true);
            return;
        }

        ActiveProfile = targetProfile;
        RefreshAllVisualIndicators();

        if (_currentSettings.Mode != "Proxy" && !IsAdministrator())
        {
            ShowNotification(L("NotifyAdminRequired"), true);
            return;
        }

        if (_currentSettings.Mode != "Proxy" && !File.Exists(Path.Combine(_exeDirectory, "wintun.dll")))
        {
            ShowNotification(L("NotifyWintunMissing"), true);
            return;
        }

        KillOrphanedSingboxProcesses();

        _lastCoreError = "";
        _lastLocationCountryCode = "";
        _lastLocationApiName = null;
        ConsoleParagraph?.Inlines.Clear();
        Log($"[NeonV] Connecting to: {targetProfile.Name} ({_currentSettings.Mode} Mode)...");
        _originalIp = await GetCurrentIpAsync(null);

        try
        {
            _runningDns = string.IsNullOrWhiteSpace(_currentSettings.RemoteDns) ? "8.8.8.8" : _currentSettings.RemoteDns.Trim();
            _runningDirectDns = string.IsNullOrWhiteSpace(_currentSettings.DirectDns) ? "77.88.8.8" : _currentSettings.DirectDns.Trim();
            _runningConfiguredProxyPort = _currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080;
            int effectiveProxyPort = _runningConfiguredProxyPort;

            if (_currentSettings.Mode == "Proxy" && (!_currentSettings.UseCustomConfig || !File.Exists(Path.Combine(_exeDirectory, "config.json"))))
            {
                IPAddress listenIp = _currentSettings.AllowLan ? IPAddress.Any : IPAddress.Loopback;
                bool portFree = false;
                for (int i = 0; i < 20; i++)
                {
                    if (CanBindTcpPort(listenIp, effectiveProxyPort))
                    {
                        portFree = true;
                        break;
                    }
                    await Task.Delay(100);
                }

                if (!portFree)
                {
                    int fallbackPort = FindFreeTcpPort(listenIp);
                    if (fallbackPort > 0)
                    {
                        Log($"[NeonV] Port {effectiveProxyPort} is already in use, using port {fallbackPort}...");
                        ShowNotification(string.Format(L("NotifyPortBusyFallbackFmt"), effectiveProxyPort, fallbackPort));
                        effectiveProxyPort = fallbackPort;
                    }
                }
            }

            _runningProxyPort = effectiveProxyPort;

            if (_currentSettings.Mode != "Proxy")
                _currentTunInterfaceName = SelectAvailableTunInterfaceName();

            Process? activeProc = null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                _lastCoreError = "";

                if (!_currentSettings.UseCustomConfig || !File.Exists(Path.Combine(_exeDirectory, "config.json")))
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(_exeDirectory, "config.json"),
                        GenerateJsonConfig(targetProfile, _runningDns, _currentSettings.Mode, effectiveProxyPort));
                }

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(_exeDirectory, "sing-box.exe"),
                        Arguments = $"run -c \"{Path.Combine(_exeDirectory, "config.json")}\"",
                        WorkingDirectory = _exeDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                _singboxProcess = proc;

                proc.OutputDataReceived += (_, ev) => Log(ev.Data);
                proc.ErrorDataReceived += (_, ev) => Log(ev.Data);

                proc.Start();
                JobObjectManager.AddProcess(proc.Handle);

                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await Task.Delay(1500);
                if (!proc.HasExited)
                {
                    activeProc = proc;
                    break;
                }

                if (attempt == 0 &&
                    _currentSettings.Mode != "Proxy" &&
                    !_currentSettings.UseCustomConfig &&
                    IsTunConflictError(_lastCoreError))
                {
                    _blockedTunNames.Add(_currentTunInterfaceName);
                    proc.Dispose();
                    _singboxProcess = null;
                    KillOrphanedSingboxProcesses();

                    var existing = GetExistingAdapterNames();
                    do
                    {
                        _currentTunInterfaceName = $"NeonV-{Guid.NewGuid().ToString("N")[..4]}";
                    }
                    while (existing.Contains(_currentTunInterfaceName) || _blockedTunNames.Contains(_currentTunInterfaceName));

                    Log($"[NeonV] TUN adapter conflict detected, retrying with interface '{_currentTunInterfaceName}'...");
                    continue;
                }

                string detail = string.IsNullOrEmpty(_lastCoreError)
                    ? L("NotifyCoreExited")
                    : $"{L("NotifyCoreExited")}\n{_lastCoreError}";
                throw new Exception(detail);
            }

            if (activeProc == null)
                throw new Exception(L("NotifyCoreExited"));

            activeProc.EnableRaisingEvents = true;
            activeProc.Exited += (_, _) =>
            {
                if (_singboxProcess == activeProc && _isRunning)
                {
                    Dispatcher.InvokeAsync(async () =>
                    {
                        if (_singboxProcess == activeProc && _isRunning)
                        {
                            ShowNotification(L("NotifyCoreExited"), true);
                            await StopVPNAsync();
                        }
                    });
                }
            };

            _isRunning = true;
            IsVpnRunning = true;
            _lastBytesReceived = 0;
            _lastBytesSent = 0;

            if (_currentSettings.Mode == "Proxy")
                SystemProxyManager.EnableProxy($"127.0.0.1:{_runningProxyPort}");

            UpdateButtonText();
            AnimateToggleButtonPulse();
            UpdateTrayIconImage(true);
            Log("[NeonV] Running successfully!");

            _connectionStartTime = DateTime.Now;
            if (TxtConnectionTime != null) TxtConnectionTime.Text = "...";
            if (TxtSpeedDown != null) TxtSpeedDown.Text = "↓ 0 B/s";
            if (TxtSpeedUp != null) TxtSpeedUp.Text = "↑ 0 B/s";
            if (TxtLocation != null) TxtLocation.Text = "...";
            if (ImgFlag != null) { ImgFlag.Source = null; ImgFlag.Visibility = Visibility.Collapsed; }
            if (IconLocation != null) IconLocation.Visibility = Visibility.Visible;

            _uptimeTimer?.Start();
            AnimateLocationBadge(true);
            _ = FetchLocationAsync();
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
            ShowNotification(ex.Message, true);
            await StopVpnCoreAsync();
        }
    }

    private async Task StopVpnCoreAsync()
    {
        _locationCts?.Cancel();
        AnimateLocationBadge(false);
        Log("[NeonV] Stopping...");
        SystemProxyManager.DisableProxy();

        var procToStop = _singboxProcess;
        _singboxProcess = null;

        await Task.Run(() =>
        {
            try
            {
                if (procToStop is { HasExited: false })
                {
                    procToStop.Kill(true);
                    procToStop.WaitForExit(2000);
                }
            }
            catch { }
            KillOrphanedSingboxProcesses();
        });

        procToStop?.Dispose();
        _isRunning = false;
        IsVpnRunning = false;
        _uptimeTimer?.Stop();

        UpdateButtonText();
        AnimateToggleButtonPulse();
        UpdateTrayIconImage(false);

        PingManager.Clear();
        _ = Task.WhenAll(_profiles.Select(p => p.CheckPingAsync()));
    }

    private void AnimateLocationBadge(bool show)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (LocationBadge != null)
            {
                var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                LocationBadge.BeginAnimation(MaxWidthProperty, new DoubleAnimation { To = show ? 360 : 0, Duration = GetDuration(0.35), EasingFunction = cubicEase });
                LocationBadge.BeginAnimation(OpacityProperty, new DoubleAnimation { To = show ? 1 : 0, Duration = GetDuration(0.35), EasingFunction = cubicEase });
                LocationBadge.BeginAnimation(MarginProperty, new ThicknessAnimation { To = show ? new Thickness(0, 0, 16, 0) : new Thickness(0), Duration = GetDuration(0.35), EasingFunction = cubicEase });
            }
        });
    }

    private static async Task<(string ip, string countryCode, string apiCountry)> QueryExitGeoAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            string resp = await client.GetStringAsync($"https://ipinfo.io/json?_t={DateTime.UtcNow.Ticks}", ct);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            string ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
            string cc = root.TryGetProperty("country", out var ccEl) ? CountryFlagHelper.NormalizeCountryCode(ccEl.GetString() ?? "") : "";
            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(cc))
                return (ip, cc, "");
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        try
        {
            string langParam = CurrentLanguage switch
            {
                "ru" or "en" or "de" or "ja" or "zh-CN" => CurrentLanguage,
                _ => "en"
            };
            string resp = await client.GetStringAsync($"https://ipwho.is/?lang={langParam}&_t={DateTime.UtcNow.Ticks}", ct);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.GetBoolean())
            {
                string ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
                string cc = root.TryGetProperty("country_code", out var ccEl) ? CountryFlagHelper.NormalizeCountryCode(ccEl.GetString() ?? "") : "";
                string country = root.TryGetProperty("country", out var cEl) ? cEl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(cc))
                    return (ip, cc, country);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        try
        {
            string langParam = CurrentLanguage switch
            {
                "ru" or "en" or "de" or "ja" or "zh-CN" => CurrentLanguage,
                _ => "en"
            };
            string resp = await client.GetStringAsync($"http://ip-api.com/json/?lang={langParam}&_t={DateTime.UtcNow.Ticks}", ct);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            string ip = root.TryGetProperty("query", out var qEl) ? qEl.GetString() ?? "" : "";
            string cc = root.TryGetProperty("countryCode", out var ccEl) ? CountryFlagHelper.NormalizeCountryCode(ccEl.GetString() ?? "") : "";
            string country = root.TryGetProperty("country", out var cEl) ? cEl.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(cc))
                return (ip, cc, country);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return ("", "", "");
    }

    private async Task FetchLocationAsync(bool immediate = false)
    {
        _locationCts?.Cancel();
        _locationCts = new CancellationTokenSource();
        var ct = _locationCts.Token;

        int activeProxyPort = _runningProxyPort > 0 ? _runningProxyPort : (_currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080);

        await Task.Run(async () =>
        {
            try
            {
                if (!immediate) await Task.Delay(1200, ct);

                for (int i = 0; i < 5; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        Dispatcher.Invoke(() => { if (TxtLocation != null) TxtLocation.Text = "..."; });

                        HttpClientHandler handler = new();
                        if (_currentSettings.Mode == "Proxy")
                        {
                            handler.Proxy = new WebProxy("127.0.0.1", activeProxyPort);
                            handler.UseProxy = true;
                        }
                        else
                        {
                            handler.UseProxy = false;
                        }

                        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

                        var (newIp, countryCode, apiCountry) = await QueryExitGeoAsync(client, ct);
                        if (string.IsNullOrEmpty(countryCode))
                        {
                            if (i == 4)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (!ct.IsCancellationRequested && TxtLocation != null)
                                        TxtLocation.Text = L("UnknownLocation");
                                });
                            }
                            else
                            {
                                await Task.Delay(2000, ct);
                            }
                            continue;
                        }

                        if (!immediate && newIp == _originalIp && !string.IsNullOrEmpty(_originalIp) && i < 4)
                        {
                            await Task.Delay(2500, ct);
                            continue;
                        }

                        if (ct.IsCancellationRequested || !_isRunning) return;

                        _lastLocationCountryCode = countryCode;
                        _lastLocationApiName = apiCountry;
                        string localizedCountry = LocalizeCountryName(countryCode, apiCountry);

                        byte[]? flagBytes = null;
                        try
                        {
                            flagBytes = await client.GetByteArrayAsync($"https://flagcdn.com/w40/{countryCode.ToLowerInvariant()}.png", ct);
                        }
                        catch { }

                        Dispatcher.Invoke(() =>
                        {
                            if (ct.IsCancellationRequested || !_isRunning) return;

                            if (TxtLocation != null)
                            {
                                TxtLocation.Text = localizedCountry;
                                TxtLocation.BeginAnimation(OpacityProperty, new DoubleAnimation { From = 0.2, To = 1, Duration = GetDuration(0.25) });
                            }
                            if (flagBytes != null && flagBytes.Length > 0)
                            {
                                try
                                {
                                    using var ms = new MemoryStream(flagBytes);
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.StreamSource = ms;
                                    bmp.EndInit();
                                    bmp.Freeze();

                                    if (ImgFlag != null)
                                    {
                                        ImgFlag.Source = bmp;
                                        ImgFlag.Visibility = Visibility.Visible;
                                        ImgFlag.BeginAnimation(OpacityProperty, new DoubleAnimation { From = 0, To = 1, Duration = GetDuration(0.25) });
                                    }
                                    if (IconLocation != null) IconLocation.Visibility = Visibility.Collapsed;
                                }
                                catch { }
                            }
                        });
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        if (i == 4)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (!ct.IsCancellationRequested && TxtLocation != null)
                                    TxtLocation.Text = L("UnknownLocation");
                            });
                        }
                        else await Task.Delay(2000, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _fullScreenOverlay?.Close();

        if (!_isRealClose)
        {
            if (!_currentSettings.MinimizeToTray)
            {
                _isRealClose = true;
            }
            else
            {
                e.Cancel = true;
                Hide();
                if (!_currentSettings.HasShownTrayNotification)
                {
                    _notifyIcon?.ShowBalloonTip(3000, "NeonV", L("TrayBgTip"), Forms.ToolTipIcon.Info);
                    _currentSettings.HasShownTrayNotification = true;
                    SaveSettings();
                }
                return;
            }
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _locationCts?.Cancel();
        _notificationCts?.Cancel();
        _uptimeTimer?.Stop();
        _pingTimer?.Stop();
        _logTimer?.Stop();
        FlushSaveSettingsSync();

        SystemProxyManager.DisableProxy();
        try
        {
            if (_singboxProcess is { HasExited: false })
            {
                _singboxProcess.Kill(true);
                _singboxProcess.WaitForExit(2000);
            }
        }
        catch { }
        KillOrphanedSingboxProcesses();
        Direct2DEmojiRenderer.Dispose();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }

    private static void ApplyTransport(Dictionary<string, object> outbound, VpnProfile p)
    {
        string type = (p.Transport ?? "").Trim().ToLowerInvariant();
        if (type == "h2") type = "http";
        if (string.IsNullOrEmpty(type) || type is "tcp" or "raw" or "none") return;

        var transport = new Dictionary<string, object> { ["type"] = type };
        if (type == "ws")
        {
            transport["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path;
            if (!string.IsNullOrEmpty(p.Host)) transport["headers"] = new Dictionary<string, string> { ["Host"] = p.Host };
        }
        else if (type == "grpc")
        {
            if (!string.IsNullOrEmpty(p.Path)) transport["service_name"] = p.Path;
        }
        else if (type == "http")
        {
            transport["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path;
            if (!string.IsNullOrEmpty(p.Host)) transport["host"] = new[] { p.Host };
        }
        else if (type == "httpupgrade")
        {
            transport["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path;
            if (!string.IsNullOrEmpty(p.Host)) transport["host"] = p.Host;
        }
        outbound["transport"] = transport;
    }

    private static Dictionary<string, object> BuildDnsServer(string tag, string input, string? detour)
    {
        var server = new Dictionary<string, object> { ["tag"] = tag };
        string fallbackIp = tag == "direct-dns" ? "77.88.8.8" : "8.8.8.8";
        string trimmed = string.IsNullOrWhiteSpace(input) ? fallbackIp : input.Trim();

        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(trimmed, UriKind.Absolute, out var httpsUri))
        {
            if (tag == "direct-dns" && !IPAddress.TryParse(httpsUri.Host, out _))
            {
                server["type"] = "local";
            }
            else
            {
                server["type"] = "https";
                server["server"] = httpsUri.Host;
                if (httpsUri.Port > 0) server["server_port"] = httpsUri.Port;
                if (!string.IsNullOrEmpty(httpsUri.AbsolutePath) && httpsUri.AbsolutePath != "/")
                    server["path"] = httpsUri.AbsolutePath;
                if (!IPAddress.TryParse(httpsUri.Host, out _) && tag != "direct-dns")
                    server["domain_resolver"] = "direct-dns";
            }
        }
        else if (trimmed.StartsWith("tls://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(trimmed, UriKind.Absolute, out var tlsUri))
        {
            if (tag == "direct-dns" && !IPAddress.TryParse(tlsUri.Host, out _))
            {
                server["type"] = "local";
            }
            else
            {
                server["type"] = "tls";
                server["server"] = tlsUri.Host;
                if (tlsUri.Port > 0) server["server_port"] = tlsUri.Port;
                if (!IPAddress.TryParse(tlsUri.Host, out _) && tag != "direct-dns")
                    server["domain_resolver"] = "direct-dns";
            }
        }
        else if (IPAddress.TryParse(trimmed, out _))
        {
            server["type"] = "udp";
            server["server"] = trimmed;
            server["server_port"] = 53;
        }
        else
        {
            server["type"] = "udp";
            server["server"] = fallbackIp;
            server["server_port"] = 53;
        }

        if (!string.IsNullOrEmpty(detour))
            server["detour"] = detour;

        return server;
    }

    private static bool IsIpOrCidr(string value)
    {
        if (IPAddress.TryParse(value, out _)) return true;
        int slash = value.IndexOf('/');
        if (slash > 0 && IPAddress.TryParse(value[..slash], out _) && int.TryParse(value[(slash + 1)..], out int prefix) && prefix is >= 0 and <= 128)
            return true;
        return false;
    }

    private string GenerateJsonConfig(VpnProfile p, string customDns, string? overrideMode = null, int? overrideProxyPort = null)
    {
        if (string.IsNullOrWhiteSpace(customDns)) customDns = "8.8.8.8";
        string directDns = string.IsNullOrWhiteSpace(_currentSettings.DirectDns) ? "77.88.8.8" : _currentSettings.DirectDns.Trim();
        string mode = overrideMode ?? _currentSettings.Mode;
        int proxyPort = overrideProxyPort ?? (_currentSettings.ProxyPort > 0 ? _currentSettings.ProxyPort : 2080);

        Dictionary<string, object>? wireguardEndpoint = null;
        Dictionary<string, object>? proxyOutbound = null;

        if (p.Protocol == "wireguard")
        {
            var wgAddresses = (string.IsNullOrEmpty(p.LocalAddress) ? "10.0.0.2/32" : p.LocalAddress)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.Contains('/') ? a : $"{a}/32")
                .ToArray();

            wireguardEndpoint = new Dictionary<string, object>
            {
                ["type"] = "wireguard",
                ["tag"] = "proxy",
                ["system"] = false,
                ["mtu"] = p.Mtu > 0 ? p.Mtu : 1420,
                ["address"] = wgAddresses,
                ["private_key"] = p.PrivateKey,
                ["domain_resolver"] = "direct-dns",
                ["peers"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["address"] = p.Address,
                        ["port"] = p.Port,
                        ["public_key"] = p.PublicKey,
                        ["allowed_ips"] = new[] { "0.0.0.0/0", "::/0" },
                        ["persistent_keepalive_interval"] = 25
                    }
                }
            };
        }
        else
        {
            var outbound = new Dictionary<string, object>
            {
                ["type"] = p.Protocol,
                ["tag"] = "proxy",
                ["server"] = p.Address,
                ["server_port"] = p.Port
            };

            if (p.Protocol == "vless")
            {
                outbound["uuid"] = p.Uuid;
                bool hasTls = p.Security != "none" && (p.Security is "tls" or "reality" || p.Port == 443 || !string.IsNullOrEmpty(p.Pbk) || !string.IsNullOrEmpty(p.Sni));
                if (hasTls) outbound["tls"] = BuildTlsConfig(p, isQuic: false, allowReality: true);

                string tr = (p.Transport ?? "").Trim().ToLowerInvariant();
                bool isTcpTransport = string.IsNullOrEmpty(tr) || tr is "tcp" or "raw" or "none";
                if (hasTls && isTcpTransport && !string.IsNullOrEmpty(p.Flow))
                {
                    string flow = p.Flow.EndsWith("-udp443", StringComparison.OrdinalIgnoreCase) ? p.Flow[..^7] : p.Flow;
                    if (!string.IsNullOrEmpty(flow)) outbound["flow"] = flow;
                }

                ApplyTransport(outbound, p);
            }
            else if (p.Protocol == "vmess")
            {
                outbound["uuid"] = p.Uuid;
                outbound["alter_id"] = p.AlterId;
                outbound["security"] = string.IsNullOrEmpty(p.Security) || p.Security is "tls" or "reality" ? "auto" : p.Security;
                if (p.Security is "tls" || (p.Security != "none" && (p.Port == 443 || !string.IsNullOrEmpty(p.Sni))))
                    outbound["tls"] = BuildTlsConfig(p, isQuic: false, allowReality: false);

                ApplyTransport(outbound, p);
            }
            else if (p.Protocol == "trojan")
            {
                outbound["password"] = p.Password;
                if (p.Security != "none")
                    outbound["tls"] = BuildTlsConfig(p, isQuic: false, allowReality: false);

                ApplyTransport(outbound, p);
            }
            else if (p.Protocol == "shadowsocks")
            {
                outbound["method"] = string.IsNullOrEmpty(p.Method) ? "chacha20-ietf-poly1305" : p.Method;
                outbound["password"] = p.Password;
            }
            else if (p.Protocol is "hysteria" or "hysteria2")
            {
                if (p.Protocol == "hysteria2") outbound["password"] = p.Password;
                else outbound["auth_str"] = p.Password;

                if (int.TryParse(Regex.Match(p.Up ?? "", @"\d+").Value, out var up) && up > 0) outbound["up_mbps"] = up;
                else if (p.Protocol == "hysteria") outbound["up_mbps"] = 100;

                if (int.TryParse(Regex.Match(p.Down ?? "", @"\d+").Value, out var dn) && dn > 0) outbound["down_mbps"] = dn;
                else if (p.Protocol == "hysteria") outbound["down_mbps"] = 100;

                if (!string.IsNullOrEmpty(p.Obfs) && !p.Obfs.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Protocol == "hysteria2")
                    {
                        if (p.Obfs.Equals("salamander", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.ObfsPassword))
                            outbound["obfs"] = new { type = "salamander", password = p.ObfsPassword };
                        else if (p.Obfs.Equals("gecko", StringComparison.OrdinalIgnoreCase))
                            outbound["obfs"] = new { type = "gecko" };
                    }
                    else
                    {
                        outbound["obfs"] = p.Obfs;
                    }
                }
                outbound["tls"] = BuildTlsConfig(p, isQuic: true, allowReality: false);
            }
            else if (p.Protocol == "tuic")
            {
                outbound["uuid"] = p.Uuid;
                outbound["password"] = p.Password;
                outbound["congestion_control"] = "bbr";
                outbound["tls"] = BuildTlsConfig(p, isQuic: true, allowReality: false);
            }
            else if (p.Protocol is "socks" or "http")
            {
                if (!string.IsNullOrEmpty(p.Uuid)) outbound["username"] = p.Uuid;
                if (!string.IsNullOrEmpty(p.Password)) outbound["password"] = p.Password;
                if (p.Protocol == "http" && p.Security == "tls") outbound["tls"] = BuildTlsConfig(p, isQuic: false, allowReality: false);
            }

            proxyOutbound = outbound;
        }

        var routeRules = new List<object>
        {
            new { action = "sniff" },
            new { protocol = "dns", action = "hijack-dns" },
            new { ip_is_private = true, action = "route", outbound = "direct" }
        };

        var directIps = new HashSet<string>();
        var directDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in _profiles.Where(x => !string.IsNullOrWhiteSpace(x.Address)))
        {
            string addr = profile.Address.Trim();
            if (IPAddress.TryParse(addr, out var ip))
            {
                if (!ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
                        ip = new IPAddress(ip.GetAddressBytes());
                    directIps.Add(ip.ToString());
                }
            }
            else if (!addr.Contains(' ') && addr is not "localhost")
            {
                directDomains.Add(addr);
            }
        }

        if (directIps.Count > 0) routeRules.Add(new { ip_cidr = directIps.ToArray(), action = "route", outbound = "direct" });
        if (directDomains.Count > 0) routeRules.Add(new { domain = directDomains.ToArray(), action = "route", outbound = "direct" });

        string ruleOutbound = _currentSettings.RouteMode == "Only" ? "proxy" : "direct";
        string finalOutbound = _currentSettings.RouteMode == "Only" ? "direct" : "proxy";
        string ruleDns = _currentSettings.RouteMode == "Only" ? "remote-dns" : "direct-dns";
        string finalDns = _currentSettings.RouteMode == "Only" ? "direct-dns" : "remote-dns";

        routeRules.Add(new { domain_suffix = new[] { "ipinfo.io", "ipwho.is", "ip-api.com", "flagcdn.com", "cloudflare.com", "ipify.org" }, action = "route", outbound = "proxy" });

        if (_currentSettings.BypassRuDomains && _currentSettings.RouteMode != "Only")
            routeRules.Add(new { domain_suffix = new[] { ".ru", ".рф", ".xn--p1ai", ".su" }, action = "route", outbound = "direct" });

        var routingIpCidrs = _currentSettings.RoutingDomains.Where(IsIpOrCidr).ToArray();
        var routingDomainSuffixes = _currentSettings.RoutingDomains.Where(d => !IsIpOrCidr(d)).ToArray();

        if (routingDomainSuffixes.Length > 0) routeRules.Add(new { domain_suffix = routingDomainSuffixes, action = "route", outbound = ruleOutbound });
        if (routingIpCidrs.Length > 0) routeRules.Add(new { ip_cidr = routingIpCidrs, action = "route", outbound = ruleOutbound });
        if (_currentSettings.RoutingApps.Count != 0) routeRules.Add(new { process_name = _currentSettings.RoutingApps.ToArray(), action = "route", outbound = ruleOutbound });

        var dnsRules = new List<object>
        {
            new { domain_suffix = new[] { "ipinfo.io", "ipwho.is", "ip-api.com", "flagcdn.com", "cloudflare.com", "ipify.org" }, action = "route", server = "remote-dns" }
        };

        if (_currentSettings.BypassRuDomains && _currentSettings.RouteMode != "Only")
            dnsRules.Add(new { domain_suffix = new[] { ".ru", ".рф", ".xn--p1ai", ".su" }, action = "route", server = "direct-dns" });

        if (routingDomainSuffixes.Length > 0) dnsRules.Add(new { domain_suffix = routingDomainSuffixes, action = "route", server = ruleDns });

        var inbounds = new List<object>();
        if (mode == "Proxy")
        {
            inbounds.Add(new
            {
                type = "mixed",
                tag = "mixed-in",
                listen = _currentSettings.AllowLan ? "0.0.0.0" : "127.0.0.1",
                listen_port = proxyPort
            });
        }
        else
        {
            inbounds.Add(new
            {
                type = "tun",
                tag = "tun-in",
                interface_name = _currentTunInterfaceName,
                address = _currentSettings.EnableIpv6 ? new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" } : new[] { "172.19.0.1/30" },
                auto_route = true,
                strict_route = _currentSettings.StrictRoute,
                stack = string.IsNullOrEmpty(_currentSettings.TunStack) ? "system" : _currentSettings.TunStack
            });
        }

        var root = new Dictionary<string, object>
        {
            ["log"] = new { level = string.IsNullOrEmpty(_currentSettings.LogLevel) ? "info" : _currentSettings.LogLevel, timestamp = true },
            ["dns"] = new
            {
                servers = new object[]
                {
                    BuildDnsServer("remote-dns", customDns, "proxy"),
                    BuildDnsServer("direct-dns", directDns, null)
                },
                rules = dnsRules.ToArray(),
                final = finalDns,
                strategy = (_currentSettings.EnableIpv6 || mode == "Proxy") ? "prefer_ipv4" : "ipv4_only"
            },
            ["inbounds"] = inbounds.ToArray()
        };

        if (wireguardEndpoint != null)
        {
            root["endpoints"] = new object[] { wireguardEndpoint };
            root["outbounds"] = new object[] { new { type = "direct", tag = "direct" } };
        }
        else
        {
            root["outbounds"] = new object[] { proxyOutbound!, new { type = "direct", tag = "direct" } };
        }

        root["route"] = new
        {
            rules = routeRules.ToArray(),
            auto_detect_interface = mode != "Proxy",
            final = finalOutbound,
            default_domain_resolver = "direct-dns"
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object BuildTlsConfig(VpnProfile p, bool isQuic = false, bool allowReality = true)
    {
        var tls = new Dictionary<string, object> { ["enabled"] = true };
        if (!string.IsNullOrEmpty(p.Sni)) tls["server_name"] = p.Sni;
        if (!string.IsNullOrEmpty(p.Alpn))
        {
            var alpnList = p.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (alpnList.Length > 0) tls["alpn"] = alpnList;
        }
        if (!isQuic)
        {
            string fp = p.Fp?.Equals("none", StringComparison.OrdinalIgnoreCase) == true ? "" : (p.Fp ?? "");
            if (allowReality && !string.IsNullOrEmpty(p.Pbk) && string.IsNullOrEmpty(fp))
                fp = "chrome";

            if (!string.IsNullOrEmpty(fp)) tls["utls"] = new { enabled = true, fingerprint = fp };
            if (allowReality && !string.IsNullOrEmpty(p.Pbk)) tls["reality"] = new { enabled = true, public_key = p.Pbk, short_id = p.Sid ?? "" };
        }
        if (p.Insecure) tls["insecure"] = true;
        return tls;
    }

    private void BtnShowModels_Click(object sender, RoutedEventArgs e)
    {
        if (_fullScreenOverlay != null)
        {
            _fullScreenOverlay.Close();
            _fullScreenOverlay = null;
            return;
        }

        string baseModels = "mind-kozel, gpt-oss-120b, gpt-oss-20b, gpt-5.2-chat, gpt-5.1-chat, gpt-5.1-2025-11-13, gpt-5.1, gpt-5-search-api-2025-10-14, gpt-5-search, gpt-5-nano-2025-08-07, gpt-5-nano, gpt-5-mini-2025-08-07, gpt-5-mini, gpt-5-chat, gpt-5-2025-08-07, gpt-5, gpt-5.5, gpt-5.2, gpt-5.4, p-gpt-5.4-nano, p-gpt-5.4-mini, pplx-gpt-5.2, pplx-gpt-5.1, pplx-gpt-5-mini, gpt-4.1-nano-2025-04-14, gpt-4.1-nano, gpt-4.1-mini-2025-04-14, gpt-4.1-mini, gpt-4.1-2025-04-14, gpt-4.1, chatgpt-4o, gpt-4o-search-preview-2025-03-11, gpt-4o-search-preview, gpt-4o-mini-search-preview-2025-03-11, gpt-4o-mini-search-preview, gpt-4o-mini-2024-07-18, gpt-4o-mini, gpt-4o-2024-11-20, gpt-4o-2024-08-06, gpt-4o-2024-05-13, gpt-4o, gpt-4-turbo-preview, gpt-4-turbo-2024-04-09, gpt-4-turbo, gpt-4-1106-preview, gpt-4-0613, gpt-4-0125-preview, gpt-4, gpt-3.5-turbo-16k, gpt-3.5-turbo-1106, gpt-3.5-turbo-0125, gpt-3.5-turbo, gpt-image-1.5, gpt-image-1, gpt-image-1-mini, gemini-1.0-pro, gemini-1.5-pro, gemini-1.5-flash, gemini-2.0-flash, gemini-2.0-flash-lite, gemini-2.5-pro, gemini-2.5-flash, gemini-2.5-flash-lite, gemini-3-flash, gemini-3.5-flash, gemini-3.1-pro, gemini-omni, pplx-gemini-3.1-pro, pplx-gemini-3-flash, deepseek-v1, deepseek-v2, deepseek-v2-chat, deepseek-v2-lite, deepseek-v2.5, deepseek-v3, deepseek-v3-chat, deepseek-v3.1, deepseek-v3.2, deepseek-v4, deepseek-r1, deepseek-r1-lite, deepseek-r1-distill-qwen-1.5b, deepseek-r1-distill-qwen-7b, deepseek-r1-distill-qwen-14b, deepseek-r1-distill-qwen-32b, deepseek-r1-distill-llama-8b, deepseek-r1-distill-llama-70b, deepseek-r2, deepseek-coder, deepseek-coder-v2, deepseek-coder-v2-lite, deepseek-coder-33b, deepseek-coder-6.7b, deepseek-coder-1.3b, deepseek-math-7b, deepseek-vl, deepseek-vl-7b, deepseek-vl2, deepseek-llm-7b, deepseek-llm-67b, deepseek-moe-16b, deepseek-prover, deepseek-prover-v1.5, qwen-1.8b, qwen-7b, qwen-14b, qwen-72b, qwen-1.5-0.5b, qwen-1.5-1.8b, qwen-1.5-4b, qwen-1.5-7b, qwen-1.5-14b, qwen-1.5-32b, qwen-1.5-72b, qwen-1.5-110b, qwen-1.5-moe-a2.7b, qwen-2-0.5b, qwen-2-1.5b, qwen-2-7b, qwen-2-57b-a14b, qwen-2-72b, qwen-2.5-0.5b, qwen-2.5-1.5b, qwen-2.5-3b, qwen-2.5-7b, qwen-2.5-14b, qwen-2.5-32b, qwen-2.5-72b, qwen-2.5-coder-1.5b, qwen-2.5-coder-7b, qwen-2.5-coder-14b, qwen-2.5-coder-32b, qwen-2.5-math-1.5b, qwen-2.5-math-7b, qwen-2.5-math-72b, qwen-2.5-vl-3b, qwen-2.5-vl-7b, qwen-2.5-vl-72b, qwen-2.5-omni-7b, qwen-3-0.6b, qwen-3-1.7b, qwen-3-4b, qwen-3-8b, qwen-3-14b, qwen-3-32b, qwen-3-30b-a3b, qwen-3-235b-a22b, qwen-3-coder-30b, qwen-3-coder-480b, qwen-3-max, qwen-3-max-thinking, qwen-3-vl-30b, qwen-3-vl-235b, qwen-3.5, qwen-3.5-max, qwen-4, qwq-32b, qwq-32b-preview, qvq-72b-preview, qwen-plus, qwen-turbo, qwen-max, qwen-long, qwen-vl-plus, qwen-vl-max, qwen-audio, qwen-audio-chat, qwen-omni-turbo, claude-1, claude-1.3, claude-instant-1, claude-instant-1.2, claude-2, claude-2.0, claude-2.1, claude-3-haiku, claude-3-sonnet, claude-3-opus, claude-3.5-haiku, claude-3.5-sonnet, claude-3.5-opus, claude-3.7-haiku, claude-3.7-sonnet, claude-3.7-opus, claude-4-haiku, claude-4-sonnet, claude-4-opus, claude-4.5-sonnet, claude-4.5-opus, claude-5-sonnet-preview, llama-1-7b, llama-1-13b, llama-1-33b, llama-1-65b, llama-2-7b, llama-2-7b-chat, llama-2-13b, llama-2-13b-chat, llama-2-70b, llama-2-70b-chat, llama-3-8b, llama-3-8b-instruct, llama-3-70b, llama-3-70b-instruct, llama-3.1-8b, llama-3.1-8b-instruct, llama-3.1-70b, llama-3.1-70b-instruct, llama-3.1-405b, llama-3.1-405b-instruct, llama-3.2-1b, llama-3.2-1b-instruct, llama-3.2-3b, llama-3.2-3b-instruct, llama-3.2-11b-vision, llama-3.2-11b-vision-instruct, llama-3.2-90b-vision, llama-3.2-90b-vision-instruct, llama-3.3-70b, llama-3.3-70b-instruct, llama-4-8b, llama-4-70b, llama-4-400b, llama-4-multimodal, codellama-7b, codellama-13b, codellama-34b, codellama-70b, codellama-instruct, codellama-python, mistral-7b-v0.1, mistral-7b-v0.2, mistral-7b-v0.3, mistral-7b-instruct-v0.1, mistral-7b-instruct-v0.2, mistral-7b-instruct-v0.3, mixtral-8x7b-v0.1, mixtral-8x7b-instruct-v0.1, mixtral-8x22b, mixtral-8x22b-instruct, mistral-medium, mistral-small-2402, mistral-small-2409, mistral-large-2402, mistral-large-2407, mistral-large-2411, mistral-large-2503, pixtral-12b, pixtral-large-124b, codestral-22b, codestral-mamba, mathstral-7b, ministral-3b, ministral-8b, mistral-nemo-12b, mistral-nemo-instruct-12b, mistral-next, phi-1, phi-1.5, phi-2, phi-3-mini-4k-instruct, phi-3-mini-128k-instruct, phi-3-small-8k-instruct, phi-3-small-128k-instruct, phi-3-medium-4k-instruct, phi-3-medium-128k-instruct, phi-3-vision-128k-instruct, phi-3.5-mini-instruct, phi-3.5-moe-instruct, phi-3.5-vision-instruct, phi-4, phi-4-mini, phi-4-multimodal, wizardlm-7b, wizardlm-13b, wizardlm-30b, wizardlm-70b, wizardlm-2-7b, wizardlm-2-8x22b, wizardlm-2-70b, wizardcoder-15b, wizardcoder-33b, wizardmath-7b, wizardmath-70b, palm-1, palm-2, palm-2-bison, palm-2-unicorn, gemma-2b, gemma-2b-it, gemma-7b, gemma-7b-it, gemma-2-2b, gemma-2-2b-it, gemma-2-9b, gemma-2-9b-it, gemma-2-27b, gemma-2-27b-it, gemma-3-4b, gemma-3-12b, gemma-3-27b, codegemma-2b, codegemma-7b, codegemma-7b-it, recurrentgemma-2b, recurrentgemma-2b-it, paligemma-3b-mix-224, paligemma-3b-mix-448, grok-1, grok-1.5, grok-1.5-vision, grok-2, grok-2-mini, grok-3, grok-3-mini, grok-3-pro, grok-3-vision, grok-4-beta, command, command-light, command-nightly, command-r-v0.1, command-r-plus-v0.1, command-r-08-2024, command-r-plus-08-2024, command-r7b, aya-101, aya-23-8b, aya-23-35b, aya-expanse-8b, aya-expanse-32b, yi-6b, yi-6b-chat, yi-9b, yi-34b, yi-34b-chat, yi-vl-6b, yi-vl-34b, yi-1.5-6b, yi-1.5-6b-chat, yi-1.5-9b, yi-1.5-9b-chat, yi-1.5-34b, yi-1.5-34b-chat, yi-large, yi-large-turbo, yi-vision, yi-lightning, yi-lightning-lite, falcon-7b, falcon-7b-instruct, falcon-40b, falcon-40b-instruct, falcon-180b, falcon-180b-chat, falcon-mamba-7b, falcon-2-11b, falcon-2-11b-vlm, falcon-3-1b, falcon-3-3b, falcon-3-7b, falcon-3-10b, falcon-3-10b-vision, chatglm-6b, chatglm2-6b, chatglm3-6b, glm-4-9b, glm-4-9b-chat, glm-4v-9b, glm-4-plus, glm-4-air, glm-4-airx, glm-4-flash, glm-4-long, glm-zero, ernie-bot, ernie-bot-turbo, ernie-bot-4.0, ernie-3.5-8b, ernie-4.0-8k, hunyuan-pro, hunyuan-standard, hunyuan-lite, hunyuan-vision, baichuan-7b, baichuan-13b, baichuan2-7b, baichuan2-13b, baichuan-3, tongyi-qianwen-max, yandexgpt, yandexgpt-2, yandexgpt-3-lite, yandexgpt-3-pro, yandexgpt-4, yandexgpt-4-pro, gigachat, gigachat-lite, gigachat-pro, gigachat-max, saiga-llama-3-8b, saiga-llama-3-70b, saiga-mistral-7b, saiga-gemma-2-9b, ruGPT-3.5-13B, ruGPT-4, dbrx-base, dbrx-instruct, dolly-v2-3b, dolly-v2-7b, dolly-v2-12b, jamba-v0.1, jamba-instruct, jamba-1.5-mini, jamba-1.5-large, jurassic-1-jumbo, jurassic-2-ultra, jurassic-2-mid, jurassic-2-light, solar-10.7b-v1.0, solar-10.7b-instruct, solar-pro, reka-core, reka-flash, reka-edge, nemotron-3-8b, nemotron-4-15b, nemotron-4-340b-base, nemotron-4-340b-instruct, nemotron-4-340b-reward, llama-3.1-nemotron-70b-instruct, granite-3b-code, granite-8b-code, granite-13b-chat, granite-34b-code, granite-3.0-8b-instruct, granite-3.0-2b-instruct, olmo-1b, olmo-7b, olmo-7b-instruct, olmo-1.7-7b, olmo-2-7b, olmo-2-13b, tulu-2-7b, tulu-2-70b, tulu-3-8b, tulu-3-70b, zephyr-7b-alpha, zephyr-7b-beta, zephyr-7b-gemma, starcoder, starcoderbase, starcoder2-3b, starcoder2-7b, starcoder2-15b, smaug-72b, nous-hermes-13b, nous-hermes-2-mixtral-8x7b, nous-hermes-2-vision, nous-hermes-3-llama-3.1-8b, nous-hermes-3-llama-3.1-70b, nous-hermes-3-llama-3.1-405b, openhermes-2.5-mistral-7b, capybara-7b, airoboros-l2-70b, dolphin-2.0-mistral-7b, dolphin-2.1-mistral-7b, dolphin-2.2-yi-34b, dolphin-2.5-mixtral-8x7b, dolphin-2.6-phi-2, dolphin-2.8-experiment26-7b, dolphin-2.9-llama3-8b, dolphin-2.9.2-qwen2-7b, mpt-7b, mpt-7b-instruct, mpt-7b-chat, mpt-30b, mpt-30b-instruct, mpt-30b-chat, xgen-7b-8k, xgen-7b-8k-inst, internlm-7b, internlm-chat-7b, internlm2-1.8b, internlm2-7b, internlm2-20b, internlm2.5-7b, internlm2.5-20b, aquila-7b, aquila2-34b, aquilachat2-34b, stablelm-3b-4e1t, stablelm-base-alpha-7b, stable-zephyr-3b, stable-code-3b, stable-beluga-7b, stable-beluga-13b, stable-beluga-2, mm1, mm1.5, openelm-270m, openelm-450m, openelm-1.1b, openelm-3b, apple-afm-on-device, apple-afm-server, apple-intelligence-ajax, amazon-titan-text-lite, amazon-titan-text-express, amazon-titan-text-premier, olympus-amazon, sambalingo-arabic, sambalingo-thai, samba-coe-v0.1, gpt-neo-125m, gpt-neo-1.3b, gpt-neo-2.7b, gpt-j-6b, gpt-neox-20b, pythia-70m, pythia-160m, pythia-410m, pythia-1b, pythia-1.4b, pythia-2.8b, pythia-6.9b, pythia-12b, bloom-560m, bloom-1b1, bloom-1b7, bloom-3b, bloom-7b1, bloom-176b, bloomz-176b, opt-125m, opt-350m, opt-1.3b, opt-2.7b, opt-6.7b, opt-13b, opt-30b, opt-66b, opt-175b, cerebras-gpt-111m, cerebras-gpt-13b, redpajama-incite-base-7b, redpajama-incite-chat-7b, llava-1.5-7b, llava-1.5-13b, llava-v1.6-34b, llava-next-72b, llava-next-qwen-72b, moondream1, moondream2, cogvlm, cogvlm2, cogvlm2-video, fuyu-8b, idefics-9b, idefics-80b, idefics2-8b, internvl-chat-v1.5, internvl2-1b, internvl2-2b, internvl2-4b, internvl2-8b, internvl2-26b, internvl2-76b, internvl2-108b, mini-cpm-v-2.0, mini-cpm-v-2.5, mini-cpm-v-2.6, mini-cpm-3-4b, qwen-vl-chat, qwen2-vl-7b, qwen2.5-vl-72b, stable-diffusion-v1.4, stable-diffusion-v1.5, stable-diffusion-2.0, stable-diffusion-2.1, sd-xl-1.0, sd-xl-turbo, sdxl-lightning, stable-cascade, sd3-medium, sd3-large, sd3-ultra, flux.1-schnell, flux.1-dev, flux.1-pro, midjourney-v1, midjourney-v2, midjourney-v3, midjourney-v4, midjourney-v5, midjourney-v5.1, midjourney-v5.2, midjourney-v6, midjourney-v6.1, midjourney-v7, dall-e-1, dall-e-2, dall-e-3, imagen, imagen-2, imagen-3, parti, muse, pixie, sora, runway-gen1, runway-gen2, runway-gen3-alpha, runway-gen3-turbo, pika-1.0, pika-2.0, kling-v1, kling-v1.5, luma-dream-machine, haiper-v1, haiper-v2, cogvideo, cogvideox-2b, cogvideox-5b, hunyuan-video, minmax-video, veed, stable-video-diffusion, svd-xt, whisper-tiny, whisper-base, whisper-small, whisper-medium, whisper-large-v1, whisper-large-v2, whisper-large-v3, whisper-large-v3-turbo, bark, tortoise-tts, vits, seamless-m4t-v2, voicebox, audiocraft, musicgen, stable-audio, suno-v2, suno-v3, suno-v3.5, udio-v1, udio-v1.5, elevenlabs-turbo-v2, elevenlabs-multilingual-v2, text-davinci-001, text-davinci-002, text-davinci-003, text-curie-001, text-babbage-001, text-ada-001, code-davinci-002, code-cushman-001, gpt-2, gpt-2-medium, gpt-2-large, gpt-2-xl, gpt-1, bert-base-uncased, roberta-base, distilbert, t5-small, t5-base, t5-large, t5-3b, t5-11b, flan-t5-small, flan-t5-base, flan-t5-large, flan-t5-xl, flan-t5-xxl, llama-65b, miqu-1-70b, midnight-miqu-70b, goliath-120b, noromaid-v0.1-mixtral-8x7b-instruct, kunoichi-dpo-v2-7b, fimbulvetr-11b, bagel-34b, openchat-3.5-0106, openchat-3.5-1210, starling-lm-7b-alpha, starling-lm-11b-beta, neural-chat-7b-v3-1, toppy-m-7b, mythomax-l2-13b, tiefighter-13b, chronos-hermes-13b, nous-capybara-34b, yi-coder-1.5b, yi-coder-9b, deepseek-coder-7b-base, phind-codellama-34b-v2, euryale-1.3-l2-70b, xwin-lm-70b, alpaca-7b, vicuna-7b, vicuna-13b, vicuna-33b, guanaco-7b, guanaco-33b, guanaco-65b, qwen1.5-0.5b-chat, qwen1.5-1.8b-chat, rwkv-4-pile-14b, rwkv-5-world-1.5b, rwkv-5-world-3b, rwkv-6-world-7b, mamba-130m, mamba-370m, mamba-790m, mamba-1.4b, mamba-2.8b, mamba2-130m, mamba2-2.7b, jina-embeddings-v2-base-en, bge-m3, bge-large-en-v1.5, nomic-embed-text, all-minilm-l6-v2, e5-mistral-7b-instruct, mxbai-embed-large-v1, cohere-embed-english-v3.0, text-embedding-3-small, text-embedding-3-large, text-embedding-ada-002, claude-3-haiku-20240307, claude-3-sonnet-20240229, claude-3-opus-20240229, claude-3-5-sonnet-20240620, claude-3-5-sonnet-20241022, gemini-1.5-pro-exp-0801, gemini-1.5-pro-exp-0827, gemini-1.5-flash-exp-0827, gemini-1.5-flash-8b-exp-0827, command-r-v0.1-4bit, command-r-plus-v0.1-8bit, mixtral-8x7b-instruct-v0.1-gguf, llama-3-70b-instruct-awq, qwen-2.5-72b-instruct-exl2, meta-llama-3-120b, mergekit-slerp, miqu-1-120b, senku-70b, xwin-math-7b, leetcode-copilot, copilot-chat, amazon-q, tabnine, codeium-base, deepcode, replit-code-v1-3b, phi-2-dpo, stable-lm-2-12b, plamo-13b, swallow-13b, elyza-japanese-llama-2-7b, qwen-14b-chat-int4, chatglm3-6b-32k, baichuan2-13b-chat-4bits, blue-orchid-2x7b, llemma-7b, llemma-34b, minotaur-15b, platypus2-70b, speecht5, bark-small, seamless-m4t-large, distil-whisper-large-v3, openvoice, metavoice-1b, litserve, vllm-engine, text-bison-001, text-bison-002, chat-bison-001, chat-bison-002, code-bison-001, code-bison-002, codechat-bison-001, codechat-bison-002, gemini-1.0-pro-vision, gemini-1.5-pro-latest, gemini-1.5-flash-latest, medlm, sec-palm, audio-palm, med-palm-2, video-poem, magika, alphafold-1, alphafold-2, alphafold-3, alphacode, alphacode-2, alphageometry, alphastar, muzero, leela-chess-zero, stockfish-16.1-nnue, llama-3-typhoon-v1.5-8b, qwen-2-math-72b-instruct, qwen-2-audio-7b, grok-1.5-vision-preview, mistral-large-2402-api, x-ai-grok-beta, openai-o1, openai-o1-preview, openai-o1-mini, chatgpt-o1, gpt-4o-realtime-preview, gpt-4o-audio-preview, gpt-4o-2024-11-20-vision, claude-3-5-haiku-20241022, claude-3-5-sonnet-v2, claude-3-5-opus-v2, llama-3.2-11b-vision-instruct-maestro, llama-3.2-90b-vision-instruct-maestro, qwen-2.5-max, qwen-2.5-plus, qwen-2.5-turbo, deepseek-v2-coder, deepseek-v2-chat-0628, deepseek-v3-base, deepseek-r1-zero, deepseek-r1-distill-llama-3-8b, deepseek-r1-distill-qwen-2.5-7b, moonshot-v1-8k, moonshot-v1-32k, moonshot-v1-200k, kimi-chat, abab6.5-chat, abab6.5s-chat, minimax-text-01, minimax-text-02, sensevoice-small, cosam, omchat-v2-13b, bria-v2.3, hunyuan-dit, lumina-next-t2i, pixart-alpha, pixart-sigma, sd3.5-large, sd3.5-large-turbo, sd3.5-medium, deepfloyd-if, kandinsky-2.1, kandinsky-2.2, kandinsky-3.0, wuerstchen, amused-512, aura-flow, mamba-codestral-7b-v0.1, jamba-1.5-large-instruct, jamba-1.5-mini-instruct, aya-expanse-32b-it, aya-expanse-8b-it, command-r7b-12-2024, granite-3.0-8b-instruct-preview, granite-3.0-2b-instruct-preview, ibm-granite-20b-code, falcon-mamba-7b-instruct, falcon-3-10b-instruct, falcon-3-7b-instruct, falcon-3-3b-instruct, falcon-3-1b-instruct, reka-core-20240501, reka-flash-20240226, reka-edge-20240208, xai-grok-2-1212, xai-grok-2-vision-1212, p-gpt-5.5, p-gpt-5.6, p-gpt-6.0-alpha, gpt-6-preview, gpt-6-nano, gpt-6-mini, gpt-6-search, openai-o2, openai-o2-mini, openai-o2-preview, openai-o3, openai-o3-mini, gemini-3.0-pro, gemini-3.0-ultra, gemini-3.0-flash, gemini-4.0-flash, claude-4-opus, claude-4-sonnet, claude-4-haiku, claude-5-opus, llama-4-8b-instruct, llama-4-70b-instruct, llama-4-400b-instruct, llama-5-base, qwen-4-max, qwen-4-plus, qwen-4-turbo, qwen-5-vl, deepseek-v4-chat, deepseek-v4-coder, deepseek-r3, deepseek-r4, mistral-large-2504, mixtral-8x34b, pixtral-huge, phi-4-vision, phi-4-multimodal-instruct, phi-5, gemma-4-2b, gemma-4-9b, gemma-4-27b, grok-4, grok-4-pro, grok-4-vision, grok-5, midjourney-v8, midjourney-v9, sora-v2, sora-v3, runway-gen4, pika-3.0, kling-v2, kling-v3, luma-dream-machine-v2, stable-diffusion-3.5, flux.2-pro, flux.2-dev, flux.2-schnell, dall-e-4, dall-e-5, suno-v4, suno-v5, udio-v2, udio-v3, elevenlabs-v3, elevenlabs-v4, whisper-v4, whisper-v5, yolov10, yolov11, yolov12, segment-anything-2, segment-anything-3, detic, groundingdino, blip-2, blip-3, instructblip, mplug-owl, mplug-owl2, mplug-owl3, qwen-vl-max-0809, qwen-audio-turbo, qwen-math-turbo, open-sora, open-sora-plan, latte, mora, opensora-v1.2, h2o-danube-1.8b, h2o-danube2-1.8b, h2o-danube3-4b, qwen2-math-1.5b, qwen2-math-7b, qwen2-math-72b, numina-math-7b-tir, numina-math-7b-cot, map-neo-7b, map-neo-7b-instruct, ziya-llama-13b, ziya-llama-13b-v1.1, ziya2-13b, sus-chat-34b, rwkv-6-world-3b, rwkv-6-world-1.5b, eagle-7b, minicpm-2b-sft-bf16, minicpm-2b-dpo-bf16, minicpm-moe-8x2b, xverse-7b, xverse-13b, xverse-65b, xverse-65b-chat, deepseek-coder-1.3b-base, deepseek-coder-1.3b-instruct, deepseek-coder-6.7b-base, deepseek-coder-6.7b-instruct, deepseek-coder-33b-base, deepseek-coder-33b-instruct, codefuse-codellama-34b, codefuse-deepseek-33b, qwen-coder-1.5b, qwen-coder-7b, wizardcoder-python-7b-v1.0, wizardcoder-python-13b-v1.0, wizardcoder-python-34b-v1.0, phi-1.5-web, phi-2-electra, btlm-3b-8k, cerenacas-gpt, cerebras-btd, databricks-dolly-v1-6b, mosaic-mpt-7b-storywriter, mosaic-mpt-30b-chat, h2oai-predict, llm360-amber, llm360-crystal, llm360-diamond, nvidia-megatron-turing-nlg-530b, google-switch-transformer, google-glam, google-gopher, google-chinchilla, google-lamda, google-sparrow, google-meena, facebook-blenderbot, facebook-blenderbot-2, facebook-blenderbot-3, facebook-galactica, facebook-opt-iml, facebook-opt-iml-max, anthropic-hh-rlhf, anthropic-claude-v1.3-100k, anthropic-claude-v1-100k, aleph-alpha-luminous-base, aleph-alpha-luminous-extended, aleph-alpha-luminous-supreme, lighton-alfred-40b, nlpcloud-finetuned-llama, bria-rmbg-1.4, isnet-general-use, u2net, rembg, dexined, pidinet, controlnet-canny, controlnet-depth, controlnet-hed, controlnet-mlsd, controlnet-normal, controlnet-openpose, controlnet-scribble, controlnet-seg, ip-adapter, ip-adapter-faceid, ip-adapter-plus, lora-xl, lycoris, lokr, loha, svd-xt-1.1, svd-image-decoder, animatediff, animatediff-v2, animatediff-v3, text2video-zero, zeroscope-v2-576w, zeroscope-v2-xl, modelscope-damo-text-to-video, potat-1, vqgan-clip, clip-vit-base-patch32, clip-vit-large-patch14, siglip-so400m-patch14-384, eva-clip, openclip-vit-h-14, openclip-vit-g-14, dino-v1, dino-v2, deit, swin-transformer, vit-mae, convnext-v2, resnet-50, resnet-101, efficientnet-b0, efficientnet-b7, mobilenet-v3, yolov8, yolov9, rtdetr, mask-rcnn, faster-rcnn, cascade-rcnn, pointrend, mask2former, oneformer, detr, deformable-detr, glip, blip, x-clip, video-llava, macaw-llm, otter-video, panda-70m, internvideo, internvideo2, vlogger, emotech, hume-ai-evi, replica-ai, character-ai-c1.2, character-ai-c1.5, pi-inflection-1, pi-inflection-2, pi-inflection-2.5, coral-cohere, jasper-chat, copy-ai-chat, writesonic-chatsonic, rytr-chat, anyword-chat, quillbot-flow, grammarly-go, notion-ai, github-copilot, github-copilot-enterprise, gitlab-duo, cursor-fast, cursor-small, cursor-large, warp-ai, mutable-ai, sweep-ai, bloop-ai, cosine-genie, devin-ai, devika, swe-agent, opendevin, auto-gpt, baby-agi, meta-gpt, chatdev, camel-ai, gpt-engineer, gpt-researcher, private-gpt, local-gpt, h2o-gpt, oobabooga-text-generation-webui, koboldcpp, llama-cpp-python, vllm, text-generation-inference, ollama, lm-studio, gpt4all, jan-ai, faraday-dev, backyard-ai, anything-llm, open-webui, chatbox, msty, pinokio, comfyui, automatic1111, invokeai, fooocus, sd-next, stable-swarm, diffusion-bee, draw-things, guernika, diffusers, transformers, peft, trl, accelerate, deepspeed, megatron-lm, fairseq, fairseq2, espnet, speechbrain, kornia, timm, torchvision, torchaudio, torchtext, tensorrt-llm, onnxruntime, openvino, neural-magic-deepsparse, exllamav2, autoawq, autogptq, bitsandbytes, ggml, gguf, safetensors, pt, bin, ckpt, ckpt-v2, pth, h5, pb, tflite, coreml, mlpackage, mlmodel, ncnn, mnn, mindspore, paddlepaddle, jax, flax, optax, haiku, trax, equinox, flax-linformer, performer, reformer, routing-transformer, sinkhorn-transformer, sparse-transformer, longformer, linformer, x-transformers, lucidrains-implementations, biogpt, biogpt-large, clinicalcamel, gatortron, med-palm, med-palm-2, med-palm-3, bloomberggpt, fingpt, fingpt-v3, fingpt-v3.1, phind-codellama-34b-v1, phind-codellama-34b-v2, phind-70b, deepseek-coder-v1.5, deepseek-math-v1.5, wizardcoder-python-34b-v1.0, wizardcoder-33b-v1.1, wizardmath-70b-v1.0, openhermes-2.5, openhermes-2.5-vision, nous-hermes-2-solar-10.7b, nous-hermes-2-yi-34b, nous-hermes-2-mixtral-8x7b-dpo, samantha-1.1-llama-33b, samantha-1.2-mistral-7b, airoboros-l2-70b-2.1, airoboros-l2-70b-2.2, airoboros-l2-70b-3.1, platypus-70b, platypus2-70b-instruct, synthia-70b-v1.2, synthia-70b-v1.5, xwin-lm-70b-v0.1, xwin-lm-70b-v0.2, xwin-math-70b, orca-2-7b, orca-2-13b, orca-mini-3b, orca-mini-7b, orca-mini-13b, mpt-7b-8k, mpt-7b-8k-instruct, mpt-7b-8k-chat, mpt-30b-instruct, mpt-30b-chat, moss-moon-003-base, moss-moon-003-sft, moss-moon-003-plugin, skywork-13b-base, skywork-13b-chat, yuan-2.0-2b, yuan-2.0-51b, yuan-2.0-102b, bluelm-7b-base, bluelm-7b-chat, orion-14b-base, orion-14b-chat, polyglot-ko-1.3b, polyglot-ko-3.8b, polyglot-ko-5.8b, polyglot-ko-12.8b, koalpaca-polyglot-12.8b, rinna-3.6b, rinna-bilingual-pretrained-v2, japanese-stablelm-base-alpha-7b, japanese-stablelm-instruct-alpha-7b, karasu-1.8b, karasu-7b, qwen-japanese-7b, rt-1, rt-2, rt-x, octo-base, octo-small, openvla-7b, aloha, eliza, alice, parry, shrdlu, word2vec, glove, fasttext, ulmfit, elmo, cohere-command-r-plus, cohere-command-r-plus-08-2024, cohere-command-r-plus-12-2024, dreamshaper-xl, dreamshaper-v8, epicrealism, cyberrealistic, majicmix-realistic, absolutereality, deliberate-v2, deliberate-v3, rvc-v2, so-vits-svc-4.0, so-vits-svc-4.1, audioldm, audioldm2, audiogen, musicgen-small, musicgen-medium, musicgen-large, musicgen-melody, bark-large, tortoise-tts-v2, gpt-7, gpt-7-omni, gpt-7-brain, gpt-7.5-quantum, gpt-8, gpt-8-singularity, gpt-9-omega, gpt-10-godmode, claude-6-opus, claude-6-sonnet, claude-6-haiku, claude-7-opus, claude-7-sonnet, claude-7-haiku, llama-6-8b, llama-6-70b, llama-6-400b, llama-7-1t, llama-7-1t-instruct, gemini-5.0-pro, gemini-5.0-ultra, gemini-5.0-flash, gemini-6.0-omni, deepseek-v5, deepseek-v6, deepseek-v7, deepseek-r5, deepseek-r6, qwen-6-max, qwen-6-plus, qwen-6-turbo, qwen-7-max, qwen-8-max, mistral-large-2601, mistral-large-2701, pixtral-gigantic, grok-6, grok-7, grok-8, midjourney-v10, midjourney-v11, sora-v4, sora-v5, runway-gen5, runway-gen6, pika-4.0, pika-5.0, kling-v4, kling-v5, stable-diffusion-4.0, stable-diffusion-5.0, flux.3-pro, flux.3-dev, flux.3-schnell, dall-e-6, dall-e-7, yi-coder-34b, yi-coder-72b, yandexgpt-5, gigachat-2, saiga-llama-4, ru-gpt-5, ru-gpt-6, chatglm5-9b, chatglm6-9b, baichuan4, baichuan5, hunyuan-max, ernie-bot-5.0, ernie-bot-6.0, internlm3-7b, internlm3-20b, internvl3, cogvlm3, minicpm-v-3.0, llava-next-110b, idefics3, fuyu-32b, falcon-4, falcon-5, nemotron-5, nemotron-6, granite-4.0, granite-5.0, jamba-2.0, jamba-3.0, dbrx-2, solar-2, reka-core-2, reka-flash-2, reka-edge-2, phi-6, phi-7, gemma-5, gemma-6, aya-expanse-70b, aya-expanse-104b, command-r-v0.2, command-r-plus-v0.2, apple-afm-2, amazon-titan-2, olympus-amazon-2, samba-coe-v0.2, starcoder3, smaug-120b, capybara-34b, dolphin-3.0, dolphin-4.0, zephyr-7b-gamma, zephyr-7b-delta, miqu-2, miqu-3, goliath-240b, noromaid-v0.2, kunoichi-dpo-v3, fimbulvetr-v2, bagel-70b, openchat-4.0, starling-lm-70b, neural-chat-7b-v4, toppy-m-13b, mythomax-l2-70b, tiefighter-70b, chronos-hermes-70b, euryale-2.0, alpaca-13b, vicuna-70b, guanaco-13b, rwkv-7, rwkv-8, mamba-3, mamba-4, jina-embeddings-v3, bge-m4, nomic-embed-text-v2, all-minilm-l12-v2, e5-mistral-70b, mxbai-embed-large-v2, cohere-embed-english-v4.0, text-embedding-4, claude-3-haiku-20241022, gemini-1.5-pro-exp-0901, gemini-1.5-flash-exp-0901, mixtral-8x22b-instruct-v0.2, llama-3-120b-instruct, qwen-2.5-110b, meta-llama-4-120b, xwin-math-70b, plamo-70b, swallow-70b, elyza-japanese-llama-3-8b, blue-orchid-4x7b, llemma-70b, minotaur-70b, speecht5-large, bark-large-v2, seamless-m4t-v3, distil-whisper-large-v4, openvoice-v2, metavoice-2b, text-bison-003, chat-bison-003, code-bison-003, codechat-bison-003, magika-v2, alphafold-4, alphacode-3, alphageometry-2, alphastar-2, muzero-2, leela-chess-zero-v2, stockfish-17, llama-3-typhoon-v1.5-70b, qwen-2-math-110b, qwen-2-audio-72b, openai-o4, openai-o4-mini, openai-o4-preview, openai-o5, chatgpt-o2, chatgpt-o3, gpt-4o-realtime-preview-2, gpt-4o-audio-preview-2, claude-3-5-haiku-v2, qwen-3-max, deepseek-v4-coder-chat, deepseek-r2-zero, moonshot-v2-8k, moonshot-v2-32k, moonshot-v2-200k, kimi-chat-v2, abab7-chat, minimax-text-03, sensevoice-large, omchat-v3, bria-v3, hunyuan-dit-v2, lumina-next-t2i-v2, pixart-sigma-v2, sd4-large, sd4-large-turbo, sd4-medium, deepfloyd-if-v2, kandinsky-4.0, wuerstchen-v2, amused-1024, aura-flow-v2, mamba-codestral-72b, jamba-2-large, jamba-2-mini, granite-3.1-8b, ibm-granite-34b-code, reka-core-20241001, xai-grok-3-0202, xai-grok-3-vision-0202, whisper-v6, yolov13, yolov14, segment-anything-4, detic-v2, groundingdino-v2, blip-4, mplug-owl4, qwen-vl-max-0909, qwen-audio-turbo-v2, qwen-math-turbo-v2, open-sora-v2, latte-v2, mora-v2, h2o-danube4-4b, numina-math-72b, map-neo-72b, ziya-llama-70b, sus-chat-70b, eagle-70b, minicpm-3b, xverse-130b, codefuse-codellama-70b, wizardcoder-python-70b, phi-3-electra, btlm-7b, databricks-dolly-v2-12b, h2oai-predict-v2, llm360-emerald, nvidia-megatron-turing-nlg-1t, google-switch-transformer-v2, google-glam-v2, google-gopher-v2, google-chinchilla-v2, google-lamda-v2, google-sparrow-v2, google-meena-v2, facebook-blenderbot-4, facebook-galactica-v2, facebook-opt-iml-max-v2, anthropic-hh-rlhf-v2, aleph-alpha-luminous-supreme-v2, lighton-alfred-70b, nlpcloud-finetuned-llama-3, bria-rmbg-2.0, isnet-general-use-v2, u2net-v2, rembg-v2, dexined-v2, pidinet-v2, controlnet-canny-v2, controlnet-depth-v2, controlnet-hed-v2, controlnet-mlsd-v2, controlnet-normal-v2, controlnet-openpose-v2, controlnet-scribble-v2, controlnet-seg-v2, ip-adapter-v2, ip-adapter-faceid-v2, ip-adapter-plus-v2, lora-xl-v2, lycoris-v2, lokr-v2, loha-v2, svd-xt-1.2, animatediff-v4, text2video-zero-v2, zeroscope-v3, modelscope-damo-text-to-video-v2, potat-2, vqgan-clip-v2, clip-vit-huge, siglip-so400m-patch14-384-v2, eva-clip-v2, openclip-vit-G-14, dino-v3, deit-v2, swin-transformer-v2, vit-mae-v2, convnext-v3, resnet-152, efficientnet-v2, mobilenet-v4, rtdetr-v2, mask-rcnn-v2, faster-rcnn-v2, cascade-rcnn-v2, pointrend-v2, mask2former-v2, oneformer-v2, detr-v2, deformable-detr-v2, glip-v2, blip-v2, x-clip-v2, video-llava-v2, macaw-llm-v2, otter-video-v2, panda-70m-v2, internvideo3, vlogger-v2, emotech-v2, hume-ai-evi-v2, replica-ai-v2, character-ai-c2.0, pi-inflection-3, coral-cohere-v2, jasper-chat-v2, copy-ai-chat-v2, writesonic-chatsonic-v2, rytr-chat-v2, anyword-chat-v2, quillbot-flow-v2, grammarly-go-v2, notion-ai-v2, github-copilot-v2, gitlab-duo-v2, cursor-fast-v2, cursor-small-v2, cursor-large-v2, warp-ai-v2, mutable-ai-v2, sweep-ai-v2, bloop-ai-v2, cosine-genie-v2, devin-ai-v2, devika-v2, swe-agent-v2, opendevin-v2, auto-gpt-v2, baby-agi-v2, meta-gpt-v2, chatdev-v2, camel-ai-v2, gpt-engineer-v2, gpt-researcher-v2, private-gpt-v2, local-gpt-v2, h2o-gpt-v2, oobabooga-text-generation-webui-v2, koboldcpp-v2, llama-cpp-python-v2, vllm-v2, text-generation-inference-v2, ollama-v2, lm-studio-v2, gpt4all-v2, jan-ai-v2, faraday-dev-v2, backyard-ai-v2, anything-llm-v2, open-webui-v2, chatbox-v2, msty-v2, pinokio-v2, comfyui-v2, automatic1111-v2, invokeai-v2, fooocus-v2, sd-next-v2, stable-swarm-v2, diffusion-bee-v2, draw-things-v2, guernika-v2, diffusers-v2, transformers-v2, peft-v2, trl-v2, accelerate-v2, deepspeed-v2, megatron-lm-v2, fairseq-v2, fairseq2-v2, espnet-v2, speechbrain-v2, kornia-v2, timm-v2, torchvision-v2, torchaudio-v2, torchtext-v2, tensorrt-llm-v2, onnxruntime-v2, openvino-v2, neural-magic-deepsparse-v2, exllamav2-v2, autoawq-v2, autogptq-v2, bitsandbytes-v2, ggml-v2, gguf-v2, safetensors-v2, pt-v2, bin-v2, ckpt-v3, pth-v2, h5-v2, pb-v2, tflite-v2, coreml-v2, mlpackage-v2, mlmodel-v2, ncnn-v2, mnn-v2, mindspore-v2, paddlepaddle-v2, jax-v2, flax-v2, optax-v2, haiku-v2, trax-v2, equinox-v2, flax-linformer-v2, performer-v2, reformer-v2, routing-transformer-v2, sinkhorn-transformer-v2, sparse-transformer-v2, longformer-v2, linformer-v2, x-transformers-v2, lucidrains-implementations-v2";

        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < 15; i++)
        {
            builder.Append(baseModels).Append("  ");
        }

        var textBlock = new TextBlock
        {
            Text = builder.ToString(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = TryFindResource("TextMutedColor") as Brush ?? Brushes.Gray,
            Opacity = 0.85,
            LineHeight = 16,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(15),
            Content = textBlock,
            IsHitTestVisible = false
        };

        _fullScreenOverlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Topmost = true,
            WindowState = WindowState.Maximized,
            ShowInTaskbar = false
        };

        _fullScreenOverlay.Closed += (_, _) => _fullScreenOverlay = null;
        _fullScreenOverlay.PreviewMouseDown += (s, ev) =>
        {
            _fullScreenOverlay?.Close();
            _fullScreenOverlay = null;
        };
        _fullScreenOverlay.PreviewKeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                _fullScreenOverlay?.Close();
                _fullScreenOverlay = null;
            }
        };

        _fullScreenOverlay.Content = scrollViewer;
        _fullScreenOverlay.Show();
    }
}