using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Layout;
using GameRandomiser.Core.Models;
using GameRandomiser.Services;

namespace GameRandomiser.UI
{
    /// <summary>Everything the wheel needs to know about how to look.</summary>
    public sealed class WheelAppearance
    {
        public IReadOnlyList<Color> SegmentColours { get; set; } = new[] { Colors.SteelBlue, Colors.IndianRed, Colors.Goldenrod };
        public Color Accent { get; set; } = Colors.DodgerBlue;
        public Color Surface { get; set; } = Color.FromRgb(0x1F, 0x22, 0x28);
        public Color Border { get; set; } = Color.FromRgb(0x38, 0x3D, 0x48);
        public Color Text { get; set; } = Colors.White;
        public GameDisplayMode DisplayMode { get; set; } = GameDisplayMode.Text;
        public bool ShowSegmentBorders { get; set; } = true;
    }

    /// <summary>
    /// The animated wheel. Rendering is split into layers of DrawingVisuals:
    ///   wheel (segments, labels, art, pegs) + highlight  - share one RotateTransform
    ///   chrome (rim and hub)                              - static
    ///   pointer                                           - static apart from a small "kick" on each tick
    /// The wheel layer is rebuilt only when entries, size or appearance change; each animation frame
    /// merely updates the rotation angle. The wheel layer is bitmap-cached so the GPU rotates a texture,
    /// keeping frames cheap even with hundreds of segments and covers.
    /// </summary>
    public sealed class WheelControl : FrameworkElement
    {
        private const double PointerAngle = WheelGeometry.DefaultPointerAngle;
        private static readonly double DefaultCoverAspect = 2.0 / 3.0;

        private readonly VisualCollection visuals;
        private readonly DrawingVisual wheelVisual = new DrawingVisual();
        private readonly DrawingVisual highlightVisual = new DrawingVisual();
        private readonly DrawingVisual chromeVisual = new DrawingVisual();
        private readonly DrawingVisual pointerVisual = new DrawingVisual();
        private readonly RotateTransform wheelRotation = new RotateTransform();
        private readonly RotateTransform pointerKick = new RotateTransform();
        private readonly Stopwatch spinClock = new Stopwatch();

        private IReadOnlyList<GameInfo> entries = new GameInfo[0];
        private WheelAppearance appearance = new WheelAppearance();
        private ImageCache images;
        private SpinPlan plan;
        private double rotation;
        private int lastTickIndex = -1;
        private double kick;
        private double lastFrameSeconds;
        private int? highlightIndex;
        private bool rebuildQueued;
        private int hoverIndex = -1;

        // Geometry of the last build, used for hit testing and highlight drawing.
        private double centreX;
        private double centreY;
        private double radius;

        public WheelControl()
        {
            visuals = new VisualCollection(this) { wheelVisual, highlightVisual, chromeVisual, pointerVisual };
            wheelVisual.Transform = wheelRotation;
            highlightVisual.Transform = wheelRotation;
            pointerVisual.Transform = pointerKick;
            wheelVisual.CacheMode = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = false };
            Cursor = Cursors.Hand;
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 250);
            System.Windows.Automation.AutomationProperties.SetName(this, "Game wheel");
        }

        /// <summary>Raised whenever a segment boundary passes the pointer during a spin.</summary>
        public event EventHandler<int> Tick;

        public event EventHandler<SpinPlan> SpinCompleted;

        /// <summary>The user clicked the wheel (Wheel-of-Names style spin-by-click).</summary>
        public event EventHandler SpinRequested;

        public bool IsSpinning => plan != null;

        public double Rotation => rotation;

        public int EntryCount => entries.Count;

        public void SetImageCache(ImageCache cache) => images = cache;

        public void SetEntries(IReadOnlyList<GameInfo> newEntries)
        {
            if (IsSpinning)
            {
                return; // never re-layout under a moving wheel
            }

            entries = newEntries ?? new GameInfo[0];
            highlightIndex = null;
            System.Windows.Automation.AutomationProperties.SetName(this, $"Game wheel, {entries.Count} games");
            QueueRebuild();
        }

        public void SetAppearance(WheelAppearance newAppearance)
        {
            appearance = newAppearance ?? new WheelAppearance();
            QueueRebuild();
        }

        public void Spin(SpinPlan spinPlan)
        {
            if (spinPlan == null || entries.Count == 0 || IsSpinning)
            {
                return;
            }

            plan = spinPlan;
            highlightIndex = null;
            DrawHighlight();
            lastTickIndex = WheelGeometry.IndexUnderPointer(rotation, entries.Count, PointerAngle);
            lastFrameSeconds = 0;
            spinClock.Restart();
            CompositionTarget.Rendering += OnFrame;
        }

        /// <summary>Jumps straight to the end of the current spin (e.g. view closed mid-spin).</summary>
        public void FinishImmediately()
        {
            if (plan != null)
            {
                Complete();
            }
        }

        public void ClearHighlight()
        {
            highlightIndex = null;
            DrawHighlight();
        }

        protected override int VisualChildrenCount => visuals.Count;

        protected override Visual GetVisualChild(int index) => visuals[index];

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
            var height = double.IsInfinity(availableSize.Height) ? width : availableSize.Height;
            return new Size(width, height);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            QueueRebuild();
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            // Hit-test the wheel disc itself so clicks on empty corners fall through.
            var p = hitTestParameters.HitPoint;
            var dx = p.X - centreX;
            var dy = p.Y - centreY;
            return dx * dx + dy * dy <= radius * radius * 1.1 ? new PointHitTestResult(this, p) : null;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsSpinning && entries.Count > 0)
            {
                SpinRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsSpinning || entries.Count == 0)
            {
                return;
            }

            var p = e.GetPosition(this);
            var screenAngle = Math.Atan2(p.X - centreX, centreY - p.Y) * 180 / Math.PI;
            var index = WheelGeometry.IndexUnderPointer(rotation, entries.Count, screenAngle);
            if (index != hoverIndex)
            {
                hoverIndex = index;
                var game = entries[index];
                ToolTip = game.IsInstalled ? game.Name : game.Name + " (not installed)";
            }
        }

        private void OnFrame(object sender, EventArgs e)
        {
            // An exception escaping a CompositionTarget.Rendering handler would take down Playnite,
            // so any failure here ends the spin cleanly on its planned result instead.
            try
            {
                AdvanceFrame();
            }
            catch (Exception ex)
            {
                Playnite.SDK.LogManager.GetLogger().Error(ex, "Game Randomiser spin animation failed; finishing the spin.");
                try
                {
                    if (plan != null)
                    {
                        Complete();
                    }
                }
                catch (Exception inner)
                {
                    Playnite.SDK.LogManager.GetLogger().Error(inner, "Game Randomiser could not finish the spin.");
                    CompositionTarget.Rendering -= OnFrame;
                    plan = null;
                }
            }
        }

        private void AdvanceFrame()
        {
            if (plan == null)
            {
                CompositionTarget.Rendering -= OnFrame;
                return;
            }

            var elapsed = spinClock.Elapsed.TotalSeconds;
            var dt = Math.Max(0, elapsed - lastFrameSeconds);
            lastFrameSeconds = elapsed;

            if (plan.IsComplete(elapsed))
            {
                Complete();
                return;
            }

            SetRotation(plan.RotationAt(elapsed));

            var index = WheelGeometry.IndexUnderPointer(rotation, entries.Count, PointerAngle);
            if (index != lastTickIndex)
            {
                lastTickIndex = index;
                kick = 1;
                Tick?.Invoke(this, index);
            }

            // Pointer flicks when a peg passes and springs back.
            kick = Math.Max(0, kick - dt * 9);
            pointerKick.Angle = -14 * kick * kick;
        }

        private void Complete()
        {
            CompositionTarget.Rendering -= OnFrame;
            spinClock.Stop();
            var finished = plan;
            plan = null;
            if (finished == null)
            {
                return;
            }

            SetRotation(finished.EndRotation);
            rotation = WheelGeometry.Normalize(rotation); // keep numbers small; visually identical
            wheelRotation.Angle = rotation;
            pointerKick.Angle = 0;
            if (finished.SegmentCount == entries.Count)
            {
                highlightIndex = finished.WinnerIndex;
                DrawHighlight();
            }

            SpinCompleted?.Invoke(this, finished);
        }

        private void SetRotation(double value)
        {
            rotation = value;
            wheelRotation.Angle = value;
        }

        private void QueueRebuild()
        {
            if (rebuildQueued)
            {
                return;
            }

            rebuildQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                rebuildQueued = false;
                Rebuild();
            }), DispatcherPriority.Render);
        }

        private void Rebuild()
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width < 40 || height < 40)
            {
                return;
            }

            // Leave room on the right for the pointer, and a margin all round.
            radius = Math.Max(10, Math.Min(width / 2.2, height / 2.06));
            centreX = width / 2 - radius * 0.06;
            centreY = height / 2;
            wheelRotation.CenterX = centreX;
            wheelRotation.CenterY = centreY;
            wheelRotation.Angle = rotation;
            pointerKick.CenterX = centreX + radius * 1.1;
            pointerKick.CenterY = centreY;

            var scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var wanted = new List<string>();
            int decodeWidth;
            using (var dc = wheelVisual.RenderOpen())
            {
                decodeWidth = DrawWheel(dc, scale, wanted);
            }

            using (var dc = chromeVisual.RenderOpen())
            {
                DrawChrome(dc);
            }

            using (var dc = pointerVisual.RenderOpen())
            {
                DrawPointer(dc);
            }

            DrawHighlight();

            if (images != null && wanted.Count > 0)
            {
                images.Prefetch(wanted, decodeWidth, Dispatcher, QueueRebuild);
            }
        }

        private int DrawWheel(DrawingContext dc, double pixelsPerDip, List<string> wantedImages)
        {
            var n = entries.Count;
            var cx = centreX;
            var cy = centreY;
            var r = radius;

            if (n == 0)
            {
                var emptyPen = new Pen(new SolidColorBrush(appearance.Border), 2) { DashStyle = DashStyles.Dash };
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x18, appearance.Text.R, appearance.Text.G, appearance.Text.B)), emptyPen, new Point(cx, cy), r, r);
                return 96;
            }

            var sweep = WheelGeometry.SegmentSweep(n);
            var hubRadius = HubRadius();
            var fills = appearance.SegmentColours.Select(c => Frozen(new SolidColorBrush(c))).ToList();
            var borderPen = appearance.ShowSegmentBorders && n <= 240
                ? Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Clamp(2.4 - n / 60.0, 0.6, 2.4)))
                : null;

            var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var measurer = new WpfTextMeasurer(typeface, pixelsPerDip);
            var engine = new LabelLayoutEngine(measurer);
            var maxFont = Clamp(r * 0.085, 9, 28);
            var minFont = Clamp(r * 0.034, 7, 10);
            var maxLines = n <= 10 ? 3 : n <= 28 ? 2 : 1;
            var textOuter = r * 0.92;
            var textInner = hubRadius + r * 0.04;

            var mode = appearance.DisplayMode;
            var rimChord = LabelLayoutEngine.ThicknessAt(r, sweep);
            var decodeWidth = 96;
            if (mode == GameDisplayMode.Cover)
            {
                decodeWidth = ImageCache.Bucket(Math.Max(rimChord, (r - hubRadius) * DefaultCoverAspect) * pixelsPerDip);
            }
            else if (mode == GameDisplayMode.SmallCoverAndTitle)
            {
                decodeWidth = ImageCache.Bucket(r * 0.22 * pixelsPerDip);
            }
            else if (mode == GameDisplayMode.IconAndTitle)
            {
                decodeWidth = ImageCache.Bucket(r * 0.16 * pixelsPerDip);
            }

            for (var i = 0; i < n; i++)
            {
                var game = entries[i];
                var start = i * sweep;
                var centreAngle = start + sweep / 2;
                var colour = appearance.SegmentColours[SegmentPalette.ColourIndexFor(i, n, appearance.SegmentColours.Count)];
                var fill = fills[SegmentPalette.ColourIndexFor(i, n, fills.Count)];
                var wedge = n == 1 ? (Geometry)new EllipseGeometry(new Point(cx, cy), r, r) : Wedge(cx, cy, r, start, start + sweep);
                dc.DrawGeometry(fill, null, wedge);

                var textBrush = SegmentPalette.PrefersDarkText(colour.R, colour.G, colour.B)
                    ? Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)))
                    : Brushes.White;
                var labelOuter = textOuter;
                var drawLabel = true;

                switch (mode)
                {
                    case GameDisplayMode.Cover:
                        if (rimChord >= 14 && n <= 120)
                        {
                            var cover = LookupImage(game.CoverPath, decodeWidth, wantedImages);
                            if (cover != null)
                            {
                                DrawCoverFill(dc, wedge, cover, centreAngle, sweep, hubRadius);
                                drawLabel = false;
                            }
                        }

                        break;
                    case GameDisplayMode.SmallCoverAndTitle:
                    {
                        var thumbWidth = Math.Min(LabelLayoutEngine.ThicknessAt(r * 0.78, sweep) * 0.8, r * 0.2);
                        if (thumbWidth >= 12)
                        {
                            var cover = LookupImage(game.CoverPath, decodeWidth, wantedImages);
                            var aspect = cover != null && cover.PixelHeight > 0 ? cover.PixelWidth / (double)cover.PixelHeight : DefaultCoverAspect;
                            var thumbHeight = Math.Min(thumbWidth / aspect, r * 0.3);
                            thumbWidth = thumbHeight * aspect;
                            DrawRadialImage(dc, cover, centreAngle, r * 0.95, thumbWidth, thumbHeight);
                            labelOuter = r * 0.95 - thumbHeight - r * 0.035;
                        }

                        break;
                    }

                    case GameDisplayMode.IconAndTitle:
                    {
                        var size = Math.Min(LabelLayoutEngine.ThicknessAt(r * 0.82, sweep) * 0.72, r * 0.15);
                        if (size >= 10)
                        {
                            var icon = LookupImage(game.IconPath, decodeWidth, wantedImages);
                            DrawUprightIcon(dc, icon, centreAngle, r * 0.94, size);
                            labelOuter = r * 0.94 - size - r * 0.03;
                        }

                        break;
                    }
                }

                if (borderPen != null && n > 1)
                {
                    dc.DrawGeometry(null, borderPen, wedge);
                }

                if (drawLabel && labelOuter - textInner > 8)
                {
                    var layout = engine.Layout(new LabelRequest
                    {
                        Text = game.Name,
                        SweepDegrees = sweep,
                        InnerRadius = textInner,
                        OuterRadius = labelOuter,
                        MaxFontSize = maxFont,
                        MinFontSize = minFont,
                        MaxLines = maxLines
                    });
                    DrawLabel(dc, layout, typeface, textBrush, centreAngle, labelOuter, pixelsPerDip);
                }
            }

            // Pegs on the rim at each boundary: what the pointer "ticks" against.
            if (n > 1 && n <= 72)
            {
                var pegBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)));
                var pegRadius = Clamp(r * 0.016, 1.5, 5);
                for (var i = 0; i < n; i++)
                {
                    dc.DrawEllipse(pegBrush, null, PointAt(cx, cy, r * 0.965, i * sweep), pegRadius, pegRadius);
                }
            }

            return decodeWidth;
        }

        private BitmapSource LookupImage(string path, int decodeWidth, List<string> wanted)
        {
            if (string.IsNullOrEmpty(path) || images == null)
            {
                return null;
            }

            var image = images.TryGet(path, decodeWidth);
            if (image == null)
            {
                wanted.Add(path);
            }

            return image;
        }

        /// <summary>Fills a wedge with cover art, top of the art facing the rim.</summary>
        private void DrawCoverFill(DrawingContext dc, Geometry wedge, BitmapSource cover, double centreAngle, double sweep, double hubRadius)
        {
            var r = radius;
            var aspect = cover.PixelHeight > 0 ? cover.PixelWidth / (double)cover.PixelHeight : DefaultCoverAspect;
            var radialLength = r - hubRadius;
            var tangential = sweep >= 180 ? 2 * r : LabelLayoutEngine.ThicknessAt(r, sweep);
            var height = Math.Max(radialLength, tangential / aspect);
            var width = height * aspect;

            dc.PushClip(wedge);
            dc.PushTransform(new RotateTransform(centreAngle - 90, centreX, centreY));
            var pivot = new Point(centreX + r - height / 2, centreY);
            dc.PushTransform(new RotateTransform(90, pivot.X, pivot.Y));
            dc.DrawImage(cover, new Rect(pivot.X - width / 2, pivot.Y - height / 2, width, height));
            dc.Pop();

            // Soft shade toward the hub so the pointer area and borders stay readable.
            var shade = new LinearGradientBrush(Color.FromArgb(0x00, 0, 0, 0), Color.FromArgb(0x55, 0, 0, 0), new Point(1, 0.5), new Point(0, 0.5));
            dc.DrawRectangle(shade, null, new Rect(centreX, centreY - r, r, 2 * r));
            dc.Pop();
            dc.Pop();
        }

        /// <summary>Draws a portrait image with its top facing outward, ending at <paramref name="outerRadius"/>.</summary>
        private void DrawRadialImage(DrawingContext dc, BitmapSource image, double centreAngle, double outerRadius, double width, double height)
        {
            dc.PushTransform(new RotateTransform(centreAngle - 90, centreX, centreY));
            var pivot = new Point(centreX + outerRadius - height / 2, centreY);
            dc.PushTransform(new RotateTransform(90, pivot.X, pivot.Y));
            var rect = new Rect(pivot.X - width / 2, pivot.Y - height / 2, width, height);
            if (image != null)
            {
                dc.PushClip(new RectangleGeometry(rect, 3, 3));
                dc.DrawImage(image, rect);
                dc.Pop();
            }
            else
            {
                DrawPlaceholder(dc, rect);
            }

            dc.Pop();
            dc.Pop();
        }

        /// <summary>Draws a square icon in the label's reading orientation, ending at <paramref name="outerRadius"/>.</summary>
        private void DrawUprightIcon(DrawingContext dc, BitmapSource icon, double centreAngle, double outerRadius, double size)
        {
            dc.PushTransform(new RotateTransform(centreAngle - 90, centreX, centreY));
            var rect = new Rect(centreX + outerRadius - size, centreY - size / 2, size, size);
            if (icon != null)
            {
                dc.DrawImage(icon, rect);
            }
            else
            {
                DrawPlaceholder(dc, rect);
            }

            dc.Pop();
        }

        private static void DrawPlaceholder(DrawingContext dc, Rect rect)
        {
            var fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1);
            dc.DrawRoundedRectangle(fill, pen, rect, 3, 3);
            // Simple controller-ish glyph: two dots.
            var r = Math.Min(rect.Width, rect.Height) * 0.1;
            dc.DrawEllipse(pen.Brush, null, new Point(rect.Left + rect.Width * 0.35, rect.Top + rect.Height / 2), r, r);
            dc.DrawEllipse(pen.Brush, null, new Point(rect.Left + rect.Width * 0.65, rect.Top + rect.Height / 2), r, r);
        }

        private void DrawLabel(DrawingContext dc, LabelLayout layout, Typeface typeface, Brush brush, double centreAngle, double outerRadius, double pixelsPerDip)
        {
            if (!layout.IsVisible)
            {
                return;
            }

            dc.PushTransform(new RotateTransform(centreAngle - 90, centreX, centreY));
            var lineHeight = typeface.FontFamily.LineSpacing * layout.FontSize;
            var top = centreY - lineHeight * layout.Lines.Count / 2;
            for (var i = 0; i < layout.Lines.Count; i++)
            {
                var text = new FormattedText(layout.Lines[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, layout.FontSize, brush, pixelsPerDip);
                var x = centreX + outerRadius - text.WidthIncludingTrailingWhitespace;
                dc.DrawText(text, new Point(x, top + i * lineHeight));
            }

            dc.Pop();
        }

        private void DrawChrome(DrawingContext dc)
        {
            var r = radius;
            var rimWidth = Clamp(r * 0.035, 3, 12);
            var rimPen = Frozen(new Pen(new SolidColorBrush(appearance.Surface), rimWidth));
            var outline = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 1));
            dc.DrawEllipse(null, rimPen, new Point(centreX, centreY), r + rimWidth / 2 - 0.5, r + rimWidth / 2 - 0.5);
            dc.DrawEllipse(null, outline, new Point(centreX, centreY), r + rimWidth, r + rimWidth);

            if (entries.Count == 0)
            {
                return;
            }

            var hub = HubRadius();
            dc.DrawEllipse(new SolidColorBrush(appearance.Surface), Frozen(new Pen(new SolidColorBrush(appearance.Accent), Clamp(hub * 0.14, 2, 5))), new Point(centreX, centreY), hub, hub);
            dc.DrawEllipse(new SolidColorBrush(appearance.Accent), null, new Point(centreX, centreY), hub * 0.32, hub * 0.32);
        }

        private void DrawPointer(DrawingContext dc)
        {
            var r = radius;
            var tipX = centreX + r * 0.9;
            var baseX = centreX + r * 1.1;
            var halfHeight = Clamp(r * 0.075, 7, 26);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(tipX, centreY), true, true);
                ctx.LineTo(new Point(baseX, centreY - halfHeight), true, true);
                ctx.QuadraticBezierTo(new Point(baseX + halfHeight * 0.35, centreY), new Point(baseX, centreY + halfHeight), true, true);
            }

            geometry.Freeze();
            var shadow = geometry.Clone();
            shadow.Transform = new TranslateTransform(1.5, 2);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), null, shadow);
            dc.DrawGeometry(new SolidColorBrush(appearance.Accent), Frozen(new Pen(Brushes.White, Clamp(r * 0.012, 1.5, 3))), geometry);
            dc.DrawEllipse(Brushes.White, null, new Point(baseX - halfHeight * 0.1, centreY), halfHeight * 0.22, halfHeight * 0.22);
        }

        private void DrawHighlight()
        {
            using (var dc = highlightVisual.RenderOpen())
            {
                var n = entries.Count;
                if (!highlightIndex.HasValue || n < 2 || highlightIndex.Value >= n || radius <= 0)
                {
                    return;
                }

                var sweep = WheelGeometry.SegmentSweep(n);
                var start = highlightIndex.Value * sweep;
                var wedge = Wedge(centreX, centreY, radius, start, start + sweep);
                var disc = new EllipseGeometry(new Point(centreX, centreY), radius, radius);
                var dimmed = new CombinedGeometry(GeometryCombineMode.Exclude, disc, wedge);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), null, dimmed);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(appearance.Accent), Clamp(radius * 0.02, 2.5, 6)) { LineJoin = PenLineJoin.Round }, wedge);
                dc.DrawGeometry(null, new Pen(Brushes.White, 1.2) { LineJoin = PenLineJoin.Round }, wedge);
            }
        }

        private double HubRadius() => Clamp(radius * 0.12, 12, 60);

        private static Geometry Wedge(double cx, double cy, double r, double startDeg, double endDeg)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(cx, cy), true, true);
                ctx.LineTo(PointAt(cx, cy, r, startDeg), true, true);
                ctx.ArcTo(PointAt(cx, cy, r, endDeg), new Size(r, r), 0, endDeg - startDeg > 180, SweepDirection.Clockwise, true, true);
            }

            geometry.Freeze();
            return geometry;
        }

        /// <summary>Point at an angle measured clockwise from 12 o'clock.</summary>
        private static Point PointAt(double cx, double cy, double r, double degrees)
        {
            var rad = degrees * Math.PI / 180;
            return new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
        }

        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        /// <summary>WPF text measurement for the Core label engine. Width scales linearly, so each string is measured once.</summary>
        private sealed class WpfTextMeasurer : ITextMeasurer
        {
            private const double ReferenceSize = 100;
            private readonly Typeface typeface;
            private readonly double pixelsPerDip;
            private readonly Dictionary<string, double> widths = new Dictionary<string, double>();

            public WpfTextMeasurer(Typeface typeface, double pixelsPerDip)
            {
                this.typeface = typeface;
                this.pixelsPerDip = pixelsPerDip;
            }

            public double MeasureWidth(string text, double fontSize)
            {
                if (!widths.TryGetValue(text, out var width))
                {
                    width = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, ReferenceSize, Brushes.Black, pixelsPerDip)
                        .WidthIncludingTrailingWhitespace;
                    widths[text] = width;
                }

                return width * fontSize / ReferenceSize;
            }

            public double LineHeight(double fontSize) => typeface.FontFamily.LineSpacing * fontSize;
        }
    }
}
