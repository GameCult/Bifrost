import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { isWithin } from "../tools/path-within.mjs";

const flavours = [
  { name: "posix", pathApi: path.posix, root: "/srv/repo", sep: "/", inside: "/srv/repo/src/a.txt", sibling: "/srv/repo-evil/a.txt", other: "/etc/passwd" },
  { name: "win32", pathApi: path.win32, root: "C:\\Work\\Repo", sep: "\\", inside: "C:\\Work\\Repo\\src\\a.txt", sibling: "C:\\Work\\Repo-evil\\a.txt", other: "D:\\Work\\Repo\\a.txt" },
];

for (const f of flavours) {
  const opts = { pathApi: f.pathApi };
  test(`${f.name}: a path inside the root, and the root itself, are within`, () => {
    assert.equal(isWithin(f.root, f.inside, opts), true);
    assert.equal(isWithin(f.root, f.root, opts), true);
    assert.equal(isWithin(f.root, f.root + f.sep, opts), true);
  });

  test(`${f.name}: a sibling sharing the root as a name prefix is refused`, () => {
    assert.equal(isWithin(f.root, f.sibling, opts), false);
  });

  test(`${f.name}: a dot-dot escape and an unrelated absolute path are refused`, () => {
    assert.equal(isWithin(f.root, f.root + f.sep + ["..", "x"].join(f.sep), opts), false);
    assert.equal(isWithin(f.root, f.other, opts), false);
    assert.equal(isWithin(f.root, f.root + f.sep + "..", opts), false);
  });

  test(`${f.name}: a name that merely starts with two dots is inside`, () => {
    assert.equal(isWithin(f.root, f.root + f.sep + "..cache" + f.sep + "x", opts), true);
  });

  test(`${f.name}: excluded dir, strictly beneath it vs not`, () => {
    const excluded = f.root + f.sep + "tools" + f.sep + "git-gate";
    const strict = { ...opts, allowSelf: false };
    assert.equal(isWithin(excluded, excluded + f.sep + "git.cmd", strict), true);
    assert.equal(isWithin(excluded, excluded, strict), false);
    assert.equal(isWithin(excluded, excluded + "-other" + f.sep + "git", strict), false);
    assert.equal(isWithin(excluded, f.root + f.sep + "bin" + f.sep + "git", strict), false);
  });
}

test("win32: comparison ignores case, as Windows paths do", () => {
  assert.equal(isWithin("C:\\Work\\Repo", "c:\\work\\repo\\SRC\\a.txt", { pathApi: path.win32 }), true);
  assert.equal(isWithin("C:\\Work\\Repo", "c:\\work\\repo-evil\\a.txt", { pathApi: path.win32 }), false);
});

test("posix: comparison is case-sensitive", () => {
  assert.equal(isWithin("/srv/repo", "/srv/Repo/a.txt", { pathApi: path.posix }), false);
});
