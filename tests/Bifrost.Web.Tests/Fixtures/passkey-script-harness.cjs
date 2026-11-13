// Runs wwwroot/js/passkeys.js against a simulated browser, once per scenario, and prints what it did as JSON:
// every fetch (URL, headers, body), the arguments to navigator.credentials, the form values at the submit, and any navigation.
// Usage: node passkey-script-harness.cjs <passkeys.js>
const fs = require("fs");
const vm = require("vm");

const source = fs.readFileSync(process.argv[2], "utf8");
const buf = (...bytes) => Uint8Array.from(bytes).buffer;

function form(names, extra = {}) {
  const elements = {};
  for (const name of names) {
    elements[name] = { value: "" };
  }
  Object.assign(elements, extra);
  return {
    elements,
    dataset: {},
    handler: null,
    addEventListener(type, fn) {
      if (type === "submit") this.handler = fn;
    },
    querySelector() {
      return { value: "TOKEN" };
    },
    submit() {
      this.submitted = Object.fromEntries(Object.entries(this.elements).map(([k, v]) => [k, v.value]));
    },
  };
}

async function run(name, { kind, fetchReply, credentialsReply }) {
  const enrol = kind === "enrol";
  const theForm = enrol
    ? form(["id", "rawId", "attestationObject", "clientDataJson", "transports", "label"])
    : form(["id", "rawId", "authenticatorData", "clientDataJson", "signature", "userHandle"], { identityId: { value: "IDENTITY" } });
  const log = { name, fetches: [], credentialCall: null, navigations: [], status: "" };
  const statusEl = { textContent: "" };
  const document = {
    getElementById: (id) => (id === "passkey-status" ? statusEl : null),
    querySelectorAll: (selector) => (selector === (enrol ? "form[data-passkey-enrol]" : "form[data-passkey-unlink]") ? [theForm] : []),
  };
  const location = { pathname: "/Membership/LinkedAccounts", assign: (url) => log.navigations.push(url) };
  const fetch = async (url, init) => {
    log.fetches.push({ url, method: init.method, headers: init.headers, body: String(init.body), credentials: init.credentials });
    return { ok: fetchReply.ok, json: async () => fetchReply.body };
  };
  const call = async (publicKey) => {
    log.credentialCall = {
      challengeIsBuffer: publicKey.challenge instanceof ArrayBuffer,
      challengeBytes: Array.from(new Uint8Array(publicKey.challenge)),
      userIdBytes: publicKey.user ? Array.from(new Uint8Array(publicKey.user.id)) : null,
      listIdsAreBuffers: (publicKey.excludeCredentials || publicKey.allowCredentials).every((x) => x.id instanceof ArrayBuffer),
    };
    if (credentialsReply === "reject") throw new Error("NotAllowedError");
    return credentialsReply;
  };
  const navigator = {
    credentials: enrol ? { create: ({ publicKey }) => call(publicKey) } : { get: ({ publicKey }) => call(publicKey) },
  };
  vm.runInNewContext(source, { document, location, fetch, navigator, URLSearchParams, Uint8Array, ArrayBuffer, atob, btoa, Promise, Error });
  await theForm.handler({ preventDefault() {} });
  await new Promise((r) => setTimeout(r, 20));
  log.submitted = theForm.submitted || null;
  log.status = statusEl.textContent;
  return log;
}

(async () => {
  const creationOptions = {
    ok: true,
    body: { challenge: "AQID", user: { id: "BAUG" }, excludeCredentials: [{ type: "public-key", id: "BwgJ" }] },
  };
  const requestOptions = {
    ok: true,
    body: { challenge: "AQID", allowCredentials: [{ type: "public-key", id: "BwgJ", transports: ["internal"] }] },
  };
  const created = {
    id: "CRED-ID",
    rawId: buf(251, 255, 254),
    response: { attestationObject: buf(1, 2, 3), clientDataJSON: buf(4, 5), getTransports: () => ["internal", "hybrid"] },
  };
  const asserted = {
    id: "CRED-ID",
    rawId: buf(251, 255, 254),
    response: { authenticatorData: buf(9, 9), clientDataJSON: buf(4, 5), signature: buf(7, 7, 7), userHandle: buf(1, 1) },
  };
  const out = [
    await run("enrol", { kind: "enrol", fetchReply: creationOptions, credentialsReply: created }),
    await run("unlink", { kind: "unlink", fetchReply: requestOptions, credentialsReply: asserted }),
    await run("refused", { kind: "enrol", fetchReply: { ok: false, body: { error: "enrol_needs_sign_in" } }, credentialsReply: created }),
    await run("hostile-error", { kind: "unlink", fetchReply: { ok: false, body: { error: "<script>alert(1)</script>" } }, credentialsReply: asserted }),
    await run("cancelled", { kind: "unlink", fetchReply: requestOptions, credentialsReply: "reject" }),
  ];
  console.log(JSON.stringify(out));
})();
