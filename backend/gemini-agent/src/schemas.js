import fs from "node:fs";
import path from "node:path";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

function resolveSchemaDir() {
  const candidates = [
    path.resolve(__dirname, "..", "..", "..", "shared", "ipc-protocol", "schema"),
    path.resolve(__dirname, "..", "shared", "ipc-protocol", "schema"),
    path.resolve(process.cwd(), "shared", "ipc-protocol", "schema"),
    path.resolve(process.cwd(), "..", "..", "shared", "ipc-protocol", "schema")
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(path.join(candidate, "agent-request.schema.json"))) {
      return candidate;
    }
  }

  throw new Error(
    `Schema directory not found. Checked: ${candidates.join(", ")}`
  );
}

const schemaDir = resolveSchemaDir();

function loadSchema(fileName) {
  const fullPath = path.join(schemaDir, fileName);
  return JSON.parse(fs.readFileSync(fullPath, "utf8"));
}

export function createSchemaValidators() {
  const ajv = new Ajv2020({
    allErrors: true,
    strict: false
  });
  addFormats(ajv);

  const requestSchema = loadSchema("agent-request.schema.json");
  const responseSchema = loadSchema("agent-response.schema.json");

  return {
    validateRequest: ajv.compile(requestSchema),
    validateResponse: ajv.compile(responseSchema)
  };
}
