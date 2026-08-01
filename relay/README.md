# Discord Feedback Relay

Bridges the app's one-way Discord webhook to two-way communication:

- The app creates a per-report Discord thread (webhook) and registers it here.
- This relay reads replies in each thread with a **bot token** and stores them.
- The app polls this relay and shows replies as toasts; the user can reply
  in-app, and the relay posts the reply back into the thread via the webhook.

## How the loop works

1. User clicks **Send to Dev** in the app → app posts to the Discord webhook
   (creates thread `Diag Pack {REPORT-ID} — …`), captures the thread ID, and
   calls `POST /register` on this relay.
2. The relay polls each registered thread for new messages and stores them
   (webhook-authored messages are ignored). Messages are backfilled page by
   page, and edits are propagated (an edited reply re-triggers delivery).
3. The app polls `GET /replies/{REPORT-ID}?after=<id>&edited_after=<time>`
   every 60 s while running. New replies show a toast:
   *"Support reply — Report {REPORT-ID}"*.
4. You reply to the user **in Discord, in that thread**. Replies starting with
   `[FIX]` show a *"Fix available"* toast and trigger the app's update check.
5. The user can reply in-app (Chat button → Send Reply); the relay posts it
   into the thread, so you see it in Discord.

## Setup (one time)

1. Create a bot at https://discord.com/developers/applications
   - App → Bot → Reset Token → copy it.
   - No privileged gateway intents are required (no gateway here).
2. Invite the bot to your Discord server:
   - `https://discord.com/oauth2/authorize?client_id=YOUR_CLIENT_ID&permissions=68608&scope=bot`
   - Permissions needed: View Channels, Read Message History (the webhook
     already covers posting).
3. Pick a shared secret — this is the same token embedded in the app
   (`FeedbackRelayService.RelayToken`). If it leaks, change it in **both**
   places and restart the relay.
4. Run the relay:

   ```
   set DISCORD_BOT_TOKEN=your-bot-token
   set RELAY_SECRET=your-shared-secret
   python discord_feedback_relay.py
   ```

   (Linux: `DISCORD_BOT_TOKEN=... RELAY_SECRET=... python3 discord_feedback_relay.py`)

   Optional env vars: `RELAY_PORT` (8090), `RELAY_HOST` (127.0.0.1),
   `POLL_INTERVAL_SECONDS` (20), `RELAY_ALLOW_REMOTE` (unset = loopback only).

5. **Access control is enforced in layers:**
   - The relay binds to **127.0.0.1 only** and rejects any request that does
     not originate from the local machine, so nothing on your LAN or the
     internet can reach it directly — even with a valid token.
   - `RELAY_ALLOW_REMOTE=1` is required to accept non-loopback connections
     (only needed if you run the relay on a VPS instead of your PC).
   - For real users, expose it publicly with HTTPS — Cloudflare Tunnel is the
     easiest free option and works with the loopback-only setup because
     `cloudflared` connects *outbound* to Cloudflare and forwards to
     localhost:
     `cloudflared tunnel --url http://localhost:8090` gives you a public HTTPS
     URL. **Never expose the relay over plain HTTP publicly** — the shared
     secret would travel in cleartext.

## Point the app at the relay

The relay URL is a runtime setting — no rebuild needed. Edit
`%APPDATA%\AcEvoFfbTuner\settings.json` on each machine (or the app's
`AppSettings.FeedbackRelayUrl` default before shipping):

```json
{
  "feedbackRelayUrl": "https://your-public-relay.example.com"
}
```

## Endpoints (all require `X-Relay-Token` header)

| Method | Path | Purpose |
|---|---|---|
| POST | `/register` | `{reportId, threadId, channelId, webhookUrl}` — called by the app after creating the thread |
| GET | `/replies/{reportId}` | Stored replies; optional `?after=<messageId>` (new messages) and `?edited_after=<ISO-time>` (messages edited since) |
| POST | `/reply` | `{reportId, content}` — posts a user reply into the thread via the stored webhook |

Requests are rate-limited (30 per 10 s per IP) and bodies capped at 64 KB.

## Conventions

- A developer reply starting with `[FIX]` (case-insensitive) is flagged
  `isFix: true` and triggers the "Fix available" toast + update check in the
  app.
- Replies are read via the bot token; webhook-authored messages (the app's own
  posts) are filtered out so the user only sees human replies.
- Editing a reply in Discord propagates to users who already received it
  (they get a re-toast with the corrected content).
- State is kept in `relay.db` (SQLite) next to this script. Delete the DB to
  reset — apps re-register automatically on their next poll.
- Reports expire from the app's polling after 14 days; deleting the Discord
  thread removes the registration here, and the app drops the report on the
  next 404.

## Troubleshooting

- **No replies appear in the app**: check `feedback_relay.log` in
  `%APPDATA%\AcEvoFfbTuner\` on the user machine, and confirm the relay logs
  show successful polls (`[poll REPORT-ID] ...`). The bot must be in the
  server and have Read Message History on the channel where threads are made.
- **401 errors**: `RELAY_SECRET` on the relay does not match
  `FeedbackRelayService.RelayToken` in the app.
- **Thread deleted in Discord**: the relay drops the registration (404), and
  the app removes the report on its next poll.
- **Relay unreachable from app**: verify `feedbackRelayUrl` in
  `settings.json` and that the port is open.
