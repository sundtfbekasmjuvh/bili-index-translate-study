"use strict";

// In-memory stand-in for one chrome.storage area (sync or local), backed by the object it is given.
// get() follows the real API: a string or an array returns only the keys that are stored, an object
// supplies defaults for the keys it names, and no argument returns everything.
function storageArea(data) {
  const has = (key) => Object.prototype.hasOwnProperty.call(data, key);
  return {
    async get(keys) {
      if (keys === undefined || keys === null) return { ...data };
      const out = {};
      if (typeof keys === "string" || Array.isArray(keys)) {
        for (const key of [].concat(keys)) if (has(key)) out[key] = data[key];
        return out;
      }
      for (const key of Object.keys(keys)) out[key] = has(key) ? data[key] : keys[key];
      return out;
    },
    async set(items) {
      Object.assign(data, items);
    },
    async remove(keys) {
      for (const key of [].concat(keys)) delete data[key];
    },
    async clear() {
      for (const key of Object.keys(data)) delete data[key];
    }
  };
}

module.exports = { storageArea };
