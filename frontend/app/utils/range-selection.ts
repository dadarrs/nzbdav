export type SelectionModifiers = {
    shiftKey?: boolean;
    ctrlKey?: boolean;
    metaKey?: boolean;
};

export function isRangeSelection(event: SelectionModifiers): boolean {
    return !!(event.shiftKey || event.ctrlKey || event.metaKey);
}

// IDs, rather than indices, keep the anchor attached to its row during live updates.
export function selectionRange(ids: readonly string[], anchor: string | null, target: string, range: boolean): Set<string> {
    const end = ids.indexOf(target);
    if (end === -1) return new Set();
    const start = anchor === null ? -1 : ids.indexOf(anchor);
    if (!range || start === -1) return new Set([target]);
    return new Set(ids.slice(Math.min(start, end), Math.max(start, end) + 1));
}
