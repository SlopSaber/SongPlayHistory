using UnityEngine;

namespace SongPlayHistory.UI
{
    internal sealed class StatsHoverOrder : MonoBehaviour
    {
        private RectTransform? _stats;

        public void Initialize(RectTransform stats)
        {
            _stats = stats;
            KeepStatsAboveLeaderboards();
        }

        private void OnEnable() => KeepStatsAboveLeaderboards();

        private void OnTransformChildrenChanged() => KeepStatsAboveLeaderboards();

        private void KeepStatsAboveLeaderboards()
        {
            if (_stats == null || _stats.parent != transform || _stats.GetSiblingIndex() == transform.childCount - 1)
            {
                return;
            }

            // Custom leaderboard views are added as siblings after LevelStatsView.
            // Raise the whole stats row so its hover target wins overlapping raycasts.
            _stats.SetAsLastSibling();
            Plugin.Log.Debug("Raised play history stats above leaderboard content");
        }
    }
}
