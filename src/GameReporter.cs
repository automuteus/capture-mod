using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AmongUs.Data;
using Il2CppInterop.Runtime.Attributes;
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

    // What happened to a player, by the numbers Galactus expects.
    enum PlayerAction { Joined, Left, Died, ChangedColor, ForceUpdated, Disconnected, Exiled }

    record Lobby(string LobbyCode, int Region, int Map);

    record Player(string Name, int Color, bool IsDead, bool Disconnected);

    Galactus? reportingTo;
    int connections;
    Phase? lastPhase;
    Lobby? lastLobby;
    readonly Dictionary<byte, Player> players = new();
    // Exiled players, who the game only marks dead once the exile animation ends. They were reported dead when the
    // meeting closed, so their death mustn't be reported again as a kill.
    readonly HashSet<byte> exiled = new();
    readonly HashSet<byte> present = new();
    // Who the vote is exiling, read while the meeting shows its results: the meeting can be gone by the first frame
    // the game is back to tasks.
    byte? exiling;

    public GameReporter(IntPtr ptr) : base(ptr)
    {
    }

    void Update()
    {
        var galactus = CapturePlugin.Galactus;
        if (galactus is null || !galactus.Connected) return;

        var resend = galactus != reportingTo || galactus.Connections != connections;
        if (resend)
        {
            reportingTo = galactus;
            connections = galactus.Connections;
            lastPhase = null;
            lastLobby = null;
            players.Clear();
        }

        var phase = CurrentPhase();
        if (phase != lastPhase)
        {
            // Reported before the phase, so the bot already counts the exiled player as dead when it applies the
            // tasks phase's mutes.
            if (lastPhase == Phase.Discussion && phase == Phase.Tasks) ReportExile(galactus);
            if (phase is Phase.Lobby or Phase.Menu) exiled.Clear();
            exiling = null;

            galactus.Emit("state", ((int)phase).ToString());
            lastPhase = phase;
        }

        var lobby = CurrentLobby();
        if (lobby is not null && lobby != lastLobby)
        {
            galactus.Emit("lobby", JsonSerializer.Serialize(lobby));
            lastLobby = lobby;
        }

        if (phase == Phase.Discussion)
        {
            var meeting = MeetingHud.Instance;
            var player = meeting == null ? null : meeting.exiledPlayer;
            if (player != null) exiling = player.PlayerId;
        }

        ReportPlayers(galactus, resend);
    }

    [HideFromIl2Cpp]
    void ReportExile(Galactus galactus)
    {
        if (exiling is not { } id || !players.TryGetValue(id, out var known)) return;

        exiled.Add(id);
        players[id] = known with { IsDead = true };
        EmitPlayer(galactus, PlayerAction.Exiled, players[id]);
    }

    [HideFromIl2Cpp]
    void ReportPlayers(Galactus galactus, bool resend)
    {
        present.Clear();
        var data = GameData.Instance;
        if (data != null)
        {
            var all = data.AllPlayers;
            for (var i = 0; i < all.Count; i++)
            {
                var info = all[i];
                // A joining player has no name or outfit until the game has their details.
                if (info == null || info.IsIncomplete || string.IsNullOrEmpty(info.PlayerName)) continue;

                var id = info.PlayerId;
                // Once the game has caught up with an exile, it's the one to say whether the player is still dead.
                if (info.IsDead) exiled.Remove(id);
                var now = new Player(info.PlayerName, info.DefaultOutfit.ColorId, info.IsDead || exiled.Contains(id),
                    info.Disconnected);
                present.Add(id);

                if (!players.TryGetValue(id, out var before) || before.Name != now.Name)
                {
                    if (before is not null) EmitPlayer(galactus, PlayerAction.Left, before);
                    EmitPlayer(galactus, resend ? PlayerAction.ForceUpdated : PlayerAction.Joined, now);
                }
                else
                {
                    // An exile the meeting didn't name, e.g. if the meeting closed before it was read, is still an exile.
                    if (now.IsDead && !before.IsDead)
                        EmitPlayer(galactus, info.WasEjected ? PlayerAction.Exiled : PlayerAction.Died, now);
                    // Role mods can bring players back, like Town of Us's Altruist. The bot has no action for that,
                    // and takes the update as it would after a reconnect.
                    else if (!now.IsDead && before.IsDead)
                        EmitPlayer(galactus, PlayerAction.ForceUpdated, now);
                    if (now.Color != before.Color) EmitPlayer(galactus, PlayerAction.ChangedColor, now);
                    if (now.Disconnected && !before.Disconnected)
                        EmitPlayer(galactus, PlayerAction.Disconnected, now);
                }
                players[id] = now;
            }
        }

        if (players.Count == present.Count) return;
        foreach (var id in players.Keys.Where(id => !present.Contains(id)).ToList())
        {
            EmitPlayer(galactus, PlayerAction.Left, players[id]);
            players.Remove(id);
        }
    }

    static void EmitPlayer(Galactus galactus, PlayerAction action, Player player) =>
        galactus.Emit("player", JsonSerializer.Serialize(new
        {
            Action = (int)action,
            player.Name,
            player.IsDead,
            player.Disconnected,
            player.Color
        }));

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
