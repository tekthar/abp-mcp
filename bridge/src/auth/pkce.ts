import { createHash, randomBytes } from "node:crypto";

/** A PKCE pair (RFC 7636, S256) and a `state` value for one authorization request. */
export interface PkcePair {
  readonly verifier: string;
  readonly challenge: string;
  readonly state: string;
}

export function createPkce(): PkcePair {
  const verifier = randomBytes(32).toString("base64url");
  return {
    verifier,
    challenge: createHash("sha256").update(verifier).digest("base64url"),
    state: randomBytes(16).toString("base64url"),
  };
}
