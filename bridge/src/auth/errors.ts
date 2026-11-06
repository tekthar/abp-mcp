/** Sign-in cannot proceed without the user. The message says what to run. */
export class NotSignedInError extends Error {
  override readonly name = "NotSignedInError";
}

/** Sign-in is paused after the authorization server rejected the credentials, to avoid locking the account. */
export class BreakerOpenError extends Error {
  override readonly name = "BreakerOpenError";

  constructor(readonly until: Date, readonly reason: string, profile: string) {
    super(
      `Sign-in for profile '${profile}' is paused until ${until.toISOString()} because the server rejected the ` +
      `credentials (${reason}). Fix them, then run \`abp-mcp login ${profile}\` to sign in and clear the pause.`);
  }
}

/** An OAuth error response from the authorization server (RFC 6749 section 5.2). */
export class OAuthError extends Error {
  override readonly name = "OAuthError";

  constructor(readonly error: string, readonly description?: string, readonly status?: number) {
    super(description ? `${error}: ${description}` : error);
  }

  /** The server rejected the credentials themselves; retrying them can only lock the account. */
  get isCredentialRejection(): boolean {
    return this.error === "invalid_grant" || this.error === "invalid_client" || this.error === "unauthorized_client";
  }
}

/** Saving a secret needs the OS keychain, and there is none on this machine. */
export class KeychainUnavailableError extends Error {
  override readonly name = "KeychainUnavailableError";
}
