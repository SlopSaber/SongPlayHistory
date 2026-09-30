using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraUtil.Logging;
using SongPlayHistory.Configuration;
using SongPlayHistory.Model;
using SongPlayHistory.SongPlayTracking;
using SongPlayHistory.Utils;
using Zenject;

namespace SongPlayHistory.SongPlayData
{
    internal class RecordsManager: IInitializable, IDisposable, IAsyncRecordManager
    {
        private readonly string DataFile = Path.Combine(UnityGame.UserDataPath, "SongPlayData.json");

        private ConcurrentDictionary<string, IList<Record>> Records { get; set; } = new();
        private readonly object _saveLock = new();
        private readonly object _recordsLock = new();
        private Task _pendingSave = Task.CompletedTask;

        [Inject]
        private readonly SiraLog _logger = null!;

        public void Initialize()
        {
            lock (_saveLock)
            {
                _pendingSave = Task.Run(LoadInitialRecords);
            }
        }

        private void LoadInitialRecords()
        {
            // We don't anymore support migrating old records from a config file.

            if (!LoadRecords(DataFile, out var records) || records.Count == 0)
            {
                _logger.Warn("Did not load any records from file. Will try to restore from a backup.");
                // Try to restore from a backup.
                var backup = new FileInfo(Path.ChangeExtension(DataFile, ".bak"));
                if (backup.Exists && backup.Length > 0)
                {
                    _logger.Notice("Restoring from a backup");
                    LoadRecords(backup.FullName, out records);
                    Records = records;
                }
                else
                {
                    // There's nothing more we can try. Overwrite the file.
                    _logger.Warn("Backup not found.");
                    Records = new ConcurrentDictionary<string, IList<Record>>();
                }
            }
            else
            {
                Records = records;
            }
            
            SaveRecordsToFile();
            _logger.Info($"Loaded {SumRecords(Records)} records from {Records.Count} levels.");
            
            // TODO remove bad records?
        }

        private bool LoadRecords(string path, out ConcurrentDictionary<string, IList<Record>> records)
        {
            _logger.Info($"Loading history from {path}");
            records = new ConcurrentDictionary<string, IList<Record>>();
            
            if (!File.Exists(path))
            {
                _logger.Warn($"History file doesn't exist: {path}");
                return false;
            }
            
            try
            {
                // Read records from a data file.
                var text = File.ReadAllText(path);
                var deserialized = JsonConvert.DeserializeObject<ConcurrentDictionary<string, IList<Record>>>(text);
                records = deserialized ?? throw new Exception();
                return true;
            }
            catch (Exception e)
            {
                _logger.Error("Unable to deserialize song play records.");
                _logger.Error(e);
                return false;
            }
        }

        public void Dispose()
        {
            Task finalSave;
            lock (_saveLock)
            {
                finalSave = _pendingSave = _pendingSave.ContinueWith(task =>
                {
                    task.GetAwaiter().GetResult();
                    BackupRecords();
                },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            // App shutdown must drain queued persistence before the process exits.
            finalSave.GetAwaiter().GetResult();
        }

        public IList<ISongPlayRecord> GetRecords(BeatmapKey beatmap)
        {
            var key = new LevelMapKey(beatmap);
            Task pending;
            lock (_saveLock)
            {
                pending = _pendingSave;
            }

            // Preserve the synchronous API for consumers; UI uses GetRecordsAsync.
            pending.GetAwaiter().GetResult();
            return GetRecordsSnapshot(key);
        }

        public async Task<IList<ISongPlayRecord>> GetRecordsAsync(BeatmapKey beatmap, CancellationToken cancellationToken)
        {
            var key = new LevelMapKey(beatmap);
            Task pending;
            lock (_saveLock)
            {
                pending = _pendingSave;
            }

            await pending.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => GetRecordsSnapshot(key), cancellationToken).ConfigureAwait(false);
        }

        private IList<ISongPlayRecord> GetRecordsSnapshot(LevelMapKey key)
        {
            _logger.Debug($"Getting records for {key}");
            lock (_recordsLock)
            {
                if (Records.TryGetValue(key.ToOldKey(), out var records))
                {
                    _logger.Debug($"Total number of records: {records.Count}");
                    return records.Copy();
                }
            }

            _logger.Debug("No records found.");
            return new List<ISongPlayRecord>();
        }

        public void SaveRecord(LevelCompletionResults result, LevelCompletionResultsExtraData extraData)
        {
            var beatmapKey = extraData.SceneSetupData.beatmapKey;
            if (!beatmapKey.IsValid())
            {
                _logger.Warn("Invalid BeatmapKey, not saving record.");
                return;
            }
            
            if (extraData.IsPractice || extraData.IsParty)
            {
                _logger.Info("It was in practice or party mode, ignored.");
                return;
            }
            
            if (result.multipliedScore <= 0)
            {
                _logger.Warn("Record ignored, score is 0.");
                return;
            }

            // Cancelled.
            if (result.levelEndStateType == LevelCompletionResults.LevelEndStateType.Incomplete)
            {
                return;
            }
            
            // We now keep failed records.
            var cleared = result.levelEndStateType == LevelCompletionResults.LevelEndStateType.Cleared;
            var noFailEnabled = result.gameplayModifiers.noFailOn0Energy;
            var energyDidReach0 = extraData.EnergyDidReach0;
            var failRecord = extraData.ScoringDataWhenEnergyReached0;
            
            _logger.Debug($"Cleared: {cleared}, NoFail: {noFailEnabled}, SoftFailed: {energyDidReach0}, FailRecord: {failRecord}");
            
            var param = ParamHelper.ModsToParam(result.gameplayModifiers, energyDidReach0);
            param |= extraData.ScoreSubmissionDisabled ? SongPlayParam.SubmissionDisabled : 0;
            param |= extraData.IsMultiplayer ? SongPlayParam.Multiplayer : 0;
            var time = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Record record;
            
            if (cleared && energyDidReach0 && failRecord != null)
            {
                // use our tracked values at the time of soft fail
                record = new Record
                {
                    Date = time,
                    ModifiedScore = failRecord.Value.ModifiedScore,
                    RawScore = failRecord.Value.RawScore,
                    LastNote = failRecord.Value.NotesPassed,
                    Params = param,
                    MaxRawScore = failRecord.Value.MaxRawScore
                };
            }
            else if (!cleared && noFailEnabled && energyDidReach0 && failRecord != null)
            {
                // No fail is enabled and did soft fail, but level still failed (for example, the FailButton mod)
                // ues our tracked values at the time of soft fail
                record = new Record
                {
                    Date = time,
                    ModifiedScore = failRecord.Value.ModifiedScore,
                    RawScore = failRecord.Value.RawScore,
                    LastNote = failRecord.Value.NotesPassed,
                    Params = param,
                    MaxRawScore = failRecord.Value.MaxRawScore
                };
            }
            else
            {
                record = new Record
                {
                    Date = time,
                    ModifiedScore = result.modifiedScore,
                    RawScore = result.multipliedScore,
                    LastNote = cleared ? -1 : result.goodCutsCount + result.badCutsCount + result.missedCount,
                    Params = param,
                    MaxRawScore = cleared ? null : failRecord?.MaxRawScore
                };
            }

            _logger.Info($"Saving result. Record: {record}");

            var key = new LevelMapKey(beatmapKey).ToOldKey();

            lock (_saveLock)
            {
                _pendingSave = _pendingSave.ContinueWith(task =>
                {
                    task.GetAwaiter().GetResult();
                    lock (_recordsLock)
                    {
                        Records.GetOrAdd(key, new List<Record>()).Add(record);
                    }

                    SaveRecordsToFile();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            _logger.Info($"Queued a new record ({result.modifiedScore}) for saving.");
        }

        private void SaveRecordsToFile()
        {
            Dictionary<string, Record[]> snapshot;
            lock (_recordsLock)
            {
                if (Records.Count == 0) return;
                snapshot = Records.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            }

            SaveRecordsSnapshot(snapshot);
        }

        private void SaveRecordsSnapshot(Dictionary<string, Record[]> snapshot)
        {
            try
            {
                var serialized = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(DataFile, serialized);
            }
            catch (Exception ex) // IOException, JsonException
            {
                _logger.Error($"Failed to save records to file: {ex.Message}");
                _logger.Error(ex);
            }
        }

        private void BackupRecords()
        {
            if (!File.Exists(DataFile))
            {
                return;
            }

            var backupFile = Path.ChangeExtension(DataFile, ".bak");
            try
            {
                if (File.Exists(backupFile) && LoadRecords(backupFile, out var backupRecords))
                {
                    // Compare file sizes instead of the last modified.
                    if (SumRecords(Records) >= SumRecords(backupRecords))
                    {
                        File.Copy(DataFile, backupFile, true);
                    }
                    else
                    {
                        _logger.Info("Nothing to backup.");
                    }
                }
                else
                {
                    File.Copy(DataFile, backupFile);
                }
            }
            catch (IOException ex)
            {
                _logger.Error(ex.ToString());
            }
        }

        private static int SumRecords(IDictionary<string, IList<Record>> records)
        {
            return records.Select(pair => pair.Value.Count).Sum();
        }
    }
}
