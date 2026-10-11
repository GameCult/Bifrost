// Runs the complete page's real inline script against a simulated browser, once per scenario, and prints
// what it did as JSON: the ordered events, and the address and form values at the moment of the submit.
// Usage: node complete-page-harness.cjs <page.html>
const fs = require("fs");
const vm = require("vm");

const html = fs.readFileSync(process.argv[2], "utf8");
const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];
if (scripts.length !== 1) {
  throw new Error(`expected exactly one inline script, found ${scripts.length}`);
}
const script = scripts[0][1];

function run(name, search, hash) {
  const events = [];
  let submit = null;
  const location = {
    pathname: "/auth/heimdall/complete",
    search,
    hash,
    get href() {
      return this.pathname + this.search + this.hash;
    },
    replace(url) {
      events.push(`replace:${url}`);
    },
  };
  const history = {
    replaceState(_state, _title, url) {
      events.push(`replaceState:${url}`);
      const next = new URL(url, "https://bifrost.test" + location.href);
      location.pathname = next.pathname;
      location.search = next.search;
      location.hash = next.hash;
    },
  };
  const form = {
    elements: { attempt: { value: "" }, code: { value: "" } },
    submit() {
      events.push("submit");
      submit = {
        attempt: form.elements.attempt.value,
        code: form.elements.code.value,
        href: location.href,
        hash: location.hash,
      };
    },
  };
  const document = { getElementById: (id) => (id === "complete" ? form : null) };
  vm.runInNewContext(script, { URLSearchParams, location, history, document });
  return { name, events, submit };
}

const complete = "&heimdall_completion_code=CODE123";
console.log(
  JSON.stringify([
    run("success", "?attempt=NONCE", "#heimdall_status=success" + complete),
    run("success-with-decoy-query-code", "?attempt=NONCE&heimdall_completion_code=QCODE", "#heimdall_status=success" + complete),
    run("error-status-with-code", "?attempt=NONCE", "#heimdall_status=error&heimdall_error=denied" + complete),
    run("no-code", "?attempt=NONCE", "#heimdall_status=success"),
    run("no-attempt", "", "#heimdall_status=success" + complete),
    run("code-in-query-only", "?attempt=NONCE&heimdall_completion_code=QCODE", ""),
  ]),
);
