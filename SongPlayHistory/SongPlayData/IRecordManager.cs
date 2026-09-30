using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SongPlayHistory.SongPlayTracking;

namespace SongPlayHistory.SongPlayData;

[PublicAPI]
public interface IRecordManager
{
    public IList<ISongPlayRecord> GetRecords(BeatmapKey beatmap);

    internal void SaveRecord(LevelCompletionResults results, LevelCompletionResultsExtraData extraData);
}

[PublicAPI]
public interface IAsyncRecordManager : IRecordManager
{
    Task<IList<ISongPlayRecord>> GetRecordsAsync(BeatmapKey beatmap, CancellationToken cancellationToken);
}
