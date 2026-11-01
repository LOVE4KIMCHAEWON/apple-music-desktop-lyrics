@echo off
setlocal
set "lyricsDir=%~dp0"
if not exist "%lyricsDir%AppleMusicLyrics.exe" set "lyricsDir=%~dp0artifacts\apple-music-desktop-lyrics-0.2.3-win-x64\"
if not exist "%lyricsDir%AppleMusicLyrics.exe" (
    echo AppleMusicLyrics.exe was not found. Extract the complete portable ZIP first.
    echo Expected folder: "%lyricsDir%"
    pause
    exit /b 1
)
start "" "%lyricsDir%AppleMusicLyrics.exe" --data-dir "%lyricsDir%data" --settings
