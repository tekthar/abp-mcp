/** Every cut leaves this marker, so the model (and a reader) can see the value is incomplete. */
export const TRUNCATED = "--- TRUNCATED ---";

/** Below this, a string or container is dropped (arrays) or cut to a marker rather than shrunk further. */
const MIN_SLOT = 64;

/**
 * `value` with its JSON cut to about `maxChars` characters, largest parts first. The result is still valid JSON
 * of the same shape: long strings end in the marker, long arrays keep their first items and end with a marker
 * naming how many were dropped, and objects keep every key with smaller values.
 */
export function truncateJson(value: unknown, maxChars: number): unknown {
  if (size(value) <= maxChars) {
    return value;
  }

  // Structural overhead (keys, brackets, markers) is not budgeted exactly: tighten until it fits.
  let budget = maxChars;
  for (let attempt = 0; attempt < 12; attempt++) {
    const shaped = shrink(value, budget);
    if (size(shaped) <= maxChars) {
      return shaped;
    }

    budget = Math.floor(budget * 0.8);
  }

  return `${JSON.stringify(value).slice(0, Math.max(0, maxChars - TRUNCATED.length - 2))} ${TRUNCATED}`;
}

function shrink(value: unknown, budget: number): unknown {
  if (size(value) <= budget) {
    return value;
  }

  if (typeof value === "string") {
    const keep = Math.max(0, budget - TRUNCATED.length - 4);
    return `${value.slice(0, keep)} ${TRUNCATED}`;
  }

  if (Array.isArray(value)) {
    return shrinkArray(value, budget);
  }

  if (value && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>);
    const shares = waterFill(entries.map(([key, child]) => size(child) + key.length + 4), budget);
    return Object.fromEntries(entries.map(([key, child], index) => [key, shrink(child, Math.max(MIN_SLOT, shares[index]! - key.length - 4))]));
  }

  return value;
}

function shrinkArray(items: readonly unknown[], budget: number): unknown[] {
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

    const item = shrink(items[index], Math.min(size(items[index]), Math.max(MIN_SLOT, Math.floor(room / Math.min(remaining, 4)))));
    kept.push(item);
    used += size(item) + 1;
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

function size(value: unknown): number {
  return JSON.stringify(value)?.length ?? 4;
}
