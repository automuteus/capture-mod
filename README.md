# AutoMuteUs Capture Mod

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin that does the [Capture App](https://github.com/automuteus/amonguscapture)'s job from inside Among Us: it reports game events to [AutoMuteUs](https://github.com/automuteus/automuteus) so the bot can mute and unmute players. It reads the game's own state instead of memory offsets, so it doesn't need updating for every game patch.

It supports the latest Steam release of Among Us (currently 2026.9.29) on BepInEx 6 for IL2CPP.

## Use

1. Put `AutoMuteUsCapture.dll` in `BepInEx/plugins`.
2. Start a game in Discord with `/new` and copy the `aucapture://` link it posts.
3. Set `CaptureLink` in `BepInEx/config/us.automute.capture.cfg` to that link, then start Among Us.

## Build

```sh
dotnet build src
```

The game's interop assemblies are generated on the first build.
