import { Table, Badge, Form } from "react-bootstrap";
import type { HealthCheckQueueItem } from "~/clients/backend-client.server";
import styles from "./health-table.module.css";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import { StatusBadge } from "~/routes/queue/components/status-badge/status-badge";
import { TriCheckbox, type TriCheckboxState } from "~/routes/queue/components/tri-checkbox/tri-checkbox";
import { Pagination } from "~/routes/queue/components/pagination/pagination";

export type HealthTableProps = {
    isEnabled: boolean,
    healthCheckItems: HealthCheckQueueItem[],
    totalCount: number,
    pageNumber: number,
    totalPages: number,
    onPageSelected: (page: number) => void,
    searchInput: string,
    onSearchInputChanged: (value: string) => void,
    isSearchActive: boolean,
    selectedIds: Set<string>,
    onToggleSelect: (id: string, isSelected: boolean) => void,
    onToggleSelectAll: (isSelected: boolean) => void,
    onCheckNow: (ids: string[]) => void,
    isTriggering: boolean,
}

export function HealthTable({
    isEnabled,
    healthCheckItems,
    totalCount,
    pageNumber,
    totalPages,
    onPageSelected,
    searchInput,
    onSearchInputChanged,
    isSearchActive,
    selectedIds,
    onToggleSelect,
    onToggleSelectAll,
    onCheckNow,
    isTriggering,
}: HealthTableProps) {
    const selectedOnPage = healthCheckItems.filter(x => selectedIds.has(x.id)).length;
    const headerCheckboxState: TriCheckboxState =
        selectedOnPage === 0 ? 'none'
            : selectedOnPage === healthCheckItems.length ? 'all'
                : 'some';

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Schedule</h3>
                <div className={styles.count}>
                    {totalCount} item{totalCount === 1 ? '' : 's'}
                </div>
            </div>

            {!isEnabled ? (
                <div className={styles.emptyState}>
                    <div className={styles.emptyIcon}>🩺💙💪</div>
                    <div className={styles.emptyTitle}>Enable Repairs In Settings</div>
                    <div className={styles.emptyDescription}>
                        Once you enable repairs, all mounted usenet files will be queued for continuous health monitoring
                    </div>
                </div>
            ) : (
                <>
                    <div className={styles.toolbar}>
                        <input
                            type="search"
                            className={styles.searchInput}
                            placeholder="Search library…"
                            value={searchInput}
                            onChange={e => onSearchInputChanged(e.target.value)}
                        />
                        <button
                            type="button"
                            className={styles.checkNowButton}
                            disabled={selectedIds.size === 0 || isTriggering}
                            onClick={() => onCheckNow([...selectedIds])}
                        >
                            {isTriggering ? 'Queueing…' : `Check now (${selectedIds.size})`}
                        </button>
                    </div>

                    {healthCheckItems.length === 0 ? (
                        isSearchActive ? (
                            <div className={styles.emptyState}>
                                <div className={styles.emptyIcon}>🔍</div>
                                <div className={styles.emptyTitle}>No Matching Items</div>
                                <div className={styles.emptyDescription}>
                                    No items in the health-check queue match your search
                                </div>
                            </div>
                        ) : (
                            <div className={styles.emptyState}>
                                <div className={styles.emptyIcon}>🩺💙💪</div>
                                <div className={styles.emptyTitle}>No Items To Health-Check</div>
                                <div className={styles.emptyDescription}>
                                    Once you begin processing nzbs, the mounted usenet files will be queued for continuous health monitoring
                                </div>
                            </div>
                        )
                    ) : (
                        <>
                            <div className={styles.tableContainer}>
                                <Table className={styles.table} responsive>
                                    <thead className={styles.desktop}>
                                        <tr>
                                            <th>
                                                <TriCheckbox state={headerCheckboxState} onChange={onToggleSelectAll}>
                                                    Name
                                                </TriCheckbox>
                                            </th>
                                            <th className={styles.desktop}>Created</th>
                                            <th className={styles.desktop}>Last Check</th>
                                            <th className={styles.desktop}>Next Check</th>
                                            <th className={styles.desktop}></th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {healthCheckItems.map(item => (
                                            <tr key={item.id} className={styles.tableRow}>
                                                <td className={styles.nameCell}>
                                                    <div className={styles.nameRow}>
                                                        <div className={styles.rowCheckbox}>
                                                            <Form.Check
                                                                checked={selectedIds.has(item.id)}
                                                                onChange={e => onToggleSelect(item.id, e.target.checked)}
                                                                aria-label={`Select ${item.name}`}
                                                            />
                                                        </div>
                                                        <div className={styles.nameContainer}>
                                                            <div className={styles.name}><Truncate>{item.name}</Truncate></div>
                                                            <div className={styles.path}><Truncate>{item.path}</Truncate></div>
                                                            <div className={styles.mobile}>
                                                                <DateDetailsTable item={item} />
                                                                <button
                                                                    type="button"
                                                                    className={`${styles.rowAction} ${styles.mobileAction}`}
                                                                    disabled={isTriggering}
                                                                    onClick={() => onCheckNow([item.id])}
                                                                >
                                                                    Check now
                                                                </button>
                                                            </div>
                                                        </div>
                                                    </div>
                                                </td>
                                                <td className={`${styles.dateCell} ${styles.desktop}`}>
                                                    {formatDateBadge(item.releaseDate, 'Unknown', 'info')}
                                                </td>
                                                <td className={`${styles.dateCell} ${styles.desktop}`}>
                                                    {formatDateBadge(item.lastHealthCheck, 'Never', 'warning')}
                                                </td>
                                                <td className={`${styles.dateCell} ${styles.desktop}`}>
                                                    <NextCheckBadge item={item} />
                                                </td>
                                                <td className={`${styles.actionCell} ${styles.desktop}`}>
                                                    <button
                                                        type="button"
                                                        className={styles.rowAction}
                                                        disabled={isTriggering}
                                                        onClick={() => onCheckNow([item.id])}
                                                        title="Move this item to the front of the health-check queue"
                                                    >
                                                        Check now
                                                    </button>
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </Table>
                            </div>
                            {totalPages > 1 &&
                                <div className={styles.footer}>
                                    <Pagination
                                        pageNumber={pageNumber}
                                        totalPages={totalPages}
                                        onPageSelected={onPageSelected}
                                    />
                                </div>
                            }
                        </>
                    )}
                </>
            )}
        </div>
    );
}

function DateDetailsTable({ item }: { item: HealthCheckQueueItem }) {
    return (
        <div className={styles.dateDetailsTable}>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Created</div>
                <div className={styles.dateDetailsValue}>
                    {formatDateBadge(item.releaseDate, 'Unknown', 'info')}
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Last Health Check</div>
                <div className={styles.dateDetailsValue}>
                    {formatDateBadge(item.lastHealthCheck, 'Never', 'warning')}
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Next Health Check</div>
                <div className={styles.dateDetailsValue}>
                    <NextCheckBadge item={item} />
                </div>
            </div>
        </div>
    );
}

function NextCheckBadge({ item }: { item: HealthCheckQueueItem }) {
    if (item.progress > 0)
        return <StatusBadge status="health-checking" className={styles.dateBadge} percentage={item.progress.toString()} />;
    if (!item.nextHealthCheck)
        return <Badge bg="success" className={styles.dateBadge}>ASAP</Badge>;
    if (new Date(item.nextHealthCheck).getTime() <= Date.now())
        return <Badge bg="success" className={styles.dateBadge}>Due now</Badge>;
    return formatDateBadge(item.nextHealthCheck, 'ASAP', 'success');
}

function formatDate(dateString: string | null, fallback: string) {
    try {
        if (!dateString) return fallback;
        const now = new Date();
        const datetime = new Date(dateString);
        return isSameDate(datetime, now)
            ? datetime.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
            : datetime.toLocaleDateString();
    } catch {
        return 'Unknown';
    }
};

function formatDateBadge(dateString: string | null, fallback: string, variant: 'info' | 'warning' | 'success') {
    const dateText = formatDate(dateString, fallback);
    return <Badge bg={variant} className={styles.dateBadge}>{dateText}</Badge>;
};

function isSameDate(one: Date, two: Date) {
    return (
        one.getFullYear() === two.getFullYear() &&
        one.getMonth() === two.getMonth() &&
        one.getDate() === two.getDate()
    );
}
