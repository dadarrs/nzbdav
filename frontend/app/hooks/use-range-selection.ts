import { useCallback, useEffect, useRef } from "react";
import { selectionRange } from "../utils/range-selection";

export function useRangeSelection(
    ids: readonly string[],
    scope: string | number,
    onChange: (ids: Set<string>, selected: boolean) => void,
) {
    const anchor = useRef<{ id: string; scope: string | number } | null>(null);
    const resetAnchor = useCallback(() => { anchor.current = null; }, []);

    useEffect(() => {
        if (anchor.current && (anchor.current.scope !== scope || !ids.includes(anchor.current.id)))
            resetAnchor();
    }, [ids, scope, resetAnchor]);

    const onRowSelectionChanged = useCallback((id: string, selected: boolean, range = false) => {
        const previous = anchor.current?.scope === scope ? anchor.current.id : null;
        const selectedIds = selectionRange(ids, previous, id, range);
        if (selectedIds.size === 0) return;
        anchor.current = { id, scope };
        onChange(selectedIds, selected);
    }, [ids, scope, onChange]);

    return { onRowSelectionChanged, resetAnchor };
}
