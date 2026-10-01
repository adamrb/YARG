using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
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
    /// with the ones that look most like mistakes. Each answer updates the profile's taste model, and new
    /// cards come from the genres and artists the model knows least about.
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

        [SerializeField]
        private SongSwipeView _swipeView;

        private SwipeSession _swipeSession;
        private bool _swipeStarting;
        private readonly List<SongEntry> _swipeQueue = new();
        private readonly Stack<SwipeAction> _swipeHistory = new();

        // Review mode walks back through songs already rated. Ranking runs in the background; answers wait
        // until it is done. The session is recorded so a ranking left behind by a closed session never
        // blocks a new one.
        private bool _swipeReviewing;
        private SwipeSession _reviewLoadingFor;
        private bool ReviewLoading => _reviewLoadingFor != null && _reviewLoadingFor == _swipeSession;
        private readonly Dictionary<SongEntry, bool> _reviewAnswers = new();
        private int _reviewTotal;

        // New-song cards waiting while the player reviews, so none are lost
        private readonly List<SongEntry> _pendingNewSongs = new();

        public async void EnterSwipeMode()
        {
            if (_swipeStarting || MenuState == MenuState.Swipe)
            {
                return;
            }

            // The loading screen blocks menu and pointer input until swipe mode is set up, so the library
            // cannot move on while the model trains
            using var loading = new LoadingContext();
            loading.SetLoadingText(Localize.Key(SWIPE_KEY, "Loading"));
            _swipeStarting = true;
            SwipeSession session;
            try
            {
                session = await RecommendationService.StartSwipeSessionAsync();
            }
            finally
            {
                _swipeStarting = false;
            }

            if (this == null || !isActiveAndEnabled || MenuState != MenuState.Library)
            {
                session?.Close();
                return;
            }

            if (session == null)
            {
                ToastManager.ToastWarning(Localize.Key(SWIPE_KEY, "NeedsProfile"));
                return;
            }

            // A controller may have connected another profile while the model trained
            if (session.Profile.Id != RecommendationService.GetPrimaryProfile()?.Id)
            {
                session.Close();
                return;
            }

            var songs = session.NextSongs(SWIPE_QUEUE_SIZE);
            if (songs.Count == 0 && !session.HasRatings)
            {
                ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingLeft"));
                return;
            }

            _swipeSession = session;
            _swipeReviewing = false;
            _swipeHistory.Clear();
            _swipeQueue.Clear();
            _pendingNewSongs.Clear();
            _swipeQueue.AddRange(songs);

            _mainLibraryIndex = SelectedIndex;
            ClearPreview();

            // Swap the library scheme for the swipe scheme instead of stacking on top of it
            MenuState = MenuState.Swipe;
            Navigator.Instance.PopScheme();
            SetSwipeNavigationScheme();

            _swipeView.ButtonClicked += Swipe;
            _swipeView.UndoClicked += UndoSwipe;
            _swipeView.ReviewClicked += ToggleSwipeReview;
            _swipeView.Show();
            // The sidebar's difficulty rings sit on a canvas above the library and would show under the help bar
            SetSidebarDifficultiesVisible(false);
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
            _swipeSession?.Close();
            _swipeSession = null;
            _swipeQueue.Clear();
            _swipeHistory.Clear();
            _reviewAnswers.Clear();
            _pendingNewSongs.Clear();
            _swipeReviewing = false;
            _swipeView.ButtonClicked -= Swipe;
            _swipeView.UndoClicked -= UndoSwipe;
            _swipeView.ReviewClicked -= ToggleSwipeReview;
            _swipeView.Hide();
            SetSidebarDifficultiesVisible(true);
        }

        private void SetSwipeNavigationScheme()
        {
            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                // Strum down is "yes", like nodding; right and left match the cards flying off
                new NavigationScheme.Entry(MenuAction.Down, $"{SWIPE_KEY}.Like",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Like); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Up, $"{SWIPE_KEY}.Pass",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Pass); }, hide: true),
                // The help bar shows left and right as one entry, so they share a label
                new NavigationScheme.Entry(MenuAction.Right, $"{SWIPE_KEY}.PassLike",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Like); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Left, $"{SWIPE_KEY}.PassLike",
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

        private SongSwipeView.CardInfo? CardAt(int index)
        {
            return index < _swipeQueue.Count ? DescribeCard(_swipeQueue[index]) : null;
        }

        private void ShowSwipeStack()
        {
            UpdateSwipeHeader();
            _swipeView.SetCards(CardAt(0), CardAt(1));
            PlaySwipePreview();
        }

        private void UpdateSwipeHeader()
        {
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

        private async void ToggleSwipeReview()
        {
            var session = _swipeSession;
            if (session == null || ReviewLoading)
            {
                return;
            }

            if (!_swipeReviewing)
            {
                // Answers are held until the ranking is done, so it matches what the player sees
                List<(SongEntry Song, bool Liked)> rated;
                _reviewLoadingFor = session;
                try
                {
                    rated = await session.RatedSongsAsync();
                }
                catch (Exception e)
                {
                    YargLogger.LogException(e, "Failed to rank Song Swipe ratings.");
                    return;
                }
                finally
                {
                    if (_reviewLoadingFor == session)
                    {
                        _reviewLoadingFor = null;
                    }
                }

                // Swipe mode may have closed while the ratings were ranked
                if (_swipeSession != session)
                {
                    return;
                }

                if (rated.Count == 0)
                {
                    ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingToReview"));
                    return;
                }

                _swipeView.CompleteAnimation();
                _pendingNewSongs.Clear();
                _pendingNewSongs.AddRange(_swipeQueue);
                _swipeReviewing = true;
                _swipeQueue.Clear();
                _reviewAnswers.Clear();
                foreach (var (song, liked) in rated)
                {
                    _swipeQueue.Add(song);
                    _reviewAnswers[song] = liked;
                }

                _reviewTotal = _swipeQueue.Count;
            }
            else
            {
                _swipeView.CompleteAnimation();
                _swipeReviewing = false;
                _reviewAnswers.Clear();
                _swipeQueue.Clear();
                _swipeQueue.AddRange(_pendingNewSongs);
                _pendingNewSongs.Clear();
                if (_swipeQueue.Count < SWIPE_QUEUE_SIZE)
                {
                    _swipeQueue.AddRange(session.NextSongs(SWIPE_QUEUE_SIZE - _swipeQueue.Count));
                }
            }

            // Undo works within one stack; switching stacks starts a new history
            _swipeHistory.Clear();
            StopSwipePreview();
            ShowSwipeStack();
        }

        private void Swipe(SwipeDirection direction)
        {
            if (_swipeSession == null || _swipeQueue.Count == 0 || ReviewLoading)
            {
                return;
            }

            // Land any card still in flight so the queue and the stack stay in step
            _swipeView.CompleteAnimation();

            var song = _swipeQueue[0];
            var action = new SwipeAction { Song = song, Direction = direction };
            if (direction != SwipeDirection.Skip)
            {
                // A favorite is also a like, which replaces any earlier pass on the song
                bool addFavorite = direction == SwipeDirection.Favorite &&
                    !PlaylistContainer.FavoritesPlaylist.ContainsSong(song);
                action.FeedbackToken = _swipeSession.Swipe(song, direction != SwipeDirection.Pass, addFavorite);
                if (action.FeedbackToken < 0)
                {
                    ToastManager.ToastError(Localize.Key(SWIPE_KEY, "SaveFailed"));
                    return;
                }

                if (addFavorite)
                {
                    PlaylistContainer.FavoritesPlaylist.AddSong(song);
                    action.AddedFavorite = true;
                }
            }

            _swipeHistory.Push(action);
            _swipeQueue.RemoveAt(0);
            if (!_swipeReviewing && _swipeQueue.Count < SWIPE_QUEUE_REFILL_AT)
            {
                _swipeQueue.AddRange(_swipeSession.NextSongs(SWIPE_QUEUE_SIZE - _swipeQueue.Count));
            }

            UpdateSwipeHeader();
            StopSwipePreview();
            _swipeView.Throw(direction, CardAt(0), CardAt(1), PlaySwipePreview);
        }

        private void UndoSwipe()
        {
            if (_swipeSession == null || ReviewLoading)
            {
                return;
            }

            if (_swipeHistory.Count == 0)
            {
                ToastManager.ToastInformation(Localize.Key(SWIPE_KEY, "NothingToUndo"));
                return;
            }

            _swipeView.CompleteAnimation();
            var action = _swipeHistory.Peek();
            if (action.FeedbackToken >= 0 && !_swipeSession.UndoSwipe(action.FeedbackToken))
            {
                ToastManager.ToastError(Localize.Key(SWIPE_KEY, "UndoFailed"));
                return;
            }

            _swipeHistory.Pop();
            if (action.AddedFavorite)
            {
                PlaylistContainer.FavoritesPlaylist.RemoveSong(action.Song);
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
