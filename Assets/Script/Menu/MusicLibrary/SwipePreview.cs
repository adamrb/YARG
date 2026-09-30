using System;
using System.Threading;
using System.Threading.Tasks;
using YARG.Audio.BASS;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Menu.MusicLibrary
{
    /// <summary>
    /// Plays a song's preview for Song Swipe, like the library preview, but levels its loudness: it listens
    /// to the first moment of the clip and adjusts the volume so quiet and loud charts play at a similar
    /// level. The level is read before the volume control, so one measurement gives the correction.
    /// </summary>
    public sealed class SwipePreview : IDisposable
    {
        private const double DEFAULT_START = 20.0;
        private const double CLIP_LENGTH = 30.0;
        private const double FADE_IN = 0.5;
        private const double FADE_OUT = 1.0;

        // Mono RMS the leveling aims for, and how far it may turn a song up or down
        private const float TARGET_RMS = 0.12f;
        private const float MIN_GAIN = 0.3f;
        private const float MAX_GAIN = 3f;
        private const int MEASURE_MILLISECONDS = 500;

        private readonly StemMixer _mixer;
        private readonly CancellationTokenSource _canceller = new();
        private readonly double _start;
        private readonly double _length;
        private readonly float _volume;
        private bool _disposed;

        public static async Task<SwipePreview> Create(SongEntry song, float volume, double delay,
            bool enableCensoring, CancellationToken token)
        {
            try
            {
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token);
                }

                var mixer = await Task.Run(() => song.LoadPreviewAudio(1f, enableCensoring), token);
                if (mixer == null || token.IsCancellationRequested)
                {
                    mixer?.Dispose();
                    return null;
                }

                double length = mixer.Length;
                double start = song.PreviewStartSeconds >= 0 && song.PreviewStartSeconds < length
                    ? song.PreviewStartSeconds
                    : length >= DEFAULT_START + CLIP_LENGTH ? DEFAULT_START : Math.Max(0, (length - CLIP_LENGTH) / 2);
                double clip = Math.Min(CLIP_LENGTH, length - start);

                var preview = new SwipePreview(mixer, start, clip, volume);
                _ = preview.Loop();
                return preview;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Error while loading the Song Swipe preview!");
                return null;
            }
        }

        private SwipePreview(StemMixer mixer, double start, double length, float volume)
        {
            _mixer = mixer;
            _start = start;
            _length = length;
            _volume = volume;
        }

        private async Task Loop()
        {
            var token = _canceller.Token;
            try
            {
                double volume = _volume;
                bool leveled = false;
                while (!token.IsCancellationRequested)
                {
                    _mixer.SetPosition(_start);
                    _mixer.FadeIn(volume, FADE_IN);
                    _mixer.Play();

                    if (!leveled)
                    {
                        leveled = true;
                        volume = await MeasureAndLevel(volume, token);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _length - FADE_OUT)), token);
                    _mixer.FadeOut(FADE_OUT);
                    await Task.Delay(TimeSpan.FromSeconds(FADE_OUT), token);
                    _mixer.Pause();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Song Swipe preview stopped unexpectedly.");
            }
        }

        private async Task<double> MeasureAndLevel(double volume, CancellationToken token)
        {
            var level = new float[1];
            float sum = 0f;
            int samples = 0;
            for (int elapsed = 0; elapsed < MEASURE_MILLISECONDS; elapsed += 50)
            {
                await Task.Delay(50, token);
                if (_mixer.GetLevel(level) == 0 && level[0] > 0.001f)
                {
                    sum += level[0];
                    samples++;
                }
            }

            if (samples == 0)
            {
                return volume;
            }

            float rms = sum / samples;
            float gain = Math.Clamp(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN);

            // Volumes go through YARG's perceptual curve, so scale the real amplitude and map it back
            double amplitude = BassHelpers.ExponentialVolume(volume) * gain;
            double leveled = BassHelpers.LogarithmicVolume(Math.Min(amplitude, 1.5));
            _mixer.FadeIn(leveled, FADE_IN);
            return leveled;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _canceller.Cancel();
            try
            {
                _mixer.FadeOut(0.15);
            }
            catch (Exception)
            {
                // The mixer may already be gone if the audio device changed
            }

            _ = DisposeLater();
        }

        private async Task DisposeLater()
        {
            await Task.Delay(200);
            _mixer.Dispose();
            _canceller.Dispose();
        }
    }
}
