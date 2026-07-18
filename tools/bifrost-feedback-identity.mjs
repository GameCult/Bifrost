import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign as ed25519Sign } from "node:crypto";
import { chmod, mkdir, open, readFile, stat } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const ID_DOMAIN=Buffer.from("epiphany.host-incarnation.identity.v0\0","utf8");
const SIGNATURE_DOMAIN=Buffer.from("epiphany.host-incarnation.signature.v0\0","utf8");
export const FEEDBACK_SIGNING_PURPOSE="bifrost.persona-feedback.delivery.v0";

export async function enrollBifrostFeedbackIdentity(privateKeyPath){
  await mkdir(dirname(privateKeyPath),{recursive:true});
  const {privateKey}=generateKeyPairSync("ed25519"), jwk=privateKey.export({format:"jwk"});
  const seed=Buffer.from(jwk.d,"base64url");
  const handle=await open(privateKeyPath,"wx",0o600);
  try{await handle.writeFile(seed);await handle.sync();}finally{await handle.close();}
  await chmod(privateKeyPath,0o600);
  return loadBifrostFeedbackIdentity(privateKeyPath);
}
export async function loadBifrostFeedbackIdentity(privateKeyPath){
  const info=await stat(privateKeyPath);
  if(!info.isFile())throw new Error("Bifrost feedback identity must be a regular file.");
  if(process.platform!=="win32"&&(info.mode&0o077)!==0)throw new Error("Bifrost feedback identity private key must have mode 0600.");
  if(process.platform!=="win32"&&typeof process.geteuid==="function"&&info.uid!==process.geteuid())throw new Error("Bifrost feedback identity private key must be owned by the service user.");
  const seed=await readFile(privateKeyPath);if(seed.length!==32)throw new Error("Bifrost feedback identity seed must be exactly 32 bytes.");
  const key=createPrivateKey({key:Buffer.concat([Buffer.from("302e020100300506032b657004220420","hex"),seed]),format:"der",type:"pkcs8"});
  const publicKey=createPublicKey(key).export({format:"der",type:"spki"}).subarray(-32);
  const identityId=createHash("sha256").update(ID_DOMAIN).update(publicKey).digest("hex");
  return {identityId,publicKey,key};
}
export function signFeedbackAdmission(identity,payload){
  const purpose=Buffer.from(FEEDBACK_SIGNING_PURPOSE,"utf8"), message=Buffer.concat([SIGNATURE_DOMAIN,u64(purpose.length),purpose,u64(payload.length),payload]);
  return ed25519Sign(null,message,identity.key);
}
export function trustAnchor(identity,createdAt){return {schemaVersion:"epiphany.host_identity_trust_anchor.v0",identityId:identity.identityId,publicKey:new Uint8Array(identity.publicKey),assurance:"os_service_file_bound",identityCreatedAt:createdAt,sourceIdentityRecordSha256:`sha256-${createHash("sha256").update(identity.publicKey).digest("hex")}`};}
export function defaultFeedbackIdentityPath(){return process.env.BIFROST_PERSONA_FEEDBACK_PRIVATE_KEY??(process.platform==="win32"?resolve(".bifrost","private","persona-feedback-ed25519.seed"):"/var/lib/gamecult/bifrost/persona-feedback/persona-feedback-ed25519.seed");}
function u64(value){const b=Buffer.alloc(8);b.writeBigUInt64BE(BigInt(value));return b;}
