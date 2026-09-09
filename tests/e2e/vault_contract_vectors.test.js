"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const contractRoot = path.resolve(__dirname, "../../contracts/vault");
const positivePath = path.join(contractRoot, "test-vectors/positive-vectors.json");
const negativePath = path.join(contractRoot, "test-vectors/negative-vectors.json");

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function collectRefs(value, refs = []) {
  if (Array.isArray(value)) {
    for (const entry of value) collectRefs(entry, refs);
  } else if (value && typeof value === "object") {
    for (const [key, entry] of Object.entries(value)) {
      if (key === "$ref" && typeof entry === "string") refs.push(entry);
      collectRefs(entry, refs);
    }
  }
  return refs;
}

(async () => {
  const schemaFiles = fs
    .readdirSync(contractRoot)
    .filter((name) => name.endsWith(".schema.json"))
    .sort();
  assert.ok(schemaFiles.length >= 10, "Vault v1 schema set is incomplete");

  const ids = new Set();
  for (const name of schemaFiles) {
    const schema = readJson(path.join(contractRoot, name));
    assert.equal(
      schema.$schema,
      "https://json-schema.org/draft/2020-12/schema",
      `${name} must use JSON Schema 2020-12`,
    );
    assert.equal(typeof schema.$id, "string", `${name} must declare $id`);
    assert.ok(!ids.has(schema.$id), `${name} repeats $id ${schema.$id}`);
    ids.add(schema.$id);

    for (const ref of collectRefs(schema)) {
      if (ref.startsWith("https://")) continue;
      const target = ref.split("#", 1)[0];
      assert.ok(
        fs.existsSync(path.join(contractRoot, target)),
        `${name} has missing local ref ${ref}`,
      );
    }
  }

  const checkedIn = readJson(positivePath);
  const generated = await import("../../scripts/generate-vault-c2-vectors.mjs");
  assert.deepEqual(
    generated.output,
    checkedIn,
    "reviewed Vault KAT must match the deterministic generator",
  );
  assert.equal(checkedIn.testOnly, true);
  assert.equal(checkedIn.vectors.length, 3);

  const epoch1 = checkedIn.vectors.find((item) => item.id === "epoch1-item-payload-4k");
  const epoch2 = checkedIn.vectors.find(
    (item) => item.id === "independent-epoch2-same-context",
  );
  const chunk = checkedIn.vectors.find((item) => item.id === "file-generation-chunk-3");
  assert.ok(epoch1 && epoch2 && chunk, "required Vault vectors are missing");
  assert.notEqual(epoch1.epochSecretHex, epoch2.epochSecretHex);
  assert.notEqual(epoch1.derivedKeyHex, epoch2.derivedKeyHex);
  assert.equal(epoch1.nonceHex, `${epoch1.noncePrefixHex}0000000000000007`);
  assert.equal(chunk.nonceHex, `${chunk.noncePrefixHex}0000000000000003`);
  assert.equal(epoch1.paddedPlaintextBytes, 4096);

  const negatives = readJson(negativePath);
  assert.equal(negatives.testOnly, true);
  for (const group of [
    "contractCases",
    "authenticationCases",
    "nonceCases",
    "initializationCases",
    "epochCases",
  ]) {
    assert.ok(Array.isArray(negatives[group]) && negatives[group].length > 0, group);
  }
  assert.ok(
    negatives.nonceCases.some((item) => item.id === "counter-exhausted"),
    "counter exhaustion must be a shared negative case",
  );
  assert.ok(
    negatives.initializationCases.some((item) => item.id === "database-only"),
    "database-only recovery must be fail-closed",
  );
  assert.ok(
    negatives.epochCases.some((item) => item.id === "derive-future-from-old-secret"),
    "future epoch derivation must be forbidden",
  );

  console.log(
    `Vault C2 contract gate passed: ${schemaFiles.length} schemas, ` +
      `${checkedIn.vectors.length} positive vectors.`,
  );
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
