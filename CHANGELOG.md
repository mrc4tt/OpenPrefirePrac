# Changelog

## 0.1.49
- New de_cache practices: Mid to Heaven (51 bots), A main (73) and B main (70), converted with permission from the workshop map "Cache prefire by someBOT". The bot positions come from pro match footage; credit to someBOT.
- In-game practice recorder for admins (`!prefire rec ...`): start a route, add bots where you stand or where you aim, add guiding-line points, undo, and save to `maps/<map>/<name>.txt`. The route is available right away when nobody is practicing. See the README.
- Profiles accept lines pasted straight from `getpos` (`setpos ...;setang ... [True|False]` for bots and the player start, `setpos X Y Z` for guiding-line points). Heights are converted like `calculate_height.py` does, so the script is optional.
- A route without a name in `lang/` (a newly recorded route, or a deploy without the lang folder) shows its file name in readable form ("B main") instead of the raw key `map.de_cache.b_main`.
- After `!prefireon` everyone on the server is told to type `!prefire` to choose a route.
- No freeze time while the plugin is loaded: it is cleared again on every map (re)load, not only when a route starts.
- A plain `dotnet build`/`publish -c Release` now copies `maps/`, `lang/` and `resources/` next to the dll.
- Numbers in profiles are always read with a dot as the decimal separator, whatever the server's language setting.

## 0.1.48
- Each player's guiding line is only sent to that player. Before, every client drew every practicing player's line beams.

## 0.1.47
- First fshost release, based on lengran/OpenPrefirePrac 0.1.47.
- Built against CounterStrikeSharp 1.0.407 (mrc4tt/CounterStrikeSharp).
- Works when loaded mid-map with `css_plugins load`: sets up the current map and the players already on the server.
- Unloads cleanly with `css_plugins unload`: kicks the practice bots it spawned and restores the convars it changed.
- Settings moved to `addons/counterstrikesharp/configs/plugins/OpenPrefirePrac/default_cfg.json`, created with defaults on first start (an existing `default_cfg.json` in the plugin folder is copied over).
- On servers started with `-nobots`, practices no longer start with invisible bots: the player gets a chat message explaining that bots are disabled, and the server log gets a warning.
- Hit feedback sound on kills (`HitFeedbackSound`), plus performance fixes.
