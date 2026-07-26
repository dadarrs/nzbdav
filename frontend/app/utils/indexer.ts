// Shortens an indexer label for display. Host-like values (a download-URL fallback
// such as "api.nzbgeek.info") drop their common subdomain prefix so only the domain
// shows; resolved arr indexer names (which contain spaces/parens) are left untouched.
export function shortenIndexer(value: string | null | undefined): string {
    if (!value) return "";
    let v = value.trim();
    if (/^[a-z0-9.:_-]+$/i.test(v) && v.includes(".")) {
        v = v.replace(/^(www|api)\./i, "");
    }
    return v;
}
