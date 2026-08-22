# wiki/ — conventions

The wiki is **design-time domain knowledge**: vendor APIs, market sessions, instrument specifics. It is the
reasoning behind the requirements, kept in one maintained place.

**Not read by the product.** No code loads a page from here.

## Precedence

**Ingested reference, not repo truth.** When the wiki and a repo document disagree, the repo document wins —
`documentation/prd.md`, the ADRs and the data dictionary describe what *this system* does, while a wiki page
describes what something external does. A page that contradicts an ADR is a page that needs correcting, or an
ADR that needs an update; it is never a licence to follow the page.

## Page header

Every page opens with a metadata blockquote:

```
> **Trust tier:** authoritative | curated | unverified
> **Verified:** <how, and when> · **Sources:** <primary URLs>
> **Access:** <how the source was obtained, and anything its terms impose>
> **Informs:** <the R-# / Q-# it grounds>
```

- **authoritative** — checked against the vendor's own documentation or observed directly against the running
  system, with a date.
- **curated** — assembled from secondary sources and read carefully. Usable, worth re-checking before anything
  load-bearing.
- **unverified** — captured but not confirmed. Do not build on it.

## On third-party material

These pages are **original summaries** — endpoint names, parameters, limits, session times — written in our own
words from publicly documented sources, each cited by URL.

Facts and functional API details are **summarised, not reproduced**. No page carries substantial verbatim text
and no vendor document is redistributed here. Short quotes are attributed inline. Trademarks belong to their
owners and are used only to identify what is being described.

Where a claim is load-bearing — a limit, an enum value, a required field — mark it with how it was confirmed.
"The docs say" and "I watched it do this" are different kinds of knowledge, and the second is worth more.

## Corrections

A page is **corrected in place**, with a dated note in the header saying what changed and why the old reading
was wrong. That differs from an ADR, which is superseded rather than rewritten: an ADR records a decision, and
the history of a decision matters. A wiki page records an external fact, and a stale fact left visible for
provenance is a trap for the next reader.
