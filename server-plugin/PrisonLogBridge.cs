// Rust-side bridge for admin jail UI plugins that invoke OnPlayerJail,
// or that can invoke On5HeadsPrisoned after a successful incarceration.
// Install in oxide/plugins/PrisonLogBridge.cs (or equivalent Carbon folder).
using Newtonsoft.Json;
using Oxide.Core;
using System;

namespace Oxide.Plugins
{
    [Info("PrisonLogBridge", "5Heads", "1.0.0")]
    [Description("Reports confirmed manual jail actions to 5Heads Arena's Discord logging bot via Rust console.")]
    public class PrisonLogBridge : RustPlugin
    {
        private const string Marker = "[5HEADS_PRISON]";
        private const string MissingReason = "Not supplied by admin UI";

        private void Emit(string steamId, string username, string reason)
        {
            if (string.IsNullOrEmpty(steamId) || !steamId.StartsWith("7656119") || steamId.Length != 17)
                return;

            var payload = new
            {
                steamId = steamId,
                username = string.IsNullOrEmpty(username) ? "Unknown" : username,
                reason = string.IsNullOrWhiteSpace(reason) ? MissingReason : reason
            };
            // Rust WebRCON forwards console output to the Node bot. No webhook
            // credentials or public HTTP listener are needed on the game server.
            Puts(Marker + " " + JsonConvert.SerializeObject(payload));
        }

        // Some Rust Jail plugins (including versions of the 'Jail' plugin)
        // emit this hook when a player is actually jailed.
        private void OnPlayerJail(ulong playerId, double sentenceSeconds)
        {
            var player = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
            Emit(playerId.ToString(), player == null ? "Unknown" : player.displayName, MissingReason);
        }

        // For UI plugins that do not emit OnPlayerJail, add this ONE line to
        // their code immediately AFTER a successful imprisonment:
        // Interface.CallHook("On5HeadsPrisoned", target.UserIDString, target.displayName, reason);
        //
        // If the UI has no reason input, use the string "Not supplied by admin UI".
        private void On5HeadsPrisoned(string steamId, string playerName, string reason)
        {
            Emit(steamId, playerName, reason);
        }
    }
}
