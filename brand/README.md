# Mockifyr brand assets

Source artwork for the Mockifyr mark, lockup, app icon and favicon. Everything here is SVG, so it
stays sharp at any size.

## The mark

A pair of braces holding a single point — the API's own punctuation, and the engine's job in one
glyph. Braces are what a payload is written between; what sits between them here is a stand-in,
which is what a mock is.

## Colours

| Role | Hex | Notes |
| --- | --- | --- |
| Ink | `#111111` | The mark on light backgrounds. |
| Brand blue | `#0a4ecf` | Point + accent on light backgrounds. 7.0:1 against white. |
| Light blue | `#5a8dff` | Point on dark backgrounds. 6.3:1 against `#0a0a0c`. |
| Icon blue | `#bcd0ff` | Point on the brand-blue app-icon tile, where `#5a8dff` has too little separation. |

The point always switches with the surface behind it — that is deliberate, not an inconsistency.

## Geometry

The mark is drawn on a tight `0 0 188 120` viewBox: the artwork's ink, including the round stroke
caps, touches all four edges exactly. Consumers add their own padding, and the mark centres
correctly in any container without eyeballing.

Stroke width is `18` with round caps and joins throughout. The braces open away from the point, so
the weight is bounded by the brace's own counter rather than by its distance to anything else:
push the stroke much past this and each brace closes into a crescent-shaped blob, which is the
first thing to check after any change. The round caps are not decoration — square terminals on a
curve this short read as cut off rather than finished.

## Files

```
mark/      mark only — black (light bg) · white (dark bg) · duo (two-tone)
lockup/    horizontal logo, mark + wordmark — light / dark
app-icon/  512px rounded-square app icon — blue / black / white tile
favicon/   64px browser tab icon — transparent, theme-aware
social-preview.png   1280x640 share card for the repository's link preview
```

## The share card

`social-preview.png` is what a chat client or social platform shows when someone pastes a link to this
repository. GitHub generates a generic card from the repo name and description when none is set, so
this replaces that.

**It has to be uploaded by hand** — there is no API for it. Repository **Settings → General → Social
preview → Upload an image**. Once uploaded it applies to every link to the repository.

The docs site carries its own copy of the same card at `/og.png`, wired up through `og:image`, because
a link to the site and a link to the repository are scraped independently.

The card's wordmark is set in a system family rather than Sora, deliberately: it is rasterised once at
author time, so a font missing on the rendering machine would substitute silently — the same trap the
lockups carry a warning about below.

The favicon is the one file that deliberately breaks the geometry above, and it has to. Scaling a
line drawing down past roughly 24px does not preserve it: at tab size the master stroke lands near
one pixel and renders as grey haze next to neighbours that are bold shapes filling their whole box.
So the favicon is redrawn at its own optical size — same skeleton, stroke `30` instead of `18`, and
94% fill instead of 64%, which puts the line on about 2.3 device pixels at 16px. The extra weight
costs no clearance: each brace's nearest approach to the point is an arm tip some 63 units from its
centre, so the glyph gains body without the three shapes welding together.

It carries no tile. A tab strip already has a background, and a white square reads as a white square
on every tab that is not the active one. With nothing behind it the ink has to carry itself, so the
file switches colour on `prefers-color-scheme` rather than assuming a light browser.

## Which file to use

- Dashboard, light theme — `mark/mockifyr-mark-black.svg`
- Dashboard, dark theme — `mark/mockifyr-mark-white.svg`
- Browser tab — `favicon/mockifyr-favicon.svg`
- App / store icon — `app-icon/mockifyr-appicon-blue.svg`

Inside the dashboard the mark is not loaded from these files. It ships as an inline React component
(`ui/src/components/ui/brand-mark.tsx`) whose stroke is `currentColor`, so a single element follows
the theme instead of two files being swapped on every theme change.

## A note on the lockups

The wordmark in `lockup/*.svg` is live SVG `<text>` set in **Sora**. On a machine without Sora
installed it silently falls back to another font — the logo then renders as something that is not
the logo. Treat these files as editable sources, not as artwork to embed.

Wherever a lockup is needed in the product or in documentation, compose it from the mark plus real
text (HTML, markdown heading, slide title). That keeps the text selectable, translatable and
theme-aware, and removes the font dependency entirely. To ship a lockup as a single image, convert
the text to outlines first on a machine that has Sora.

## Trademark

The Mockifyr name and these logo files are trademarks of the project. The Apache-2.0 licence that
covers the source code does not grant trademark rights (see LICENSE §6 and NOTICE). Do not use them
to brand a fork or to imply endorsement.
