using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Playnite.SDK;

namespace GameRandomiser.Services
{
    /// <summary>
    /// Wheel tick and winner sounds. Playnite does not expose its sound engine to extensions, so this
    /// uses the lightweight Windows PlaySound API with sounds synthesised in memory (no asset files,
    /// no extra dependencies). Volume is baked into the samples; each new tick cuts off the previous one,
    /// which keeps rapid ticking crisp instead of piling up.
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        private const int SampleRate = 22050;
        private const uint SndAsync = 0x0001;
        private const uint SndNoDefault = 0x0002;
        private const uint SndMemory = 0x0004;

        /// <summary>Minimum gap between ticks so very fast spins sound like a rattle, not a buzz.</summary>
        private static readonly TimeSpan MinTickInterval = TimeSpan.FromMilliseconds(38);

        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object sync = new object();
        private GCHandle tickHandle;
        private GCHandle winnerHandle;
        private int builtVolume = -1;
        private TimeSpan? lastTick;
        private bool broken;

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        public bool TickEnabled { get; set; } = true;
        public bool WinnerEnabled { get; set; } = true;

        /// <summary>0..100</summary>
        public int Volume { get; set; } = 50;

        public void PlayTick()
        {
            if (!TickEnabled || Volume <= 0 || broken)
            {
                return;
            }

            var now = clock.Elapsed;
            if (lastTick.HasValue && now - lastTick.Value < MinTickInterval)
            {
                return;
            }

            lastTick = now;
            Play(isWinner: false);
        }

        public void PlayWinner()
        {
            if (!WinnerEnabled || Volume <= 0 || broken)
            {
                return;
            }

            Play(isWinner: true);
        }

        private void Play(bool isWinner)
        {
            try
            {
                lock (sync)
                {
                    EnsureBuffers();
                    var handle = isWinner ? winnerHandle : tickHandle;
                    PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
                }
            }
            catch (Exception e)
            {
                // No audio device, missing winmm, etc. Sound is optional: disable quietly.
                broken = true;
                Logger.Warn(e, "Game Randomiser audio disabled.");
            }
        }

        private void EnsureBuffers()
        {
            if (builtVolume == Volume && tickHandle.IsAllocated)
            {
                return;
            }

            // Stop anything playing from the old buffers before releasing them.
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            Free();
            var gain = Math.Pow(Volume / 100.0, 1.6); // perceptual curve
            tickHandle = GCHandle.Alloc(BuildWav(Tick(gain)), GCHandleType.Pinned);
            winnerHandle = GCHandle.Alloc(BuildWav(Fanfare(gain)), GCHandleType.Pinned);
            builtVolume = Volume;
        }

        /// <summary>A short woody click: filtered noise burst plus a damped 1.9 kHz ping.</summary>
        private static short[] Tick(double gain)
        {
            var length = (int)(SampleRate * 0.028);
            var samples = new short[length];
            var random = new Random(7);
            double lowPass = 0;
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)SampleRate;
                var envelope = Math.Exp(-t / 0.0035);
                lowPass += 0.35 * ((random.NextDouble() * 2 - 1) - lowPass);
                var ping = Math.Sin(2 * Math.PI * 1900 * t) * Math.Exp(-t / 0.006);
                var value = (0.55 * lowPass * envelope + 0.45 * ping) * gain * 0.55;
                samples[i] = (short)(Math.Max(-1, Math.Min(1, value)) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>A bright rising arpeggio (C5 E5 G5 C6) with a soft tail.</summary>
        private static short[] Fanfare(double gain)
        {
            var notes = new[] { 523.25, 659.25, 783.99, 1046.5 };
            const double NoteGap = 0.085;
            var length = (int)(SampleRate * (NoteGap * notes.Length + 0.55));
            var samples = new short[length];
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)SampleRate;
                double value = 0;
                for (var n = 0; n < notes.Length; n++)
                {
                    var start = n * NoteGap;
                    if (t < start)
                    {
                        continue;
                    }

                    var local = t - start;
                    var decay = n == notes.Length - 1 ? 0.28 : 0.12;
                    var attack = Math.Min(1, local / 0.004);
                    var tone = Math.Sin(2 * Math.PI * notes[n] * local) + 0.3 * Math.Sin(4 * Math.PI * notes[n] * local);
                    value += tone * attack * Math.Exp(-local / decay);
                }

                value *= 0.28 * gain;
                samples[i] = (short)(Math.Max(-1, Math.Min(1, value)) * short.MaxValue);
            }

            return samples;
        }

        private static byte[] BuildWav(short[] samples)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                var dataBytes = samples.Length * 2;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                writer.Write(16);             // fmt chunk size
                writer.Write((short)1);       // PCM
                writer.Write((short)1);       // mono
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2); // byte rate
                writer.Write((short)2);       // block align
                writer.Write((short)16);      // bits per sample
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);
                foreach (var s in samples)
                {
                    writer.Write(s);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private void Free()
        {
            if (tickHandle.IsAllocated)
            {
                tickHandle.Free();
            }

            if (winnerHandle.IsAllocated)
            {
                winnerHandle.Free();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                try
                {
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                }
                catch (Exception)
                {
                    // Ignore: shutting down.
                }

                Free();
            }
        }
    }
}
