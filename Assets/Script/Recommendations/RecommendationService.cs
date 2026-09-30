using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Core.Song.Recommendations;
using YARG.Helpers;
using YARG.Player;
using YARG.Playlists;
using YARG.Scores;
using YARG.Song;

namespace YARG.Recommendations
{
    /// <summary>
    /// Connects the recommender in YARG.Core to the game: gathers the library and the profile's history,
    /// runs the model, and maps the results back to songs.
    /// </summary>
    public static class RecommendationService
    {
        public sealed class Row
        {
            public RecommendationKind Kind;
            public SongEntry[] Songs;
        }

        /// <summary>
        /// The library and one profile's history, as the recommender sees them.
        /// </summary>
        internal sealed class Snapshot
        {
            public YargProfile Profile;
            public Dictionary<string, SongFacts> Library;
            public Dictionary<string, SongEntry> Songs;
            public ProfileHistory History;

            // The history's lists, kept mutable so a swipe session can add answers as they happen
            public List<FeedbackFact> Feedback;
            public List<string> Favorites;
        }

        // How many manual refreshes back a song stays hidden, so a refresh brings genuinely new picks
        private const int REFRESH_MEMORY = 3;

        private static readonly Random _random = new();
        private static readonly Queue<HashSet<string>> _shownBeforeRefresh = new();
        private static HashSet<string> _lastShown = new();
        private static Guid _lastProfile;
        private static readonly Lazy<ArtistMap> _artistMap = new(LoadArtistMap);

        /// <summary>
        /// The profile recommendations are made for: the first human player, as the score sorts use.
        /// </summary>
        public static YargProfile GetPrimaryProfile()
        {
            return PlayerContainer.Players.Select(player => player.Profile).FirstOrDefault(profile => !profile.IsBot);
        }

        /// <summary>
        /// Builds the recommendation rows for the primary profile, or returns null when there is no human
        /// player (the caller then keeps the old random recommendations).
        /// </summary>
        /// <param name="refresh">
        /// True when the player asked for new picks: songs shown by the last few refreshes are skipped.
        /// Otherwise the rows change only as the history does, so they stay familiar between visits.
        /// </param>
        public static List<Row> GetRows(bool refresh = false)
        {
            var profile = GetPrimaryProfile();
            if (profile == null)
            {
                return null;
            }

            if (profile.Id != _lastProfile)
            {
                _lastProfile = profile.Id;
                _shownBeforeRefresh.Clear();
                _lastShown = new HashSet<string>();
            }

            try
            {
                var snapshot = TakeSnapshot(profile);
                var history = snapshot.History;
                var random = refresh
                    ? _random
                    : new Random(HashCode.Combine(profile.Id, history.Plays.Count, history.Feedback.Count));
                var result = Recommender.Recommend(snapshot.Library, history, random, refresh ? SkipForRefresh() : null);
                _lastShown = new HashSet<string>(result.Songs.Select(s => s.Song.Key));

                return result.Songs
                    .GroupBy(s => s.Kind)
                    .Select(g => new Row { Kind = g.Key, Songs = g.Select(s => snapshot.Songs[s.Song.Key]).ToArray() })
                    .ToList();
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to build song recommendations.");
                return null;
            }
        }

        public static SwipeSession StartSwipeSession()
        {
            var profile = GetPrimaryProfile();
            if (profile == null)
            {
                return null;
            }

            try
            {
                return new SwipeSession(TakeSnapshot(profile));
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to start Song Swipe.");
                return null;
            }
        }

        private static HashSet<string> SkipForRefresh()
        {
            _shownBeforeRefresh.Enqueue(_lastShown);
            while (_shownBeforeRefresh.Count > REFRESH_MEMORY)
            {
                _shownBeforeRefresh.Dequeue();
            }

            return new HashSet<string>(_shownBeforeRefresh.SelectMany(set => set));
        }

        internal static Snapshot TakeSnapshot(YargProfile profile)
        {
            var instrument = profile.HasValidInstrument ? profile.CurrentInstrument : Instrument.FiveFretGuitar;
            var difficulty = profile.CurrentDifficulty;
            var map = _artistMap.Value;

            var library = new Dictionary<string, SongFacts>();
            var songs = new Dictionary<string, SongEntry>();
            foreach (var song in SongContainer.Songs)
            {
                string key = song.Hash.ToString();
                if (song.IsDuplicate || songs.ContainsKey(key))
                {
                    continue;
                }

                // SearchStr has rich-text tags and diacritics removed, so markup never becomes a feature
                int year = song.YearAsNumber != int.MaxValue ? song.YearAsNumber : 0;
                library[key] = new SongFacts
                {
                    Key = key,
                    Features = SongNormalizer.Features(song.Artist.SearchStr, song.Genre.SearchStr,
                        song.Subgenre.SearchStr, song.Charter.SearchStr, song.Source.SearchStr, year,
                        song.SongLengthSeconds),
                    ChartDifficulty = ChartDifficulty(song, instrument, difficulty, 1f),
                    Identity = SongNormalizer.Identity(song.Artist.SearchStr, song.Name.SearchStr),
                    ArtistPosition = map.Find(song.Artist.SearchStr),
                };
                songs[key] = song;
            }

            SongNormalizer.AssignCanonical(library.Values.Select(facts => (facts, songs[facts.Key].Name.SearchStr)));

            var plays = ScoreContainer.GetPlayerHistory(profile.Id)
                .Where(record => record.SongChecksum != null && record.Percent != null)
                .Select(record =>
                {
                    string key = HashWrapper.Create(record.SongChecksum).ToString();
                    return new PlayFact
                    {
                        Key = key,
                        Date = record.Date,
                        Accuracy = Math.Clamp(record.Percent.Value, 0f, 1f),
                        OnCurrentInstrument = record.Instrument == instrument,
                        OnCurrentDifficulty = record.Difficulty == difficulty,
                        SongSpeed = record.SongSpeed > 0f ? record.SongSpeed : 1f,
                        ChartDifficulty = songs.TryGetValue(key, out var song)
                            ? ChartDifficulty(song, record.Instrument, record.Difficulty, record.SongSpeed)
                            : null,
                    };
                })
                .ToList();

            var feedback = RecommendationStore.GetFeedback(profile.Id)
                .Select(f => new FeedbackFact { Key = HashWrapper.Create(f.SongChecksum).ToString(), Liked = f.Liked, Date = f.Date })
                .ToList();
            var quits = RecommendationStore.GetQuits(profile.Id)
                .Select(q => new QuitFact { Key = HashWrapper.Create(q.SongChecksum).ToString(), Progress = q.Progress, Date = q.Date })
                .ToList();
            var favorites = PlaylistContainer.FavoritesPlaylist?.ToList().Select(song => song.Hash.ToString()).ToList()
                ?? new List<string>();

            return new Snapshot
            {
                Profile = profile,
                Library = library,
                Songs = songs,
                Feedback = feedback,
                Favorites = favorites,
                History = new ProfileHistory
                {
                    Plays = plays,
                    Feedback = feedback,
                    Quits = quits,
                    Favorites = favorites,
                    CurrentDifficulty = (int) difficulty,
                    Now = DateTime.Now,
                },
            };
        }

        /// <summary>
        /// The recommender's difficulty number for a song's part, or null if it cannot be played that way.
        /// Uses the same lane conversions as the difficulty select: 5-lane drums play on 4-lane and Pro
        /// Drums, and 4-lane charts play on 5-lane.
        /// </summary>
        private static float? ChartDifficulty(SongEntry song, Instrument instrument, Difficulty difficulty, float songSpeed)
        {
            var part = song[instrument];
            if (!part.IsActive())
            {
                part = instrument switch
                {
                    Instrument.FourLaneDrums or Instrument.ProDrums => song[Instrument.FiveLaneDrums],
                    Instrument.FiveLaneDrums                         => song[Instrument.ProDrums],
                    _                                                => part,
                };
            }

            // Vocals do not track difficulties per part in song metadata
            bool isVocals = instrument is Instrument.Vocals or Instrument.Harmony;
            if (!part.IsActive() || (!isVocals && !part[difficulty]))
            {
                return null;
            }

            return SkillModel.ChartDifficulty(part.Intensity, (int) difficulty, songSpeed);
        }

        /// <summary>
        /// Starts loading the artist map in the background, so the first recommendations do not wait on it.
        /// </summary>
        public static void PreloadArtistMap() => Task.Run(() => _artistMap.Value);

        /// <summary>
        /// The artist map shipped in StreamingAssets/recommendations/artist-map.tsv.gz. A missing file just
        /// means no map-based signal.
        /// </summary>
        private static ArtistMap LoadArtistMap()
        {
            try
            {
                string path = Path.Combine(PathHelper.StreamingAssetsPath, "recommendations", "artist-map.tsv.gz");
                if (File.Exists(path))
                {
                    using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
                    var map = ArtistMap.Parse(ReadLines(reader));
                    YargLogger.LogFormatInfo("Loaded artist map with {0} artists", map.Count);
                    return map;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to load the artist map.");
            }

            return ArtistMap.Empty;
        }

        private static IEnumerable<string> ReadLines(TextReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }
    }
}
