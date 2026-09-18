export async function fetchJson(path, options = {}) {
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...options });
    let body = null;
    try { body = await response.json(); } catch { body = null; }
    if (!response.ok) {
        const error = new Error(body?.detail || body?.title || `HTTP ${response.status}`);
        error.status = response.status;
        throw error;
    }
    return body;
}

export function rows(target, values) {
    target.replaceChildren();
    for (const [term, value] of values) {
        const dt = document.createElement('dt'); dt.textContent = term;
        const dd = document.createElement('dd'); dd.textContent = value === null || value === undefined || value === '' ? '—' : String(value);
        target.append(dt, dd);
    }
}

export const label = (value) => String(value || 'unknown').replaceAll('-', ' ');

export function availabilityBadge(target, category) {
    const badge = document.createElement('span');
    badge.className = 'availability-pill';
    badge.dataset.availability = category || 'unknown';
    badge.textContent = label(category);
    target.append(badge);
    return badge;
}

export function availabilitySummary(counts) {
    const active = (counts || []).filter((item) => item.count > 0);
    return active.length
        ? active.map((item) => `${label(item.category)} ${item.count}`).join(' · ')
        : 'No employees';
}
