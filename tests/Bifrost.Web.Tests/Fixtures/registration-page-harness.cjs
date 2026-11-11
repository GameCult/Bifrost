// Runs the sign-up page's real inline script against a simulated browser and prints what it did as JSON.
// Usage: node registration-page-harness.cjs <page.html>
const fs = require("fs");
const vm = require("vm");
const { webcrypto } = require("crypto");

const html = fs.readFileSync(process.argv[2], "utf8");
const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];
if (scripts.length !== 1) {
  throw new Error(`expected exactly one inline script, found ${scripts.length}`);
}
const script = scripts[0][1];
const challenge = html.match(/<input type="hidden" name="challenge" value="([^"]*)"/)[1];
const difficulty = html.match(/data-difficulty="(\d+)"/)[1];

async function run(name, { withCrypto, submits, skipAfter = false }) {
  const statuses = [];
  let handler = null;
  let submitted = 0;
  let sawSubmit;
  const done = new Promise((resolve) => (sawSubmit = resolve));
  const form = {
    elements: { challenge: { value: challenge }, nonce: { value: "" }, waited: { disabled: false } },
    getAttribute: (n) => (n === "data-difficulty" ? difficulty : n === "data-wait" ? "30" : null),
    addEventListener: (type, fn) => {
      if (type === "submit") handler = fn;
    },
    submit() {
      submitted++;
      sawSubmit();
    },
  };
  const status = {
    set textContent(v) {
      statuses.push(v);
    },
  };
  const document = { getElementById: (id) => (id === "register" ? form : id === "status" ? status : null) };
  const window = withCrypto ? { crypto: webcrypto, TextEncoder } : {};
  vm.runInNewContext(script, { window, document, crypto: webcrypto, TextEncoder, Uint8Array, Promise, setTimeout, Math, parseInt, String });
  if (!handler) return { name, listening: false, statuses, submitted, nonce: "", waitedDisabled: false, prevented: [], challenge, difficulty };
  const prevented = [];
  for (let i = 0; i < submits; i++) handler({ preventDefault: () => prevented.push(true) });
  if (skipAfter) {
    // The skip button pressed while the solve is in flight: the script must let this submit through and drop the solve.
    const skipPrevented = [];
    handler({ preventDefault: () => skipPrevented.push(true), submitter: { id: "skip" } });
    await new Promise((r) => setTimeout(r, 200));
    return { name, listening: true, statuses, submitted, nonce: form.elements.nonce.value, waitedDisabled: form.elements.waited.disabled, prevented: skipPrevented, challenge, difficulty };
  }
  await done;
  await new Promise((r) => setTimeout(r, 20));
  return { name, listening: true, statuses, submitted, nonce: form.elements.nonce.value, waitedDisabled: form.elements.waited.disabled, prevented, challenge, difficulty };
}

(async () => {
  const out = [
    await run("solves", { withCrypto: true, submits: 1 }),
    await run("double-press", { withCrypto: true, submits: 2 }),
    await run("skip-during-solve", { withCrypto: true, submits: 1, skipAfter: true }),
    await run("no-subtle", { withCrypto: false, submits: 0 }),
  ];
  console.log(JSON.stringify(out));
})();
