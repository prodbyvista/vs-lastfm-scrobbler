# V's Last.fm Scrobbler

A small Windows tray app that scrobbles what you play in **Apple Music** (the Microsoft Store app) or **iTunes** to your [Last.fm](https://www.last.fm) profile. Made by Vista.

- Scrobbles and "now playing", with an offline queue so nothing gets lost
- Discord Rich Presence ("Listening to Apple Music", with the cover)
- Notifications for scrobble milestones and when a friend listens to the same artist
- A friends tab: what the people you follow are listening to right now
- A stats tab: today, this week, this month, this year, your streak, this week's top artists and tracks
- Monthly and yearly "wraps" of your listening
- Black & green terminal look, updates itself from this page

## Install

1. Download the newest `VsLastfmScrobbler-x.y.zip` from **[Releases](https://github.com/prodbyvista/vs-lastfm-scrobbler/releases/latest)**.
2. Unzip it somewhere you'll keep it, e.g. `C:\Last.fm Scrobbler` (not inside Program Files).
3. Double-click **Install.bat**. The app is built on your PC with the C# compiler that comes with Windows, then it starts in the tray.
4. Follow **README.txt** to get your free Last.fm API key and connect your account (about 5 minutes).

Needs Windows 10 or 11 and a Last.fm account.

## Updates

The app checks this repository's releases when it starts (and when you click **check for updates** in Settings). When a new version is out you get a notification, and the tray menu shows **update to vX.Y**. Click it and the app downloads, builds and restarts by itself. Your settings, queued scrobbles and wraps are kept.

## Building by hand

All the code is in `Source\`. `Source\build.bat` builds `V's Last.fm Scrobbler.exe` into the folder above it, using `csc.exe` from .NET Framework 4.x. The code is written in C# 5 so that compiler can build it. If the build fails, the details are in `Source\build_log.txt`.
