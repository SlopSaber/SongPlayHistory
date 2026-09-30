using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using IPA.Utilities;
using SongPlayHistory.Configuration;
using SongPlayHistory.Model;
using SongPlayHistory.VoteTracker;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static UnityEngine.Object;

namespace SongPlayHistory
{
    [HarmonyPatch(typeof(LevelListTableCell), nameof(LevelListTableCell.SetDataFromLevelAsync))]
    internal class SetDataFromLevelAsync
    {
        private static Sprite? _thumbsUp;
        private static Sprite? _thumbsDown;
        private static CancellationTokenSource? _iconLoad;

        private static Color _upColor = new Color(0.455f, 0.824f, 0.455f, 0.8f);
        private static Color _downColor = new Color(0.824f, 0.498f, 0.455f, 0.8f);
        public static bool Prepare()
        {
            return !Plugin.Instance.BeatSaverVotingInstalled;
        }

        internal static async void PrepareIcons()
        {
            if (Plugin.Instance.BeatSaverVotingInstalled) return;
            _iconLoad?.Cancel();
            _iconLoad?.Dispose();
            _iconLoad = new CancellationTokenSource();
            var token = _iconLoad.Token;
            try
            {
                var bytes = await Task.Run(() => (
                    ReadResource(@"SongPlayHistory.Assets.ThumbsUp.png"),
                    ReadResource(@"SongPlayHistory.Assets.ThumbsDown.png")), token).ConfigureAwait(false);
                await UnityGame.SwitchToMainThreadAsync();
                token.ThrowIfCancellationRequested();
                _thumbsUp = CreateSprite(bytes.Item1);
                _thumbsDown = CreateSprite(bytes.Item2);
                InMenuVoteTrackingHelper.Instance?.RefreshVotes();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error while loading vote icons: {ex}");
            }
        }

        private static byte[] ReadResource(string resourcePath)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath)
                ?? throw new InvalidDataException($"Resource not found: {resourcePath}");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        [HarmonyAfter("com.kyle1413.BeatSaber.SongCore")]
        public static void Postfix(LevelListTableCell __instance, BeatmapLevel? beatmapLevel, bool isFavorite, 
            Image ____favoritesBadgeImage, TextMeshProUGUI? ____songBpmText)
        {
            if (!PluginConfig.Instance.ShowVotes) return;
            if (beatmapLevel == null) return;
            if (_thumbsUp == null || _thumbsDown == null) return;
            if (____songBpmText != null)
            {
                if (float.TryParse(____songBpmText.text, out float bpm))
                {
                    ____songBpmText.text = bpm.ToString("0");
                }
            }

            Image? voteIcon = __instance.transform.Find("Vote")?.GetComponent<Image>();
            if (voteIcon == null)
            {
                voteIcon = Instantiate(____favoritesBadgeImage, __instance.transform);
                voteIcon.name = "Vote";
                voteIcon.rectTransform.sizeDelta = new Vector2(2.5f, 2.5f);
            }

            if (!isFavorite && InMenuVoteTrackingHelper.Instance?.TryGetVote(beatmapLevel, out var vote) == true)
            {
                if (vote == VoteType.Upvote)
                {
                    voteIcon.sprite = _thumbsUp;
                    voteIcon.color = _upColor;
                }
                else
                {
                    voteIcon.sprite = _thumbsDown;
                    voteIcon.color = _downColor;
                }
                
                voteIcon.enabled = true;
            }
            else
            {
                voteIcon.enabled = false;
            }
        }

        public static void OnUnpatch()
        {
            _iconLoad?.Cancel();
            _iconLoad?.Dispose();
            _iconLoad = null;
            foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
            {
                if (image.name == "Vote")
                {
                    Destroy(image.gameObject);
                }
            }

            DestroySprite(_thumbsUp);
            DestroySprite(_thumbsDown);
            _thumbsUp = null;
            _thumbsDown = null;
        }

        private static void DestroySprite(Sprite? sprite)
        {
            if (sprite == null) return;
            Destroy(sprite.texture);
            Destroy(sprite);
        }

        private static Sprite? CreateSprite(byte[] bytes)
        {
            Texture2D? texture = null;
            try
            {
                texture = new Texture2D(2, 2);
                if (!texture.LoadImage(bytes)) throw new InvalidDataException("Unable to decode vote icon.");

                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0, 0));
                return sprite;
            }
            catch (Exception ex)
            {
                if (texture != null) Destroy(texture);
                Plugin.Log?.Error("Error while loading a resource.\n" + ex.ToString());
                return null;
            }
        }
    }
}
