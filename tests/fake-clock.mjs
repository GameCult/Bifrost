// Test preload: fixes Date.now() to FAKE_NOW_MS when a test sets it.
if (process.env.FAKE_NOW_MS) {
  const fixed = Number(process.env.FAKE_NOW_MS);
  Date.now = () => fixed;
}
