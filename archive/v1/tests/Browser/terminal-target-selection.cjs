function selectRuntime(runtimes, explicitId) {
  if (explicitId) return runtimes.find(x => x.id === explicitId) || null;
  return runtimes.find(x => x.transport === 'Connected') || runtimes[0] || null;
}

module.exports = { selectRuntime };
