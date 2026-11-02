using SongPlayHistory.SongPlayTracking;
using Zenject;

namespace SongPlayHistory.Installers
{
    public class SongPlayTrackingInstaller : Installer<SongPlayTrackingInstaller>
    {
        public override void InstallBindings()
        {
            Plugin.Log.Debug("Binding ScoreTracker");
            Container.BindInterfacesAndSelfTo<ScoreTracker>().AsSingle();
            Container.BindInterfacesTo<SongPlayTracker>().AsSingle();
        }
    }
}
