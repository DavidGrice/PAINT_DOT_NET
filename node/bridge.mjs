import net from "node:net";

const maxMessageBytes = 1024 * 1024;

// One named-pipe connection, many length-prefixed JSON frames.
// The plugin answers a handshake immediately. Inspect and Apply stay
// unanswered until the matching effect runs, so the caller triggers
// that effect after the request is written.
export class Bridge {
  constructor(pipeName) {
    this.pipeName = pipeName;
    this.socket = null;
    this.buffer = Buffer.alloc(0);
    this.waiters = [];
  }

  connect(timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
      const socket = net.connect(`\\\\.\\pipe\\${this.pipeName}`);
      const timer = setTimeout(() => {
        socket.destroy();
        reject(new Error(`The pipe ${this.pipeName} did not connect.`));
      }, timeoutMs);
      const fail = (error) => {
        clearTimeout(timer);
        socket.destroy();
        reject(error);
      };
      socket.once("error", fail);
      socket.once("connect", () => {
        clearTimeout(timer);
        socket.off("error", fail);
        socket.on("error", (error) => this.failAll(error));
        socket.on("data", (chunk) => this.onData(chunk));
        socket.on("end", () => this.failAll(new Error("The Paint.NET bridge closed the pipe.")));
        this.socket = socket;
        resolve();
      });
    });
  }

  destroy() {
    const socket = this.socket;
    this.socket = null;
    if (socket) socket.destroy();
    this.failAll(new Error("The Paint.NET bridge closed the pipe."));
  }

  async roundTrip(message, trigger, timeoutMs) {
    if (!this.socket) throw new Error("The Paint.NET bridge is not connected.");
    let rejectWaiter = null;
    const response = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        const error = new Error("The Paint.NET plugin did not answer before the timeout.");
        error.code = "timeout";
        reject(error);
      }, timeoutMs);
      rejectWaiter = (error) => {
        clearTimeout(timer);
        reject(error);
      };
      this.waiters.push({
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: rejectWaiter,
      });
    });
    const guarded = response.catch((error) => {
      throw error;
    });
    try {
      await this.send(message);
      if (trigger) await trigger();
      return await guarded;
    } catch (error) {
      this.destroy();
      try {
        await guarded;
      } catch {
        // destroy() rejects the waiter that this call already failed.
      }
      throw error;
    }
  }

  send(message) {
    const payload = Buffer.from(JSON.stringify(message), "utf8");
    if (payload.length > maxMessageBytes) {
      throw new Error(`Bridge message is ${payload.length} bytes. The limit is ${maxMessageBytes}.`);
    }
    const header = Buffer.alloc(4);
    header.writeUInt32LE(payload.length, 0);
    return new Promise((resolve, reject) => {
      this.socket.write(Buffer.concat([header, payload]), (error) => (error ? reject(error) : resolve()));
    });
  }

  onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32LE(0);
      if (length < 2 || length > maxMessageBytes) {
        this.failAll(new Error(`Bridge frame length ${length} is outside the allowed range.`));
        this.destroy();
        return;
      }
      if (this.buffer.length < 4 + length) return;
      const payload = this.buffer.subarray(4, 4 + length).toString("utf8");
      this.buffer = this.buffer.subarray(4 + length);
      let message;
      try {
        message = JSON.parse(payload);
      } catch (error) {
        this.failAll(error);
        return;
      }
      const waiter = this.waiters.shift();
      if (waiter) waiter.resolve(message);
    }
  }

  failAll(error) {
    const pending = this.waiters;
    this.waiters = [];
    for (const waiter of pending) waiter.reject(error);
  }
}
