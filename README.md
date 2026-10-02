<p align="center">
  <img src="assets/virtual-tv-plugin-banner.jpg" alt="Virtual TV" width="900">
</p>

# Virtual TV for Jellyfin

**Turn your Jellyfin library into your own television network.**

Virtual TV is a Jellyfin plugin that turns the movies and TV shows you already have in your libraries into custom TV-style channels.

Instead of always choosing a movie or episode manually, you can create channels with their own schedules and content rules, then watch them through Jellyfin's normal Live TV experience.

## Why I created it

I created Virtual TV because I could not find an existing solution that worked exactly the way I wanted.

There are already several great pseudo-TV and linear-TV tools, but I wanted something integrated directly into Jellyfin that gave me control over which content belongs to each channel, how that content is scheduled, and how the channel should behave for each user.

I wanted the convenience and surprise of traditional television, while still taking advantage of Jellyfin features such as watched status and resume progress.

## How it was created

I am not a programmer and had no previous experience developing Jellyfin plugins.

Virtual TV was created with the help of **ChatGPT**. I defined how I wanted the plugin to work, tested each version on my own Jellyfin server and devices, reported problems, and refined the behaviour step by step.

ChatGPT was used to help design and write the code, while the features and workflow were shaped around real-world use and testing.

## How it works

Virtual TV creates Live TV channels using media that is already available in your Jellyfin libraries.

You choose the movies, series or seasons that belong to a channel and decide how the content should be selected. Virtual TV then builds a schedule and exposes the channel through Jellyfin's normal Live TV Guide.

There are two main viewing styles:

- **Standard TV** — behaves like a traditional television channel. You join the programme at the point where it is currently scheduled, with the option to start the current programme from the beginning.
- **Personalized TV** — can use your Jellyfin user history to choose what to play, including unwatched episodes and partially watched content.

Your original media remains in Jellyfin; Virtual TV simply creates another way to watch it.

## Main features

- Create custom TV channels from Jellyfin movies and TV shows
- Choose specific series, seasons or groups of content
- Standard TV-style scheduled playback
- Personalized channels based on the Jellyfin user
- Sequential, Random and weighted **True Random** playback
- **Next Unwatched** and **Random Unwatched** modes
- Resume-first behaviour for unfinished content
- Optional support for Specials
- Movie-only or series-only channels
- Flexible scheduling and programme blocks
- Per-user channel visibility
- Content Coverage tools to identify assigned and unassigned media
- Integration with Jellyfin's existing Live TV Guide
- Start the current Standard TV programme from the beginning in the normal Jellyfin player

## Current release

The current full release is **Virtual TV v2.0.0.0**, developed and tested for **Jellyfin 12.1**.

Virtual TV is still a personal project and will continue to evolve as new ideas, improvements and issues are found through everyday use.

## Installation

Download the latest ZIP from the **Releases** section.

For a manual installation:

1. Stop Jellyfin.
2. Extract the included `VirtualTV` folder into your Jellyfin plugins directory.
3. Start Jellyfin again.
4. Open **Dashboard → Plugins → My Plugins → Virtual TV** and confirm that the plugin is active.

The exact plugins directory depends on how your Jellyfin server is installed.

## Jellyfin repository metadata

Repository name:

`R-BCK-gitgub / Virtual TV`

Repository URL:

`https://raw.githubusercontent.com/R-BCK-gitgub/jellyfin-plugin-virtualtv/main/manifest.json`

The repository catalog allows Jellyfin to identify the plugin, developer and repository information correctly.

## Feedback

Feedback, suggestions and bug reports are welcome.

If you find an issue or have an idea that could improve Virtual TV, feel free to open an issue on GitHub.

---

*Virtual TV is an independent personal project and is not affiliated with or endorsed by Jellyfin.*
