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

        /// <summary>
        /// Each word of the genre and subgenre, so "Pop Punk" and "Punk Rock" share "punk".
        /// </summary>
        GenreWord,
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

        /// <summary>
        /// The same song across charts and versions (for example a Harmonix and a Neversoft chart, or a
        /// prototype), so only one version is ever recommended. Defaults to <see cref="Key"/>.
        /// </summary>
        public string Identity;

        /// <summary>
        /// False for the other versions of a song that has several; only the canonical one is offered.
        /// </summary>
        public bool Canonical = true;

        public string Id => Identity ?? Key;

        public string Artist => First(FeatureType.Artist);
        public string Genre => First(FeatureType.Genre);

        private string First(FeatureType type)
        {
            foreach (var feature in Features)
            {
                if (feature.Type == type) return feature.Value;
            }

            return null;
        }
    }

    /// <summary>
    /// Turns song metadata into model features, cleaning up the inconsistencies of a big custom
    /// library: "Pop/Rock", "Pop Rock" and "pop-rock" are one genre, "Artist (Charter Name)" is the
    /// artist, and "Song (2005 Prototype)" is the same song as "Song".
    /// </summary>
    public static class SongNormalizer
    {
        private static readonly HashSet<string> GenreStopWords = new() { "and", "n", "the", "other", "of" };

        // Version tags that mark a non-definitive chart of a song
        private static readonly string[] AlternateVersionWords =
            { "demo", "prototype", "beta", "live", "remix", "rehearsal", "alt", "alternate", "cover", "karaoke" };

        // Words that make a bracketed part of a title a version note rather than part of the name
        private static readonly HashSet<string> VersionNoteWords = new(AlternateVersionWords.Concat(new[]
        {
            "version", "remaster", "remastered", "mix", "edit", "radio", "single", "album", "feat", "ft",
            "featuring", "take", "retail", "early", "original", "rerecord", "rerecorded", "mono", "stereo",
            "acoustic", "unplugged", "instrumental", "extended", "short", "full", "jan", "feb", "mar", "apr",
            "may", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        }));

        public static string Collapse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
            return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public static string StripBrackets(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var result = new System.Text.StringBuilder();
            int depth = 0;
            foreach (char c in text)
            {
                if (c is '(' or '[' or '{') { depth++; continue; }
                if (c is ')' or ']' or '}') { depth = Math.Max(0, depth - 1); continue; }
                if (depth == 0) result.Append(c);
            }

            return result.ToString();
        }

        public static string Artist(string artist)
        {
            string name = Collapse(StripBrackets(artist));
            return name.StartsWith("the ") ? name.Substring(4) : name;
        }

        /// <summary>
        /// The song's name without version notes: "(Live)", "(2005 Prototype)" and "[Remastered]" are
        /// dropped, while "(Slight Return)" or "(Parts I-V)" stay, since they are part of the name.
        /// </summary>
        public static string Title(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            var result = new System.Text.StringBuilder();
            var group = new System.Text.StringBuilder();
            int depth = 0;
            foreach (char c in title)
            {
                if (c is '(' or '[' or '{')
                {
                    if (depth == 0) group.Clear();
                    depth++;
                    continue;
                }

                if (c is ')' or ']' or '}')
                {
                    if (depth == 0) continue;
                    depth--;
                    if (depth == 0 && !IsVersionNote(group.ToString()))
                    {
                        result.Append(' ').Append(group).Append(' ');
                    }

                    continue;
                }

                (depth == 0 ? result : group).Append(c);
            }

            return Collapse(result.ToString());
        }

        private static bool IsVersionNote(string text)
        {
            return Collapse(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(w => VersionNoteWords.Contains(w) || (w.Length == 4 && w.All(char.IsDigit)));
        }

        public static string Identity(string artist, string title) => Artist(artist) + "|" + Title(title);

        /// <summary>
        /// True when the title marks this chart as a demo, prototype, live take or similar.
        /// </summary>
        public static bool IsAlternateVersion(string title)
        {
            var words = Collapse(title).Split(' ');
            var plain = Collapse(StripBrackets(title)).Split(' ');
            return words.Any(w => AlternateVersionWords.Contains(w) && !plain.Contains(w));
        }

        public static SongFeature[] Features(string artist, string genre, string subgenre, string charter,
            string source, int year, double lengthSeconds)
        {
            var features = new List<SongFeature>(12);

            void Add(FeatureType type, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    features.Add(new SongFeature(type, value));
                }
            }

            Add(FeatureType.Artist, Artist(artist));
            string genreText = Collapse(genre);
            string subgenreText = Collapse(subgenre);
            Add(FeatureType.Genre, genreText);
            Add(FeatureType.Subgenre, subgenreText);
            foreach (string word in (genreText + " " + subgenreText).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !GenreStopWords.Contains(w)).Distinct())
            {
                Add(FeatureType.GenreWord, word);
            }

            Add(FeatureType.Charter, Collapse(charter));
            Add(FeatureType.Source, Collapse(source));

            if (year > 0 && year < 3000)
            {
                Add(FeatureType.Decade, (year / 10 * 10).ToString());
            }

            if (lengthSeconds > 0)
            {
                Add(FeatureType.Length, lengthSeconds switch
                {
                    < 150 => "short",
                    < 270 => "medium",
                    < 420 => "long",
                    _     => "epic",
                });
            }

            return features.ToArray();
        }

        /// <summary>
        /// Groups a library by song identity and marks one version of each as canonical: a playable
        /// chart first, then one that is not a demo or prototype, then the shortest title.
        /// </summary>
        public static void AssignCanonical(IEnumerable<(SongFacts Facts, string Title)> songs)
        {
            foreach (var group in songs.GroupBy(s => s.Facts.Id))
            {
                var best = group
                    .OrderBy(s => s.Facts.ChartDifficulty.HasValue ? 0 : 1)
                    .ThenBy(s => IsAlternateVersion(s.Title) ? 1 : 0)
                    .ThenBy(s => s.Title.Length)
                    .First();
                foreach (var song in group)
                {
                    song.Facts.Canonical = ReferenceEquals(song.Facts, best.Facts);
                }
            }
        }
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
                { FeatureType.Artist, 0.6f },
                { FeatureType.Genre, 0.5f },
                { FeatureType.Subgenre, 0.4f },
                { FeatureType.GenreWord, 0.7f },
                { FeatureType.Decade, 0.35f },
                { FeatureType.Charter, 0.15f },
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
        private float _evidenceTotal;

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
            model._evidenceTotal = known.Sum(e => e.Value);
            model._baseline = known.Count > 0 ? BASELINE_SHARE * model._evidenceTotal / known.Count : 0f;

            foreach (var (key, evidence) in known)
            {
                foreach (var feature in library[key].Features)
                {
                    model._features.TryGetValue(feature, out var entry);
                    // Raw sums; the baseline is applied when reading, so it can be recomputed for leave-one-out
                    model._features[feature] = (entry.Sum + evidence, entry.Count + 1);
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

            return (entry.Sum - entry.Count * _baseline) / (entry.Count + SHRINKAGE);
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
        /// The score this song would get if its own evidence were not part of the model (leave-one-out).
        /// </summary>
        public float ScoreExcluding(SongFacts song)
        {
            if (!_evidence.TryGetValue(song.Key, out float own))
            {
                return Score(song);
            }

            // Recompute the baseline as if this song had no evidence, then take it out of each feature
            int others = ObservedSongCount - 1;
            float baseline = others > 0 ? BASELINE_SHARE * (_evidenceTotal - own) / others : 0f;
            return WeightedAverageByType(song.Features, feature =>
            {
                if (!_features.TryGetValue(feature, out var entry)) return 0f;
                int count = entry.Count - 1;
                return (entry.Sum - own - count * baseline) / (count + SHRINKAGE);
            });
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
        public string Identity;
        public string Artist;
        public string Genre;
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
                { RecommendationKind.ForYou, 8 },
                { RecommendationKind.AtYourLevel, 6 },
                { RecommendationKind.NextStepUp, 6 },
                { RecommendationKind.Challenge, 4 },
            };

        private const double RECENTLY_PLAYED_DAYS = 2;
        private const int MAX_PER_ARTIST = 1;
        private const int MAX_GENRE_PER_ROW = 3;
        private const int MAX_GENRE_TOTAL = 6;
        private const double SWIPE_EXPLORATION = 0.5;
        private const double SWIPE_NOISE = 0.25;
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

            string IdOf(string key) => library.TryGetValue(key, out var facts) ? facts.Id : key;

            // Everything below works on song identities, so a play or pass on one version of a song
            // applies to all of its versions
            var recentlyPlayed = new HashSet<string>(plays
                .Where(p => (now - p.Date).TotalDays < RECENTLY_PLAYED_DAYS)
                .Select(p => IdOf(p.Key)));
            // A pass hides a song only until it is actually played, matching how the taste model
            // stops counting swipes once there is real play data
            var playedKeys = new HashSet<string>(plays.Select(p => IdOf(p.Key)));
            var passed = new HashSet<string>(feedback
                .GroupBy(f => IdOf(f.Key))
                .Where(g => !g.OrderBy(f => f.Date).Last().Liked && !playedKeys.Contains(g.Key))
                .Select(g => g.Key));

            // A little randomness keeps the list fresh between visits. Discovery is a separate score that
            // adds a bigger random bonus scaled by how unsure the model is, so unexplored areas get one
            // slot without crowding out what the profile is known to enjoy.
            var discovery = new Dictionary<string, float>();
            var candidates = library.Values
                .Where(s => s.Canonical && s.ChartDifficulty.HasValue && !recentlyPlayed.Contains(s.Id) &&
                    !passed.Contains(s.Id))
                .Select(s =>
                {
                    float tasteScore = taste.Score(s) + (float) (NextGaussian(random) * FRESHNESS_NOISE);
                    discovery[s.Key] = tasteScore +
                        (float) (Math.Abs(NextGaussian(random)) * DISCOVERY_NOISE * taste.Uncertainty(s));
                    return new RecommendedSong
                    {
                        Key = s.Key,
                        Identity = s.Id,
                        Artist = s.Artist,
                        Genre = s.Genre,
                        Taste = tasteScore,
                        PredictedAccuracy = skill.PredictSong(s),
                    };
                })
                .ToList();

            var result = new RecommendationResult { Taste = taste, Skill = skill };
            var used = new HashSet<string>();
            var artistCounts = new Dictionary<string, int>();
            var genreCounts = new Dictionary<string, int>();

            // Variety rules: one version of a song; in a row, one song per artist and at most
            // MAX_GENRE_PER_ROW of one genre; across all rows, at most MAX_PER_ARTIST songs by one
            // artist and MAX_GENRE_TOTAL of one genre
            void Take(RecommendationKind kind, IEnumerable<RecommendedSong> ordered, int? count = null)
            {
                int wanted = count ?? SectionSizes[kind];
                var row = result.Songs.Where(s => s.Kind == kind).ToList();
                var rowArtists = new HashSet<string>(row.Where(s => s.Artist != null).Select(s => s.Artist));
                var rowGenres = row.Where(s => s.Genre != null).GroupBy(s => s.Genre)
                    .ToDictionary(g => g.Key, g => g.Count());
                foreach (var song in ordered)
                {
                    if (wanted <= 0) break;
                    if (used.Contains(song.Identity)) continue;
                    if (song.Artist != null)
                    {
                        artistCounts.TryGetValue(song.Artist, out int total);
                        if (rowArtists.Contains(song.Artist) || total >= MAX_PER_ARTIST) continue;
                    }

                    if (song.Genre != null)
                    {
                        rowGenres.TryGetValue(song.Genre, out int inRow);
                        genreCounts.TryGetValue(song.Genre, out int total);
                        if (inRow >= MAX_GENRE_PER_ROW || total >= MAX_GENRE_TOTAL) continue;
                        rowGenres[song.Genre] = inRow + 1;
                        genreCounts[song.Genre] = total + 1;
                    }

                    if (song.Artist != null)
                    {
                        rowArtists.Add(song.Artist);
                        artistCounts.TryGetValue(song.Artist, out int total);
                        artistCounts[song.Artist] = total + 1;
                    }

                    song.Kind = kind;
                    used.Add(song.Identity);
                    result.Songs.Add(song);
                    wanted--;
                }
            }

            // For You mixes one known favorite, the best-matching songs not played yet, and one discovery
            var playableForYou = candidates
                .Where(s => s.PredictedAccuracy >= STRETCH)
                .OrderByDescending(s => s.Taste)
                .ToList();
            int forYouSize = SectionSizes[RecommendationKind.ForYou];
            Take(RecommendationKind.ForYou, playableForYou.Where(s => playedKeys.Contains(s.Identity)), 1);
            Take(RecommendationKind.ForYou, playableForYou.Where(s => !playedKeys.Contains(s.Identity)), forYouSize - 2);
            Take(RecommendationKind.ForYou, playableForYou.OrderByDescending(s => discovery[s.Key]), 1);
            Take(RecommendationKind.ForYou, playableForYou,
                forYouSize - result.Songs.Count(s => s.Kind == RecommendationKind.ForYou));

            Take(RecommendationKind.AtYourLevel, candidates
                .Where(s => s.PredictedAccuracy >= AT_LEVEL)
                .OrderByDescending(s => s.Taste));

            // The ladder: the best-liked songs just above the comfort zone that have not been
            // mastered yet, shown easiest first
            Take(RecommendationKind.NextStepUp, candidates
                .Where(s => s.PredictedAccuracy >= STRETCH && s.PredictedAccuracy < AT_LEVEL &&
                    (skill.BestAccuracy(s.Key) ?? 0f) < AT_LEVEL)
                .OrderByDescending(s => s.Taste));
            var ladder = result.Songs.Where(s => s.Kind == RecommendationKind.NextStepUp)
                .OrderByDescending(s => s.PredictedAccuracy).ToList();
            result.Songs.RemoveAll(s => s.Kind == RecommendationKind.NextStepUp);
            result.Songs.AddRange(ladder);

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
            // Anything already seen, rated or played counts for every version of the song
            var seen = new HashSet<string>(exclude.Concat(taste.Evidence.Keys)
                .Select(k => library.TryGetValue(k, out var facts) ? facts.Id : k));
            var pool = library.Values
                .Where(s => s.Canonical && s.ChartDifficulty.HasValue && !seen.Contains(s.Id))
                .ToList();
            if (pool.Count == 0)
            {
                // Nothing playable on the current instrument, fall back to the whole library
                pool = library.Values
                    .Where(s => s.Canonical && !seen.Contains(s.Id))
                    .ToList();
            }

            // Scoring thousands of songs per pick is cheap, but cap the pool to keep swipes instant
            if (pool.Count > 4000)
            {
                pool = pool.OrderBy(_ => random.Next()).Take(4000).ToList();
            }

            var picked = new List<string>();
            var pickedArtists = new HashSet<string>();
            var extra = new Dictionary<SongFeature, int>();
            var available = new HashSet<SongFacts>(pool);
            while (picked.Count < count && available.Count > 0)
            {
                SongFacts best = null;
                double bestScore = double.MinValue;
                foreach (var song in available)
                {
                    // Never two songs by the same artist in one batch
                    if (song.Artist != null && pickedArtists.Contains(song.Artist))
                    {
                        continue;
                    }

                    // Mostly songs the profile will probably enjoy, with a push toward unexplored
                    // corners of the library so each batch still teaches the model something
                    double score = taste.Score(song) + SWIPE_EXPLORATION * taste.Uncertainty(song, extra) +
                        SWIPE_NOISE * NextGumbel(random);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = song;
                    }
                }

                if (best == null)
                {
                    break;
                }

                available.Remove(best);
                picked.Add(best.Key);
                if (best.Artist != null)
                {
                    pickedArtists.Add(best.Artist);
                }

                foreach (var feature in best.Features)
                {
                    extra.TryGetValue(feature, out int n);
                    extra[feature] = n + 1;
                }
            }

            return picked;
        }

        /// <summary>
        /// Orders a profile's swipes by how much each one disagrees with everything else the profile has
        /// done, so accidental likes and passes can be reviewed first. Each swipe is judged by a taste
        /// model built without it.
        /// </summary>
        public static List<(string Key, bool Liked, float Surprise)> RankLikelyMistakes(
            IReadOnlyDictionary<string, SongFacts> library,
            IReadOnlyList<PlayFact> plays,
            IReadOnlyList<FeedbackFact> feedback,
            IReadOnlyList<QuitFact> quits,
            IReadOnlyCollection<string> favorites,
            DateTime now)
        {
            var latest = feedback
                .Where(f => f.Key != null && library.ContainsKey(f.Key))
                .GroupBy(f => f.Key)
                .Select(g => g.OrderBy(f => f.Date).Last())
                .ToList();

            // One model for everything, then each swipe is scored with its own song's evidence taken out
            var taste = TasteModel.Build(library, plays, feedback, quits, favorites, now);
            var result = new List<(string, bool, float)>();
            foreach (var swipe in latest)
            {
                float score = taste.ScoreExcluding(library[swipe.Key]);
                result.Add((swipe.Key, swipe.Liked, swipe.Liked ? -score : score));
            }

            return result.OrderByDescending(r => r.Item3).ToList();
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
