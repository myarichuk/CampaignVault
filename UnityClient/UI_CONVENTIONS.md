# UI conventions (Unity client)

The client uses UI Toolkit, Unity's retained-mode UI. If you know the web, the split maps directly:

| Web              | Here                                                        | Where                                              |
|------------------|-------------------------------------------------------------|----------------------------------------------------|
| HTML             | **UXML** templates                                          | `Assets/CampaignVault/UI/Resources/VaultUI/Templates/<Area>/*.uxml` |
| CSS              | **USS** theme                                               | `Assets/CampaignVault/UI/Theme/*.uss` (one theme, `VaultTheme.tss`) |
| View model / data context | **View models** (`ViewModel`, `[CreateProperty]`)   | `Scripts/UI/<Area>/*ViewModel.cs`                  |
| Data binding     | `<Bindings>` in UXML (`DataBinding`, `ClassBinding`)        | in the template                                     |
| Components       | **Custom controls** (`[UxmlElement] partial class`)          | `Scripts/UI/Controls/`                             |
| Code-behind      | The page class (`Overlay` subclass): lifecycle only         | `Scripts/UI/*Overlay.cs`                           |

UXML opens in UI Builder (Window → UI Toolkit → UI Builder) for visual editing.

## The rules

1. **Layout is UXML.** Anything not generated from data (a page, a card, a row, a section) is a template.
   Repeated content is a `Repeater` over a list property, with an item template. C# doesn't build UI trees.
2. **Looks are USS.** No `style.*` in C#. Visual state (selected, current, error, hidden) is a class toggled
   by a `ClassBinding`. The only inline styles allowed are runtime values with no class equivalent (a bar's
   fill width, the tooltip's position), listed in `UiConventionTests.Allowed`.
3. **State is a view model.** A page's view model `Watch`es the `VaultAppState` areas it shows and rebuilds
   its properties in `Refresh()`. Properties raise a change only when the value changed (`Set`, `SetList`),
   so binding touches only what changed: typing, focus, scroll and hover survive every server reply.
4. **The controller is the only writer of state.** View models call `VaultController` for anything that
   changes the game; they hold no game state of their own. (Draft text a field is holding, like the builder's
   question for the DM, may go straight to its slot on the state.)
5. **Commands are `Action` properties** bound to `VaultButton.command`. Enablement binds to `enabledSelf`, a
   hover hint to `tooltip`.
6. **Text is display-safe before it's bound.** Server and model text goes through `DisplayText.Plain` (shown as
   typed) or `DisplayText.Rich` (the markdown subset). Static text in UXML needs nothing.
7. **Names are test hooks.** PlayMode tests press real buttons by `name` (`opt-<id>`, `builder-commit`).
   Item names are bound from the item view model (`property="name"`). Keep them stable.
8. **No view model for tiny pieces** (a chip, a count line, an icon). A small template or a few elements in
   the parent template is enough.
9. **Templates carry no `<Style>`.** The theme styles everything, so a template looks the same wherever it's
   cloned.

## Building blocks

| Piece | What it does |
|---|---|
| `ViewModel` | `INotifyBindablePropertyChanged` base: `Set`, `SetList`, `Notify`, `Watch(state, areas)`, `Refresh`, `Dispose`. |
| `ItemList.Sync` | Keeps item view models across refreshes by key, so their elements are reused, not rebuilt. |
| `ITemplated` | A view model that names its template. `ContentPresenter` and `Repeater` clone it (one widget per step kind). |
| `Repeater` | `itemsSource` → one cloned `item-template` per item, each item the data source of its element. No virtualization; lays out with flex inside the page's scroll. |
| `LiveRepeater` | A list over an `ObservableCollection`: one element inserted, removed or swapped per change, the rest untouched. For lists that grow at the tail and trim at the head while read (the story log, toasts); `Repeater` re-binds by position. Expose the collection as an `IList` property, set once. |
| `ContentPresenter` | `content` → the template the view model names, rebinding when only the data changed. |
| `ClassBinding` | Toggles one or more USS classes from a bound value (`true`, non-empty string, non-zero, non-empty list). `invert="true"` with `cv-hidden` hides "nothing to show". |
| `VaultButton` | The cv-btn: `label`, `icon`, `command`; plays the click cue. |
| `VaultField` | The cv-field input with a bindable `placeholder`, and a `focusRequest` counter a view model bumps to focus it. Use `is-delayed="true"` to commit on blur/Enter, `password="true"` for secrets, `readonly="true"` for dumps. |
| `VaultSwitch` | An on/off switch with a `text` caption; `value` binds two-way (a bool property whose setter calls the controller). |
| `VaultSpinner` | An icon that turns while `spinning` (bind it); the rotation is the allowed runtime inline style. |
| `VaultCard` | The cv-card with a bindable small-caps `title` as its first line. |
| `ChoiceViewModel` + `Common/Choice` | One button of a group that shows whether it is the current choice (profiles, presets, rules). |
| `ActionViewModel` + `Common/ActionButton` | A foot or toolbar button as data (label, icon, enabled, tooltip, primary/ghost/danger); a page that changes its buttons per state exposes a list of them. |
| `TabViewModel` + `Common/Tab` | A tab in a row of tabs (settings, codex). |
| `Overlay` | Every page sits in `Shell/Modal.uxml`. A page's `Template` fills the modal's regions from its top-level `toolbar` / `body` / `dock` / `foot` elements; `CreateViewModel()` is made on open and disposed on close. |
| `Templates` | Loads and clones templates by path (`"Builder/OptionCard"`). |
| `TooltipLayer` | Shows any element's `tooltip` (from UXML or a binding) in the runtime bubble. A disabled button gets no hover, so its reason goes on a wrapper. |

## A binding, start to finish

```xml
<!-- Templates/Builder/OptionCard.uxml -->
<cv:VaultButton class="cv-option">
    <Bindings>
        <ui:DataBinding property="name" data-source-path="Name" binding-mode="ToTarget" />
        <ui:DataBinding property="command" data-source-path="Pick" binding-mode="ToTarget" />
        <cv:ClassBinding property="cls-selected" data-source-path="Selected" class-name="cv-option--selected" />
    </Bindings>
    <ui:Label class="cv-option__label">
        <Bindings><ui:DataBinding property="text" data-source-path="Label" binding-mode="ToTarget" /></Bindings>
    </ui:Label>
</cv:VaultButton>
```

```csharp
public sealed class OptionViewModel : ViewModel, IKeyed
{
    private bool _selected;
    [CreateProperty] public bool Selected { get { return _selected; } private set { Set(ref _selected, value); } }
    ...
}
```

Two-way fields bind `value` with `binding-mode="TwoWay"`. The view model's setter is the user's intent (it calls
the controller). `Refresh` takes a field's value from state **only when state changed underneath it** (keep the
value last seen, compare), then `Set(ref field, value, "Name")` shows it without echoing it back. Never copy state
into an edited field on every refresh: one field's commit refreshes the page, and a sibling field whose typing
hasn't reached the view model yet would get its old value pushed back over it.

Binding runs on the panel's update. In a PlayMode test, set a field's value, then let a frame pass before
asserting what reached the view model.

## Tests

- **View models:** EditMode, no panel. Change state, `Notify`, assert the properties, and assert what was
  *not* raised (`BuilderViewModelTests`).
- **Templates and binding plumbing:** `BindingTests` (PlayMode) and `UiConventionTests.EveryTemplate_Loads`.
- **Screens:** PlayMode, driving real buttons by name, with a snapshot of each state in `Library/VaultSnapshots`.
  Compare snapshots before and after a UI change.

## Pages with several states

A page that is a different thing at different times (the onboarding: a start form, a question, the brainstorm chat,
a wait) exposes `Page` (a `ContentPresenter` shows the view model's template), `Dock` (the docked reply box, or
null) and `Actions` (the foot buttons for that state). Page view models that must keep what was typed (the answer,
the start form) are kept for the life of the dialog and updated, not re-made, when the same question refreshes.

A dialog's tabs (Settings) each get a page view model made once per visit, so typing on one tab survives a trip to
another. A page that must ask the server for something when it opens does so in `Enter()`, never in `Refresh()`
(a failed load would otherwise loop).

Behaviour a binding can't do (scrolling the chat to its newest message) stays in the page class, driven by a
counter property on the view model (`ScrollTick`).

## The table

The shell is `Shell.uxml`: each region has its own data source (`TopBar` → `TopBarViewModel`, `Toasts` →
`ToastsViewModel`, `Party`, `Log`, `Command`, `Codex`), so nothing inherits a view model it doesn't use. Two
variant bindings with the same prefix on one element would clobber each other, and a prefix must not also start a
state class (`cv-roll--result-*` is the variant, `cv-roll--enter` a `ClassBinding`). Several independent states on
one element (a sigil that is ok, bad or busy) are one `ClassBinding` each.

`UiConventionTests.Legacy` is empty: no file builds UI in code any more. Keep it that way.
