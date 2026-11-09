using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BatchStudio.App.Services;

/// <summary>The standard Windows colour dialog (same one Excel's "More Colors" uses), without WinForms.</summary>
public static class ColorPicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChooseColorW(ref CHOOSECOLOR cc);

    private const int CC_RGBINIT = 0x1, CC_FULLOPEN = 0x2, CC_ANYCOLOR = 0x100;
    private static readonly int[] Custom = new int[16];

    /// <summary>Returns the picked colour as a VBA colour number (r + g*256 + b*65536), or null when cancelled.</summary>
    public static int? Pick(int? initial)
    {
        var custom = Marshal.AllocCoTaskMem(16 * 4);
        try
        {
            Marshal.Copy(Custom, 0, custom, 16);
            var owner = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = owner,
                rgbResult = initial ?? 0,          // COLORREF = 0x00BBGGRR, the same layout as a VBA colour
                lpCustColors = custom,
                Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
            };
            if (!ChooseColorW(ref cc)) return null;
            Marshal.Copy(custom, Custom, 0, 16);
            return cc.rgbResult & 0xFFFFFF;
        }
        finally { Marshal.FreeCoTaskMem(custom); }
    }
}
