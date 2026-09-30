using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Playlists;
using YARG.Recommendations;

namespace YARG.Menu.MusicLibrary
{
    /// <summary>
    /// Song Swipe: a full-screen stack of song cards with the front card's preview playing. Strum down (or
    /// press right) to like it and the card flies right; strum up (or press left) to pass and it flies
    /// left. Orange undoes the last answer, and Select switches to reviewing earlier answers, starting
    /// with the ones that look most like mistakes. Each answer updates the profile's taste model right
    /// away, and new cards come from the genres and artists the model knows least about.
    /// </summary>
    public partial class MusicLibraryMenu
    {
        private const int SWIPE_QUEUE_SIZE = 8;
        private const int SWIPE_QUEUE_REFILL_AT = 4;
        private const double SWIPE_PREVIEW_DELAY = 0.15;
        private const string SWIPE_KEY = "Menu.MusicLibrary.SongSwipe";

        private sealed class SwipeAction
        {
            public SongEntry Song;
            public SwipeDirection Direction;
            public int FeedbackToken = -1;
            public bool AddedFavorite;
        }

        private SwipeSession _swipeSession;
        private readonly List<SongEntry> _swipeQueue = new();
        private readonly Stack<SwipeAction> _swipeHistory = new();
        private SongSwipeView _swipeView;

        // Review mode walks back through songs already rated
        private bool _swipeReviewing;
        private readonly Dictionary<SongEntry, bool> _reviewAnswers = new();
        private int _reviewTotal;

        // New-song cards waiting while the player reviews, so none are lost
        private readonly List<SongEntry> _pendingNewSongs = new();

        // Set while the last card flies away; the end-of-queue step only runs if nothing undid it
        private bool _swipeEndPending;

        public void EnterSwipeMode()
        {
            var session = RecommendationService.StartSwipeSession();
            if (session == null)
            {
                ToastManager.ToastWarning(Localize.Key(SWIPE_KEY, "NeedsProfile"));
                return;
            }

            _swipeSession = session;
            _swipeReviewing = false;
            _swipeEndPending = false;
            _swipeHistory.Clear();
            _swipeQueue.Clear();
            _pendingNewSongs.Clear();
            _swipeQueue.AddRange(session.NextSongs(SWIPE_QUEUE_SIZE));
            if (_swipeQueue.Count == 0 && !StartReviewQueue())
            {
                // Nothing new to rate and nothing rated yet
                _swipeSession = null;
                ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingLeft"));
                return;
            }

            _mainLibraryIndex = SelectedIndex;
            ClearPreview();

            // Swap the library scheme for the swipe scheme instead of stacking on top of it
            MenuState = MenuState.Swipe;
            Navigator.Instance.PopScheme();
            SetSwipeNavigationScheme();

            var canvas = GetComponentInParent<Canvas>();
            var parent = canvas != null ? canvas.rootCanvas.transform : transform;
            _swipeView = SongSwipeView.Create(parent, _subHeader.font);
            _swipeView.ButtonClicked += Swipe;
            _swipeView.UndoClicked += UndoSwipe;
            _swipeView.ReviewClicked += ToggleSwipeReview;

            ShowSwipeStack();
        }

        private void LeaveSwipeMode()
        {
            if (MenuState != MenuState.Swipe)
            {
                return;
            }

            Navigator.Instance.PopScheme();

            int liked = _swipeSession?.LikedCount ?? 0;
            int passed = _swipeSession?.PassedCount ?? 0;
            CloseSwipeView();

            MenuState = MenuState.Library;
            // Refresh rebuilds the recommendations with the new answers and pushes the library scheme
            Refresh();

            if (!SetIndexToFirstRecommendedSong())
            {
                SelectedIndex = Mathf.Clamp(_mainLibraryIndex, 0, Mathf.Max(ViewList.Count - 1, 0));
            }

            if (liked + passed > 0)
            {
                ToastManager.ToastSuccess(Localize.KeyFormat((SWIPE_KEY, "Summary"), liked, passed));
            }
        }

        private void CloseSwipeView()
        {
            StopSwipePreview();
            _swipeSession = null;
            _swipeQueue.Clear();
            _swipeHistory.Clear();
            _reviewAnswers.Clear();
            _pendingNewSongs.Clear();
            _swipeReviewing = false;
            _swipeEndPending = false;
            if (_swipeView != null)
            {
                _swipeView.ButtonClicked -= Swipe;
                _swipeView.UndoClicked -= UndoSwipe;
                _swipeView.ReviewClicked -= ToggleSwipeReview;
                Destroy(_swipeView.gameObject);
                _swipeView = null;
            }
        }

        // The library list stays behind the swipe screen unchanged
        private List<ViewType> CreateSwipeViewList() => CreateNormalViewList();

        private void SetSwipeNavigationScheme()
        {
            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                // Strum down is "yes", like nodding; right and left match the cards flying off
                new NavigationScheme.Entry(MenuAction.Down, $"{SWIPE_KEY}.Like",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Like); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Up, $"{SWIPE_KEY}.Pass",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Pass); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Right, $"{SWIPE_KEY}.Like",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Like); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Left, $"{SWIPE_KEY}.Pass",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Pass); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Green, $"{SWIPE_KEY}.PlayNow", PlaySwipeSong),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", LeaveSwipeMode),
                new NavigationScheme.Entry(MenuAction.Yellow, $"{SWIPE_KEY}.Favorite",
                    () => Swipe(SwipeDirection.Favorite)),
                new NavigationScheme.Entry(MenuAction.Blue, $"{SWIPE_KEY}.Skip", () => Swipe(SwipeDirection.Skip)),
                new NavigationScheme.Entry(MenuAction.Orange, $"{SWIPE_KEY}.Undo", UndoSwipe),
                new NavigationScheme.Entry(MenuAction.Select, $"{SWIPE_KEY}.Review", ToggleSwipeReview),
            }, false));
        }

        private SongSwipeView.CardInfo DescribeCard(SongEntry song)
        {
            var (reason, difficulty) = _swipeSession.Describe(song);
            if (_swipeReviewing && _reviewAnswers.TryGetValue(song, out bool liked))
            {
                reason = Localize.Key(SWIPE_KEY, liked ? "YouLiked" : "YouPassed");
            }

            return new SongSwipeView.CardInfo { Song = song, Reason = reason, Difficulty = difficulty };
        }

        private SongSwipeView.CardInfo? BackCardInfo()
        {
            return _swipeQueue.Count > 1 ? DescribeCard(_swipeQueue[1]) : null;
        }

        private void ShowSwipeStack()
        {
            UpdateSwipeHeader();
            if (_swipeQueue.Count == 0)
            {
                return;
            }

            _swipeView.SetCards(DescribeCard(_swipeQueue[0]), BackCardInfo());
            PlaySwipePreview();
        }

        private void UpdateSwipeHeader()
        {
            if (_swipeView == null || _swipeSession == null)
            {
                return;
            }

            if (_swipeReviewing)
            {
                int done = _reviewTotal - _swipeQueue.Count;
                _swipeView.SetMode(Localize.Key(SWIPE_KEY, "ReviewHeader"), Localize.Key(SWIPE_KEY, "ReviewHelp"),
                    Localize.Key(SWIPE_KEY, "BackToNew"));
                _swipeView.SetStatus(Localize.KeyFormat((SWIPE_KEY, "ReviewCounts"), Mathf.Min(done + 1, _reviewTotal),
                    _reviewTotal));
            }
            else
            {
                _swipeView.SetMode(Localize.Key(SWIPE_KEY, "Header"), Localize.Key(SWIPE_KEY, "HeaderHelp"),
                    Localize.Key(SWIPE_KEY, "Review"));
                _swipeView.SetStatus(Localize.KeyFormat((SWIPE_KEY, "Counts"), _swipeSession.LikedCount,
                    _swipeSession.PassedCount));
            }
        }

        private void ToggleSwipeReview()
        {
            if (_swipeSession == null || _swipeView == null)
            {
                return;
            }

            if (!_swipeReviewing)
            {
                var rated = _swipeSession.RatedSongs();
                if (rated.Count == 0)
                {
                    // Leave any end-of-queue step in flight alone
                    ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingToReview"));
                    return;
                }

                _swipeEndPending = false;
                _swipeView.CompleteAnimation();
                var newSongs = new List<SongEntry>(_swipeQueue);
                StartReviewQueue(rated);
                _pendingNewSongs.Clear();
                _pendingNewSongs.AddRange(newSongs);
            }
            else
            {
                _swipeEndPending = false;
                _swipeView.CompleteAnimation();
                _swipeReviewing = false;
                _reviewAnswers.Clear();
                _swipeQueue.Clear();
                _swipeQueue.AddRange(_pendingNewSongs);
                _pendingNewSongs.Clear();
                if (_swipeQueue.Count < SWIPE_QUEUE_SIZE)
                {
                    _swipeQueue.AddRange(_swipeSession.NextSongs(SWIPE_QUEUE_SIZE - _swipeQueue.Count));
                }
            }

            _swipeHistory.Clear();

            if (_swipeQueue.Count == 0)
            {
                ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingLeft"));
                LeaveSwipeMode();
                return;
            }

            ShowSwipeStack();
        }

        /// <summary>
        /// Replaces the queue with the songs already rated, likely mistakes first. Returns false (and
        /// leaves the queue alone) when nothing has been rated.
        /// </summary>
        private bool StartReviewQueue(List<(SongEntry Song, bool Liked)> rated = null)
        {
            rated ??= _swipeSession.RatedSongs();
            if (rated.Count == 0)
            {
                return false;
            }

            _swipeReviewing = true;
            _swipeQueue.Clear();
            _reviewAnswers.Clear();
            foreach (var (song, liked) in rated)
            {
                _swipeQueue.Add(song);
                _reviewAnswers[song] = liked;
            }

            _reviewTotal = _swipeQueue.Count;
            return true;
        }

        private void Swipe(SwipeDirection direction)
        {
            if (_swipeSession == null || _swipeView == null || _swipeQueue.Count == 0)
            {
                return;
            }

            // Land any card still in flight so the queue and the stack stay in step
            _swipeView.CompleteAnimation();

            var song = _swipeQueue[0];
            var action = new SwipeAction { Song = song, Direction = direction };
            switch (direction)
            {
                case SwipeDirection.Like:
                    action.FeedbackToken = _swipeSession.Swipe(song, true);
                    break;
                case SwipeDirection.Pass:
                    action.FeedbackToken = _swipeSession.Swipe(song, false);
                    break;
                case SwipeDirection.Favorite:
                    // A favorite is also a like, which replaces any earlier pass on the song
                    if (!PlaylistContainer.FavoritesPlaylist.ContainsSong(song))
                    {
                        PlaylistContainer.FavoritesPlaylist.AddSong(song);
                        _swipeSession.Favorite(song);
                        action.AddedFavorite = true;
                    }
                    action.FeedbackToken = _swipeSession.Swipe(song, true);
                    break;
            }

            _swipeHistory.Push(action);
            _swipeQueue.RemoveAt(0);
            if (!_swipeReviewing && _swipeQueue.Count < SWIPE_QUEUE_REFILL_AT)
            {
                _swipeQueue.AddRange(_swipeSession.NextSongs(SWIPE_QUEUE_SIZE - _swipeQueue.Count));
            }

            UpdateSwipeHeader();
            StopSwipePreview();

            if (_swipeQueue.Count == 0)
            {
                bool reviewing = _swipeReviewing;
                _swipeEndPending = true;
                _swipeView.Throw(direction, null, null, () =>
                {
                    // An undo while the last card was in flight cancels leaving
                    if (!_swipeEndPending)
                    {
                        return;
                    }

                    _swipeEndPending = false;
                    ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, reviewing ? "ReviewDone" : "NothingLeft"));
                    if (reviewing)
                    {
                        ToggleSwipeReview();
                    }
                    else
                    {
                        LeaveSwipeMode();
                    }
                });
                return;
            }

            _swipeView.Throw(direction, DescribeCard(_swipeQueue[0]), BackCardInfo(), PlaySwipePreview);
        }

        private void UndoSwipe()
        {
            if (_swipeSession == null || _swipeView == null)
            {
                return;
            }

            if (_swipeHistory.Count == 0)
            {
                ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingToUndo"));
                return;
            }

            // Cancel any end-of-queue step before landing the card still in flight
            _swipeEndPending = false;
            _swipeView.CompleteAnimation();

            var action = _swipeHistory.Pop();
            _swipeSession.UndoSwipe(action.FeedbackToken);
            if (action.AddedFavorite)
            {
                PlaylistContainer.FavoritesPlaylist.RemoveSong(action.Song);
                _swipeSession.UndoFavorite(action.Song);
            }

            _swipeQueue.Insert(0, action.Song);
            UpdateSwipeHeader();
            StopSwipePreview();
            _swipeView.Restore(DescribeCard(action.Song), action.Direction, PlaySwipePreview);
        }

        /// <summary>
        /// Plays the front card's preview through the library's preview player, with loudness leveling.
        /// </summary>
        private void PlaySwipePreview()
        {
            StopSwipePreview();
            if (_swipeQueue.Count == 0)
            {
                return;
            }

            _currentSong = _swipeQueue[0];
            _previewCanceller = new CancellationTokenSource();
            StartPreview(SWIPE_PREVIEW_DELAY, _previewCanceller, leveled: true);
        }

        private void StopSwipePreview() => StopPreview(clearCurrentSong: true);

        private void PlaySwipeSong()
        {
            if (_swipeQueue.Count == 0)
            {
                return;
            }

            var song = _swipeQueue[0];

            // Return to the normal library afterwards. The play itself is the signal, no swipe is recorded.
            Navigator.Instance.PopScheme();
            CloseSwipeView();
            MenuState = MenuState.Library;
            SetNavigationScheme();

            new SongViewType(this, song).PrimaryButtonClick();
        }
    }
}
