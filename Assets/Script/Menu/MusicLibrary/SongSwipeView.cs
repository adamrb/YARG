using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Core.Song;

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
    /// moves forward.
    /// </summary>
    public class SongSwipeView : MonoBehaviour
    {
        public struct CardInfo
        {
            public SongEntry Song;
            public string Reason;
            public string Difficulty;
        }

        private const float BACK_SCALE = 0.93f;
        private const float BACK_OFFSET = -64f;
        private const float BACK_ALPHA = 0.5f;
        private const float FLY_DURATION = 0.3f;

        [SerializeField]
        private TextMeshProUGUI _title;
        [SerializeField]
        private TextMeshProUGUI _subtitle;
        [SerializeField]
        private TextMeshProUGUI _counter;
        [SerializeField]
        private TextMeshProUGUI _reviewButtonText;
        [SerializeField]
        private GameObject _emptyMessage;
        [SerializeField]
        private RectTransform _stack;
        [SerializeField]
        private SongSwipeCard _cardPrefab;

        [Space]
        [SerializeField]
        private Button _likeButton;
        [SerializeField]
        private Button _passButton;
        [SerializeField]
        private Button _undoButton;
        [SerializeField]
        private Button _reviewButton;

        private SongSwipeCard _front;
        private SongSwipeCard _back;
        private SongSwipeCard _thrown;
        private Sequence _animation;

        public event Action<SwipeDirection> ButtonClicked;
        public event Action UndoClicked;
        public event Action ReviewClicked;

        private void Awake()
        {
            _likeButton.onClick.AddListener(() => OnClick(() => ButtonClicked?.Invoke(SwipeDirection.Like)));
            _passButton.onClick.AddListener(() => OnClick(() => ButtonClicked?.Invoke(SwipeDirection.Pass)));
            _undoButton.onClick.AddListener(() => OnClick(() => UndoClicked?.Invoke()));
            _reviewButton.onClick.AddListener(() => OnClick(() => ReviewClicked?.Invoke()));
        }

        private static void OnClick(Action action)
        {
            // Keep controller focus away from the buttons so strums stay with the swipe scheme
            EventSystem.current?.SetSelectedGameObject(null);
            action();
        }

        public void Show()
        {
            gameObject.SetActive(true);
            UpdateEmptyMessage();
        }

        public void Hide()
        {
            ClearCards();
            gameObject.SetActive(false);
        }

        public void SetStatus(string text)
        {
            _counter.text = text;
        }

        public void SetMode(string title, string subtitle, string reviewButton)
        {
            _title.text = title;
            _subtitle.text = subtitle;
            _reviewButtonText.text = reviewButton;
        }

        /// <summary>
        /// Shows a fresh stack, or an empty one when <paramref name="front"/> is null.
        /// </summary>
        public void SetCards(CardInfo? front, CardInfo? back)
        {
            ClearCards();
            if (back.HasValue)
            {
                _back = CreateCard(back.Value);
                PlaceAsBack(_back);
            }

            if (front.HasValue)
            {
                _front = CreateCard(front.Value);
            }

            UpdateEmptyMessage();
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
                Destroy(promoted.gameObject);
                promoted = null;
            }

            if (promoted != null)
            {
                // The model may have learned since this card was dealt
                promoted.SetInfo(newFront.Value);
            }
            else if (newFront.HasValue)
            {
                promoted = CreateCard(newFront.Value);
                PlaceAsBack(promoted);
                promoted.Rect.SetSiblingIndex(thrown.Rect.GetSiblingIndex());
            }

            _front = promoted;
            _back = newBack.HasValue ? CreateCard(newBack.Value) : null;
            if (_back != null)
            {
                PlaceAsBack(_back);
                _back.Rect.SetAsFirstSibling();
                _back.Group.alpha = 0f;
            }

            var (target, rotation) = ThrowTarget(direction);
            var stamp = thrown.Stamp(direction);

            _animation = DOTween.Sequence().SetUpdate(true);
            _animation.Join(TweenPosition(thrown.Rect, target).SetEase(Ease.InQuad));
            _animation.Join(thrown.Rect.DOLocalRotate(new Vector3(0f, 0f, rotation), FLY_DURATION));
            _animation.Insert(0f, stamp != null
                ? TweenAlpha(stamp, 1f, FLY_DURATION * 0.4f)
                : TweenAlpha(thrown.Group, 0f, FLY_DURATION));

            if (promoted != null)
            {
                _animation.Insert(0f, TweenPosition(promoted.Rect, Vector2.zero));
                _animation.Insert(0f, promoted.Rect.DOScale(1f, FLY_DURATION));
                _animation.Insert(0f, TweenAlpha(promoted.Group, 1f, FLY_DURATION));
            }

            if (_back != null)
            {
                _animation.Insert(FLY_DURATION * 0.5f, TweenAlpha(_back.Group, BACK_ALPHA, FLY_DURATION * 0.5f));
            }

            _animation.OnComplete(() =>
            {
                _animation = null;
                Destroy(thrown.gameObject);
                _thrown = null;
                UpdateEmptyMessage();
                onFrontChanged?.Invoke();
            });
        }

        /// <summary>
        /// Brings a card back in from where it was thrown (for undo). The current front card moves back.
        /// </summary>
        public void Restore(CardInfo front, SwipeDirection thrownTo, Action onFrontChanged)
        {
            CompleteAnimation();
            if (_back != null)
            {
                Destroy(_back.gameObject);
            }

            _back = _front;
            var returning = CreateCard(front);
            var (start, rotation) = ThrowTarget(thrownTo);
            returning.Rect.anchoredPosition = start;
            returning.Rect.localRotation = Quaternion.Euler(0f, 0f, rotation);
            _front = returning;
            UpdateEmptyMessage();

            _animation = DOTween.Sequence().SetUpdate(true);
            _animation.Join(TweenPosition(returning.Rect, Vector2.zero).SetEase(Ease.OutQuad));
            _animation.Join(returning.Rect.DOLocalRotate(Vector3.zero, FLY_DURATION));
            if (_back != null)
            {
                var back = _back;
                _animation.Insert(0f, TweenPosition(back.Rect, new Vector2(0f, BACK_OFFSET)));
                _animation.Insert(0f, back.Rect.DOScale(BACK_SCALE, FLY_DURATION));
                _animation.Insert(0f, TweenAlpha(back.Group, BACK_ALPHA, FLY_DURATION));
            }

            _animation.OnComplete(() =>
            {
                _animation = null;
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
        }

        private void ClearCards()
        {
            CompleteAnimation();
            foreach (var card in new[] { _front, _back, _thrown })
            {
                if (card != null)
                {
                    Destroy(card.gameObject);
                }
            }

            _front = _back = _thrown = null;
        }

        private void UpdateEmptyMessage()
        {
            _emptyMessage.SetActive(_front == null);
        }

        private SongSwipeCard CreateCard(CardInfo info)
        {
            var card = Instantiate(_cardPrefab, _stack);
            card.Show(info);
            return card;
        }

        private static void PlaceAsBack(SongSwipeCard card)
        {
            card.Rect.anchoredPosition = new Vector2(0f, BACK_OFFSET);
            card.Rect.localScale = Vector3.one * BACK_SCALE;
            card.Group.alpha = BACK_ALPHA;
        }

        private static (Vector2 Position, float Rotation) ThrowTarget(SwipeDirection direction)
        {
            return direction switch
            {
                SwipeDirection.Like     => (new Vector2(1500f, -140f), -22f),
                SwipeDirection.Pass     => (new Vector2(-1500f, -140f), 22f),
                SwipeDirection.Favorite => (new Vector2(0f, 1400f), 0f),
                _                       => (new Vector2(0f, -1400f), 0f),
            };
        }

        private static Tween TweenPosition(RectTransform rect, Vector2 target)
        {
            return DOTween.To(() => rect.anchoredPosition, v => rect.anchoredPosition = v, target, FLY_DURATION);
        }

        private static Tween TweenAlpha(CanvasGroup group, float target, float duration)
        {
            return DOTween.To(() => group.alpha, a => group.alpha = a, target, duration);
        }
    }
}
