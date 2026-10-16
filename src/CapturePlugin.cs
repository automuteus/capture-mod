using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace CaptureMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("Among Us.exe")]
public class CapturePlugin : BasePlugin
{
    internal static Galactus? Galactus;

    static ConfigEntry<string> captureLink = null!;
    static ManualLogSource log = null!;

    public override void Load()
    {
        log = Log;
        // Remembered so a restarted game reconnects to the same bot game. A link whose game has ended does no harm:
        // Galactus accepts it, and nothing happens until a bot game claims the code.
        captureLink = Config.Bind("Capture", "CaptureLink", "",
            "The aucapture:// link the bot posts when you start a game with /new. Set by the Paste capture link button.");
        AddComponent<GameReporter>();
        AddComponent<CaptureLinkButton>();
        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

        var link = CaptureLink.Parse(captureLink.Value);
        if (link is not null) Connect(link);
    }

    // Connects to the bot game a Capture Link names, in place of whatever this was connected to.
    internal static void Connect(CaptureLink link)
    {
        if (Galactus is { Replaced: false } current && current.Link == link) return;

        Galactus?.Stop();
        captureLink.Value = link.ToString();
        Galactus = new Galactus(link, MyPluginInfo.PLUGIN_VERSION, log.LogInfo);
        Galactus.Start();
    }
}
