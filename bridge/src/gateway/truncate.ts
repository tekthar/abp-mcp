/** Every cut leaves this marker, so the model (and a reader) can see the value is incomplete. */
export const TRUNCATED = "--- TRUNCATED ---";

/** Below this, a string or container is dropped (arrays) or cut to a marker rather than shrunk further. */
const MIN_SLOT = 64;

/** Deeper than this, a value is cut as text instead of shaped: shaping recurses once per level. */
const MAX_DEPTH = 32;

/**
 * Larger than this, a value is cut as text instead of shaped. With the depth capped and sizes computed once per node,
 * shaping costs at most about MAX_DEPTH times the value's size, so this bounds it at a fraction of a second.
 */
export const MAX_SHAPED_CHARS = 1_000_000;

/**
 * `value` with its JSON cut to about `maxChars` characters, largest parts first. A value of ordinary depth and size
 * keeps its shape: long strings end in the marker, long arrays keep their first items and end with a marker naming
 * how many were dropped, and objects keep every key with smaller values. A value too deep or too large to shape
 * cheaply becomes its JSON text, cut. Either way the work is bounded by the budget, not by the value.
 */
export function truncateJson(value: unknown, maxChars: number): unknown {
  if (exceedsDepth(value, MAX_DEPTH)) {
    return cutText(value, maxChars);
  }

  const sizer = new Sizer();
  const total = sizer.size(value);
  if (total <= maxChars) {
    return value;
  }

  if (total > MAX_SHAPED_CHARS) {
    return cutText(value, maxChars);
  }

  // Structural overhead (keys, brackets, markers) is not budgeted exactly: tighten until it fits.
  let budget = maxChars;
  for (let attempt = 0; attempt < 12; attempt++) {
    const shaped = shrink(value, budget, sizer);
    if (sizer.size(shaped) <= maxChars) {
      return shaped;
    }

    budget = Math.floor(budget * 0.8);
  }

  return cutText(value, maxChars);
}

/** `text` cut so that, with the marker, it fits `maxChars` characters once JSON-encoded. */
export function cutString(text: string, maxChars: number): string {
  if (JSON.stringify(text).length <= maxChars) {
    return text;
  }

  let keep = Math.max(0, maxChars - TRUNCATED.length - 3);
  while (keep > 0 && JSON.stringify(text.slice(0, keep)).length + TRUNCATED.length + 1 > maxChars) {
    keep = Math.floor(keep * 0.9);
  }

  return `${text.slice(0, keep)} ${TRUNCATED}`;
}

/** The value's JSON text, cut to fit `maxChars` once encoded as a JSON string. */
function cutText(value: unknown, maxChars: number): string {
  let text: string;
  try {
    text = JSON.stringify(value) ?? "null";
  } catch {
    // Too deep for the host's own JSON.stringify: say so rather than fail the answer.
    text = "The result is nested too deeply to show.";
  }

  return cutString(text, maxChars);
}

/** Iterative, so a value of any depth cannot overflow the stack here; stops as soon as the limit is passed. */
function exceedsDepth(value: unknown, limit: number): boolean {
  const stack: [unknown, number][] = [[value, 1]];
  while (stack.length > 0) {
    const [node, depth] = stack.pop()!;
    if (node === null || typeof node !== "object") {
      continue;
    }

    if (depth > limit) {
      return true;
    }

    for (const child of Array.isArray(node) ? node : Object.values(node as Record<string, unknown>)) {
      stack.push([child, depth + 1]);
    }
  }

  return false;
}

/** JSON sizes, computed once per object for one truncation rather than once per level. */
class Sizer {
  private readonly cache = new WeakMap<object, number>();

  size(value: unknown): number {
    if (value === null || typeof value !== "object") {
      return JSON.stringify(value)?.length ?? 4;
    }

    let known = this.cache.get(value);
    if (known === undefined) {
      known = JSON.stringify(value)?.length ?? 4;
      this.cache.set(value, known);
    }

    return known;
  }
}

function shrink(value: unknown, budget: number, sizer: Sizer): unknown {
  if (sizer.size(value) <= budget) {
    return value;
  }

  if (typeof value === "string") {
    const keep = Math.max(0, budget - TRUNCATED.length - 4);
    return `${value.slice(0, keep)} ${TRUNCATED}`;
  }

  if (Array.isArray(value)) {
    return shrinkArray(value, budget, sizer);
  }

  if (value && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>);
    const shares = waterFill(entries.map(([key, child]) => sizer.size(child) + key.length + 4), budget);
    return Object.fromEntries(entries.map(([key, child], index) => [key, shrink(child, Math.max(MIN_SLOT, shares[index]! - key.length - 4), sizer)]));
  }

  return value;
}

function shrinkArray(items: readonly unknown[], budget: number, sizer: Sizer): unknown[] {
  // Keep as many leading items as fit at a useful size; name the rest in a marker.
  const kept: unknown[] = [];
  let used = 2;
  for (let index = 0; index < items.length; index++) {
    const remaining = items.length - index;
    const marker = `${TRUNCATED} ${remaining} more item(s)`;
    const room = budget - used - marker.length - 4;
    if (room < MIN_SLOT) {
      kept.push(marker);
      return kept;
    }

    const item = shrink(items[index], Math.min(sizer.size(items[index]), Math.max(MIN_SLOT, Math.floor(room / Math.min(remaining, 4)))), sizer);
    kept.push(item);
    used += sizer.size(item) + 1;
  }

  return kept;
}

/** Split `budget` so small parts keep their full size and the large ones share what is left equally. */
function waterFill(sizes: readonly number[], budget: number): number[] {
  const shares = new Array<number>(sizes.length).fill(0);
  const order = sizes.map((value, index) => ({ value, index })).sort((a, b) => a.value - b.value);
  let left = budget;
  for (let position = 0; position < order.length; position++) {
    const fair = Math.floor(left / (order.length - position));
    const { value, index } = order[position]!;
    shares[index] = Math.min(value, fair);
    left -= shares[index]!;
  }

  return shares;
}
