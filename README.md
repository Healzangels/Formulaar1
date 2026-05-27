
# Formulaar1 (Healzangels fork)

> **Fork notice.** This is a maintained fork of [Jimmy062006/Formulaar1](https://github.com/Jimmy062006/Formulaar1) updated for compatibility with current Sonarr v4 and qBittorrent 5.x. The bundled NuGet SDKs in upstream v0.5.0 are abandoned and break on newer Sonarr / qBit schemas; this fork replaces the affected code paths with direct HTTP shims.
>
> A pre-built Docker image is published at [healzangels/formulaar1](https://hub.docker.com/r/healzangels/formulaar1) by [Healzangels/formulaar1-docker](https://github.com/Healzangels/formulaar1-docker). For most users, that's the easiest way to deploy.
>
> The content below is upstream Formulaar1's README, preserved here for context.

---

# Formulaar1

A small tool that automates Formula 1, Formula 2, and Formula 3 release pushes to Sonarr. It intercepts releases from AutoBrr, matches them to the correct TVDB episode, and forwards them to Sonarr with the correct metadata.

Hardlinking is supported on Linux, macOS, and Windows.

```mermaid
graph LR
A[AutoBrr] --> B{Formulaar1}
B --> D[Sonarr]
```

## Requirements

- [.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Sonarr v3
- qBittorrent (with Web UI enabled)

## Install Guide

### Pre-Built Binary

1. Download the latest release from [GitHub Releases](https://github.com/avassdal/Formulaar1/releases).

2. Extract into a folder.

3. Edit `appsettings.json` with your settings:

   ```json
   {
     "TorrentClient": "qBittorrent",
     "Hardlinkpath": "/full/path/to/hardlink/folder",
     "APICredentials": {
       "Sonarr": {
         "ApiKey": "",
         "BasePath": "http://127.0.0.1:8989"
       },
       "qBittorrentClient": {
         "Username": "",
         "Password": "",
         "BasePath": "http://127.0.0.1:10169"
       },
       "bugsnag": {
         "apiKey": "",
         "enabled": false
       }
     }
   }
   ```

   | Setting | Environment Variable | Description |
   | --- | --- | --- |
   | `TorrentClient` | `FORMULAAR1__TorrentClient` | Currently only `qBittorrent` is supported |
   | `EnableHardlinking` | `FORMULAAR1__EnableHardlinking` | `false` (default) — Sonarr handles file management. Set to `true` only if Sonarr cannot reach the qBittorrent download path directly |
   | `Hardlinkpath` | `FORMULAAR1__Hardlinkpath` | Only required when `EnableHardlinking` is `true`. Folder where Formulaar1 creates hardlinks before triggering a Sonarr import |
   | `Sonarr.ApiKey` | `FORMULAAR1__Sonarr__ApiKey` | Found in Sonarr → Settings → General |
   | `Sonarr.BasePath` | `FORMULAAR1__Sonarr__BasePath` | Full URL to your Sonarr instance |
   | `qBittorrentClient.BasePath` | `FORMULAAR1__qBittorrentClient__BasePath` | Full URL to your qBittorrent Web UI |
   | `bugsnag.apiKey` | `FORMULAAR1__bugsnag__apiKey` | Optional — your own Bugsnag project API key for error reporting |
   | `bugsnag.enabled` | `FORMULAAR1__bugsnag__enabled` | Set to `true` if you supply a Bugsnag API key |

4. Start Formulaar1:

   ```sh
   ./Formulaar1
   ```

   You should see output like:

   ```log
   info: Microsoft.Hosting.Lifetime[14]
        Now listening on: http://localhost:5000
   ```

5. In AutoBrr, create a new client with:
   - **Type:** Sonarr
   - **Host:** `http://127.0.0.1:5000` (or whichever port Formulaar1 is listening on)
   - **API Key:** your normal Sonarr API key

   Clicking **Test** should return a green OK.

6. Set up an AutoBrr filter pointing to this new client. That's it!

## Supported Series

| Series | TVDB ID |
| --- | --- |
| Formula 1 | 387219 |
| Formula 2 | 392717 |
| Formula 3 | 396724 |

## Circuit/Country Detection

At startup, Formulaar1 fetches the current F1 season calendar from [f1api.dev](https://f1api.dev) to automatically populate circuit and city names for the current year. This means new F1 venues are supported without any code changes.

If the API is unavailable, Formulaar1 falls back to a built-in static dictionary which also covers F2/F3 circuits and common alternate names used in release titles (e.g. `COTA`, `Imola`, `UAE`, `British`).

## Issues

Please raise any issues if you have any problems.
