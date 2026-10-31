import { readFileSync } from 'node:fs';
import { createPublicKey, verify } from 'node:crypto';

const [file, signature] = process.argv.slice(2);
const publicKey = Buffer.from('e9dd0828f9ee5ecb72e0a811723a79c6e5373ca1c20bd5b255d68a2b3928fcd3', 'hex');
const key = createPublicKey({ key: Buffer.concat([Buffer.from('302a300506032b6570032100', 'hex'), publicKey]), format: 'der', type: 'spki' });
const text = readFileSync(signature, 'utf8').trim();
if (!/^[0-9a-f]{128}$/i.test(text) || !verify(null, readFileSync(file), key, Buffer.from(text, 'hex'))) {
  throw new Error('DLSSync source signature verification failed. Catalog unchanged.');
}
