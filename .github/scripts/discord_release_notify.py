#!/usr/bin/env python3
"""Post a GitHub release notification to a Discord channel.

Posts an embed with the release name, link, and version notes. Two auth
routes are supported:

  1. Discord bot: DISCORD_BOT_TOKEN + DISCORD_CHANNEL_ID
  2. Webhook:     DISCORD_WEBHOOK_URL (no bot/token/channel needed)

The webhook route reuses the same webhook the app's feedback relay uses.

Input:
  - GITHUB_EVENT_PATH (set by GitHub Actions): the release webhook payload
  - MANUAL_RELEASE_FILE (optional): JSON with {name, html_url, body} when
    triggered via workflow_dispatch (no webhook event)

Routing:
  - Stable releases → DISCORD_WEBHOOK_URL, else DISCORD_BOT_TOKEN +
    DISCORD_CHANNEL_ID (public announcements channel).
  - Prereleases (beta test builds) → DISCORD_BOT_TOKEN +
    DISCORD_BETA_CHANNEL_ID (tester-gated channel) ONLY. They are never
    posted publicly and never via the shared webhook (a webhook is pinned
    to one — public — channel). If the beta channel is not configured the
    script skips the post instead of leaking it publicly.
"""
import json
import os
import sys
import urllib.error
import urllib.request

DISCORD_API = "https://discord.com/api/v10"
DISCORD_USER_AGENT = "DiscordBot (https://github.com/WnDTech/AcEvoFfbTuner, 1.0.0)"
EMBED_COLOR = 0xFF8800
BETA_EMBED_COLOR = 0x9E6BFF
MAX_DESCRIPTION_LEN = 4096


DISCORD_FORUM_CHANNEL_TYPE = 15


def _api_call(method, endpoint, headers, payload=None):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(
        endpoint,
        data=data,
        headers=headers,
        method=method,
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main():
    token = os.environ.get("DISCORD_BOT_TOKEN", "")
    channel_id = os.environ.get("DISCORD_CHANNEL_ID", "")
    beta_channel_id = os.environ.get("DISCORD_BETA_CHANNEL_ID", "")
    webhook_url = os.environ.get("DISCORD_WEBHOOK_URL", "")
    event_path = os.environ.get("GITHUB_EVENT_PATH", "")
    manual_file = os.environ.get("MANUAL_RELEASE_FILE", "")

    release = None
    if event_path and os.path.exists(event_path):
        with open(event_path, encoding="utf-8") as f:
            event = json.load(f)
        release = event.get("release")
    if not release and manual_file and os.path.exists(manual_file):
        with open(manual_file, encoding="utf-8") as f:
            release = json.load(f)
    if not release:
        print("No release information available", file=sys.stderr)
        return 1

    is_prerelease = bool(release.get("prerelease", False))

    if is_prerelease:
        # Tester-only beta: bot + gated channel only. Never the public
        # channel, never the shared webhook. Skip (don't leak) when the
        # beta channel isn't configured.
        if not (token and beta_channel_id):
            print("Prerelease detected but DISCORD_BETA_CHANNEL_ID (+ bot token) is not configured; skipping public post")
            return 0
        endpoint = f"{DISCORD_API}/channels/{beta_channel_id}/messages"
        headers = {
            "Authorization": f"Bot {token}",
            "Content-Type": "application/json",
            "User-Agent": DISCORD_USER_AGENT,
        }
        channel_id = beta_channel_id
    elif webhook_url:
        endpoint = webhook_url
        headers = {
            "Content-Type": "application/json",
            "User-Agent": DISCORD_USER_AGENT,
        }
    elif token and channel_id:
        endpoint = f"{DISCORD_API}/channels/{channel_id}/messages"
        headers = {
            "Authorization": f"Bot {token}",
            "Content-Type": "application/json",
            "User-Agent": DISCORD_USER_AGENT,
        }
    else:
        print(
            "Missing DISCORD_WEBHOOK_URL, or DISCORD_BOT_TOKEN + DISCORD_CHANNEL_ID",
            file=sys.stderr,
        )
        return 1

    name = release.get("name") or release.get("tag_name") or "New release"
    url = release.get("html_url") or ""
    body = (release.get("body") or "").strip()
    if not body:
        print("Release body is empty; skipping notification", file=sys.stderr)
        return 1

    if is_prerelease:
        description = "🧪 *Tester-only prerelease — please report issues in your task thread.*\n\n" + body
    else:
        description = body
    if len(description) > MAX_DESCRIPTION_LEN:
        description = description[: MAX_DESCRIPTION_LEN - 3] + "..."

    embed = {
        "title": f"🧪 Beta test build: {name}" if is_prerelease else f"New version: {name}",
        "url": url,
        "description": description,
        "color": BETA_EMBED_COLOR if is_prerelease else EMBED_COLOR,
    }

    # Forum channels (type 15) reject plain messages; the post must be a thread.
    if token and channel_id:
        try:
            channel = _api_call(
                "GET", f"{DISCORD_API}/channels/{channel_id}", headers
            )
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")
            print(
                f"Discord rejected the channel lookup: HTTP {e.code} {detail}",
                file=sys.stderr,
            )
            return 1
        if channel.get("type") == DISCORD_FORUM_CHANNEL_TYPE:
            thread_name = name[:100]
            endpoint = f"{DISCORD_API}/channels/{channel_id}/threads"
            payload = {
                "name": thread_name,
                "auto_archive_duration": 1440,
                "message": {"embeds": [embed]},
            }
        else:
            payload = {"embeds": [embed]}
    else:
        payload = {"embeds": [embed]}

    try:
        msg = _api_call("POST", endpoint, headers, payload)
        print(f"Posted release notification to Discord (message id {msg.get('id')})")
        return 0
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        print(f"Discord rejected the post: HTTP {e.code} {detail}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
