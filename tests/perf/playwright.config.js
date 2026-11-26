// @ts-check
const { defineConfig } = require("@playwright/test");

// Measurements, not checks: run with `npm run perf`. Not part of CI, because the numbers depend on the machine.
module.exports = defineConfig({
  testDir: __dirname,
  testMatch: "*.perf.js",
  fullyParallel: false,
  workers: 1,
  timeout: 120000,
  reporter: "line",
  outputDir: "../../test-results/perf"
});
