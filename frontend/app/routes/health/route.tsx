import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient, type HealthCheckHistoryResponse, type HealthCheckQueueResponse } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthHistory } from "./components/health-history/health-history";
import { HealthStats } from "./components/health-stats/health-stats";
import { useCallback, useEffect, useRef, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { Alert } from "react-bootstrap";

const queuePageSize = 30;
const historyPageSize = 20;
const searchDebounceMs = 400;

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
}

export async function loader() {
    const enabledKey = 'repair.enable';
    const [queueData, historyData, config] = await Promise.all([
        backendClient.getHealthCheckQueue(queuePageSize),
        backendClient.getHealthCheckHistory(historyPageSize),
        backendClient.getConfig([enabledKey])
    ]);

    return {
        uncheckedCount: queueData.uncheckedCount,
        queueItems: queueData.items,
        queueTotalCount: queueData.totalCount,
        historyStats: historyData.stats,
        historyItems: historyData.items,
        historyTotalCount: historyData.totalCount,
        isEnabled: config
            .filter(x => x.configName === enabledKey)
            .filter(x => x.configValue.toLowerCase() === "true")
            .length > 0
    };
}

export default function Health({ loaderData }: Route.ComponentProps) {
    const { isEnabled } = loaderData;
    const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
    const [queueItems, setQueueItems] = useState(loaderData.queueItems);
    const [queueTotalCount, setQueueTotalCount] = useState(loaderData.queueTotalCount);
    const [uncheckedCount, setUncheckedCount] = useState(loaderData.uncheckedCount);
    const [historyItems, setHistoryItems] = useState(loaderData.historyItems);
    const [historyTotalCount, setHistoryTotalCount] = useState(loaderData.historyTotalCount);

    // pagination, search, and selection state
    const [queuePage, setQueuePage] = useState(1);
    const [historyPage, setHistoryPage] = useState(1);
    const [searchInput, setSearchInput] = useState("");
    const [search, setSearch] = useState("");
    const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
    const [isTriggering, setIsTriggering] = useState(false);
    const [showRepairDisabledWarning, setShowRepairDisabledWarning] = useState(false);
    const [triggerError, setTriggerError] = useState<string | null>(null);

    const queueTotalPages = Math.max(1, Math.ceil(queueTotalCount / queuePageSize));
    const historyTotalPages = Math.max(1, Math.ceil(historyTotalCount / historyPageSize));

    // client-side fetches (the /api proxy injects auth for session users).
    // a sequence guard drops out-of-order responses when pages are flipped quickly.
    const queueFetchSeq = useRef(0);
    const fetchQueuePage = useCallback(async (page: number, searchTerm: string) => {
        const seq = ++queueFetchSeq.current;
        try {
            const params = new URLSearchParams({ page: String(page), pageSize: String(queuePageSize) });
            if (searchTerm) params.set('search', searchTerm);
            const response = await fetch(`/api/get-health-check-queue?${params}`);
            if (!response.ok) return;
            const body: HealthCheckQueueResponse = await response.json();
            if (seq !== queueFetchSeq.current) return;
            setQueueItems(body.items);
            setQueueTotalCount(body.totalCount);
            setUncheckedCount(body.uncheckedCount);
        } catch {
            // transient fetch errors just leave the previous page on screen
        }
    }, []);

    const historyFetchSeq = useRef(0);
    const fetchHistoryPage = useCallback(async (page: number, searchTerm: string) => {
        const seq = ++historyFetchSeq.current;
        try {
            const params = new URLSearchParams({ page: String(page), pageSize: String(historyPageSize) });
            if (searchTerm) params.set('search', searchTerm);
            const response = await fetch(`/api/get-health-check-history?${params}`);
            if (!response.ok) return;
            const body: HealthCheckHistoryResponse = await response.json();
            if (seq !== historyFetchSeq.current) return;
            setHistoryItems(body.items);
            setHistoryTotalCount(body.totalCount);
            setHistoryStats(body.stats);
        } catch {
            // transient fetch errors just leave the previous page on screen
        }
    }, []);

    // debounce the search box; committing a new term resets both tables to page 1
    useEffect(() => {
        const next = searchInput.trim();
        if (next === search) return;
        const timer = setTimeout(() => {
            setSearch(next);
            setQueuePage(1);
            setHistoryPage(1);
            setSelectedIds(new Set());
        }, searchDebounceMs);
        return () => clearTimeout(timer);
    }, [searchInput, search]);

    // refetch on page/search change (initial page 1 with no search comes from the loader)
    const skipInitialQueueFetch = useRef(true);
    useEffect(() => {
        if (skipInitialQueueFetch.current) { skipInitialQueueFetch.current = false; return; }
        fetchQueuePage(queuePage, search);
    }, [queuePage, search, fetchQueuePage]);

    const skipInitialHistoryFetch = useRef(true);
    useEffect(() => {
        if (skipInitialHistoryFetch.current) { skipInitialHistoryFetch.current = false; return; }
        fetchHistoryPage(historyPage, search);
    }, [historyPage, search, fetchHistoryPage]);

    // clamp pages when shrinking result sets leave us beyond the last page
    useEffect(() => {
        if (queuePage > queueTotalPages) {
            setQueuePage(queueTotalPages);
            setSelectedIds(new Set());
        }
    }, [queuePage, queueTotalPages]);

    useEffect(() => {
        if (historyPage > historyTotalPages) setHistoryPage(historyTotalPages);
    }, [historyPage, historyTotalPages]);

    // refill page 1 as websocket events consume the visible queue slice
    useEffect(() => {
        if (queuePage !== 1) return;
        if (queueItems.length >= 15) return;
        if (queueItems.length >= queueTotalCount) return;
        fetchQueuePage(1, search);
    }, [queueItems, queuePage, queueTotalCount, search, fetchQueuePage]);

    // selection
    const onQueuePageSelected = useCallback((page: number) => {
        setQueuePage(page);
        setSelectedIds(new Set());
    }, []);

    const onToggleSelect = useCallback((id: string, isSelected: boolean) => {
        setSelectedIds(prev => {
            const next = new Set(prev);
            if (isSelected) next.add(id);
            else next.delete(id);
            return next;
        });
    }, []);

    const onToggleSelectAll = useCallback((isSelected: boolean) => {
        setSelectedIds(isSelected ? new Set(queueItems.map(x => x.id)) : new Set());
    }, [queueItems]);

    // manual health-check triggering
    const onCheckNow = useCallback(async (ids: string[]) => {
        if (ids.length === 0) return;
        setIsTriggering(true);
        setTriggerError(null);
        try {
            const response = await fetch(`/api/trigger-health-check?ids=${ids.join(',')}`, { method: 'POST' });
            if (!response.ok) {
                setTriggerError('Failed to trigger the health check. Please try again.');
                return;
            }
            const body: { status: boolean, triggeredCount: number, repairJobEnabled: boolean } = await response.json();
            setShowRepairDisabledWarning(body.repairJobEnabled === false);
            setSelectedIds(new Set());
            // triggered items jump to the front of the queue, so show page 1 again
            if (queuePage !== 1) setQueuePage(1);
            else await fetchQueuePage(1, search);
        } catch {
            setTriggerError('Failed to trigger the health check. Please try again.');
        } finally {
            setIsTriggering(false);
        }
    }, [queuePage, search, fetchQueuePage]);

    // events
    const onHealthItemStatus = useCallback(async (message: string) => {
        const [davItemId, healthResult, repairAction] = message.split('|');
        setQueueItems(x => x.filter(item => item.id !== davItemId));
        setUncheckedCount(x => x - 1);
        setSelectedIds(prev => {
            if (!prev.has(davItemId)) return prev;
            const next = new Set(prev);
            next.delete(davItemId);
            return next;
        });
        setHistoryStats(x => {
            const healthResultNum = Number(healthResult);
            const repairActionNum = Number(repairAction);

            // attempt to find and update a matching statistic
            let updated = false;
            const newStats = x.map(stat => {
                if (stat.result === healthResultNum && stat.repairStatus === repairActionNum) {
                    updated = true;
                    return { ...stat, count: stat.count + 1 };
                }
                return stat;
            });

            // if no statistic was updated, add a new one
            if (!updated) {
                return [
                    ...x,
                    {
                        result: healthResultNum,
                        repairStatus: repairActionNum,
                        count: 1
                    }
                ];
            }

            // if an update occurred, return the modified array
            return newStats;
        });
    }, [setQueueItems, setHistoryStats]);

    const onHealthItemProgress = useCallback((message: string) => {
        const [davItemId, progress] = message.split('|');
        if (progress === "done") return;
        setQueueItems(queueItems => {
            var index = queueItems.findIndex(x => x.id === davItemId);
            if (index === -1) return queueItems;
            return queueItems
                .filter((_, i) => i >= index)
                .map(item => item.id === davItemId
                    ? { ...item, progress: Number(progress) }
                    : item
                )
        });
    }, [setQueueItems]);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.healthItemStatus)
            onHealthItemStatus(message);
        else if (topic == topicNames.healthItemProgress)
            onHealthItemProgress(message);
    }, [
        onHealthItemStatus,
        onHealthItemProgress
    ]);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => { ws.send(JSON.stringify(topicSubscriptions)); }
            ws.onclose = () => { !disposed && setTimeout(() => connect(), 1000); };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }

        return connect();
    }, [onWebsocketMessage]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <HealthStats stats={historyStats} />
            </div>
            {isEnabled && uncheckedCount > 20 &&
                <Alert className={styles.alert} variant={'warning'}>
                    <b>Attention</b>
                    <ul className={styles.list}>
                        <li className={styles.listItem}>
                            You have ~{uncheckedCount} files whose health has never been determined.
                        </li>
                        <li className={styles.listItem}>
                            The queue will run an initial health check of these files.
                        </li>
                        <li className={styles.listItem}>
                            Under normal operation, health checks will occur much less frequently.
                        </li>
                    </ul>
                </Alert>
            }
            {showRepairDisabledWarning &&
                <Alert className={styles.alert} variant={'warning'}>
                    <b>Repair job is disabled</b> in Settings → Repairs —
                    queued items won't be checked until it's enabled.
                </Alert>
            }
            {triggerError &&
                <Alert className={styles.alert} variant={'danger'}>
                    {triggerError}
                </Alert>
            }
            <div className={styles.section}>
                <HealthTable
                    isEnabled={isEnabled}
                    healthCheckItems={queueItems}
                    totalCount={queueTotalCount}
                    pageNumber={queuePage}
                    totalPages={queueTotalPages}
                    onPageSelected={onQueuePageSelected}
                    searchInput={searchInput}
                    onSearchInputChanged={setSearchInput}
                    isSearchActive={search !== ""}
                    selectedIds={selectedIds}
                    onToggleSelect={onToggleSelect}
                    onToggleSelectAll={onToggleSelectAll}
                    onCheckNow={onCheckNow}
                    isTriggering={isTriggering}
                />
            </div>
            <div className={styles.section}>
                <HealthHistory
                    items={historyItems}
                    totalCount={historyTotalCount}
                    pageNumber={historyPage}
                    totalPages={historyTotalPages}
                    onPageSelected={setHistoryPage}
                    isSearchActive={search !== ""}
                />
            </div>
        </div>
    );
}
