---
status: accepted
---

# Cache the shared part of an Advisor request

Amends the seam of ADR [0036](0036-advise-the-evidence-check-through-a-configured-advisor.md) and the
pricing of ADR [0046](0046-map-each-item-once-and-price-every-map.md). Adds OpenAI's Decisions API as
the third Advisor.

## Context

OpenAI's Decisions API, generally available when checked on 2026-10-31: `POST /v1/decisions` with
`gpt-6-luna` only, 1–400 named questions over one `input`, choices as `{value, description}`, answers
in question order with `choice`, `probabilities` as `[{value, probability}]` and `confidence`, and a
separate `{"type": "refusal", "name": …}` answer. Input costs USD 0.10 per million tokens uncached,
0.01 read from the prompt cache and 0.125 written to it with the 30-minute lifetime; output is free.
An explicit `prompt_cache_breakpoint` on an `input_text` part — an `input_json` part cannot carry
one — ends a reusable prefix of at least 1,024 tokens, and `usage.input_tokens_details` reports
`cached_tokens` and `cache_write_tokens` as parts of `input_tokens`. `gpt-6-luna` is a general model,
but the endpoint is the typed service ADR 0036 expected behind the seam and the one its rule against
general-purpose LLMs leaves open: it answers only the alternatives supplied, with a probability for
each, and generates no text.

A probe on 2026-11-01 sent three requests shaped as decided below, with the rubric's question names
and option values. Every answer named its question, gave a probability for every option exactly
once in the order sent, summing to 1, picked the most probable option, and reported a confidence
between 0 and 1; each took 0.8–1.8 s. Of two requests sharing a 2,759-token first part, the first
wrote those tokens to the cache and the second read all of them, while neither wrote the part after
the breakpoint. The token rate limit fell by the whole input count even for the cached request.

Every request of an Evidence map carries the same requirements, exclusions and Change points and
differs only in its `item` (`EvidenceRubric.State`). One full map of Concurrency Hunter Run
`e08c4f` was 637 requests and 12.2 million input tokens, about 19,000 a request. Uncached on OpenAI
that map costs about USD 1.22, against about USD 0.51 on Jev.

Two things stood in the way of caching it:

- `AdviceRequest(State, Questions)` hands an Advisor one opaque object. To put a breakpoint after
  the shared part, an adapter would have to know that the rubric keeps the per-request part under
  `item`, a name the seam never promised.
- `AdviceUsage` holds only input and output tokens, and ADR 0046 prices every input token at
  `InputUsdPerMillion`. A cached read would be charged ten times its price and a write 20 % under it.

## Decision

- **The request names its shared part.** `AdviceRequest` carries the state every request of a map
  shares — requirements, exclusions and Change points — apart from the state this request adds, the
  item. The core builds both; each Advisor decides what the split means on its wire. systemone
  services merge them into one `state` object, shared properties first, so the bodies Jev and
  Microsoft-Decision-1 receive are byte for byte what they were, and neither caches. OpenAI sends
  them as two `input_text` parts of one user message, compact JSON each, the first ending in an
  explicit breakpoint, with `prompt_cache_options.mode` set to `explicit` so the per-request part is
  never written to the cache. No `prompt_cache_key`: OpenAI routes GPT-5.6 and later models to
  their cache without one.
- **Usage and price know the cache.** `AdviceUsage` gains cached-input and cache-write token counts,
  parts of the input count and null when not reported, and the `advisor.usage` telemetry record
  carries them. Every Advisor publishes three input rates — uncached, cache read and cache write —
  and a systemone service sets all three to its one rate, so Jev's and Microsoft's costs do not
  change. A request's reported cost is its uncached tokens at the first rate, its cached tokens at
  the second and its written tokens at the third.
- **The estimate stays at the uncached rate.** ADR 0046's estimate, made before anything is sent,
  still prices every measured token at the uncached rate, because nothing can tell in advance what
  will be cached. On a cold cache it can fall short by a quarter of the written share; on a warm
  one it can be ten times the bill. The budget is a guard against runaway maps, not an invoice.
- **OpenAI's Decisions API is the third Advisor**, `openai-decisions`, configured by the secret
  `PLANFORGE_OPENAI_API_KEY` with the optional `PLANFORGE_OPENAI_MODEL`, which defaults to
  `gpt-6-luna`, and sent only to the fixed `https://api.openai.com/v1/decisions`. `OPENAI_API_KEY`
  never configures it (ADR 0036). It speaks its own protocol, so the HTTP transport — the client,
  the deadline, the retries, cancellation and the exception mapping — moves out of the systemone
  helper into a protocol-independent base that both protocols extend. A 429 whose body names an
  exhausted quota or a spend limit is `Forbidden` and stops the map; any other 429 is rate limiting
  and is retried. There is no pacing: OpenAI limits requests and tokens per usage tier, cached
  tokens count against the token limit, and a real map is held back by the tokens a minute, which
  pacing cannot raise (measured; see Consequences).

## Considered and rejected

- **No caching.** The smallest change, at about 2.4 times Jev's price per token, and it would teach
  nothing about what the cache saves.
- **A breakpoint inside the OpenAI adapter**, splitting `State` before its `item` property. No
  contract change, but the adapter would depend on a rubric property name the seam does not promise,
  and usage would still have nowhere to put cached tokens.
- **Estimating at the cache-write rate.** Safe on a cold cache, but 12.5 times the bill on a warm
  one, so the budget would stop maps that cost cents.
- **A failure kind for an exhausted quota.** A more precise message, but the core's messages carry
  no service content anyway, and `Forbidden` already stops the map without caching anything.

## Consequences

- Judgment cache keys do not change: they already hash the shared state and the item apart
  (ADR 0046).
- OpenAI's cache helps only once the shared part reaches 1,024 tokens; a small plan pays the uncached
  rate throughout.
- Whether concurrent cold requests each write the shared part is undocumented. The mapper does not
  warm the cache with a first request on its own; at most eight writes per map cost cents.
- OpenAI's schema does not promise that `probabilities` cover every option or sum to one; the probe
  found they do. The core's answer check (ADR 0036) still decides, and an answer that fails it is
  `invalid_answer`.
- The sanitized fixture's shared part is far below 1,024 tokens, so its live evaluation proves the
  wire format and not the cache. A second live test sends two requests through the adapter with a
  larger shared part and expects the second to read it from the cache.
- OpenAI keeps API content for abuse monitoring for up to 30 days and does not train on it by
  default; setting the key is the consent ADR 0036 describes.
- Measured on 2026-11-01 by replaying three real Concurrency Hunter maps of 2,589, 4,138 and 5,876
  judgments at the Build usage tier (2M tokens a minute). Every map completed: the two smaller in one
  call each, the largest in three, whose 76 failed requests were all transient — 329 attempts refused
  with `rate_limit_exceeded` and 52 answered 503. Its 13.25M input tokens cannot pass a 2M-a-minute
  limit in less than 6.6 minutes, and the map took 6.7. Pacing would turn those refusals into waiting
  without shortening the map, so there is none; a higher tier removes them.
- The shared part is small, about 4,000 tokens; most of a request is the candidate plan text inside
  the questions, which the API keeps outside the cacheable prefix. On those maps the cache saved 14–24%
  of the price, not most of it. The three cost USD 1.92 against Jev's 1.72 and took about five times
  as long.
