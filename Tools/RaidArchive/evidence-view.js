/* Shared by the page and Node regression tests; no DOM or raw HTML input. */
function coverageSummary(f, raids) {
  if (f.status && f.status !== "runtime") return f.status;
  if (f.configEnabled === false || (raids > 0 && f.disabledRaids === raids))
    return "Disabled in recorded config";
  if (["S01", "S03"].includes(f.id || f.feature)) {
    if (f.effects?.["Enemy engaged from new angle"] > 0)
      return "Enemy engaged from new angle";
    if (f.effects?.["Angle held"] > 0) return "Angle reached and held";
    if (f.effects?.["Position reached"] > 0) return "Position reached";
  }
  if (f.completed > 0) return "Completion recorded";
  if (f.started > 0) return "Execution observed";
  const id = f.id || f.feature;
  if (f.randomTrials > 0) return "Random draw recorded";
  const effects = Object.values(f.effects || {}).some((n) => n > 0);
  const facts = Object.entries(f.observationFacts || {}).some(
    ([k, n]) =>
      n > 0 &&
      (k === "started" || k === "completed" || k.includes(" ")),
  );
  if (effects || facts) return "Effect / observation recorded";
  const observations = f.observationFacts || {};
  if (f.unconfirmed > 0 || f.accepted > 0 || observations.accepted > 0)
    return "Command recorded; execution unconfirmed";
  if (f.events && ["T01", "T02", "T03", "T04"].includes(id)) return "Journal recorded";
  if (f.events && ["C14", "U01"].includes(id)) return "Configuration recorded";
  if (f.events && ["A06", "A08", "A09", "A10", "A12", "E03", "E04", "P10"].includes(id))
    return "Decision context recorded";
  if (f.requested > 0 || observations.requested > 0) return "Request recorded";
  if (f.refused > 0 || observations.refused > 0) return "Refusal recorded";
  if (f.events) return "Evaluation recorded";
  if (["A11", "C03"].includes(id)) return "No separate trace";
  if (id === "C10") return "No safety intervention recorded";
  if (id === "G08") return "No gas exposure recorded";
  if (["D02", "D03", "D04", "D05", "D06", "D07", "D08", "D11", "D12", "D13", "D14"].includes(id))
    return "No door follow-up recorded";
  return "No feature trace recorded";
}
function lifecycleValue(f, key) {
  if (
    ["draw"].includes(f.counting) ||
    (key === "completed" && ["A01", "A02"].includes(f.id || f.feature))
  )
    return null;
  if (!f.identifiedRaids && f.coverage !== "identified-attempts") return null;
  return f[key] || 0;
}
if (typeof module !== "undefined")
  module.exports = { coverageSummary, lifecycleValue };
