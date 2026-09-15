// Minimal organization overview.
//
// Reads the authoritative SQLite-backed organization through the
// owner-protected same-origin /api/organization endpoint and renders a compact
// summary. The request is only made once the control runtime reports ready or
// degraded, because that is when the authoritative store is open. The panel
// stays hidden when the runtime is disabled or the request fails; nothing is
// fabricated client-side, and no request is issued that would log a spurious
// console error on the disabled baseline.
const portal = document.querySelector("[data-portal]");
const root = document.querySelector("[data-org-overview]");

if (root && portal) {
    const nameField = root.querySelector("[data-org-name]");
    const slugField = root.querySelector("[data-org-slug]");
    const departmentsField = root.querySelector("[data-org-departments]");
    const employeesField = root.querySelector("[data-org-employees]");
    const auditField = root.querySelector("[data-org-audit]");
    const orientationField = root.querySelector("[data-org-orientation]");
    const deliverButton = root.querySelector("[data-orientation-deliver]");
    const holdButton = root.querySelector("[data-orientation-hold]");
    const comprehensionButton = root.querySelector("[data-orientation-comprehension]");
    const orientationReceipt = root.querySelector("[data-orientation-receipt]");

    const text = (value) => (value === null || value === undefined ? "\u2014" : String(value));

    const appendRow = (list, term, value) => {
        const dt = document.createElement("dt");
        dt.textContent = term;
        const dd = document.createElement("dd");
        dd.textContent = value;
        list.append(dt, dd);
    };

    let loading = false;
    let loaded = false;

    const load = () => {
        const state = portal.dataset.runtimeState;
        if (loaded || loading || (state !== "ready" && state !== "degraded")) {
            return;
        }

        loading = true;
        fetch("/api/organization", {
            headers: { Accept: "application/json" },
            credentials: "same-origin",
        })
            .then((response) => (response.ok ? response.json() : null))
            .then((data) => {
                if (!data) {
                    return;
                }

                nameField.textContent = text(data.displayName);
                slugField.textContent = text(data.slug);
                root.title = `${text(data.description)} ${text(data.basicInstructions)}`;

                departmentsField.replaceChildren();
                for (const department of data.departments || []) {
                    appendRow(
                        departmentsField,
                        department.displayName,
                        `${department.employeeCount} employee${department.employeeCount === 1 ? "" : "s"}`,
                    );
                }

                employeesField.replaceChildren();
                orientationField.replaceChildren();
                for (const employee of data.employees || []) {
                    appendRow(
                        employeesField,
                        employee.displayName,
                        `${employee.departmentDisplayName} \u00b7 ${employee.roleDisplayName} \u00b7 ${employee.placement}`,
                    );
                    if (employee.orientation) {
                        appendRow(orientationField, "Version", employee.orientation.orientationVersion);
                        appendRow(orientationField, "State", employee.orientation.state);
                        appendRow(orientationField, "Dispatch", employee.orientation.dispatchHeld ? `Held: ${employee.orientation.holdReasons.join(", ")}` : "Ready");
                        appendRow(orientationField, "Runtime load", employee.orientation.restartRequired ? "Restart required" : "Loaded");
                        appendRow(orientationField, "Artifact", `${employee.orientation.artifactFileName} (${employee.orientation.artifactBytes} bytes)`);
                        holdButton.textContent = employee.orientation.holdReasons.includes("manual") ? "Clear manual hold" : "Set manual hold";
                        holdButton.dataset.held = employee.orientation.holdReasons.includes("manual") ? "true" : "false";
                    }
                }

                auditField.replaceChildren();
                for (const audit of data.adoptionAudit || []) {
                    appendRow(
                        auditField,
                        text(audit.authorizationReference),
                        `${audit.source} \u00b7 ${audit.adoptedAt}`,
                    );
                }

                loaded = true;
                root.hidden = false;
            })
            .catch(() => {
                // The overview is optional; a failure leaves the panel hidden.
            })
            .finally(() => {
                loading = false;
            });
    };

    const mutate = async (path, method, body) => {
        orientationReceipt.textContent = "Working...";
        const response = await fetch(path, {
            method,
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        if (response.ok) {
            const result = await response.clone().json().catch(() => null);
            orientationReceipt.textContent = result?.restartRequired
                ? "Orientation delivered. Runtime restart required."
                : "Orientation state updated.";
        } else {
            orientationReceipt.textContent = `Update failed (${response.status}).`;
        }
        if (response.ok) {
            loaded = false;
            loading = false;
            load();
        }
    };
    deliverButton?.addEventListener("click", () => mutate("/api/orientation/deliver", "POST", {}));
    comprehensionButton?.addEventListener("click", () => mutate("/api/orientation/comprehension/run", "POST", {}));
    holdButton?.addEventListener("click", () => mutate("/api/orientation/manual-hold", "PUT", {
        held: holdButton.dataset.held !== "true",
        detail: "Owner portal action",
    }));

    load();
    new MutationObserver(load).observe(portal, {
        attributes: true,
        attributeFilter: ["data-runtime-state"],
    });
}
