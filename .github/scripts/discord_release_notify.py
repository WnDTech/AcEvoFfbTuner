#!/usr/bin/env python3
"""Post a GitHub release notification to a Discord channel.

Reuses the project's existing Discord bot token (the same one used by the
feedback relay). Posts an embed with the release name, link, and version notes
to the channel given in DISCORD_CHANNEL_ID.

Input:
  - GITHUB_EVENT_PATH (set by GitHub Actions): the release webhook payload
  - MANUAL_RELEASE_FILE (optional): JSON with {name, html_url, body} when
    triggered via workflow_dispatch (no webhook event)
"""
import json
import os
import sys
import urllib.error
import urllib.request

DISCORD_API = "https://discord.com/api/v10"
DISCORD_USER_AGENT = "DiscordBot (https://github.com/WnDTech/AcEvoFfbTuner, 1.0.0)"
EMBED_COLOR = 0xFF8800
MAX_DESCRIPTION_LEN = 4096


def main():
    token = os.environ.get("DISCORD_BOT_TOKEN", "")
    channel_id = os.environ.get("DISCORD_CHANNEL_ID", "")
    event_path = os.environ.get("GITHUB_EVENT_PATH", "")
    manual_file = os.environ.get("MANUAL_RELEASE_FILE", "")

    if not token or not channel_id:
        print("Missing DISCORD_BOT_TOKEN or DISCORD_CHANNEL_ID", file=sys.stderr)
        return 1

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

    name = release.get("name") or release.get("tag_name") or "New release"
    url = release.get("html_url") or ""
    body = (release.get("body") or "").strip()
    if not body:
        print("Release body is empty; skipping notification", file=sys.stderr)
        return 1

    description = body
    if len(description) > MAX_DESCRIPTION_LEN:
        description = description[: MAX_DESCRIPTION_LEN - 3] + "..."

    payload = {
        "embeds": [
            {
                "title": f"New version: {name}",
                "url": url,
                "description": description,
                "color": EMBED_COLOR,
            }
        ]
    }

    req = urllib.request.Request(
        f"{DISCORD_API}/channels/{channel_id}/messages",
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Authorization": f"Bot {token}",
            "Content-Type": "application/json",
            "User-Agent": DISCORD_USER_AGENT,
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            msg = json.loads(resp.read().decode("utf-8"))
        print(f"Posted release notification to Discord (message id {msg.get('id')})")
        return 0
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        print(f"Discord rejected the post: HTTP {e.code} {detail}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
