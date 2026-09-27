using System.Linq;
using GameRandomiser.Core.Layout;
using Xunit;

namespace GameRandomiser.Core.Tests
{
    public class LayoutTests
    {
        private readonly LabelLayoutEngine engine = new LabelLayoutEngine(new FakeMeasurer());
        private readonly FakeMeasurer measurer = new FakeMeasurer();

        private static LabelRequest Request(string text, int segments, double outer = 180, double inner = 30) => new LabelRequest
        {
            Text = text,
            SweepDegrees = 360.0 / segments,
            InnerRadius = inner,
            OuterRadius = outer,
            MaxFontSize = 18,
            MinFontSize = 7,
            MaxLines = 3
        };

        [Fact]
        public void ShortName_OneLine_MaxFont()
        {
            var layout = engine.Layout(Request("Hades", 8));
            Assert.True(layout.IsVisible);
            Assert.Equal(new[] { "Hades" }, layout.Lines);
            Assert.Equal(18, layout.FontSize, 3);
            Assert.False(layout.IsTruncated);
        }

        [Fact]
        public void LongName_WrapsIntoMultipleLines_WhenThereIsRoom()
        {
            var layout = engine.Layout(Request("Marvel's Guardians of the Galaxy", 3));
            Assert.True(layout.IsVisible);
            Assert.True(layout.Lines.Count > 1);
            Assert.Equal("Marvel's Guardians of the Galaxy", string.Join(" ", layout.Lines));
        }

        [Fact]
        public void Layout_AlwaysFitsRadialLength_AndWedgeThickness()
        {
            var names = new[] { "Hades", "Baldur's Gate 3", "The Legend of Zelda: Tears of the Kingdom", "Marvel's Guardians of the Galaxy", "A" };
            foreach (var segments in new[] { 2, 4, 8, 16, 32, 64, 120 })
            {
                foreach (var name in names)
                {
                    var request = Request(name, segments);
                    var layout = engine.Layout(request);
                    if (!layout.IsVisible)
                    {
                        continue;
                    }

                    var width = layout.Lines.Max(l => measurer.MeasureWidth(l, layout.FontSize));
                    Assert.True(width <= request.OuterRadius - request.InnerRadius + 0.5, $"{name} overflows radially at N={segments}");

                    var textInner = System.Math.Max(request.InnerRadius, request.OuterRadius - width);
                    var thickness = LabelLayoutEngine.ThicknessAt(textInner, request.SweepDegrees);
                    Assert.True(measurer.LineHeight(layout.FontSize) * layout.Lines.Count <= thickness + 0.5,
                        $"{name} overflows wedge at N={segments}");
                    Assert.True(layout.FontSize >= request.MinFontSize - 0.001);
                }
            }
        }

        [Fact]
        public void VeryLongName_InThinSegment_IsTruncatedWithEllipsis()
        {
            var layout = engine.Layout(Request("The Legend of Zelda Tears of the Kingdom Special Collector Edition", 40, outer: 120, inner: 20));
            Assert.True(layout.IsVisible);
            Assert.True(layout.IsTruncated);
            Assert.EndsWith("…", layout.Lines.Single());
        }

        [Fact]
        public void ExtremelyThinSegment_HidesLabel()
        {
            var layout = engine.Layout(Request("Hades", 2000, outer: 100));
            Assert.False(layout.IsVisible);
        }

        [Fact]
        public void EmptyText_IsHidden()
        {
            Assert.False(engine.Layout(Request("   ", 4)).IsVisible);
        }

        [Fact]
        public void BalancedWrap_MinimisesWidestLine()
        {
            var lines = engine.WrapBalanced("aaaa bb cc dddd".Split(' '), 2, 10);
            Assert.Equal(new[] { "aaaa bb", "cc dddd" }, lines);
        }

        [Theory]
        [InlineData(8, 4)]
        [InlineData(9, 4)]
        [InlineData(5, 4)]
        [InlineData(13, 3)]
        public void Palette_NeverRepeatsAcrossSeam(int segments, int palette)
        {
            for (var i = 0; i < segments; i++)
            {
                var current = SegmentPalette.ColourIndexFor(i, segments, palette);
                var next = SegmentPalette.ColourIndexFor((i + 1) % segments, segments, palette);
                Assert.NotEqual(current, next);
            }
        }

        [Fact]
        public void Palette_ParsesHex()
        {
            Assert.True(SegmentPalette.TryParseHex("#FF8000", out var r, out var g, out var b));
            Assert.Equal((255, 128, 0), (r, g, b));
            Assert.True(SegmentPalette.TryParseHex("0f0", out r, out g, out b));
            Assert.Equal((0, 255, 0), (r, g, b));
            Assert.True(SegmentPalette.TryParseHex("#80112233", out r, out g, out b));
            Assert.Equal((0x11, 0x22, 0x33), (r, g, b));
            Assert.False(SegmentPalette.TryParseHex("nope", out _, out _, out _));
            Assert.False(SegmentPalette.TryParseHex(null, out _, out _, out _));
        }

        [Fact]
        public void Palette_ContrastPicksReadableText()
        {
            Assert.True(SegmentPalette.PrefersDarkText(0xEE, 0xB2, 0x11)); // amber -> black text
            Assert.False(SegmentPalette.PrefersDarkText(0x33, 0x69, 0xE8)); // blue -> white text
            Assert.Equal(21, SegmentPalette.ContrastRatio(0, 1), 3);
        }
    }
}
