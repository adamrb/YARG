using System.Linq;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Song;
using YARG.Helpers.Extensions;

namespace YARG.Menu.MusicLibrary
{
    /// <summary>
    /// One song card in Song Swipe: album art (or a genre-colored placeholder), the song's details, why it
    /// was dealt, and the stamps shown as it is thrown.
    /// </summary>
    public class SongSwipeCard : MonoBehaviour
    {
        private const float REASON_PADDING = 36f;
        private const float MAX_REASON_WIDTH = 508f;

        [SerializeField]
        private CanvasGroup _group;
        [SerializeField]
        private Image _placeholder;
        [SerializeField]
        private TextMeshProUGUI _placeholderText;
        [SerializeField]
        private RawImage _art;
        [SerializeField]
        private RectTransform _reasonBadge;
        [SerializeField]
        private TextMeshProUGUI _reasonText;
        [SerializeField]
        private TextMeshProUGUI _title;
        [SerializeField]
        private TextMeshProUGUI _artist;
        [SerializeField]
        private TextMeshProUGUI _details;
        [SerializeField]
        private TextMeshProUGUI _difficulty;
        [SerializeField]
        private CanvasGroup _likeStamp;
        [SerializeField]
        private CanvasGroup _passStamp;
        [SerializeField]
        private CanvasGroup _favoriteStamp;

        private CancellationTokenSource _artLoad;

        public SongEntry Song { get; private set; }
        public RectTransform Rect => (RectTransform) transform;
        public CanvasGroup Group => _group;

        public void Show(SongSwipeView.CardInfo info)
        {
            var song = info.Song;
            Song = song;
            _placeholder.color = GenreColor(song.Genre.Original);
            _placeholderText.text = song.Genre.Original;
            _title.text = song.Name.Original;
            _artist.text = song.Artist.Original;
            _details.text = string.Join("   ·   ", new[] { song.Album.Original, song.ParsedYear, song.Genre.Original }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            _art.color = Color.clear;
            _artLoad = new CancellationTokenSource();
            _art.LoadAlbumCover(song, _artLoad.Token);
            SetInfo(info);
        }

        /// <summary>
        /// Updates the reason and difficulty lines, which change as the model learns.
        /// </summary>
        public void SetInfo(SongSwipeView.CardInfo info)
        {
            bool hasReason = !string.IsNullOrEmpty(info.Reason);
            _reasonBadge.gameObject.SetActive(hasReason);
            if (hasReason)
            {
                // The badge hugs its text, up to the width of the art
                _reasonText.text = info.Reason;
                float width = _reasonText.GetPreferredValues(info.Reason).x + REASON_PADDING;
                _reasonBadge.sizeDelta = new Vector2(Mathf.Min(width, MAX_REASON_WIDTH), _reasonBadge.sizeDelta.y);
            }

            _difficulty.text = info.Difficulty ?? string.Empty;
        }

        public CanvasGroup Stamp(SwipeDirection direction) => direction switch
        {
            SwipeDirection.Like     => _likeStamp,
            SwipeDirection.Pass     => _passStamp,
            SwipeDirection.Favorite => _favoriteStamp,
            _                       => null,
        };

        private void OnDestroy()
        {
            _artLoad?.Cancel();
            _artLoad?.Dispose();
            if (_art.texture != null)
            {
                Destroy(_art.texture);
            }
        }

        private static Color GenreColor(string genre)
        {
            float hue = Mathf.Abs((genre ?? string.Empty).ToLowerInvariant().GetHashCode() % 1000) / 1000f;
            return Color.HSVToRGB(hue, 0.5f, 0.45f);
        }
    }
}
