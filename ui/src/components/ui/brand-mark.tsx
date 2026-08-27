// The Mockifyr mark: a pair of braces holding a single point — the API's own punctuation, and the
// engine's job in one glyph. What sits between the braces is a stand-in, which is what a mock is.
//
// Drawn on a tight 188x120 viewBox: the ink, round stroke caps included, touches all four edges
// exactly, so the mark centres in any container without eyeballing and every consumer adds its own
// padding rather than inheriting some.
//
// The stroke is currentColor and the point is var(--brand): one element follows the theme, so there
// is no second file to swap and no flash on a theme change. The favicon is the one place this
// skeleton is redrawn rather than scaled — see brand/README.md.

export function BrandMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 188 120" fill="none" xmlns="http://www.w3.org/2000/svg" className={className} role="presentation">
      <g stroke="currentColor" strokeWidth="18" strokeLinecap="round" strokeLinejoin="round">
        <path d="M57 9 C29 9 33 49 9 60 C33 71 29 111 57 111" />
        <path d="M131 9 C159 9 155 49 179 60 C155 71 159 111 131 111" />
      </g>
      <circle cx="94" cy="60" r="14" fill="var(--brand)" />
    </svg>
  )
}
