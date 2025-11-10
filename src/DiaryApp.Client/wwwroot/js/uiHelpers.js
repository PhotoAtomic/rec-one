export function scrollIntoView(element) {
    if (!element) {
        return;
    }
    element.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

export function focusElement(element) {
    element?.focus?.();
}
