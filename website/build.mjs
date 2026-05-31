import { copyFile, cp, mkdir, rm } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const dist = join(root, "dist");

await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });
await copyFile(join(root, "index.html"), join(dist, "index.html"));
await copyFile(join(root, "styles.css"), join(dist, "styles.css"));
await cp(join(root, "public"), dist, { recursive: true });

