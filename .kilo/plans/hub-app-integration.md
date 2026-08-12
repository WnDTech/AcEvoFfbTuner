# Plan: App Integration — Profile Hub (Share & Browse)

**Status:** Ready to implement in a fresh session
**Created:** 2026-08-12
**Server API:** DONE and live at https://ffbtuner.wndtech.tips/api/hub.php

---

## 1. What's Already Done (do not rebuild)

### Server-side (live)
- API router: website/api/hub.php (and index.php backup) — deployed at https://ffbtuner.wndtech.tips/api/
- Endpoints:
  - GET ?action=list&game=&wheel=&car=&q=&sort=&page=&per= — public list (live profiles only)
  - GET ?action=detail&id=N — single profile + full JSON
  - GET ?action=download&id=N — streams profile JSON + increments download count
  - POST ?action=upload — requires X-App-Key header; body: {title, description, author, authorId, game, car, track, wheel, wheelType, torqueNm, profile}; new uploads go to pending status
  - POST ?action=rate — body {"value":1-5}
  - GET ?action=admin_list&key=ADMIN_KEY / POST ?action=approve&key=...&status=live|rejected / POST ?action=delete&key=...
- MariaDB: FFBTunerProfiles db, profiles table auto-created by db.php
- Website hub page: website/hub.html + assets/js/hub.js + assets/css/hub.css — live and working

### App-side settings (already added — verify)
src/AcEvoFfbTuner/Services/AppSettings.cs lines ~37-41:
- HubApiBaseUrl = https://ffbtuner.wndtech.tips/api/hub.php
- HubApiKey = d0fbf9a40df1393eac2c2e0c2ed4563e319f4ba4b3b6a22c3ac8d358d6e93e4b (matches server config)
- HubAuthorName = "" (user sets once)
- HubAuthorId = "" (app generates once, e.g. GUID, stored in settings — used for edit/delete auth)

### Keys
- App upload key: d0fbf9a40df1393eac2c2e0c2ed4563e319f4ba4b3b6a22c3ac8d358d6e93e4b
- Admin key: 4730d97474b692e373450c75c6718fad9477492a19468746705ce81e4df0fbaf (server only — used for ?key= approve/reject)

---

## 2. Feature 1: Share to Hub (per-profile upload)

### UX
- Profiles page: add a "Share to Hub" button per profile (next to Export/Import)
- On click — dialog with: title (prefill from profile name), description textbox, author name (prefill from settings, editable), preview of game/car/track/wheel
- Submit — POST upload — success message "Submitted for review — will appear on the Hub after approval"
- If HubAuthorId empty — generate GUID once, persist to settings

### Payload mapping (must match server)
{
  "title": "BMW M4 GT3 — Moza R5",
  "description": "Tuned for Nurburgring...",
  "author": "Paul",
  "authorId": "<guid>",
  "game": "AcEvo",            // enum name from SupportedGame
  "car": "BMW M4 GT3",        // from profile.CarMatch
  "track": "",                 // from profile.TrackMatch
  "wheel": "Moza R5",          // from current wheel device name
  "wheelType": "DirectDrive",  // from wheel detector
  "torqueNm": 5.5,             // from profile.WheelMaxTorqueNm
  "profile": { ...full FfbProfile serialized... }
}

### Code touch points
- src/AcEvoFfbTuner/ViewModels/MainViewModel.Profile.cs — add ShareProfileToHub(FfbProfile) async method (near existing ExportProfile ~line 288)
- src/AcEvoFfbTuner/Views/Pages/ProfilesPage.xaml(.cs) — add button + dialog wiring
- New service: src/AcEvoFfbTuner/Services/HubClient.cs — wraps HttpClient: UploadProfileAsync(), GetProfilesAsync(), DownloadProfileAsync(), RateProfileAsync()
- Serialize profile with JsonSerializerOptions matching ProfileManager.JsonOptions (camelCase) — profile object must be the SAME shape FfbProfile serializes to in ProfileManager.ExportProfile (line 151)

---

## 3. Feature 2: Browse Hub (in-app)

### UX
- New sidebar page "Hub" (add Hub to NavPage enum in src/AcEvoFfbTuner/ViewModels/NavPage.cs)
- Reuse pattern from LiveTrackMapPage / FFBCoachPage for page registration (find where NavPage enum values map to pages in MainViewModel/MainWindow — search NavPage usage)
- Page shows: search box, game filter, sort dropdown, profile cards grid (reuse hub.html card design in WPF)
- Download button — fetch JSON — save temp — ProfileManager.ImportProfile(path) (line 158 — handles migration) — toast "Imported: {name}"

### Behavior notes
- Show pending-approved profiles only (server already filters status=live)
- Handle network errors gracefully (offline — show error state, don't crash)
- Imported profile auto-migrates via NeedsMigration / Migrate() — no special handling needed
- Optional: badge/indicator on imported profiles (compare IsBuiltIn)

---

## 4. Implementation Order

1. HubClient.cs service (HttpClient wrapper + models: HubProfileDto, HubUploadRequest)
2. Share flow: dialog + button in ProfilesPage + MainViewModel.Profile.cs method
3. Browse flow: Hub NavPage + new HubPage.xaml(.cs) + ViewModel wiring
4. Test with live hub (upload test profile, approve via admin, verify appears)
5. Full clean build: dotnet clean AcEvoFfbTuner.slnx -c Release 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release

---

## 5. Verification Checklist

- [ ] Share button opens dialog with prefilled data
- [ ] Upload returns success and profile appears in ?action=admin_list&status=pending (via browser)
- [ ] Approve via ?action=approve&id=N&status=live&key=ADMIN_KEY
- [ ] Profile appears on https://ffbtuner.wndtech.tips/hub.html
- [ ] Browse Hub page in app lists it
- [ ] Download from app imports and saves to Profiles folder
- [ ] Download counter increments on server
- [ ] No regressions to existing profiles/export/import (run unit tests: dotnet test)

---

## 6. Gotchas / Notes

- CORS: server allows https://ffbtuner.wndtech.tips origin — app uses HttpClient with no Origin header, fine. Do NOT change CORS to * again.
- Date format: server stores UTC `Y-m-d H:i:s` (plain space format — MariaDB strict mode rejects the ISO `T...Z` format that the original hub.php used; fixed 2026-08-12 in both hub.php and index.php). C# treats dates as strings / DateTimeOffset.Parse.
- Server fix NOT yet deployed: the live server still runs the old hub.php — uploads fail with `SQLSTATE[22007] Invalid datetime format` until `website/api/hub.php` + `index.php` are uploaded to the host.
- Game enum names must be exactly: AcEvo, Raceroom, AssettoCorsa, LeMansUltimate, AssettoCorsaCompetizione (matches SupportedGame in MainViewModel.Game.cs line 10).
- Rate limit: 10 uploads/hour/IP server-side — handle 429 responses with a friendly message.
- Do NOT commit/push unless explicitly asked (per repo rules).
- Profiles live in C:\\Users\\paul_\\AppData\\Roaming\\AcEvoFfbTuner\\Profiles
