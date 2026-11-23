# Guide - The Scrolling Guide Simulator

![The Guide Channel](/Guide/assets/images/guide-channel.png)

## What is it?
This is a simulator for classic television scrolling guides. Right now, there's an implementation that closely mimics the old "Esquire" guide style you may have seen on the Prevue Guide growing up.

![Guide screenshot](/.readme/guide.png)

## How do I run it?
The guide uses .NET 9 and SDL 3. Install .NET 9, SDL 3, SDL 3 TTF (`SDL3_ttf`), and SDL 3 Image (`SDL3_image`).

**Note**: On a Mac with Homebrew, use these commands:

`brew install --cask dotnet-sdk; brew install sdl3 sdl3_image sdl3_ttf`

On macOS, the guide looks for the SDL 3 libraries in `/opt/homebrew/lib` and `/usr/local/lib`. You do not have to copy the libraries.

The guide gets its listings from a [Channels DVR](https://getchannels.com/dvr-server/) server. To start the guide, run this command from the main directory:

`dotnet run --project Guide/Guide.csproj -- --channels-dvr http://<server>:8089`

You can also set the server address in the `GUIDE_CHANNELS_DVR` environment variable.

### Options
- `--channels-dvr <address>`: The address of the Channels DVR server.
- `--screenshot <path>`: Save a PNG of the window to this path, and then quit.
- `--screenshot-delay <seconds>`: The time to wait before the screenshot. The default is 3 seconds.
- `--sample`: Show fixed sample listings, with no server. The first channels are the channels of the ESQ screenshot in the `esquire` repository.
- `--time <date and time>`: Start the clock at this time, for example `2020-11-01T15:00:22`.
- `--screenshot-count <number>`: Save this number of screenshots, and then quit. The names get a number, for example `sample-000.png`.
- `--screenshot-interval <seconds>`: The time between two screenshots of a series. The default is 0.1 seconds.

To compare the guide with ESQ, use the two options together:

`dotnet run --project Guide/Guide.csproj -- --sample --time 2020-11-01T15:00:22 --screenshot sample.png --screenshot-delay 1.2`

### Behavior
- The guide scrolls at 30 pixels each second, the default speed of ESQ.
- Each cycle of the listings starts with a time header row and a date row.
- The time bar takes its labels from the time header rows. When a header row gets to the top of the guide, its labels roll into the time bar. The old labels move up and out, and the new labels come in from below. The frame of the time bar does not move.
- A tall row stops the scroll for 2 seconds when it gets to the top of the guide. A tall row has one program across the full grid, with more than 2 lines of text. The row directly after a row that stopped the scroll does not stop it.
- If the guide cannot get listings, it shows a red row with "Please Stand By for your Local Listings. ER007". It tries again every 10 seconds.

### Keys
- `F`: Switch between full screen and a window.
- `M`: Change the full-screen mode (scaled fill, zoomed fill, letterbox).
- `S`: Save a screenshot to the folder of the build output.
- `P`: Show or hide the frame rate.
- `L`: Show or hide the log.
- `V`: Turn vertical sync on or off.
- `T`: Turn the 30 FPS limit on or off.
- `D`: Remove the first row.
- `Q`: Quit.

## Contributing?
Make an issue or a PR. I don't have as much time to work on this as I'd like, so I'm going at a glacial pace right now. Any help would be appreciated! Admittedly, this started off as a proof of concept so the code is pretty messy in some areas, the comments may not make much sense, there's graphical glitches and features definitely missing, and it's hella unoptimized.

## Todo:
- Background colors: Light blue and red support for premium channels
- Look at the imgui implementation in the dynamictextures branch, maybe bring some of that in
- Ad loops for the top-half of the screen (images, gifs, videos, etc.)
- Additional listings providers to create rows for things like "now showing" on media servers, etc.
- Super stretch goal: emscripten build

## Notes
This uses the [PrevueGrid font](https://ariweinstein.com/prevue/viewtopic.php?t=449) from @RudyValencia
