# ADR-0008: Tool payloads are numeric-only

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.2` · [tool catalogue](../mcp-tool-catalog.md) ·
[ADR-0002](0002-read-only-venue-boundary.md)

## Context

Everything this server returns is read by a language model, and a model does not reliably distinguish data it
was given from instructions it was given. Any free text that reaches a tool result is text that can steer the
agent — and some of it originates outside the operator's control.

`trading-copilot` reached the same conclusion when it built the market context passed to its trigger reviewer,
and made that payload deliberately numeric-only, deferring news headlines precisely because they are the
injection surface.

The venue's own responses are mostly numbers, but not entirely: contract descriptions, account names, order tags
and error messages are all vendor-supplied strings, and `customTag` in particular is an arbitrary field.

## Decision

**Every field in a tool result is a number, a timestamp, a boolean, or an enum name drawn from a closed set
this repository defines.**

Concretely:

- Bars, indicators, levels, sizes and prices: numbers.
- Times: ISO-8601 UTC.
- Symbols and contract ids: strings, but **normalised and validated** against what this server resolved — never
  echoed back raw from the vendor.
- Account and instrument classifications: enum names from **this** codebase's vocabularies, not vendor text.
- Errors: this server's own messages, with vendor detail reduced to a numeric code.

What this excludes: contract display names, account names, order `customTag` values, and raw vendor error
strings.

## The awkward case, stated honestly

Account names are the awkward case. On this platform they encode real information — the funding stage and the
account size live in strings like `50KTC-V2-DLL-0000` and `PRAC-...`, and nothing else on the wire carries it.

The resolution is to **parse, not pass**: match the name against anchored patterns and return a
`Practice | Evaluation | Funded | Unknown` enum. A near-miss resolves to `Unknown`, never to a guess.

`record_observation` is the deliberate exception on the *input* side: an agent writes free text there, and reads
it back later. That text originates with the operator's own agent rather than the vendor, and it is stored and
returned as data the operator asked for. It is a smaller surface than a vendor feed, not a zero one, and this
record should be revisited if observations ever become shared across agents.

## Alternatives considered

**Pass vendor strings through with a "this is untrusted data" wrapper.** Rejected. The wrapper is itself text,
and it competes with the payload for the model's attention. Delimiters and warnings are a mitigation, not a
boundary.

**Sanitise rather than exclude.** Rejected. Sanitising prose against instruction-shaped content is not a solved
problem, and a filter that is 95% effective on a channel that is read every call is a filter that fails.

## Consequences

- Some genuinely useful vendor detail is dropped. A contract's display name would be pleasant to show; it is
  not worth the channel.
- Every vendor enum needs a mapping in this codebase, and an unrecognised value must map to an explicit
  `Unknown` rather than falling through. A silent default here would be a classification error wearing a valid
  name.
- Tool results are compact, which is a real secondary benefit — an agent asking for 500 bars pays for numbers
  rather than for repeated descriptive strings.
- This constrains future tools. A news or filings tool would violate this record and needs its own decision,
  not a quiet exemption.
