using BeatSaberMarkupLanguage.Settings;
using Zenject;
using System;

namespace SongPlayHistory.UI;

internal class MenuSettingsManager: IInitializable, IDisposable
{
    private readonly SettingsController _settingsController;
    private readonly BSMLSettings _bsmlSettings;
    
    public MenuSettingsManager(SettingsController settingsController, BSMLSettings bsmlSettings)
    {
        _settingsController = settingsController;
        _bsmlSettings = bsmlSettings;
    }
    
    public void Initialize()
    {
        _bsmlSettings.AddSettingsMenu("Song Play History", "SongPlayHistory.UI.Settings.bsml", _settingsController);
    }

    public void Dispose()
    {
        _bsmlSettings.RemoveSettingsMenu(_settingsController);
    }
}
