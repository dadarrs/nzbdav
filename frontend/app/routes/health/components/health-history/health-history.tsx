import { Table, Badge } from "react-bootstrap";
import type { HealthCheckResult } from "~/clients/backend-client.server";
import styles from "./health-history.module.css";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import { Pagination } from "~/routes/queue/components/pagination/pagination";

export type HealthHistoryProps = {
    items: HealthCheckResult[],
    totalCount: number,
    pageNumber: number,
    totalPages: number,
    onPageSelected: (page: number) => void,
    isSearchActive: boolean,
}

const resultBadges: Record<number, { label: string, variant: string }> = {
    0: { label: 'Healthy', variant: 'success' },
    1: { label: 'Unhealthy', variant: 'danger' },
};

const repairBadges: Record<number, { label: string, variant: string }> = {
    0: { label: 'None', variant: 'secondary' },
    1: { label: 'Repaired', variant: 'info' },
    2: { label: 'Deleted', variant: 'danger' },
    3: { label: 'Action Needed', variant: 'warning' },
};

export function HealthHistory({
    items,
    totalCount,
    pageNumber,
    totalPages,
    onPageSelected,
    isSearchActive,
}: HealthHistoryProps) {
    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>History</h3>
                <div className={styles.count}>
                    {totalCount} result{totalCount === 1 ? '' : 's'}
                </div>
            </div>

            {items.length === 0 ? (
                isSearchActive ? (
                    <div className={styles.emptyState}>
                        <div className={styles.emptyIcon}>🔍</div>
                        <div className={styles.emptyTitle}>No Matching Results</div>
                        <div className={styles.emptyDescription}>
                            No health-check results match your search
                        </div>
                    </div>
                ) : (
                    <div className={styles.emptyState}>
                        <div className={styles.emptyIcon}>🩺💙💪</div>
                        <div className={styles.emptyTitle}>No Health Checks Yet</div>
                        <div className={styles.emptyDescription}>
                            Results will appear here once the repair job begins checking your library
                        </div>
                    </div>
                )
            ) : (
                <>
                    <div className={styles.tableContainer}>
                        <Table className={styles.table} responsive>
                            <thead className={styles.desktop}>
                                <tr>
                                    <th>File</th>
                                    <th className={styles.desktop}>Checked</th>
                                    <th className={styles.desktop}>Result</th>
                                    <th className={styles.desktop}>Repair</th>
                                </tr>
                            </thead>
                            <tbody>
                                {items.map(item => (
                                    <tr key={item.id} className={styles.tableRow}>
                                        <td className={styles.nameCell}>
                                            <div className={styles.nameContainer}>
                                                <div className={styles.name}><Truncate>{baseName(item.path)}</Truncate></div>
                                                <div className={styles.path}><Truncate>{item.path}</Truncate></div>
                                                {item.message &&
                                                    <div className={styles.message}><Truncate>{item.message}</Truncate></div>
                                                }
                                                <div className={styles.mobile}>
                                                    <ResultDetailsTable item={item} />
                                                </div>
                                            </div>
                                        </td>
                                        <td className={`${styles.dateCell} ${styles.desktop}`}>
                                            <Badge bg="info" className={styles.statusBadge}>
                                                {formatDate(item.createdAt)}
                                            </Badge>
                                        </td>
                                        <td className={`${styles.dateCell} ${styles.desktop}`}>
                                            <ResultBadge result={item.result} />
                                        </td>
                                        <td className={`${styles.dateCell} ${styles.desktop}`}>
                                            <RepairBadge repairStatus={item.repairStatus} />
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
        </div>
    );
}

function ResultDetailsTable({ item }: { item: HealthCheckResult }) {
    return (
        <div className={styles.detailsTable}>
            <div className={styles.detailsRow}>
                <div className={styles.detailsLabel}>Checked</div>
                <div className={styles.detailsValue}>
                    <Badge bg="info" className={styles.statusBadge}>
                        {formatDate(item.createdAt)}
                    </Badge>
                </div>
            </div>
            <div className={styles.detailsRow}>
                <div className={styles.detailsLabel}>Result</div>
                <div className={styles.detailsValue}>
                    <ResultBadge result={item.result} />
                </div>
            </div>
            <div className={styles.detailsRow}>
                <div className={styles.detailsLabel}>Repair</div>
                <div className={styles.detailsValue}>
                    <RepairBadge repairStatus={item.repairStatus} />
                </div>
            </div>
        </div>
    );
}

function ResultBadge({ result }: { result: number }) {
    const badge = resultBadges[result] ?? { label: 'Unknown', variant: 'secondary' };
    return <Badge bg={badge.variant} className={styles.statusBadge}>{badge.label}</Badge>;
}

function RepairBadge({ repairStatus }: { repairStatus: number }) {
    const badge = repairBadges[repairStatus] ?? { label: 'Unknown', variant: 'secondary' };
    return <Badge bg={badge.variant} className={styles.statusBadge}>{badge.label}</Badge>;
}

function baseName(path: string): string {
    const parts = path.split('/');
    return parts[parts.length - 1] || path;
}

function formatDate(dateString: string | null) {
    try {
        if (!dateString) return 'Unknown';
        const now = new Date();
        const datetime = new Date(dateString);
        return isSameDate(datetime, now)
            ? datetime.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
            : datetime.toLocaleDateString();
    } catch {
        return 'Unknown';
    }
}

function isSameDate(one: Date, two: Date) {
    return (
        one.getFullYear() === two.getFullYear() &&
        one.getMonth() === two.getMonth() &&
        one.getDate() === two.getDate()
    );
}
