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
- `DISCORD_PRISON_LOG_WEBHOOK`
- `DISCORD_HISTORY_WEBHOOK`
- `DATA_DIR=/data`

The Rust server must have WebRCON enabled (normally `rcon.web 1`) and its RCON port must accept outbound connections from Railway.

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
