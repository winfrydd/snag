# Snag

A simple Windows app for downloading video and audio. Paste a link, pick Video or Audio only, pick the quality, then press Download.
Under the hood it runs [yt-dlp](https://github.com/yt-dlp/yt-dlp) (YouTube and well over a thousand other sites) and [ffmpeg](https://ffmpeg.org).

## Install

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/winfrydd/snag/main/get.ps1 | iex
```

The installer:

- checks for the .NET 10 Desktop Runtime, yt-dlp and ffmpeg, and offers to install any that are missing through winget;
- installs Snag to `%LOCALAPPDATA%\Programs\Snag` (no admin rights needed);
- adds Snag to the Start menu.

Run the same command again to update; your settings are kept.

If you'd rather do it by hand, download the zip from [Releases](../../releases), unzip it anywhere and run `Snag.exe`.

## Features

- **Paste and go.** Paste a link (or several at once). You get a preview with the title, channel, length and best available quality.
- **Best quality by default.** The quality list shows only what each video really has, and it starts at Best.
  Snag always takes the best streams (AV1/VP9 video, Opus audio) and copies them into MP4 or MKV without re-encoding.
- **Audio only** as MP3, M4A, Opus, FLAC, WAV, or the original stream. When the site already has the format you picked, Snag uses that stream directly instead of re-encoding it.
- **Playlists.** Playlist links download into their own folder with numbered files. For a video inside a playlist, you choose whether to get just that video or the whole list.
- **Download queue** with progress, speed and time left. Cancel, retry, play, and show in folder. You set how many downloads run at the same time.
- **Extras:** cover picture, title/artist info, chapters, and subtitles embedded in the file. Optional Firefox sign-in for age-restricted, members-only, or sign-in sites such as Vimeo.
- **Quality of life:**
  - grabs a copied link when you switch to Snag;
  - Ctrl+V anywhere in the window pastes links;
  - drag and drop links onto the window;
  - Enter downloads;
  - progress shows on the taskbar button, which flashes when everything finished in the background;
  - settings and window size are remembered.
- **One-click "Update yt-dlp"** for when a site changes and downloads stop working.

## Privacy

No accounts, telemetry, ads or update checks. Snag only contacts:

- the sites you download from;
- GitHub, but only when you press "Update yt-dlp".

Settings stay in `data\settings.json` next to `Snag.exe`. "Use Firefox sign-in" lets yt-dlp read your Firefox cookies on your PC, and those cookies are only sent to the site you download from.

Copy-protected services (Netflix, Spotify, Apple Music and similar) don't work, and Snag won't try to get around that.

## Where Snag looks for yt-dlp and ffmpeg

In this order:

1. a path you pick in the app;
2. a `tools\` folder next to `Snag.exe`;
3. your PATH.

## Build from source

You need the .NET 10 SDK. Then run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 [-Target <folder>]   # build + install + Start menu shortcut
powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version 1.0.0  # release zip in dist\
```

Pushing a tag like `v1.0.0` makes GitHub Actions build the zip and publish the release.

## License

MIT. yt-dlp (Unlicense) and ffmpeg (LGPL/GPL) are separate programs and aren't bundled.
