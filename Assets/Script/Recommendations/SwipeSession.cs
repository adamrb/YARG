using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Core.Song.Recommendations;
using YARG.Localization;

namespace YARG.Recommendations
{
    /// <summary>
    /// One Song Swipe session: keeps the library snapshot and the taste model, deals cards, and records
    /// (and undoes) answers, updating the model after each one.
    /// </summary>
    public sealed class SwipeSession
    {
        private const string KEY = "Menu.MusicLibrary.SongSwipe";

        // How close two artists must be on the artist map for "Fans of X play this"
        private const float FANS_OF_SIMILARITY = 0.6f;

        // How strongly the model must lean for "More from X" and "You play a lot of X"
        private const float STRONG_WEIGHT = 0.3f;

        private static readonly Random _random = new();

        private readonly RecommendationService.Snapshot _snapshot;
        private readonly HashSet<string> _dealt;
        private readonly SkillModel _skill;
        private readonly Dictionary<int, (int RecordId, FeedbackFact Fact)> _answers = new();
        private TasteModel _taste;
        private int _nextToken;

        public YargProfile Profile => _snapshot.Profile;
        public int LikedCount { get; private set; }
        public int PassedCount { get; private set; }

        internal SwipeSession(RecommendationService.Snapshot snapshot)
        {
            _snapshot = snapshot;
            _dealt = new HashSet<string>(snapshot.Feedback.Select(f => f.Key));
            _skill = SkillModel.Fit(snapshot.History);
            _taste = TasteModel.Build(snapshot.Library, snapshot.History);
        }

        public List<SongEntry> NextSongs(int count)
        {
            var cards = Recommender.PickSwipeCards(_snapshot.Library, _taste, _dealt, count, _random);
            _dealt.UnionWith(cards.Select(c => c.Key));
            return cards.Select(c => _snapshot.Songs[c.Key]).ToList();
        }

        /// <summary>
        /// Songs already swiped, likely mistakes first, with the current answer for each.
        /// </summary>
        public List<(SongEntry Song, bool Liked)> RatedSongs()
        {
            return Recommender.RankLikelyMistakes(_snapshot.Library, _snapshot.History)
                .Select(r => (_snapshot.Songs[r.Key], r.Liked))
                .ToList();
        }

        /// <summary>
        /// Records a like or pass. Returns a token for <see cref="UndoSwipe"/>, valid even if saving failed.
        /// </summary>
        public int Swipe(SongEntry song, bool liked)
        {
            int recordId = RecommendationStore.RecordFeedback(Profile.Id, song.Hash.HashBytes, liked);
            var fact = new FeedbackFact { Key = song.Hash.ToString(), Liked = liked, Date = DateTime.Now };
            _snapshot.Feedback.Add(fact);
            _answers[_nextToken] = (recordId, fact);
            Count(liked, 1);
            Retrain();
            return _nextToken++;
        }

        public void UndoSwipe(int token)
        {
            if (!_answers.Remove(token, out var answer))
            {
                return;
            }

            RecommendationStore.DeleteFeedback(answer.RecordId);
            _snapshot.Feedback.Remove(answer.Fact);
            Count(answer.Fact.Liked, -1);
            Retrain();
        }

        /// <summary>
        /// Tells the model the song was just added to favorites.
        /// </summary>
        public void Favorite(SongEntry song)
        {
            _snapshot.Favorites.Add(song.Hash.ToString());
            Retrain();
        }

        public void UndoFavorite(SongEntry song)
        {
            _snapshot.Favorites.Remove(song.Hash.ToString());
            Retrain();
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

            var instrument = Profile.HasValidInstrument ? Profile.CurrentInstrument : Instrument.FiveFretGuitar;
            int intensity = song[instrument].Intensity;
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

        private void Retrain()
        {
            _taste = TasteModel.Build(_snapshot.Library, _snapshot.History);
        }
    }
}
