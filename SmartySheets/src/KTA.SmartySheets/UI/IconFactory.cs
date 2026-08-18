using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KTA.SmartySheets.Core;

namespace KTA.SmartySheets.UI
{
    /// <summary>
    /// Draws the ribbon icon at runtime rather than shipping PNG resources. A sheet with a
    /// tick: two shapes, no binary assets to keep in step with the build.
    /// </summary>
    internal static class IconFactory
    {
        public static ImageSource Create(int size)
        {
            try
            {
                var visual = new DrawingVisual();

                using (var dc = visual.RenderOpen())
                {
                    var inset = size * 0.14;
                    var page = new Rect(inset, inset * 0.6, size - inset * 2, size - inset * 1.2);

                    dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x45)), Math.Max(1, size / 16.0)), page);

                    var rule = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB0)), Math.Max(1, size / 22.0));
                    for (var i = 1; i <= 3; i++)
                    {
                        var y = page.Top + page.Height * i / 5.0;
                        dc.DrawLine(rule, new Point(page.Left + page.Width * 0.14, y), new Point(page.Right - page.Width * 0.18, y));
                    }

                    var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x9D, 0x55)), Math.Max(1.5, size / 8.0))
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round
                    };

                    dc.DrawLine(tick, new Point(size * 0.36, size * 0.62), new Point(size * 0.50, size * 0.78));
                    dc.DrawLine(tick, new Point(size * 0.50, size * 0.78), new Point(size * 0.82, size * 0.30));
                }

                var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                // A text-only ribbon button is perfectly usable. Never fail startup for an icon.
                Log.Instance.Warn("Ribbon icon could not be drawn: " + ex.Message);
                return null;
            }
        }
    }
}
