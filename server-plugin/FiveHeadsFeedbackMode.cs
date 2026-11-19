using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Plugins;

namespace Carbon.Plugins;

[Info("FiveHeadsFeedbackMode", "5HeadNN", "1.0.0")]
[Description("Read-only server console lookup of a connected player's CoreLobby mode.")]
public class FiveHeadsFeedbackMode : CarbonPlugin
{
    [PluginReference] private Plugin CoreLobby;

    [ConsoleCommand("fiveheads.feedbackmode")]
    private void FeedbackMode(ConsoleSystem.Arg arg)
    {
        if (arg == null || arg.Connection != null) return;
        if (arg.Args == null || arg.Args.Length != 1 || !ulong.TryParse(arg.Args[0], out ulong id)) return;
        string mode = null;
        try
        {
            var player = BasePlayer.activePlayerList.FirstOrDefault(p => p != null && p.IsConnected && (ulong)p.userID == id);
            var core = CoreLobby ?? plugins.Find("CoreLobby");
            if (player != null && core != null)
            {
                var all = core.Call("GetAllGamemodePlayers") as IDictionary<ulong, string>;
                var direct = core.Call("GetPlayerGamemode", id) as string;
                if (!string.IsNullOrWhiteSpace(direct)) mode = direct;
                else if (all != null && all.TryGetValue(id, out string tracked) && !string.IsNullOrWhiteSpace(tracked)) mode = tracked;
                else if (all != null && !all.ContainsKey(id)) mode = "Lobby";
            }
        }
        catch { mode = null; }
        arg.ReplyWith(JsonConvert.SerializeObject(new { steam_id = id.ToString(), mode = mode, observed_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
    }
}
