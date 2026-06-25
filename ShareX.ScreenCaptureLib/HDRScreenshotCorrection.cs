#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib
{
    internal static class HDRScreenshotCorrection
    {
        private const int ERROR_SUCCESS = 0;
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;
        private const int CCHDEVICENAME = 32;

        public static Bitmap ApplyIfNeeded(Bitmap bitmap, Rectangle captureRectangle)
        {
            if (bitmap == null)
            {
                return null;
            }

            List<CorrectionRegion> correctionRegions = GetCorrectionRegions(captureRectangle);

            if (correctionRegions.Count == 0)
            {
                return bitmap;
            }

            Bitmap correctedBitmap = EnsureSupportedPixelFormat(bitmap);
            ApplyCorrectionRegions(correctedBitmap, correctionRegions);

            if (!ReferenceEquals(correctedBitmap, bitmap))
            {
                bitmap.Dispose();
            }

            return correctedBitmap;
        }

        private static List<CorrectionRegion> GetCorrectionRegions(Rectangle captureRectangle)
        {
            List<CorrectionRegion> correctionRegions = new List<CorrectionRegion>();

            if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
            {
                return correctionRegions;
            }

            try
            {
                Dictionary<string, float> displayScales = GetHdrDisplayCorrectionScales();

                if (displayScales.Count == 0)
                {
                    return correctionRegions;
                }

                foreach (Screen screen in Screen.AllScreens)
                {
                    if (!displayScales.TryGetValue(screen.DeviceName, out float scale))
                    {
                        continue;
                    }

                    Rectangle intersection = Rectangle.Intersect(captureRectangle, screen.Bounds);

                    if (intersection.Width <= 0 || intersection.Height <= 0)
                    {
                        continue;
                    }

                    Rectangle bitmapRectangle = new Rectangle(
                        intersection.X - captureRectangle.X,
                        intersection.Y - captureRectangle.Y,
                        intersection.Width,
                        intersection.Height);

                    correctionRegions.Add(new CorrectionRegion(bitmapRectangle, scale));
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR screenshot correction failed.");
            }

            return correctionRegions;
        }

        private static Dictionary<string, float> GetHdrDisplayCorrectionScales()
        {
            Dictionary<string, float> displayScales = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);

            if (result != ERROR_SUCCESS || pathCount == 0)
            {
                return displayScales;
            }

            DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (result != ERROR_SUCCESS)
            {
                return displayScales;
            }

            for (int i = 0; i < pathCount; i++)
            {
                DISPLAYCONFIG_PATH_INFO path = paths[i];

                string displayName = GetSourceDisplayName(path.sourceInfo.adapterId, path.sourceInfo.id);

                if (string.IsNullOrEmpty(displayName))
                {
                    continue;
                }

                if (!IsAdvancedColorEnabled(path.targetInfo.adapterId, path.targetInfo.id))
                {
                    continue;
                }

                float correctionScale = GetSdrWhiteLevelCorrectionScale(path.targetInfo.adapterId, path.targetInfo.id);

                if (correctionScale < 0.999f)
                {
                    displayScales[displayName] = correctionScale;
                }
            }

            return displayScales;
        }

        private static string GetSourceDisplayName(LUID adapterId, uint sourceId)
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = CreateDeviceInfoHeader(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId, sourceId)
            };

            int result = DisplayConfigGetSourceDeviceName(ref sourceName);

            return result == ERROR_SUCCESS ? sourceName.viewGdiDeviceName : null;
        }

        private static bool IsAdvancedColorEnabled(LUID adapterId, uint targetId)
        {
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO advancedColorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = CreateDeviceInfoHeader(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(), adapterId, targetId)
            };

            int result = DisplayConfigGetAdvancedColorInfo(ref advancedColorInfo);

            return result == ERROR_SUCCESS && (advancedColorInfo.value & 0x2) != 0;
        }

        private static float GetSdrWhiteLevelCorrectionScale(LUID adapterId, uint targetId)
        {
            DISPLAYCONFIG_SDR_WHITE_LEVEL sdrWhiteLevel = new DISPLAYCONFIG_SDR_WHITE_LEVEL
            {
                header = CreateDeviceInfoHeader(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(), adapterId, targetId)
            };

            int result = DisplayConfigGetSdrWhiteLevel(ref sdrWhiteLevel);

            if (result != ERROR_SUCCESS || sdrWhiteLevel.SDRWhiteLevel <= 1000)
            {
                return 1.0f;
            }

            float correctionScale = 1000.0f / sdrWhiteLevel.SDRWhiteLevel;

            return Math.Max(0.1f, Math.Min(1.0f, correctionScale));
        }

        private static DISPLAYCONFIG_DEVICE_INFO_HEADER CreateDeviceInfoHeader(int type, int size, LUID adapterId, uint id)
        {
            return new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = type,
                size = (uint)size,
                adapterId = adapterId,
                id = id
            };
        }

        private static Bitmap EnsureSupportedPixelFormat(Bitmap bitmap)
        {
            if (bitmap.PixelFormat == PixelFormat.Format32bppArgb ||
                bitmap.PixelFormat == PixelFormat.Format32bppPArgb ||
                bitmap.PixelFormat == PixelFormat.Format32bppRgb)
            {
                return bitmap;
            }

            Bitmap convertedBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(convertedBitmap))
            {
                graphics.DrawImageUnscaled(bitmap, Point.Empty);
            }

            return convertedBitmap;
        }

        private static void ApplyCorrectionRegions(Bitmap bitmap, List<CorrectionRegion> correctionRegions)
        {
            Rectangle bitmapBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData bitmapData = bitmap.LockBits(bitmapBounds, ImageLockMode.ReadWrite, bitmap.PixelFormat);

            try
            {
                foreach (CorrectionRegion correctionRegion in correctionRegions)
                {
                    Rectangle region = Rectangle.Intersect(bitmapBounds, correctionRegion.Rectangle);

                    if (region.Width <= 0 || region.Height <= 0)
                    {
                        continue;
                    }

                    byte[] lookupTable = CreateSrgbCorrectionLookupTable(correctionRegion.Scale);
                    ApplyCorrectionRegion(bitmapData, region, lookupTable);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        private static void ApplyCorrectionRegion(BitmapData bitmapData, Rectangle region, byte[] lookupTable)
        {
            int bytesPerPixel = Image.GetPixelFormatSize(bitmapData.PixelFormat) / 8;

            if (bytesPerPixel < 3)
            {
                return;
            }

            int rowBytes = region.Width * bytesPerPixel;
            byte[] row = new byte[rowBytes];

            for (int y = region.Top; y < region.Bottom; y++)
            {
                IntPtr rowPointer = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride + region.Left * bytesPerPixel);
                Marshal.Copy(rowPointer, row, 0, rowBytes);

                for (int x = 0; x < rowBytes; x += bytesPerPixel)
                {
                    row[x] = lookupTable[row[x]];
                    row[x + 1] = lookupTable[row[x + 1]];
                    row[x + 2] = lookupTable[row[x + 2]];
                }

                Marshal.Copy(row, 0, rowPointer, rowBytes);
            }
        }

        private static byte[] CreateSrgbCorrectionLookupTable(float scale)
        {
            byte[] lookupTable = new byte[256];

            for (int i = 0; i < lookupTable.Length; i++)
            {
                lookupTable[i] = ScaleSrgb(i, scale);
            }

            return lookupTable;
        }

        private static byte ScaleSrgb(int value, float scale)
        {
            double srgb = value / 255.0;
            double linear = srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
            linear *= scale;

            double corrected = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            int result = (int)Math.Round(corrected * 255.0);

            return (byte)Math.Max(0, Math.Min(255, result));
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetSourceDeviceName(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetAdvancedColorInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetSdrWhiteLevel(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

        private sealed class CorrectionRegion
        {
            public Rectangle Rectangle { get; private set; }
            public float Scale { get; private set; }

            public CorrectionRegion(Rectangle rectangle, float scale)
            {
                Rectangle = rectangle;
                Scale = scale;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECTL
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public int scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public int pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize;
            public RECTL DesktopImageRegion;
            public RECTL DesktopImageClip;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)]
            public DISPLAYCONFIG_TARGET_MODE targetMode;

            [FieldOffset(0)]
            public DISPLAYCONFIG_SOURCE_MODE sourceMode;

            [FieldOffset(0)]
            public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public int infoType;
            public uint id;
            public LUID adapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public int scanLineOrdering;

            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;

            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
        }
    }
}
