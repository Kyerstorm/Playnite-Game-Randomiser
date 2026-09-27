using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace GameRandomiser.UI
{
    /// <summary>
    /// Lightweight, non-interactive confetti burst drawn directly in OnRender. Runs for a couple of
    /// seconds after a win, then unhooks from the render loop entirely.
    /// </summary>
    public sealed class ConfettiLayer : FrameworkElement
    {
        private const double Lifetime = 2.6;
        private const double Gravity = 900;
        private readonly List<Particle> particles = new List<Particle>();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Random random = new Random();
        private double lastSeconds;
        private bool running;

        public ConfettiLayer()
        {
            IsHitTestVisible = false;
        }

        public void Burst(IReadOnlyList<Color> colours, Point origin)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0 || colours == null || colours.Count == 0)
            {
                return;
            }

            particles.Clear();
            var count = (int)Math.Min(160, Math.Max(60, ActualWidth / 5));
            for (var i = 0; i < count; i++)
            {
                var angle = (-90 + (random.NextDouble() - 0.5) * 140) * Math.PI / 180;
                var speed = 380 + random.NextDouble() * 520;
                var brush = new SolidColorBrush(colours[random.Next(colours.Count)]);
                brush.Freeze();
                particles.Add(new Particle
                {
                    X = origin.X + (random.NextDouble() - 0.5) * 40,
                    Y = origin.Y,
                    VX = Math.Cos(angle) * speed,
                    VY = Math.Sin(angle) * speed,
                    Rotation = random.NextDouble() * 360,
                    Spin = (random.NextDouble() - 0.5) * 720,
                    Width = 6 + random.NextDouble() * 6,
                    Height = 3 + random.NextDouble() * 4,
                    Brush = brush
                });
            }

            lastSeconds = 0;
            clock.Restart();
            if (!running)
            {
                running = true;
                CompositionTarget.Rendering += OnFrame;
            }
        }

        public void Stop()
        {
            particles.Clear();
            if (running)
            {
                running = false;
                CompositionTarget.Rendering -= OnFrame;
            }

            InvalidateVisual();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = Math.Min(0.05, now - lastSeconds);
            lastSeconds = now;
            if (now > Lifetime)
            {
                Stop();
                return;
            }

            foreach (var p in particles)
            {
                p.VY += Gravity * dt;
                p.VX *= 1 - 0.9 * dt; // air drag
                p.X += p.VX * dt;
                p.Y += p.VY * dt;
                p.Rotation += p.Spin * dt;
            }

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (particles.Count == 0)
            {
                return;
            }

            var fade = Math.Max(0, Math.Min(1, (Lifetime - lastSeconds) / 0.6));
            dc.PushOpacity(fade);
            foreach (var p in particles)
            {
                // Flip the width with a sine to fake 3D tumbling.
                var w = p.Width * Math.Abs(Math.Cos(p.Rotation * Math.PI / 90));
                dc.PushTransform(new RotateTransform(p.Rotation, p.X, p.Y));
                dc.DrawRectangle(p.Brush, null, new Rect(p.X - w / 2, p.Y - p.Height / 2, Math.Max(1, w), p.Height));
                dc.Pop();
            }

            dc.Pop();
        }

        private sealed class Particle
        {
            public double X, Y, VX, VY, Rotation, Spin, Width, Height;
            public Brush Brush;
        }
    }
}
