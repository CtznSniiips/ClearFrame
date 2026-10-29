# Changelog

## 0.2.0

- Each TV now keeps its own settings, changed from ClearFrame on the TV: which home rows show, their order, items per row, sports look-ahead, play delay and how long to wait for the Emby app. New endpoints: `GET`/`POST /ClearFrame/Settings` and `POST /ClearFrame/Settings/Reset`.
- Team picker: `GET /ClearFrame/Sports/Teams` lists teams found in the guide's sports listings, so teams can be chosen with the remote instead of typed.
- Playback uses each TV's own delay and wait time.
- Seerr titles that Seerr reports as available or partly available are matched to the Emby library by TMDB id. Movies play or resume and shows play their next episode (`PlayItemId`, `PlayAction`). Available titles default to playing; partly available ones still default to requesting.
- Each title appears only once across a TV's rows, in the first row that has it (episodes count as their show).
- TVs can send their token as `Authorization: ClearFrame <token>`. Emby's CORS policy already allows that header, so browser-based clients pass the preflight. `X-ClearFrame-Token` still works.
- The plugin page's home and playback settings are now the defaults for TVs. Each TV card shows whether the TV has changed its settings and can reset it. Every setting has a short explanation, and the Seerr section has its own Save button.

## 0.1.0

First version of the ClearFrame Emby plugin.

- TV pairing with on-screen codes approved from the plugin page; per-TV tokens stored only as hashes.
- One Emby user per TV, with Emby app session matching by device ID or the TV's DUID.
- Home rows: Continue Watching, Next Up, On Now, Your Teams (per-TV sports keywords), and Seerr Trending, Popular Movies and Popular Shows.
- Seerr requests from the TV, made as the linked Seerr user. Links are synced from Seerr's stored Emby ids, with username/email suggestions and manual links; one Emby user can link to several Seerr users, with the last requester remembered per TV and an optional fallback account.
- Play, resume (from the saved position) and details commands sent to the Emby app on the TV, queued until the app connects when it isn't running yet.
- Settings page under Dashboard → Plugins → ClearFrame.
- Requires Emby Server 4.9 or later; tested on 4.10.2.0.
