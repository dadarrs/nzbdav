import { useCallback, useEffect, useRef, useState } from "react";
import { Badge } from "react-bootstrap";
import type { ArrLink, HealthCheckHistoryResponse, HealthCheckResult } from "~/clients/backend-client.server";
import { Pagination } from "~/routes/queue/components/pagination/pagination";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import styles from "./repair-details.module.css";

const pageSize = 20;

// mirrors RepairAction in backend-client.server.ts, which is server-only
// and can't be imported as a value into client components
const repairedAction = 1;

export type RepairDetailsProps = {
    // RepairAction.Repaired or RepairAction.Deleted
    filter: number,
    windowDays: number,
    onClose: () => void,
}

export function RepairDetails({ filter, windowDays, onClose }: RepairDetailsProps) {
    const [items, setItems] = useState<HealthCheckResult[] | null>(null);
    const [arrLinks, setArrLinks] = useState<Record<string, ArrLink>>({});
    const [totalCount, setTotalCount] = useState(0);
    const [page, setPage] = useState(1);

    const title = filter === repairedAction ? "Repaired" : "Deleted";
    const windowLabel = windowDays >= 365 ? "year" : `${windowDays} days`;
    const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

    // drop out-of-order responses when the filter or page flips quickly
    const fetchSeq = useRef(0);
    const fetchPage = useCallback(async (filterToLoad: number, windowToLoad: number, pageToLoad: number) => {
        const seq = ++fetchSeq.current;
        try {
            const params = new URLSearchParams({
                page: String(pageToLoad),
                pageSize: String(pageSize),
                repairStatus: String(filterToLoad),
                windowDays: String(windowToLoad),
            });
            const response = await fetch(`/api/get-health-check-history?${params}`);
            if (!response.ok) return;
            const body: HealthCheckHistoryResponse = await response.json();
            if (seq !== fetchSeq.current) return;
            setItems(body.items);
            setArrLinks(body.arrLinks ?? {});
            setTotalCount(body.totalCount);
        } catch {
            // transient fetch errors just leave the previous page on screen
        }
    }, []);

    useEffect(() => {
        setPage(1);
        setItems(null);
        fetchPage(filter, windowDays, 1);
    }, [filter, windowDays, fetchPage]);

    const onPageSelected = useCallback((newPage: number) => {
        setPage(newPage);
        fetchPage(filter, windowDays, newPage);
    }, [filter, windowDays, fetchPage]);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>{title}</h3>
                    <div className={styles.sub}>
                        {items === null
                            ? "Loading…"
                            : `${totalCount} file${totalCount === 1 ? "" : "s"} · last ${windowLabel}`}
                    </div>
                </div>
                <button
                    type="button"
                    className={styles.closeButton}
                    onClick={onClose}
                    aria-label={`Close ${title.toLowerCase()} list`}
                >
                    ×
                </button>
            </div>

            {items !== null && items.length === 0 && (
                <div className={styles.empty}>
                    No files were {title.toLowerCase()} in the last {windowLabel}.
                </div>
            )}

            {items !== null && items.length > 0 && (
                <div className={styles.list}>
                    {items.map(item => (
                        <RepairRow key={item.id} item={item} arrLink={arrLinks[item.id]} />
                    ))}
                </div>
            )}

            {items !== null && totalPages > 1 && (
                <div className={styles.footer}>
                    <Pagination
                        pageNumber={page}
                        totalPages={totalPages}
                        onPageSelected={onPageSelected}
                    />
                </div>
            )}
        </div>
    );
}

function RepairRow({ item, arrLink }: { item: HealthCheckResult, arrLink?: ArrLink }) {
    return (
        <div className={styles.row}>
            <div className={styles.rowMain}>
                <div className={styles.nameBlock}>
                    <div className={styles.name}><Truncate>{baseName(item.path)}</Truncate></div>
                    <div className={styles.path}><Truncate>{item.path}</Truncate></div>
                    {item.message && <div className={styles.message}><Truncate>{item.message}</Truncate></div>}
                </div>
                <div className={styles.rowSide}>
                    <Badge bg="info" className={styles.dateBadge}>{formatDate(item.createdAt)}</Badge>
                    {arrLink && (
                        <a
                            className={styles.arrLink}
                            href={arrLink.url}
                            target="_blank"
                            rel="noreferrer"
                            title={arrLink.title
                                ? `Open "${arrLink.title}" in ${arrLink.kind === "radarr" ? "Radarr" : "Sonarr"}`
                                : `Open in ${arrLink.kind === "radarr" ? "Radarr" : "Sonarr"}`}
                        >
                            {arrLink.kind === "radarr" ? "Radarr" : "Sonarr"} ↗
                        </a>
                    )}
                </div>
            </div>
        </div>
    );
}

function baseName(path: string): string {
    const parts = path.split("/");
    return parts[parts.length - 1] || path;
}

function formatDate(dateString: string | null) {
    try {
        if (!dateString) return "Unknown";
        const now = new Date();
        const datetime = new Date(dateString);
        const isSameDate = datetime.getFullYear() === now.getFullYear()
            && datetime.getMonth() === now.getMonth()
            && datetime.getDate() === now.getDate();
        return isSameDate
            ? datetime.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
            : datetime.toLocaleDateString();
    } catch {
        return "Unknown";
    }
}
