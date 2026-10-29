# Changelog

## 0.1.0

First version of the ClearFrame Emby plugin.

- TV pairing with on-screen codes approved from the plugin page; per-TV tokens stored only as hashes.
- One Emby user per TV, with Emby app session matching by device ID or the TV's DUID.
- Home rows: Continue Watching, Next Up, On Now, Your Teams (per-TV sports keywords), and Seerr Trending, Popular Movies and Popular Shows.
- Seerr requests from the TV, made as the linked Seerr user. Links are synced from Seerr's stored Emby ids, with username/email suggestions and manual links; one Emby user can link to several Seerr users, with the last requester remembered per TV and an optional fallback account.
- Play, resume (from the saved position) and details commands sent to the Emby app on the TV, queued until the app connects when it isn't running yet.
- Settings page under Dashboard → Plugins → ClearFrame.
- Requires Emby Server 4.9 or later; tested on 4.10.2.0.
