import { resolve } from "node:path";

// The CultLib checkout the tests load at run time, as the tools resolve it:
// $VOIDBOT_CULTLIB_ROOT, else the sibling ../CultLib. Its dist/ is gitignored; build it there first.
export const cultLibRoot = resolve(process.env.VOIDBOT_CULTLIB_ROOT || resolve(import.meta.dirname, "..", "..", "CultLib"));
