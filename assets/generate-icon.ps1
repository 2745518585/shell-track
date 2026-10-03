#Requires -Version 7.0
# Original Shell Track artwork; licensed with the repository's MIT license.
# Render the SVG's geometry with System.Drawing, then write a PNG-compressed,
# multi-resolution ICO. No downloaded tools or fonts are needed on Windows.
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Icon generation requires Windows System.Drawing.' }
Add-Type -AssemblyName System.Drawing
if (-not ('ShellTrackArtwork.Renderer' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Runtime -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ShellTrackArtwork {
    public static class Renderer {
        static Color ColorOf(string value) => ColorTranslator.FromHtml(value);
        static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius) {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(x, y, diameter, diameter, 180, 90);
            path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
            path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
            path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        static Pen Stroke(string color, float width) => new Pen(ColorOf(color), width) {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        public static byte[] Png(int size) {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)) {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.ScaleTransform(size / 256f, size / 256f);
                using var panel = RoundedRectangle(20, 20, 216, 216, 44);
                using var gradient = new LinearGradientBrush(new PointF(0, 20), new PointF(0, 236), ColorOf("#1c3044"), ColorOf("#101d2d"));
                using var border = Stroke("#33475e", 4);
                graphics.FillPath(gradient, panel); graphics.DrawPath(border, panel);
                using var separator = Stroke("#33475e", 3);
                graphics.DrawLine(separator, 48, 67, 208, 67);
                using var muted = new SolidBrush(ColorOf("#8296ac"));
                foreach (float x in new float[] { 52, 67, 82 }) graphics.FillEllipse(muted, x - 4, 42, 8, 8);
                using var cyan = Stroke("#67e8f9", 13);
                graphics.DrawLines(cyan, new PointF[] { new PointF(57, 98), new PointF(85, 125), new PointF(57, 152) });
                graphics.DrawLine(cyan, 104, 152, 139, 152);
                using var rail = Stroke("#465e76", 7);
                graphics.DrawLine(rail, 184, 100, 184, 177);
                graphics.FillEllipse(muted, 175, 91, 18, 18);
                graphics.FillEllipse(muted, 175, 127, 18, 18);
                using var green = new SolidBrush(ColorOf("#8ce3ac"));
                graphics.FillEllipse(green, 168, 161, 32, 32);
                using var check = Stroke("#101d2d", 4);
                graphics.DrawLines(check, new PointF[] { new PointF(177, 177), new PointF(182, 182), new PointF(191, 172) });
            }
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
    }
}
'@
}
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$iconFrames = @($iconSizes | ForEach-Object { ,([ShellTrackArtwork.Renderer]::Png($_)) })
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'shelltrack.png'), $iconFrames[-1])
$iconStream = [IO.File]::Create((Join-Path $PSScriptRoot 'shelltrack.ico'))
$iconWriter = [IO.BinaryWriter]::new($iconStream)
try {
    $iconWriter.Write([uint16]0)
    $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$iconSizes.Length)
    $iconOffset = 6 + 16 * $iconSizes.Length
    for ($iconIndex = 0; $iconIndex -lt $iconSizes.Length; $iconIndex++) {
        $iconSizeByte = if ($iconSizes[$iconIndex] -eq 256) { 0 } else { $iconSizes[$iconIndex] }
        $iconWriter.Write([byte]$iconSizeByte)
        $iconWriter.Write([byte]$iconSizeByte)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$iconFrames[$iconIndex].Length)
        $iconWriter.Write([uint32]$iconOffset)
        $iconOffset += $iconFrames[$iconIndex].Length
    }
    foreach ($iconFrame in $iconFrames) { $iconWriter.Write([byte[]]$iconFrame) }
} finally { $iconWriter.Dispose() }
Write-Host 'Updated assets/shelltrack.png and assets/shelltrack.ico (16–256 px).'
