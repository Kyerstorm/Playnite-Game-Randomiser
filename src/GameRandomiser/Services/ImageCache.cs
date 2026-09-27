using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;

namespace GameRandomiser.Services
{
    /// <summary>
    /// Decodes cover/icon art at the size it is actually drawn, off the UI thread, and keeps the
    /// frozen bitmaps in memory. The wheel never loads images during animation: it asks for what is
    /// cached, draws placeholders for the rest, and redraws once when a background batch completes.
    /// </summary>
    public sealed class ImageCache
    {
        private const int MaxEntries = 1500;
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly ConcurrentDictionary<string, BitmapSource> cache = new ConcurrentDictionary<string, BitmapSource>();
        private readonly ConcurrentDictionary<string, bool> failed = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, bool> pending = new ConcurrentDictionary<string, bool>();

        /// <summary>Decode widths are bucketed so resizing the sidebar doesn't thrash the cache.</summary>
        public static int Bucket(double pixels)
        {
            if (pixels <= 48) return 48;
            if (pixels <= 96) return 96;
            if (pixels <= 192) return 192;
            return 400;
        }

        public BitmapSource TryGet(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            return cache.TryGetValue(Key(path, decodeWidth), out var image) ? image : null;
        }

        /// <summary>Synchronous load for single, user-initiated images (e.g. the winner card).</summary>
        public BitmapSource Get(string path, int decodeWidth)
        {
            var cached = TryGet(path, decodeWidth);
            if (cached != null || string.IsNullOrEmpty(path))
            {
                return cached;
            }

            var loaded = Load(path, decodeWidth);
            if (loaded != null)
            {
                Store(Key(path, decodeWidth), loaded);
            }

            return loaded;
        }

        /// <summary>Loads missing images in the background, then invokes <paramref name="onLoaded"/> once on the dispatcher.</summary>
        public void Prefetch(IEnumerable<string> paths, int decodeWidth, Dispatcher dispatcher, Action onLoaded)
        {
            var todo = paths
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .Where(p => !cache.ContainsKey(Key(p, decodeWidth)) && !failed.ContainsKey(Key(p, decodeWidth)))
                .Where(p => pending.TryAdd(Key(p, decodeWidth), true))
                .ToList();
            if (todo.Count == 0)
            {
                return;
            }

            Task.Run(() =>
            {
                var any = false;
                foreach (var path in todo)
                {
                    var key = Key(path, decodeWidth);
                    var image = Load(path, decodeWidth);
                    if (image != null)
                    {
                        Store(key, image);
                        any = true;
                    }

                    pending.TryRemove(key, out _);
                }

                if (any && onLoaded != null)
                {
                    dispatcher.BeginInvoke(onLoaded, DispatcherPriority.Background);
                }
            });
        }

        private BitmapSource Load(string path, int decodeWidth)
        {
            var key = Key(path, decodeWidth);
            try
            {
                if (!File.Exists(path))
                {
                    failed[key] = true;
                    return null;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad; // read fully so the file isn't locked
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.DecodePixelWidth = decodeWidth;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze(); // frozen bitmaps can cross from the worker thread to the UI thread
                    return image;
                }
            }
            catch (Exception e)
            {
                // Corrupt or unsupported artwork: remember and fall back to placeholders.
                failed[key] = true;
                Logger.Debug($"Game Randomiser could not load image {path}: {e.Message}");
                return null;
            }
        }

        private void Store(string key, BitmapSource image)
        {
            if (cache.Count > MaxEntries)
            {
                cache.Clear();
            }

            cache[key] = image;
        }

        private static string Key(string path, int width) => width + "|" + path;
    }
}
