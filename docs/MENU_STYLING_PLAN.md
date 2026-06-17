# Session Browser context menu — styling rebuild plan

## Why this exists

The iterative patching of the row-context-menu styling in 1.0.2 produced a
sequence of partial fixes that each surfaced a new visual artefact (light
gutter, 1px boundary lines, mismatched item heights, light Separator). The
broken intermediate state was reverted before tagging the next release; the
menu currently uses WPF's default theme (ugly but stable). This plan is the
single spec we agree on before touching the XAML again.

Reference: user's mock screenshot — uniformly dark rows, no light gutter,
subtle 1-px separator lines, dark submenu popups.

## Visual target

| Element | Background | Foreground | Border | Notes |
|---|---|---|---|---|
| ContextMenu container | `#2A2A3A` | `#E0E0E0` | `#40606080` 1px | Already set on `ContextMenu` element |
| Item — idle | `#2A2A3A` | `#E0E0E0` | none | Flat dark, no gutter strip |
| Item — hovered / keyboard-highlighted | `#3A3A5A` | `#E0E0E0` | none | Subtle lift |
| Item — colored (Red / Orange / …) | `#2A2A3A` | inline override (`#E05050` etc.) | none | Style default must lose to inline `Foreground` |
| Item — destructive (Delete Session) | `#2A2A3A` | inline `#E08080` | none | Same as colored — inline wins |
| Submenu arrow | n/a | `#A0B0FF` | n/a | Only shown when `Role == SubmenuHeader`; glyph `▸` U+25B8 |
| Submenu popup container | `#2A2A3A` | `#E0E0E0` | `#40606080` 1px | Same palette as parent ContextMenu — popup reads as same surface |
| Separator | `#3A3A4A` | n/a | none | 1-px tall, `Margin=0,4` so it doesn't crowd neighbours |

No icon gutter column. No check column. No drop shadow inside the menu.

## Architecture

### One `Style` per shape, no named overrides

- **Implicit** `Style TargetType=MenuItem` in `Window.Resources`.
- **One** `ControlTemplate`. Differences between leaf and submenu-parent rows
  are switched on the `Role` property inside `ControlTemplate.Triggers`
  (`SubmenuHeader` → show arrow + bind popup; `SubmenuItem` → arrow collapsed
  + popup unused).
- **Implicit** `Style x:Key="{x:Static MenuItem.SeparatorStyleKey}"` —
  the only resource key WPF looks up for separators rendered inside a menu.
- No `DarkSubmenuHeader` / `DarkSubmenuItem` named styles. Every item in the
  `ContextMenu` picks the implicit style up automatically.

### `OverridesDefaultStyle=True` is mandatory

Without it, WPF re-applies the default theme's role triggers on top of our
template and the icon-gutter column drifts back in.

### `TemplateBinding` for `Foreground`

`ContentPresenter` must bind `TextElement.Foreground="{TemplateBinding Foreground}"`
so the inline `Foreground` on individual `<MenuItem>` declarations (the
colored dots, the red Delete Session) still wins over the style default —
`TemplateBinding` reads the instance value, not the style setter.

### Scope: `Window.Resources` is fine

Both context menus in `SessionBrowserWindow` (DataGrid rows and column
headers) should look the same dark style. No other MenuItems in this window
to worry about.

## Template structure (concrete)

```
Border (Background = TemplateBinding Background, BorderThickness=0, Padding=10,5)
└── Grid
    ├── Col 0 (*)         ContentPresenter ContentSource=Header
    │                     TextElement.Foreground = TemplateBinding Foreground
    ├── Col 1 (Auto)      TextBlock arrow  Text="▸" Foreground=#A0B0FF
    │                     Visibility=Collapsed (Trigger sets Visible)
    └── Popup popup       Placement=Right, IsOpen=TemplateBinding IsSubmenuOpen
                          AllowsTransparency=True PopupAnimation=Fade
        └── Border        Background=#2A2A3A BorderBrush=#40606080 BorderThickness=1 Padding=2
            └── StackPanel IsItemsHost=True

Triggers:
  IsHighlighted=True   → root Border.Background = #3A3A5A
  Role=SubmenuHeader   → arrow.Visibility = Visible
```

## Separator template (concrete)

```
Border Background=#3A3A4A Height=1
Margin=0,4,0,4 (vertical breathing room)
OverridesDefaultStyle=True
```

## Edge cases that must keep working

| Case | What must happen |
|---|---|
| Inline `Foreground="#E05050"` on `● Red` | The dot still renders red |
| Inline `Foreground="#E08080"` on `Delete Session` | Text still red |
| Empty `Tag=""` on `Clear color` | No change in styling |
| `Resume with model →` opens its submenu | Popup appears to the right, items inside use the same style automatically |
| Keyboard navigation (Arrow keys) | `IsHighlighted` trigger fires the same way as mouse hover |
| `RecognizesAccessKey=True` on the `ContentPresenter` | Underline on `_R`esume, `_T`oggle etc. still works |

## Manual test plan after implementation

Tick every box before the next 1.0.x tag:

- [ ] All items render flat dark, no light gutter, no light strip on the left
- [ ] `Resume with model` and `Resume with effort` show `▸` flush right
- [ ] Hovering either parent opens its submenu, items inside also flat dark
- [ ] Separators render as thin (~1px) subtle lines, no light two-tone divider
- [ ] Mouse hover on any item highlights its row with `#3A3A5A`
- [ ] `●` color items keep their dot color
- [ ] `Delete Session` text is still red
- [ ] No 1-px artefacts at top/bottom of any row
- [ ] Submenu popup background matches the parent context menu surface
- [ ] Column-header context menu ("Restore default layout (columns + window size)")
      picks up the same style and reads dark too

## Explicitly out of scope for this rebuild

- WPF check / icon columns — we don't use checkable items, no need to model the
  column at all
- Top-level menu bar styling — sidekick has no `Menu` controls
- Animated highlight — flat color swap is enough
- Per-item icon glyphs — current items are pure text or text + Unicode dots
- Touch / pen input affordances

## Implementation order

1. Land this doc.
2. Apply the implicit `MenuItem` style and `Separator` style as one atomic
   commit — single source of truth, no incremental patches on top.
3. Run through the manual test plan above with the running app.
4. Only after every box ticks: remove the empty `DarkSubmenuHeader` /
   `DarkSubmenuItem` placeholder styles from `Window.Resources` (they exist
   today only so the legacy `Style="{StaticResource …}"` references compile;
   the rebuild can delete those references too in the same commit).

## Rollback

If the rebuild misses a corner, revert the single commit and the menu
returns to today's WPF-default baseline. No iterative patches on top —
either the rebuild works end-to-end or we go back to baseline.
