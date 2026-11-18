// Token-bucket speed limiter shared by any number of streams. A rate of 0 means unlimited.

/** setTimeout as a promise that rejects early when `signal` aborts. */
export function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(signal.reason);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal.reason);
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

export class Throttle {
  #rate = 0;
  #tokens = 0;
  #last = Date.now();

  constructor(bytesPerSecond = 0) {
    this.setRate(bytesPerSecond);
  }

  get rate() {
    return this.#rate;
  }

  setRate(bytesPerSecond) {
    this.#rate = Math.max(0, Number(bytesPerSecond) || 0);
    this.#tokens = 0; // drop old burst/debt so the new limit applies immediately
    this.#last = Date.now();
  }

  /**
   * Wait until `bytes` may pass. Callers may overdraw the bucket. The debt is
   * repaid by sleeping, so concurrent streams together never exceed the rate.
   */
  async take(bytes, signal) {
    if (!this.#rate) return;
    const now = Date.now();
    const burst = Math.max(this.#rate / 4, 16 * 1024);
    this.#tokens = Math.min(burst, this.#tokens + ((now - this.#last) / 1000) * this.#rate);
    this.#last = now;
    this.#tokens -= bytes;
    if (this.#tokens < 0) await sleep((-this.#tokens / this.#rate) * 1000, signal);
  }
}
