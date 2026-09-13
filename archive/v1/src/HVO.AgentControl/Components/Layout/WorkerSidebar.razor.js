const collapseKey = "hvo.agentcontrol.sidebar-collapsed";

export function readCollapsed() {
  try {
    const saved = localStorage.getItem(collapseKey);
    return saved === null ? matchMedia("(max-width: 850px)").matches : saved === "true";
  } catch {
    return false;
  }
}

export function writeCollapsed(value) {
  try {
    localStorage.setItem(collapseKey, value ? "true" : "false");
  } catch {
    // Storage can be blocked; the in-memory toggle still works.
  }
}
