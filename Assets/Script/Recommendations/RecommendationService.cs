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
using YARG.Helpers.Extensions;
using YARG.Player;
using YARG.Playlists;
using YARG.Scores;
using YARG.Settings;
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
            public Instrument Instrument;
            public IReadOnlyDictionary<string, SongFacts> Library;
            public IReadOnlyDictionary<string, SongEntry> Songs;
            public ProfileHistory History;

            // The history's lists, kept mutable so a swipe session can add answers as they happen
            public List<FeedbackFact> Feedback;
            public List<string> Favorites;

            /// <summary>
            /// The history as it is now, safe to read on another thread while the lists keep changing.
            /// </summary>
            public ProfileHistory CopyHistory() => new()
            {
                Plays = History.Plays,
                Quits = History.Quits,
                Feedback = Feedback.ToList(),
                Favorites = Favorites.ToList(),
                CurrentDifficulty = History.CurrentDifficulty,
                Now = DateTime.Now,
            };
        }

        // How many manual refreshes back a song stays hidden, so a refresh brings genuinely new picks
        private const int REFRESH_MEMORY = 3;

        private static readonly Random _random = new();
        private static readonly Lazy<SongMap> _songMap = new(LoadSongMap);

        // The library as the recommender sees it, rebuilt only when the songs or the profile's part change
        private static SongEntry[] _librarySongs;
        private static (Instrument, Difficulty) _libraryPart;
        private static Dictionary<string, SongFacts> _library;
        private static Dictionary<string, SongEntry> _entries;

        // The rows last built and the models behind them. Coming back to the library shows the same rows
        // (including a refresh's) until the history changes, and a refresh reuses the models.
        private static int _rowsStamp;
        private static TasteModel _taste;
        private static SkillModel _skill;
        private static List<Row> _rows;
        private static readonly Queue<HashSet<string>> _shownBeforeRefresh = new();

        /// <summary>
        /// The profile recommendations are made for: the first human player, as the score sorts use.
        /// </summary>
        public static YargProfile GetPrimaryProfile()
        {
            return PlayerContainer.Players.Select(player => player.Profile).FirstOrDefault(profile => !profile.IsBot);
        }

        /// <summary>
        /// Builds the recommendation rows for the primary profile, or returns null when there is no human
        /// player or personal recommendations are off (the caller then keeps the old random picks).
        /// </summary>
        /// <param name="refresh">
        /// True when the player asked for new picks: songs shown by the last few refreshes are skipped.
        /// Otherwise the rows change only as the history does, so they stay familiar between visits.
        /// </param>
        public static List<Row> GetRows(bool refresh = false)
        {
            var profile = GetPrimaryProfile();
            if (profile == null || !SettingsManager.Settings.PersonalizedRecommendations.Value)
            {
                return null;
            }

            try
            {
                var snapshot = TakeSnapshot(profile);
                var history = snapshot.History;
                int stamp = HashCode.Combine(profile.Id, _library, history.Plays.Count, history.Feedback.Count,
                    history.Quits.Count, history.Favorites.Aggregate(0, (hash, key) => hash ^ key.GetHashCode()));
                if (stamp != _rowsStamp || _taste == null)
                {
                    _rowsStamp = stamp;
                    _taste = TasteModel.Build(snapshot.Library, history);
                    _skill = SkillModel.Fit(history);
                    _rows = null;
                    _shownBeforeRefresh.Clear();
                }

                if (_rows != null && !refresh)
                {
                    return _rows;
                }

                var skip = refresh ? SkipForRefresh() : null;
                var random = refresh ? _random : new Random(stamp);
                var songs = Recommender.Recommend(snapshot.Library, history, _taste, _skill, random, skip);
                if (songs.Count == 0 && skip != null)
                {
                    // The last few refreshes used up every candidate, so start over
                    _shownBeforeRefresh.Clear();
                    songs = Recommender.Recommend(snapshot.Library, history, _taste, _skill, random);
                }

                // Nothing to recommend (for example no chart at the profile's difficulty): keep the random picks
                if (songs.Count == 0)
                {
                    return null;
                }

                _rows = songs
                    .GroupBy(s => s.Kind)
                    .Select(g => new Row { Kind = g.Key, Songs = g.Select(s => snapshot.Songs[s.Song.Key]).ToArray() })
                    .ToList();
                return _rows;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to build song recommendations.");
                return null;
            }
        }

        /// <summary>
        /// Starts a Song Swipe session for the primary profile, training its model in the background.
        /// Returns null when there is no human player or the session could not be built.
        /// </summary>
        public static async Task<SwipeSession> StartSwipeSessionAsync()
        {
            var profile = GetPrimaryProfile();
            if (profile == null)
            {
                return null;
            }

            try
            {
                var snapshot = TakeSnapshot(profile);
                var (skill, taste) = await Task.Run(() =>
                    (SkillModel.Fit(snapshot.History), TasteModel.Build(snapshot.Library, snapshot.History)));
                return new SwipeSession(snapshot, skill, taste);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to start Song Swipe.");
                return null;
            }
        }

        /// <summary>
        /// Starts loading the song map in the background, so the first recommendations do not wait on it.
        /// </summary>
        public static void PreloadSongMap() => Task.Run(() => _songMap.Value);

        private static HashSet<string> SkipForRefresh()
        {
            if (_rows != null)
            {
                _shownBeforeRefresh.Enqueue(new HashSet<string>(_rows.SelectMany(row => row.Songs)
                    .Select(song => song.Hash.ToString())));
            }

            while (_shownBeforeRefresh.Count > REFRESH_MEMORY)
            {
                _shownBeforeRefresh.Dequeue();
            }

            return new HashSet<string>(_shownBeforeRefresh.SelectMany(set => set));
        }

        private static Snapshot TakeSnapshot(YargProfile profile)
        {
            var instrument = profile.HasValidInstrument ? profile.CurrentInstrument : Instrument.FiveFretGuitar;
            var difficulty = profile.CurrentDifficulty;
            BuildLibrary(instrument, difficulty);

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
                        ChartDifficulty = _entries.TryGetValue(key, out var song)
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
                Instrument = instrument,
                Library = _library,
                Songs = _entries,
                Feedback = feedback,
                Favorites = favorites,
                History = new ProfileHistory
                {
                    Plays = plays,
                    Feedback = feedback,
                    Quits = quits,
                    Favorites = favorites,
                    CurrentDifficulty = difficulty,
                    Now = DateTime.Now,
                },
            };
        }

        private static void BuildLibrary(Instrument instrument, Difficulty difficulty)
        {
            var songs = SongContainer.Songs;
            if (_library != null && ReferenceEquals(songs, _librarySongs) && _libraryPart == (instrument, difficulty))
            {
                return;
            }

            var map = _songMap.Value;
            var library = new Dictionary<string, SongFacts>();
            var entries = new Dictionary<string, SongEntry>();
            foreach (var song in songs)
            {
                string key = song.Hash.ToString();
                if (song.IsDuplicate || entries.ContainsKey(key))
                {
                    continue;
                }

                // SearchStr has rich-text tags and diacritics removed, so markup never becomes a feature
                int year = song.YearAsNumber != int.MaxValue ? song.YearAsNumber : 0;
                var (position, popularity) = map.Place(song.Artist.SearchStr, song.Name.SearchStr);
                library[key] = new SongFacts
                {
                    Key = key,
                    Features = SongNormalizer.Features(song.Artist.SearchStr, song.Genre.SearchStr,
                        song.Subgenre.SearchStr, song.Charter.SearchStr, song.Source.SearchStr, year,
                        song.SongLengthSeconds).Concat(popularity).ToArray(),
                    ChartDifficulty = ChartDifficulty(song, instrument, difficulty, 1f),
                    Identity = SongNormalizer.Identity(song.Artist.SearchStr, song.Name.SearchStr),
                    Position = position,
                };
                entries[key] = song;
            }

            SongNormalizer.AssignCanonical(library.Values.Select(facts => (facts, entries[facts.Key].Name.SearchStr)));
            _librarySongs = songs;
            _libraryPart = (instrument, difficulty);
            _library = library;
            _entries = entries;
        }

        /// <summary>
        /// The recommender's difficulty number for a song's part, or null if Difficulty Select would not
        /// offer the song on this instrument and difficulty.
        /// </summary>
        private static float? ChartDifficulty(SongEntry song, Instrument instrument, Difficulty difficulty, float songSpeed)
        {
            var part = song.PlayablePart(instrument);
            if (!part.IsActive() || !song.HasPlayableDifficulty(instrument, difficulty))
            {
                return null;
            }

            return SkillModel.ChartDifficulty(part.Intensity, difficulty, songSpeed);
        }

        /// <summary>
        /// The song map shipped in StreamingAssets/recommendations/song-map.tsv.gz. A missing file just means
        /// no map-based signal.
        /// </summary>
        private static SongMap LoadSongMap()
        {
            try
            {
                string path = Path.Combine(PathHelper.StreamingAssetsPath, "recommendations", "song-map.tsv.gz");
                if (File.Exists(path))
                {
                    using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
                    var map = SongMap.Parse(ReadLines(reader));
                    YargLogger.LogFormatInfo("Loaded song map with {0} songs and {1} artists", map.TrackCount, map.ArtistCount);
                    return map;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to load the song map.");
            }

            return SongMap.Empty;
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
