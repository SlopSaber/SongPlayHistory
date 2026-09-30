using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraUtil.Logging;
using SongPlayHistory.Model;
using Zenject;

namespace SongPlayHistory.VoteTracker
{
    internal class InternalVoteTracker: IVoteTracker, IInitializable, IDisposable
    {

        private static readonly string VoteFile = Path.Combine(UnityGame.UserDataPath, "votedSongs.json");
        
        private static ConcurrentDictionary<string, UserVote>? _votes = new();

        private static readonly object _voteWriteLock = new();
        private Task _pendingWork = Task.CompletedTask;
        internal Task Ready { get; private set; } = Task.CompletedTask;
        internal Task Pending
        {
            get
            {
                lock (_voteWriteLock) return _pendingWork;
            }
        }

        [Inject]
        private readonly SiraLog _logger = null!;

        private bool _readonly = true;

        // private DateTime _voteLastWritten;

        public void Initialize()
        {
            lock (_voteWriteLock)
            {
                Volatile.Write(ref _votes, new ConcurrentDictionary<string, UserVote>());
                _pendingWork = Ready = Task.Run(LoadVotes);
            }
        }

        private void LoadVotes()
        {
            _logger.Info("Loading votes.");
            _readonly = true;
            
            if (!File.Exists(VoteFile))
            {
                _logger.Debug("BeatSaverVoting votedSongs.json doesn't exist.");
                _readonly = false;
                return;
            }

            try
            {
                var text = File.ReadAllText(VoteFile, Encoding.UTF8);
                var votes = JsonConvert.DeserializeObject<Dictionary<string, UserVote>?>(text) ?? new Dictionary<string, UserVote>();
                Volatile.Write(ref _votes, new ConcurrentDictionary<string, UserVote>(votes));
                _logger.Info("votedSongs.json Loaded");
            }
            catch (Exception ex) // IOException, JsonException
            {
                _readonly = true;
                _logger.Error("Failed to load votedSongs.json. Entering readonly mode.");
                _logger.Error(ex);
            }

            try
            {
                // backup in case something bad happened.
                File.Copy(VoteFile, VoteFile + ".sph.bak", true);
                _readonly = false;
            }
            catch (Exception e)
            {
                _readonly = true;
                _logger.Error("Failed to backup votedSongs.json. Entering readonly mode.");
                _logger.Error(e);
            }
        }

        private void SaveVotes()
        {
            if (_readonly)
            {
                _logger.Debug("Votes not saved, read only");
                return;
            }

            try
            {
                var votes = Volatile.Read(ref _votes);
                if (votes != null && !votes.IsEmpty)
                {
                    File.WriteAllText(VoteFile, JsonConvert.SerializeObject(votes), Encoding.UTF8);
                }
            }
            catch (Exception e)
            {
                _logger.Error($"Failed to save votedSongs.json: {e.Message}");
                _logger.Error(e);
            }
        }

        public void Dispose()
        {
            Task finalSave;
            lock (_voteWriteLock)
            {
                finalSave = _pendingWork = _pendingWork.ContinueWith(_ =>
                {
                    SaveVotes();
                    Volatile.Write(ref _votes, null);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            // Flush at app shutdown so a queued vote cannot be lost at process exit.
            finalSave.GetAwaiter().GetResult();
        }

        public bool TryGetVote(BeatmapLevel level, out VoteType voteType)
        {
            voteType = VoteType.Downvote;
            try
            {
                Ready.GetAwaiter().GetResult();
                var hash = Utils.Utils.GetLowerCaseCustomLevelHash(level);
                if (hash != null && Volatile.Read(ref _votes)?.TryGetValue(hash, out var vote) == true)
                {
                    voteType = vote.VoteType;
                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.Warn($"Failed to get vote: {e.Message}");
                _logger.Warn(e);
            }

            return false;
        }

        public void Vote(BeatmapLevel level, VoteType voteType)
        {
            var hash = Utils.Utils.GetLowerCaseCustomLevelHash(level);
            var levelId = level.levelID;
            if (hash == null) return;
            lock (_voteWriteLock)
            {
                _pendingWork = _pendingWork.ContinueWith(_ =>
                {
                    var votes = Volatile.Read(ref _votes);
                    if (votes != null)
                    {
                        if (!votes.TryGetValue(hash, out var vote) || vote.VoteType != voteType)
                        {
                            votes[hash] = new UserVote
                            {
                                Hash = hash,
                                VoteType = voteType
                            };
                            SaveVotes();
                        }
                        Plugin.Log.Info($"Voted {voteType} to {levelId}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

    }
}
