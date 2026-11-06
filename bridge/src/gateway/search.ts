import type { Tool } from "@modelcontextprotocol/sdk/types.js";

/** One search match: enough to choose a tool, without its schemas (that is what `describe_tools` is for). */
export interface ToolMatch {
  readonly name: string;
  readonly description: string;
  /** The tool's HTTP method from the host's `/mcp/_discover`, when the bridge knows it. */
  readonly httpMethod?: string;
  /** From the tool's MCP annotations, when the host sets them. */
  readonly readOnly?: boolean;
}

export const DEFAULT_SEARCH_LIMIT = 10;
export const MAX_SEARCH_LIMIT = 50;
const SUMMARY_CHARS = 160;

interface Field {
  readonly text: string;
  readonly tokens: readonly string[];
}

interface Entry {
  readonly tool: Tool;
  readonly name: Field;
  readonly description: Field;
}

/**
 * Ranked keyword search over tool names and descriptions, with no embeddings: names are split on camelCase,
 * snake_case, dots and dashes, a name match outweighs a description match, and a result must contain enough
 * of the query's words (all of them for one or two words, 60% for longer queries).
 */
export class ToolSearchIndex {
  private readonly entries: readonly Entry[];

  constructor(tools: readonly Tool[], private readonly httpMethods?: ReadonlyMap<string, string | null>) {
    this.entries = tools.map((tool) => ({
      tool,
      name: field(tool.name),
      description: field(tool.description ?? ""),
    }));
  }

  search(query: string, limit = DEFAULT_SEARCH_LIMIT): ToolMatch[] {
    const phrase = normalize(query);
    const words = tokenize(query);
    if (words.length === 0) {
      return [];
    }

    const scored: { entry: Entry; score: number }[] = [];
    for (const entry of this.entries) {
      const score = scoreEntry(entry, phrase, words);
      if (score > 0) {
        scored.push({ entry, score });
      }
    }

    scored.sort((a, b) => b.score - a.score || a.entry.tool.name.localeCompare(b.entry.tool.name));
    const capped = Math.min(Math.max(1, Math.floor(limit)), MAX_SEARCH_LIMIT);
    return scored.slice(0, capped).map(({ entry }) => this.match(entry.tool));
  }

  private match(tool: Tool): ToolMatch {
    const method = this.httpMethods?.get(tool.name);
    const readOnly = tool.annotations?.readOnlyHint;
    return {
      name: tool.name,
      description: summarize(tool.description ?? ""),
      ...(method ? { httpMethod: method } : {}),
      ...(typeof readOnly === "boolean" ? { readOnly } : {}),
    };
  }
}

function scoreEntry(entry: Entry, phrase: string, words: readonly string[]): number {
  let score = 0;
  let matched = 0;
  for (const word of words) {
    const inName = scoreWord(word, entry.name, 10);
    const inDescription = scoreWord(word, entry.description, 3);
    if (inName + inDescription > 0) {
      matched++;
    }

    score += inName + inDescription;
  }

  const coverage = matched / words.length;
  const needed = words.length <= 2 ? 1 : 0.6;
  if (matched === 0 || coverage < needed) {
    return 0;
  }

  if (entry.name.text === phrase) {
    score += 100;
  } else if (entry.name.text.includes(phrase)) {
    score += 30;
  } else if (entry.description.text.includes(phrase)) {
    score += 8;
  }

  return score + coverage * 10;
}

/** Exact word, then prefix either way (issue/issues), then substring, weighted by the field. */
function scoreWord(word: string, target: Field, weight: number): number {
  if (target.tokens.includes(word)) {
    return weight * 4;
  }

  if (target.tokens.some((token) => token.startsWith(word) || word.startsWith(token))) {
    return weight * 2;
  }

  return target.text.includes(word) ? weight : 0;
}

function field(value: string): Field {
  return { text: normalize(value), tokens: tokenize(value) };
}

function normalize(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_./:-]+/g, " ")
    .toLowerCase()
    .replace(/\s+/g, " ")
    .trim();
}

function tokenize(value: string): string[] {
  return normalize(value).split(/[^a-z0-9]+/).filter((token) => token.length > 0);
}

/** The first sentence, cut to a line. */
export function summarize(description: string): string {
  const flat = description.replace(/\s+/g, " ").trim();
  const sentence = flat.match(/^.*?[.!?](?=\s|$)/)?.[0] ?? flat;
  return sentence.length <= SUMMARY_CHARS ? sentence : `${sentence.slice(0, SUMMARY_CHARS - 1).trimEnd()}…`;
}
