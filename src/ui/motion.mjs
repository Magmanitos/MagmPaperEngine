export class FrameClock {
  constructor(fps = 60) { this.period = 1000 / fps; this.next = null; this.last = null; }
  advance(now) {
    if (this.next !== null && now + 0.5 < this.next) return null;
    const elapsed = this.last === null ? this.period : Math.max(0, now - this.last);
    this.last = now;
    // Advance a deadline instead of resetting a minimum-duration timer every frame.
    // Small RAF timestamp jitter must not turn 60 Hz into alternating 30/60 Hz.
    this.next = this.next === null ? now + this.period : this.next + this.period;
    if (this.next <= now) this.next = now + this.period;
    return elapsed;
  }
}

export class MouseMotion {
  constructor(smoothingMs = 0) {
    this.smoothingMs = smoothingMs;
    this.enabled = true;
    this.position = [0.5, 0.5];
    this.target = [0.5, 0.5];
  }
  setEnabled(enabled) {
    this.enabled = enabled;
    if (!enabled) { this.position = [0.5, 0.5]; this.target = [0.5, 0.5]; }
  }
  setTarget(x, y) {
    if (!this.enabled || !Number.isFinite(x) || !Number.isFinite(y)) return;
    this.target = [Math.max(0, Math.min(1, x)), Math.max(0, Math.min(1, y))];
    if (!this.smoothingMs) this.position = [...this.target];
  }
  advance(elapsed) {
    const alpha = this.smoothingMs ? -Math.expm1(-Math.max(0, elapsed) / this.smoothingMs) : 1;
    for (let i = 0; i < 2; i++) {
      this.position[i] += (this.target[i] - this.position[i]) * alpha;
      if (Math.abs(this.position[i] - this.target[i]) < 0.00001) this.position[i] = this.target[i];
    }
    return this.position;
  }
}
