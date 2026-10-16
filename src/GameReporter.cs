using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using AmongUs.Data;
using InnerNet;
using UnityEngine;

namespace CaptureMod;

// Reads the game's own state every frame and reports what changed to Galactus. After each (re)connection it reports
// everything again, because Galactus starts every connection from nothing.
public class GameReporter : MonoBehaviour
{
    // The bot's phases, by the numbers Galactus expects.
    enum Phase { Lobby, Tasks, Discussion, Menu, Ended }

    // The bot only knows the official regions. Anything else, such as a modded server, is reported as unknown.
    const int UnknownRegion = -1;

    static readonly Regex LobbyCodePattern = new("^[A-Z]{4}$|^[A-Z]{6}$");

    record Lobby(string LobbyCode, int Region, int Map);

    int connections;
    Phase? lastPhase;
    Lobby? lastLobby;

    public GameReporter(IntPtr ptr) : base(ptr)
    {
    }

    void Update()
    {
        var galactus = CapturePlugin.Galactus;
        if (galactus is null || !galactus.Connected) return;

        if (galactus.Connections != connections)
        {
            connections = galactus.Connections;
            lastPhase = null;
            lastLobby = null;
        }

        var phase = CurrentPhase();
        if (phase != lastPhase)
        {
            galactus.Emit("state", ((int)phase).ToString());
            lastPhase = phase;
        }

        var lobby = CurrentLobby();
        if (lobby is not null && lobby != lastLobby)
        {
            galactus.Emit("lobby", JsonSerializer.Serialize(lobby));
            lastLobby = lobby;
        }
    }

    static Phase CurrentPhase()
    {
        var client = AmongUsClient.Instance;
        if (client == null) return Phase.Menu;

        switch (client.GameState)
        {
            case InnerNetClient.GameStates.NotJoined:
                return Phase.Menu;
            case InnerNetClient.GameStates.Joined:
                return Phase.Lobby;
            case InnerNetClient.GameStates.Ended:
                return Phase.Ended;
            default:
                // The meeting screen stays up while the exile plays out; voice should open with the meeting, not
                // wait for it to close.
                var meeting = MeetingHud.Instance;
                return meeting != null && meeting.CurrentState < MeetingHud.MeetingStates.Proceeding
                    ? Phase.Discussion
                    : Phase.Tasks;
        }
    }

    // The lobby is known once the game has joined an online room. Local games have no code to report.
    static Lobby? CurrentLobby()
    {
        var client = AmongUsClient.Instance;
        if (client == null || client.GameState == InnerNetClient.GameStates.NotJoined) return null;

        var code = GameCode.IntToGameName(client.GameId);
        if (code is null || !LobbyCodePattern.IsMatch(code)) return null;
        // Streamer mode hides the code on screen, so keep it out of Discord too, as the Capture App does.
        if (DataManager.Settings.Gameplay.StreamerMode) code = "******";

        var map = GameOptionsManager.Instance?.CurrentGameOptions?.MapId ?? 0;
        return new Lobby(code, CurrentRegion(), map);
    }

    static int CurrentRegion()
    {
        var servers = ServerManager.Instance;
        var region = servers == null ? null : servers.CurrentRegion;
        if (region == null) return UnknownRegion;

        return region.TranslateName switch
        {
            StringNames.ServerNA => 0,
            StringNames.ServerAS => 1,
            StringNames.ServerEU => 2,
            _ => UnknownRegion
        };
    }
}
