using System;
using HMUI;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities.Async;
using SiraUtil.Logging;
using SongPlayHistory.Model;
using Zenject;

namespace SongPlayHistory.VoteTracker
{
    internal class InMenuVoteTrackingHelper: IInitializable, IDisposable
    {
        internal static InMenuVoteTrackingHelper? Instance { get; private set; }
        
        [Inject] 
        private readonly IVoteTracker _voteTracker = null!;
        
        [Inject]
        private readonly ResultsViewController _resultsViewController = null!;
        
        [Inject]
        private readonly SiraLog _logger = null!;
        
        private readonly TableView _tableView;

        public InMenuVoteTrackingHelper(LevelCollectionViewController levelCollectionViewController)
        {
            var levelCollectionTableView = levelCollectionViewController._levelCollectionTableView;
            _tableView = levelCollectionTableView._tableView;
        }

        public void Initialize()
        {
            Instance = this;
            _resultsViewController.continueButtonPressedEvent -= OnPlayResultDismiss;
            _resultsViewController.continueButtonPressedEvent += OnPlayResultDismiss;
            if (_voteTracker is InternalVoteTracker tracker)
            {
                tracker.Ready.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        _logger.Error($"Failed to load votes: {task.Exception}");
                        return;
                    }

                    if (Instance == this) _tableView.RefreshCellsContent();
                }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, UnityMainThreadTaskScheduler.Default);
            }
        }

        public void Dispose()
        {
            Instance = null;
            _resultsViewController.continueButtonPressedEvent -= OnPlayResultDismiss;
        }
        
        private void OnPlayResultDismiss(ResultsViewController _)
        {
            // The user may have voted on this map.
            _tableView.RefreshCellsContent();
        }

        internal void Vote(BeatmapLevel level, VoteType voteType)
        {
            _voteTracker.Vote(level, voteType);
            if (_voteTracker is InternalVoteTracker tracker)
            {
                tracker.Pending.ContinueWith(task =>
                {
                    if (task.IsFaulted) _logger.Error($"Failed to save vote: {task.Exception}");
                    if (Instance == this) RefreshVotes();
                }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, UnityMainThreadTaskScheduler.Default);
            }

            _logger.Debug("Refreshing cells content");
            _tableView.RefreshCellsContent();
        }
        
        internal bool TryGetVote(BeatmapLevel level, out VoteType voteType)
        {
            if (_voteTracker is InternalVoteTracker tracker && !tracker.Ready.IsCompleted)
            {
                voteType = VoteType.Downvote;
                return false;
            }

            return _voteTracker.TryGetVote(level, out voteType);
        }

        internal void RefreshVotes() => _tableView.RefreshCellsContent();
    }
}
