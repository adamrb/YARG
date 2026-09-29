using System;
using System.Linq;
using System.Threading;
using DG.Tweening;
using Nobi.UiRoundedCorners;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Data;

namespace YARG.Menu.MusicLibrary
{
    public enum SwipeDirection
    {
        Like,
        Pass,
        Skip,
        Favorite,
    }

    /// <summary>
    /// The Song Swipe screen: a stack of song cards over the music library. The front card flies right
    /// when liked, left when passed, down when skipped and up when favorited, and the card behind it
    /// moves forward. Built in code so it needs no prefab changes.
    /// </summary>
    public class SongSwipeView : MonoBehaviour
    {
        public struct CardInfo
        {
            public SongEntry Song;
            public string Reason;
            public string Difficulty;
        }

        private const float CARD_WIDTH = 600f;
        private const float CARD_HEIGHT = 820f;
        private const float ART_SIZE = 540f;
        private const float BACK_SCALE = 0.93f;
        private const float BACK_OFFSET = -64f;
        private const int SORTING_ORDER = 108;
        private const float HELP_BAR_HEIGHT = 75f;
        private const float BACK_ALPHA = 0.5f;
        private const float FLY_DURATION = 0.3f;

        private static readonly Color CardColor = new(0.07f, 0.11f, 0.2f, 1f);
        private static readonly Color MutedText = new(0.62f, 0.68f, 0.78f, 1f);

        private TMP_FontAsset _font;
        private RectTransform _stack;
        private TextMeshProUGUI _counter;
        private Card _front;
        private Card _back;
        private Card _thrown;
        private Sequence _animation;

        public event Action<SwipeDirection> ButtonClicked;

        public bool HasCard => _front != null;

        private sealed class Card
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public RawImage Art;
            public CanvasGroup LikeStamp;
            public CanvasGroup PassStamp;
            public CanvasGroup FavoriteStamp;
            public CancellationTokenSource ArtLoad;
            public SongEntry Song;

            public void Destroy()
            {
                ArtLoad?.Cancel();
                ArtLoad?.Dispose();
                if (Art != null && Art.texture != null)
                {
                    UnityEngine.Object.Destroy(Art.texture);
                }

                UnityEngine.Object.Destroy(Root.gameObject);
            }
        }

        public static SongSwipeView Create(Transform parent, TMP_FontAsset font)
        {
            var root = NewRect("SongSwipe", parent);
            Stretch(root);
            root.SetAsLastSibling();

            // Own canvas so the whole library (including its separately sorted search bar and sidebar)
            // sits underneath, while the persistent help bar and toasts stay on top
            var canvas = root.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = SORTING_ORDER;
            root.gameObject.AddComponent<GraphicRaycaster>();

            var view = root.gameObject.AddComponent<SongSwipeView>();
            view._font = font;
            view.Build(root);
            return view;
        }

        private void Build(RectTransform root)
        {
            // Dim the library and block clicks from reaching it
            var backdrop = NewImage(root, "Backdrop", new Color(0.02f, 0.03f, 0.07f, 1f), 0f);
            Stretch(backdrop.rectTransform);
            backdrop.rectTransform.offsetMin = new Vector2(0f, HELP_BAR_HEIGHT);

            var title = NewText(root, "Title", Localize.Key("Menu.MusicLibrary.SongSwipe.Header"), 64,
                Color.white, FontStyles.Bold, TextAlignmentOptions.TopLeft);
            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(80f, -60f), new Vector2(900f, 80f));

            var subtitle = NewText(root, "Subtitle", Localize.Key("Menu.MusicLibrary.SongSwipe.HeaderHelp"), 28,
                MutedText, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(82f, -140f), new Vector2(900f, 40f));

            _counter = NewText(root, "Counter", string.Empty, 32, Color.white, FontStyles.Bold,
                TextAlignmentOptions.TopRight);
            Place(_counter.rectTransform, new Vector2(1f, 1f), new Vector2(-80f, -70f), new Vector2(700f, 50f));

            _stack = NewRect("Stack", root);
            Place(_stack, new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(CARD_WIDTH, CARD_HEIGHT));

            // Big round buttons either side of the card, for mouse and touch
            NewRoundButton(root, "PassButton", Localize.Key("Menu.MusicLibrary.SongSwipe.Pass"),
                MenuData.Colors.NavigationRed, new Vector2(-CARD_WIDTH / 2f - 170f, 60f), SwipeDirection.Pass);
            NewRoundButton(root, "LikeButton", Localize.Key("Menu.MusicLibrary.SongSwipe.Like"),
                MenuData.Colors.NavigationGreen, new Vector2(CARD_WIDTH / 2f + 170f, 60f), SwipeDirection.Like);
        }

        public void SetCounts(int liked, int passed)
        {
            _counter.text = Localize.KeyFormat("Menu.MusicLibrary.SongSwipe.Counts", liked, passed);
        }

        /// <summary>
        /// Shows a fresh stack. Replaces any cards currently shown.
        /// </summary>
        public void SetCards(CardInfo front, CardInfo? back)
        {
            CompleteAnimation();
            _front?.Destroy();
            _back?.Destroy();
            _back = null;

            if (back.HasValue)
            {
                _back = CreateCard(back.Value);
                PlaceAsBack(_back);
            }

            _front = CreateCard(front);
        }

        /// <summary>
        /// Throws the front card off screen, brings <paramref name="newFront"/> forward (reusing the back card
        /// when it already shows that song) and adds <paramref name="newBack"/> behind it.
        /// <paramref name="onFrontChanged"/> runs once the new front card is in place.
        /// </summary>
        public void Throw(SwipeDirection direction, CardInfo? newFront, CardInfo? newBack, Action onFrontChanged)
        {
            CompleteAnimation();
            if (_front == null)
            {
                return;
            }

            var thrown = _front;
            _thrown = thrown;

            var promoted = _back;
            if (promoted != null && (!newFront.HasValue || promoted.Song != newFront.Value.Song))
            {
                promoted.Destroy();
                promoted = null;
            }

            if (promoted == null && newFront.HasValue)
            {
                promoted = CreateCard(newFront.Value);
                PlaceAsBack(promoted);
                promoted.Root.SetSiblingIndex(thrown.Root.GetSiblingIndex());
            }

            _front = promoted;
            _back = newBack.HasValue ? CreateCard(newBack.Value) : null;
            if (_back != null)
            {
                PlaceAsBack(_back);
                _back.Root.SetAsFirstSibling();
                _back.Group.alpha = 0f;
            }

            var (target, rotation, stamp) = direction switch
            {
                SwipeDirection.Like     => (new Vector2(1500f, -140f), -22f, thrown.LikeStamp),
                SwipeDirection.Pass     => (new Vector2(-1500f, -140f), 22f, thrown.PassStamp),
                SwipeDirection.Favorite => (new Vector2(0f, 1400f), 0f, thrown.FavoriteStamp),
                _                       => (new Vector2(0f, -1400f), 0f, null),
            };

            _animation = DOTween.Sequence().SetUpdate(true);
            _animation.Join(TweenPosition(thrown.Root, target).SetEase(Ease.InQuad));
            _animation.Join(thrown.Root.DOLocalRotate(new Vector3(0f, 0f, rotation), FLY_DURATION));
            if (stamp != null)
            {
                _animation.Insert(0f, TweenAlpha(stamp, 1f, FLY_DURATION * 0.4f));
            }
            else
            {
                _animation.Insert(0f, TweenAlpha(thrown.Group, 0f, FLY_DURATION));
            }

            if (promoted != null)
            {
                _animation.Insert(0f, TweenPosition(promoted.Root, Vector2.zero));
                _animation.Insert(0f, promoted.Root.DOScale(1f, FLY_DURATION));
                _animation.Insert(0f, TweenAlpha(promoted.Group, 1f, FLY_DURATION));
            }

            if (_back != null)
            {
                _animation.Insert(FLY_DURATION * 0.5f, TweenAlpha(_back.Group, BACK_ALPHA, FLY_DURATION * 0.5f));
            }

            _animation.OnComplete(() =>
            {
                _animation = null;
                thrown.Destroy();
                _thrown = null;
                onFrontChanged?.Invoke();
            });
        }

        /// <summary>
        /// Finishes any card animation immediately, so fast strumming never gets ahead of the stack.
        /// </summary>
        public void CompleteAnimation()
        {
            _animation?.Complete(true);
            _animation = null;
        }

        private void OnDestroy()
        {
            _animation?.Kill();
            _thrown?.Destroy();
            _front?.Destroy();
            _back?.Destroy();
        }

        private void PlaceAsBack(Card card)
        {
            card.Root.anchoredPosition = new Vector2(0f, BACK_OFFSET);
            card.Root.localScale = Vector3.one * BACK_SCALE;
            card.Group.alpha = BACK_ALPHA;
        }

        private Card CreateCard(CardInfo info)
        {
            var song = info.Song;
            var card = new Card { Song = song };

            var background = NewImage(_stack, "Card", CardColor, 36f);
            card.Root = background.rectTransform;
            Place(card.Root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CARD_WIDTH, CARD_HEIGHT));
            card.Group = card.Root.gameObject.AddComponent<CanvasGroup>();
            card.Group.blocksRaycasts = false;

            // Placeholder shown until (or unless) the album art loads
            var placeholder = NewImage(card.Root, "Placeholder", GenreColor(song.Genre.Original), 26f);
            PlaceTop(placeholder.rectTransform, 30f, ART_SIZE, ART_SIZE);
            var placeholderText = NewText(placeholder.rectTransform, "Genre", song.Genre.Original.ToUpperInvariant(),
                54, Color.white.WithAlpha(0.8f), FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(placeholderText.rectTransform, 30f);
            placeholderText.textWrappingMode = TextWrappingModes.Normal;

            var artObject = NewRect("Art", card.Root);
            PlaceTop(artObject, 30f, ART_SIZE, ART_SIZE);
            card.Art = artObject.gameObject.AddComponent<RawImage>();
            card.Art.color = Color.clear;
            card.Art.raycastTarget = false;
            var artCorners = artObject.gameObject.AddComponent<ImageWithRoundedCorners>();
            artCorners.radius = 26f;
            artCorners.Refresh();
            card.ArtLoad = new CancellationTokenSource();
            card.Art.LoadAlbumCover(song, card.ArtLoad.Token);

            if (!string.IsNullOrEmpty(info.Reason))
            {
                var badgeText = NewText(card.Root, "ReasonText", info.Reason.ToUpperInvariant(), 22, Color.white,
                    FontStyles.Bold, TextAlignmentOptions.Center);
                float width = Mathf.Min(badgeText.GetPreferredValues(info.Reason.ToUpperInvariant()).x + 36f,
                    ART_SIZE - 32f);
                var badge = NewImage(card.Root, "Reason", new Color(0f, 0f, 0f, 0.72f), 20f);
                badge.rectTransform.anchorMin = badge.rectTransform.anchorMax = new Vector2(0f, 1f);
                badge.rectTransform.pivot = new Vector2(0f, 1f);
                badge.rectTransform.anchoredPosition = new Vector2(46f, -46f);
                badge.rectTransform.sizeDelta = new Vector2(width, 42f);
                badgeText.rectTransform.SetParent(badge.rectTransform, false);
                Stretch(badgeText.rectTransform);
            }

            float y = 30f + ART_SIZE + 22f;
            var title = NewText(card.Root, "Title", song.Name.Original, 42, Color.white, FontStyles.Bold,
                TextAlignmentOptions.Left);
            PlaceTop(title.rectTransform, y, ART_SIZE, 56f);
            y += 56f;

            var artist = NewText(card.Root, "Artist", song.Artist.Original, 32, MenuData.Colors.PrimaryText,
                FontStyles.Normal, TextAlignmentOptions.Left);
            PlaceTop(artist.rectTransform, y, ART_SIZE, 44f);
            y += 48f;

            string meta = string.Join("   ·   ", new[] { song.Album.Original, song.ParsedYear, song.Genre.Original }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var details = NewText(card.Root, "Details", meta, 24, MutedText, FontStyles.Normal,
                TextAlignmentOptions.Left);
            PlaceTop(details.rectTransform, y, ART_SIZE, 34f);
            y += 40f;

            var difficulty = NewText(card.Root, "Difficulty", info.Difficulty ?? string.Empty, 24,
                MenuData.Colors.NavigationYellow, FontStyles.Bold, TextAlignmentOptions.Left);
            PlaceTop(difficulty.rectTransform, y, ART_SIZE, 34f);

            card.LikeStamp = NewStamp(card.Root, Localize.Key("Menu.MusicLibrary.SongSwipe.Like"),
                MenuData.Colors.NavigationGreen, new Vector2(-150f, 290f), -16f);
            card.PassStamp = NewStamp(card.Root, Localize.Key("Menu.MusicLibrary.SongSwipe.Pass"),
                MenuData.Colors.NavigationRed, new Vector2(150f, 290f), 16f);
            card.FavoriteStamp = NewStamp(card.Root, Localize.Key("Menu.MusicLibrary.SongSwipe.Favorite"),
                MenuData.Colors.NavigationYellow, new Vector2(0f, 120f), 0f);

            return card;
        }

        private CanvasGroup NewStamp(RectTransform parent, string text, Color color, Vector2 position, float rotation)
        {
            var stamp = NewImage(parent, "Stamp", color, 18f);
            var rect = stamp.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.localRotation = Quaternion.Euler(0f, 0f, rotation);
            var label = NewText(rect, "Text", text.ToUpperInvariant(), 52, MenuData.Colors.GetBestTextColor(color),
                FontStyles.Bold, TextAlignmentOptions.Center);
            rect.sizeDelta = new Vector2(label.GetPreferredValues(text.ToUpperInvariant()).x + 60f, 84f);
            Stretch(label.rectTransform);

            var group = stamp.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            return group;
        }

        private void NewRoundButton(RectTransform parent, string name, string text, Color color, Vector2 position,
            SwipeDirection direction)
        {
            var image = NewImage(parent, name, color, 70f);
            Place(image.rectTransform, new Vector2(0.5f, 0.5f), position, new Vector2(140f, 140f));
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() =>
            {
                // Keep controller focus away from the button so strums stay with the swipe scheme
                EventSystem.current?.SetSelectedGameObject(null);
                ButtonClicked?.Invoke(direction);
            });

            var label = NewText(image.rectTransform, "Text", text.ToUpperInvariant(), 28,
                MenuData.Colors.GetBestTextColor(color), FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
        }

        private static Color GenreColor(string genre)
        {
            float hue = Mathf.Abs((genre ?? string.Empty).ToLowerInvariant().GetHashCode() % 1000) / 1000f;
            return Color.HSVToRGB(hue, 0.5f, 0.45f);
        }

        private Tween TweenPosition(RectTransform rect, Vector2 target)
        {
            return DOTween.To(() => rect.anchoredPosition, v => rect.anchoredPosition = v, target, FLY_DURATION);
        }

        private static Tween TweenAlpha(CanvasGroup group, float target, float duration)
        {
            return DOTween.To(() => group.alpha, a => group.alpha = a, target, duration);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.layer = parent.gameObject.layer;
            var rect = (RectTransform) gameObject.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static Image NewImage(Transform parent, string name, Color color, float radius)
        {
            var rect = NewRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = name == "Backdrop";
            if (radius > 0f)
            {
                var corners = rect.gameObject.AddComponent<ImageWithRoundedCorners>();
                corners.radius = radius;
                corners.Refresh();
            }

            return image;
        }

        private TextMeshProUGUI NewText(Transform parent, string name, string text, float size, Color color,
            FontStyles style, TextAlignmentOptions alignment)
        {
            var rect = NewRect(name, parent);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null)
            {
                label.font = _font;
            }

            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.fontStyle = style;
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void Place(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        /// <summary>
        /// Places a child horizontally centered on a card, <paramref name="top"/> units below its top edge.
        /// </summary>
        private static void PlaceTop(RectTransform rect, float top, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -top);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
