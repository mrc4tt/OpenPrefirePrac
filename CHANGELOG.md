# Changelog

## 0.1.47
- First fshost release, based on lengran/OpenPrefirePrac 0.1.47.
- Built against CounterStrikeSharp 1.0.407 (mrc4tt/CounterStrikeSharp).
- Works when loaded mid-map with `css_plugins load`: sets up the current map and the players already on the server.
- Unloads cleanly with `css_plugins unload`: kicks the practice bots it spawned and restores the convars it changed.
- Settings moved to `addons/counterstrikesharp/configs/plugins/OpenPrefirePrac/default_cfg.json`, created with defaults on first start (an existing `default_cfg.json` in the plugin folder is copied over).
- Hit feedback sound on kills (`HitFeedbackSound`), plus performance fixes.
