const transcripts = new WeakMap();

export function observeTranscript(workerId) {
    const element = document.querySelector('.transcript');
    if (!element) return;
    let state = transcripts.get(element);
    if (!state) {
        state = { pinned: true, workerId };
        transcripts.set(element, state);
        element.addEventListener('scroll', () => {
            state.pinned = element.scrollHeight - element.scrollTop - element.clientHeight < 80;
        }, { passive: true });
        new MutationObserver(() => {
            if (state.pinned) element.scrollTop = element.scrollHeight;
        }).observe(element, { childList: true, subtree: true, characterData: true });
        element.scrollTop = element.scrollHeight;
    }
    if (state.workerId !== workerId) {
        state.workerId = workerId;
        state.pinned = true;
        element.scrollTop = element.scrollHeight;
    }
}
