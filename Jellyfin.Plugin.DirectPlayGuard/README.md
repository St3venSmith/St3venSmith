# Direct Play Guard for Jellyfin 12

Direct Play Guard disables Jellyfin video/audio transcoding and gives Jellyfin Web users a clearer message when their client cannot Direct Play the media.

## Install from Jellyfin

After the automated build finishes, add this repository URL in **Dashboard → Plugins → Repositories**:

`https://raw.githubusercontent.com/St3venSmith/St3venSmith/main/jellyfin-directplayguard-manifest.json`

Then open **Catalog**, install **Direct Play Guard**, and restart Jellyfin.

## Behavior

- Video transcoding: disabled by default
- Audio transcoding: disabled by default
- Remux/direct stream: allowed by default
- Strict Direct Play mode can also disable remuxing
- Existing and newly-created users are enforced automatically
- Original transcoding permissions are backed up and restored when the plugin is disabled
- Jellyfin Web gets a custom unsupported-client popup with recommended apps
- Native clients are still blocked server-side, but use their own built-in error UI

## Source

The complete source snapshot used by the automated build is stored in `source/Jellyfin.Plugin.DirectPlayGuard-source.zip`.

The GitHub Actions workflow builds against Jellyfin 12 / .NET 10 and publishes the installable ZIP into this repository automatically.
