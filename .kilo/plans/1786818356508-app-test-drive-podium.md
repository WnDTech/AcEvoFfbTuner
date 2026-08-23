# Test Drive Page: Remove Hero Join Discord Button, Add Footer Link — Plan

## Problem

`beta.html` hero has both "Apply Now" and "Join Discord" CTAs. Two Discord entry
points confuse the apply flow — "Join Discord" looks like it could be the
sign-in step, but the real sign-in is the "Continue with Discord" OAuth flow in
the apply section. The only Discord invite on the page is the hero button
(beta.html:67-70); the login box, FAQ, and rewards copy all already point into
the apply flow.

## Change (website only, HTML only)

1. **`website/beta.html` — hero** (lines 63-71): remove the "Join Discord"
   `<a href="https://discord.gg/wDrE2PZJyN">` button block. "Apply Now" becomes
   the single hero CTA (hero-actions container centers fine with one button).
2. **`website/beta.html` — footer**: add a subdued "Discord" footer link to the
   community server: `<li><a href="https://discord.gg/wDrE2PZJyN" target="_blank" rel="noopener">Discord</a></li>`
   alongside the other footer links (e.g. after Hub / near GitHub) — the footer
   links are already dim, so it reads as a community link, not a CTA.

No JS or CSS changes. `index.html`'s beta section keeps its "Join Discord"
button (main site, out of scope).

## Validation

- `grep beta.html` for `discord.gg` → exactly 1 match (footer).
- `grep beta.html` for "Join Discord" → 0 matches.
- Hero shows only "Apply Now"; apply flow unchanged (login box "Continue with
  Discord" untouched).

## Files touched

- `website/beta.html`
