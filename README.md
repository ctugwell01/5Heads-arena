# 5Heads Arena Bot

Moderation bot for the 5Heads Rust Arena server.

## Railway setup

Required variables:

- `RCON_HOST` — Rust server hostname or IP only (no `ws://`)
- `RCON_PORT` — Rust WebRCON port
- `RCON_PASS` — Rust RCON password

Recommended variables:

- `ANTHROPIC_API_KEY` — enables AI moderation
- `DISCORD_WEBHOOK`
- `DISCORD_VOICE_WEBHOOK`
- `DISCORD_RECORDINGS_WEBHOOK`
- `DISCORD_VIP_LOG_WEBHOOK` — posts reason-hidden prison alerts to the VIP channel
- `DISCORD_OWNER_LOG_WEBHOOK` — posts prison alerts including the reason to the owner-only channel
- `DISCORD_BOT_TOKEN` — alternative to the two webhooks: a Discord bot token with Send Messages + Embed Links permissions in both channels
- `DISCORD_HISTORY_WEBHOOK`
- `DATA_DIR=/data`

The Rust server must have WebRCON enabled (normally `rcon.web 1`) and its RCON port must accept outbound connections from Railway.

## Discord prison logging: owner vs VIP

When the bot jails a player, **two separate alerts** are generated:

- **Owner logs** (Discord channel [1558850603533213867](https://discord.com/channels/1540790437508546700/1558850603533213867)): player, Steam ID/profile, UTC timestamp, reason, offence count if applicable.
- **VIP** (Discord channel [1558442485364293682](https://discord.com/channels/1540790437508546700/1558442485364293682)): player, Steam ID/profile, UTC timestamp **only**. No reason or offence detail is included.

You can configure delivery in Railway in **one** of two ways:

1. **Webhooks (recommended)**: in each Discord channel, open **Edit Channel → Integrations → Webhooks → New Webhook**, copy the URL, and set the matching Railway variable (`DISCORD_VIP_LOG_WEBHOOK` and `DISCORD_OWNER_LOG_WEBHOOK`). Keep webhook URLs secret. The webhook determines the destination channel.
2. **Discord bot token**: set `DISCORD_BOT_TOKEN` in Railway and invite the bot into the Discord server with **View Channel**, **Send Messages**, and **Embed Links** permission for the two channels. The code defaults to the channel IDs above; optionally override them via `DISCORD_VIP_CHANNEL_ID` and `DISCORD_OWNER_CHANNEL_ID`.

**Important:** Discord channel links or channel IDs are *not* webhooks and cannot send messages alone. A bot token or the two webhook URLs must be configured. Detailed owner messages deliberately **do not** fall back to `DISCORD_WEBHOOK` or the old `DISCORD_PRISON_LOG_WEBHOOK`, because those could expose reasons in an unintended channel.

Both channels receive alerts for automatic prison events and, once the updated Rust plugin is installed, manual UI prison events. The VIP channel is a redacted notification feed, not a list of VIP players. The updated Prison plugin displays the reason in public Rust chat, but not in VIP Discord. Its releases are also branded **5Heads Arena**. Other independent prison plugins may produce their own notices. Stored prison history retains the reason for moderator review.

## Prison UI plugin (manual and automatic jail events)

The actual custom **Prison v3.4.0** Rust plugin has been patched as **Prison v3.4.5** in [`server-plugin/Prison.cs`](server-plugin/Prison.cs). **Install this file on your Rust game server** as `oxide/plugins/Prison.cs` or the equivalent Carbon plugin path, replacing the previous Prison plugin. Reload with `oxide.reload Prison` in the Rust server console. Deploying the Railway Node service does **not** install Rust plugins.

The patched `JailPlayer()` function broadcasts a screenshot-style prison message and emits a `[5HEADS_PRISON]` JSON message to the Rust server console for every successful jail (admin UI, `/prison`, `prison` console command, offline pending jail on connection, and `ApiJailPlayer`), including Steam ID, original username, actual prison reason and sentence duration. The Railway bot consumes this event via WebRCON and sends separate messages:
- **Owner logs**: includes reason.
- **VIP**: shows who and when, **hides the reason**.

The old hard-coded exclusion for two admin Steam IDs has been removed. The in-game broadcast uses **5Heads Arena** branding with a green header, gold player name and Steam ID, sentence duration and reason. The jailed player also receives their reason privately. **Reasons are visible to everyone in Rust chat**, but remain hidden from the VIP Discord channel. The old `Discord.WebhookURL` plugin setting is no longer used; Discord credentials remain configured in Railway as `DISCORD_OWNER_LOG_WEBHOOK` and `DISCORD_VIP_LOG_WEBHOOK`.

**Announcement format:**

```
[5Heads Arena] PlayerName(7656119...) locked up for 20 minutes - Reason: Toxicity
```

Automatic sentence-completed releases are announced as `[5Heads Arena] PlayerName(7656119...) has been released - sentence completed`. Admin releases say `released by admin`. The original screenshot's `Combat Prison` release line was not present in the uploaded `Prison.cs`; if another plugin is generating it, that plugin must be reconfigured, edited or disabled separately.

Timed and permanent sentences are formatted from the actual server plugin sentence. The Arena bot now sends an optional `--duration-minutes=N` argument with its automatic jail commands to keep the prison plugin's announced sentence aligned with its timer. The Arena bot no longer sends a duplicate in-game prison notification. Manual admin UI jails use the configured Prison sentence length. The original Combat Prison plugin that produces the screenshot is separate; only events handled by this `Prison.cs` and the bot are affected.

**Do not also install `server-plugin/PrisonLogBridge.cs` for this version of Prison**. That is a fallback sample bridge for other jail plugins which expose a different hook; the custom Prison plugin now emits the required event itself.

**Testing (no jail needed):** An authorised admin already able to run `!history` can type `!testprisonlogs` in game chat. This posts labelled test messages to both Discord channels. To test the actual prison UI, jail and release a player via `/prison`, then look for `[5HEADS_PRISON]` in the Rust console and `[PRISON EVENT] UI event received` plus both Discord delivery messages in Railway. A successful webhook test on its own does not prove the Rust plugin was updated.


## Prison cell door decay protection (v3.4.5)

**What went wrong:** The original anti-decay hook used `Prison.Radius` (6 metres by default), which is also the player's movement boundary. A prison door or building block farther than 6m from the main prison position **and every saved cell location** could still decay. Separately, the Rust-side `Prison.cs` must actually be loaded; a successful Railway deployment does not guarantee the game-server plugin is active.

**Fix:** The updated plugin now protects **all door types and building block grades** from *decay damage only*, within an independent **40-metre** `Prison.DecayProtectionRadius` of the prison location or **any saved cell**. It does not alter `Prison.Radius` (player movement), prison timers, PvP/explosion damage, the VIP privacy rule, or existing data. If your prison is larger, set the radius just large enough to encompass its structures so nearby unrelated buildings aren't protected.

**Rust server installation & diagnosis:**

1. Place [`server-plugin/Prison.cs`](server-plugin/Prison.cs) in `oxide/plugins/Prison.cs` (or the equivalent Carbon plugin folder) **on the Rust server** and run `oxide.reload Prison`. Confirm the server console reports the plugin loaded without compiler errors and logs `[5Heads Arena] Prison anti-decay radius 40.0m ...`.
2. In the actual prison, stand next to a cell door and enter **`/prisondecay`** as an admin with `prison.use`. It reports whether your character is **INSIDE** the protected area, the distance to the nearest prison/cell position, the configured radius, and number of saved cells. This checks your position as an approximation of the door's position.
3. If **OUTSIDE**, increase `Prison.DecayProtectionRadius` in `oxide/config/Prison.json`, then reload the plugin, or configure additional jail cell positions using `/addcell`. Do not change `Prison.Radius` just to address decay: that would also affect prison containment.
4. **Emergency upkeep:** Keep a Tool Cupboard authorised and stocked with sufficient building upkeep while testing. Rust can still decay structures whenever the plugin is unloaded, fails to compile, is outside the protected area, or another plugin interferes.
5. Replace/rebuild already destroyed cell doors, as decay protection prevents future damage but does not restore missing entities.

The protection only applies to doors and building blocks, not every deployable object. If doors inside the protected radius still decay with the plugin active, check the Rust game-server console for compiler errors and other decay plugins; Railway application logs alone cannot diagnose the Rust plugin.

## Racism prison escalation (v3.4.4)

Players get escalating **timed** prison sentences for the `Racism` reason; they are automatically released when the timer expires:

| Racism offence | Sentence |
| --- | --- |
| 1st | 5 minutes |
| 2nd | 10 minutes |
| 3rd | 15 minutes |
| 4th | 30 minutes |
| 5th | 1 hour |
| 6th and any later | 2 hours |

There is no automatic permanent stage for this offence. After the sixth offence the last stage repeats, instead of increasing indefinitely. Offence counts remain in the existing `PrisonOffenseCounts` data file, and each new jail uses the correct stage based on that saved count. The plugin migrates the original default `Escalation.Racism` ladder (`[5, 20, 240, 0]`) in the Rust server's existing config to `[5, 10, 15, 30, 60, 120]` on plugin startup, and migrates the original fallback sentence of 120 minutes to 5 minutes. Custom non-default ladders are preserved. **Do not delete the config or offence-history files.**

The changed ladder applies to **new prison events**; an already-running sentence keeps its previously assigned duration. `Toxicity` and `Exploiting` punishment ladders are unchanged. The Railway bot's automatic `HateSpeech` timer is a separate mechanism and is not changed by this Rust plugin setting.

## Persistence

Mount a Railway Volume at `/data`. The bot stores learned examples, blocklist additions, offence counts and prison history there. Runtime history is no longer committed back to GitHub, preventing moderation events from triggering new Railway deployments.

The legacy GitHub history file can still be loaded at startup when `GITHUB_TOKEN` is configured. Defaults:

- `GITHUB_REPO=ctugwell01/5Heads-arena`
- `GITHUB_PATH=prison_history_arena.json`

## Health endpoints

- `/health` — process health; always returns HTTP 200 while the bot process is alive
- `/ready` — returns HTTP 200 only when Rust RCON is connected

Both responses include the current RCON state and process uptime.

## Diagnostics

Useful runtime log messages:

- `[RCON] Connected to Rust RCON`
- `[RCON] Error ECONNREFUSED`
- `[RCON] Error ETIMEDOUT`
- `[RCON] Handshake rejected with HTTP ...`
- `[RCON] Disconnected. code=...`

The bot redacts the RCON password from connection logs.

## Local check

```bash
npm install
npm run check
```
