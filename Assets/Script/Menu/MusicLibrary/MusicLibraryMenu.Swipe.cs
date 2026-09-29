using System.Collections.Generic;
using System.Linq;
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
    /// Song Swipe: a full-screen stack of song cards with the front card's preview playing. Strum up (or
    /// press right) to like it and the card flies right; strum down (or press left) to pass and it flies
    /// left. Each answer updates the profile's taste model right away, and the next cards come from the
    /// genres and artists the model knows least about.
    /// </summary>
    public partial class MusicLibraryMenu
    {
        private const int SWIPE_QUEUE_SIZE = 8;
        private const int SWIPE_QUEUE_REFILL_AT = 4;
        private const double SWIPE_PREVIEW_DELAY = 0.15;

        private RecommendationService.SwipeSession _swipeSession;
        private readonly List<SongEntry> _swipeQueue = new();
        private SongSwipeView _swipeView;

        public void EnterSwipeMode()
        {
            var session = RecommendationService.StartSwipeSession();
            if (session == null)
            {
                ToastManager.ToastWarning(Localize.Key("Menu.MusicLibrary.SongSwipe.NeedsProfile"));
                return;
            }

            _swipeQueue.Clear();
            _swipeQueue.AddRange(session.NextSongs(SWIPE_QUEUE_SIZE));
            if (_swipeQueue.Count == 0)
            {
                ToastManager.ToastInformation(Localize.Key("Menu.MusicLibrary.SongSwipe.NothingLeft"));
                return;
            }

            _swipeSession = session;
            _mainLibraryIndex = SelectedIndex;

            // Swap the library scheme for the swipe scheme instead of stacking on top of it
            MenuState = MenuState.Swipe;
            Navigator.Instance.PopScheme();
            SetSwipeNavigationScheme();

            var canvas = GetComponentInParent<Canvas>();
            var parent = canvas != null ? canvas.rootCanvas.transform : transform;
            _swipeView = SongSwipeView.Create(parent, _subHeader.font);
            _swipeView.ButtonClicked += OnSwipeButtonClicked;
            _swipeView.SetCounts(0, 0);
            _swipeView.SetCards(DescribeCard(_swipeQueue[0]), BackCardInfo());

            PlaySwipePreview();
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
                ToastManager.ToastSuccess(Localize.KeyFormat("Menu.MusicLibrary.SongSwipe.Summary", liked, passed));
            }
        }

        private void CloseSwipeView()
        {
            ClearPreview();
            _swipeSession = null;
            _swipeQueue.Clear();
            if (_swipeView != null)
            {
                _swipeView.ButtonClicked -= OnSwipeButtonClicked;
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
                new NavigationScheme.Entry(MenuAction.Up, "Menu.MusicLibrary.SongSwipe.Like",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Like); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.MusicLibrary.SongSwipe.Pass",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Pass); }, hide: true),
                new NavigationScheme.Entry(MenuAction.Right, "Menu.MusicLibrary.SongSwipe.PassLike",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Like); }),
                new NavigationScheme.Entry(MenuAction.Left, "Menu.MusicLibrary.SongSwipe.PassLike",
                    ctx => { if (!ctx.IsRepeat) Swipe(SwipeDirection.Pass); }),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.MusicLibrary.SongSwipe.PlayNow", PlaySwipeSong),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", LeaveSwipeMode),
                new NavigationScheme.Entry(MenuAction.Yellow, "Menu.MusicLibrary.SongSwipe.Favorite",
                    () => Swipe(SwipeDirection.Favorite)),
                new NavigationScheme.Entry(MenuAction.Blue, "Menu.MusicLibrary.SongSwipe.Skip",
                    () => Swipe(SwipeDirection.Skip)),
            }, false));
        }

        private void OnSwipeButtonClicked(SwipeDirection direction) => Swipe(direction);

        private SongSwipeView.CardInfo DescribeCard(SongEntry song)
        {
            var (reason, difficulty) = _swipeSession.Describe(song);
            return new SongSwipeView.CardInfo { Song = song, Reason = reason, Difficulty = difficulty };
        }

        private SongSwipeView.CardInfo? BackCardInfo()
        {
            return _swipeQueue.Count > 1 ? DescribeCard(_swipeQueue[1]) : null;
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
            switch (direction)
            {
                case SwipeDirection.Like:
                    _swipeSession.Swipe(song, true);
                    break;
                case SwipeDirection.Pass:
                    _swipeSession.Swipe(song, false);
                    break;
                case SwipeDirection.Favorite:
                    // A favorite is a stronger signal than a like, so it stands in for the swipe
                    if (!PlaylistContainer.FavoritesPlaylist.ContainsSong(song))
                    {
                        PlaylistContainer.FavoritesPlaylist.AddSong(song);
                    }
                    _swipeSession.Favorite(song);
                    break;
            }

            _swipeQueue.RemoveAt(0);
            if (_swipeQueue.Count < SWIPE_QUEUE_REFILL_AT)
            {
                _swipeQueue.AddRange(_swipeSession.NextSongs(SWIPE_QUEUE_SIZE - _swipeQueue.Count));
            }

            _swipeView.SetCounts(_swipeSession.LikedCount, _swipeSession.PassedCount);
            StopPreview(clearCurrentSong: true);

            if (_swipeQueue.Count == 0)
            {
                _swipeView.Throw(direction, null, null, () =>
                {
                    ToastManager.ToastInformation(Localize.Key("Menu.MusicLibrary.SongSwipe.NothingLeft"));
                    LeaveSwipeMode();
                });
                return;
            }

            _swipeView.Throw(direction, DescribeCard(_swipeQueue[0]), BackCardInfo(), PlaySwipePreview);
        }

        private void PlaySwipePreview()
        {
            if (_swipeQueue.Count == 0)
            {
                return;
            }

            StopPreview();
            _currentSong = _swipeQueue[0];
            _previewCanceller = new CancellationTokenSource();
            StartPreview(SWIPE_PREVIEW_DELAY, _previewCanceller);
        }

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
