using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HMUI;
using IPA.Utilities;
using BGLib.Polyglot;
using IPA.Utilities.Async;
using SiraUtil.Logging;
using SongPlayHistory.Configuration;
using SongPlayHistory.Model;
using SongPlayHistory.SongPlayData;
using SongPlayHistory.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Zenject;
using UObject = UnityEngine.Object;

namespace SongPlayHistory.UI
{
    internal class SPHUI: IInitializable, IDisposable
    {
        [Inject]
        private readonly IRecordManager _recordsManager = null!;

        [Inject]
        private readonly PlayerDataModel _playerDataModel = null!;

        [Inject]
        private readonly ResultsViewController _resultsViewController = null!;
        
        [Inject]
        private readonly IScoringCacheManager _scoringCacheManager = null!;
        
        private readonly SiraLog _logger;
        
        private readonly StandardLevelDetailViewController _levelDetailViewController;
        
        private readonly HoverHint? _hoverHint;

        private readonly HoverHintController _hoverHintController;

        private readonly HoverAreaState? _hoverAreaState;

        private readonly StatsHoverOrder? _hoverOrder;

        private readonly TMP_Text? _playCount;

        private readonly TMP_Text? _highScore;
        
        private CancellationTokenSource? _cts;

        public SPHUI(PlatformLeaderboardViewController leaderboardViewController, StandardLevelDetailViewController levelDetailViewController,
            HoverHintController hoverHintController, SiraLog logger)
        {
            _levelDetailViewController = levelDetailViewController;
            _logger = logger;
            _hoverHintController = hoverHintController;
            
            var levelStatsView = leaderboardViewController._levelStatsView;
            var levelParamsPanel = levelDetailViewController._standardLevelDetailView._levelParamsPanel;

            try
            {
                _logger.Info("Preparing SPU UI");
                _hoverHint = PrepareHoverHint((RectTransform)levelStatsView.transform, levelParamsPanel);
                _hoverHintController = _hoverHint.GetField<HoverHintController, HoverHint>("_hoverHintController") ?? hoverHintController;
                _hoverHint.SetField("_hoverHintController", _hoverHintController);
                _hoverAreaState = _hoverHint.GetComponent<HoverAreaState>();
                _playCount = PreparePlayCount(levelStatsView, out var highScore);
                _highScore = highScore;
                _hoverHint.transform.SetAsLastSibling();
                _hoverOrder = leaderboardViewController.gameObject.AddComponent<StatsHoverOrder>();
                _hoverOrder.Initialize((RectTransform)levelStatsView.transform);
            }
            catch (Exception ex)
            {
                _logger.Critical($"Failed to prepare SPU UI: {ex.Message}");
                _logger.Error(ex);
            }
        }
        
        private HoverHint PrepareHoverHint(RectTransform parent, LevelParamsPanel levelParamsPanel)
        {
            _logger.Debug("Preparing hover area for play history");
            var template = levelParamsPanel.GetComponentsInChildren<RectTransform>().First(x => x.name == "NotesCount");
            var label = UObject.Instantiate(template, parent);
            label.name = "SPH HoverArea";
            label.MatchParent();
            UObject.Destroy(label.Find("Icon").gameObject);
            UObject.Destroy(label.Find("ValueText").gameObject);
            var localizedHint = label.GetComponent<LocalizedHoverHint>();
            localizedHint.enabled = false;
            UObject.Destroy(localizedHint);

            label.gameObject.AddComponent<HoverAreaState>();
            var hoverHint = label.GetComponent<HoverHint>();
            hoverHint.text = "";
            return hoverHint;
        }

        private TMP_Text PreparePlayCount(LevelStatsView levelStatsView, out TMP_Text highScoreText)
        {
            _logger.Debug("Preparing extra level stats ui for play count");
            var maxCombo = levelStatsView.GetComponentsInChildren<RectTransform>().First(x => x.name == "MaxCombo");
            var highscore = levelStatsView.GetComponentsInChildren<RectTransform>().First(x => x.name == "Highscore");
            var maxRank = levelStatsView.GetComponentsInChildren<RectTransform>().First(x => x.name == "MaxRank");
            highScoreText = highscore.GetComponentsInChildren<TextMeshProUGUI>().First(x => x.name == "Value");

            var playCount = UObject.Instantiate(maxCombo, levelStatsView.transform);
            playCount.name = "SPH PlayCount";

            const float w = 0.225f;
            maxCombo.anchorMin = new Vector2(0f, .5f);
            maxCombo.anchorMax = new Vector2(1 * w, .5f);
            highscore.anchorMin = new Vector2(1 * w, .5f);
            highscore.anchorMax = new Vector2(2 * w, .5f);
            maxRank.anchorMin = new Vector2(2 * w, .5f);
            maxRank.anchorMax = new Vector2(3 * w, .5f);
            playCount.anchorMin = new Vector2(3 * w, .5f);
            playCount.anchorMax = new Vector2(4 * w, .5f);
                    
            var title = playCount.GetComponentsInChildren<TextMeshProUGUI>().First(x => x.name == "Title");
            // The text behind the title of the cloned maxCombo component is localized, so we need 
            // to destroy the localized component so that we can change its text to "Play Count", 
            // otherwise the localized text is retained in favour of ours
            UObject.Destroy(title.GetComponentInChildren<LocalizedTextMeshProUGUI>());
            title.text = "Play Count";
            var text = playCount.GetComponentsInChildren<TextMeshProUGUI>().First(x => x.name == "Value");
            return text;
        }
        
        
        public void Initialize()
        {
            if (_hoverHint == null || _playCount == null || _highScore == null) return;
            
            _levelDetailViewController.didChangeDifficultyBeatmapEvent -= OnDifficultyChanged;
            _levelDetailViewController.didChangeDifficultyBeatmapEvent += OnDifficultyChanged;
            _levelDetailViewController.didChangeContentEvent -= OnContentChanged;
            _levelDetailViewController.didChangeContentEvent += OnContentChanged;
            _resultsViewController.continueButtonPressedEvent -= OnPlayResultDismiss;
            _resultsViewController.continueButtonPressedEvent += OnPlayResultDismiss;
        }

        public void Dispose()
        {
            _levelDetailViewController.didChangeDifficultyBeatmapEvent -= OnDifficultyChanged;
            _levelDetailViewController.didChangeContentEvent -= OnContentChanged;
            _resultsViewController.continueButtonPressedEvent -= OnPlayResultDismiss;
            
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            if (_hoverOrder != null) UObject.Destroy(_hoverOrder);
        }
        
        private void OnDifficultyChanged(StandardLevelDetailViewController controller)
        {
            UpdateUI(controller.beatmapKey, controller.beatmapLevel);
        }

        private void OnContentChanged(StandardLevelDetailViewController controller, StandardLevelDetailViewController.ContentType contentType)
        {
            if (contentType == StandardLevelDetailViewController.ContentType.OwnedAndReady)
            {
                UpdateUI(controller.beatmapKey, controller.beatmapLevel);
            }
        }

        private void OnPlayResultDismiss(ResultsViewController _)
        {
            UpdateUI(_levelDetailViewController.beatmapKey, _levelDetailViewController.beatmapLevel);
        }

        private void UpdateUI(BeatmapKey beatmapKey, BeatmapLevel? beatmap)
        {
            if (beatmap == null) return;
            _logger.Info("Updating SPH UI");
            _logger.Debug($"{beatmap.songName} {beatmapKey.characteristic.SerializedName()} {beatmapKey.difficulty}");

            var records = _recordsManager.GetRecords(beatmapKey);
            var stats = _playerDataModel.playerData.TryGetPlayerLevelStatsData(beatmapKey);
            int? highScore = stats?.validScore == true ? stats.highScore : null;
            SetStats(beatmapKey, records.Count);

            _hoverHint!.text = GetRecordsText(records, null);
            if (_hoverAreaState?.IsHovered == true)
            {
                _hoverHintController.ShowHint(_hoverHint);
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            if (records.Count == 0 && !highScore.HasValue) return;

            var token = _cts.Token;
            _scoringCacheManager.GetScoringInfo(beatmapKey, beatmap, token)
                .ContinueWith(task =>
                {
                    if (token.IsCancellationRequested) return;
                    if (task.IsFaulted && task.Exception != null)
                    {
                        _logger.Error($"Failed to update SPH ui: {task.Exception.Message}");
                        _logger.Error(task.Exception);
                        return;
                    }

                    var cache = task.Result;
                    _logger.Debug($"Scoring data ready for {beatmapKey.SerializedName()}: {cache}");
                    _hoverHint!.text = GetRecordsText(records, cache);
                    if (highScore.HasValue && cache.MaxMultipliedScore > 0)
                    {
                        var percentage = highScore.Value * 100d / cache.MaxMultipliedScore;
                        _highScore!.text = percentage.ToString("0.00", CultureInfo.InvariantCulture) + "%";
                    }

                    if (_hoverAreaState?.IsHovered == true)
                    {
                        _hoverHintController.ShowHint(_hoverHint!);
                    }
                }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, UnityMainThreadTaskScheduler.Default);
        }
        
        private string GetRecordsText(IEnumerable<ISongPlayRecord> records, LevelScoringCache? cache)
        {
            var config = PluginConfig.Instance;
            records =
                from record in records
                where config.ShowFailed || record.LevelEnd == LevelEndType.Cleared
                select record;
            
            records = config.SortByDate 
                ? records.OrderByDescending(record => record.LocalTime) 
                : records.OrderByDescending(record => record.ModifiedScore);

            var truncated = records.Take(10).ToList();
            
            if (truncated.Count == 0)
            {
                return "No saved play history for this difficulty.";
            }

            var fullMaxScore = cache?.MaxMultipliedScore ?? -1;
            var notesCount = cache?.NotesCount ?? -1;
            var isV2Score = cache?.IsV2Score == true;
            
            var builder = new StringBuilder(200);
            for (var index = 0; index < truncated.Count; index++)
            {
                var r = truncated[index];
                _logger.Trace($"Record: {r}");
                builder.TMPSpace(truncated.Count - index - 1);
                builder.Append($"<size=2.5><color=#1a252bff> {r.LocalTime:d}</color></size>");
                builder.Append($"<size=3.5><color=#0f4c75ff> {r.ModifiedScore}</color></size>");
                
                var levelFinished = r.LevelEnd == LevelEndType.Cleared;

                #region acc
                var denominator = -1;
                if (levelFinished || !config.AverageAccuracy)
                {
                    denominator = fullMaxScore;
                }
                else if (r.MaxRawScore != null)
                {
                    denominator = r.MaxRawScore.Value;
                }
                else if (isV2Score)
                {
                    denominator = ScoreUtils.CalculateV2MaxScore(r.LastNote);
                }
                // Only display acc if we can get the max scores with the data we have on hand.
                // Some soft failed record saved total score instead of at the time of fail   
                // result in the the saved score be much greater than the max score
                if (denominator > 0 && r.RawScore <= denominator)
                {
                    var accuracy = r.RawScore / (float)denominator * 100f;
                    builder.Append($"<size=3.5><color=#368cc6ff> {accuracy:0.00}%</color></size>");
                }
                #endregion

                #region modifiers
                var param = r.Params;
                if (levelFinished && r.ModifiedScore == r.RawScore)
                {
                    // NF penalty definitely not triggered 
                    param &= ~SongPlayParam.NoFail;
                }

                var paramString = param.ToParamString();
                if (paramString.Length == 0 && r.RawScore != r.ModifiedScore)
                {
                    paramString = "?!";
                }
                
                if (paramString.Length > 0)
                {
                    builder.Append($"<size=2><color=#1a252bff> {paramString}</color></size>");
                }
                #endregion
                
                #region level end state
                if (config.ShowFailed)
                {
                    var notesRemaining = notesCount - r.LastNote;
                    if (r.LastNote == -1)
                        builder.Append($"<size=2.5><color=#1a252bff> cleared</color></size>");
                    else if (r.LastNote == 0) // old record (success, fail, or practice)
                        builder.Append($"<size=2.5><color=#584153ff> unknown</color></size>");
                    else if (notesCount >= 0)
                        builder.Append($"<size=2.5><color=#ff5722ff> +{notesRemaining} notes</color></size>");
                    else
                        builder.Append("<size=2.5><color=#ff5722ff> failed</color></size>");
                }
                #endregion

                builder.TMPSpace(index);
                builder.AppendLine();
            }
 
            return builder.ToString();
        }

        private void SetStats(BeatmapKey beatmap, int sphPlayCount)
        {
            var playCount = _playerDataModel.playerData.levelsStatsData.TryGetValue(beatmap, out var data)
                            && data.validScore
                ? Math.Max(data.playCount, sphPlayCount)
                : sphPlayCount;
            _playCount!.text = playCount.ToString();
        }
    }

    internal sealed class HoverAreaState : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public bool IsHovered { get; private set; }

        public void OnPointerEnter(PointerEventData eventData)
        {
            IsHovered = true;
            Plugin.Log.Debug("Play history hover entered");
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            IsHovered = false;
            var target = eventData.pointerCurrentRaycast.gameObject;
            Plugin.Log.Debug($"Play history hover exited to {(target != null ? target.name : "none")}");
        }

        private void OnDisable() => IsHovered = false;
    }
}
