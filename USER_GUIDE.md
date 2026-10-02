# Virtual TV User Guide

This guide explains how to create and configure Virtual TV channels in plain language.

It is written for people who want to use the plugin without needing to understand how Jellyfin plugins, Live TV providers or playback APIs work.

> **Version covered:** Virtual TV 2.0.0.0 for Jellyfin 12.1.

---

## 1. The basic idea

Virtual TV turns movies and TV shows that already exist in your Jellyfin libraries into TV-style channels.

When you create a channel, you are making four main decisions:

1. **What type of channel is it?** Series or Movies.
2. **How should it behave when you watch it?** Personalized TV or Standard TV.
3. **What content is allowed on the channel?** Libraries, titles, seasons and Specials.
4. **How should Virtual TV choose what comes next?** Playback mode and, for series channels, schedule strategy.

The most important thing to understand is the difference between **Personalized TV** and **Standard TV**.

---

# 2. Personalized TV vs Standard TV

## Personalized TV

Personalized TV is designed for a more personal Jellyfin experience.

In simple terms, when you tune to the channel:

1. Jellyfin briefly opens the Virtual TV loading screen.
2. Virtual TV checks what should play at that moment.
3. It opens **one normal Jellyfin library item** — an episode or movie.
4. You then use Jellyfin's normal player controls.
5. When the item really finishes, Virtual TV returns to the loading screen and decides what should play next.

That loading step is intentional. It gives Jellyfin time to close one player cleanly before Virtual TV opens the next item.

Because Personalized TV opens the real library item, Jellyfin can normally update:

- watched status;
- resume position;
- Continue Watching;
- normal audio/subtitle choices;
- normal seek controls.

For **Sequential** and **Random** modes, the scheduled item normally starts from the beginning.

For **Next Unwatched** and **Random Unwatched**, Virtual TV can use your Jellyfin watch history and resume position to decide exactly which episode or movie to open.

### Best use cases

Personalized TV is useful when you want things such as:

- "play my next unwatched Simpsons episode";
- "resume something I started before choosing a new episode";
- "give me a random unwatched movie";
- normal Jellyfin seek, pause and resume controls.

---

## Standard TV

Standard TV behaves much more like a real television channel.

The schedule is decided in advance. When you tune in, you join the programme **at the point where it is currently being broadcast**.

Example:

- a movie is scheduled from 20:00 to 22:00;
- you open the channel at 20:45;
- Standard TV joins that movie around the 45-minute point.

The channel continues through later programmes as one continuous Live TV stream.

Because Jellyfin sees the **channel** rather than the source episode/movie as the thing being played, normal source-item progress is not updated. That means Standard TV does **not** normally change:

- watched status;
- resume position;
- Continue Watching.

Standard TV is therefore useful when you want to channel-surf without changing your normal Jellyfin watch history.

### Live-TV limitations

The Standard TV stream itself behaves like live television:

- no normal VOD restart;
- no normal resume position;
- no normal seeking through the source item;
- no watched-state logic such as Next Unwatched.

English subtitles are automatically burned into the live picture when an eligible English subtitle track is available. Because they are part of the live picture, they are not a normal selectable subtitle track while watching Standard TV.

### Best use cases

Standard TV is useful when you want:

- a traditional linear TV experience;
- several users seeing the same scheduled programme at the same wall-clock time;
- channel surfing without affecting Continue Watching or watched status;
- a fixed programme guide that behaves like normal television.

---

# 3. The Standard TV Record button

This is an important Virtual TV 2.0 behaviour.

On a Standard TV channel, Virtual TV repurposes Jellyfin's normal **Record** action as a **Play from Beginning** shortcut.

So, if you are watching a Standard TV programme and want to watch the real episode or movie from the beginning:

1. Open the programme controls.
2. Choose **Record**, **Just this once**, or, on clients that show it, **Record series**.
3. Virtual TV identifies the programme currently on air.
4. It exits the Live TV stream.
5. It opens the actual Jellyfin library item from **00:00** using Jellyfin's normal player.

From that point onward you are no longer watching the live channel. You are watching the normal library item, so normal Jellyfin behaviour applies again, including watched status, resume progress and normal player controls.

## Does it really record anything?

**No.**

Virtual TV does not create a DVR recording, recording file or recording timer.

The button is only being reused because Jellyfin already exposes it consistently across clients and there is currently no universal plugin button that can be added to every Jellyfin player.

Some Jellyfin clients may still briefly show messages such as:

- "Recording scheduled";
- a red record icon;
- a Record state in the Guide.

That is a Jellyfin client-side visual response to the button being pressed. Virtual TV itself returns no DVR timers and creates no recording file.

If the red icon remains visible, refreshing the Guide normally clears it.

---

# 4. Creating a channel — step by step

Open:

**Dashboard → Plugins → My Plugins → Virtual TV → Settings**

Then select **Create channel**.

## Channel name

This is the name that appears in Jellyfin Live TV.

Examples:

- Cartoon Network
- Saturday Morning Cartoons
- Classic Movies
- The Simpsons Golden Age

## Channel number

This controls the order of the channel in the Live TV lineup.

If you assign a number that is already being used, Virtual TV moves the existing channel — and, if necessary, following channels — up to make room.

---

# 5. Channel experience

Choose one:

- **Personalized TV**
- **Standard TV**

This is the most important setting because it controls how playback works.

| Feature | Personalized TV | Standard TV |
|---|---|---|
| Opens normal Jellyfin item | Yes | No |
| Joins current programme at live position | No | Yes |
| Normal seek controls | Yes | No |
| Jellyfin watched/resume updates | Yes | No |
| Next Unwatched / Random Unwatched | Yes | No |
| Record button = Play from Beginning | No | Yes |
| Shared traditional TV experience | No | Yes |
| Automatic English subtitle burn-in | No | Yes, when available |

---

# 6. Channel type

Choose:

- **Series**
- **Movies**

A Series channel can only use Jellyfin libraries configured as TV Shows.

A Movie channel can only use Jellyfin libraries configured as Movies.

Changing the channel type changes the available playback options because episodes and movies are scheduled differently.

---

# 7. Source libraries

Select one or more compatible Jellyfin libraries.

Only titles from those libraries will be available for the channel.

For example, you could create:

- a Series channel using only your **TV Kids** library;
- a Movie channel using only your **Movies** library;
- a Series channel combining two different TV-show libraries.

Selecting a library does **not** automatically put every title on the channel. It defines where Virtual TV is allowed to look. You still choose the actual titles in the next section.

---

# 8. Content selection

Select the series or movies that should be available to the channel.

You can:

- click individual titles;
- filter by name;
- use **Select visible** after filtering;
- clear the selection and start again.

For Series channels, selecting a series also unlocks individual **Series options**.

---

# 9. Series options

Each selected series has its own options.

## All Seasons

Enabled by default.

When enabled, all current seasons are eligible.

Future seasons added to Jellyfin are also automatically eligible.

## Specific seasons

Disable **All Seasons** if you only want certain seasons.

Example:

**The Simpsons — Seasons 1 to 12 only**

This is useful for channels built around a specific era of a long-running show.

## Include Specials

Disabled by default.

Enable it if Season 0 / Specials should also be part of the eligible episode pool.

If Specials are included, Virtual TV treats them as eligible content alongside the selected normal seasons.

## True Random weight

This option appears only when the schedule strategy is **True Random**.

The weight controls how likely that series is to receive the next turn.

Examples:

- Weight 1 vs Weight 1 → both are equally likely.
- Weight 2 vs Weight 1 → the first is twice as likely.
- Weights 1, 1 and 3 → the probabilities are approximately 20%, 20% and 60%.

A weight does **not** guarantee a fixed number of appearances. Every turn is a fresh random draw.

Consecutive appearances are therefore possible in True Random.

---

# 10. Series playback modes

The **Playback mode** answers this question:

> Once Virtual TV has chosen a series, which episode should it play?

The available options depend on the Channel experience.

## Sequential

Available in Personalized TV and Standard TV.

Episodes are used in chronological order.

After the last eligible episode, the series cycles back to the beginning.

In Personalized TV, the concrete scheduled episode opens as a normal Jellyfin item.

In Standard TV, the concrete episode is part of the fixed linear broadcast schedule.

## Random

Available in Personalized TV and Standard TV.

Virtual TV uses a shuffle cycle for episodes.

That means every eligible episode in a series is used once before a new shuffle cycle begins.

This avoids repeatedly picking the same few episodes while others never appear.

## Next Unwatched

Personalized TV only.

This mode uses the active user's Jellyfin watch history.

For the series chosen by the schedule:

1. If there is a partially watched episode, Virtual TV resumes the **earliest chronological** partially watched episode.
2. Otherwise it plays the earliest episode that has never been started.
3. If everything is already watched, it falls back to a random episode.

Example:

- S01E01 — watched
- S01E02 — 12 minutes watched, unfinished
- S01E03 — never started

Virtual TV resumes **S01E02** before moving to S01E03.

## Random Unwatched

Personalized TV only.

For the series chosen by the schedule:

1. If there are partially watched episodes, Virtual TV randomly chooses one of those and resumes it.
2. Otherwise it randomly chooses an episode that has never been started.
3. If everything is already watched, it falls back to a random episode.

This gives you variety while still prioritizing unfinished and unwatched content.

---

# 11. Series schedule strategies

The **Schedule strategy** answers a different question:

> Which series gets the next turn on the channel?

This is separate from Playback mode.

For example:

- Schedule strategy may choose **The Simpsons**;
- Playback mode then decides which Simpsons episode to use.

That distinction is important.

## Repeating Order

Virtual TV shuffles the selected series once and saves that order.

It then repeats that order.

Example:

1. The Simpsons
2. Futurama
3. Bob's Burgers
4. The Simpsons
5. Futurama
6. Bob's Burgers

The order remains stable until the eligible series set changes or you explicitly generate a new schedule.

**Generate New Schedule** creates a fresh repeating order.

### Good for

A predictable channel that still starts from a randomized lineup.

---

## Randomized Rotation

Virtual TV uses a shuffle cycle at the **series level**.

Every selected series gets a turn before a new cycle begins.

With four series, Virtual TV will try to use all four before any series starts the next cycle.

It also avoids an immediate repeat at the boundary between cycles when possible.

### Good for

Fair rotation with variety.

---

## True Random

Every series turn is an independent random draw.

Weights can make some series more likely than others.

Unlike Randomized Rotation:

- a series can appear again immediately;
- there is no requirement to use every series before repeating.

### Good for

Channels where some shows should dominate the schedule.

Example:

- The Simpsons — Weight 4
- Futurama — Weight 2
- King of the Hill — Weight 1

The Simpsons is four times as likely per turn as King of the Hill.

---

## Manual Order

You decide the exact repeating series order.

Use the arrows in the configuration page to move titles up or down.

Example:

1. Batman
2. Superman
3. Teenage Mutant Ninja Turtles
4. X-Men

Then the pattern starts again from Batman.

### Good for

A curated schedule where order matters.

---

## Smart Schedule

Smart Schedule creates a stable **Sunday-to-Saturday** weekly pattern.

The important ideas are:

- the series order is stable for the week;
- it is coverage-first, so every selected series gets a turn before a series receives a second turn, provided there are enough programme turns;
- the weekly pattern repeats;
- after the configured Smart Schedule refresh period, Virtual TV generates a new template.

Refresh options are:

- every 1 month;
- every 2 months;
- every 3 months;
- every 6 months.

If you make programming changes to a Smart Schedule channel, the current week is preserved and the changed schedule begins at the next Sunday boundary.

### Good for

A channel that should feel like a real recurring television schedule rather than a continuously changing shuffle.

---

# 12. Playback mode + schedule strategy combinations

For **Series channels**, think of the two settings like this:

- **Schedule strategy = which series gets the slot**
- **Playback mode = which episode that series supplies**

Examples:

| Playback mode | Schedule strategy | Result |
|---|---|---|
| Sequential | Repeating Order | Stable repeating series order; episodes advance chronologically |
| Sequential | Randomized Rotation | Every series gets a turn before the series rotation reshuffles; episodes remain chronological |
| Sequential | True Random | Series are independently drawn by weight; chosen series supplies its next chronological episode |
| Sequential | Manual Order | Exact series order you define; episodes advance chronologically |
| Sequential | Smart Schedule | Stable weekly series pattern; episodes advance chronologically |
| Random | Repeating Order | Stable repeating series order; each series uses its own episode shuffle cycle |
| Random | Randomized Rotation | Fair shuffled series rotation plus shuffled episodes |
| Random | True Random | Weighted random series selection plus shuffled episodes |
| Next Unwatched | Any strategy | Strategy chooses the series; Virtual TV resolves that user's next/resumable episode at playback time |
| Random Unwatched | Any strategy | Strategy chooses the series; Virtual TV randomly resolves resumable/unwatched content at playback time |

Standard TV only offers **Sequential** and **Random**, because watched-state decisions cannot be part of one shared linear broadcast.

---

# 13. Consecutive turns

Series channels can give a series:

- **1 episode / block**
- **2 episodes / blocks**

This controls how long a selected series keeps the channel before the schedule rotates to another series.

Examples:

### 1 turn

- Simpsons
- Futurama
- Bob's Burgers
- Simpsons

### 2 turns

- Simpsons
- Simpsons
- Futurama
- Futurama
- Bob's Burgers
- Bob's Burgers

For Sequential and Random modes, a turn is a real episode.

For Next Unwatched and Random Unwatched, a turn is a fixed schedule block.

---

# 14. Block duration

Block duration is used for watched-dependent Personalized TV modes.

Available values:

- 15
- 20
- 30
- 40
- 45
- 60
- 75
- 90
- 120 minutes

## Series: Next Unwatched / Random Unwatched

The Guide cannot know the final episode until playback time because the answer depends on the user's current Jellyfin history.

So the schedule contains a block for the **series**, not a preselected episode.

Example:

**20:00–20:30 — The Simpsons**

When you tune in, Virtual TV checks your watch state and decides which Simpsons episode should actually open.

This is why the Guide can show only the series title for these modes.

## Movies: Random Unwatched

The block represents the movie slot.

Virtual TV tries to use an unwatched movie, with partially watched movies taking priority for resume.

If the actual movie runs longer than the nominal block, the movie is not deliberately cut just because the block duration has been reached.

---

# 15. Movie channels

Movie channels are intentionally simpler.

They do not use the Series schedule-strategy selector.

## Personalized TV — Random / Shuffle Cycle

Every eligible movie is used once before a new shuffle cycle begins.

This gives variety without repeatedly choosing the same movie while others are still unused in the cycle.

The movie opens as a normal Jellyfin item.

## Personalized TV — Random Unwatched

Virtual TV uses the owner's Jellyfin watch state.

Priority is:

1. partially watched movies — randomly choose one and resume it;
2. never-started movies;
3. if everything is watched, fall back to random.

This mode is personal to the owner because different users can have different watched states.

## Standard TV — Random / Shuffle Cycle

Standard TV movie channels use a concrete shuffle cycle.

The selected movies are placed into the linear Guide and everyone watching the channel sees the same programme at the same wall-clock point.

---

# 16. Channel visibility

For channels that do **not** depend on watched state, you can choose:

- **All users**
- **Selected users**

Visibility changes take effect without requiring a new schedule.

## Watched-dependent channels

Personalized TV channels using:

- Next Unwatched;
- Random Unwatched;
- Movie Random Unwatched

are personal in Virtual TV 2.0.

They are automatically restricted to the administrator who owns/created the channel.

This prevents one user's watched history from being used to decide another user's channel.

---

# 17. Operating hours

Enable **24 Hours** if the channel should always be on air.

If 24 Hours is disabled, you can define:

- **On air from**
- **Off air from**

The default is:

- On air: 07:00
- Off air: 02:00 the following day

During the off-air period, the Guide shows **Off Air** instead of normal programmes.

This is useful if you want channels that imitate traditional broadcast schedules rather than running all night.

---

# 18. Saving a channel vs generating a schedule

These are two separate actions.

## Save channel

**Save channel** stores the configuration.

If you changed something that affects programming, Virtual TV marks the schedule as needing an update.

Examples include:

- changing channel experience;
- changing Series vs Movies;
- changing playback mode;
- changing schedule strategy;
- adding/removing titles;
- changing seasons or Specials;
- changing True Random weights;
- changing block duration;
- changing consecutive turns;
- changing operating hours.

## Generate New Schedule

Use **Generate New Schedule** when you want the new programming choices to appear in the Guide.

Virtual TV deliberately protects what is already on air.

For normal schedules, the current programme/block is preserved and regeneration starts from the next safe boundary.

For Smart Schedule, the current Sunday-to-Saturday week is preserved and the regenerated programming begins with the next week.

---

# 19. How much schedule is created?

Virtual TV keeps approximately **30 days of future programming**.

It also keeps about **7 days of schedule history**.

The plugin includes automatic maintenance tasks that:

- repair schedules after Jellyfin starts;
- apply library/configuration changes to future programming;
- extend the future schedule so it remains filled.

The default maintenance includes:

- startup recovery when Jellyfin starts;
- daily reconciliation;
- weekly schedule extension.

Normally you do not need to manage this manually.

---

# 20. Content Coverage

The **Content Coverage** report helps answer:

> "Which of my movies or series have I already assigned to a Virtual TV channel?"

It shows:

- **Total** — all series and movies seen by the report;
- **Assigned** — titles used by at least one channel;
- **Unassigned** — titles not used by any Virtual TV channel;
- **Multiple channels** — titles assigned to more than one channel.

You can also:

- search by title or channel;
- filter by library;
- filter Series vs Movies;
- export the filtered result to CSV.

If a channel still references an item that no longer exists in Jellyfin, it can appear as **Missing**.

---

# 21. What happens when you actually watch a channel?

## Personalized TV in plain English

Think of Personalized TV as a controller that opens one Jellyfin item at a time.

The cycle is:

**Virtual TV loading screen → one episode/movie → loading screen → next episode/movie**

The loading screen is not filler content. It is a deliberate handoff point that helps Jellyfin close the previous item cleanly and prevents the normal Jellyfin "next episode" queue from taking over the Virtual TV schedule.

Virtual TV sends only one item at a time.

When that item physically finishes, Virtual TV goes back to the channel, checks the current schedule/rules again, and chooses what should play next.

## Standard TV in plain English

Think of Standard TV as an actual broadcast.

Virtual TV looks at the wall clock, finds what should be on air now, enters that source at the matching time position, and continues into later scheduled programmes as a single stream.

The source episode/movie is not being played as a normal library item, which is why it does not normally affect watched/resume information.

---

# 22. Common questions

## Why does Personalized TV briefly show "Loading Virtual TV"?

That is expected.

The loading screen gives the client time to switch cleanly between Live TV and the real Jellyfin library item.

It also prevents Jellyfin's normal automatic next-episode behaviour from replacing the Virtual TV schedule.

---

## Why does Next Unwatched show only the series name in the Guide?

Because Virtual TV does not know the final episode until playback time.

The answer can change as your Jellyfin watched/resume status changes.

The Guide therefore schedules a **series block**, and Virtual TV resolves the actual episode when you tune in.

---

## Why does Standard TV not update watched status?

Because Jellyfin is playing the Virtual TV **channel stream**, not the original episode/movie as a library item.

That is intentional.

Use Personalized TV when you want watched/resume tracking.

---

## How do I restart the current Standard TV programme?

Use Jellyfin's **Record** action.

On Virtual TV Standard TV channels, Record means:

**Open the programme currently on air as a normal library item from the beginning.**

No recording is created.

---

## Jellyfin says "Recording scheduled". Is it recording?

No.

That message comes from the Jellyfin client because the plugin is reusing the existing Record command.

Virtual TV does not store a DVR timer and does not create a recording file.

The same applies to a temporary red Record icon in the Guide.

---

## What does "Record series" do on Android TV?

For a Virtual TV Standard TV channel, both:

- **Just this once**
- **Record series**

are routed to the same Virtual TV action: open the programme currently on air from the beginning.

No series recording rule is created.

---

## Why can I not use Next Unwatched in Standard TV?

Because Standard TV is one shared linear schedule.

"Next unwatched" is different for every user, so it cannot describe one universal broadcast that all users join at the same wall-clock point.

Use Personalized TV for watched-dependent modes.

---

## Why does Standard TV show English subtitles automatically?

Virtual TV looks for an eligible English subtitle track and, when available, burns it into the live picture.

This makes the live stream more consistent across Jellyfin clients.

Because the subtitle is already part of the picture, it is not a normal selectable subtitle track while watching Standard TV.

---

## Why did my configuration change not immediately appear in the Guide?

Saving configuration and generating programming are separate actions.

After changing programming options, use **Generate New Schedule** if you want the change applied to the future Guide immediately.

Virtual TV also has automatic reconciliation tasks, but Generate New Schedule is the clearest way to apply deliberate programming changes.

---

## What happens if content disappears from Jellyfin?

Virtual TV tries to keep the channel usable.

If there is no eligible content for a slot/channel, the Guide can show **Content Not Available** rather than silently failing.

The Content Coverage report can also identify missing configured titles.

---

# 23. Suggested configurations

## Traditional cartoon channel

- Experience: **Standard TV**
- Type: **Series**
- Playback: **Sequential**
- Schedule: **Randomized Rotation**
- Consecutive turns: **1**
- 24 Hours: **On**

Result: every selected show gets regular airtime, episodes progress in order, and everyone sees the same broadcast.

## Weighted comedy channel

- Experience: **Standard TV** or **Personalized TV**
- Type: **Series**
- Playback: **Random**
- Schedule: **True Random**
- Give favourite shows higher weights

Result: favourite shows appear more often, while episode selection still uses shuffle cycles.

## "Continue my shows" channel

- Experience: **Personalized TV**
- Type: **Series**
- Playback: **Next Unwatched**
- Schedule: **Randomized Rotation**
- Block: **30 minutes**

Result: the schedule rotates through shows fairly, but when each show comes up Virtual TV resumes or chooses your next unwatched episode.

## Surprise unwatched channel

- Experience: **Personalized TV**
- Type: **Series**
- Playback: **Random Unwatched**
- Schedule: **True Random**

Result: both the series selection and the unwatched episode choice feel unpredictable, while unfinished content is still prioritised.

## Personal unwatched movie channel

- Experience: **Personalized TV**
- Type: **Movies**
- Playback: **Random Unwatched**
- Block: **90 minutes**

Result: partially watched movies are resumed first; otherwise Virtual TV selects from movies you have not watched.

## Classic scheduled movie channel

- Experience: **Standard TV**
- Type: **Movies**
- Playback: **Random / Shuffle Cycle**
- Visibility: **All users**

Result: a shared movie channel with a fixed Guide and no effect on individual watched history.

---

# 24. A simple rule for choosing settings

If you are unsure, start with this question:

### Do I want everyone to see the same thing at the same time?

If **yes**, choose **Standard TV**.

If **no**, and you want Jellyfin history, resume points or unwatched logic to matter, choose **Personalized TV**.

Then:

- choose **Sequential** if episode order matters;
- choose **Random** if you want variety without repeats until the cycle is exhausted;
- choose **Next Unwatched** if progression is the priority;
- choose **Random Unwatched** if variety is the priority but unwatched content should come first.

For Series channels:

- choose **Repeating Order** for a stable repeating lineup;
- choose **Randomized Rotation** for fair variety;
- choose **True Random** for probability-based scheduling;
- choose **Manual Order** when you want exact control;
- choose **Smart Schedule** when you want a stable weekly television-style pattern.

---

## Feedback

Virtual TV is a personal project and continues to evolve through real-world use.

If something in this guide is unclear, or you find a configuration that behaves differently from what is described here, please open an issue in the GitHub repository.
