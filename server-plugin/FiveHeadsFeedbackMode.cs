using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Plugins;

namespace Carbon.Plugins;

[Info("FiveHeadsFeedbackMode", "5HeadNN", "1.0.2")]
[Description("Read-only server console lookup of a connected player's CoreLobby mode.")]
public class FiveHeadsFeedbackMode : CarbonPlugin
{
    [PluginReference] private Plugin CoreLobby;

    private void OnServerInitialized()
    {
        Puts("Feedback mode lookup ready. Console command: fiveheads.feedbackmode <SteamID64>");
    }

    [ConsoleCommand("fiveheads.feedbackmode")]
    private void FeedbackMode(ConsoleSystem.Arg arg)
    {
        if (arg == null) return;
        if (arg.Connection != null) return;
        if (arg.Args == null || arg.Args.Length != 1 || !ulong.TryParse(arg.Args[0], out ulong id))
        {
            arg.ReplyWith("Usage: fiveheads.feedbackmode <SteamID64>");
            return;
        }
        string mode = null;
        string status = "unknown";
        try
        {
            var player = BasePlayer.activePlayerList.FirstOrDefault(p => p != null && p.IsConnected && (ulong)p.userID == id);
            var core = CoreLobby ?? plugins.Find("CoreLobby");
            if (core == null) status = "corelobby_not_loaded";
            else if (player == null) status = "player_not_online";
            else
            {
                var direct = core.Call("GetPlayerGamemode", id) as string;
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    mode = direct;
                    status = "direct";
                }
                else
                {
                    var all = core.Call("GetAllGamemodePlayers") as IDictionary<ulong, string>;
                    if (all == null) status = "mode_api_unavailable";
                    else if (all.TryGetValue(id, out string tracked))
                    {
                        if (!string.IsNullOrWhiteSpace(tracked)) { mode = tracked; status = "tracked"; }
                        else status = "empty_mode";
                    }
                    else { mode = "Lobby"; status = "not_in_tracked_mode"; }
                }
            }
        }
        catch (Exception ex) { status = "lookup_error:" + ex.GetType().Name; mode = null; }
        var result = JsonConvert.SerializeObject(new { steam_id = id.ToString(), mode = mode, status = status, observed_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        Puts(result);
        arg.ReplyWith(result);
    }
}
