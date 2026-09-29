using System;
using System.Collections.Generic;
using System.Linq;

// This file intentionally has no Unity or YARG dependencies so the model can be tested on its own.
namespace YARG.Recommendations
{
    public enum FeatureType
    {
        Artist,
        Genre,
        Subgenre,
        Decade,
        Charter,
        Source,
        Length,
    }

    public readonly struct SongFeature : IEquatable<SongFeature>
    {
        public readonly FeatureType Type;
        public readonly string Value;

        public SongFeature(FeatureType type, string value)
        {
            Type = type;
            Value = value;
        }

        public bool Equals(SongFeature other) => Type == other.Type && Value == other.Value;
        public override bool Equals(object obj) => obj is SongFeature other && Equals(other);
        public override int GetHashCode() => ((int) Type * 397) ^ (Value?.GetHashCode() ?? 0);
        public override string ToString() => $"{Type}:{Value}";
    }

    /// <summary>
    /// What the model knows about a song in the library.
    /// </summary>
    public sealed class SongFacts
    {
        public string Key;
        public SongFeature[] Features;

        /// <summary>
        /// Chart difficulty on the profile's current instrument and difficulty, or null if the song
        /// cannot be played that way. See <see cref="SkillModel.ChartDifficulty"/>.
        /// </summary>
        public float? ChartDifficulty;
    }

    public sealed class PlayFact
    {
        public string Key;
        public DateTime Date;
        public float Accuracy;

        /// <summary>
        /// True when the play was on the instrument the skill model is being fitted for.
        /// </summary>
        public bool OnCurrentInstrument;

        /// <summary>
        /// Chart difficulty of the part that was played, adjusted for song speed. Null when unknown.
        /// </summary>
        public float? ChartDifficulty;

        /// <summary>
        /// True when the play was on the profile's current difficulty.
        /// </summary>
        public bool OnCurrentDifficulty;

        public float SongSpeed = 1f;
    }

    public sealed class FeedbackFact
    {
        public string Key;
        public bool Liked;
        public DateTime Date;
    }

    public sealed class QuitFact
    {
        public string Key;
        public float Progress;
        public DateTime Date;
    }

    /// <summary>
    /// Learns which artists, genres, decades and so on a profile enjoys, from how it has actually behaved.
    /// </summary>
    /// <remarks>
    /// Each song the profile has touched gets an "enjoyment evidence" number. Coming back to a song on
    /// another day is the strongest signal, especially after a poor score. Finishing a song once is only
    /// a weak signal, and a low score is never counted against a song (that is the skill model's job).
    /// Swipe feedback only counts for songs that have never been played; real plays replace it.
    /// A feature's affinity is the evidence of its songs, shrunk toward zero when there are few of them.
    /// </remarks>
    public sealed class TasteModel
    {
        public static readonly IReadOnlyDictionary<FeatureType, float> FeatureWeights =
            new Dictionary<FeatureType, float>
            {
                { FeatureType.Artist, 1.0f },
                { FeatureType.Genre, 0.8f },
                { FeatureType.Subgenre, 0.6f },
                { FeatureType.Decade, 0.35f },
                { FeatureType.Charter, 0.2f },
                { FeatureType.Source, 0.15f },
                { FeatureType.Length, 0.1f },
            };

        private const float SHRINKAGE = 2f;

        private const float FIRST_PLAY = 0.4f;
        private const float RETURN_VISIT = 1.0f;
        private const float RETURN_AFTER_BAD_SCORE = 0.5f;
        private const float BAD_SCORE = 0.85f;
        private const int MAX_COUNTED_RETURNS = 4;
        private const float FAVORITE = 1.5f;
        private const float SWIPE_LIKE = 1.0f;
        private const float SWIPE_PASS = -0.8f;
        private const float EARLY_QUIT = -0.6f;
        private const float LATE_QUIT = -0.2f;
        private const double RECENCY_HALF_LIFE_DAYS = 90;

        private const float BASELINE_SHARE = 0.5f;

        private readonly Dictionary<SongFeature, (float Sum, int Count)> _features = new();
        private readonly Dictionary<string, float> _evidence = new();
        private float _baseline;

        public IReadOnlyDictionary<string, float> Evidence => _evidence;

        /// <summary>
        /// Songs in the library that have any evidence (played, swiped, quit or favorited).
        /// </summary>
        public int ObservedSongCount { get; private set; }

        public static TasteModel Build(
            IReadOnlyDictionary<string, SongFacts> library,
            IEnumerable<PlayFact> plays,
            IEnumerable<FeedbackFact> feedback,
            IEnumerable<QuitFact> quits,
            IEnumerable<string> favorites,
            DateTime now)
        {
            var model = new TasteModel();

            var playsBySong = plays
                .Where(p => p.Key != null)
                .GroupBy(p => p.Key)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Date).ToList());

            foreach (var (key, songPlays) in playsBySong)
            {
                var days = songPlays
                    .GroupBy(p => p.Date.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => (Day: g.Key, Best: g.Max(p => p.Accuracy)))
                    .ToList();

                float evidence = FIRST_PLAY;
                int returns = Math.Min(days.Count - 1, MAX_COUNTED_RETURNS);
                evidence += returns * RETURN_VISIT;

                for (int i = 1; i <= returns; i++)
                {
                    if (days[i - 1].Best < BAD_SCORE)
                    {
                        evidence += RETURN_AFTER_BAD_SCORE;
                    }
                }

                double daysSinceLast = (now - songPlays[^1].Date).TotalDays;
                evidence *= (float) (0.5 + 0.5 * Math.Pow(0.5, Math.Max(0, daysSinceLast) / RECENCY_HALF_LIFE_DAYS));

                model.AddEvidence(key, evidence);
            }

            foreach (var quit in quits.Where(q => q.Key != null))
            {
                model.AddEvidence(quit.Key, quit.Progress < 0.5f ? EARLY_QUIT : LATE_QUIT);
            }

            foreach (var key in favorites.Where(k => k != null))
            {
                model.AddEvidence(key, FAVORITE);
            }

            // The latest swipe on a song wins, and only counts until the song is actually played
            var latestFeedback = feedback
                .Where(f => f.Key != null)
                .GroupBy(f => f.Key)
                .Select(g => g.OrderBy(f => f.Date).Last());
            foreach (var swipe in latestFeedback)
            {
                if (playsBySong.ContainsKey(swipe.Key))
                {
                    continue;
                }

                model.AddEvidence(swipe.Key, swipe.Liked ? SWIPE_LIKE : SWIPE_PASS);
            }

            // Features are judged partly against the profile's own average, so a genre that was tried
            // once and never revisited ends up below the genres the profile keeps returning to, while
            // a profile that only ever plays one genre still rates that genre above untried ones
            var known = model._evidence.Where(e => library.ContainsKey(e.Key)).ToList();
            model._baseline = known.Count > 0 ? BASELINE_SHARE * known.Average(e => e.Value) : 0f;

            foreach (var (key, evidence) in known)
            {
                foreach (var feature in library[key].Features)
                {
                    model._features.TryGetValue(feature, out var entry);
                    model._features[feature] = (entry.Sum + evidence - model._baseline, entry.Count + 1);
                }
            }

            model.ObservedSongCount = known.Count;
            return model;
        }

        private void AddEvidence(string key, float amount)
        {
            _evidence.TryGetValue(key, out float current);
            _evidence[key] = current + amount;
        }

        public float Affinity(SongFeature feature)
        {
            if (!_features.TryGetValue(feature, out var entry))
            {
                return 0f;
            }

            return entry.Sum / (entry.Count + SHRINKAGE);
        }

        public int ObservationCount(SongFeature feature)
        {
            return _features.TryGetValue(feature, out var entry) ? entry.Count : 0;
        }

        /// <summary>
        /// How much the profile is expected to enjoy a song. Zero means no opinion either way.
        /// </summary>
        public float Score(SongFacts song)
        {
            return WeightedAverageByType(song.Features, Affinity);
        }

        /// <summary>
        /// How little the model knows about songs like this one. High for untried genres and artists.
        /// </summary>
        public float Uncertainty(SongFacts song, IReadOnlyDictionary<SongFeature, int> extraObservations = null)
        {
            return WeightedAverageByType(song.Features, f =>
            {
                int count = ObservationCount(f);
                if (extraObservations != null && extraObservations.TryGetValue(f, out int extra))
                {
                    count += extra;
                }

                return (float) (1.0 / Math.Sqrt(1 + count));
            });
        }

        private static readonly int FeatureTypeCount = Enum.GetValues(typeof(FeatureType)).Length;

        /// <summary>
        /// Averages a value over the features of each type, then sums the per-type averages by weight.
        /// Written without LINQ because Song Swipe calls it for thousands of songs per pick.
        /// </summary>
        private static float WeightedAverageByType(SongFeature[] features, Func<SongFeature, float> value)
        {
            Span<float> sums = stackalloc float[FeatureTypeCount];
            Span<int> counts = stackalloc int[FeatureTypeCount];
            foreach (var feature in features)
            {
                sums[(int) feature.Type] += value(feature);
                counts[(int) feature.Type]++;
            }

            float total = 0f;
            for (int i = 0; i < FeatureTypeCount; i++)
            {
                if (counts[i] > 0)
                {
                    total += FeatureWeights[(FeatureType) i] * sums[i] / counts[i];
                }
            }

            return total;
        }

        public IEnumerable<(SongFeature Feature, float Affinity, int Count)> TopFeatures(int count)
        {
            // A feature every observed song shares (one charter, one source) says nothing about taste
            return _features
                .Where(kv => kv.Value.Count < ObservedSongCount || ObservedSongCount <= 1)
                .Select(kv => (kv.Key, Affinity(kv.Key), kv.Value.Count))
                .OrderByDescending(f => Math.Abs(f.Item2))
                .Take(count);
        }
    }

    /// <summary>
    /// Estimates how accurately a profile will play a chart, from its past accuracy on its current instrument.
    /// </summary>
    /// <remarks>
    /// Every chart gets a difficulty number: the song's intensity tier for the instrument (0 to 6),
    /// shifted by the difficulty level (Expert is 0, Hard is -1.5, and so on) and by song speed.
    /// The player gets a skill number on the same scale, fitted to their results. A chart whose
    /// difficulty equals the player's skill is predicted at about 77% accuracy, two points below at
    /// about 95%, two points above at about 60%.
    /// </remarks>
    public sealed class SkillModel
    {
        public const float UNKNOWN_INTENSITY = 3f;

        private const float SLOPE = 1.1f;
        private const float MAX_LOSS = 0.45f;
        private const float PRIOR_WEIGHT = 1f;
        private const double RECENCY_HALF_LIFE_DAYS = 60;
        private const float OWN_RESULT_WEIGHT = 0.6f;

        public float Skill { get; private set; }
        public int PlayCount { get; private set; }

        private readonly Dictionary<string, float> _lastAccuracy = new();
        private readonly Dictionary<string, float> _bestAccuracy = new();

        public static float DifficultyOffset(int difficulty)
        {
            // Matches YARG.Core.Difficulty: Beginner, Easy, Medium, Hard, Expert, ExpertPlus
            return difficulty switch
            {
                0 => -6f,
                1 => -4.5f,
                2 => -3f,
                3 => -1.5f,
                4 => 0f,
                5 => 0.5f,
                _ => 0f,
            };
        }

        public static float ChartDifficulty(int intensity, int difficulty, float songSpeed = 1f)
        {
            float tier = intensity < 0 ? UNKNOWN_INTENSITY : intensity;
            float speed = songSpeed > 0f ? songSpeed : 1f;
            return tier + DifficultyOffset(difficulty) + 2f * (float) Math.Log(speed, 2);
        }

        public static float Predict(float skill, float chartDifficulty)
        {
            return 1f - MAX_LOSS / (1f + (float) Math.Exp(-SLOPE * (chartDifficulty - skill)));
        }

        public static SkillModel Fit(IEnumerable<PlayFact> plays, int currentDifficulty, DateTime now)
        {
            var model = new SkillModel();

            // Before any plays, assume a player on a given difficulty handles a mid-tier chart there
            float prior = 4f + DifficultyOffset(currentDifficulty);

            var usable = plays
                .Where(p => p.OnCurrentInstrument && p.ChartDifficulty.HasValue)
                .OrderBy(p => p.Date)
                .ToList();
            model.PlayCount = usable.Count;

            // Own results are reused directly only when they were played at normal speed. Other speeds
            // still inform the skill fit through their speed-adjusted chart difficulty.
            foreach (var play in usable.Where(p => p.OnCurrentDifficulty && Math.Abs(p.SongSpeed - 1f) < 0.01f))
            {
                model._lastAccuracy[play.Key] = play.Accuracy;
                model._bestAccuracy.TryGetValue(play.Key, out float best);
                model._bestAccuracy[play.Key] = Math.Max(best, play.Accuracy);
            }

            var weights = usable
                .Select(p => 0.1 + Math.Pow(0.5, Math.Max(0, (now - p.Date).TotalDays) / RECENCY_HALF_LIFE_DAYS))
                .ToArray();

            float bestSkill = prior;
            double bestError = double.MaxValue;
            for (float skill = -8f; skill <= 12f; skill += 0.05f)
            {
                double error = PRIOR_WEIGHT * (skill - prior) * (skill - prior) * 0.01;
                for (int i = 0; i < usable.Count; i++)
                {
                    double diff = usable[i].Accuracy - Predict(skill, usable[i].ChartDifficulty!.Value);
                    error += weights[i] * diff * diff;
                }

                if (error < bestError)
                {
                    bestError = error;
                    bestSkill = skill;
                }
            }

            model.Skill = bestSkill;
            return model;
        }

        /// <summary>
        /// Predicted accuracy for a song on the current instrument and difficulty. Songs already played
        /// that way lean on the player's own last result.
        /// </summary>
        public float PredictSong(SongFacts song)
        {
            if (!song.ChartDifficulty.HasValue)
            {
                return 0f;
            }

            float predicted = Predict(Skill, song.ChartDifficulty.Value);
            if (_lastAccuracy.TryGetValue(song.Key, out float last))
            {
                predicted = OWN_RESULT_WEIGHT * last + (1f - OWN_RESULT_WEIGHT) * predicted;
            }

            return predicted;
        }

        public float? BestAccuracy(string key)
        {
            return _bestAccuracy.TryGetValue(key, out float best) ? best : null;
        }
    }

    public enum RecommendationKind
    {
        ForYou,
        AtYourLevel,
        NextStepUp,
        Challenge,
    }

    public sealed class RecommendedSong
    {
        public string Key;
        public RecommendationKind Kind;
        public float Taste;
        public float PredictedAccuracy;
    }

    public sealed class RecommendationResult
    {
        public TasteModel Taste;
        public SkillModel Skill;
        public List<RecommendedSong> Songs = new();

        public IEnumerable<RecommendedSong> OfKind(RecommendationKind kind) => Songs.Where(s => s.Kind == kind);
    }

    public static class Recommender
    {
        public const float AT_LEVEL = 0.95f;
        public const float STRETCH = 0.85f;
        public const float CHALLENGE = 0.65f;

        public static readonly IReadOnlyDictionary<RecommendationKind, int> SectionSizes =
            new Dictionary<RecommendationKind, int>
            {
                { RecommendationKind.ForYou, 4 },
                { RecommendationKind.AtYourLevel, 3 },
                { RecommendationKind.NextStepUp, 3 },
                { RecommendationKind.Challenge, 2 },
            };

        private const double RECENTLY_PLAYED_DAYS = 2;
        private const double FRESHNESS_NOISE = 0.05;
        private const double DISCOVERY_NOISE = 0.5;

        public static RecommendationResult Recommend(
            IReadOnlyDictionary<string, SongFacts> library,
            IReadOnlyList<PlayFact> plays,
            IReadOnlyList<FeedbackFact> feedback,
            IReadOnlyList<QuitFact> quits,
            IReadOnlyCollection<string> favorites,
            int currentDifficulty,
            DateTime now,
            Random random)
        {
            var taste = TasteModel.Build(library, plays, feedback, quits, favorites, now);
            var skill = SkillModel.Fit(plays, currentDifficulty, now);

            var recentlyPlayed = new HashSet<string>(plays
                .Where(p => (now - p.Date).TotalDays < RECENTLY_PLAYED_DAYS)
                .Select(p => p.Key));
            // A pass hides a song only until it is actually played, matching how the taste model
            // stops counting swipes once there is real play data
            var playedKeys = new HashSet<string>(plays.Select(p => p.Key));
            var passed = new HashSet<string>(feedback
                .GroupBy(f => f.Key)
                .Where(g => !g.OrderBy(f => f.Date).Last().Liked && !playedKeys.Contains(g.Key))
                .Select(g => g.Key));

            // A little randomness keeps the list fresh between visits. Discovery is a separate score that
            // adds a bigger random bonus scaled by how unsure the model is, so unexplored areas get one
            // slot without crowding out what the profile is known to enjoy.
            var discovery = new Dictionary<string, float>();
            var candidates = library.Values
                .Where(s => s.ChartDifficulty.HasValue && !recentlyPlayed.Contains(s.Key) && !passed.Contains(s.Key))
                .Select(s =>
                {
                    float tasteScore = taste.Score(s) + (float) (NextGaussian(random) * FRESHNESS_NOISE);
                    discovery[s.Key] = tasteScore +
                        (float) (Math.Abs(NextGaussian(random)) * DISCOVERY_NOISE * taste.Uncertainty(s));
                    return new RecommendedSong
                    {
                        Key = s.Key,
                        Taste = tasteScore,
                        PredictedAccuracy = skill.PredictSong(s),
                    };
                })
                .ToList();

            var result = new RecommendationResult { Taste = taste, Skill = skill };
            var used = new HashSet<string>();

            void Take(RecommendationKind kind, IEnumerable<RecommendedSong> ordered, int? count = null)
            {
                foreach (var song in ordered.Where(s => !used.Contains(s.Key)).Take(count ?? SectionSizes[kind]))
                {
                    song.Kind = kind;
                    used.Add(song.Key);
                    result.Songs.Add(song);
                }
            }

            // For You mixes one known favorite, the best-matching songs not played yet, and one discovery
            var playableForYou = candidates
                .Where(s => s.PredictedAccuracy >= STRETCH)
                .OrderByDescending(s => s.Taste)
                .ToList();
            int forYouSize = SectionSizes[RecommendationKind.ForYou];
            Take(RecommendationKind.ForYou, playableForYou.Where(s => playedKeys.Contains(s.Key)), 1);
            Take(RecommendationKind.ForYou, playableForYou.Where(s => !playedKeys.Contains(s.Key)), forYouSize - 2);
            Take(RecommendationKind.ForYou, playableForYou.OrderByDescending(s => discovery[s.Key]), 1);
            Take(RecommendationKind.ForYou, playableForYou,
                forYouSize - result.Songs.Count(s => s.Kind == RecommendationKind.ForYou));

            Take(RecommendationKind.AtYourLevel, candidates
                .Where(s => s.PredictedAccuracy >= AT_LEVEL)
                .OrderByDescending(s => s.Taste));

            // The ladder: among well-liked songs just above the comfort zone that have not been
            // mastered yet, offer the easiest ones first
            Take(RecommendationKind.NextStepUp, candidates
                .Where(s => s.PredictedAccuracy >= STRETCH && s.PredictedAccuracy < AT_LEVEL &&
                    (skill.BestAccuracy(s.Key) ?? 0f) < AT_LEVEL)
                .OrderByDescending(s => s.Taste)
                .Take(SectionSizes[RecommendationKind.NextStepUp] * 3)
                .OrderByDescending(s => s.PredictedAccuracy));

            Take(RecommendationKind.Challenge, candidates
                .Where(s => s.PredictedAccuracy >= CHALLENGE && s.PredictedAccuracy < STRETCH)
                .OrderByDescending(s => s.Taste));

            return result;
        }

        /// <summary>
        /// Picks songs for Song Swipe. Favors songs from genres and artists the model knows least about,
        /// spreading picks out so one swipe session covers a lot of ground.
        /// </summary>
        public static List<string> PickSwipeCandidates(
            IReadOnlyDictionary<string, SongFacts> library,
            TasteModel taste,
            ISet<string> exclude,
            int count,
            Random random)
        {
            var pool = library.Values
                .Where(s => s.ChartDifficulty.HasValue && !exclude.Contains(s.Key) && !taste.Evidence.ContainsKey(s.Key))
                .ToList();
            if (pool.Count == 0)
            {
                // Nothing playable on the current instrument, fall back to the whole library
                pool = library.Values
                    .Where(s => !exclude.Contains(s.Key) && !taste.Evidence.ContainsKey(s.Key))
                    .ToList();
            }

            // Scoring thousands of songs per pick is cheap, but cap the pool to keep swipes instant
            if (pool.Count > 4000)
            {
                pool = pool.OrderBy(_ => random.Next()).Take(4000).ToList();
            }

            var picked = new List<string>();
            var extra = new Dictionary<SongFeature, int>();
            var available = new HashSet<SongFacts>(pool);
            while (picked.Count < count && available.Count > 0)
            {
                SongFacts best = null;
                double bestScore = double.MinValue;
                foreach (var song in available)
                {
                    double score = taste.Uncertainty(song, extra) + 0.4 * taste.Score(song) +
                        0.3 * NextGumbel(random);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = song;
                    }
                }

                available.Remove(best);
                picked.Add(best!.Key);
                foreach (var feature in best.Features)
                {
                    extra.TryGetValue(feature, out int n);
                    extra[feature] = n + 1;
                }
            }

            return picked;
        }

        private static double NextGaussian(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static double NextGumbel(Random random)
        {
            return -Math.Log(-Math.Log(1.0 - random.NextDouble() * 0.999999));
        }
    }
}
