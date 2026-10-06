# Virtual TV User Guide

This guide explains how to create and configure Virtual TV channels in plain language.

It is written for people who want to use the plugin without needing to understand how Jellyfin plugins, Live TV providers or playback APIs work.

> **Version covered:** Virtual TV 1.0.0 for Jellyfin 12.1.

## Installation

### Recommended — install directly from Jellyfin

1. Open **Dashboard → Plugins → Repositories**.
2. Add:
   - **Repository Name:** `R-BCK-gitgub / Virtual TV`
   - **Repository URL:** `https://raw.githubusercontent.com/R-BCK-gitgub/jellyfin-plugin-virtualtv/main/manifest.json`
3. Save the repository.
4. Open **Dashboard → Plugins → Catalog**.
5. Find **Virtual TV** and select **Install**.
6. Restart Jellyfin when prompted.

This is the recommended installation method and also allows Jellyfin to offer future compatible plugin updates through the normal Plugin Catalog.

### Manual installation

1. Download the latest ZIP from the repository's **Releases** section.
2. Stop Jellyfin.
3. Create a `VirtualTV` folder inside the Jellyfin plugins directory.
4. Extract the ZIP contents directly into that folder.
5. Start Jellyfin again.
6. Confirm **Virtual TV** is active under **Dashboard → Plugins → My Plugins**.

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

- "play my next unwatched Comedy Series episode";
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

## Client compatibility (important)

Virtual TV uses a few client-side behaviours that are not identical across Jellyfin apps. These features are currently tested primarily with **Jellyfin Web in a browser** and **Jellyfin for webOS**. The webOS app is a lightweight wrapper around the Jellyfin Web interface provided by the server, so its playback behaviour is generally closer to the browser than to fully native TV clients.

### Android TV

With the native **Jellyfin Android TV** client, **Personalized TV remains unsupported**. Its Live TV/loading-source → normal library-item handoff does not behave like Jellyfin Web, so watched-dependent Personalized TV features should not be expected to work there.

**Standard TV uses Jellyfin's normal Live TV path and is supported by the current Virtual TV architecture.**

The Standard TV **Record → Play from Beginning** shortcut is different: native Android TV sends Record through its own DVR/session flow, and that handoff is **not considered fully validated on Android TV in v1.0.0**. Pressing Record may still produce Jellyfin's native recording message instead of opening the library item. No real recording is stored by Virtual TV.

Use Standard TV live playback as the supported Android TV path. Treat Play from Beginning on Android TV as a known client-specific limitation in this release.

### Android phone and tablet

The normal **Jellyfin for Android** app is treated separately from Android TV. In Virtual TV 1.0.0, Android phones/tablets use the normal confirmed-loading bootstrap and then route the selected episode/movie through the companion WebView control session for the same local `playbackManager.play()` path used by Jellyfin when Play is pressed on an episode. Physical EOF returns to the Virtual TV Loading source before the next programme is resolved. Keep **Play next episode automatically** disabled for the Virtual TV user so Jellyfin does not advance its own episode queue.

The previous Android-specific manual Stop before PlayNow was removed because Jellyfin already handles replacement of the current player when PlayNow is received. If a Personalized handoff fails before the real item can open, Virtual TV now stops the loading player and logs the reason instead of leaving an endless Loading Virtual TV screen.

Web and webOS keep their existing successful Personalized TV flow unchanged.

### Hide from Android TV

When **Channel experience = Personalized TV**, the editor can show **Hide from Android TV**.

When enabled, that channel is omitted only from the **Jellyfin Android TV** client family. Virtual TV recognizes both the older `Jellyfin Android TV` name and the newer `Jellyfin for Android TV` name, including debug variants. This covers devices such as Android TV, Google TV and Fire TV when they use the Jellyfin Android TV application.

The hidden channel is filtered from Android TV channel lists, Guide/program responses and Live TV recommendation rows such as On Now/Upcoming. It remains available on:

- Jellyfin Web;
- Jellyfin for webOS;
- Jellyfin for Android phones;
- Jellyfin for Android tablets.

Changing this setting is a client-visibility change only and does **not** require Generate New Schedule. Virtual TV also blocks playback of a hidden Personalized channel if an Android TV client reaches it through stale cached data.

Use **Jellyfin Web/browser** or a web-based client such as **Jellyfin for webOS** as the primary reference clients for Personalized TV.

---

# 3. The Standard TV Record button

This is an important Virtual TV 1.0.0 behaviour.

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

The button is only being reused because Jellyfin exposes a Record action on many clients and there is currently no universal plugin button that can be added to every Jellyfin player. On Android TV, Virtual TV 1.0.0 also satisfies the client's native DVR request flow with a synthetic timer identifier before opening the actual item; no real timer is stored.

Some Jellyfin clients may still briefly show messages such as:

- "Recording scheduled";
- a red record icon;
- a Record state in the Guide.

That is a Jellyfin client-side visual response to the button being pressed. Virtual TV itself returns no DVR timers and creates no recording file.

If the red icon remains visible, refreshing the Guide normally clears it.

---

# 4. Creating a channel — every field explained

Open:

**Dashboard → Plugins → My Plugins → Virtual TV → Settings**

Then select **Create channel**.

The editor is designed so that some options appear or disappear depending on earlier choices. That is intentional: Virtual TV only shows settings that can actually affect the type of channel you are creating.

## Channel name

This is the name shown in Jellyfin Live TV and in the Guide.

Generic examples:

- Action Movies
- Comedy Movies
- Comedy Series
- Detective Series

Changing the name changes the label users see. It does not change which content is scheduled and does not, by itself, require a new schedule.

## Channel number

This controls where the channel appears in the Live TV lineup.

Channel numbers must be **1 or higher**.

If you enter a number already used by another Virtual TV channel, Virtual TV makes room automatically by moving the existing channel to the next number. If that number is also occupied, the shift continues until a free number is found.

Changing only the channel number does not change the programming.

## Channel experience

Choose:

- **Personalized TV**
- **Standard TV**

This is the most important field because it changes the playback architecture of the channel.

### Personalized TV

Choose this when the viewer's own Jellyfin state should matter, or when you want normal VOD controls.

Expected behaviour:

- the channel first opens the neutral **Loading Virtual TV** source;
- after the loading handoff, Virtual TV resolves exactly one library episode or movie;
- that item opens in Jellyfin's normal player;
- pause, seek, audio/subtitle selection and normal watched/resume behaviour are available;
- when the item physically ends, Virtual TV returns to the loading source and resolves the next item from the schedule/rules;
- only one VOD item is sent at a time, so Jellyfin's normal automatic next-episode queue does not take over the Virtual TV schedule.

Personalized TV allows:

- Sequential;
- Random;
- Next Unwatched;
- Random Unwatched.

Movie channels allow Random / Shuffle Cycle and Random Unwatched.

### Standard TV

Choose this when the channel should behave like a shared traditional broadcast.

Expected behaviour:

- the schedule is materialized in advance with real episodes or movies;
- everyone tuning to the channel sees the programme scheduled for that wall-clock time;
- joining halfway through a programme starts at the matching live offset;
- playback continues as one Live TV stream into later programmes;
- the source library item's watched status, resume position and Continue Watching state are not updated by normal live viewing;
- normal VOD seek/restart controls are not available on the live stream;
- eligible English subtitles are burned into the live picture automatically.

Standard TV deliberately does **not** offer watched-dependent modes because "next unwatched" is different for every user and therefore cannot represent one shared broadcast.

For Series channels, Standard TV offers only:

- Sequential;
- Random.

For Movie channels, Standard TV uses:

- Random / Shuffle Cycle.

### Standard TV quality

When **Channel experience = Standard TV**, the editor shows **Standard TV quality**:

- **480p** — 854×480;
- **720p** — 1280×720;
- **1080p** — 1920×1080.

This is the fixed broadcast resolution for that channel. Standard TV keeps one stable live-stream format while programmes change, so source material is scaled to the selected channel resolution while preserving its aspect ratio.

New channels default to **1080p** in the editor. Existing Standard TV channels that already use the historical **720p** setting keep it until you edit them, so upgrading or restoring configuration does not silently increase their processing requirements.

Changing Standard TV quality is treated as a channel programming change: **Save channel**, then use **Generate New Schedule**. The setting is editable at any time.

The resolution setting changes the output size only. The current Standard TV pipeline still normalizes the continuous broadcast to H.264/AAC for compatibility and continuity.

## Channel type

Choose:

- **Series**
- **Movies**

This determines which Jellyfin libraries, titles and playback options are available.

### Series

Only Jellyfin libraries configured as TV Shows are shown.

You select whole series, then optionally restrict each selected series by season and Specials.

Series channels also have a **Schedule strategy**, because Virtual TV must decide which series receives each turn.

### Movies

Only Jellyfin libraries configured as Movies are shown.

You select individual movies.

Movie channels do not show the Series schedule-strategy field because the movie pool itself is shuffled directly.

### What happens if you change Channel type while editing?

The editor clears the current library/content selection because Series IDs and Movie IDs are not interchangeable.

You then select compatible source libraries and content again.

## Playback / scheduling

This is a **read-only summary field**.

You do not edit it directly.

Virtual TV updates it as you change the Channel experience, Playback mode, Schedule strategy and, when relevant, Block duration.

Its purpose is simply to show the effective combination you are building before you save.

## Save channel

Saves the current configuration.

If the change affects programming, the channel is marked as needing schedule regeneration/reconciliation. The existing current programme is not immediately destroyed simply because you pressed Save.

## Cancel

Closes the editor without saving the changes made in that editing session.

## Clone Channel

Each channel row includes **Clone Channel**.

Clone Channel opens the normal channel editor as a **new channel**, pre-filled from the selected source channel. It copies the channel configuration, including:

- Channel experience and type;
- selected libraries and titles;
- per-series season, Specials and True Random weight settings;
- playback mode and schedule strategy;
- Standard TV quality;
- Hide from Android TV;
- consecutive turns and block settings;
- visibility and operating hours.

The clone receives a **new channel ID**, the next available channel number and a temporary name ending in **Copy** so you can rename it before saving.

The existing generated schedule is **not** copied. After making any desired changes, save the cloned channel and use **Generate New Schedule** to publish its programming.

## Export / Import channel configuration

Virtual TV 1.0.0 can save the editable configuration of an individual channel to a small portable file.

### Export configuration

Open an existing channel with **Edit** and choose **Export configuration**.

The exported file contains the channel settings that you could otherwise enter manually in the editor, including:

- channel name and number;
- Personalized TV / Standard TV and Standard TV resolution;
- channel type;
- selected libraries and titles;
- series season restrictions, Specials and True Random weights;
- playback and scheduling settings;
- visibility settings;
- operating hours.

The export deliberately **does not** contain:

- the generated schedule or Guide entries;
- channel artwork/image;
- internal channel identity;
- internal scheduler/reconcile state.

### Import configuration

**Import configuration** is available in both **Create channel** and **Edit channel**.

Selecting a previously exported file only **fills the editor fields**. It behaves as if you had entered those values manually.

Importing a configuration:

- does **not** save the channel automatically;
- does **not** generate a schedule;
- does **not** copy or restore artwork;
- does **not** overwrite internal schedule state.

After importing, review the fields and choose **Save channel**. If you created a new channel or changed programming settings, use **Generate New Schedule** afterwards exactly as you normally would.

If a referenced Jellyfin library, title or user no longer exists, Virtual TV keeps the editor usable and reports that the unavailable selection could not be restored.

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

This section answers:

> "Where is Virtual TV allowed to look for content for this channel?"

Only compatible Jellyfin libraries are shown:

- **Series channel** → TV Show libraries;
- **Movie channel** → Movie libraries.

You can select one or several compatible libraries.

### What selecting a library does

Selecting a library makes its compatible titles available in the **Content** section below.

It does **not** automatically add every title in that library to the channel.

A title is scheduled only when you explicitly select that title in the Content section.

### Example using generic libraries

If a server has:

- Main TV
- Archive TV
- Main Movies

then a Series channel can use Main TV and Archive TV, while a Movie channel can use Main Movies.

The actual channel can still contain only a small subset of the titles in those libraries.

### Expected behaviour when libraries change

When you add or remove a source library:

- the available Content cards are reloaded;
- previously selected titles that no longer belong to one of the selected libraries are removed from the channel selection;
- titles still available in the remaining libraries stay selected;
- changing the source-library set is a programming change and the future schedule should be regenerated/reconciled.

# 8. Content selection

This section answers:

> "Which exact series or movies may this channel use?"

After selecting source libraries, Virtual TV loads the compatible titles from those libraries and shows them as selectable cards.

## Selecting and deselecting titles

Click a title card to select it.

Click it again to deselect it.

Only explicitly selected titles are part of the channel's eligible content pool.

For a Series channel, the selected item is the **series**. Virtual TV later resolves eligible episodes from that series according to the Series options and Playback mode.

For a Movie channel, the selected item is the **movie** itself.

## Filter titles

Typing in **Filter titles** only changes what is currently shown on screen.

It does not remove hidden titles from the channel and it does not change any schedule by itself.

The filter matches the visible title list and library information.

## Select visible

**Select visible** selects every title currently shown by the active filter.

This is useful for building a genre/category channel when the Jellyfin titles you want can be found with the same search text.

Important: it selects the **currently visible filtered results**, not every title in every selected library.

## Clear selection

**Clear selection** removes all currently selected titles from the channel editor.

It does not delete anything from Jellyfin.

## What happens after adding or removing titles?

The channel configuration changes immediately when you save, but the already-published Guide is protected.

Use **Generate New Schedule** when you want the new selection reflected in future programming immediately.

Automatic reconciliation can also apply changes later, but Generate New Schedule is the explicit "apply my programming change now" action.

## Manual Order and selection order

For Series channels using **Manual Order**, the selected series become the starting list for the manual sequence.

You can then use the up/down controls in the Manual Order section to define the exact repeating order.

# 9. Series options

Series options appear once at least one Series title is selected.

Each selected series has its own independent settings.

## All Seasons

Enabled by default.

When **All Seasons** is enabled:

- all currently available normal seasons are eligible;
- future normal seasons added to that series in Jellyfin also become eligible automatically;
- you do not have to edit the channel just because a new season is added.

Specials are still controlled separately by **Include Specials**.

## Specific seasons

Disable **All Seasons** to choose individual season numbers.

Expected behaviour:

- only checked normal seasons are eligible;
- unchecked normal seasons are ignored by every playback mode;
- adding a new season to Jellyfin does not automatically make it eligible while All Seasons is disabled;
- Season 0 / Specials is not controlled by these checkboxes — use Include Specials.

This is useful for generic concepts such as:

- Early Seasons;
- Classic Era;
- Recent Seasons.

## Include Specials

Disabled by default.

Enable it when Season 0 / Specials should be eligible.

There is an important difference between ordered and random playback:

### Sequential / Next Unwatched

These modes need a reliable chronological position.

A Special is included only when Jellyfin metadata gives Virtual TV enough information to place it in chronology, for example:

- airs before a season;
- airs after a season;
- airs before a specific episode;
- or a usable premiere date.

A Special with no reliable chronological metadata may therefore be skipped in ordered modes.

### Random / Random Unwatched

Chronological placement is not required for random selection.

Eligible Specials can therefore participate even when they do not have enough metadata to place them precisely in episode order.

## True Random weight

This field appears only when **Schedule strategy = True Random**.

Allowed range:

**1 to 100**

The weight changes the probability that the series receives the next **series turn**.

It does not directly change which episode is chosen inside that series.

Example with generic series:

- Comedy Series — Weight 1
- Detective Series — Weight 1
- Action Series — Weight 3

Total weight = 5.

Approximate probability per series turn:

- Comedy Series: 20%
- Detective Series: 20%
- Action Series: 60%

Each turn is an independent draw.

Therefore:

- a Weight 3 series is three times as likely as a Weight 1 series;
- consecutive repeats are allowed;
- a weight does not guarantee a fixed number of appearances over a short period;
- the displayed percentage updates as weights change.

# 10. Series playback modes

The **Playback mode** answers:

> "Once the schedule has chosen a series, which episode should Virtual TV play?"

This is different from Schedule strategy, which decides **which series** gets the turn.

## Sequential

Available in Personalized TV and Standard TV.

Virtual TV uses eligible episodes in chronological order.

Expected behaviour:

- watched status is **not** used to skip episodes;
- if an episode was already watched, it can still be scheduled;
- the next concrete episode follows the previous concrete episode for that series;
- after the final eligible episode, the sequence cycles back to the beginning.

With Specials enabled, only Specials that can be positioned reliably are used in the chronological sequence.

### Personalized TV + Sequential

The concrete scheduled episode opens as a normal Jellyfin VOD item from the beginning.

Jellyfin can then update watched/resume data normally.

### Standard TV + Sequential

The episode is materialized into the shared Guide and broadcast at its scheduled wall-clock time.

Tuning in midway joins that episode midway.

Normal live viewing does not alter the source episode's watched/resume state.

---

## Random

Available in Personalized TV and Standard TV.

Random uses a **shuffle cycle**, not a completely independent random draw for every episode.

For each selected series:

1. Virtual TV creates a shuffled pool of all eligible episodes.
2. Each eligible episode is used once before that pool is refilled.
3. A new shuffled cycle then begins.
4. When possible, the first item of the new cycle is prevented from being the same item that just played.

Expected behaviour:

- watched status does not decide eligibility;
- watched and unwatched episodes can both be used;
- every eligible episode gets coverage before repeats within that series' shuffle cycle.

This is different from **True Random**, which applies to selecting the series, not the episode.

---

## Next Unwatched

**Personalized TV only.**

Next Unwatched does not preselect the final episode when the Guide is generated.

Instead, the Guide reserves a fixed block for a **series**. When playback reaches that block, Virtual TV checks the active user's Jellyfin data at that moment.

For the scheduled series, the exact order of priority is:

1. Find episodes that are **not marked Played** and have a saved playback position greater than zero.
2. If one or more exist, resume the **earliest chronological** partially watched episode.
3. If none are partially watched, choose the **earliest chronological never-started** episode.
4. If every eligible episode is already watched, fall back to a random eligible episode.

### What "earliest chronological" means

Regular episodes are ordered by season and episode number, with metadata such as premiere date used as a tie-breaker.

Eligible Specials are inserted only when they have enough chronology metadata to be positioned reliably.

### Example with generic episode state

Suppose the selected series has:

- Episode 1 — Watched
- Episode 2 — Started but unfinished
- Episode 3 — Never started

Next Unwatched resumes **Episode 2** first.

It does not jump to Episode 3 just because Episode 3 has never been opened.

### Resume behaviour

Virtual TV opens the real library episode and applies the user's saved Jellyfin resume position.

The episode then behaves like normal Jellyfin VOD.

---

## Random Unwatched

**Personalized TV only.**

Random Unwatched also resolves the final episode at playback time, but uses random choice within each priority group.

Exact priority:

1. Find eligible episodes that are not Played and have a saved resume position.
2. If any exist, randomly choose one of those partially watched episodes and resume it.
3. If there are no partial episodes, randomly choose among never-started episodes.
4. If everything is already watched, randomly choose from all eligible episodes.

When there is more than one valid candidate, Virtual TV tries to avoid immediately replaying the item that just completed in the same Virtual TV session.

### Important distinction

**Random Unwatched does not mix partial and never-started items into one equal pool.**

Partially watched episodes have priority.

Only when there are no resumable episodes does Virtual TV choose from never-started episodes.

---

## Why the Guide may show only the series name

Next Unwatched and Random Unwatched are dynamic.

The exact episode can change between schedule generation and playback because the user's watched/resume state can change.

For that reason, the Guide stores a **series block** rather than pretending it already knows the final episode.

# 11. Series schedule strategies

The **Schedule strategy** answers:

> "Which selected series gets the next turn?"

It does not decide the episode. After a series is selected, the Playback mode decides which episode that series supplies.

All five strategies can be used with Personalized TV Series channels. Standard TV Series channels also use these strategies, but only with Sequential or Random episode playback.

## Repeating Order

Virtual TV shuffles the selected series once, stores that base order, and repeats it.

Generic example:

1. Comedy Series
2. Detective Series
3. Action Series
4. Comedy Series
5. Detective Series
6. Action Series

Expected behaviour:

- the order is stable after it has been created;
- it repeats continuously;
- adding/removing eligible series causes the stored order to be rebuilt;
- pressing **Generate New Schedule** explicitly clears the previous repeating order so a new shuffled base order is created.

Choose this when you want a predictable repeating lineup with a randomized starting arrangement.

---

## Randomized Rotation

This is a shuffle cycle at the **series level**.

Expected behaviour:

1. Virtual TV shuffles the eligible series.
2. Every selected series gets a turn once before the cycle refills.
3. A new shuffled series cycle begins.
4. When there is more than one series, Virtual TV tries to avoid the last series of the old cycle immediately becoming the first series of the new cycle.

This gives every selected series regular coverage without fixing one permanent order.

Choose this when fairness/coverage matters more than probability.

---

## True Random

Each series turn is an independent weighted draw.

Expected behaviour:

- there is no "must use every series first" rule;
- the same series can be selected twice in a row;
- the **Weight** configured on each selected series controls its relative chance;
- after a series is selected, Consecutive turns is applied before the next weighted draw.

Generic example:

- Action Series — Weight 4
- Comedy Series — Weight 2
- Detective Series — Weight 1

For each new series draw, Action Series is four times as likely as Detective Series.

Choose this when some selected series should intentionally appear more often than others.

---

## Manual Order

You define the exact repeating order.

The **Manual series order** section appears only when Manual Order is selected.

Use the up/down buttons to arrange the selected series.

Expected behaviour:

- the list repeats from top to bottom;
- newly selected series are appended to the list;
- removing a series removes it from the effective order;
- the list order is deterministic until you change it.

Choose this when you want direct editorial control.

---

## Smart Schedule

Smart Schedule creates a stable weekly pattern based on a Sunday-to-Saturday week.

Expected behaviour:

- Virtual TV creates a seeded shuffled series order;
- the order is **coverage-first** — every selected series gets a turn before any selected series receives a second turn, provided the week contains enough turns;
- the same weekly pattern begins again each Sunday while the current template is active;
- the template remains stable until its configured refresh boundary;
- when the refresh boundary is reached, a new template seed/order is generated.

### Smart Schedule refresh

Available choices:

- Every 1 month
- Every 2 months
- Every 3 months
- Every 6 months

This setting controls how long the current weekly template remains in use before Virtual TV creates a new one.

### What happens when you edit or regenerate a Smart Schedule?

Virtual TV protects the current Sunday-to-Saturday week.

Programming changes are applied from the next Sunday boundary rather than rewriting the current week underneath viewers.

Choose Smart Schedule when you want a recurring TV-like weekly structure rather than a constantly changing rotation.

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

Available on Series channels.

Choices:

- **1 episode / block**
- **2 episodes / blocks**

This controls how many consecutive turns the currently selected series receives before the Schedule strategy is asked to choose another series.

## With concrete playback: Sequential or Random

A turn means one actual episode.

### 1 episode / block

Generic pattern:

- Comedy Series — one episode
- Detective Series — one episode
- Action Series — one episode

### 2 episodes / blocks

Generic pattern:

- Comedy Series — episode
- Comedy Series — episode
- Detective Series — episode
- Detective Series — episode
- Action Series — episode
- Action Series — episode

For True Random, the weight draw happens when a **new series turn group** is needed. With Consecutive turns = 2, the selected series receives two episode turns before the next weighted series draw.

## With dynamic playback: Next Unwatched or Random Unwatched

A turn means one fixed Guide block.

With Consecutive turns = 2, the same selected series receives two consecutive blocks before the Schedule strategy rotates to another series.

The actual episode inside each block is still resolved from the user's live watched/resume state at playback time.

# 14. Block duration

Block duration appears only for **watched-dependent Personalized TV modes**:

- Series → Next Unwatched
- Series → Random Unwatched
- Movies → Random Unwatched

Available values:

- 15 minutes
- 20 minutes
- 30 minutes
- 40 minutes
- 45 minutes
- 60 minutes
- 75 minutes
- 90 minutes
- 120 minutes

The block duration is primarily a **Guide/scheduling unit**.

It is not a command to cut the real VOD item after exactly that many minutes.

## Series: Next Unwatched / Random Unwatched

The Guide reserves a fixed block for a series.

Generic example:

**20:00–20:30 — Comedy Series**

At playback time, Virtual TV checks the user's Jellyfin state and resolves the actual episode.

The Guide shows the series because the exact episode cannot be known reliably in advance.

## Movies: Random Unwatched

The Guide reserves a fixed movie block and materializes an unwatched candidate when the schedule is generated.

At playback time Virtual TV validates that choice against the user's current state and can replace it if needed.

## What if the real episode or movie is longer than the block?

The real VOD item is not deliberately cut at the nominal block boundary.

When the item physically ends, Virtual TV returns to the loading source and checks **what schedule block is active at that wall-clock moment**.

Therefore:

- if the next block is already active, Virtual TV follows that block;
- if the same dynamic block is still active because the item ended early, Virtual TV can resolve another eligible item under that same block/rule.

Think of Block duration as "how the dynamic channel is divided in the Guide", not "maximum playback length".

# 15. Movie channels

Movie channels have fewer scheduling fields because Virtual TV does not need to rotate between separate series.

## Personalized TV — Random / Shuffle Cycle

Expected behaviour:

1. Virtual TV creates a shuffled pool of all selected eligible movies.
2. Each selected movie is used once before the pool refills.
3. A new shuffle cycle begins.
4. When possible, an immediate repeat across the cycle boundary is avoided.
5. The chosen movie opens as a normal Jellyfin VOD item from the beginning.

Watched status does not remove a movie from this mode's pool.

If you want watched state to matter, use Random Unwatched.

---

## Personalized TV — Random Unwatched

This mode uses the channel owner's Jellyfin state.

There are two stages: schedule generation and playback-time validation.

### When the schedule is generated

- if there are unwatched movies, Virtual TV builds the dynamic schedule from that unwatched pool;
- if every selected movie is already watched, it falls back to the full selected movie pool;
- selection uses a shuffle-cycle style pool to spread choices.

### When a movie block is actually played

Virtual TV checks the current user state again.

Exact priority:

1. If there are partially watched movies, randomly choose one of those and resume it.
2. Otherwise, if the movie materialized for this block is still never-started and is not an immediate repeat, use it.
3. Otherwise, randomly choose another never-started movie.
4. If every selected movie is watched, fall back to a random selected movie.

This means the final movie can differ from the title originally materialized into the schedule if the user's watched state changed after schedule generation.

When several valid choices exist, Virtual TV tries to avoid immediately repeating the last completed movie in the same Virtual TV session.

---

## Standard TV — Random / Shuffle Cycle

Standard TV materializes real selected movies directly into the shared Guide.

Expected behaviour:

- every selected movie is used once before a new shuffle cycle;
- all viewers see the same scheduled movie at the same wall-clock time;
- tuning in midway joins the movie at the corresponding live offset;
- live viewing does not normally change the source movie's watched/resume state;
- use the Standard TV **Record** shortcut if you want to leave Live TV and open the current movie from 00:00 as normal VOD.

# 16. Channel visibility

This controls which Jellyfin users are allowed to see the Virtual TV channel.

## All users

The channel is visible to all Jellyfin users.

Use this for shared channels whose programming does not depend on one person's watched state.

## Selected users

The user list becomes visible.

Check every Jellyfin user who should be able to see the channel.

At least one user must be selected before the channel can be saved in this mode.

Changing visibility does **not** require schedule regeneration.

## Watched-dependent Personalized TV channels

If the channel uses:

- Series → Next Unwatched;
- Series → Random Unwatched;
- Movies → Random Unwatched;

Virtual TV treats that channel as personal in this release.

Expected behaviour:

- the normal visibility controls are hidden;
- the channel is restricted to the administrator owner/current creator;
- another user's watch history is not used to make decisions for this channel.

The reason is simple: two users can have completely different Played and Resume states, so one shared "next unwatched" decision would be ambiguous.

# 17. Operating hours

This controls when the channel is considered on air.

## 24 Hours

When enabled:

- the channel has no planned Off Air period;
- On air from / Off air from fields are hidden;
- Virtual TV can schedule content continuously.

## 24 Hours disabled

Two time fields are shown:

- **On air from**
- **Off air from**

The default is:

- On air from: **07:00**
- Off air from: **02:00**

Times are interpreted using the Jellyfin server's local time zone.

Cross-midnight schedules are supported.

For example, 07:00 → 02:00 means the channel is on air from 07:00 in the morning until 02:00 the following morning.

If On air and Off air are set to the exact same time, the scheduling logic effectively treats the channel as always on.

## What appears in the Guide during Off Air?

Virtual TV creates explicit **Off Air** schedule entries.

The Guide shows **Off Air** and indicates when the channel returns.

## What actually plays during Off Air?

Virtual TV does not start a selected library episode or movie while the active schedule entry is Off Air.

The exact presentation depends on the channel experience:

### Personalized TV during Off Air

The neutral Virtual TV Live TV loading source is what Jellyfin opens for the channel.

After the normal loading confirmation, Virtual TV sees that there is no playable programme in the current Off Air window and **does not hand off to a library VOD item**.

In practical terms: no Next Unwatched/Random/Sequential movie-or-episode resolution is launched for the Off Air entry.

The visible lifetime of the neutral loading source can still depend on the Jellyfin client, but Virtual TV does not keep sending library PlayNow commands during Off Air.

### Standard TV during Off Air

Standard TV remains a real Live TV stream, but Virtual TV serves a neutral live slate instead of a library item for the Off Air period.

No source episode/movie is played for that Off Air entry.

## What if content starts before Off Air and runs long?

Virtual TV does not deliberately cut a concrete episode/movie in the middle just because the nominal Off Air time arrives.

A concrete programme that already started can finish its scheduled runtime, and the channel enters Off Air at the next schedule boundary.

Dynamic fixed blocks are different: their schedule block can be shortened at the Off Air boundary so that the Guide enters Off Air cleanly.

# 18. Saving a channel, schedule status and Generate New Schedule

Saving configuration and publishing future programming are related but separate operations.

## Save channel

**Save channel** stores the editor values.

While the save is in progress, the button changes to **Saving...** and the editor actions are temporarily disabled to prevent duplicate saves. Virtual TV only shows Jellyfin's **Settings saved** confirmation after the configuration has been persisted and the channel table is visible again. The Live TV Guide refresh then continues without keeping the editor blocked; if that follow-up refresh fails, the saved configuration is retained and Virtual TV shows a warning.

Changes that normally **do not** require rebuilding programme choices include:

- Channel name;
- Channel number;
- Visibility.

Changes that affect programming mark the channel for schedule reconciliation, including:

- Channel experience;
- Channel type;
- Playback mode;
- Schedule strategy;
- Standard TV quality;
- Source libraries;
- selected titles;
- season restrictions;
- Include Specials;
- True Random weights;
- Consecutive turns;
- Block duration;
- Smart Schedule refresh;
- Operating hours.

After saving a programming change, the existing Guide is not immediately destroyed. The channel is marked so the future schedule can be rebuilt safely.

## Schedule status in the channel list

The Schedule column shows the current schedule state first, with **Generate New Schedule** directly underneath it in the same channel row. This keeps the status visible while keeping the action next to the schedule it affects.

### Not generated

The channel exists but has no published Virtual TV schedule yet.

Use **Generate New Schedule**.

### Needs reconcile

The configuration changed after the current schedule was generated.

The currently published Guide can remain active while Virtual TV waits to apply the new programming safely.

You can press Generate New Schedule if you want to explicitly rebuild the future programming now.

### Ready

A schedule has been generated and Virtual TV shows the date through which future programming is currently available.

## Generate New Schedule

This creates/recreates future programming and refreshes Jellyfin's Live TV Guide.

Virtual TV protects what viewers could already be watching.

### Normal schedules

- schedule history is preserved;
- the current active programme/block is preserved;
- the new schedule begins at the next mutable boundary.

### Smart Schedule

- the current Sunday-to-Saturday week is preserved;
- the regenerated Smart programming starts at the next Sunday boundary;
- a new Smart template seed/order is created for the regenerated future period.

This avoids rewriting the Guide underneath the currently active programme or current Smart week.

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

# 20. Content Coverage — purpose, controls and expected behaviour

Content Coverage is a **reporting tool**.

It answers questions such as:

- Which Jellyfin series/movies are already used by Virtual TV?
- Which titles have not been assigned to any channel?
- Which titles are used by more than one channel?
- Does a channel still reference something that was removed from Jellyfin?

It does **not** change channel configuration by itself.

Opening, filtering or exporting the report never adds or removes content from a channel.

## What Content Coverage compares

Virtual TV looks at Series and Movie items in the Jellyfin library and compares them with each channel's explicitly selected Content list.

For a Series channel, coverage is counted at the **series level**.

Example:

If a generic series is assigned to a channel but that channel uses only Seasons 1–3, Content Coverage still considers the **series** assigned. It is not an episode-by-episode or season-by-season coverage report.

For Movies, each selected movie is compared directly.

Also remember:

**Selecting a source library does not count as assigning every title in that library.**

A title becomes Assigned only when it is explicitly selected in a Virtual TV channel's Content section.

## Summary cards

### Total

The number of entries currently known to the report.

This normally includes Jellyfin Series/Movies plus any missing configured references that Virtual TV can still identify as stale channel selections.

Because Missing entries are separate from Assigned/Unassigned, Total does not always have to equal Assigned + Unassigned.

### Assigned

The summary number counts titles that are assigned to **one or more** Virtual TV channels.

In the detailed status/filter view, a normal **Assigned** row means the title is assigned to exactly one channel; titles in more than one channel use the separate **Multiple channels** status.

### Unassigned

Titles that exist in Jellyfin but are not explicitly selected in any Virtual TV channel.

This is particularly useful when building a complete channel lineup and checking what content has not yet been categorized.

### Multiple channels

Titles explicitly selected in more than one Virtual TV channel.

This is not automatically an error.

A title may intentionally belong to several channels. The report simply makes that overlap visible.

## Missing items

A configured title can disappear from Jellyfin after a channel was created.

For example, the library may have been rescanned, removed or rebuilt.

When Virtual TV can still see a configured ID that no longer resolves to a real Jellyfin item, the report can show:

- **Missing library item**
- Library: **Unavailable**
- Status: **Missing**

Missing is a diagnostic state. It helps you identify a channel configuration that should probably be edited.

## Clicking the summary cards

The cards act as filters.

- **Total** → show everything;
- **Assigned** → show normal single-channel Assigned rows;
- **Unassigned** → show titles with no channel;
- **Multiple channels** → show titles used by more than one channel.

The Assigned summary count conceptually includes all titles used by at least one channel, while the table filter keeps Multiple-channel rows separate so overlap can be inspected independently.

## Search

The Search box checks:

- title;
- library name;
- assigned Virtual TV channel name.

Search works together with the active status/library/type filters.

## Library filter

Limits the report to one Jellyfin library.

Choose **All libraries** to remove that restriction.

## Type filter

Choices:

- All types
- Series
- Movies

Use this when you want to review TV and Movie organization separately.

## Refresh

**Refresh** rebuilds the report from the current Jellyfin library and current Virtual TV configuration.

Use it after making library changes if the report view is already open.

The plugin also refreshes coverage after channel configuration changes in the Virtual TV page.

## Export CSV

**Export CSV** exports the **currently filtered view**, not necessarily the entire unfiltered report.

The CSV includes:

- Library;
- Title;
- Type;
- Year;
- Channels;
- Channel Count;
- Status.

This is useful for reviewing the channel organization outside Jellyfin.

## Practical ways to use Content Coverage

### Find content not yet organized

1. Open Content Coverage.
2. Select **Unassigned**.
3. Optionally choose Series or Movies.
4. Use the list as a checklist when deciding what channel should receive each title.

### Find accidental overlaps

1. Select **Multiple channels**.
2. Review the listed channel names.
3. Decide whether the overlap is intentional.
4. If not, edit one of the channels and remove the title there.

### Find stale channel selections

1. Leave the report on Total.
2. Search for **Missing library item** entries or scan for the Missing status.
3. Edit the referenced channel and remove/reselect the unavailable title.

### Audit one library

1. Choose the desired Library filter.
2. Select Total, Assigned or Unassigned.
3. Optionally export the filtered result to CSV.

## What Content Coverage does not tell you

Content Coverage is not a schedule preview.

It does not tell you:

- how often a title will appear;
- which episode is next;
- the True Random probability of an individual episode;
- whether every season of a selected series is enabled;
- whether a title will play tonight.

Those behaviours are controlled by the channel's programming settings and schedule.

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

## What happens if I tune to a channel while it is Off Air?

The Guide shows **Off Air** and Virtual TV does not open a configured library title for that Off Air entry.

- **Personalized TV:** Jellyfin opens the neutral Virtual TV loading source, but Virtual TV does not hand off to a library VOD item while the schedule is Off Air.
- **Standard TV:** the continuous Live TV stream serves a neutral live slate instead of a scheduled library item.

So Off Air is not a hidden random programme and it does not deliberately create watched/resume activity for your selected content.

---

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

## Does Play from Beginning work on Android TV?

Not reliably enough to be considered supported in v1.0.0.

Android TV sends the Record action through its native DVR/session flow. Virtual TV does not create a real recording, but the native client may show a recording message and may not complete the handoff to the normal library player.

Standard TV live playback remains supported on Android TV; **Record → Play from Beginning on Android TV is a known client-specific limitation in this release**.

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
