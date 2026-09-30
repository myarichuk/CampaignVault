# Narration baseline: the 8-beat script

A fixed script for comparing DM prose before and after a change
(NARRATION_AND_CLIENT_PLAN.md, N0/N2/N3). Same provider, same model, same
settings for every run; only the change under test differs.

## Setup (every run)

1. Point the client at an **empty** database (fresh embedded data folder, or a
   scratch `CAMPAIGN_DB_PATH`). Never run this against real campaigns.
2. Create a SFW scratch campaign through onboarding: D&D 5e, low fantasy,
   a river valley with a mill town, one PC (a human ranger, level 3), one
   companion. Let the DM seed the world.
3. Start a session. Open F12 → Inspector and note the model and settings.

## Beats (paste each line as-is, wait for the reply, don't steer)

1. **Quiet travel:** `I shoulder my pack and follow the river road north toward the mill town, taking my time.`
2. **Search:** `At the first milestone I stop and search the ditch and the stones around it for anything out of place.`
3. **Failed check (retry if it passes, note it):** `I try to climb the crumbling boundary wall to get a look over the fields.`
4. **Chase with a miss:** `A figure bolts from behind the wall with something of mine. I chase them and throw my hatchet at their legs.`
5. **NPC exchange:** `In town I find whoever keeps the mill and ask them, politely, who's been prowling the river road.`
6. **Rest interrupted:** `We take a room at the inn and settle in for a long rest.`
7. **Combat start:** `Whatever woke us, I grab my bow and go to the window to see it.`
8. **Lore recall:** `Once it's quiet, I think back on what I know about this valley and its old stories.`

## Export and measure

F12 → Inspector → **EXPORT TRANSCRIPT** (the path is copied), then:

```
python3 scripts/measure/prose_stats.py <transcript.md>
python3 scripts/measure/prose_stats.py <before.md> <after.md>
```

Record the summary in NARRATION_AND_CLIENT_PLAN.md under the phase's gate, and
read the transcripts: the numbers support the judgement, they don't make it.
