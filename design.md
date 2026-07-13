# nzbdav UI design guide

How the frontend looks and why. Every new piece of UI should read like it was
built by whoever built the overview dashboard — this file makes that concrete.
When a component here and reality disagree, reality (the overview page, the
provider settings page) wins; update this file.

## 1. Non-negotiables

- **Every color comes from a token.** No hex/rgb/hsl literals in component CSS.
  The palette lives in `frontend/app/app.css` under `:root` and
  `html[data-theme="..."]` (midnight, carbon, tangerine, claude, napster). A
  component styled with tokens works in all five themes for free; a hardcoded
  color breaks the light themes.
- **Both light and dark must work.** Tangerine and claude are light. If a
  design only works on dark backgrounds, it's wrong. Tinted backgrounds are
  built with `color-mix(in srgb, var(--token) N%, transparent)` so they adapt.
- **Icons are inline SVG with `currentColor`** (or CSS masks colored by
  tokens). Never image files, never fixed fill colors.
- **CSS modules per component** (`*.module.css`, `.d.ts` when the module has
  one). No inline `style={{color: ...}}` for colors that a class could carry —
  the few existing offenders are legacy, not license.
- **No `!important`** except when fighting bootstrap table specificity, and
  then a comment saying so.

## 2. Tokens (defined in `frontend/app/app.css`)

| Role | Tokens | Notes |
|---|---|---|
| Backgrounds | `--bg-base` → `--bg-elevated` → `--bg-surface` → `--bg-surface-2` | Ascending prominence. Page sits on `base`; cards on `elevated`; insets/wells on `surface`; controls/chips on `surface-2`. |
| Hover | `--bg-hover` | Generic hover wash for rows/buttons. |
| Borders | `--border-subtle` / `--border-default` / `--border-strong` | `subtle` for card outlines and row dividers; `default` for form controls; `strong` rarely. |
| Text | `--text-primary` / `--text-secondary` / `--text-muted` / `--text-faint` | Primary = headings & key values; secondary = body; muted = labels/meta; faint = hints/disabled. |
| Semantic | `--accent`, `--accent-contrast`, `--success`, `--warning`, `--danger`, `--danger-muted` | Accent is the interactive color (links, active states, primary tint). |
| Radii | `--radius-sm` 6px / `--radius-md` 10px / `--radius-lg` 14px | Controls / inner panels / cards, respectively. |
| Layout | `--top-nav-height`, `--left-nav-width`, `--page-padding` | |

## 3. Typography scale

Font is the app default (system stack). Sizes are deliberate and few:

| Use | Spec | Example in code |
|---|---|---|
| Card title | 14px / 600 / `--text-primary` / letter-spacing -0.015em | `.title` in `stream-history.module.css` |
| Section micro-label | 11px / 600 / UPPERCASE / letter-spacing 0.5–0.6px / `--text-muted` | `.provider-section-title` in `usenet.module.css` |
| Body / values | 12–13px / 400–500 / `--text-secondary` (values `--text-primary`) | row content everywhere |
| Meta / sublines | 11px / `--text-muted` | `.sub` under card titles |
| Hints | 11–12px / `--text-faint`, sentence case | `.form-hint` in settings |
| Big stat numbers | 2–2.5em / 700 / semantic color | `.statNumber` in `health-stats.module.css` |
| Pills/badges | 10–10.5px / 600 / UPPERCASE / letter-spacing 0.04em | `.reason` pills in stream-history |

Numbers that sit in columns get `font-variant-numeric: tabular-nums`.

## 4. Spacing & shape

- Card padding: **18–24px**; inner panel padding: **12–16px**.
- Vertical gap between page sections: **24–28px**; between elements inside a
  card: **10–14px**.
- Cards: `background: var(--bg-elevated)`, `border: 1px solid
  var(--border-subtle)`, `border-radius: var(--radius-lg)` (or 10–12px).
- Inset panels (detail rows, wells, code-ish areas): `--bg-surface` background,
  `--radius-md`, subtle border. An inset must look *contained*, not like text
  floating in the parent.
- Row dividers: `border-top: 1px solid var(--border-subtle)`; first row
  borderless (`stream-history.module.css` `.row` is the reference).
- Grids of cards: `repeat(auto-fill, minmax(<min>px, 1fr))` with 20px gap.

## 5. Component recipes (copy these, don't invent)

**Card / page panel** — see `.container` in `stream-history.module.css` or
`health-stats.module.css`. Header row is flex space-between: title block left,
actions/status pill right.

**Section micro-label** — 11px uppercase muted label above grouped content
(`.provider-section-title`). Optional inline hint appended in `--text-faint`,
lowercase, separated by `·`.

**Pill badge** — rounded-full, tinted background at ~10–14% of its semantic
color, text in that color:
```css
color: var(--success);
background: color-mix(in srgb, var(--success) 14%, transparent);
padding: 1px 8px; border-radius: 999px; /* + pill typography from §3 */
```

**Chip / stat chip** — small bordered box on `--bg-surface-2` or transparent,
`--radius-sm`–8px, used for window selectors and key–value pairs
(`.windowButton` in `repair-details.module.css` is the segmented variant).

**Buttons**
- Icon button: 32×32, `--bg-base` fill, `--border-subtle`, `--radius-sm`;
  hover raises to `--bg-elevated` + accent border/text
  (`.header-action-button` in `usenet.module.css`).
- Subtle danger action: transparent, danger-tinted border at 35%, hover fills
  10% (`.clearButton` in `stream-history.module.css`).
- Primary CTA: react-bootstrap `Button variant="primary"` (theme maps it to
  accent).

**Tables & lists** — prefer the list-row pattern (name + meta line, divider
between rows) over `<table>`. When a real table is needed: header cells in
section-micro-label typography, generous column gaps (16–24px), numeric
columns center- or right-aligned *consistently*, `tabular-nums`, no vertical
borders ever.

**Forms** — label (12–13px, 500) → control (`.form-input` pattern:
`--bg-base`, `--border-default`, `--radius-sm`) → `.form-hint` in
`--text-faint`. Optional fields say "(optional)" in the label, muted.

**Inline expansion row** (history import-stats is the reference once
compliant): the expanded content is an **inset panel** (`--bg-surface`,
radius-md, padding 12–16px, margin below the parent row), never bare text in
the table cell. Open state of the parent row gets a subtle accent cue.

## 6. Motion & interaction

- Transitions: `0.12s–0.2s ease` on background/color/border only. No movement
  animations for state changes; dnd-kit handles drag transforms.
- Hover on interactive rows/cards: background wash (`--bg-hover` or accent at
  4–6%), `cursor: pointer` only when the whole element is clickable.
- Clickability must be discoverable: hover wash + cursor, or a visible
  affordance (grip handle, chevron). Don't rely on either alone for novel
  interactions.

## 7. Anti-patterns (each of these has already happened once)

- Bare text dumped into a table cell without containment (import-stats v1).
- A pile of different font sizes in one panel — pick from §3 only.
- `"`-style default JSON escaping, `!important` sprinkled to win fights
  that restructuring would win better.
- Desktop-only styling inside a media block when the rule is universal
  (cursor/hover for clickable rows).
- Light-theme regressions from assuming dark backgrounds (themes v1).
- Labels invented per-component ("Pool Connections" vs "Pooled Providers") —
  reuse existing wording.

## 8. Checklist before shipping UI

1. Screenshot-think it in midnight **and** tangerine (light) — any hardcoded
   color or contrast failure?
2. Every size/weight from §3, every color from §2, every shape from §4?
3. Interactive things discoverable (§6) and non-interactive things not
   pretending (no stray pointers)?
4. Mobile: does it collapse sanely (`.desktop`/`.mobile` helpers in
   `page-table.module.css`)?
5. Numbers aligned with `tabular-nums`; empty states written ("Nothing
   streamed yet." tone — short, plain, no exclamation marks).
