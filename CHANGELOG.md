# Changelog

## 0.1.49
- In-game practice recorder for admins (`!prefire rec ...`): start a route, add bots where you stand or where you aim, add guiding-line points, undo, and save to `maps/<map>/<name>.txt`. The route is available right away when nobody is practicing. See the README.
- Profiles accept lines pasted straight from `getpos` (`setpos ...;setang ... [True|False]` for bots and the player start, `setpos X Y Z` for guiding-line points). Heights are converted like `calculate_height.py` does, so the script is optional.
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
