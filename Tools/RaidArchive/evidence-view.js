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
  const effects = Object.values(f.effects || {}).some((n) => n > 0);
  const facts = Object.entries(f.observationFacts || {}).some(
    ([k, n]) =>
      n > 0 &&
      !["eligible", "requested", "accepted", "refused", "released"].includes(k),
  );
  if (effects || facts) return "Effect / observation recorded";
  if (f.events) return "Evaluated; execution unconfirmed";
  return "No recorded opportunity";
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
