const toggle = document.querySelector('[data-nav-toggle]');
const nav = document.querySelector('[data-primary-nav]');
if (toggle && nav) {
    toggle.addEventListener('click', () => {
        const open = toggle.getAttribute('aria-expanded') !== 'true';
        toggle.setAttribute('aria-expanded', String(open));
        nav.dataset.open = String(open);
    });
}

const hash = location.hash;
if (hash.startsWith('#employee/')) {
    location.replace(`/employees/${encodeURIComponent(decodeURIComponent(hash.slice(10)))}`);
} else if (!document.querySelector('[data-organization-page]') && /^#(operations|development|qa)$/.test(hash)) {
    // Legacy department hash on any page other than the organization overview.
    // The overview resolves the mutable slug to a stable department id after the
    // authoritative read; this fallback maps the old link to the employee filter.
    location.replace(`/employees?department=${encodeURIComponent(hash.slice(1))}`);
}
