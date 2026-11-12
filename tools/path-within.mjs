import path from "node:path";

// True when `candidate` is `root` itself (only if allowSelf) or lies beneath it.
// Containment is path.relative-based, so it follows the host's separator and
// case rules; pass path.win32 or path.posix to pin a flavour.
export function isWithin(root, candidate, { pathApi = path, allowSelf = true } = {}) {
  const relative = pathApi.relative(pathApi.resolve(root), pathApi.resolve(candidate));
  if (relative === "") {
    return allowSelf;
  }
  if (pathApi.isAbsolute(relative) || relative === ".." || relative.startsWith(`..${pathApi.sep}`)) {
    return false;
  }
  return true;
}
