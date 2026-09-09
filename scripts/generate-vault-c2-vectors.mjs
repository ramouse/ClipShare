import {
  createCipheriv,
  createHash,
  hkdfSync,
} from "node:crypto";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const PREFIX_KEY = Buffer.from("clipshare:vault:key:v1\0", "ascii");
const PREFIX_AAD = Buffer.from("clipshare:vault:aad:v1\0", "ascii");

function uuidBytes(value) {
  return Buffer.from(value.replaceAll("-", ""), "hex");
}

function u16(value) {
  const result = Buffer.alloc(2);
  result.writeUInt16BE(value);
  return result;
}

function u64(value) {
  const result = Buffer.alloc(8);
  result.writeBigUInt64BE(BigInt(value));
  return result;
}

function lengthPrefixedUtf8(value) {
  const encoded = Buffer.from(value, "utf8");
  return Buffer.concat([u16(encoded.length), encoded]);
}

function deriveKey(epochSecret, vaultId, purpose, originDeviceId) {
  const info = Buffer.concat([
    PREFIX_KEY,
    lengthPrefixedUtf8(purpose),
    uuidBytes(originDeviceId),
  ]);
  return Buffer.from(
    hkdfSync("sha256", epochSecret, uuidBytes(vaultId), info, 32),
  );
}

function buildAad({
  purpose,
  vaultId,
  originDeviceId,
  entityId,
  field,
  keyEpoch,
  nonceCounter,
  paddedPlaintextBytes,
}) {
  return Buffer.concat([
    PREFIX_AAD,
    u16(1),
    lengthPrefixedUtf8(purpose),
    uuidBytes(vaultId),
    uuidBytes(originDeviceId),
    uuidBytes(entityId),
    lengthPrefixedUtf8(field),
    u64(keyEpoch),
    u64(nonceCounter),
    u64(paddedPlaintextBytes),
  ]);
}

function frame4096(payload) {
  const framedLength = 8 + payload.length;
  const paddedLength = Math.max(4096, Math.ceil(framedLength / 4096) * 4096);
  const result = Buffer.alloc(paddedLength);
  result.writeBigUInt64BE(BigInt(payload.length), 0);
  payload.copy(result, 8);
  return result;
}

function encrypt(key, nonce, plaintext, aad) {
  const cipher = createCipheriv("aes-256-gcm", key, nonce, {
    authTagLength: 16,
  });
  cipher.setAAD(aad, { plaintextLength: plaintext.length });
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  return Buffer.concat([ciphertext, cipher.getAuthTag()]);
}

function b64url(value) {
  return value.toString("base64url");
}

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

const vaultId = "00112233-4455-4677-8899-aabbccddeeff";
const originDeviceId = "11111111-2222-4333-8444-555555555555";
const itemId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
const fileId = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff";
const epoch1 = Buffer.from(
  "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
  "hex",
);
const epoch2 = Buffer.from(
  "ffeeddccbbaa99887766554433221100f0e0d0c0b0a0908070605040302010000",
  "hex",
);
const purpose = "item-payload";
const payload = Buffer.from("ClipShare 中文🙂\u0000\r\né|é", "utf8");
const framed = frame4096(payload);
const counter = 7;
const nonce = Buffer.concat([Buffer.from("a0a1a2a3", "hex"), u64(counter)]);
const aad = buildAad({
  purpose,
  vaultId,
  originDeviceId,
  entityId: itemId,
  field: "content",
  keyEpoch: 1,
  nonceCounter: counter,
  paddedPlaintextBytes: framed.length,
});
const key1 = deriveKey(epoch1, vaultId, purpose, originDeviceId);
const key2 = deriveKey(epoch2, vaultId, purpose, originDeviceId);
const encrypted = encrypt(key1, nonce, framed, aad);

const fileDek = Buffer.from(
  "f0e0d0c0b0a09080706050403020100000112233445566778899aabbccddeeff",
  "hex",
);
const chunkIndex = 3;
const chunkNonce = Buffer.concat([Buffer.from("10111213", "hex"), u64(chunkIndex)]);
const chunk = Buffer.from("00ff80017f102030405060708090a0b0", "hex");
const chunkAad = buildAad({
  purpose: "file-chunk",
  vaultId,
  originDeviceId,
  entityId: fileId,
  field: "chunk",
  keyEpoch: 1,
  nonceCounter: chunkIndex,
  paddedPlaintextBytes: chunk.length,
});
const encryptedChunk = encrypt(fileDek, chunkNonce, chunk, chunkAad);

export const output = {
  suite: "clipshare-vault-v1",
  suiteVersion: 1,
  testOnly: true,
  vectors: [
    {
      id: "epoch1-item-payload-4k",
      purpose,
      vaultId,
      originDeviceId,
      entityId: itemId,
      field: "content",
      keyEpoch: 1,
      epochSecretHex: epoch1.toString("hex"),
      derivedKeyHex: key1.toString("hex"),
      noncePrefixHex: "a0a1a2a3",
      nonceCounter: counter,
      nonceHex: nonce.toString("hex"),
      payloadHex: payload.toString("hex"),
      payloadUtf8: "ClipShare 中文🙂\u0000\r\né|é",
      paddedPlaintextBytes: framed.length,
      paddedPlaintextSha256Hex: sha256(framed),
      aadHex: aad.toString("hex"),
      cipherAndTagBytes: encrypted.length,
      cipherAndTagSha256Hex: sha256(encrypted),
    },
    {
      id: "independent-epoch2-same-context",
      purpose,
      vaultId,
      originDeviceId,
      keyEpoch: 2,
      epochSecretHex: epoch2.toString("hex"),
      derivedKeyHex: key2.toString("hex"),
      mustDifferFrom: "epoch1-item-payload-4k",
    },
    {
      id: "file-generation-chunk-3",
      purpose: "file-chunk",
      vaultId,
      originDeviceId,
      entityId: fileId,
      field: "chunk",
      keyEpoch: 1,
      fileDekHex: fileDek.toString("hex"),
      noncePrefixHex: "10111213",
      nonceCounter: chunkIndex,
      nonceHex: chunkNonce.toString("hex"),
      payloadHex: chunk.toString("hex"),
      paddedPlaintextBytes: chunk.length,
      aadHex: chunkAad.toString("hex"),
      cipherAndTagB64Url: b64url(encryptedChunk),
      cipherAndTagSha256Hex: sha256(encryptedChunk),
    },
  ],
};

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(resolve(process.argv[1])).href
) {
  process.stdout.write(`${JSON.stringify(output, null, 2)}\n`);
}
