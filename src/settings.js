const DEFAULT_SETTINGS = Object.freeze({ mouseEnabled: true, pauseWhenCovered: true, pauseOnBattery: false, fps: 60, renderScale: 1 });
function validateSettings(patch) {
  if (!patch || typeof patch !== 'object' || Array.isArray(patch)) throw new Error('Некорректные параметры.');
  for (const [key, value] of Object.entries(patch)) {
    const valid = ['mouseEnabled', 'pauseWhenCovered', 'pauseOnBattery'].includes(key) ? typeof value === 'boolean'
      : key === 'fps' ? [15, 30, 60].includes(value) : key === 'renderScale' ? [0.5, 0.75, 1].includes(value) : false;
    if (!valid) throw new Error(`Некорректный параметр: ${key}`);
  }
  return { ...patch };
}
module.exports = { DEFAULT_SETTINGS, validateSettings };
