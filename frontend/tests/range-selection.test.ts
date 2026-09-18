import assert from "node:assert/strict";
import { test } from "node:test";
import { isRangeSelection, selectionRange } from "../app/utils/range-selection";

const ids = ["a", "b", "c", "d", "e"];

test("plain clicks affect only their row", () => {
    assert.deepEqual([...selectionRange(ids, "a", "d", false)], ["d"]);
});

test("Shift, Ctrl and Cmd each request a range", () => {
    for (const modifier of ["shiftKey", "ctrlKey", "metaKey"]) {
        assert.equal(isRangeSelection({ [modifier]: true }), true);
    }
    assert.equal(isRangeSelection({}), false);
});

test("ranges include both endpoints, forward and backward", () => {
    assert.deepEqual([...selectionRange(ids, "b", "d", true)], ["b", "c", "d"]);
    assert.deepEqual([...selectionRange(ids, "d", "b", true)], ["b", "c", "d"]);
    assert.deepEqual([...selectionRange(ids, "b", "b", true)], ["b"]);
});

test("missing or removed anchors fall back to a single row", () => {
    assert.deepEqual([...selectionRange(ids, null, "d", true)], ["d"]);
    assert.deepEqual([...selectionRange(ids, "removed", "d", true)], ["d"]);
    assert.deepEqual([...selectionRange(ids, "b", "removed", true)], []);
});

test("live inserts and reorderings use current visible order", () => {
    assert.deepEqual([...selectionRange(["d", "new", "c", "b", "a"], "b", "d", true)],
        ["d", "new", "c", "b"]);
});

test("applying a range preserves selection outside it, including when deselecting", () => {
    const selected = new Set(["a", "e"]);
    for (const id of selectionRange(ids, "b", "d", true)) selected.add(id);
    assert.equal(selected.size, 5);
    for (const id of selectionRange(ids, "d", "b", true)) selected.delete(id);
    assert.deepEqual([...selected], ["a", "e"]);
});
