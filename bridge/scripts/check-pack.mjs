// Fails when `npm pack` would publish anything but the build output, the README, the license and package.json.
import { execFileSync } from "node:child_process";

const [pack] = JSON.parse(execFileSync(process.platform === "win32" ? "npm.cmd" : "npm", ["pack", "--dry-run", "--json", "--ignore-scripts"], {
  encoding: "utf8",
  shell: process.platform === "win32",
}));
const allowed = (file) => file === "package.json" || file === "README.md" || file === "LICENSE" || (file.startsWith("dist/") && file.endsWith(".js"));
const unexpected = pack.files.map((entry) => entry.path).filter((file) => !allowed(file));
if (unexpected.length > 0) {
  console.error(`npm pack would also publish:\n  ${unexpected.join("\n  ")}`);
  process.exit(1);
}

if (!pack.files.some((entry) => entry.path === "dist/bin.js")) {
  console.error("npm pack is missing dist/bin.js: run the build first.");
  process.exit(1);
}

console.log(`npm pack: ${pack.files.length} files, ${pack.size} bytes, all expected.`);
