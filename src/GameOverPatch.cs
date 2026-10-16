using System.Collections.Generic;
using System.Text.Json;
using AmongUs.GameOptions;
using HarmonyLib;

namespace CaptureMod;

// Reports how the game ended, as the game itself decided it (ADR 0003), along with every player's side.
[HarmonyPatch(typeof(AmongUsClient), "OnGameEnd")]
static class GameOverPatch
{
    // The bot's result for a game end it can't name, such as a role mod's custom win. It counts toward no one's stats.
    const int UnknownEnding = -3;

    record PlayerSide(string Name, bool IsImpostor);

    // A prefix, so every player is still in the game's data, including those who disconnected.
    static void Prefix(EndGameResult endGameResult)
    {
        var galactus = CapturePlugin.Galactus;
        if (galactus is null || !galactus.Connected) return;

        // The game's own results line up with the bot's. Role mods add their own past the end.
        var reason = (int)endGameResult.GameOverReason;
        if (reason < (int)GameOverReason.CrewmatesByVote || reason > (int)GameOverReason.HideAndSeek_ImpostorsByKills)
            reason = UnknownEnding;

        var sides = new List<PlayerSide>();
        var data = GameData.Instance;
        if (data != null)
        {
            var all = data.AllPlayers;
            for (var i = 0; i < all.Count; i++)
            {
                var info = all[i];
                if (info == null || string.IsNullOrEmpty(info.PlayerName)) continue;
                sides.Add(new PlayerSide(info.PlayerName, info.Role != null && info.Role.TeamType == RoleTeamTypes.Impostor));
            }
        }

        galactus.Emit("gameover", JsonSerializer.Serialize(new { GameOverReason = reason, PlayerInfos = sides }));
    }
}
