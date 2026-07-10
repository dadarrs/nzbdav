import { ActionButton } from "../action-button/action-button"
import { useCallback, useEffect, useState } from "react"
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal"
import { Link } from "react-router"
import { type TriCheckboxState } from "../tri-checkbox/tri-checkbox"
import type { PresentationHistorySlot } from "../../route"
import { getLeafDirectoryName } from "~/utils/path"
import { PageRow, PageTable } from "../page-table/page-table"
import styles from "../../route.module.css"
import { PageSection } from "../page-section/page-section"
import { Pagination } from "../pagination/pagination"
import { DropdownOptions } from "~/routes/explore/dropdown-options/dropdown-options"
import { ExportNzb, Remove } from "~/routes/explore/item-menu/item-menu"

export type HistoryTableProps = {
    historySlots: PresentationHistorySlot[],
    totalHistoryCount: number,
    pageNumber: number,
    totalPages: number,
    isLive: boolean,
    onPageSelected: (page: number) => void,
    onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void,
    onRemoved: (nzo_ids: Set<string>) => void,
    onRemovedAll: () => void,
}

export function HistoryTable({ historySlots, totalHistoryCount, pageNumber, totalPages, isLive, onPageSelected, onIsSelectedChanged, onIsRemovingChanged, onRemoved, onRemovedAll }: HistoryTableProps) {
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [selectAllAcrossPages, setSelectAllAcrossPages] = useState(false);
    var selectedCount = historySlots.filter(x => !!x.isSelected).length;
    var headerCheckboxState: TriCheckboxState = selectedCount === 0 ? 'none' : selectedCount === historySlots.length ? 'all' : 'some';

    // gmail-style escalation: only offered while every row on this page is selected,
    // and automatically dropped the moment that stops being true (deselection,
    // page navigation, live inserts/removals, ...)
    const allOnPageSelected = historySlots.length > 0 && selectedCount === historySlots.length;
    useEffect(() => {
        if (!allOnPageSelected) setSelectAllAcrossPages(false);
    }, [allOnPageSelected]);

    const onSelectAll = useCallback((isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>(historySlots.map(x => x.nzo_id)), isSelected);
    }, [historySlots, onIsSelectedChanged]);

    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        setIsConfirmingRemoval(false);

        // "select all across pages" -- delete the entire history server-side
        if (selectAllAcrossPages) {
            const pageIds = new Set<string>(historySlots.map(x => x.nzo_id));
            onIsRemovingChanged(pageIds, true);
            try {
                const url = `/api?mode=history&name=delete&del_all=1&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
                const response = await fetch(url, { method: 'POST' });
                if (response.ok) {
                    const data = await response.json();
                    if (data.status === true) {
                        // clear optimistically; the "*" websocket broadcast is idempotent
                        setSelectAllAcrossPages(false);
                        onRemovedAll();
                        return;
                    }
                }
            } catch { }
            onIsRemovingChanged(pageIds, false);
            return;
        }

        var nzo_ids = new Set<string>(historySlots.filter(x => !!x.isSelected).map(x => x.nzo_id));
        onIsRemovingChanged(nzo_ids, true);
        try {
            const url = `/api?mode=history&name=delete&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json;charset=UTF-8',
                },
                body: JSON.stringify({ nzo_ids: Array.from(nzo_ids) }),
            });
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(nzo_ids);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(nzo_ids, false);
    }, [historySlots, selectAllAcrossPages, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved, onRemovedAll]);

    var sectionTitle = (
        <div className={styles.sectionTitle}>
            <h3>History</h3>
            {headerCheckboxState !== 'none' && <>
                <span className={styles.selectedCount}>
                    {selectAllAcrossPages
                        ? `All ${totalHistoryCount} selected`
                        : `${selectedCount} selected`}
                </span>
                <ActionButton type="delete" onClick={onRemove} />
            </>}
        </div>
    );

    const selectAllBanner = allOnPageSelected && totalHistoryCount > historySlots.length && (
        <div className={styles.selectAllBanner}>
            {selectAllAcrossPages ? <>
                <span>All {totalHistoryCount} items in history are selected.</span>
                <button
                    type="button"
                    className={styles.selectAllLink}
                    onClick={() => setSelectAllAcrossPages(false)}>
                    Clear selection
                </button>
            </> : <>
                <span>All {selectedCount} on this page are selected.</span>
                <button
                    type="button"
                    className={styles.selectAllLink}
                    onClick={() => setSelectAllAcrossPages(true)}>
                    Select all {totalHistoryCount} in history
                </button>
            </>}
        </div>
    );

    const footer = totalPages > 1 ? (
        <div className={styles.tableFooter}>
            {!isLive && <span className={styles.pausedNote}>Live updates pause on older pages. Go to page 1 for live.</span>}
            <Pagination pageNumber={pageNumber} totalPages={totalPages} onPageSelected={onPageSelected} />
        </div>
    ) : undefined;

    return (
        <PageSection title={sectionTitle}>
            {selectAllBanner}
            <PageTable headerCheckboxState={headerCheckboxState} onHeaderCheckboxChange={onSelectAll} footer={footer}>
                {historySlots.map(slot =>
                    <HistoryRow
                        key={slot.nzo_id}
                        slot={slot}
                        onIsSelectedChanged={(id, isSelected) => onIsSelectedChanged(new Set<string>([id]), isSelected)}
                        onIsRemovingChanged={(id, isRemoving) => onIsRemovingChanged(new Set<string>([id]), isRemoving)}
                        onRemoved={(id) => onRemoved(new Set([id]))}
                    />
                )}
            </PageTable>

            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={selectAllAcrossPages
                    ? `ALL ${totalHistoryCount} history items will be removed`
                    : `${selectedCount} item(s) will be removed`}
                checkboxMessage="Delete mounted files"
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </PageSection>
    );
}


type HistoryRowProps = {
    slot: PresentationHistorySlot,
    onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void,
    onRemoved: (nzo_id: string) => void
}

export function HistoryRow({ slot, onIsSelectedChanged, onIsRemovingChanged, onRemoved }: HistoryRowProps) {
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);

    // events
    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(slot.nzo_id, true);
        try {
            const url = '/api?mode=history&name=delete'
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(slot.nzo_id);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    // view
    return (
        <>
            <PageRow
                isSelected={!!slot.isSelected}
                isRemoving={!!slot.isRemoving}
                name={slot.name}
                category={slot.category}
                status={slot.status}
                error={slot.fail_message}
                fileSizeBytes={slot.bytes}
                actions={<Actions slot={slot} onRemove={onRemove} />}
                onRowSelectionChanged={isSelected => onIsSelectedChanged(slot.nzo_id, isSelected)}
            />
            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={slot.nzb_name}
                checkboxMessage={!slot.fail_message ? "Delete mounted files" : undefined}
                errorMessage={slot.fail_message}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </>
    )
}

export function Actions({ slot, onRemove }: { slot: PresentationHistorySlot, onRemove: () => void }) {
    const [isMenuOpen, setIsMenuOpen] = useState(false);

    // determine explore action link url
    var downloadFolder = slot.storage && getLeafDirectoryName(slot.storage);
    const encodedCategory = downloadFolder && encodeURIComponent(slot.category);
    const encodedDownloadFolder = downloadFolder && encodeURIComponent(downloadFolder);
    var folderLink = downloadFolder && `/explore/content/${encodedCategory}/${encodedDownloadFolder}`;

    // determine nzb download URL
    var nzbDownloadUrl = slot.nzb_blob_id
        ? `/api/download-nzb?nzbBlobId=${slot.nzb_blob_id}`
        : null;

    // determine whether explore action should be disabled
    var isFolderDisabled = !downloadFolder || !!slot.isRemoving || !!slot.fail_message;

    const onMenuClick = useCallback((e: React.MouseEvent) => {
        e.stopPropagation();
        setIsMenuOpen(x => !x);
    }, []);

    const onRemoveSelected = useCallback(() => {
        setIsMenuOpen(false);
        onRemove?.();
    }, [onRemove]);

    return (
        <>
            {!isFolderDisabled &&
                <Link to={folderLink} >
                    <ActionButton type="explore" />
                </Link>
            }
            {isFolderDisabled &&
                <ActionButton type="explore" disabled />
            }
            <div style={{ position: "relative" }}>
                <ActionButton
                    type="menu"
                    disabled={!!slot.isRemoving}
                    selected={isMenuOpen}
                    onClick={onMenuClick} />
                <DropdownOptions
                    style={{ marginTop: "5px" }}
                    isOpen={isMenuOpen}
                    onClose={() => setIsMenuOpen(false)}
                    options={[
                        !!nzbDownloadUrl ? { option: <ExportNzb />, linkTo: nzbDownloadUrl } : undefined,
                        { option: <Remove />, onSelect: onRemoveSelected, variant: "danger" },
                    ]} />
            </div>
        </>
    );
}