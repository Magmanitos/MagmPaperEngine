// Coalesce resolution/DPI bursts and prevent a completed old rebuild from
// replacing a newer selection or resurrecting wallpapers after Stop/sleep.
function createRecoveryController({ enabled, rebuild, onError, delay = 750, retryDelay = 2000, attempts = 8 }) {
  let generation = 0, timer = null, pending = false, failed = false;
  function cancel() { generation++; clearTimeout(timer); timer = null; pending = false; failed = false; }
  function request() {
    cancel();
    if (!enabled()) return;
    pending = true;
    const token = generation;
    const current = () => token === generation && enabled();
    async function run(remaining) {
      timer = null;
      if (!current()) { if (token === generation) pending = false; return; }
      try {
        await rebuild(current);
        if (token === generation) pending = false;
      } catch (error) {
        if (!current()) return;
        if (remaining > 1) timer = setTimeout(() => { void run(remaining - 1); }, retryDelay);
        else { pending = false; failed = true; onError(error); }
      }
    }
    timer = setTimeout(() => { void run(attempts); }, delay);
  }
  return { request, cancel, ensure: () => { if (!pending && !failed) request(); }, get pending() { return pending; } };
}
module.exports = { createRecoveryController };
