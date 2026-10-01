# Character Art Plan

One picture per PC and NPC: a half-body figure on a plain white background, drawn once by
the player's own image provider (BYOK). The client removes the background and stores the
picture in RavenDB through a small HTTP endpoint. The portrait is a crop of the same image.

The art shows up in three places:
- every place that shows a monogram today;
- the character builder's preview;
- a visual-novel style dialogue view, where the storyteller tags who is speaking and the
  client draws that line next to the speaker's figure.

Written 2026-10-01 against `master` @ `925f0b6` plus the uncommitted work from the same day.
`~` marks approximate line numbers; re-check them before starting a phase.

Related: `CHARACTER_CREATION_PLAN.md`. The builder's identity step gains the appearance
field this plan draws from. Neither plan blocks the other: until the builder ships, art is
drawn from `Character.CurrentAppearance` and the class line.

---

## Principles

- **Once per character.** A picture is drawn once and reused. Redrawing happens only when
  the player presses REDRAW, never automatically.
- **The client draws, the server stores.**
  - The image key stays in the client with the other BYOK keys.
  - The server never calls an image provider and needs no image key. A server running on
    its own (MCP, Grok Web) still serves the art it has.
- **Art is cosmetic.** Like `NpcVoice` today (`SegmentSplitter.cs` rule 1), a picture or a
  speaker tag never decides anything in the game.
- **Nothing silent:**
  - Before drawing, show which provider and model will be used.
  - A refusal (content moderation) shows the provider's reason on the tile.
  - A failure shows an error with RETRY.
  - A missing picture falls back to the monogram with a DRAW button.
  - When the speaker tags are missing, the dialogue view says the tags weren't found rather
    than just looking like plain text.
- **No new dependencies.** No bundled background-removal model and no WebP. The files are
  PNG, which Unity decodes natively.

---

## Data model

The art goes in a **separate document**, not on `Character`:

```
CharacterArt  (id: "art/" + characterId, e.g. "art/chars/mirelle")
  CampaignName      // campaign-scoped like every other entity
  CharacterId
  InputsHash        // hash of what the picture shows (see below); drives "out of date"
  Prompt            // the exact prompt sent, for redraws and debugging
  Style             // the campaign's art style string at draw time
  Provider, Model   // e.g. "openai", "gpt-image-1"
  KeyColor          // background colour asked for (default #FFFFFF)
  Tolerance         // mask threshold used
  Crop              // portrait rect in the source, normalised 0..1 {x, y, w, h}
  DrawnAt
attachments:
  source.png        // the image as generated (white background)
  figure.png        // background removed (alpha), the dialogue figure
  portrait.png      // square crop of figure.png, ~256px, for tiles
```

Why a separate document:
- **Concurrency.** Game turns write `Character` all the time, under
  `OptimisticConcurrencyMode.Writes` (`CampaignRepository.OpenSession`, ~line 86). Adding an
  attachment changes the document's change vector, so a turn in flight would conflict with
  a picture being saved.
- **The published SDK.** `Character` lives in `CampaignVault.PluginSdk`, which is published.
  Art fields there would be an SDK change for something that's purely cosmetic.
- **Embeddings.** `Character` implements `IHasDirt` and is re-embedded. Art metadata
  doesn't belong in that text or that cycle.
- **Deletion.** Deleting the character also deletes its `art/` document. That's one hook in
  the campaign delete and character delete paths (Phase 1 lists them).

`InputsHash` is computed from: name, ancestry or species, the class line, the appearance
text, the personality cue (for the expression), the campaign style and the key colour.
When the hash differs from the current inputs, the tile shows "portrait out of date ·
REDRAW". The old picture stays until the player redraws.

**Storing `source.png`** lets the player re-run the mask (tolerance, crop) without paying
for another drawing.

---

## Background removal (client, once at draw time)

**The prompt** asks for: a half-body figure, centred, head in the top third, facing
slightly to the side, on a **plain, flat, pure white background with no floor shadow, no
scenery and no frame**.

Current image models follow "plain white background" reliably. Without it they paint
scenery, so the instruction must always be in the prompt.

**The mask** is a flood fill, not a colour threshold:
- Start the fill from the **top, left and right edges only**. A half-body figure is cut
  off by the bottom edge, so a fill seeded there would eat a white robe.
- Spread through pixels within `Tolerance` of the key colour (default distance ~30 in RGB).
- Only background connected to the outside becomes transparent. A white beard, white eyes
  or a white tabard inside the outline survives. A plain threshold would punch holes in
  all of them.
- Feather the edge by about 2px, so the white fringe doesn't show on a dark dialogue panel.

**Pale subjects:**
- If the appearance or class mentions white, silver, snow, pale, ghost, platinum or bone,
  ask for a **flat mid-grey (#808080)** background instead.
- The key colour is stored, so the mask uses the colour that was actually asked for.
- The redraw dialog lets the player choose the key colour by hand too.

**Mask preview:** after drawing, show the figure over the dark panel colour with a
tolerance slider and the crop box. KEEP saves it; REDRAW pays again; RE-MASK is free.

This is pure C# on `Texture2D.GetPixels32` / `SetPixels32`, run once. There is no shader
and no per-frame cost.

---

## Phases

Phases 1–3 are useful on their own (portraits everywhere). Phases 4–5 add the dialogue view.

### Phase 1: server endpoint and storage

1. Add the `CharacterArt` model to `src/CampaignVault/Models/`, not the SDK.
2. Add the endpoints in `Program.cs` next to `/plugins` (~line 326), with
   `.RequireLocalPort(mcpPorts)` like the others. `AuthMiddleware` already covers them
   when a bearer token is set.
   - `GET  /campaigns/{slug}/art/{**characterId}?part=portrait|figure|source`
     returns the PNG, with an `ETag` (the attachment's change vector) and `304` support.
   - `GET  /campaigns/{slug}/art/{**characterId}/meta` returns the metadata JSON, or 404.
   - `PUT  /campaigns/{slug}/art/{**characterId}` takes a multipart upload (source, figure,
     portrait, plus the metadata JSON). Upload limit is about 8 MB; anything larger gets a
     `413` with a message the client shows.
   - `DELETE /campaigns/{slug}/art/{**characterId}`.
   - `GET  /campaigns/{slug}/art` lists the character ids that have art, with their hashes,
     so the client can fetch a whole party at once.
3. `{**characterId}` is a catch-all segment, because ids contain `/` (`chars/mirelle`).
   Check that the character exists in that campaign, and return 404 otherwise.
4. Validate uploads: PNG signature check and size cap. Reject anything else with `415`.
5. Delete art in the campaign delete path and wherever characters are deleted (find them
   with `find_referencing_symbols`).
6. MCP stays unaware of art, so tool tokens are unchanged. One optional extra: the party
   and scene tools could include `hasArt: true`, so another MCP client could show it too.
   It's not needed for Unity.

**Tests (server unit/integration):**
- Round trip PUT → GET (bytes equal) → ETag → 304.
- 404 for an unknown character and for a character in another campaign.
- 413 and 415 errors.
- Deleting a character removes its art.
- Saving art while a session holds the `Character` doc causes no concurrency conflict.

### Phase 2: client image provider and drawing

1. **Image provider profile** in `ByokSettings`, separate from the chat profiles: base URL,
   key, model and size. Off by default.
   - The first adapter is the OpenAI-compatible `POST /images/generations` (`b64_json`
     response). Note on the settings screen which providers it covers.
   - TEST draws a small sample and shows it.
   - Detection is only a hint. If `/models` reports `output_modalities` including `image`
     (OpenRouter does), suggest that model. Never switch it on automatically.
2. **Campaign art style.** One line chosen at onboarding: suggested from the tone answer,
   editable in the campaign settings. It goes into every prompt, so the party looks like it
   belongs in the same book. A new style marks every picture "out of date" (through the
   hash), but redraws nothing.
3. **`ArtPrompt` (pure C#, unit tested):** builds the prompt from the character's
   description, the style and the key colour. Personality only affects the expression.
   Also computes the hash.
4. **`ArtMask` (pure C#, unit tested):** the flood fill, the feathering and the crop.
5. **`ArtClient`:**
   - draw → mask → preview → upload;
   - fetch with an ETag cache on disk under `Application.persistentDataPath/art/{slug}/`;
   - shows every failure on screen.
6. **The draw dialog:** provider and model line, the prompt (editable, advanced), key
   colour, preview with the tolerance slider and the crop box, then KEEP / REDRAW / RE-MASK.

**Tests (EditMode):**
- Prompt contents.
- Stable hash, and a changed hash on each input.
- Mask: interior white survives, edge-connected white goes, a bottom-edge robe survives.
- The crop rect maths.
- The upload request is formed correctly against a mock server.

### Phase 3: portraits everywhere

1. `Ui.Portrait(parent, characterId, name, classes)`: shows the portrait if there is one,
   otherwise `Ui.Monogram`. It shows a small "out of date" dot when the hash differs, and a
   DRAW button when there's no picture and the image provider is on.
2. Replace the monogram in: party tiles, the sheet header, `cv-face`, allies, the builder
   preview (once the builder exists), and the companion stat block.
3. NPCs are drawn **on request**. Add "Draw NPCs when they first speak" to the settings,
   **off by default**. When it's on, each automatic drawing is announced in the story log
   ("Drawing Mirelle…"), so spending money is never silent.

**Tests:** PlayMode screenshots of party and sheet with portraits, plus the fallback
monogram case.

### Phase 4: speaker tags in the narration

Today `SegmentSplitter.VoicePattern` only catches `Name: "line"` at the start of a
sentence. The dense, novel-like prose you want (see the narration-taste memory) mostly
writes `"You're late," Mirelle says`, which that pattern misses. So the storyteller is
asked to mark speech explicitly, **without changing the prose**:

```
«Mirelle|You're late.» She doesn't look up from the ledger. «Mirelle|Again.»
```

1. **The tag format:** `«Name|spoken words»`.
   - Guillemets almost never appear in English prose, so there are no false matches.
   - Only the spoken words go inside; the speech tags and gestures stay in the prose.
   - The speaker is the NPC's name **exactly as the brief lists it**.
2. **The `Storyteller.NarrationHeader` contract:** tag every spoken line with the
   speaker's exact name from "Who is present or involved", and never tag the player
   character's own words unless they're quoted back. The single-pass `SinglePassNote` gets
   the same paragraph.
3. **`SegmentSplitter.SplitNarration`:**
   - Parse the tags first, into `NpcVoice` segments that keep their place in the prose.
   - Fall back to `VoicePattern` when there are no tags.
   - Handle broken tags (missing `|`, unclosed `«`) by showing the text as prose with the
     guillemets removed. A tag must never eat text.
4. **Name → character id**, so the art can be found:
   - Build a cast list for the turn from the party and the brief's `presentNPCs`. Check
     during the phase whether `presentNPCs` includes ids (Storyteller.cs ~line 252); if it
     doesn't, add `id` to that tool output on the server.
   - Match exactly, then case-insensitively, then by first name if it's unique.
   - A speaker with no match keeps the monogram and the speaker colour
     (`Ui.SpeakerColor`). It doesn't trigger a drawing.
5. **The prose stays readable:**
   - `TranscriptStore` (~line 207) rebuilds the "scene as narrated last turn" for the
     model. Rebuild it **with** the tags, so the storyteller keeps seeing the convention.
   - Strip the tags everywhere the player reads plain text: copy, export, recap.
6. **Visibility:** if the turn mentions present NPCs, contains quoted speech and has no
   tags, write a quiet line in the dev/diagnostics log, and show a small "speakers
   untagged" hint on the turn in the dialogue view.

**Tests (EditMode):**
- Tags parsed in reading order.
- Mixed tagged and untagged speech.
- Malformed tags lose no text.
- Names with apostrophes or hyphens and multi-word names.
- Name resolution order, and ambiguous first names.
- The fallback to `VoicePattern`.
- Tags stripped on export.

### Phase 5: the dialogue view (visual novel style)

1. **A view toggle in the story log: LOG / SCENE.** LOG is today's view: tagged lines
   become voice blocks with a small portrait in the gutter. SCENE is the new one:
   - the current speaker's `figure.png` is large, at the bottom left or right;
   - the line sits in a dialogue box with a nameplate;
   - narration between lines shows as a box with no figure;
   - roll cards stay inline;
   - the last two or three distinct speakers stay on stage, dimmed while not speaking.
2. **Paging:** click or Space shows the next segment; a "show the whole turn" button jumps
   to LOG for that turn. Streaming narration fills the current box as it arrives.
3. **Figures:**
   - Left or right stays the same for each speaker during a scene.
   - Cache the textures in memory per scene and free them on scene change, so a long
     session doesn't keep every NPC's image in memory.
   - A speaker with no figure shows a silhouette card with the monogram, and DRAW if
     enabled.
4. **Theme:** the dialogue box uses the existing parchment and speaker-colour tokens, and
   is readable at phone width (the figure shrinks above the box).

**Tests:** PlayMode screenshots in SCENE mode for a tagged two-NPC exchange, a missing
figure, a long line that scrolls inside the box, and phone width.

---

## Out of scope

- Generating the art on the server (the image key would have to live there).
- Scene backgrounds or location art. They'd fit the same endpoint later as `art/locations/…`.
- Expression variants (angry, sad), which would mean several drawings per character. If it
  ever happens, it's a `variants/` attachment set on the same document.
- Bundling a background-removal model.
- Animation or lip-flap in SCENE mode.

---

## Open questions

1. **Size:** draw at 1024×1536 (portrait orientation, about 1.5–2.5 MB of PNG with alpha),
   or 768×1152 to keep the database small?
2. **Monster stat blocks** (generic goblins): draw once per creature *type* and share it,
   or leave them with monograms? This plan leaves them out.
3. **PCs in SCENE mode:** do the player's own lines show their figure too (true visual
   novel style), or only the NPCs'?
4. **The art style line:** free text, or a short preset list (ink and wash, painterly,
   woodcut…) plus free text?

## Gates

- Each phase ends with the full server suite and EditMode/PlayMode suites green, and any
  failures reported.
- Phase 1 test runs use scratch campaigns only; never read existing campaign data.
- Phase 2 and 3 tests use a mock image server, so no real provider is called.
- Phase 4 is measured against a scratch campaign: tag coverage on 20 turns with two or
  more NPCs, with the target ≥ 90% of spoken lines tagged before Phase 5 starts.
