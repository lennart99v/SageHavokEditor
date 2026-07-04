# Roadmap

Planned improvements and polish, grouped by area. Items here are candidates — not commitments.

## Modifiers

- [ ] **"Common modifiers" section in the Add-Modifier picker.** The picker currently lists all 58 modifier classes alphabetically. Add a short curated group at the top (e.g. `hkbModifierList`, `BSDirectAtModifier`, `BSLookAtModifier`, `BSIsActiveModifier`, `BSTimerModifier`, `hkbModifierGenerator`) with the full list below.
- [ ] **Better default names for new modifiers.** New modifiers are named `New_<Class>`. Either prompt for a name on creation, or derive one from the target node (e.g. wrapping `GetUpFaceUp` with a `BSIsActiveModifier` → `GetUpFaceUp_IsActive`).

## Behavior tree (left panel)

- [ ] **Right-click context menu on tree items.** Add a context menu when right-clicking nodes in the behavior tree, with more options than are available today. First option: **"Jump to in graph"** — select/reveal that object in the graph view (drill to the right level and highlight the node). Other candidates to consider: copy id/name, inspect in Object Data, bookmark.
