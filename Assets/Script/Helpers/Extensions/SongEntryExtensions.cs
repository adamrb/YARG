using YARG.Core;
using YARG.Core.Song;

namespace YARG.Helpers.Extensions
{
    public static class SongEntryExtensions
    {
        /// <summary>
        /// Whether the song can be played on this instrument and difficulty, counting the lane
        /// conversions: 5-lane drum charts play on 4-lane and Pro Drums, and 4-lane charts play on 5-lane.
        /// </summary>
        public static bool HasPlayableDifficulty(this SongEntry entry, Instrument instrument, Difficulty difficulty)
        {
            // For vocals, insert special difficulties
            if (instrument is Instrument.Vocals or Instrument.Harmony)
            {
                return difficulty is not Difficulty.ExpertPlus;
            }

            // For PK, disallow beginner
            if (instrument is Instrument.ProKeys && difficulty is Difficulty.Beginner)
            {
                return false;
            }

            // Otherwise, we can do this
            return entry[instrument][difficulty] || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => entry[Instrument.FiveLaneDrums][difficulty],
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => entry[Instrument.ProDrums][difficulty],
                _ => false
            };
        }

        /// <summary>
        /// The part played on this instrument, following the same lane conversions as
        /// <see cref="HasPlayableDifficulty"/> when the song has no part of its own for it.
        /// </summary>
        public static PartValues PlayablePart(this SongEntry entry, Instrument instrument)
        {
            var part = entry[instrument];
            if (part.IsActive())
            {
                return part;
            }

            return instrument switch
            {
                Instrument.FourLaneDrums or Instrument.ProDrums => entry[Instrument.FiveLaneDrums],
                Instrument.FiveLaneDrums                         => entry[Instrument.ProDrums],
                _                                                => part,
            };
        }
    }
}
