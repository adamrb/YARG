using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Core.Song.Recommendations;
using YARG.Localization;

namespace YARG.Recommendations
{
    /// <summary>
    /// One Song Swipe session: keeps the library snapshot and the taste model, deals cards, and records
    /// (and undoes) answers. Each answer retrains the model in the background; until that finishes, cards
    /// come from the previous model.
    /// </summary>
    public sealed class SwipeSession
    {
        private const string KEY = "Menu.MusicLibrary.SongSwipe";

        // How close two artists must be on the artist map for "Fans of X play this"
        private const float FANS_OF_SIMILARITY = 0.6f;

        // How strongly the model must lean for "More from X" and "You play a lot of X"
        private const float STRONG_WEIGHT = 0.3f;

        private static readonly Random _random = new();

        private sealed class Answer
        {
            public int RecordId;
            public FeedbackFact Fact;
            public string Favorite;
        }

        private readonly RecommendationService.Snapshot _snapshot;
        private readonly HashSet<string> _dealt;
        private readonly SkillModel _skill;
        private readonly Dictionary<int, Answer> _answers = new();
        private TasteModel _taste;
        private int _nextToken;
        private int _trainings;

        public YargProfile Profile => _snapshot.Profile;
        public int LikedCount { get; private set; }
        public int PassedCount { get; private set; }
        public bool HasRatings => _snapshot.Feedback.Count > 0;

        internal SwipeSession(RecommendationService.Snapshot snapshot, SkillModel skill, TasteModel taste)
        {
            _snapshot = snapshot;
            _skill = skill;
            _taste = taste;
            _dealt = new HashSet<string>(snapshot.Feedback.Select(f => f.Key));
        }

        public List<SongEntry> NextSongs(int count)
        {
            var cards = Recommender.PickSwipeCards(_snapshot.Library, _taste, _dealt, count, _random);
            _dealt.UnionWith(cards.Select(c => c.Key));
            return cards.Select(c => _snapshot.Songs[c.Key]).ToList();
        }

        /// <summary>
        /// Songs already swiped, likely mistakes first, with the current answer for each. Ranking trains
        /// several models, so it runs in the background.
        /// </summary>
        public async Task<List<(SongEntry Song, bool Liked)>> RatedSongsAsync()
        {
            var library = _snapshot.Library;
            var history = _snapshot.CopyHistory();
            var ranked = await Task.Run(() => Recommender.RankLikelyMistakes(library, history));
            return ranked.Select(r => (_snapshot.Songs[r.Key], r.Liked)).ToList();
        }

        /// <summary>
        /// Records a like or pass, and optionally that the song was just added to favorites. Returns a
        /// token for <see cref="UndoSwipe"/>, or -1 if the answer could not be saved (nothing changes then).
        /// </summary>
        public int Swipe(SongEntry song, bool liked, bool addedFavorite = false)
        {
            int recordId = RecommendationStore.RecordFeedback(Profile.Id, song.Hash.HashBytes, liked);
            if (recordId < 0)
            {
                return -1;
            }

            string key = song.Hash.ToString();
            var answer = new Answer
            {
                RecordId = recordId,
                Fact = new FeedbackFact { Key = key, Liked = liked, Date = DateTime.Now },
                Favorite = addedFavorite ? key : null,
            };
            _snapshot.Feedback.Add(answer.Fact);
            if (answer.Favorite != null)
            {
                _snapshot.Favorites.Add(answer.Favorite);
            }

            _answers[_nextToken] = answer;
            Count(liked, 1);
            Retrain();
            return _nextToken++;
        }

        /// <summary>
        /// Takes back an answer. Returns false if it could not be removed from the database (nothing
        /// changes then).
        /// </summary>
        public bool UndoSwipe(int token)
        {
            if (!_answers.TryGetValue(token, out var answer))
            {
                return true;
            }

            if (!RecommendationStore.DeleteFeedback(answer.RecordId))
            {
                return false;
            }

            _answers.Remove(token);
            _snapshot.Feedback.Remove(answer.Fact);
            if (answer.Favorite != null)
            {
                _snapshot.Favorites.Remove(answer.Favorite);
            }

            Count(answer.Fact.Liked, -1);
            Retrain();
            return true;
        }

        /// <summary>
        /// A short reason the song was dealt and how hard it should be, for its card.
        /// </summary>
        public (string Reason, string Difficulty) Describe(SongEntry song)
        {
            var facts = _snapshot.Library[song.Hash.ToString()];
            return (Reason(song, facts), DifficultyLine(song, facts));
        }

        private string Reason(SongEntry song, SongFacts facts)
        {
            string fanOf = ClosestLikedArtist(facts);
            if (fanOf != null)
            {
                return Localize.KeyFormat((KEY, "ReasonFansOf"), fanOf);
            }

            if (facts.Artist != null && _taste.Weight(new SongFeature(FeatureType.Artist, facts.Artist)) > STRONG_WEIGHT)
            {
                return Localize.KeyFormat((KEY, "ReasonArtist"), song.Artist.Original);
            }

            if (facts.Genre != null)
            {
                var genre = new SongFeature(FeatureType.Genre, facts.Genre);
                if (_taste.Weight(genre) > STRONG_WEIGHT)
                {
                    return Localize.KeyFormat((KEY, "ReasonGenre"), song.Genre.Original);
                }

                if (_taste.Observations(genre) == 0)
                {
                    return Localize.KeyFormat((KEY, "ReasonNewGenre"), song.Genre.Original);
                }
            }

            return Localize.Key(KEY, "ReasonDifferent");
        }

        private string DifficultyLine(SongEntry song, SongFacts facts)
        {
            if (!facts.ChartDifficulty.HasValue)
            {
                return Localize.Key(KEY, "NotCharted");
            }

            int intensity = RecommendationService.PlayablePart(song, _snapshot.Instrument).Intensity;
            float predicted = _skill.PredictSong(facts);
            string band = predicted >= Recommender.AT_LEVEL ? "Comfortable"
                : predicted >= Recommender.STRETCH ? "Stretch"
                : "Challenge";
            return Localize.KeyFormat((KEY, "DifficultyLine"), intensity < 0 ? "?" : intensity.ToString(),
                Localize.Key(KEY, band));
        }

        /// <summary>
        /// The liked artist closest to this song's artist on the artist map, if close enough.
        /// </summary>
        private string ClosestLikedArtist(SongFacts facts)
        {
            string best = null;
            float bestSimilarity = FANS_OF_SIMILARITY;
            foreach (var (key, evidence) in _taste.Evidence)
            {
                if (evidence <= 0f || !_snapshot.Library.TryGetValue(key, out var liked) || liked.Artist == facts.Artist)
                {
                    continue;
                }

                float similarity = ArtistMap.Similarity(facts.ArtistPosition, liked.ArtistPosition);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = _snapshot.Songs[key].Artist.Original;
                }
            }

            return best == null ? null : SongNormalizer.StripBrackets(best).Trim();
        }

        private void Count(bool liked, int change)
        {
            if (liked) LikedCount += change;
            else PassedCount += change;
        }

        /// <summary>
        /// Trains a new model on the answers so far in the background. Only the newest training's result
        /// is kept, so answers given in quick succession never leave an older model in place.
        /// </summary>
        private async void Retrain()
        {
            int training = ++_trainings;
            var library = _snapshot.Library;
            var history = _snapshot.CopyHistory();
            try
            {
                var taste = await Task.Run(() => TasteModel.Build(library, history));
                if (training == _trainings)
                {
                    _taste = taste;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to update the Song Swipe model.");
            }
        }
    }
}
