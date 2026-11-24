import type { Config } from "jest";
import nextJest from "next/jest.js";

const createJestConfig = nextJest({ dir: "./" });

const config: Config = {
  testEnvironment: "jest-environment-jsdom",
  testMatch: ["**/__tests__/**/*.test.{ts,tsx}"],
  moduleNameMapper: {
    "^@/(.*)$": "<rootDir>/$1",
    // Server-only modules (auth.ts/session-store.ts) are import-safe in
    // jest — the real package throws outside a server build.
    "^server-only$": "<rootDir>/test/server-only-stub.ts",
  },
};

export default createJestConfig(config);
