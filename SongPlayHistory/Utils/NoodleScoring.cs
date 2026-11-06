using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IPA.Loader;

namespace SongPlayHistory.Utils;

internal static class NoodleScoring
{
    internal static IReadonlyBeatmapData GetScoringData(BeatmapKey key, IReadonlyBeatmapData data)
    {
        if (!PluginManager.EnabledPlugins.Any(plugin => plugin.Id == "NoodleExtensions") ||
            SongCore.Collections.GetCustomLevelSongDifficultyData(key)?.additionalDifficultyData?._requirements
                ?.Contains("Noodle Extensions") != true)
            return data;

        var properties = new Dictionary<Type, PropertyInfo?>();
        var version = data.GetType().GetProperty("version")?.GetValue(data) as Version;
        var fakeKey = version?.Major == 2 ? "_fake" : "NE_fake";

        bool IsFake(BeatmapDataItem item)
        {
            if (item is not NoteData && item is not SliderData) return false;
            var type = item.GetType();
            if (!properties.TryGetValue(type, out var property))
            {
                property = type.GetProperty("customData", BindingFlags.Public | BindingFlags.Instance);
                properties.Add(type, property);
            }

            return property?.GetValue(item) is IDictionary<string, object?> customData &&
                   customData.TryGetValue(fakeKey, out var fake) && fake is true;
        }

        // Menu loading precedes Noodle's gameplay deserialization that marks fake notes unscorable.
        if (!data.GetBeatmapDataItems<NoteData>(0).Any(IsFake) &&
            !data.GetBeatmapDataItems<SliderData>(0).Any(IsFake))
            return data;

        // Keep the loaded map untouched; native scoring still handles arcs, chains and multiplier order.
        return data.GetFilteredCopy(item => IsFake(item) ? null! : item);
    }
}
