import styles from "./stream-history.module.css";
import { useCallback, useEffect, useState } from "react";
import { Pagination } from "~/routes/queue/components/pagination/pagination";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { formatBytes } from "../../utils/format";

const PAGE_SIZE = 10;
const REFRESH_INTERVAL_MS = 30_000;

type ReadSessionItem = {
    id: string,
    path: string,
    startedAt: number,
    endedAt: number,
    durationMs: number,
    fileSize: number | null,
    bytesServed: number,
    failoverSaves: number,
    clientIp: string | null,
    clientUserAgent: string | null,
    endReason: number,
};

type ReadSessionsResponse = {
    status: boolean,
    page: number,
    pageSize: number,
    totalCount: number,
    sessions: ReadSessionItem[],
};

const END_REASONS: Record<number, { label: string, style: string }> = {
    0: { label: "completed", style: styles.reasonOk },
    1: { label: "aborted", style: styles.reasonNeutral },
    2: { label: "timeout", style: styles.reasonWarn },
    3: { label: "error", style: styles.reasonBad },
};

export function StreamHistory() {
    const [page, setPage] = useState(1);
    const [searchInput, setSearchInput] = useState("");
    const [search, setSearch] = useState("");
    const [refresh, setRefresh] = useState(0);
    const [data, setData] = useState<ReadSessionsResponse | null>(null);
    const [error, setError] = useState(false);
    const [isConfirmingClear, setIsConfirmingClear] = useState(false);
    const [isClearing, setIsClearing] = useState(false);

    useEffect(() => {
        const timer = setTimeout(() => {
            const next = searchInput.trim();
            if (next !== search) {
                setPage(1);
                setSearch(next);
            }
        }, 250);
        return () => clearTimeout(timer);
    }, [searchInput, search]);

    useEffect(() => {
        let controller: AbortController | undefined;
        const fetchPage = async () => {
            controller?.abort();
            const request = new AbortController();
            controller = request;
            try {
                const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
                if (search) params.set("search", search);
                const response = await fetch(`/api/get-read-sessions?${params}`, { signal: request.signal });
                if (!response.ok) throw new Error("Unable to load history");
                const body: ReadSessionsResponse = await response.json();
                if (!body.status) throw new Error("Unable to load history");
                if (request.signal.aborted) return;
                setData(body);
                setPage(body.page);
                setError(false);
            } catch {
                if (!request.signal.aborted) setError(true);
            }
        };
        setData(null);
        setError(false);
        void fetchPage();
        // new sessions only appear on page 1; refreshing deeper pages would
        // shift rows underneath the reader as new history arrives above.
        const timer = page === 1 ? setInterval(() => {
            if (document.visibilityState === "visible") void fetchPage();
        }, REFRESH_INTERVAL_MS) : undefined;
        return () => {
            controller?.abort();
            clearInterval(timer);
        };
    }, [page, search, refresh]);

    const onConfirmClear = useCallback(async () => {
        setIsConfirmingClear(false);
        setIsClearing(true);
        try {
            const response = await fetch("/api/clear-read-sessions", { method: "POST" });
            if (response.ok) {
                setPage(1);
                setRefresh(value => value + 1);
            }
        } catch {
            // leave current view on transient failure
        } finally {
            setIsClearing(false);
        }
    }, []);

    const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>Stream history</h3>
                    <div className={styles.sub}>
                        {data ? `${data.totalCount} ${search ? "matching" : "completed"} read${data.totalCount === 1 ? "" : "s"}` : error ? "History unavailable" : "Loading…"}
                    </div>
                </div>
                {data !== null && data.totalCount > 0 && (
                    <button
                        type="button"
                        className={styles.clearButton}
                        disabled={isClearing}
                        onClick={() => setIsConfirmingClear(true)}
                    >
                        {isClearing ? "Clearing…" : "Clear history"}
                    </button>
                )}
            </div>

            <input
                type="search"
                className={styles.searchInput}
                aria-label="Search stream history by filename or path"
                placeholder="Search history…"
                value={searchInput}
                onChange={event => setSearchInput(event.target.value)}
            />

            <ConfirmModal
                show={isConfirmingClear}
                title="Clear stream history?"
                message={"All stream history entries, including those outside the current search, will be permanently removed from this list. "
                    + "This cannot be undone. Dashboard statistics (bytes read, session counts, "
                    + "read time, All Time totals) are not affected."}
                confirmText="Clear history"
                cancelText="Cancel"
                onConfirm={onConfirmClear}
                onCancel={() => setIsConfirmingClear(false)} />

            {data && data.sessions.length === 0 && (
                <div className={styles.empty}>{search ? "No streams match your search." : "Nothing streamed yet."}</div>
            )}

            {error && <div className={styles.empty} role="status">
                Could not refresh history. <button type="button" onClick={() => setRefresh(value => value + 1)}>Retry</button>
            </div>}

            {data && data.sessions.length > 0 && (
                <div className={styles.list}>
                    {data.sessions.map(session => <SessionRow key={session.id} session={session} />)}
                </div>
            )}

            {data && totalPages > 1 && (
                <div className={styles.footer}>
                    <Pagination
                        pageNumber={page}
                        totalPages={totalPages}
                        onPageSelected={setPage}
                    />
                </div>
            )}
        </div>
    );
}

function SessionRow({ session }: { session: ReadSessionItem }) {
    const reason = END_REASONS[session.endReason] ?? { label: "unknown", style: styles.reasonNeutral };
    const served = formatBytes(session.bytesServed);
    const size = session.fileSize ? ` of ${formatBytes(session.fileSize)}` : "";
    return (
        <div className={styles.row}>
            <div className={styles.rowMain}>
                <span className={styles.fileName} title={session.path}>{baseName(session.path)}</span>
                <span className={`${styles.reason} ${reason.style}`}>{reason.label}</span>
                {session.failoverSaves > 0 && (
                    <span
                        className={styles.failover}
                        title={`${session.failoverSaves} article(s) rescued by a backup provider during this read`}
                    >
                        ⛑ {session.failoverSaves}
                    </span>
                )}
            </div>
            <div className={styles.rowMeta}>
                <span>{timeAgo(session.endedAt)}</span>
                <span>{formatDuration(session.durationMs)}</span>
                <span>{served}{size}</span>
                {session.clientIp && <span title={session.clientUserAgent ?? undefined}>{session.clientIp}</span>}
            </div>
        </div>
    );
}

function baseName(path: string): string {
    const parts = path.split("/");
    return parts[parts.length - 1] || path;
}

function timeAgo(unixMs: number): string {
    const seconds = Math.max(0, (Date.now() - unixMs) / 1000);
    if (seconds < 60) return "just now";
    const minutes = seconds / 60;
    if (minutes < 60) return `${Math.round(minutes)}m ago`;
    const hours = minutes / 60;
    if (hours < 24) return `${Math.round(hours)}h ago`;
    return `${Math.round(hours / 24)}d ago`;
}

function formatDuration(ms: number): string {
    if (ms < 1000) return `${ms}ms`;
    const s = ms / 1000;
    if (s < 60) return `${s.toFixed(0)}s`;
    const m = s / 60;
    if (m < 60) return `${m.toFixed(0)}m ${Math.round(s % 60)}s`;
    return `${Math.floor(m / 60)}h ${Math.round(m % 60)}m`;
}
