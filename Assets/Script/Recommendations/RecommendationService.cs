using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Player;
using YARG.Playlists;
using YARG.Scores;
using YARG.Song;

namespace YARG.Recommendations
{
    /// <summary>
    /// Connects the recommendation model to the game: gathers the profile's history, runs the model,
    /// and maps the results back to songs in the library.
    /// </summary>
    public static class RecommendationService
    {
        public sealed class Section
        {
            public RecommendationKind Kind;
            public SongEntry[] Songs;
        }

        private static readonly System.Random _random = new();

        /// <summary>
        /// The profile recommendations are made for: the first human player, like the score sorts use.
        /// </summary>
        public static YargProfile GetPrimaryProfile()
        {
            return PlayerContainer.Players
                .Select(player => player.Profile)
                .FirstOrDefault(profile => !profile.IsBot);
        }

        internal sealed class Snapshot
        {
            public YargProfile Profile;
            public Dictionary<string, SongFacts> Library;
            public Dictionary<string, SongEntry> Songs;
            public List<PlayFact> Plays;
            public List<FeedbackFact> Feedback;
            public List<QuitFact> Quits;
            public List<string> Favorites;
        }

        private static Snapshot TakeSnapshot(YargProfile profile)
        {
            var instrument = profile.HasValidInstrument ? profile.CurrentInstrument : Instrument.FiveFretGuitar;
            var difficulty = profile.CurrentDifficulty;

            var library = new Dictionary<string, SongFacts>();
            var songs = new Dictionary<string, SongEntry>();
            foreach (var song in SongContainer.Songs)
            {
                if (song.IsDuplicate)
                {
                    continue;
                }

                string key = song.Hash.ToString();
                if (library.ContainsKey(key))
                {
                    continue;
                }

                library[key] = new SongFacts
                {
                    Key = key,
                    Features = GetFeatures(song),
                    ChartDifficulty = GetChartDifficulty(song, instrument, difficulty, 1f),
                    Identity = SongNormalizer.Identity(song.Artist.SearchStr, song.Name.SearchStr),
                };
                songs[key] = song;
            }

            SongNormalizer.AssignCanonical(library.Values.Select(f => (f, songs[f.Key].Name.SearchStr)));

            var plays = new List<PlayFact>();
            foreach (var record in ScoreContainer.GetPlayerHistory(profile.Id))
            {
                if (record.SongChecksum == null || record.Percent == null)
                {
                    continue;
                }

                string key = HashWrapper.Create(record.SongChecksum).ToString();
                songs.TryGetValue(key, out var song);
                plays.Add(new PlayFact
                {
                    Key = key,
                    Date = record.Date,
                    Accuracy = Math.Clamp(record.Percent.Value, 0f, 1f),
                    OnCurrentInstrument = record.Instrument == instrument,
                    OnCurrentDifficulty = record.Difficulty == difficulty,
                    SongSpeed = record.SongSpeed > 0f ? record.SongSpeed : 1f,
                    ChartDifficulty = song != null
                        ? GetChartDifficulty(song, record.Instrument, record.Difficulty, record.SongSpeed)
                        : null,
                });
            }

            var feedback = RecommendationStore.GetFeedback(profile.Id)
                .Where(f => f.SongChecksum != null)
                .Select(f => new FeedbackFact
                {
                    Key = HashWrapper.Create(f.SongChecksum).ToString(),
                    Liked = f.Liked,
                    Date = f.Date,
                })
                .ToList();

            var quits = RecommendationStore.GetQuits(profile.Id)
                .Where(q => q.SongChecksum != null)
                .Select(q => new QuitFact
                {
                    Key = HashWrapper.Create(q.SongChecksum).ToString(),
                    Progress = q.Progress,
                    Date = q.Date,
                })
                .ToList();

            var favorites = PlaylistContainer.FavoritesPlaylist?.ToList()
                .Select(song => song.Hash.ToString())
                .ToList() ?? new List<string>();

            return new Snapshot
            {
                Profile = profile,
                Library = library,
                Songs = songs,
                Plays = plays,
                Feedback = feedback,
                Quits = quits,
                Favorites = favorites,
            };
        }

        private static SongFeature[] GetFeatures(SongEntry song)
        {
            int year = song.YearAsNumber != int.MaxValue ? song.YearAsNumber : 0;
            // SearchStr has rich-text tags and diacritics removed, so markup never turns into features
            return SongNormalizer.Features(song.Artist.SearchStr, song.Genre.SearchStr, song.Subgenre.SearchStr,
                song.Charter.SearchStr, song.Source.SearchStr, year, song.SongLengthSeconds);
        }

        private static float? GetChartDifficulty(SongEntry song, Instrument instrument, Difficulty difficulty,
            float songSpeed)
        {
            var part = song[instrument];
            if (!part.IsActive())
            {
                // Same lane conversions the difficulty select allows: 5-lane charts play on 4-lane
                // and Pro Drums, and 4-lane charts play on 5-lane
                part = instrument switch
                {
                    Instrument.FourLaneDrums or Instrument.ProDrums => song[Instrument.FiveLaneDrums],
                    Instrument.FiveLaneDrums                         => song[Instrument.ProDrums],
                    _                                                => part,
                };

                if (!part.IsActive())
                {
                    return null;
                }
            }

            // Vocals do not track difficulties per part in the song metadata
            bool isVocals = instrument is Instrument.Vocals or Instrument.Harmony;
            if (!isVocals && !part[difficulty])
            {
                return null;
            }

            return SkillModel.ChartDifficulty(part.Intensity, (int) difficulty, songSpeed);
        }

        /// <summary>
        /// Builds the recommendation sections for the primary profile, or returns null when there is no
        /// human player (the caller should fall back to the old random recommendations).
        /// </summary>
        public static List<Section> GetSections()
        {
            var profile = GetPrimaryProfile();
            if (profile == null)
            {
                return null;
            }

            try
            {
                var snapshot = TakeSnapshot(profile);
                var result = Recommender.Recommend(
                    snapshot.Library,
                    snapshot.Plays,
                    snapshot.Feedback,
                    snapshot.Quits,
                    snapshot.Favorites,
                    (int) profile.CurrentDifficulty,
                    DateTime.Now,
                    _random);

                WriteReport(snapshot, result);

                return result.Songs
                    .GroupBy(s => s.Kind)
                    .OrderBy(g => g.Key)
                    .Select(g => new Section
                    {
                        Kind = g.Key,
                        Songs = g.Select(s => snapshot.Songs[s.Key]).ToArray(),
                    })
                    .ToList();
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to build song recommendations.");
                return null;
            }
        }

        /// <summary>
        /// Keeps the model between swipes so each swipe does not rebuild the whole library snapshot.
        /// </summary>
        public sealed class SwipeSession
        {
            private readonly Snapshot _snapshot;
            private readonly HashSet<string> _seen = new();
            private TasteModel _taste;

            public YargProfile Profile => _snapshot.Profile;
            public int LikedCount { get; private set; }
            public int PassedCount { get; private set; }

            internal SwipeSession(Snapshot snapshot)
            {
                _snapshot = snapshot;
                foreach (var feedback in snapshot.Feedback)
                {
                    _seen.Add(feedback.Key);
                }

                Rebuild();
            }

            private void Rebuild()
            {
                _taste = TasteModel.Build(_snapshot.Library, _snapshot.Plays, _snapshot.Feedback,
                    _snapshot.Quits, _snapshot.Favorites, DateTime.Now);
            }

            private SkillModel _skill;

            /// <summary>
            /// A short reason the song was picked and how hard it should be, for the swipe card.
            /// </summary>
            public (string Reason, string Difficulty) Describe(SongEntry song)
            {
                _skill ??= SkillModel.Fit(_snapshot.Plays, (int) Profile.CurrentDifficulty, DateTime.Now);

                const string KEY = "Menu.MusicLibrary.SongSwipe";
                var artist = new SongFeature(FeatureType.Artist, SongNormalizer.Artist(song.Artist.SearchStr));
                var genre = new SongFeature(FeatureType.Genre, SongNormalizer.Collapse(song.Genre.SearchStr));

                string reason;
                if (_taste.Affinity(artist) > 0.3f)
                {
                    reason = Localize.KeyFormat((KEY, "ReasonArtist"), song.Artist.Original);
                }
                else if (_taste.Affinity(genre) > 0.3f)
                {
                    reason = Localize.KeyFormat((KEY, "ReasonGenre"), song.Genre.Original);
                }
                else if (genre.Value.Length > 0 && _taste.ObservationCount(genre) == 0)
                {
                    reason = Localize.KeyFormat((KEY, "ReasonNewGenre"), song.Genre.Original);
                }
                else
                {
                    reason = Localize.Key(KEY, "ReasonDifferent");
                }

                if (!_snapshot.Library.TryGetValue(song.Hash.ToString(), out var facts) ||
                    !facts.ChartDifficulty.HasValue)
                {
                    return (reason, Localize.Key(KEY, "NotCharted"));
                }

                var instrument = Profile.HasValidInstrument ? Profile.CurrentInstrument : Instrument.FiveFretGuitar;
                int intensity = song[instrument].Intensity;
                string tier = intensity < 0 ? "?" : intensity.ToString();
                float predicted = _skill.PredictSong(facts);
                string band = predicted >= Recommender.AT_LEVEL ? "Comfortable"
                    : predicted >= Recommender.STRETCH ? "Stretch"
                    : "Challenge";

                return (reason, Localize.KeyFormat((KEY, "DifficultyLine"), tier, Localize.Key(KEY, band)));
            }

            public List<SongEntry> NextSongs(int count)
            {
                var keys = Recommender.PickSwipeCandidates(_snapshot.Library, _taste, _seen, count, _random);
                foreach (var key in keys)
                {
                    _seen.Add(key);
                }

                return keys.Select(k => _snapshot.Songs[k]).ToList();
            }

            // Undo tokens are local to the session, so undo works even if the database write failed
            private readonly Dictionary<int, (int RecordId, FeedbackFact Fact)> _sessionFeedback = new();
            private int _nextToken;

            /// <summary>
            /// Records a like or pass and returns a token that <see cref="UndoSwipe"/> can take back.
            /// </summary>
            public int Swipe(SongEntry song, bool liked)
            {
                int recordId = RecommendationStore.RecordFeedback(Profile.Id, song.Hash.HashBytes, liked);
                var fact = new FeedbackFact
                {
                    Key = song.Hash.ToString(),
                    Liked = liked,
                    Date = DateTime.Now,
                };
                _snapshot.Feedback.Add(fact);
                int token = _nextToken++;
                _sessionFeedback[token] = (recordId, fact);

                if (liked) LikedCount++;
                else PassedCount++;

                Rebuild();
                return token;
            }

            public void UndoSwipe(int token)
            {
                if (!_sessionFeedback.TryGetValue(token, out var entry))
                {
                    return;
                }

                RecommendationStore.DeleteFeedback(entry.RecordId);
                _snapshot.Feedback.Remove(entry.Fact);
                _sessionFeedback.Remove(token);

                if (entry.Fact.Liked) LikedCount--;
                else PassedCount--;

                Rebuild();
            }

            /// <summary>
            /// Call after the song was added to favorites, so the rest of the session learns from it.
            /// Returns true if the song was not already a favorite in the model.
            /// </summary>
            public bool Favorite(SongEntry song)
            {
                string key = song.Hash.ToString();
                if (_snapshot.Favorites.Contains(key))
                {
                    return false;
                }

                _snapshot.Favorites.Add(key);
                Rebuild();
                return true;
            }

            public void UndoFavorite(SongEntry song)
            {
                _snapshot.Favorites.Remove(song.Hash.ToString());
                Rebuild();
            }

            /// <summary>
            /// Songs this profile has already swiped, most likely mistakes first (the ones that disagree
            /// most with everything else the profile has done), with the current answer for each.
            /// </summary>
            public List<(SongEntry Song, bool Liked)> RatedSongs()
            {
                return Recommender.RankLikelyMistakes(_snapshot.Library, _snapshot.Plays, _snapshot.Feedback,
                        _snapshot.Quits, _snapshot.Favorites, DateTime.Now)
                    .Where(r => _snapshot.Songs.ContainsKey(r.Key))
                    .Select(r => (_snapshot.Songs[r.Key], r.Liked))
                    .ToList();
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

        /// <summary>
        /// Writes a plain-text summary of what the model learned, for tuning and for curious players.
        /// </summary>
        private static void WriteReport(Snapshot snapshot, RecommendationResult result)
        {
            try
            {
                var profile = snapshot.Profile;
                var text = new StringBuilder();
                text.AppendLine($"Recommendation model for {profile.Name} ({DateTime.Now:yyyy-MM-dd HH:mm})");
                text.AppendLine($"Instrument: {profile.CurrentInstrument}, difficulty: {profile.CurrentDifficulty}");
                text.AppendLine($"Plays: {snapshot.Plays.Count} ({result.Skill.PlayCount} on this instrument), " +
                    $"swipes: {snapshot.Feedback.Count}, quits: {snapshot.Quits.Count}, favorites: {snapshot.Favorites.Count}");
                text.AppendLine($"Songs with taste evidence: {result.Taste.ObservedSongCount}");
                text.AppendLine($"Skill: {result.Skill.Skill:0.00} (a chart at this number predicts about 77% accuracy)");
                text.AppendLine();
                text.AppendLine("Strongest learned preferences:");
                foreach (var (feature, affinity, count) in result.Taste.TopFeatures(20))
                {
                    text.AppendLine($"  {affinity,6:+0.00;-0.00}  {feature.Type,-9} {feature.Value} ({count} songs)");
                }

                text.AppendLine();
                foreach (var group in result.Songs.GroupBy(s => s.Kind))
                {
                    text.AppendLine(group.Key.ToString());
                    foreach (var song in group)
                    {
                        var entry = snapshot.Songs[song.Key];
                        text.AppendLine($"  taste {song.Taste,5:0.00}  predicted {song.PredictedAccuracy:P0}  " +
                            $"{entry.Artist.Original} - {entry.Name.Original}");
                    }
                }

                string path = Path.Combine(Path.GetDirectoryName(RecommendationStore.DatabasePath)!,
                    $"model-{profile.Id}.txt");
                File.WriteAllText(path, text.ToString());
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to write the recommendation report.");
            }
        }
    }
}
