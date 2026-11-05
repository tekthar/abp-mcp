import readline from "node:readline";

/** Terminal input and output for the CLI commands (not for stdio mode, where stdout is the protocol). */
export interface Io {
  out(line: string): void;
  err(line: string): void;
  prompt(question: string): Promise<string>;
  /** Read a secret without echoing it. */
  promptHidden(question: string): Promise<string>;
}

export const terminalIo: Io = {
  out: (line) => process.stdout.write(line + "\n"),
  err: (line) => process.stderr.write(line + "\n"),
  prompt: async (question) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stderr });
    try {
      return (await new Promise<string>((resolve) => rl.question(question, resolve))).trim();
    } finally {
      rl.close();
    }
  },
  promptHidden: (question) => readHidden(question),
};

function readHidden(question: string): Promise<string> {
  const input = process.stdin;
  if (!input.isTTY) {
    // Piped input: read one line.
    const rl = readline.createInterface({ input });
    return new Promise((resolve) => rl.once("line", (line) => {
      rl.close();
      resolve(line);
    }));
  }

  process.stderr.write(question);
  return new Promise((resolve, reject) => {
    let value = "";
    input.setRawMode(true);
    input.resume();
    input.setEncoding("utf8");

    const finish = (): void => {
      input.setRawMode(false);
      input.pause();
      input.off("data", onData);
      process.stderr.write("\n");
    };

    const onData = (chunk: string): void => {
      for (const char of chunk) {
        if (char === "\r" || char === "\n") {
          finish();
          resolve(value);
          return;
        }

        if (char === "\u0003") {
          finish();
          reject(new Error("Cancelled."));
          return;
        }

        if (char === "\u007f" || char === "\b") {
          value = value.slice(0, -1);
        } else {
          value += char;
        }
      }
    };

    input.on("data", onData);
  });
}
