---
name: mockifyr-change
description: Run a change to Mockifyr through its delivery cycle — a roadmap item, a defect, a parity gap. Enforces the SEQUENCE and adds the three steps the repo's own loop does not have; every rule stays in CLAUDE.md and docs/testing.md.
---

# mockifyr-change — the sequence, not a second rule book

This repository already has a development loop (`CLAUDE.md` §3), a testing contract (§3a and
`docs/testing.md`) and a documentation contract (§3b). **They are the rules. This skill does
not restate them** — a skill that repeats a rule becomes a second source of truth waiting to
disagree with the first.

What follows is the SEQUENCE, plus the three steps the loop does not have. Everything else is
a pointer.

## Read first

1. `CLAUDE.md` §3 (the loop), §3a (tests), §3b (docs) — the rules
2. `docs/parity/` for the area you are touching — this is the repository's MEMORY; a surprise
   that is already recorded there costs you nothing to learn twice
3. `docs/decisions/` — if the behaviour you are about to add looks missing, check whether its
   absence was deliberate

## The sequence

**1 — What kind of change is this?**

*A roadmap item:* `CLAUDE.md` §3 owns it, step by step. Follow it.

*A defect:* the loop is roadmap-shaped and does not cover this, so: **prove the cause before
touching code.** Evidence, not a hypothesis — the failing request, the diff against the oracle,
the actual bytes. If two explanations fit, rule one out in writing. A defect fixed from the
first plausible explanation is a defect that comes back wearing different clothes.

**2 — The failing test, and PROOF that it fails**

§3 already says the test comes first and must fail. The half it does not say: **put the fault
back afterwards and confirm the test goes red again.** A test that is green on both sides of a
fix advertises a guarantee it does not provide. With a differential harness this is cheap —
revert the implementation, re-run, watch the diff reappear.

**3 — Implement minimally, then §3a in full**

Layers, mutation on new pure logic, the edge-case checklist, all suites green. `docs/testing.md`
is the contract; do not summarise it here.

**4 — Feed the memory**

§3's step 5. `docs/parity/` is where a surprise becomes a durable fact, and the register in
`deferred-edges.md` is where a gap becomes visible instead of forgotten.

**5 — Watch what you did not intend to change**

The loop does not ask this and it should: does an existing test encode the behaviour you just
changed — and is that test now asserting the old contract? Move it deliberately and say so.
Did a field's meaning change while its name did not? Does removing a path leave a stale flag,
screen or doc page behind? §3b lists the documents; this step is the judgement §3b cannot make.

**6 — §3b, then stop for approval**

Documentation in the same branch, including the website repo when the change is user-facing.
Then the summary and the stop, exactly as §3 ends.

## The hook

`.claude/hooks/stop-gate.sh` will not let a turn end on a red build or a red repository gate.
It builds only the projects whose files changed, because a full solution build on every turn
end is slow enough that the hook gets deleted — and a deleted gate is worse than an absent one.
