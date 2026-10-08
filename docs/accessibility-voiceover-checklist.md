# iPhone VoiceOver checklist

Requirement 35 asks for iPhone VoiceOver testing. A browser test cannot do it: VoiceOver runs only on an Apple device, with a person listening. This checklist is for that person. The automated suite covers everything else in requirements 35 and 36:

- `tests/browser/app-accessibility.spec.js`: scenarios 42, 43 and 44, reflow at 320 CSS pixels, reduced motion, and the weekly totals and a sync problem on a phone.
- `tests/browser/app.spec.js`: landmarks, the skip link, focus rings and touch targets.

Record each pass in the table at the end. WI-0068 stays open until a pass has every row marked **pass**, or a failure has its own work item.

## Before you start

- An iPhone on a current iOS, using Safari.
- VoiceOver on (Settings > Accessibility > VoiceOver), at the default speaking rate.
- A Chrona deployment. The GitHub Pages demo works for everything except signing in; it runs in local mode, where records last until the tab closes.
- Note the iOS version and the Chrona build. The build is `chrona-build.json` beside the page, or "development".

Gestures used below:

- **Swipe right / left:** next or previous item.
- **Double tap:** activate.
- **Rotor:** two-finger rotate, then swipe up or down.

## Checks

| # | Do this | Expect |
|---|---|---|
| 1 | Open Chrona. Swipe right from the top. | The skip link is read first ("Skip to content"), then the page's regions: navigation, the main content and the tab bar. Every control is read with a name and a role; none is read as just "button". |
| 2 | Rotor > Headings. Go through the headings on Today, Track and More. | Each screen has one first-level heading, and the section headings name what follows them. |
| 3 | On More, add a project, an activity type and a tag with the on-screen keyboard. | Each field is read with its label. Each addition is announced once. |
| 4 | Track: choose an activity type and a project, then double-tap **Start timer**. | "Timer started." is announced once. VoiceOver moves to **Pause**, not to the top of the page. |
| 5 | Stay on the timer for 30 seconds without moving. | Nothing is announced while the time counts up. Swiping to the timer reads the elapsed time in words ("32 seconds"), not digits. |
| 6 | Double-tap **Pause**, then **Resume**. | "Timer paused." and "Timer resumed." are each announced once. VoiceOver lands on the control that replaced the one tapped. |
| 7 | After at least 31 seconds, double-tap **Stop activity**. | The stop is announced with the time held. VoiceOver moves to "Complete this activity." |
| 8 | Leave the business purpose empty and double-tap **Save activity**. | The problem is read: "Add the business purpose: why this work mattered to the business." |
| 9 | Fill the purpose and save. | "Saved 1m of …" is announced once. |
| 10 | Today: swipe through the day's records and the "This period" card. | Each record reads its time, title, classification and status in words. Colour alone carries no meaning. The period totals read as labelled facts ("Exact, 1m"). |
| 11 | Track: enter a manual entry that overlaps the one just saved. | The refusal names what it overlaps and is read once, near the form. |
| 12 | Settings > Accessibility > Display & Text Size: turn on Larger Text at the largest size, and Reduce Motion. Return to Chrona. | Text grows with no sideways scrolling and nothing cut off. Nothing moves when you switch screens. |
| 13 | Turn the phone sideways, then back. | The layout reflows. The tab bar stays reachable, and VoiceOver's position is kept. |
| 14 | With a storing deployment, turn on Airplane Mode and sign in or reload. | "Your records could not be opened." is read, with the reason. **Try again** is reachable and labelled. |

## Recorded passes

| Date | Tester | iPhone and iOS | Chrona build | Rows that passed | Failures, with their work items |
|---|---|---|---|---|---|
| (none yet) | | | | | |
