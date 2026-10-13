=====================================================================
  V's Last.fm Scrobbler                                  made by Vista
=====================================================================

A small app that sits in your Windows tray and scrobbles what you play
in Apple Music (or iTunes) to your Last.fm profile.

It also shows:
  - what you're playing on your Last.fm profile ("now playing")
  - what you're playing on Discord (optional)
  - notifications for scrobble milestones, and when a friend is
    listening to the same artist as you
  - what your Last.fm friends are listening to
  - your stats: today, this week, this month, this year, your streak
  - a monthly and a yearly "wrap" of your listening, like Spotify Wrapped


---------------------------------------------------------------------
 WHAT YOU NEED
---------------------------------------------------------------------
  - Windows 10 or 11
  - Apple Music (the app from the Microsoft Store) or iTunes
  - A Last.fm account

  Spotify isn't needed here: Spotify scrobbles to Last.fm on its own
  (Spotify > Settings > connect Last.fm).


---------------------------------------------------------------------
 INSTALLING (about 5 minutes)
---------------------------------------------------------------------

 1. PUT THE FOLDER SOMEWHERE
    Unzip this folder to a place you'll keep it, for example
        C:\Last.fm Scrobbler
    or a folder on another drive. NOT inside "Program Files" (the app
    saves its settings next to itself, and Windows blocks that there).

 2. DOUBLE-CLICK  Install.bat
    The app is built on your PC from the code in the Source folder,
    using a tool that's already part of Windows. A black window shows
    up for a few seconds, then the app starts. Look for the green
    Last.fm icon in the tray (bottom right, maybe behind the little ^
    arrow next to the clock).

    If Windows shows a security warning ("Windows protected your PC"
    or "Do you want to run this file?"): click "More info" > "Run
    anyway", or "Run". It only asks because the file came in a zip.

 3. GET YOUR OWN LAST.FM API KEY (free, one time)
    Every user needs their own key. The Settings window opens by itself
    the first time.
      a. Go to  https://www.last.fm/api/account/create
         (log in to Last.fm if it asks)
      b. Fill in:
           Contact email ........ your email
           Application name ..... anything, e.g. "My Scrobbler"
           Description .......... anything, e.g. "Scrobbles Apple Music"
           Callback URL ......... leave empty
           Homepage ............. leave empty
         and click Submit.
      c. You now see an "API key" and a "Shared secret". Copy both into
         the Settings window (api key / secret).
      You can always find them again at  https://www.last.fm/api/accounts

 4. CONNECT
      a. In Settings, click  [ 1. connect to last.fm ]
         Your browser opens. Click "Yes, allow access".
      b. Back in Settings, click  [ 2. i've allowed access ]
         It now says "connected as <your name>".

 5. FINISH
    Tick "start with windows" if you want it to run all the time
    (recommended), then click  [ save & close ].

  Done. Play a song in Apple Music: after half the song (or 4 minutes)
  it shows up on your Last.fm profile.


---------------------------------------------------------------------
 USING IT
---------------------------------------------------------------------
  Everything is in the tray icon:
    - Rest the mouse on it: what's playing, with the album cover,
      a level meter and how far into the song you are.
    - Right-click it: the menu (pause, friends, wraps, history,
      settings, ...).
    - Double-click it: Settings.

  The dot on the icon:
    green ............. playing
    no dot ............ nothing playing
    grey .............. scrobbling paused
    orange ............ offline, scrobbles are saved and sent later
    red ............... not connected to Last.fm (open Settings)

  Nothing gets lost when you're offline: scrobbles wait and are sent
  when the internet is back (Last.fm accepts them up to 14 days late).

  Menu items:
    wraps .............. make a wrap of this month / this year so far,
                         or open the shelf with all of them. They're
                         also made automatically on the 1st of each
                         month and on 1 January.
    app ................ opens the app window. It has a tab for each:
        friends ........ what your Last.fm friends are listening to
        stats .......... today / week / month / year, the last 30 days,
                         this week's top artists and tracks
        history ........ your last 50 scrobbles and whether they arrived
        log ............ what the app did (for when something's wrong)
        settings, about
      Ctrl+Tab switches tabs, Esc closes the window.


---------------------------------------------------------------------
 DISCORD (optional)
---------------------------------------------------------------------
  Shows "Listening to Apple Music" with the song and cover on your
  Discord profile.
    1. Go to  https://discord.com/developers/applications
       and click "New Application". Name it  Apple Music
       (that's the name Discord shows: "Listening to Apple Music").
    2. Copy its "Application ID".
    3. Paste it into Settings > discord > app id, and tick
       "show what i'm playing on discord".
  The Discord desktop app must be running, and in Discord:
  Settings > Activity Privacy > "Share your detected activities" on.


---------------------------------------------------------------------
 WHAT'S IN THE FOLDER
---------------------------------------------------------------------
  V's Last.fm Scrobbler.exe   the app (appears after Install.bat)
  Install.bat                 builds the app; run it again after updates
  Source\                     the app's code
  Data\                       your settings, saved scrobbles, log
                              (your key and login are encrypted, only
                              your Windows account can read them)
  Wraps\                      your wraps; open index.html to see them all
  README.txt                  this file
  LICENSE.txt                 MIT license: free to use, change and share,
                              as long as the copyright notice stays


---------------------------------------------------------------------
 UPDATING
---------------------------------------------------------------------
  The app checks for a new version each time it starts (Settings >
  "check for updates" checks right away). When there is one, you
  get a notification, and the tray menu shows "update to v..." at the
  top. Click it: the app downloads the new version, closes, builds it
  and starts again (about a minute). Settings, saved scrobbles and
  wraps stay. You can switch the check at startup off in Settings.

  By hand: download the newest zip from
      https://github.com/prodbyvista/vs-lastfm-scrobbler/releases
  unzip it over the old folder (say yes to replacing files) and
  double-click Install.bat again.


---------------------------------------------------------------------
 REMOVING IT
---------------------------------------------------------------------
  1. Settings: untick "start with windows", save & close.
  2. Right-click the tray icon > exit.
  3. Delete the folder.


---------------------------------------------------------------------
 IF SOMETHING DOESN'T WORK
---------------------------------------------------------------------
  "Install.bat says Build FAILED"
     Send Vista the file  Source\build_log.txt

  "Could not find the .NET Framework 4 C# compiler"
     Open "Turn Windows features on or off" (search for it in the
     Start menu), tick ".NET Framework 4.8 Advanced Services", OK,
     then run Install.bat again.

  "It says Apple Music support NOT included"
     It still works with iTunes, but not with the Apple Music app.
     Send Vista the file  Source\build_log.txt

  "Nothing gets scrobbled"
     - Is the dot red? Open Settings and connect again (step 4).
     - Is it grey? Right-click > untick "pause scrobbling".
     - Songs shorter than 30 seconds are never scrobbled (Last.fm rule).
     - Right-click > log... shows what's going on. "write diagnostics"
       adds a full report you can send to Vista.

  "No tray icon"
     It may be hidden behind the ^ arrow next to the clock. Drag it
     onto the taskbar to keep it visible.

=====================================================================
