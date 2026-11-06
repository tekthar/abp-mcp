const REDACTED = "[REDACTED]";

// Shorter values are too likely to collide with ordinary text to replace blindly.
const MIN_SECRET_LENGTH = 6;

const BEARER = /(Bearer\s+)[A-Za-z0-9\-._~+/]+=*/gi;
const FORM_SECRET = /\b(access_token|refresh_token|id_token|password|client_secret|code|code_verifier|token)=([^&\s"']+)/gi;
const JSON_SECRET = /("(?:access_token|refresh_token|id_token|password|client_secret|code|code_verifier)"\s*:\s*")([^"]*)(")/gi;

/**
 * Removes secrets from anything the bridge writes to a log. Every token, password, authorization code and
 * client secret is registered as soon as the bridge sees it; known secret-bearing shapes (bearer headers,
 * form fields, JSON fields) are masked even when a value was never registered.
 */
export class Redactor {
  private readonly secrets = new Set<string>();

  /** Remember a secret so every later log line masks it. Ignores empty and very short values. */
  add(secret: string | null | undefined): void {
    if (secret && secret.length >= MIN_SECRET_LENGTH) {
      this.secrets.add(secret);
    }
  }

  /** The text with every registered secret and secret-bearing field masked. */
  redact(text: string): string {
    let result = text;
    for (const secret of this.secrets) {
      result = result.split(secret).join(REDACTED);
    }

    return result
      .replace(BEARER, `$1${REDACTED}`)
      .replace(FORM_SECRET, `$1=${REDACTED}`)
      .replace(JSON_SECRET, `$1${REDACTED}$3`);
  }
}
