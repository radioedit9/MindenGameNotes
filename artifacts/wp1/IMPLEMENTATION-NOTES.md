# WP 1 — Page 1 Visual Fidelity Prototype

## Final WP 1 fidelity proof

Impact + Arial Narrow is the selected Page 1 baseline. The PDF writer embeds both TrueType fonts directly and the WPF/PDF paths use the same family pair and horizontal-scale metrics. The rejected variants have been removed.

## Embedding permissions

| Baseline | Display | Body | Installed `fsType` | PDF status |
|---|---|---|---|---|
| Page 1 | Impact | Arial Narrow | Impact `0x0008` editable; Arial Narrow `0x0000` installable | Both embedded |

No restricted/no-embedding flag was detected. These are system-wide Windows fonts. Microsoft permits document embedding of Windows-supplied fonts when the application honors the fonts’ OpenType embedding flags. The generated PDF is a document, not redistribution of standalone font files.

## Implementation

- Page 1 is a native 612 × 792 point display list in `PageOneRenderer`.
- `Tide-No Background.png` is alpha-cropped at render time and embedded as a dedicated PDF image XObject in the established masthead geometry. The flattened Page 1 authority is never embedded.
- Each typography PDF contains two `/Subtype /TrueType` resources, two `/FontFile2` streams, WinAnsi character widths derived from the embedded font’s `cmap`/`hmtx` tables, and a font descriptor derived from its metrics.
- PDF and WPF preview/raster paths consume the same normalized `GameNotesProject.PageOne` fields.
- Locked publication values are limited to static facts/configuration; weekly and verification-sensitive values are model properties.
- Enrollment visibly retains `[VERIFY]` until its verification flag is set.
- Broadcast Crew and the separate Springhill note were removed.
- `STAT OF THE WEEK` accepts multiple concise statistics.
- The footer reads `MINDEN HIGH SCHOOL GAME NOTES • PAGE 1`.
- Pages 2–8 retain their pre-WP 1 `PageComposer` composition.

## Metric adjustment

No container or page geometry changed. Existing per-label horizontal scaling is now applied identically by PDF (`Tz`) and WPF (`ScaleTransform`) to account for text metrics and maintain accepted fits.

The final pass added deterministic single-line width fitting, value wrapping with row-height calculation, Quick Facts stadium clearance, and Series History value wrapping. The matchup and venue bars shrink horizontally within their existing bounds rather than overflowing. The PNG proof DPI conversion was corrected to render the full 8.5 × 11 page.

## Canvas audit and root cause

- PDF MediaBox is exactly 612 × 792 points (8.5 × 11 inches at 72 points per inch).
- `PageOneRenderer` uses one top-origin point coordinate system. PDF output only flips the Y axis; it does not rescale coordinates.
- The flattened 1100 × 1424 reference pixels are not used as renderer coordinates.
- The cropped proof was caused by the PNG path applying a 72-point-to-target-pixel scale while `RenderTargetBitmap` also applied the requested target DPI. The transform was therefore applied twice in raster output. PDF container coordinates were not outside its MediaBox.
- PNG rendering now scales the 612 × 792 point display list exactly once and keeps the WPF bitmap surface at its native 96-DPI coordinate interpretation.
- Required-container validation runs before PDF and PNG generation. It fails generation if a required container is missing or if its bounds exceed the 11-point printable margin canvas (`x = 11…601`, bottom `y = 781`) or the physical MediaBox.
- Rightmost required containers end at `x = 601`; the bottom section ends at `y = 761`; the footer ends at `y = 781`.

## Final polish and lock candidate

- Impact remains limited to masthead/display text, section headings, prominent numbers, and deliberate callouts.
- Ordinary labels use Arial Narrow with synthetic bolding. Only Arial Narrow Regular is installed on this workstation; there is no separate Arial Narrow Bold font file. WPF requests bold weight from the same family, and PDF uses fill-and-stroke text rendering (`Tr 2`) against the embedded Arial Narrow font.
- Ordinary data text was increased modestly where the locked containers permit it. Deterministic width fitting and row-height wrapping remain active.
- The masthead title width was refined within its existing title reserve to reduce unused space before the unchanged Week box.
- The weekly `Venue` field now carries the intended two-line Week 1 value: `North Webster High School` and `Baucum-Farrar Stadium — Springhill, LA`. The matchup bar derives a single-line form from that same normalized field; no duplicate site authority was introduced.
- Explicit line breaks are now included in deterministic wrapping and row-height calculation.
- Stat of the Week and By the Numbers structures were not changed.

## Publication-depth primitives

The visual-depth pass introduced reusable, renderer-neutral publication tokens rather than Page 1-only colors:

- `PublicationFill.Black` — primary bars and strongest emphasis.
- `PublicationFill.DarkGray` — secondary headers such as Minden Leaders and Series Extremes.
- `PublicationFill.LightGray` — feature panels, inset results, alternating table rows, and selected callouts.
- `PublicationFill.White` — ordinary content panels.
- `StrongRule` (`1.2 pt`) — feature/callout boundaries.
- `NormalRule` (`0.65 pt`) — normal section boundaries.
- `LightRule` (`0.28 pt`) — internal table and comparison rules.
- `Bar` — reusable primary section bar.
- `SecondaryBar` — reusable dark-gray subordinate header.
- `FeaturePanel` — reusable light-gray inset with a strong boundary.
- Existing `Label` roles distinguish Impact display/major-stat text, Arial Narrow body text, and synthetic-bold Arial Narrow labels.

The WPF and PDF renderers resolve the same fill and rule tokens. Gray values are black `0%`, dark gray `28%`, light gray `88%`, and white `100%`, providing clear monochrome separation without interior crimson.

No required-container coordinate or dimension changed during this pass. Depth was added inside the frozen containers through fills, rule hierarchy, table cells, and typography emphasis only.

## Artifact caveat

`Minden-Game-Notes-Page-1-Proof.png` is a rasterization of the same native Page 1 display list, not a third-party rasterization of the PDF bytes. A headless Edge attempt produced a blank viewer capture and was discarded. The PDF itself remains native/vector.

## Repository hygiene

- `references/` is listed in `.gitignore`.
- Reference files remain under `references/` and were not copied into application source.
- This workspace does not contain `.git` metadata, so tracked/untracked status cannot be queried locally.
