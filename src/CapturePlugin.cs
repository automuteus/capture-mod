using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace CaptureMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("Among Us.exe")]
public class CapturePlugin : BasePlugin
{
    internal static Galactus? Galactus;

    ConfigEntry<string> captureLink = null!;

    public override void Load()
    {
        captureLink = Config.Bind("Capture", "CaptureLink", "",
            "The aucapture:// link the bot posts when you start a game with /new.");
        AddComponent<GameReporter>();

        var link = CaptureLink.Parse(captureLink.Value);
        if (link is null)
        {
            Log.LogWarning("No valid capture link in the config; set CaptureLink to the aucapture:// link from /new");
            return;
        }
        Galactus = new Galactus(link, MyPluginInfo.PLUGIN_VERSION, Log.LogInfo);
        Galactus.Start();
    }
}
