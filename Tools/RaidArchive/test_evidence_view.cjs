const { strict: assert } = require("node:assert");
const { coverageSummary, lifecycleValue, lifecycleSummary } = require("./evidence-view.js");
assert.equal(lifecycleSummary({ events: 3 }), "No distinct attempt ID");
assert.equal(lifecycleSummary({ events: 0 }), "No events");
assert.equal(lifecycleSummary({ counting: "draw", identifiedRaids: 1 }), "Not applicable to draws");
assert.equal(lifecycleSummary({ identifiedRaids: 1 }), "0 / 0");
assert.equal(lifecycleSummary({ id: "A01", started: 2, identifiedRaids: 1 }), "2 / -");
assert.equal(
  coverageSummary({ disabledRaids: 4 }, 4),
  "Disabled in recorded config",
);
assert.equal(
  coverageSummary({ disabledRaids: 0 }, 0),
  "No feature trace recorded",
);
assert.equal(
  coverageSummary({ events: 10, observationFacts: { "Speed cap applied": 9 } }),
  "Effect / observation recorded",
);
assert.equal(
  coverageSummary({ events: 10, observationFacts: { accepted: 9 } }),
  "Command recorded; execution unconfirmed",
);
assert.equal(
  lifecycleValue({ counting: "trace", coverage: "events-only" }, "started"),
  null,
);
assert.equal(
  lifecycleValue(
    { counting: "trace", identifiedRaids: 2, started: 3 },
    "started",
  ),
  3,
);
assert.equal(
  lifecycleValue(
    { id: "A01", counting: "choice", identifiedRaids: 2 },
    "completed",
  ),
  null,
);
assert.equal(
  coverageSummary({
    id: "S01",
    effects: { "Position reached": 1, "Angle held": 1 },
    completed: 0,
  }),
  "Angle reached and held",
);
assert.equal(
  coverageSummary({
    id: "S03",
    effects: { "Position reached": 1 },
    completed: 0,
  }),
  "Position reached",
);
assert.equal(
  coverageSummary({
    id: "S03",
    effects: {
      "Position reached": 1,
      "Angle held": 1,
      "Enemy engaged from new angle": 1,
    },
    completed: 1,
  }),
  "Enemy engaged from new angle",
);
assert.equal(
  coverageSummary({ id: "S03", events: 20, started: 1 }),
  "Execution observed",
);
for (const [data, label] of [
  [{ id: "T03", events: 10 }, "Journal recorded"],
  [{ id: "A04", events: 2, randomTrials: 1 }, "Random draw recorded"],
  [{ id: "P10", events: 10 }, "Decision context recorded"],
  [{ id: "U01", events: 10 }, "Configuration recorded"],
  [{ id: "D05", events: 1, observationFacts: { refused: 1 } }, "Refusal recorded"],
  [{ id: "M06", events: 1 }, "Evaluation recorded"],
  [{ id: "A11" }, "No separate trace"],
  [{ id: "D02" }, "No door follow-up recorded"],
  [{ id: "G08" }, "No gas exposure recorded"],
  [{ id: "C10" }, "No safety intervention recorded"],
  [{ events: 2, observationFacts: { interrupted: 1 } }, "Evaluation recorded"],
]) assert.equal(coverageSummary(data), label);
console.log("Evidence view regressions passed");
